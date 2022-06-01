﻿namespace Nu
open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Prime

// TODO: 3D: cache empty sets and dicts.
// TODO: 3D: remove any incidental allocation.
// TODO: 3D: make sure to use proper collection comparer for string keys.
// TODO: 3D: make sure to use TryAdd in the appropriate places.

/// Allows a value to always pass as equal with another of its same type.
type [<CustomEquality; NoComparison>] 'a AlwaysEqual =
    { AlwaysEqualValue : 'a }
    override this.GetHashCode () = 0
    override this.Equals (that : obj) = that :? AlwaysEqual<'a>

type [<StructuralEquality; NoComparison>] Term =
    | Z of int
    | R of single
    | C of IComparable
    | Obj of obj
    | Tag
    | Label of string
    | Labels of string HashSet
    | Entity of uint64
    | Component' of string * Type * obj AlwaysEqual
    | Terms of Term list
    static member equals (this : Term) (that : Term) = this.Equals that
    static member equalsMany (lefts : Dictionary<string, Term>) (rights : Dictionary<string, Term>) =
        if lefts.Count = rights.Count then
            let mutable result = true
            let mutable enr = lefts.GetEnumerator ()
            while result && enr.MoveNext () do
                let current = enr.Current
                let termName = current.Key
                match rights.TryGetValue termName with
                | (true, term) -> result <- Term.equals current.Value term
                | (false, _) -> result <- false
            result
        else false
    static member dict entries = dictPlus<string, Term> HashIdentity.Structural entries
    static member singleton termName term = Term.dict [(termName, term)]
    static member zero = Term.dict []

type [<StructuralEquality; NoComparison>] Subquery =
    | Is
    | Has of string
    | Uses of string * Type
    | Eq of Term
    | Gt of Term
    | Ge of Term
    | Lt of Term
    | Le of Term
    | Not of Subquery
    | And of Subquery list
    | Or of Subquery list

    static member private equalTo term term2 =
        match (term, term2) with
        | (Z z, Z z2) -> z = z2
        | (R r, R r2) -> r = r2
        | (C c, C c2) -> c = c2
        | (Obj o, Obj o2) -> objEq o o2
        | (Label label, Label label2) -> strEq label label2
        | (Labels labels, Labels labels2) -> labels.SetEquals labels2
        | (Entity entityId, Entity entityId2) -> entityId = entityId2
        | (Terms terms, Terms terms2) ->
            if terms.Length = terms2.Length
            then List.forall2 Subquery.equalTo terms terms2
            else false
        | _ -> false

    static member eval term subquery =
        match subquery with
        | Is ->
            true
        | Has label ->
            match term with
            | Labels labels -> labels.Contains label
            | _ -> false
        | Uses (name, ty) ->
            match term with
            | Component' (name2, ty2, _)  -> strEq name name2 && refEq ty ty2
            | _ -> false
        | Eq term2  ->
            Subquery.equalTo term term2
        | Gt term2 ->
            match (term, term2) with
            | (Z z, Z z2) -> z > z2
            | (R r, R r2) -> r > r2
            | (C c, C c2) -> c.CompareTo c2 > 0
            | (_, _) -> false
        | Ge term2 ->
            match (term, term2) with
            | (Z z, Z z2) -> z >= z2
            | (R r, R r2) -> r >= r2
            | (C c, C c2) -> c.CompareTo c2 >= 0
            | (_, _) -> false
        | Lt term2 ->
            match (term, term2) with
            | (Z z, Z z2) -> z < z2
            | (R r, R r2) -> r < r2
            | (C c, C c2) -> c.CompareTo c2 < 0
            | (_, _) -> false
        | Le term2 ->
            match (term, term2) with
            | (Z z, Z z2) -> z <= z2
            | (R r, R r2) -> r <= r2
            | (C c, C c2) -> c.CompareTo c2 <= 0
            | (_, _) -> false
        | Not subquery ->
            not (Subquery.eval term subquery)
        | And subqueries ->
            match term with
            | Terms terms -> if terms.Length = subqueries.Length then List.forall2 Subquery.eval terms subqueries else false
            | _ -> false
        | Or subqueries ->
            match term with
            | Terms terms -> if terms.Length = subqueries.Length then List.exists2 Subquery.eval terms subqueries else false
            | _ -> false

    static member evalMany (terms : Dictionary<string, Term>) (subqueries : Dictionary<string, Subquery>) =
        let mutable result = true
        let mutable termEnr = terms.GetEnumerator ()
        while result && termEnr.MoveNext () do
            let termEntry = termEnr.Current
            match subqueries.TryGetValue termEntry.Key with
            | (true, subquery) -> result <- Subquery.eval termEntry.Value subquery
            | (false, _) -> result <- false
        result

    static member dict entries = dictPlus<string, Subquery> HashIdentity.Structural entries
    static member singleton subqueryName subquery = Subquery.dict [(subqueryName, subquery)]
    static member zero = Subquery.dict []

/// Identifies an archetype.
/// TODO: consider embedding hash code to make look-ups faster.
type [<CustomEquality; NoComparison>] ArchetypeId =
    { Terms : Dictionary<string, Term> }

    static member inline Hash (dict : Dictionary<_, _>) =
        let mutable h = 0
        for item in dict do
            h <- h ^^^ item.Key.GetHashCode () ^^^ item.Value.GetHashCode ()
        h

    static member equals left right =
        Term.equalsMany left.Terms right.Terms

    override this.GetHashCode () =
        ArchetypeId.Hash this.Terms

    override this.Equals that =
        match that with
        | :? ArchetypeId as that -> ArchetypeId.equals this that
        | _ -> failwithumf ()

/// The component that holds an entity's id.
type [<NoEquality; NoComparison; Struct>] EntityId =
    { mutable Active : bool
      mutable EntityId : uint64 }
    interface EntityId Component with
        member this.Active with get () = this.Active and set value = this.Active <- value

/// Data for an Ecs registration event.
type [<NoEquality; NoComparison; Struct>] RegistrationData =
    { EntityId : uint64
      ContextName : string }

/// Describes a means to store components.
type Store =
    interface
        abstract Length : int
        abstract Name : string
        abstract Item : int -> obj
        abstract SetItem : int -> obj -> unit
        abstract ZeroItem : int -> unit
        abstract Grow : unit -> unit
        abstract Read : int -> int -> FileStream -> unit
        end

/// Stores components.
type Store<'c when 'c : struct and 'c :> 'c Component> (name) =
    let mutable arr = Array.zeroCreate<'c> Constants.Ecs.ArrayReserve
    member this.Length = arr.Length
    member this.Name = name
    member this.Item i = &arr.[i]
    member this.SetItem index comp = arr.[index] <- comp
    member this.ZeroItem index = arr.[index] <- Unchecked.defaultof<'c>
    member this.Grow () =
        let length = int (single (max arr.Length 2) * 1.5f)
        let arr' = Array.zeroCreate<'c> length
        Array.Copy (arr, arr', arr.Length)
        arr <- arr'
    member this.Read index count (stream : FileStream) =
        let compSize = sizeof<'c>
        let comp = Unchecked.defaultof<'c> :> obj
        let buffer = Array.zeroCreate<byte> compSize
        let gch = GCHandle.Alloc (comp, GCHandleType.Pinned)
        try 
            let mutable index = index
            for _ in 0 .. dec count do
                stream.Read (buffer, 0, compSize) |> ignore<int>
                Marshal.Copy (buffer, 0, gch.AddrOfPinnedObject (), compSize)
                if index = arr.Length then this.Grow ()
                arr.[index] <- comp :?> 'c
                index <- inc index
        finally gch.Free ()
    interface Store with
        member this.Length = this.Length
        member this.Name = this.Name
        member this.Item i = this.Item i :> obj
        member this.SetItem index compObj = this.SetItem index (compObj :?> 'c)
        member this.ZeroItem index = this.ZeroItem index
        member this.Grow () = this.Grow ()
        member this.Read index count stream = this.Read index count stream

/// Describes a means to query components.
type 'w IQuery =
    interface
        abstract Subqueries : Dictionary<string, Subquery>
        abstract CheckCompatibility : 'w Archetype -> bool
        abstract RegisterArchetype : 'w Archetype -> unit
        end

/// A collection of component stores.
and 'w Archetype (terms : Dictionary<string, Term>) =

    let mutable freeIndex = 0
    let freeList = hashSetPlus<int> HashIdentity.Structural []
    let stores = dictPlus<string, Store> HashIdentity.Structural []
    let id = { Terms = terms }

    do
        let storeTypeGeneric = typedefof<EntityId Store>
        for termEntry in terms do
            match termEntry.Value with
            | Component' (name, ty, _) ->
                let storeType = storeTypeGeneric.MakeGenericType [|ty|]
                let store = Activator.CreateInstance (storeType, name) :?> Store
                stores.Add (name, store)
            | _ -> ()

    member this.Id = id
    member this.Stores = stores
    member this.ComponentNames = hashSetPlus HashIdentity.Structural stores.Keys
    member this.Terms = terms

    member this.Register (comps : Dictionary<string, obj>) =
        if freeList.Count > 0 then
            let index = Seq.head freeList
            freeList.Remove index |> ignore<bool>
            for compEntry in comps do
                stores.[compEntry.Key].SetItem index compEntry.Value
            index
        else
            match Seq.tryHead stores with
            | Some headStoreEntry ->
                let index = freeIndex
                if index = headStoreEntry.Value.Length then
                    for storeEntry in stores do
                        storeEntry.Value.Grow ()
                for compEntry in comps do
                    stores.[compEntry.Key].SetItem index compEntry.Value
                freeIndex <- inc freeIndex
                index
            | None ->
                let index = freeIndex
                freeIndex <- inc freeIndex
                index

    member this.Unregister (index : int) =
        for storeEntry in stores do
            storeEntry.Value.ZeroItem index
        if index = dec freeIndex then
            freeIndex <- dec freeIndex
        else
            freeList.Add index |> ignore<bool>

    member this.GetComponents index =
        let comps = dictPlus<string, obj> HashIdentity.Structural []
        for storeEntry in stores do
            comps.Add (storeEntry.Key, storeEntry.Value.[index])
        comps

    member this.Read count (stream : FileStream) =
        for storeEntry in stores do
            let store = storeEntry.Value
            store.Read count freeIndex stream
        freeIndex <- freeIndex + count

/// An entity's place in an archetype.
type [<StructuralEquality; NoComparison; Struct>] 'w ArchetypeSlot =
    { ArchetypeIndex : int
      Archetype : 'w Archetype }

[<RequireQualifiedAccess>]
module EcsEvents =

    let [<Literal>] Update = "Update"
    let [<Literal>] UpdateParallel = "UpdateParallel"
    let [<Literal>] PostUpdate = "PostUpdate"
    let [<Literal>] PostUpdateParallel = "PostUpdateParallel"
    let [<Literal>] Actualize = "Actualize"
    let [<Literal>] Register = "Register"
    let [<Literal>] Unregistering = "Unregistering"

/// An ECS event.
type [<NoEquality; NoComparison>] EcsEvent<'d, 'w> =
    { EcsEventData : 'd }

/// An ECS event callback.
type EcsCallback<'d, 'w> =
    EcsEvent<'d, 'w> -> 'w Ecs -> 'w -> 'w

/// A boxed ECS event callback.
and EcsCallbackBoxed<'w> =
    EcsEvent<obj, 'w> -> 'w Ecs -> 'w -> 'w

/// An archetype-based ECS construct.
and 'w Ecs () =

    let mutable subscriptionIdCurrent = 0u
    let mutable entityIdCurrent = 0UL
    let archetypes = dictPlus<ArchetypeId, 'w Archetype> HashIdentity.Structural []
    let archetypeSlots = dictPlus<uint64, 'w ArchetypeSlot> HashIdentity.Structural []
    let componentTypes = dictPlus<string, Type> HashIdentity.Structural []
    let subscriptions = dictPlus<string, Dictionary<uint32, obj>> StringComparer.Ordinal []
    let queries = List<'w IQuery> ()

    let createArchetype (archetypeId : ArchetypeId) =
        let archetype = Archetype<'w> (archetypeId.Terms)
        archetypes.Add (archetypeId, archetype)
        for query in queries do
            if query.CheckCompatibility archetype then
                query.RegisterArchetype archetype
        archetype

    member private this.SubscriptionId =
        subscriptionIdCurrent <- inc subscriptionIdCurrent
        if subscriptionIdCurrent = UInt32.MaxValue then failwith "Unbounded use of ECS subscription ids not supported."
        subscriptionIdCurrent

    member private this.BoxCallback<'d> (callback : EcsCallback<'d, 'w>) =
        let boxableCallback = fun (evt : EcsEvent<obj, 'w>) store ->
            let evt = { EcsEventData = evt.EcsEventData :?> 'd }
            callback evt store
        boxableCallback :> obj

    member this.EntityRef =
        entityIdCurrent <- inc entityIdCurrent
        if entityIdCurrent = UInt64.MaxValue then failwith "Unbounded use of ECS entity ids not supported."
        { EntityId = entityIdCurrent; Ecs = this }

    member this.Publish<'d> eventName (eventData : 'd) world =
        match subscriptions.TryGetValue eventName with
        | (false, _) -> world
        | (true, callbacks) ->
            Seq.fold (fun world (callback : obj) ->
                match callback with
                | :? EcsCallback<obj, 'w> as objCallback ->
                    let evt = { EcsEventData = eventData :> obj }
                    objCallback evt this world
                | _ -> failwithumf ())
                world callbacks.Values

    member this.PublishAsync<'d> eventName (eventData : 'd) =
        let vsync =
            match subscriptions.TryGetValue eventName with
            | (true, callbacks) ->
                callbacks |>
                Seq.map (fun subscription ->
                    Task.Run (fun () ->
                        match subscription.Value with
                        | :? EcsCallback<obj, 'w> as objCallback ->
                            let evt = { EcsEventData = eventData :> obj }
                            objCallback evt this Unchecked.defaultof<'w> |> ignore<'w>
                        | _ -> failwithumf ()) |> Vsync.AwaitTask) |>
                Vsync.Parallel
            | (false, _) -> Vsync.Parallel []
        Vsync.StartAsTask vsync

    member this.RegisterComponentType<'c when 'c : struct and 'c :> 'c Component> componentName =
        match componentTypes.TryGetValue componentName with
        | (true, _) -> failwith "Component type already registered."
        | (false, _) -> componentTypes.Add (componentName, typeof<'c>)

    member this.RegisterTerm (termName : string) term (entityRef : 'w EntityRef) world =
        if termName.StartsWith "@" then failwith "Term names that start with '@' are for internal use only."
        match archetypeSlots.TryGetValue entityRef.EntityId with
        | (true, archetypeSlot) ->
            let archetype = archetypeSlot.Archetype
            let comps = archetype.GetComponents archetypeSlot.ArchetypeIndex
            match term with Component' (compName, _, comp) -> comps.Add (compName, comp.AlwaysEqualValue) | _ -> ()
            archetype.Unregister archetypeSlot.ArchetypeIndex
            archetypeSlots.Remove entityRef.EntityId |> ignore<bool>
            let archetypeId = { Terms = Dictionary<_, _> archetype.Id.Terms }
            archetypeId.Terms.Add (termName, term)
            let world =
                match archetypes.TryGetValue archetypeId with
                | (true, archetype) ->
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
                | (false, _) ->
                    let archetype = createArchetype archetypeId
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
            let eventData = { EntityId = entityRef.EntityId; ContextName = termName }
            this.Publish EcsEvents.Register eventData world
        | (false, _) ->
            let archetypeId = { Terms = Dictionary.singleton HashIdentity.Structural termName term }
            let comps = dictPlus HashIdentity.Structural [] // TODO: use cached empty dictionary.
            match term with Component' (compName, _, comp) -> comps.Add (compName, comp.AlwaysEqualValue) | _ -> ()
            let world =
                match archetypes.TryGetValue archetypeId with
                | (true, archetype) ->
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
                | (false, _) ->
                    let archetype = createArchetype archetypeId
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
            let eventData = { EntityId = entityRef.EntityId; ContextName = termName }
            this.Publish EcsEvents.Register eventData world

    member this.UnregisterTerm (termName : string) (entityRef : 'w EntityRef) world =
        if termName.StartsWith "@" then failwith "Term names that start with '@' are for internal use only."
        match archetypeSlots.TryGetValue entityRef.EntityId with
        | (true, archetypeSlot) ->
            let archetype = archetypeSlot.Archetype
            let comps = archetype.GetComponents archetypeSlot.ArchetypeIndex
            let eventData = { EntityId = entityRef.EntityId; ContextName = termName }
            let world = this.Publish EcsEvents.Unregistering eventData world
            archetype.Unregister archetypeSlot.ArchetypeIndex
            archetypeSlots.Remove entityRef.EntityId |> ignore<bool>
            let archetypeId = { Terms = Dictionary<_, _> archetype.Id.Terms }
            archetypeId.Terms.Remove termName |> ignore<bool>
            if archetypeId.Terms.Count > 0 then
                let archetypeIndex = archetype.Register comps
                archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                world
            else world
        | (false, _) -> world

    member this.RegisterNamedComponent<'c when 'c : struct and 'c :> 'c Component> compName (comp : 'c) (entityRef : 'w EntityRef) world =
        match archetypeSlots.TryGetValue entityRef.EntityId with
        | (true, archetypeSlot) ->
            let archetype = archetypeSlot.Archetype
            let comps = archetype.GetComponents archetypeSlot.ArchetypeIndex
            archetype.Unregister archetypeSlot.ArchetypeIndex
            archetypeSlots.Remove entityRef.EntityId |> ignore<bool>
            comps.Add (compName, comp)
            let archetypeId = { Terms = Dictionary<_, _> archetype.Id.Terms }
            archetypeId.Terms.Add ("@" + compName, Component' (compName, typeof<'c>, { AlwaysEqualValue = null }))
            let world =
                match archetypes.TryGetValue archetypeId with
                | (true, archetype) ->
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
                | (false, _) ->
                    let archetype = createArchetype archetypeId
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
            let eventData = { EntityId = entityRef.EntityId; ContextName = compName }
            this.Publish EcsEvents.Register eventData world
        | (false, _) ->
            let archetypeId = { Terms = Dictionary.singleton HashIdentity.Structural ("@" + compName) (Component' (compName, typeof<'c>, { AlwaysEqualValue = null })) }
            let comps = Dictionary.singleton HashIdentity.Structural compName (comp :> obj)
            let world =
                match archetypes.TryGetValue archetypeId with
                | (true, archetype) ->
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
                | (false, _) ->
                    let archetype = createArchetype archetypeId
                    let archetypeIndex = archetype.Register comps
                    archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                    world
            let eventData = { EntityId = entityRef.EntityId; ContextName = compName }
            this.Publish EcsEvents.Register eventData world

    member this.RegisterComponent<'c when 'c : struct and 'c :> 'c Component> (comp : 'c) (entityRef : 'w EntityRef) world =
        let compName = typeof<'c>.Name
        this.RegisterNamedComponent<'c> compName comp (entityRef : 'w EntityRef) world

    member this.UnregisterNamedComponent compName (entityRef : 'w EntityRef) world =
        match archetypeSlots.TryGetValue entityRef.EntityId with
        | (true, archetypeSlot) ->
            let archetype = archetypeSlot.Archetype
            let comps = archetype.GetComponents archetypeSlot.ArchetypeIndex
            let eventData = { EntityId = entityRef.EntityId; ContextName = compName }
            let world = this.Publish EcsEvents.Unregistering eventData world
            archetype.Unregister archetypeSlot.ArchetypeIndex
            archetypeSlots.Remove entityRef.EntityId |> ignore<bool>
            comps.Remove compName |> ignore<bool>
            let archetypeId = { Terms = Dictionary<_, _> archetype.Id.Terms }
            archetypeId.Terms.Remove ("@" + compName) |> ignore<bool>
            if archetypeId.Terms.Count > 0 then
                let archetypeIndex = archetype.Register comps
                archetypeSlots.Add (entityRef.EntityId, { ArchetypeIndex = archetypeIndex; Archetype = archetype })
                world
            else world
        | (false, _) -> world

    member this.UnregisterComponent<'c> (entityRef : 'w EntityRef) world =
        this.UnregisterNamedComponent typeof<'c>.Name entityRef world

    member this.UnregisterEntity (entityRef : 'w EntityRef) world =
        match archetypeSlots.TryGetValue entityRef.EntityId with
        | (true, archetypeSlot) ->
            let archetype = archetypeSlot.Archetype
            let mutable world = world
            for compName in archetype.Stores.Keys do
                let eventData = { EntityId = entityRef.EntityId; ContextName = compName }
                world <- this.Publish EcsEvents.Unregistering eventData world
            for termEntry in archetype.Terms do
                let eventData = { EntityId = entityRef.EntityId; ContextName = termEntry.Key }
                world <- this.Publish EcsEvents.Unregistering eventData world
            archetype.Unregister archetypeSlot.ArchetypeIndex
            world
        | (false, _) -> world

    member this.RegisterQuery<'q when 'q :> 'w IQuery> (query : 'q) =
        for archetypeEntry in archetypes do
            let archetype = archetypeEntry.Value
            if query.CheckCompatibility archetype then
                query.RegisterArchetype archetype
        queries.Add query
        query

    member this.SubscribePlus<'d> subscriptionId eventName (callback : EcsCallback<'d, 'w>) =
        match subscriptions.TryGetValue eventName with
        | (true, callbacks) ->
            callbacks.Add (subscriptionId, this.BoxCallback<'d> callback)
            subscriptionId
        | (false, _) ->
            let callbacks = dictPlus HashIdentity.Structural [(subscriptionId, this.BoxCallback<'d> callback)]
            subscriptions.Add (eventName, callbacks)
            subscriptionId

    member this.Subscribe<'d> eventName callback =
        this.SubscribePlus<'d> this.SubscriptionId eventName callback |> ignore

    member this.Unsubscribe eventName subscriptionId =
        match subscriptions.TryGetValue eventName with
        | (true, callbacks) -> callbacks.Remove subscriptionId
        | (false, _) -> false

    member this.IndexArchetypeSlot (entityRef : 'w EntityRef) =
        archetypeSlots.[entityRef.EntityId]

    member this.Index (compName, statement : Statement<'c, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], state)

    member this.Index (compName, comp2Name, statement : Statement<'c, 'c2, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], state)

    member this.Index (compName, comp2Name, comp3Name, statement : Statement<'c, 'c2, 'c3, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], state)

    member this.Index (compName, comp2Name, comp3Name, comp4Name, statement : Statement<'c, 'c2, 'c3, 'c4, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let store4 = stores.[comp4Name] :?> 'c4 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], &store4.[i], state)

    member this.Index (compName, comp2Name, comp3Name, comp4Name, comp5Name, statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let store4 = stores.[comp4Name] :?> 'c4 Store
        let store5 = stores.[comp5Name] :?> 'c5 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], &store4.[i], &store5.[i], state)

    member this.Index (compName, comp2Name, comp3Name, comp4Name, comp5Name, comp6Name, statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let store4 = stores.[comp4Name] :?> 'c4 Store
        let store5 = stores.[comp5Name] :?> 'c5 Store
        let store6 = stores.[comp6Name] :?> 'c6 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], &store4.[i], &store5.[i], &store6.[i], state)

    member this.Index (compName, comp2Name, comp3Name, comp4Name, comp5Name, comp6Name, comp7Name, statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let store4 = stores.[comp4Name] :?> 'c4 Store
        let store5 = stores.[comp5Name] :?> 'c5 Store
        let store6 = stores.[comp6Name] :?> 'c6 Store
        let store7 = stores.[comp7Name] :?> 'c7 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], &store4.[i], &store5.[i], &store6.[i], &store7.[i], state)

    member this.Index (compName, comp2Name, comp3Name, comp4Name, comp5Name, comp6Name, comp7Name, comp8Name, statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 'c8, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let store4 = stores.[comp4Name] :?> 'c4 Store
        let store5 = stores.[comp5Name] :?> 'c5 Store
        let store6 = stores.[comp6Name] :?> 'c6 Store
        let store7 = stores.[comp7Name] :?> 'c7 Store
        let store8 = stores.[comp8Name] :?> 'c8 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], &store4.[i], &store5.[i], &store6.[i], &store7.[i], &store8.[i], state)

    member this.Index (compName, comp2Name, comp3Name, comp4Name, comp5Name, comp6Name, comp7Name, comp8Name, comp9Name, statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 'c8, 'c9, 's>, (entityRef : 'w EntityRef), state : 's) =
        let archetypeSlot = this.IndexArchetypeSlot entityRef
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let store2 = stores.[comp2Name] :?> 'c2 Store
        let store3 = stores.[comp3Name] :?> 'c3 Store
        let store4 = stores.[comp4Name] :?> 'c4 Store
        let store5 = stores.[comp5Name] :?> 'c5 Store
        let store6 = stores.[comp6Name] :?> 'c6 Store
        let store7 = stores.[comp7Name] :?> 'c7 Store
        let store8 = stores.[comp8Name] :?> 'c8 Store
        let store9 = stores.[comp9Name] :?> 'c9 Store
        let i = archetypeSlot.ArchetypeIndex
        statement.Invoke (&store.[i], &store2.[i], &store3.[i], &store4.[i], &store5.[i], &store6.[i], &store7.[i], &store8.[i], &store9.[i], state)

    member this.Index'<'c, 's when
        'c : struct and 'c :> 'c Component>
        (statement : Statement<'c, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component>
        (statement : Statement<'c, 'c2, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component>
        (statement : Statement<'c, 'c2, 'c3, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 'c4, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component and
        'c4 : struct and 'c4 :> 'c4 Component>
        (statement : Statement<'c, 'c2, 'c3, 'c4, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, typeof<'c4>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 'c4, 'c5, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component and
        'c4 : struct and 'c4 :> 'c4 Component and
        'c5 : struct and 'c5 :> 'c5 Component>
        (statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, typeof<'c4>.Name, typeof<'c5>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component and
        'c4 : struct and 'c4 :> 'c4 Component and
        'c5 : struct and 'c5 :> 'c5 Component and
        'c6 : struct and 'c6 :> 'c6 Component>
        (statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, typeof<'c4>.Name, typeof<'c5>.Name, typeof<'c6>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component and
        'c4 : struct and 'c4 :> 'c4 Component and
        'c5 : struct and 'c5 :> 'c5 Component and
        'c6 : struct and 'c6 :> 'c6 Component and
        'c7 : struct and 'c7 :> 'c7 Component>
        (statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, typeof<'c4>.Name, typeof<'c5>.Name, typeof<'c6>.Name, typeof<'c7>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 'c8, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component and
        'c4 : struct and 'c4 :> 'c4 Component and
        'c5 : struct and 'c5 :> 'c5 Component and
        'c6 : struct and 'c6 :> 'c6 Component and
        'c7 : struct and 'c7 :> 'c7 Component and
        'c8 : struct and 'c8 :> 'c8 Component>
        (statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 'c8, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, typeof<'c4>.Name, typeof<'c5>.Name, typeof<'c6>.Name, typeof<'c7>.Name, typeof<'c8>.Name, statement, entityRef, state)

    member this.Index<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 'c8, 'c9, 's when
        'c : struct and 'c :> 'c Component and
        'c2 : struct and 'c2 :> 'c2 Component and
        'c3 : struct and 'c3 :> 'c3 Component and
        'c4 : struct and 'c4 :> 'c4 Component and
        'c5 : struct and 'c5 :> 'c5 Component and
        'c6 : struct and 'c6 :> 'c6 Component and
        'c7 : struct and 'c7 :> 'c7 Component and
        'c8 : struct and 'c8 :> 'c8 Component and
        'c9 : struct and 'c9 :> 'c9 Component>
        (statement : Statement<'c, 'c2, 'c3, 'c4, 'c5, 'c6, 'c7, 'c8, 'c9, 's>, entityRef, state : 's) =
        this.Index (typeof<'c>.Name, typeof<'c2>.Name, typeof<'c3>.Name, typeof<'c4>.Name, typeof<'c5>.Name, typeof<'c6>.Name, typeof<'c7>.Name, typeof<'c8>.Name, typeof<'c9>.Name, statement, entityRef, state)

    member this.ReadComponents count archetypeId stream =
        match archetypes.TryGetValue archetypeId with
        | (true, archetype) -> archetype.Read count stream
        | (false, _) -> failwith "Could not find archetype."

and [<StructuralEquality; NoComparison; Struct>] 'w EntityRef =
    { EntityId : uint64
      Ecs : 'w Ecs }

    member this.GetNamed<'c when 'c : struct and 'c :> 'c Component> compName =
        let archetypeSlot = this.Ecs.IndexArchetypeSlot this
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let i = archetypeSlot.ArchetypeIndex
        &store.[i]

    member this.Get<'c when 'c : struct and 'c :> 'c Component> () =
        this.GetNamed<'c> typeof<'c>.Name

    member this.SetNamed<'c when 'c : struct and 'c :> 'c Component> compName (comp : 'c) =
        let archetypeSlot = this.Ecs.IndexArchetypeSlot this
        let stores = archetypeSlot.Archetype.Stores
        let store = stores.[compName] :?> 'c Store
        let i = archetypeSlot.ArchetypeIndex
        store.[i] <- comp

    member this.Set<'c when 'c : struct and 'c :> 'c Component> (comp : 'c) =
        this.SetNamed<'c> typeof<'c>.Name comp