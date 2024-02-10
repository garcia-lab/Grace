﻿namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.DirectoryVersion
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Threading.Tasks

module DirectoryVersion =

    let GetActorId (directoryId: DirectoryId) = ActorId($"{directoryId}")
 
    type DirectoryVersionActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.DirectoryVersion
        let log = loggerFactory.CreateLogger("DirectoryVersion.Actor")
        let dtoStateName = "DirectoryVersionState"
        let directoryVersionCacheStateName = "DirectoryVersionCacheState"

        let mutable methodStartTime = Instant.MinValue
        let mutable directoryVersion = DirectoryVersion.Default
        let mutable actorStartTime = Instant.MinValue
        let mutable logScope: IDisposable = null

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant()
            let stateManager = this.StateManager
            task {
                let mutable message = String.Empty
                try
                    let! retrievedDto = Storage.RetrieveState<DirectoryVersion> stateManager dtoStateName
                    match retrievedDto with
                        | Some retrievedDto -> 
                            directoryVersion <- retrievedDto
                            message <- "Retrieved from database."
                        | None ->
                            message <- "Not found in database."
                with ex ->
                    let exc = createExceptionResponse ex
                    log.LogError("{CurrentInstant} Error in {ActorType} {ActorId}.", getCurrentInstantExtended(), this.GetType().Name, host.Id)
                    log.LogError("{CurrentInstant} {ExceptionDetails}", getCurrentInstantExtended(), exc.ToString())

                let duration_ms = getCurrentInstant().Minus(activateStartTime).TotalMilliseconds.ToString("F3")
                log.LogInformation("{CurrentInstant}: Activated {ActorType} {ActorId}. {message} Duration: {duration_ms}ms.", getCurrentInstantExtended(), actorName, host.Id, message, duration_ms)
            } :> Task

        override this.OnPreActorMethodAsync(context) =
            this.correlationId <- String.Empty
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            log.LogTrace("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id)
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = (getCurrentInstant().Minus(actorStartTime).TotalMilliseconds).ToString("F3")
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName}; Id: {Id}; CorrelationId: {correlationId}; Duration: {duration_ms}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id, this.correlationId, duration_ms)
            logScope.Dispose()
            Task.CompletedTask

        member private this.SetReminderToDeleteCachedState() =
            this.RegisterReminderAsync("DeleteCachedState", Array.empty<byte>, TimeSpan.FromDays(1.0), TimeSpan.Zero)

            //try
            //    let result = this.RegisterReminderAsync("DeleteCachedState", Array.empty<byte>, TimeSpan.FromDays(1.0), TimeSpan.Zero)
            //    ()
            //with ex ->
            //    log.LogError("{CurrentInstant}: Error in {methodName}. Exception: {exception}", getCurrentInstantExtended(), nameof(this.SetReminderToDeleteCachedState), createExceptionResponse ex)

        interface IRemindable with
            member this.ReceiveReminderAsync(reminderName, state, dueTime, period) =
                let stateManager = this.StateManager
                match reminderName with
                | "DeleteCachedState" ->
                    task {
                        let! deleteSucceeded = Storage.DeleteState stateManager directoryVersionCacheStateName
                        ()
                    } :> Task
                | _ -> Task.CompletedTask

        interface IDirectoryVersionActor with
            member this.Exists correlationId = 
                this.correlationId <- correlationId
                (directoryVersion.CreatedAt > Instant.MinValue) |> returnTask

            member this.Delete correlationId =
                this.correlationId <- correlationId
                GraceResult.Error (GraceError.Create "Not implemented" correlationId) |> returnTask

            member this.Get correlationId = 
                this.correlationId <- correlationId
                directoryVersion |> returnTask

            member this.GetCreatedAt correlationId = 
                this.correlationId <- correlationId
                directoryVersion.CreatedAt |> returnTask

            member this.GetDirectories correlationId = 
                this.correlationId <- correlationId
                directoryVersion.Directories |> returnTask

            member this.GetFiles correlationId = 
                this.correlationId <- correlationId
                directoryVersion.Files |> returnTask

            member this.GetSha256Hash correlationId = 
                this.correlationId <- correlationId
                directoryVersion.Sha256Hash |> returnTask

            member this.GetSize correlationId = 
                this.correlationId <- correlationId
                directoryVersion.Size |> returnTask

            member this.GetSizeRecursive correlationId = 
                this.correlationId <- correlationId
                let stateManager = this.StateManager
                task {
                    if directoryVersion.RecursiveSize = Constants.InitialDirectorySize then
                        // If it hasn't yet been calculated, calculate it.
                        let tasks =
                            directoryVersion.Directories
                            |> Seq.map(fun directoryId ->
                                            task {
                                                let actorId = GetActorId directoryId
                                                let subdirectoryActor = actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.DirectoryVersion)
                                                return! subdirectoryActor.GetSizeRecursive correlationId
                                            })
                        Task.WaitAll(tasks.Cast<Task>().ToArray())
                        let recursiveSize =
                            (tasks
                            |> Seq.map (fun task -> task.Result)
                            |> Seq.sum)
                            + directoryVersion.Size
                        directoryVersion <- {directoryVersion with RecursiveSize = recursiveSize}
                        do! Storage.SaveState stateManager dtoStateName directoryVersion
                        return recursiveSize
                    else
                        // If it's already been calculated, just return it.
                        return directoryVersion.RecursiveSize
                }

            member this.GetDirectoryVersionsRecursive (forceRegenerate: bool) correlationId =
                this.correlationId <- correlationId
                let stateManager = this.StateManager
                task {
                    try
                        // Check if the subdirectory versions have already been generated and cached.
                        let cachedSubdirectoryVersions = 
                            task {
                                if not <| forceRegenerate then
                                    return! Storage.RetrieveState<List<DirectoryVersion>> stateManager directoryVersionCacheStateName
                                else
                                    return None
                            }

                        // If they have, return them.
                        match! cachedSubdirectoryVersions with
                        | Some subdirectoryVersions -> 
                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}). Retrieved SubdirectoryVersions from cache.", this.Id)
                            return subdirectoryVersions
                        // If they haven't, generate them by calling each subdirectory in parallel.
                        | None ->
                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}). SubdirectoryVersions will be generated. forceRegenerate: {forceRegenerate}", this.Id, forceRegenerate)
                            let subdirectoryVersions = ConcurrentQueue<DirectoryVersion>()
                            subdirectoryVersions.Enqueue(directoryVersion)
                            do! Parallel.ForEachAsync(directoryVersion.Directories, Constants.ParallelOptions, (fun directoryId ct ->
                                ValueTask(task {
                                    let actorId = GetActorId directoryId
                                    let subdirectoryActor = actorProxyFactory.CreateActorProxy<IDirectoryVersionActor>(actorId, ActorName.DirectoryVersion)
                                    let! subdirectoryContents = subdirectoryActor.GetDirectoryVersionsRecursive forceRegenerate correlationId
                                    for directoryVersion in subdirectoryContents do
                                        subdirectoryVersions.Enqueue(directoryVersion)
                                })))
                            let subdirectoryVersionsList = subdirectoryVersions.ToList()
                            do! Storage.SaveState stateManager directoryVersionCacheStateName subdirectoryVersionsList
                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}); Storing subdirectoryVersion list.", this.Id)
                            let! _ = this.SetReminderToDeleteCachedState()
                            log.LogDebug("In DirectoryVersionActor.GetDirectoryVersionsRecursive({id}); Delete cached state reminder was set.", this.Id)
                            return subdirectoryVersionsList
                    with ex ->
                        log.LogError("{CurrentInstant}: Error in {methodName}. Exception: {exception}", getCurrentInstantExtended(), nameof(this.SetReminderToDeleteCachedState), createExceptionResponse ex)
                        return List<DirectoryVersion>()
                }

            member this.Create (newDirectoryVersion: DirectoryVersion) correlationId =
                this.correlationId <- correlationId
                let stateManager = this.StateManager
                task {
                    if directoryVersion.CreatedAt > DirectoryVersion.Default.CreatedAt then
                        return Error (GraceError.Create (DirectoryVersionError.getErrorMessage DirectoryAlreadyExists) correlationId)
                    elif newDirectoryVersion.Size <> newDirectoryVersion.Files.Sum(fun file -> int64 file.Size) then
                        return Error (GraceError.Create (DirectoryVersionError.getErrorMessage InvalidSize) correlationId)
                    else
                        do! Storage.SaveState stateManager dtoStateName newDirectoryVersion
                        directoryVersion <- newDirectoryVersion
                        return Ok (GraceReturnValue.Create "Directory created." correlationId)
                }
