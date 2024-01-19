﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2023.

namespace Nu
open System
open System.ComponentModel
open System.Numerics
open Prime
open Nu
open Nu.Effects
open Nu.Particles

[<RequireQualifiedAccess>]
module Effect =

    /// Converts effects, always providing an empty effect when converting from Symbol.
    type EffectConverter () =
        inherit TypeConverter ()

        override this.CanConvertTo (_, destType) =
            destType = typeof<Symbol> ||
            destType = typeof<Effect>

        override this.ConvertTo (_, _, source, destType) =
            if destType = typeof<Symbol> then Symbol.Symbols ([], ValueNone)
            elif destType = typeof<Effect> then source
            else failconv "Invalid EffectConverter conversion to source." None

        override this.CanConvertFrom (_, sourceType) =
            sourceType = typeof<Symbol> ||
            sourceType = typeof<GameTime>

        override this.ConvertFrom (_, _, source) =
            match source with
            | :? Symbol -> WorldModule.getEmptyEffect ()
            | :? Effect -> source
            | _ -> failconv "Invalid EffectConverter conversion from source." None

    /// An effect.
    and [<ReferenceEquality; NoComparison; TypeConverter (typeof<EffectConverter>)>] Effect =
        private
            { StartTime_ : GameTime
              PerimeterCentered_ : bool
              Offset_ : Vector3
              Transform_ : Transform
              RenderType_ : RenderType
              ParticleSystem_ : ParticleSystem
              HistoryMax_ : int
              History_ : Effects.Slice Deque
              Definitions_ : Definitions
              TagTokens_ : Map<string, Slice>
              Descriptor_ : EffectDescriptor }

        member this.StartTime = this.StartTime_
        member this.Transform = this.Transform_
        member this.RenderType = this.RenderType_
        member this.ParticleSystem = this.ParticleSystem_
        member this.HistoryMax = this.HistoryMax_
        member this.History = this.History_
        member this.Definitions = this.Definitions_
        member this.TagTokens = this.TagTokens_
        member this.Descriptor = this.Descriptor_

    let rec private processParticleSystemOutput output effect world =
        match output with
        | OutputEmitter (name, emitter) ->
            let particleSystem = { effect.ParticleSystem_ with Emitters = Map.add name emitter effect.ParticleSystem_.Emitters }
            let effect = { effect with ParticleSystem_ = particleSystem }
            effect
        | Outputs outputs ->
            SArray.fold (fun effect output ->
                processParticleSystemOutput output effect world)
                effect outputs

    let private liveness effect (world : World) =
        let time = world.GameTime
        let particleSystem = effect.ParticleSystem_
        let effectDescriptor = effect.Descriptor_
        match effectDescriptor.LifeTimeOpt with
        | Some lifeTime ->
            let localTime = time - effect.StartTime_
            if localTime <= lifeTime then Live
            else ParticleSystem.getLiveness time particleSystem
        | None -> Live

    /// Run an effect, returning any resulting requests as a token.
    let run effect (world : World) : Liveness * Effect * Token =

        // run if live
        match liveness effect world with
        | Live ->

            // set up effect system to evaluate effect
            let time = world.GameTime
            let localTime = time - effect.StartTime_
            let delta = world.GameDelta
            let mutable transform = effect.Transform_
            let effectSlice =
                { SliceTime = localTime
                  SliceDelta = delta
                  Position = transform.Position
                  Scale = transform.Scale
                  Angles = transform.Angles
                  Elevation = transform.Elevation
                  Offset = effect.Offset_
                  Size = transform.Size
                  Inset = Box2.Zero
                  Color = Color.One
                  Blend = Transparent
                  Emission = Color.Zero
                  Height = Constants.Render.HeightDefault
                  IgnoreLightMaps = false
                  Flip = FlipNone
                  Brightness = Constants.Render.BrightnessDefault
                  AttenuationLinear = Constants.Render.AttenuationLinearDefault
                  AttenuationQuadratic = Constants.Render.AttenuationQuadraticDefault
                  LightCutoff = Constants.Render.LightCutoffDefault
                  Volume = Constants.Audio.SoundVolumeDefault
                  Enabled = true
                  PerimeterCentered = effect.PerimeterCentered_ }
            let effectSystem = EffectSystem.make localTime delta transform.Absolute transform.Presence effect.RenderType_ effect.Definitions_

            // evaluate effect with effect system
            let (token, _) = EffectSystem.eval effect.Descriptor_ effectSlice effect.History_ effectSystem

            // convert token to sequence for storing tag tokens and spawning emitters
            let tokens = Token.toSeq token

            // extract tag tokens
            let tagTokens =
                tokens |>
                Seq.choose (function TagToken (name, value) -> Some (name, value :?> Slice) | _ -> None) |>
                Map.ofSeq

            // request emitters via tokens
            let particleSystem =
                tokens |>
                Seq.choose (function EmitterToken (name, descriptor) -> Some (name, descriptor) | _ -> None) |>
                Seq.choose (fun (name : string, descriptor : EmitterDescriptor) ->
                    match descriptor with
                    | :? BasicSpriteEmitterDescriptor as descriptor ->
                        match World.tryMakeEmitter time descriptor.LifeTimeOpt descriptor.ParticleLifeTimeMaxOpt descriptor.ParticleRate descriptor.ParticleMax descriptor.Style world with
                        | Some (:? BasicStaticSpriteEmitter as emitter) ->
                            let emitter =
                                { emitter with
                                    Body = descriptor.Body
                                    Blend = descriptor.Blend
                                    Image = descriptor.Image
                                    ParticleSeed = descriptor.ParticleSeed
                                    Constraint = descriptor.Constraint }
                            Some (name, emitter :> Emitter)
                        | _ -> None
                    | :? BasicBillboardEmitterDescriptor as descriptor ->
                        match World.tryMakeEmitter time descriptor.LifeTimeOpt descriptor.ParticleLifeTimeMaxOpt descriptor.ParticleRate descriptor.ParticleMax descriptor.Style world with
                        | Some (:? BasicStaticBillboardEmitter as emitter) ->
                            let emitter =
                                { emitter with
                                    Body = descriptor.Body
                                    Material = descriptor.Material
                                    RenderType = emitter.RenderType
                                    ParticleSeed = descriptor.ParticleSeed
                                    Constraint = descriptor.Constraint }
                            Some (name, emitter :> Emitter)
                        | _ -> None
                    | _ -> None) |>
                Seq.fold (fun particleSystem (name, emitter) ->
                    ParticleSystem.add name emitter particleSystem)
                    effect.ParticleSystem_

            // update effect history in-place
            effect.History_.AddToFront effectSlice
            if  effect.History_.Count > effect.HistoryMax_ then
                effect.History_.RemoveFromBack () |> ignore

            // update tag tokens
            let effect = { effect with TagTokens_ = tagTokens }

            // run particles
            let (particleSystem, output) = ParticleSystem.run delta time particleSystem
            let effect = { effect with ParticleSystem_ = particleSystem }
            let effect = processParticleSystemOutput output effect world

            // fin
            (Live, effect, token)

        // dead
        | Dead -> (Dead, effect, Token.empty)

    /// Make an effect.
    let makePlus startTime perimeterCentered offset transform renderType particleSystem historyMax history definitions descriptor =
        { StartTime_ = startTime
          PerimeterCentered_ = perimeterCentered
          Offset_ = offset
          Transform_ = transform
          RenderType_ = renderType
          ParticleSystem_ = particleSystem
          HistoryMax_ = historyMax
          History_ = history
          Definitions_ = definitions
          TagTokens_ = Map.empty
          Descriptor_ = descriptor }

    /// Make an effect.
    let make startTime offset transform renderType descriptor =
        makePlus startTime true offset transform renderType ParticleSystem.empty Constants.Effects.EffectHistoryMaxDefault (Deque ()) Map.empty descriptor

    /// The empty effect.
    let empty =
        make GameTime.zero v3Zero (Transform.makeEmpty ()) DeferredRenderType EffectDescriptor.empty

type Effect = Effect.Effect