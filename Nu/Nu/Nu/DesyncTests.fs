﻿namespace Nu
open System
open Xunit
open Prime
open Nu
open Nu.Constants
open Nu.WorldConstants
open Nu.Observer
module DesyncTests =

    let UnitEventAddress = stoa<unit> "Test"
    let incUserState (_, world) = (Cascade, World.transformUserState inc world)
    let incUserStateTwice (_, world) = (Cascade, World.transformUserState (inc >> inc) world)

    let [<Fact>] desyncWorks () =
        World.init ()
        let world = World.makeEmpty 0
        let obs = observe UnitEventAddress (atooa UnitEventAddress)
        
        let proc3 = desync { return incUserStateTwice }
        ignore proc3
        
        let proc4 = desync.Return incUserStateTwice
        ignore proc4
        
        let proc2 =
            desync.Bind (call incUserState, fun () ->
                desync.For ([0 .. 1], fun _ ->
                    desync.Bind (pass (), fun () -> desync.Return incUserState)))
        ignore proc2
        
        let proc =
            desync {
                do! call incUserState
                //for i in [0 .. 1] do pass ()
                do! desync.For ([0 .. 1], fun _ -> pass ())
                return incUserStateTwice }
        
        let world = snd <| Desync.run tautology obs proc world
        let world = World.publish4 () UnitEventAddress GameAddress world
        Assert.Equal (1, World.getUserState world)
        let world = World.publish4 () UnitEventAddress GameAddress world
        Assert.Equal (1, World.getUserState world)
        let world = World.publish4 () UnitEventAddress GameAddress world
        Assert.Equal (1, World.getUserState world)
        let world = World.publish4 () UnitEventAddress GameAddress world
        Assert.Equal (3, World.getUserState world)
        Assert.True <| Map.isEmpty world.Callbacks.CallbackStates