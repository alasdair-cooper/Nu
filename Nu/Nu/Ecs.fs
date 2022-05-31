﻿namespace Nu
open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading.Tasks
open Prime
open Nu

type SystemId = string HashSet

/// The base component type of an Ecs.
type Component<'c when 'c : struct and 'c :> 'c Component> =
    interface
        abstract Active : bool with get, set
        end

/// The component that holds an entity's id.
type [<NoEquality; NoComparison; Struct>] EntityId =
    { mutable Active : bool
      mutable EntityId : uint64 }
    interface EntityId Component with
        member this.Active with get () = this.Active and set value = this.Active <- value

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

type Store<'c when 'c : struct and 'c :> 'c Component> (name) =
    let mutable arr = Array.zeroCreate<'c> Constants.Ecs.ArrayReserve
    member this.Length = arr.Length
    member this.Name = name
    member this.Item i = &arr.[i]
    member this.SetItem index comp = arr.[index] <- comp
    member this.ZeroItem index = arr.[index] <- Unchecked.defaultof<'c>
    member this.Grow () =
        let arr' = Array.zeroCreate<'c> (arr.Length * 2)
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

type 'w Query =
    interface
        abstract CheckCompatibility : 'w System -> bool
        abstract RegisterSystem : 'w System -> unit
        end

and 'w System (storeTypes : Dictionary<string, Type>) =

    let mutable freeIndex = 0
    let freeList = hashSetPlus<int> HashIdentity.Structural []
    let stores = dictPlus<string, Store> HashIdentity.Structural []
    let id = hashSetPlus HashIdentity.Structural storeTypes.Keys

    do
        let storeTypeGeneric = typedefof<EntityId Store>
        for storeTypeEntry in storeTypes do
            let storeType = storeTypeGeneric.MakeGenericType [|storeTypeEntry.Value|]
            let store = Activator.CreateInstance (storeType, storeTypeEntry.Key) :?> Store
            stores.Add (storeTypeEntry.Key, store)

    let store0 = stores.Values |> Seq.head

    member this.Id = id

    member this.Stores = stores

    member this.Register (comps : Dictionary<string, obj>) =
        if freeList.Count > 0 then
            let index = Seq.head freeList
            freeList.Remove index |> ignore<bool>
            for compEntry in comps do
                stores.[compEntry.Key].SetItem index compEntry.Value
            index
        else
            let index = freeIndex
            if index = store0.Length then
                for storeEntry in stores do
                    storeEntry.Value.Grow ()
            for compEntry in comps do
                stores.[compEntry.Key].SetItem index compEntry.Value
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

type [<StructuralEquality; NoComparison; Struct>] 'w SystemSlot =
    { SystemIndex : int
      System : 'w System }

[<RequireQualifiedAccess>]
module EcsEvents =

    let [<Literal>] Update = "Update"
    let [<Literal>] UpdateParallel = "UpdateParallel"
    let [<Literal>] PostUpdate = "PostUpdate"
    let [<Literal>] PostUpdateParallel = "PostUpdateParallel"
    let [<Literal>] Actualize = "Actualize"
    let [<Literal>] RegisterComponent = "RegisterComponent"
    let [<Literal>] UnregisteringComponent = "UnregisteringComponent"

/// An ECS event.
type [<NoEquality; NoComparison>] EcsEvent<'d, 'w> =
    { EcsEventData : 'd }

/// An ECS event callback.
type EcsCallback<'d, 'w> =
    EcsEvent<'d, 'w> -> 'w Ecs -> 'w -> 'w

/// A boxed ECS event callback.
and EcsCallbackBoxed<'w> =
    EcsEvent<obj, 'w> -> 'w Ecs -> 'w -> 'w

and 'w Ecs () =

    let mutable subscriptionIdCurrent = 0u
    let mutable entityIdCurrent = 0UL
    let systems = dictPlus<SystemId, 'w System> (HashSet<string>.CreateSetComparer ()) []
    let systemSlots = dictPlus<uint64, 'w SystemSlot> HashIdentity.Structural []
    let componentTypes = dictPlus<string, Type> HashIdentity.Structural []
    let subscriptions = dictPlus<string, Dictionary<uint32, obj>> StringComparer.Ordinal []
    let queries = List<'w Query> ()

    let createSystem (inferredType : Type) (systemId : SystemId) =
        let storeTypes =
            systemId |>
            Seq.map (fun componentName ->
                match componentTypes.TryGetValue componentName with
                | (true, componentType) -> (componentName, componentType)
                | (false, _) ->
                    if inferredType.Name = componentName
                    then componentTypes.Add (componentName, inferredType)
                    else failwith ("Could not infer component type of '" + componentName + "'.")
                    (componentName, inferredType)) |>
            dictPlus HashIdentity.Structural
        let system = System<'w> storeTypes
        systems.Add (system.Id, system)
        for query in queries do
            if query.CheckCompatibility system then
                query.RegisterSystem system
        system

    member private this.SubscriptionId =
        subscriptionIdCurrent <- inc subscriptionIdCurrent
        if subscriptionIdCurrent = UInt32.MaxValue then failwith "Unbounded use of ECS subscription ids not supported."
        subscriptionIdCurrent

    member this.EntityId =
        entityIdCurrent <- inc entityIdCurrent
        if entityIdCurrent = UInt64.MaxValue then failwith "Unbounded use of ECS entity ids not supported."
        entityIdCurrent

    member private this.BoxCallback<'d> (callback : EcsCallback<'d, 'w>) =
        let boxableCallback = fun (evt : EcsEvent<obj, 'w>) store ->
            let evt = { EcsEventData = evt.EcsEventData :?> 'd }
            callback evt store
        boxableCallback :> obj

    member this.Publish<'d> eventName (eventData : 'd) (world : 'w) =
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

    member this.RegisterNamedComponent<'c when 'c : struct and 'c :> 'c Component> compName (comp : 'c) entityId (world : 'w) : 'w =
        match systemSlots.TryGetValue entityId with
        | (true, systemSlot) ->
            let system = systemSlot.System
            let comps = system.GetComponents systemSlot.SystemIndex
            system.Unregister systemSlot.SystemIndex
            systemSlots.Remove entityId |> ignore<bool>
            comps.Add (compName, comp)
            let systemId = HashSet system.Id
            systemId.Add compName |> ignore<bool>
            let world =
                match systems.TryGetValue systemId with
                | (true, system) ->
                    let systemIndex = system.Register comps
                    systemSlots.Add (entityId, { SystemIndex = systemIndex; System = system })
                    world
                | (false, _) ->
                    let system = createSystem typeof<'c> systemId
                    let systemIndex = system.Register comps
                    systemSlots.Add (entityId, { SystemIndex = systemIndex; System = system })
                    world
            this.Publish EcsEvents.RegisterComponent entityId world
        | (false, _) ->
            let systemId = HashSet.singleton HashIdentity.Structural compName
            let comps = Dictionary.singleton HashIdentity.Structural compName (comp :> obj)
            let world =
                match systems.TryGetValue systemId with
                | (true, system) ->
                    let systemIndex = system.Register comps
                    systemSlots.Add (entityId, { SystemIndex = systemIndex; System = system })
                    world
                | (false, _) ->
                    let system = createSystem typeof<'c> systemId
                    let systemIndex = system.Register comps
                    systemSlots.Add (entityId, { SystemIndex = systemIndex; System = system })
                    world
            this.Publish EcsEvents.RegisterComponent entityId world

    member this.RegisterComponent<'c when 'c : struct and 'c :> 'c Component> (comp : 'c) entityId world =
        let compName = typeof<'c>.Name
        this.RegisterNamedComponent<'c> compName comp entityId world

    member this.UnregisterNamedComponent compName entityId world =
        match systemSlots.TryGetValue entityId with
        | (true, systemSlot) ->
            let system = systemSlot.System
            let comps = system.GetComponents systemSlot.SystemIndex
            let world = this.Publish EcsEvents.UnregisteringComponent entityId world
            system.Unregister systemSlot.SystemIndex
            systemSlots.Remove entityId |> ignore<bool>
            comps.Remove compName |> ignore<bool>
            let systemId = HashSet system.Id
            systemId.Remove compName |> ignore<bool>
            let systemIndex = system.Register comps
            systemSlots.Add (entityId, { SystemIndex = systemIndex; System = system })
            world
        | (false, _) -> world

    member this.UnregisterComponent<'c> entityId world =
        this.UnregisterNamedComponent typeof<'c>.Name entityId world

    member this.RegisterQuery<'q when 'q :> 'w Query> (query : 'q) =
        for systemEntry in systems do
            let system = systemEntry.Value
            if query.CheckCompatibility system then
                query.RegisterSystem system
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

    member this.ReadComponents count systemId stream =
        match systems.TryGetValue systemId with
        | (true, system) -> system.Read count stream
        | (false, _) -> failwith "Could not find system."