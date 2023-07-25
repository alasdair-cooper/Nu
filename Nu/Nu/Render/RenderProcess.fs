﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2023.

namespace Nu
open System
open System.Collections.Generic
open System.Numerics
open System.Threading
open SDL2
open ImGuiNET
open Prime
open Nu

/// A renderer process that may or may not be threaded.
type RendererProcess =
    interface
        /// Whether the rendering process has started.
        abstract Started : bool
        /// Whether the rendering process has been terminated.
        abstract Terminated : bool
        /// Start the rendering process.
        abstract Start : ImFontAtlasPtr option -> Window option -> unit
        /// Enqueue a 3d rendering message.
        abstract EnqueueMessage3d : RenderMessage3d -> unit
        /// Potential fast-path for rendering static models.
        abstract RenderStaticModelFast : bool * Matrix4x4 inref * Presence * Box2 voption * MaterialProperties inref * RenderType * StaticModel AssetTag -> unit
        /// Potential fast-path for rendering static model surfaces.
        abstract RenderStaticModelSurfaceFast : bool * Matrix4x4 inref * Box2 voption * MaterialProperties inref * RenderType * StaticModel AssetTag * int -> unit
        /// Enqueue a 2d rendering message.
        abstract EnqueueMessage2d : RenderMessage2d -> unit
        /// Potential fast-path for rendering layered sprite.
        abstract RenderLayeredSpriteFast : single * single * obj AssetTag * Transform inref * Box2 ValueOption inref * Image AssetTag * Color inref * Blend * Color inref * Flip -> unit
        /// Clear enqueued render messages.
        abstract ClearMessages : unit -> unit
        /// Submit enqueued render messages for processing.
        abstract SubmitMessages : bool -> Frustum -> Frustum -> Frustum -> Box3 -> Vector3 -> Quaternion -> Vector2 -> Vector2 -> Vector2i -> ImDrawDataPtr -> unit
        /// Request to swap the underlying render buffer.
        abstract Swap : unit -> unit
        /// Terminate the rendering process, blocking until termination is complete.
        abstract Terminate : unit -> unit
        end

/// A non-threaded render process.
type RendererInline () =

    let mutable started = false
    let mutable terminated = false
    let mutable windowOpt = Option<Window>.None
    let mutable messages3d = List ()
    let mutable messages2d = List ()
    let mutable renderersOpt = Option<Renderer3d * Renderer2d * RendererImGui>.None

    interface RendererProcess with

        member this.Started =
            started

        member this.Terminated =
            terminated

        member this.Start fontsOpt windowOpt_ =

            // assign windowOpt
            windowOpt <- windowOpt_

            // ensure renderers not already created
            match renderersOpt with
            | None ->

                // create renderers
                match windowOpt with
                | Some window ->
                
                    // create gl context
                    match window with
                    | SglWindow window ->
                        OpenGL.Hl.CreateSglContext window.SglWindow |> ignore<nativeint>
                        OpenGL.Hl.Assert ()
                    | WfglWindow window ->
                        window.CreateWfglContext ()
                        OpenGL.Hl.Assert ()

                    // listen to debug messages
                    OpenGL.Hl.AttachDebugMessageCallback ()
                    OpenGL.Hl.Assert ()

                    // create 3d renderer
                    let renderer3d = GlRenderer3d.make window :> Renderer3d
                    OpenGL.Hl.Assert ()

                    // create 2d renderer
                    let renderer2d = GlRenderer2d.make window :> Renderer2d
                    OpenGL.Hl.Assert ()

                    // create imgui renderer
                    let rendererImGui =
                        match fontsOpt with
                        | Some fonts -> GlRendererImGui.make fonts :> RendererImGui
                        | None -> StubRendererImGui.make () :> RendererImGui
                    OpenGL.Hl.Assert ()

                    // fin
                    renderersOpt <- Some (renderer3d, renderer2d, rendererImGui)

                // create stub renderers
                | None ->
                    let renderer3d = StubRenderer3d.make () :> Renderer3d
                    let renderer2d = StubRenderer2d.make () :> Renderer2d
                    let rendererImGui = StubRendererImGui.make () :> RendererImGui
                    renderersOpt <- Some (renderer3d, renderer2d, rendererImGui)
                    OpenGL.Hl.Assert ()

                // fin
                started <- true

            // fail on already created
            | Some _ -> raise (InvalidOperationException "Redundant Start calls.")

        member this.EnqueueMessage3d message =
            match renderersOpt with
            | Some _ -> messages3d.Add message 
            | None -> raise (InvalidOperationException "Renderers are not yet or are no longer valid.")

        member this.RenderStaticModelFast (absolute, modelMatrix, presence, insetOpt, materialProperties, renderType, staticModel) =
            match renderersOpt with
            | Some _ -> messages3d.Add (RenderStaticModel { Absolute = absolute; ModelMatrix = modelMatrix; Presence = presence; InsetOpt = Option.ofValueOption insetOpt; MaterialProperties = materialProperties; RenderType = renderType; StaticModel = staticModel })
            | None -> raise (InvalidOperationException "Renderers are not yet or are no longer valid.")

        member this.RenderStaticModelSurfaceFast (absolute, modelMatrix, insetOpt, materialProperties, renderType, staticModel, surfaceIndex) =
            match renderersOpt with
            | Some _ -> messages3d.Add (RenderStaticModelSurface { Absolute = absolute; ModelMatrix = modelMatrix; InsetOpt = Option.ofValueOption insetOpt; MaterialProperties = materialProperties; RenderType = renderType; StaticModel = staticModel; SurfaceIndex = surfaceIndex })
            | None -> raise (InvalidOperationException "Renderers are not yet or are no longer valid.")

        member this.EnqueueMessage2d message =
            match renderersOpt with
            | Some _ -> messages2d.Add message 
            | None -> raise (InvalidOperationException "Renderers are not yet or are no longer valid.")

        member this.RenderLayeredSpriteFast (elevation, horizon, assetTag, transform, insetOpt, image, color, blend, emission, flip) =
            match renderersOpt with
            | Some _ -> messages2d.Add (LayeredOperation2d { Elevation = elevation; Horizon = horizon; AssetTag = assetTag; RenderOperation2d = RenderSprite { Transform = transform; InsetOpt = insetOpt; Image = image; Color = color; Blend = blend; Emission = emission; Flip = flip }})
            | None -> raise (InvalidOperationException "Renderers are not yet or are no longer valid.")

        member this.ClearMessages () =
            messages3d.Clear ()
            messages2d.Clear ()

        member this.SubmitMessages skipCulling frustumEnclosed frustumExposed frustumImposter lightBox eyeCenter3d eyeRotation3d eyeCenter2d eyeSize2d windowSize drawData =
            match renderersOpt with
            | Some (renderer3d, renderer2d, rendererImGui) ->
                
                // begin frame
                let viewportOffset = Constants.Render.ViewportOffset windowSize
                OpenGL.Hl.BeginFrame viewportOffset
                OpenGL.Hl.Assert ()

                // render 3d
                renderer3d.Render skipCulling frustumEnclosed frustumExposed frustumImposter lightBox eyeCenter3d eyeRotation3d windowSize messages3d
                messages3d.Clear ()
                OpenGL.Hl.Assert ()

                // render 2d
                renderer2d.Render eyeCenter2d eyeSize2d windowSize messages2d
                messages2d.Clear ()
                OpenGL.Hl.Assert ()

                // render imgui
                rendererImGui.Render drawData

                // end frame
                OpenGL.Hl.EndFrame ()
                OpenGL.Hl.Assert ()

            | None -> raise (InvalidOperationException "Renderers are not yet or are no longer valid.")

        member this.Swap () =
            match windowOpt with
            | Some window ->
                match window with
                | SglWindow window -> SDL.SDL_GL_SwapWindow window.SglWindow
                | WfglWindow window -> window.Swap ()
            | None -> ()

        member this.Terminate () =
            match renderersOpt with
            | Some (renderer3d, renderer2d, rendererImGui) ->
                renderer3d.CleanUp ()
                renderer2d.CleanUp ()
                rendererImGui.CleanUp ()
                renderersOpt <- None
                terminated <- true
            | None -> raise (InvalidOperationException "Redundant Terminate calls.")

/// A threaded render process.
type RendererThread () =

    let mutable threadOpt = None
    let [<VolatileField>] mutable started = false
    let [<VolatileField>] mutable terminated = false
    let [<VolatileField>] mutable submissionOpt = Option<bool * Frustum * Frustum * Frustum * Box3 * RenderMessage3d List * RenderMessage2d List * Vector3 * Quaternion * Vector2 * Vector2 * Vector2i * ImDrawDataPtr>.None
    let [<VolatileField>] mutable swap = false
    let mutable messageBufferIndex = 0
    let messageBuffers3d = [|List (); List ()|]
    let messageBuffers2d = [|List (); List ()|]
    let cachedSpriteMessagesLock = obj ()
    let cachedSpriteMessages = System.Collections.Generic.Queue ()
    let mutable cachedSpriteMessagesCapacity = Constants.Render.SpriteMessagesPrealloc
    let cachedStaticModelMessagesLock = obj ()
    let cachedStaticModelMessages = System.Collections.Generic.Queue ()
    let mutable cachedStaticModelMessagesCapacity = Constants.Render.StaticModelMessagesPrealloc
    let cachedStaticModelSurfaceMessagesLock = obj ()
    let cachedStaticModelSurfaceMessages = System.Collections.Generic.Queue ()
    let mutable cachedStaticModelSurfaceMessagesCapacity = Constants.Render.StaticModelSurfaceMessagesPrealloc

    let allocStaticModelMessage () =
        lock cachedStaticModelMessagesLock (fun () ->
            if cachedStaticModelMessages.Count = 0 then
                for _ in 0 .. dec cachedStaticModelMessagesCapacity do
                    let staticModelDescriptor =
                        { CachedStaticModelAbsolute = Unchecked.defaultof<_>
                          CachedStaticModelMatrix = Unchecked.defaultof<_>
                          CachedStaticModelPresence = Unchecked.defaultof<_>
                          CachedStaticModelInsetOpt = Unchecked.defaultof<_>
                          CachedStaticModelMaterialProperties = Unchecked.defaultof<_>
                          CachedStaticModelRenderType = Unchecked.defaultof<_>
                          CachedStaticModel = Unchecked.defaultof<_> }
                    let cachedStaticModelMessage = RenderCachedStaticModel staticModelDescriptor
                    cachedStaticModelMessages.Enqueue cachedStaticModelMessage
                cachedStaticModelMessagesCapacity <- cachedStaticModelMessagesCapacity * 2
                cachedStaticModelMessages.Dequeue ()
            else cachedStaticModelMessages.Dequeue ())

    let allocStaticModelSurfaceMessage () =
        lock cachedStaticModelSurfaceMessagesLock (fun () ->
            if cachedStaticModelSurfaceMessages.Count = 0 then
                for _ in 0 .. dec cachedStaticModelSurfaceMessagesCapacity do
                    let staticModelSurfaceDescriptor =
                        { CachedStaticModelSurfaceAbsolute = Unchecked.defaultof<_>
                          CachedStaticModelSurfaceMatrix = Unchecked.defaultof<_>
                          CachedStaticModelSurfaceInsetOpt = Unchecked.defaultof<_>
                          CachedStaticModelSurfaceMaterialProperties = Unchecked.defaultof<_>
                          CachedStaticModelSurfaceRenderType = Unchecked.defaultof<_>
                          CachedStaticModelSurfaceModel = Unchecked.defaultof<_>
                          CachedStaticModelSurfaceIndex = Unchecked.defaultof<_> }
                    let cachedStaticModelSurfaceMessage = RenderCachedStaticModelSurface staticModelSurfaceDescriptor
                    cachedStaticModelSurfaceMessages.Enqueue cachedStaticModelSurfaceMessage
                cachedStaticModelSurfaceMessagesCapacity <- cachedStaticModelSurfaceMessagesCapacity * 2
                cachedStaticModelSurfaceMessages.Dequeue ()
            else cachedStaticModelSurfaceMessages.Dequeue ())

    let freeStaticModelMessages messages =
        lock cachedStaticModelMessagesLock (fun () ->
            for message in messages do
                match message with
                | RenderCachedStaticModel _ -> cachedStaticModelMessages.Enqueue message
                | _ -> ())

    let freeStaticModelSurfaceMessages messages =
        lock cachedStaticModelSurfaceMessagesLock (fun () ->
            for message in messages do
                match message with
                | RenderCachedStaticModelSurface _ -> cachedStaticModelSurfaceMessages.Enqueue message
                | _ -> ())

    let allocSpriteMessage () =
        lock cachedSpriteMessagesLock (fun () ->
            if cachedSpriteMessages.Count = 0 then
                for _ in 0 .. dec cachedSpriteMessagesCapacity do
                    let spriteDescriptor = RenderCachedSprite { CachedSprite = Unchecked.defaultof<_> }
                    let cachedSpriteMessage = LayeredOperation2d { Elevation = 0.0f; Horizon = 0.0f; AssetTag = Unchecked.defaultof<_>; RenderOperation2d = spriteDescriptor }
                    cachedSpriteMessages.Enqueue cachedSpriteMessage
                cachedSpriteMessagesCapacity <- cachedSpriteMessagesCapacity * 2
                cachedSpriteMessages.Dequeue ()
            else cachedSpriteMessages.Dequeue ())

    let freeSpriteMessages messages =
        lock cachedSpriteMessagesLock (fun () ->
            for message in messages do
                match message with
                | LayeredOperation2d opertion ->
                    match opertion.RenderOperation2d with
                    | RenderCachedSprite _ -> cachedSpriteMessages.Enqueue message
                    | _ -> ()
                | _ -> ())

    member private this.Run fontsOpt windowOpt =

        // create renderers
        let (renderer3d, renderer2d, rendererImGui) =
            match windowOpt with
            | Some window ->
                
                // create gl context
                match window with
                | SglWindow window ->
                    OpenGL.Hl.CreateSglContext window.SglWindow |> ignore<nativeint>
                    OpenGL.Hl.Assert ()
                | WfglWindow window ->
                    window.CreateWfglContext ()
                    OpenGL.Hl.Assert ()

                // listen to debug messages
                OpenGL.Hl.AttachDebugMessageCallback ()
                OpenGL.Hl.Assert ()

                // create 3d renderer
                let renderer3d = GlRenderer3d.make window :> Renderer3d
                OpenGL.Hl.Assert ()

                // create 2d renderer
                let renderer2d = GlRenderer2d.make window :> Renderer2d
                OpenGL.Hl.Assert ()

                // create imgui renderer
                let rendererImGui =
                    match fontsOpt with
                    | Some fonts -> GlRendererImGui.make fonts :> RendererImGui
                    | None -> StubRendererImGui.make () :> RendererImGui

                // fin
                (renderer3d, renderer2d, rendererImGui)

            // create stub renderers
            | None ->
                let renderer3d = StubRenderer3d.make () :> Renderer3d
                let renderer2d = StubRenderer2d.make () :> Renderer2d
                let rendererImGui = StubRendererImGui.make () :> RendererImGui
                (renderer3d, renderer2d, rendererImGui)

        // mark as started
        started <- true

        // loop until terminated
        while not terminated do

            // loop until submission exists
            while Option.isNone submissionOpt && not terminated do Thread.Sleep 1

            // guard against early termination
            if not terminated then

                // receie submission
                let (skipCulling, frustumEnclosed, frustumExposed, frustumImposter, lightBox, messages3d, messages2d, eyeCenter3d, eyeRotation3d, eyeCenter2d, eyeSize2d, windowSize, drawData) = Option.get submissionOpt
                submissionOpt <- None
                
                // begin frame
                let viewportOffset = Constants.Render.ViewportOffset windowSize
                OpenGL.Hl.BeginFrame viewportOffset
                OpenGL.Hl.Assert ()

                // render 3d
                renderer3d.Render skipCulling frustumEnclosed frustumExposed frustumImposter lightBox eyeCenter3d eyeRotation3d windowSize messages3d
                freeStaticModelMessages messages3d
                freeStaticModelSurfaceMessages messages3d
                OpenGL.Hl.Assert ()

                // render 2d
                renderer2d.Render eyeCenter2d eyeSize2d windowSize messages2d
                freeSpriteMessages messages2d
                OpenGL.Hl.Assert ()
            
                // render imgui
                rendererImGui.Render drawData
                OpenGL.Hl.Assert ()

                // end frame
                OpenGL.Hl.EndFrame ()
                OpenGL.Hl.Assert ()

                // loop until swap is requested
                while not swap && not terminated do Thread.Sleep 1

                // guard against early termination
                if not terminated then

                    // attempt to swap
                    match windowOpt with
                    | Some window ->
                        match window with
                        | SglWindow window -> SDL.SDL_GL_SwapWindow window.SglWindow
                        | WfglWindow window -> window.Swap ()
                    | None -> ()

                    // complete swap request
                    swap <- false

        // clean up
        renderer2d.CleanUp ()

    interface RendererProcess with

        member this.Started =
            started

        member this.Terminated =
            terminated

        member this.Start fontsOpt windowOpt =

            // validate state
            if Option.isSome threadOpt then raise (InvalidOperationException "Render process already started.")

            // start thread
            let thread = Thread (ThreadStart (fun () -> this.Run fontsOpt windowOpt))
            threadOpt <- Some thread
            thread.IsBackground <- true
            thread.Start ()

            // wait for thread to finish starting
            while not started do Thread.Yield () |> ignore<bool>

        member this.EnqueueMessage3d message =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            match message with
            | RenderStaticModel rsm ->
                let cachedStaticModelMessage = allocStaticModelMessage ()
                match cachedStaticModelMessage with
                | RenderCachedStaticModel cachedMessage ->
                    cachedMessage.CachedStaticModelAbsolute <- rsm.Absolute
                    cachedMessage.CachedStaticModelMatrix <- rsm.ModelMatrix
                    cachedMessage.CachedStaticModelPresence <- rsm.Presence
                    cachedMessage.CachedStaticModelInsetOpt <- ValueOption.ofOption rsm.InsetOpt
                    cachedMessage.CachedStaticModelMaterialProperties <- rsm.MaterialProperties
                    cachedMessage.CachedStaticModelRenderType <- rsm.RenderType
                    cachedMessage.CachedStaticModel <- rsm.StaticModel
                    messageBuffers3d.[messageBufferIndex].Add cachedStaticModelMessage
                | _ -> failwithumf ()
            | RenderStaticModelSurface rsms ->
                let cachedStaticModelSurfaceMessage = allocStaticModelSurfaceMessage ()
                match cachedStaticModelSurfaceMessage with
                | RenderCachedStaticModelSurface cachedMessage ->
                    cachedMessage.CachedStaticModelSurfaceAbsolute <- rsms.Absolute
                    cachedMessage.CachedStaticModelSurfaceMatrix <- rsms.ModelMatrix
                    cachedMessage.CachedStaticModelSurfaceInsetOpt <- ValueOption.ofOption rsms.InsetOpt
                    cachedMessage.CachedStaticModelSurfaceMaterialProperties <- rsms.MaterialProperties
                    cachedMessage.CachedStaticModelSurfaceRenderType <- rsms.RenderType
                    cachedMessage.CachedStaticModelSurfaceModel <- rsms.StaticModel
                    cachedMessage.CachedStaticModelSurfaceIndex <- rsms.SurfaceIndex
                    messageBuffers3d.[messageBufferIndex].Add cachedStaticModelSurfaceMessage
                | _ -> failwithumf ()
            | _ -> messageBuffers3d.[messageBufferIndex].Add message

        member this.RenderStaticModelFast (absolute, modelMatrix, presence, insetOpt, materialProperties, renderType, staticModel) =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            let cachedStaticModelMessage = allocStaticModelMessage ()
            match cachedStaticModelMessage with
            | RenderCachedStaticModel cachedMessage ->
                cachedMessage.CachedStaticModelAbsolute <- absolute
                cachedMessage.CachedStaticModelMatrix <- modelMatrix
                cachedMessage.CachedStaticModelPresence <- presence
                cachedMessage.CachedStaticModelInsetOpt <- insetOpt
                cachedMessage.CachedStaticModelMaterialProperties <- materialProperties
                cachedMessage.CachedStaticModelRenderType <- renderType
                cachedMessage.CachedStaticModel <- staticModel
                messageBuffers3d.[messageBufferIndex].Add cachedStaticModelMessage
            | _ -> failwithumf ()

        member this.RenderStaticModelSurfaceFast (absolute, modelMatrix, insetOpt, materialProperties, renderType, staticModel, surfaceIndex) =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            let cachedStaticModelSurfaceMessage = allocStaticModelSurfaceMessage ()
            match cachedStaticModelSurfaceMessage with
            | RenderCachedStaticModelSurface cachedMessage ->
                cachedMessage.CachedStaticModelSurfaceAbsolute <- absolute
                cachedMessage.CachedStaticModelSurfaceMatrix <- modelMatrix
                cachedMessage.CachedStaticModelSurfaceInsetOpt <- insetOpt
                cachedMessage.CachedStaticModelSurfaceMaterialProperties <- materialProperties
                cachedMessage.CachedStaticModelSurfaceRenderType <- renderType
                cachedMessage.CachedStaticModelSurfaceModel <- staticModel
                cachedMessage.CachedStaticModelSurfaceIndex <- surfaceIndex
                messageBuffers3d.[messageBufferIndex].Add cachedStaticModelSurfaceMessage
            | _ -> failwithumf ()

        member this.EnqueueMessage2d message =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            match message with
            | LayeredOperation2d operation ->
                match operation.RenderOperation2d with
                | RenderSprite sprite ->
                    let cachedSpriteMessage = allocSpriteMessage ()
                    match cachedSpriteMessage with
                    | LayeredOperation2d cachedOperation ->
                        match cachedOperation.RenderOperation2d with
                        | RenderCachedSprite descriptor ->
                            cachedOperation.Elevation <- operation.Elevation
                            cachedOperation.Horizon <- operation.Horizon
                            cachedOperation.AssetTag <- operation.AssetTag
                            descriptor.CachedSprite.Transform <- sprite.Transform
                            descriptor.CachedSprite.InsetOpt <- sprite.InsetOpt
                            descriptor.CachedSprite.Image <- sprite.Image
                            descriptor.CachedSprite.Color <- sprite.Color
                            descriptor.CachedSprite.Blend <- sprite.Blend
                            descriptor.CachedSprite.Emission <- sprite.Emission
                            descriptor.CachedSprite.Flip <- sprite.Flip
                            messageBuffers2d.[messageBufferIndex].Add cachedSpriteMessage 
                        | _ -> failwithumf ()
                    | _ -> failwithumf ()
                | _ -> messageBuffers2d.[messageBufferIndex].Add message
            | _ -> messageBuffers2d.[messageBufferIndex].Add message

        member this.RenderLayeredSpriteFast (elevation, horizon, assetTag, transform, insetOpt, image, color, blend, emission, flip) =
            let cachedSpriteMessage = allocSpriteMessage ()
            match cachedSpriteMessage with
            | LayeredOperation2d cachedOperation ->
                match cachedOperation.RenderOperation2d with
                | RenderCachedSprite descriptor ->
                    cachedOperation.Elevation <- elevation
                    cachedOperation.Horizon <- horizon
                    cachedOperation.AssetTag <- assetTag
                    descriptor.CachedSprite.Transform <- transform
                    descriptor.CachedSprite.InsetOpt <- insetOpt
                    descriptor.CachedSprite.Image <- image
                    descriptor.CachedSprite.Color <- color
                    descriptor.CachedSprite.Blend <- blend
                    descriptor.CachedSprite.Emission <- emission
                    descriptor.CachedSprite.Flip <- flip
                    messageBuffers2d.[messageBufferIndex].Add cachedSpriteMessage 
                | _ -> failwithumf ()
            | _ -> failwithumf ()

        member this.ClearMessages () =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            messageBuffers3d.[messageBufferIndex].Clear ()
            messageBuffers2d.[messageBufferIndex].Clear ()

        member this.SubmitMessages skipCulling frustumEnclosed frustumExposed frustumImposter lightBox eyeCenter3d eyeRotation3d eyeCenter2d eyeSize2d eyeMargin drawData =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            let messages3d = messageBuffers3d.[messageBufferIndex]
            let messages2d = messageBuffers2d.[messageBufferIndex]
            messageBufferIndex <- if messageBufferIndex = 0 then 1 else 0
            messageBuffers3d.[messageBufferIndex].Clear ()
            messageBuffers2d.[messageBufferIndex].Clear ()
            submissionOpt <- Some (skipCulling, frustumEnclosed, frustumExposed, frustumImposter, lightBox, messages3d, messages2d, eyeCenter3d, eyeRotation3d, eyeCenter2d, eyeSize2d, eyeMargin, drawData)

        member this.Swap () =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            swap <- true
            while swap do Thread.Yield () |> ignore<bool>

        member this.Terminate () =
            if Option.isNone threadOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            let thread = Option.get threadOpt
            if terminated then raise (InvalidOperationException "Redundant Terminate calls.")
            terminated <- true
            thread.Join ()
            threadOpt <- None