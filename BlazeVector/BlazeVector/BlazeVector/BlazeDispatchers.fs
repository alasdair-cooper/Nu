﻿namespace BlazeVector
open System
open System.Collections
open OpenTK
open Microsoft.Xna
open FarseerPhysics
open FarseerPhysics.Dynamics
open Prime
open Nu
open Nu.NuConstants
open BlazeVector
open BlazeVector.BlazeConstants

[<AutoOpen>]
module BulletDispatcherModule =

    type Entity with

        member bullet.Age = bullet?Age () : int64
        static member setAge (value : int64) (bullet : Entity) : Entity = bullet?Age <- value

    type BulletDispatcher () =
        inherit RigidBodySpriteDispatcher (Set.empty)

        static let fieldDescriptors =
            [Entity.describeField Field?Size <| Vector2 (24.0f, 24.0f)
             Entity.describeField Field?Density 0.25f
             Entity.describeField Field?Restitution 0.5f
             Entity.describeField Field?LinearDamping 0.0f
             Entity.describeField Field?GravityScale 0.0f
             Entity.describeField Field?IsBullet true
             Entity.describeField Field?SpriteImage PlayerBulletImage
             Entity.describeField Field?Age 0L]

        let tickHandler event world =
            if World.isGamePlaying world then
                let bullet = World.getEntity event.Subscriber world
                let bullet = Entity.setAge (bullet.Age + 1L) bullet
                let world =
                    if bullet.Age < 28L then World.setEntity event.Subscriber bullet world
                    else World.removeEntity event.Subscriber world
                (Unhandled, world)
            else (Unhandled, world)

        let collisionHandler event world =
            if World.isGamePlaying world then
                let world = World.removeEntity event.Subscriber world
                (Unhandled, world)
            else (Unhandled, world)

        static member FieldDescriptors =
            fieldDescriptors

        override dispatcher.Init (bullet, dispatcherContainer) =
            let bullet = base.Init (bullet, dispatcherContainer)
            let bullet = Entity.attachFields fieldDescriptors bullet
            bullet

        override dispatcher.Register (address, world) =
            let world = base.Register (address, world)
            let world = World.observe TickEventName address (CustomSub tickHandler) world
            World.observe (CollisionEventName + address) address (CustomSub collisionHandler) world

        override dispatcher.GetBodyShape (bullet, _) =
            CircleShape { Radius = bullet.Size.X * 0.5f; Center = Vector2.Zero }

[<AutoOpen>]
module EnemyDispatcherModule =

    type Entity with

        member enemy.Health = enemy?Health () : int
        static member setHealth (value : int) (enemy : Entity) : Entity = enemy?Health <- value

        static member hasAppeared camera (enemy : Entity) =
            enemy.Position.X - (camera.EyeCenter.X + camera.EyeSize.X * 0.5f) < 0.0f

    type EnemyDispatcher () =
        inherit RigidBodyAnimatedSpriteDispatcher (Set.empty)

        static let fieldDescriptors =
            [Entity.describeField Field?Size <| Vector2 (48.0f, 96.0f)
             Entity.describeField Field?FixedRotation true
             Entity.describeField Field?LinearDamping 3.0f
             Entity.describeField Field?GravityScale 0.0f
             Entity.describeField Field?Stutter 8
             Entity.describeField Field?TileCount 6
             Entity.describeField Field?TileRun 4
             Entity.describeField Field?TileSize <| Vector2 (48.0f, 96.0f)
             Entity.describeField Field?AnimatedSpriteImage EnemyImage
             Entity.describeField Field?Health 6]

        let move enemy world =
            let physicsId = Entity.getPhysicsId enemy
            let optGroundTangent = Physics.getOptGroundContactTangent physicsId world.Integrator
            let force =
                match optGroundTangent with
                | None -> Vector2 (-2000.0f, -30000.0f)
                | Some groundTangent -> Vector2.Multiply (groundTangent, Vector2 (-2000.0f, if groundTangent.Y > 0.0f then 8000.0f else 0.0f))
            World.applyForce force physicsId world

        let die address world =
            let world = World.removeEntity address world
            World.playSound ExplosionSound 1.0f world

        let tickHandler event world =
            if World.isGamePlaying world then
                let enemy = World.getEntity event.Subscriber world
                let world = if Entity.hasAppeared world.Camera enemy then move enemy world else world
                let world = if enemy.Health <= 0 then die event.Subscriber world else world
                (Unhandled, world)
            else (Unhandled, world)

        let collisionHandler event world =
            if World.isGamePlaying world then
                let collisionData = EventData.toEntityCollisionData event.Data
                let collidee = World.getEntity collisionData.Collidee world
                let isBullet = Entity.dispatchesAs typeof<BulletDispatcher> collidee world
                if isBullet then
                    let world = World.withEntity (fun enemy -> Entity.setHealth (enemy.Health - 1) enemy) event.Subscriber world
                    let world = World.playSound HitSound 1.0f world
                    (Unhandled, world)
                else (Unhandled, world)
            else (Unhandled, world)

        static member FieldDescriptors =
            fieldDescriptors

        override dispatcher.Init (enemy, dispatcherContainer) =
            let enemy = base.Init (enemy, dispatcherContainer)
            Entity.attachFields fieldDescriptors enemy

        override dispatcher.Register (address, world) =
            let world = base.Register (address, world)
            world |>
                World.observe TickEventName address (CustomSub tickHandler) |>
                World.observe (CollisionEventName + address) address (CustomSub collisionHandler)

        override dispatcher.GetBodyShape (enemy, _) =
            CapsuleShape { Height = enemy.Size.Y * 0.5f; Radius = enemy.Size.Y * 0.25f; Center = Vector2.Zero }

[<AutoOpen>]
module PlayerDispatcherModule =

    type Entity with

        member player.LastTimeOnGroundNp = player?LastTimeOnGroundNp () : int64
        static member setLastTimeOnGroundNp (value : int64) (player : Entity) : Entity = player?LastTimeOnGroundNp <- value
        member player.LastTimeJumpNp = player?LastTimeJumpNp () : int64
        static member setLastTimeJumpNp (value : int64) (player : Entity) : Entity = player?LastTimeJumpNp <- value
        
        static member hasFallen (player : Entity) =
            player.Position.Y < -600.0f

    type PlayerDispatcher () =
        inherit RigidBodyAnimatedSpriteDispatcher (Set.empty)

        static let fieldDescriptors =
            [Entity.describeField Field?Size <| Vector2 (48.0f, 96.0f)
             Entity.describeField Field?FixedRotation true
             Entity.describeField Field?LinearDamping 3.0f
             Entity.describeField Field?GravityScale 0.0f
             Entity.describeField Field?Stutter 3
             Entity.describeField Field?TileCount 16
             Entity.describeField Field?TileRun 4
             Entity.describeField Field?TileSize (Vector2 (48.0f, 96.0f))
             Entity.describeField Field?AnimatedSpriteImage PlayerImage
             Entity.describeField Field?LastTimeOnGroundNp Int64.MinValue
             Entity.describeField Field?LastTimeJumpNp Int64.MinValue]

        let createBullet bulletAddress (playerTransform : Transform) world =
            let bullet = Entity.makeDefault typeof<BulletDispatcher>.Name (Some <| Address.last bulletAddress) world
            let bullet =
                bullet |>
                    Entity.setPosition (playerTransform.Position + Vector2 (playerTransform.Size.X * 0.9f, playerTransform.Size.Y * 0.4f)) |>
                    Entity.setDepth playerTransform.Depth
            World.addEntity bulletAddress bullet world

        let propelBullet bulletAddress world =
            let bullet = World.getEntity bulletAddress world
            let world = World.applyLinearImpulse (Vector2 (50.0f, 0.0f)) (Entity.getPhysicsId bullet) world
            World.playSound ShotSound 1.0f world

        let shootBullet address world =
            let bulletAddress = addrlist (Address.allButLast address) [string <| NuCore.makeId ()]
            let playerTransform = world |> World.getEntity address |> Entity.getTransform
            let world = createBullet bulletAddress playerTransform world
            propelBullet bulletAddress world

        let spawnBulletHandler event world =
            if World.isGamePlaying world then
                let player = World.getEntity event.Subscriber world
                if not <| Entity.hasFallen player then
                    if world.TickTime % 6L = 0L then
                        let world = shootBullet event.Subscriber world
                        (Unhandled, world)
                    else (Unhandled, world)
                else (Unhandled, world)
            else (Unhandled, world)

        let getLastTimeOnGround (player : Entity) world =
            let physicsId = Entity.getPhysicsId player
            if not <| Physics.isBodyOnGround physicsId world.Integrator
            then player.LastTimeOnGroundNp
            else world.TickTime

        let movementHandler event world =
            if World.isGamePlaying world then
                let player = World.getEntity event.Subscriber world
                let lastTimeOnGround = getLastTimeOnGround player world
                let player = Entity.setLastTimeOnGroundNp lastTimeOnGround player
                let world = World.setEntity event.Subscriber player world
                let physicsId = Entity.getPhysicsId player
                let optGroundTangent = Physics.getOptGroundContactTangent physicsId world.Integrator
                let force =
                    match optGroundTangent with
                    | None -> Vector2 (8000.0f, -30000.0f)
                    | Some groundTangent -> Vector2.Multiply (groundTangent, Vector2 (8000.0f, if groundTangent.Y > 0.0f then 12000.0f else 0.0f))
                let world = World.applyForce force physicsId world
                (Unhandled, world)
            else (Unhandled, world)

        let jumpHandler event world =
            if World.isGamePlaying world then
                let player = World.getEntity event.Subscriber world
                if  world.TickTime >= player.LastTimeJumpNp + 12L &&
                    world.TickTime <= player.LastTimeOnGroundNp + 10L then
                    let player = Entity.setLastTimeJumpNp world.TickTime player
                    let world = World.setEntity event.Subscriber player world
                    let world = World.applyLinearImpulse (Vector2 (0.0f, 18000.0f)) (Entity.getPhysicsId player) world
                    let world = World.playSound JumpSound 1.0f world
                    (Unhandled, world)
                else (Unhandled, world)
            else (Unhandled, world)

        static member FieldDescriptors =
            fieldDescriptors

        override dispatcher.Init (player, dispatcherContainer) =
            let player = base.Init (player, dispatcherContainer)
            Entity.attachFields fieldDescriptors player

        override dispatcher.Register (address, world) =
            let world = base.Register (address, world)
            world |>
                World.observe TickEventName address (CustomSub spawnBulletHandler) |>
                World.observe TickEventName address (CustomSub movementHandler) |>
                World.observe DownMouseLeftEventName address (CustomSub jumpHandler)

        override dispatcher.GetBodyShape (player, _) =
            CapsuleShape { Height = player.Size.Y * 0.5f; Radius = player.Size.Y * 0.25f; Center = Vector2.Zero }

[<AutoOpen>]
module StagePlayDispatcherModule =

    type StagePlayDispatcher () =
        inherit GroupDispatcher ()

        let getPlayer groupAddress world =
            let playerAddress = addrlist groupAddress [StagePlayerName]
            World.getEntity playerAddress world

        let adjustCamera groupAddress world =
            let player = getPlayer groupAddress world
            let eyeCenter = Vector2 (player.Position.X + player.Size.X * 0.5f + world.Camera.EyeSize.X * 0.33f, world.Camera.EyeCenter.Y)
            { world with Camera = { world.Camera with EyeCenter = eyeCenter }}

        let adjustCameraHandler event world =
            (Unhandled, adjustCamera event.Subscriber world)

        let playerFallHandler event world =
            let player = getPlayer event.Subscriber world
            if Entity.hasFallen player && (World.getSelectedScreen world).State = IdlingState then
                let world = World.playSound DeathSound 1.0f world
                let world = World.transitionScreen TitleAddress world
                (Unhandled, world)
            else (Unhandled, world)

        override dispatcher.Register (address, world) =
            let world = base.Register (address, world)
            let world =
                world |>
                World.observe TickEventName address (CustomSub adjustCameraHandler) |>
                World.observe TickEventName address (CustomSub playerFallHandler)
            adjustCamera address world

[<AutoOpen>]
module StageScreenModule =

    type StageScreenDispatcher () =
        inherit ScreenDispatcher ()

        let anonymizeEntities entities =
            List.map
                (fun (entity : Entity) -> let id = NuCore.makeId () in { entity with Id = id; Name = string id })
                entities

        let shiftEntities xShift entities =
            List.map
                (fun (entity : Entity) -> Entity.setPosition (entity.Position + Vector2 (xShift, 0.0f)) entity)
                entities

        let makeSectionFromFile fileName sectionName xShift world =
            let (sectionGroup, sectionEntities) = World.loadGroupFromFile fileName world
            let sectionEntities = anonymizeEntities sectionEntities
            let sectionEntities = shiftEntities xShift sectionEntities
            (sectionName, sectionGroup, sectionEntities)

        let startPlayHandler event world =
            let random = Random ()
            let sectionFileNames = List.toArray SectionFileNames
            let sectionDescriptors =
                [for i in 0 .. SectionCount do
                    let xShift = 2048.0f
                    let sectionFileNameIndex = if i = 0 then 0 else random.Next () % sectionFileNames.Length
                    yield makeSectionFromFile sectionFileNames.[sectionFileNameIndex] (SectionName + string i) (xShift * single i) world]
            let stagePlayDescriptor = Triple.prepend StagePlayName <| World.loadGroupFromFile StagePlayFileName world
            let groupDescriptors = stagePlayDescriptor :: sectionDescriptors
            let world = World.addGroups event.Subscriber groupDescriptors world
            let world = World.playSong DeadBlazeSong 1.0f 0 world
            (Unhandled, world)

        let stoppingPlayHandler _ world =
            let world = World.fadeOutSong DefaultTimeToFadeOutSongMs world
            (Unhandled, world)

        let stopPlayHandler event world =
            let sectionNames = [for i in 0 .. SectionCount do yield SectionName + string i]
            let groupNames = StagePlayName :: sectionNames
            let world = World.removeGroups event.Subscriber groupNames world
            (Unhandled, world)

        override dispatcher.Register (address, world) =
            let world = base.Register (address, world)
            world |>
                World.observe (SelectEventName + address) address (CustomSub startPlayHandler) |>
                World.observe (StartOutgoingEventName + address) address (CustomSub stoppingPlayHandler) |>
                World.observe (DeselectEventName + address) address (CustomSub stopPlayHandler)

[<AutoOpen>]
module BlazeVectorDispatcherModule =

    /// The custom type for BlazeVector's game dispatcher.
    type BlazeVectorDispatcher () =
        inherit GameDispatcher ()

        override dispatcher.Register world =
            let world = base.Register world
            // add the BlazeVector-specific dispatchers to the world
            let dispatchers =
                Map.addMany
                    [typeof<BulletDispatcher>.Name, BulletDispatcher () :> obj
                     typeof<PlayerDispatcher>.Name, PlayerDispatcher () :> obj
                     typeof<EnemyDispatcher>.Name, EnemyDispatcher () :> obj
                     typeof<StagePlayDispatcher>.Name, StagePlayDispatcher () :> obj
                     typeof<StageScreenDispatcher>.Name, StageScreenDispatcher () :> obj]
                    world.Dispatchers
            { world with Dispatchers = dispatchers }