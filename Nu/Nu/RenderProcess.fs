﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Collections.Generic
open System.Numerics
open System.Threading
open System.Threading.Tasks
open Prime
open Nu

/// A renderer process that may or may not be threaded.
type RendererProcess =
    interface
        abstract Started : bool
        abstract Terminated : bool
        abstract Start : unit -> unit
        abstract EnqueueMessage2d : RenderMessage2d -> unit
        abstract EnqueueMessage3d : RenderMessage3d -> unit
        abstract ClearMessages : unit -> unit
        abstract SubmitMessages : Vector2 -> Vector2 -> Vector3 -> Matrix4x4 -> Vector2i -> unit
        abstract Swap : unit -> unit
        abstract Terminate : unit -> unit
        end

/// A non-threaded renderer.
type RendererInline (createRenderer2d, createRenderer3d) =

    let mutable started = false
    let mutable terminated = false
    let mutable messages = SegmentedList.make ()
    let mutable rendererOpt = Option<Renderer2d>.None

    interface RendererProcess with

        member this.Started =
            started

        member this.Terminated =
            terminated

        member this.Start () =
            match rendererOpt with
            | Some _ -> raise (InvalidOperationException "Redundant Start calls.")
            | None ->
                rendererOpt <- Some (createRenderer2d ())
                started <- true

        member this.EnqueueMessage2d message =
            match rendererOpt with
            | Some _ -> SegmentedList.add message messages
            | None -> raise (InvalidOperationException "Renderer is not yet or is no longer valid.")

        member this.EnqueueMessage3d message =
            ()

        member this.ClearMessages () =
            messages <- SegmentedList.make ()

        member this.SubmitMessages eyePosition2d eyeSize2d eyePosition3d eyeRotation3d windowSize =
            match rendererOpt with
            | Some renderer ->
                renderer.Render eyePosition2d eyeSize2d windowSize messages
                SegmentedList.clear messages
            | None -> raise (InvalidOperationException "Renderer is not yet or is no longer valid.")

        member this.Swap () =
            match rendererOpt with
            | Some renderer -> renderer.Swap ()
            | None -> raise (InvalidOperationException "Renderer is not yet or is no longer valid.")

        member this.Terminate () =
            match rendererOpt with
            | Some renderer ->
                renderer.CleanUp ()
                rendererOpt <- None
                terminated <- true
            | None -> raise (InvalidOperationException "Redundant Terminate calls.")

/// A threaded renderer.
type RendererThread (createRenderer2d, createRenderer3d) =

    let mutable taskOpt = None
    let [<VolatileField>] mutable started = false
    let [<VolatileField>] mutable terminated = false
    let [<VolatileField>] mutable messages = SegmentedList.make ()
    let [<VolatileField>] mutable submissionOpt = Option<RenderMessage2d SegmentedList * Vector2 * Vector2 * Vector2i>.None
    let [<VolatileField>] mutable swap = false
    let cachedSpriteMessagesLock = obj ()
    let cachedSpriteMessages = Queue ()
    let mutable cachedSpriteMessagesCapacity = Constants.Render.SpriteMessagesPrealloc

    let allocSpriteMessage () =
        lock cachedSpriteMessagesLock (fun () ->
            if cachedSpriteMessages.Count = 0 then
                for _ in 0 .. dec cachedSpriteMessagesCapacity do
                    let spriteDescriptor = CachedSpriteDescriptor { CachedSprite = Unchecked.defaultof<_> }
                    let cachedSpriteMessage = RenderLayeredMessage2d { Elevation = 0.0f; Horizon = 0.0f; AssetTag = Unchecked.defaultof<_>; RenderDescriptor = spriteDescriptor }
                    cachedSpriteMessages.Enqueue cachedSpriteMessage
                cachedSpriteMessagesCapacity <- cachedSpriteMessagesCapacity * 2
                cachedSpriteMessages.Dequeue ()
            else cachedSpriteMessages.Dequeue ())

    let freeSpriteMessages messages =
        lock cachedSpriteMessagesLock (fun () ->
            for message in messages do
                match message with
                | RenderLayeredMessage2d layeredMessage ->
                    match layeredMessage.RenderDescriptor with
                    | CachedSpriteDescriptor _ -> cachedSpriteMessages.Enqueue message
                    | _ -> ()
                | _ -> ())

    member private this.Run () =

        // create renderer
        let renderer = createRenderer2d () : Renderer2d

        // mark as started
        started <- true

        // loop until terminated
        while not terminated do

            // loop until submission exists
            while Option.isNone submissionOpt && not terminated do Thread.Yield () |> ignore<bool>

            // guard against early termination
            if not terminated then

                // receive submission
                let (messages, eyePosition, eyeSize, windowSize) = Option.get submissionOpt
                submissionOpt <- None

                // render
                renderer.Render eyePosition eyeSize windowSize messages
            
                // recover cached sprite messages
                freeSpriteMessages messages

                // loop until swap is requested
                while not swap && not terminated do Thread.Yield () |> ignore<bool>

                // guard against early termination
                if not terminated then

                    // swap
                    renderer.Swap ()

                    // complete swap request
                    swap <- false

        // clean up
        renderer.CleanUp ()

    interface RendererProcess with

        member this.Started =
            started

        member this.Terminated =
            terminated

        member this.Start () =

            // validate state
            if Option.isSome taskOpt then raise (InvalidOperationException "Render process already started.")

            // start task
            let task = new Task ((fun () -> this.Run ()), TaskCreationOptions.LongRunning)
            taskOpt <- Some task
            task.Start ()

            // wait for task to finish starting
            while not started do Thread.Yield () |> ignore<bool>

        member this.EnqueueMessage2d message =
            if Option.isNone taskOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            match message with
            | RenderLayeredMessage2d layeredMessage ->
                match layeredMessage.RenderDescriptor with
                | SpriteDescriptor sprite ->
                    let cachedSpriteMessage = allocSpriteMessage ()
                    match cachedSpriteMessage with
                    | RenderLayeredMessage2d cachedLayeredMessage ->
                        match cachedLayeredMessage.RenderDescriptor with
                        | CachedSpriteDescriptor descriptor ->
                            cachedLayeredMessage.Elevation <- layeredMessage.Elevation
                            cachedLayeredMessage.Horizon <- layeredMessage.Horizon
                            cachedLayeredMessage.AssetTag <- layeredMessage.AssetTag
                            descriptor.CachedSprite.Transform <- sprite.Transform
                            descriptor.CachedSprite.InsetOpt <- sprite.InsetOpt
                            descriptor.CachedSprite.Image <- sprite.Image
                            descriptor.CachedSprite.Color <- sprite.Color
                            descriptor.CachedSprite.Blend <- sprite.Blend
                            descriptor.CachedSprite.Glow <- sprite.Glow
                            descriptor.CachedSprite.Flip <- sprite.Flip
                            SegmentedList.add cachedSpriteMessage messages
                        | _ -> failwithumf ()
                    | _ -> failwithumf ()
                | _ -> SegmentedList.add message messages
            | _ -> SegmentedList.add message messages

        member this.EnqueueMessage3d message =
            ()

        member this.ClearMessages () =
            if Option.isNone taskOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            messages <- SegmentedList.make ()

        member this.SubmitMessages eyePosition2d eyeSize2d eyePosition3d eyeRotation3d eyeMargin =
            if Option.isNone taskOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            while swap do Thread.Yield () |> ignore<bool>
            let messagesTemp = Interlocked.Exchange (&messages, SegmentedList.make ())
            submissionOpt <- Some (messagesTemp, eyePosition2d, eyeSize2d, eyeMargin)

        member this.Swap () =
            if Option.isNone taskOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            if swap then raise (InvalidOperationException "Redundant Swap calls.")
            swap <- true

        member this.Terminate () =
            if Option.isNone taskOpt then raise (InvalidOperationException "Render process not yet started or already terminated.")
            let task = Option.get taskOpt
            if terminated then raise (InvalidOperationException "Redundant Terminate calls.")
            terminated <- true
            task.Wait ()
            taskOpt <- None