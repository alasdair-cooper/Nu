﻿namespace TerraFirma
open System
open System.Numerics
open Prime
open Nu

type JumpState =
    { LastTime : int64
      LastTimeOnGround : int64 }

    static member initial =
        { LastTime = 0L
          LastTimeOnGround = 0L }

type AttackState =
    { AttackTime : int64
      AttackedCharacters : Entity Set
      FollowUpBuffered : bool }

    static member make time =
        { AttackTime = time
          AttackedCharacters = Set.empty
          FollowUpBuffered = false }

type InjuryState =
    { InjuryTime : int64 }

type ActionState =
    | NormalState
    | AttackState of AttackState
    | InjuryState of InjuryState
    | WoundedState

type [<ReferenceEquality; SymbolicExpansion>] Character =
    { Player : bool
      PositionPrevious : Vector3 Queue
      RotationPrevious : Quaternion Queue
      LinearVelocityPrevious : Vector3 Queue
      AngularVelocityPrevious : Vector3 Queue
      HitPoints : int
      ActionState : ActionState
      JumpState : JumpState
      WeaponCollisions : Entity Set
      WalkSpeed : single
      TurnSpeed : single
      JumpSpeed : single
      WeaponModel : StaticModel AssetTag }

    member this.PositionInterp position =
        if not (Queue.isEmpty this.PositionPrevious) then
            let positions = Queue.conj position this.PositionPrevious
            Seq.sum positions / single positions.Length
        else position

    member this.RotationInterp rotation =
        if not (Queue.isEmpty this.RotationPrevious) then
            let rotations = Queue.conj rotation this.RotationPrevious
            if rotations.Length > 1 then
                let unnormalized = Quaternion.Slerp (Seq.head rotations, Seq.last rotations, 0.5f)
                unnormalized.Normalized
            else rotation
        else rotation

    member this.LinearVelocityInterp linearVelocity =
        if not (Queue.isEmpty this.LinearVelocityPrevious) then
            let linearVelocities = Queue.conj linearVelocity this.LinearVelocityPrevious
            Seq.sum linearVelocities / single linearVelocities.Length
        else linearVelocity

    member this.AngularVelocityInterp angularVelocity =
        if not (Queue.isEmpty this.AngularVelocityPrevious) then
            let angularVelocities = Queue.conj angularVelocity this.AngularVelocityPrevious
            Seq.sum angularVelocities / single angularVelocities.Length
        else angularVelocity

    static member private computeTraversalAnimations rotation linearVelocity angularVelocity character =
        if character.ActionState <> WoundedState then
            let rotationInterp = character.RotationInterp rotation
            let linearVelocityInterp = character.LinearVelocityInterp linearVelocity
            let angularVelocityInterp = character.AngularVelocityInterp angularVelocity
            let forwardness = (Vector3.Dot (linearVelocityInterp * 32.0f, rotationInterp.Forward))
            let backness = (Vector3.Dot (linearVelocityInterp * 32.0f, -rotationInterp.Forward))
            let rightness = (Vector3.Dot (linearVelocityInterp * 32.0f, rotationInterp.Right))
            let leftness = (Vector3.Dot (linearVelocityInterp * 32.0f, -rotationInterp.Right))
            let turnRightness = (angularVelocityInterp * v3Up).Length () * 48.0f
            let turnLeftness = -turnRightness
            let animations =
                [{ StartTime = 0L; LifeTimeOpt = None; Name = "Armature|Idle"; Playback = Loop; Rate = 1.0f; Weight = 0.5f; BoneFilterOpt = None }]
            let animations =
                if forwardness >= 0.2f then { StartTime = 0L; LifeTimeOpt = None; Name = "Armature|WalkForward"; Playback = Loop; Rate = 1.0f; Weight = forwardness; BoneFilterOpt = None } :: animations
                elif backness >= 0.2f then { StartTime = 0L; LifeTimeOpt = None; Name = "Armature|WalkBack"; Playback = Loop; Rate = 1.0f; Weight = backness; BoneFilterOpt = None } :: animations
                else animations
            let animations =
                if rightness >= 0.2f then { StartTime = 0L; LifeTimeOpt = None; Name = "Armature|WalkRight"; Playback = Loop; Rate = 1.0f; Weight = rightness; BoneFilterOpt = None } :: animations
                elif leftness >= 0.2f then { StartTime = 0L; LifeTimeOpt = None; Name = "Armature|WalkLeft"; Playback = Loop; Rate = 1.0f; Weight = leftness; BoneFilterOpt = None } :: animations
                else animations
            let animations =
                if turnRightness >= 0.05f then { StartTime = 0L; LifeTimeOpt = None; Name = "Armature|TurnRight"; Playback = Loop; Rate = 1.0f; Weight = turnRightness; BoneFilterOpt = None } :: animations
                elif turnLeftness >= 0.05f then { StartTime = 0L; LifeTimeOpt = None; Name = "Armature|TurnLeft"; Playback = Loop; Rate = 1.0f; Weight = turnLeftness; BoneFilterOpt = None } :: animations
                else animations
            animations
        else []

    static member private tryComputeActionAnimation time character =
        match character.ActionState with
        | AttackState attack ->
            let localTime = time - attack.AttackTime
            let soundOpt =
                match localTime with
                | 7L -> Some Assets.Gameplay.SlashSound
                | 67L -> Some Assets.Gameplay.Slash2Sound
                | _ -> None
            let animationStartTime = GameTime.ofUpdates (time - localTime % 55L)
            let animationName = if localTime <= 55 then "Armature|AttackVertical" else "Armature|AttackHorizontal"
            let animation = { StartTime = animationStartTime; LifeTimeOpt = None; Name = animationName; Playback = Once; Rate = 1.0f; Weight = 32.0f; BoneFilterOpt = None }
            Some (soundOpt, animation)
        | InjuryState injury ->
            let localTime = time - injury.InjuryTime
            let soundOpt =
                match localTime with
                | 1L -> Some Assets.Gameplay.InjureSound
                | _ -> None
            let animationStartTime = GameTime.ofUpdates (time - localTime % 55L)
            let animation = { StartTime = animationStartTime; LifeTimeOpt = None; Name = "Armature|WalkBack"; Playback = Once; Rate = 1.0f; Weight = 32.0f; BoneFilterOpt = None }
            (Some (soundOpt, animation))
        | NormalState | WoundedState -> None

    static member private updateInterps position rotation linearVelocity angularVelocity character =

        // update interps
        let character =
            { character with
                PositionPrevious = (if character.PositionPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.PositionPrevious |> Queue.tail else character.PositionPrevious) |> Queue.conj position
                RotationPrevious = (if character.RotationPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.RotationPrevious |> Queue.tail else character.RotationPrevious) |> Queue.conj rotation
                LinearVelocityPrevious = (if character.LinearVelocityPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.LinearVelocityPrevious |> Queue.tail else character.LinearVelocityPrevious) |> Queue.conj linearVelocity
                AngularVelocityPrevious = (if character.AngularVelocityPrevious.Length >= Constants.Gameplay.CharacterInterpolationSteps then character.AngularVelocityPrevious |> Queue.tail else character.AngularVelocityPrevious) |> Queue.conj angularVelocity }

        // ensure previous positions interp aren't stale (such as when an entity is moved in the editor with existing previous position state)
        let character =
            let positionInterp = character.PositionInterp position
            if Vector3.Distance (positionInterp, position) > Constants.Gameplay.CharacterPositionInterpDistanceMax
            then { character with PositionPrevious = List.init Constants.Gameplay.CharacterInterpolationSteps (fun _ -> position) |> Queue.ofList }
            else character

        // fin
        character

    static member private updateMotion isKeyboardKeyDown nav3dFollow time position (rotation : Quaternion) grounded playerPosition character =

        // update jump state
        let lastTimeOnGround = if grounded then time else character.JumpState.LastTimeOnGround
        let character = { character with Character.JumpState.LastTimeOnGround = lastTimeOnGround }

        // update traversal
        if character.Player then

            // player traversal
            if character.ActionState = NormalState || not grounded then

                // compute new position
                let forward = rotation.Forward
                let right = rotation.Right
                let walkSpeed = character.WalkSpeed * if grounded then 1.0f else 0.75f
                let walkVelocity =
                    (if isKeyboardKeyDown KeyboardKey.W || isKeyboardKeyDown KeyboardKey.Up then forward * walkSpeed else v3Zero) +
                    (if isKeyboardKeyDown KeyboardKey.S || isKeyboardKeyDown KeyboardKey.Down then -forward * walkSpeed else v3Zero) +
                    (if isKeyboardKeyDown KeyboardKey.A then -right * walkSpeed else v3Zero) +
                    (if isKeyboardKeyDown KeyboardKey.D then right * walkSpeed else v3Zero)
                let position = if walkVelocity <> v3Zero then position + walkVelocity else position

                // compute new rotation
                let turnSpeed = character.TurnSpeed * if grounded then 1.0f else 0.75f
                let turnVelocity =
                    (if isKeyboardKeyDown KeyboardKey.Right then -turnSpeed else 0.0f) +
                    (if isKeyboardKeyDown KeyboardKey.Left then turnSpeed else 0.0f)
                let rotation = if turnVelocity <> 0.0f then rotation * Quaternion.CreateFromAxisAngle (v3Up, turnVelocity) else rotation
                (position, rotation, walkVelocity, v3 0.0f turnVelocity 0.0f, character)

            else (position, rotation, v3Zero, v3Zero, character)

        else // enemy traversal
            if character.ActionState = NormalState then
                let followOutput = nav3dFollow (Some 1.0f) (Some 10.0f) 0.04f 0.1f position rotation playerPosition
                (followOutput.NavPosition, followOutput.NavRotation, followOutput.NavLinearVelocity, followOutput.NavAngularVelocity, character)
            else (position, rotation, v3Zero, v3Zero, character)

    static member private updateAction time (position : Vector3) (rotation : Quaternion) (playerPosition : Vector3) character =
        if not character.Player then
            match character.ActionState with
            | NormalState when not character.Player ->
                let positionFlat = position.WithY 0.0f
                let playerPositionFlat = playerPosition.WithY 0.0f
                if  Vector3.Distance (playerPosition, position) < 1.5f &&
                    rotation.Forward.AngleBetween (playerPositionFlat - positionFlat) < 0.2f && 
                    playerPosition.Y - position.Y < 1.3f &&
                    position.Y - playerPosition.Y < 0.8f then
                    { character with ActionState = AttackState (AttackState.make time) }
                else character
            | _ -> character
        else character

    static member private updateState time character =
        match character.ActionState with
        | AttackState attack ->
            let actionState =
                let localTime = time - attack.AttackTime
                if localTime < 55 || localTime < 110 && attack.FollowUpBuffered
                then AttackState attack
                else NormalState
            { character with ActionState = actionState }
        | InjuryState injury ->
            let actionState =
                let localTime = time - injury.InjuryTime
                let injuryTime = if character.Player then 20 else 40
                if localTime < injuryTime
                then InjuryState injury
                else NormalState
            { character with ActionState = actionState }
        | NormalState -> character
        | WoundedState -> character

    static member private computeAnimations time position rotation linearVelocity angularVelocity character =
        ignore<Vector3> position
        let traversalAnimations = Character.computeTraversalAnimations rotation linearVelocity angularVelocity character
        let (soundOpt, animations) =
            match Character.tryComputeActionAnimation time character with
            | Some (soundOpt, animation) -> (soundOpt, animation :: traversalAnimations)
            | None -> (None, traversalAnimations)
        (soundOpt, animations)

    static member private updateAttackedCharacters time character =
        match character.ActionState with
        | AttackState attack ->
            let localTime = time - attack.AttackTime
            let attack =
                match localTime with
                | 55L -> { attack with AttackedCharacters = Set.empty } // reset attack tracking at start of buffered attack
                | _ -> attack
            if localTime >= 20 && localTime < 30 || localTime >= 78 && localTime < 88 then
                let attackingCharacters = Set.difference character.WeaponCollisions attack.AttackedCharacters
                let attack = { attack with AttackedCharacters = Set.union attack.AttackedCharacters character.WeaponCollisions }
                (attackingCharacters, { character with ActionState = AttackState attack })
            else (Set.empty, { character with ActionState = AttackState attack })
        | _ -> (Set.empty, character)

    static member updateInputKey time keyboardKeyData character =
        if character.Player then

            // jumping
            if keyboardKeyData.KeyboardKey = KeyboardKey.Space && not keyboardKeyData.Repeated then
                let sinceJump = time - character.JumpState.LastTime
                let sinceOnGround = time - character.JumpState.LastTimeOnGround
                if sinceJump >= 12L && sinceOnGround < 10L && character.ActionState = NormalState then
                    let character = { character with Character.JumpState.LastTime = time }
                    (true, character)
                else (false, character)

            // attacking
            elif keyboardKeyData.KeyboardKey = KeyboardKey.Rshift && not keyboardKeyData.Repeated then
                let character =
                    match character.ActionState with
                    | NormalState ->
                        { character with ActionState = AttackState (AttackState.make time) }
                    | AttackState attack ->
                        let localTime = time - attack.AttackTime
                        if localTime > 10L && not attack.FollowUpBuffered
                        then { character with ActionState = AttackState { attack with FollowUpBuffered = true }}
                        else character
                    | InjuryState _ | WoundedState -> character
                (false, character)
            else (false, character)

        else (false, character)

    static member update isKeyboardKeyDown nav3dFollow time position rotation linearVelocity angularVelocity grounded playerPosition character =
        let character = Character.updateInterps position rotation linearVelocity angularVelocity character
        let (position, rotation, linearVelocity, angularVelocity, character) = Character.updateMotion isKeyboardKeyDown nav3dFollow time position rotation grounded playerPosition character
        let character = Character.updateAction time position rotation playerPosition character
        let character = Character.updateState time character
        let (attackedCharacters, character) = Character.updateAttackedCharacters time character
        let (soundOpt, animations) = Character.computeAnimations time position rotation linearVelocity angularVelocity character
        (soundOpt, animations, attackedCharacters, position, rotation, character)

    static member initial =
        { Player = false
          PositionPrevious = Queue.empty
          RotationPrevious = Queue.empty
          LinearVelocityPrevious = Queue.empty
          AngularVelocityPrevious = Queue.empty
          HitPoints = 5
          ActionState = NormalState
          JumpState = JumpState.initial
          WeaponCollisions = Set.empty
          WalkSpeed = 0.05f
          TurnSpeed = 0.05f
          JumpSpeed = 5.0f
          WeaponModel = Assets.Gameplay.GreatSwordModel }

    static member initialPlayer =
        { Character.initial with WalkSpeed = 0.06f; Player = true }

    static member initialEnemy =
        { Character.initial with HitPoints = 3 }