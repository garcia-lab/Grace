namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks
open Types

module Reference =

    let GetActorId (referenceId: ReferenceId) = ActorId($"{referenceId}")

    type ReferenceActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = ActorName.Reference
        let mutable actorStartTime = Instant.MinValue
        let log = loggerFactory.CreateLogger("Reference.Actor")
        let mutable logScope: IDisposable = null
        let dtoStateName = "ReferenceDtoState"
        let mutable referenceDto = None

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync() =
            let activateStartTime = getCurrentInstant ()
            let stateManager = this.StateManager

            task {
                let mutable message = String.Empty
                let! retrievedDto = Storage.RetrieveState<ReferenceDto> stateManager dtoStateName

                match retrievedDto with
                | Some retrievedDto ->
                    referenceDto <- Some retrievedDto
                    message <- "Retrieved from database."
                | None -> message <- "Not found in database."

                let duration_ms = getPaddedDuration_ms activateStartTime

                log.LogInformation(
                    "{CurrentInstant}: Duration: {duration_ms}ms; Activated {ActorType} {ActorId}. {message}.",
                    getCurrentInstantExtended (),
                    duration_ms,
                    actorName,
                    host.Id,
                    message
                )
            }
            :> Task

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant ()
            this.correlationId <- String.Empty
            logScope <- log.BeginScope("Actor {actorName}", actorName)

            log.LogTrace(
                "{CurrentInstant}: Started {ActorName}.{MethodName} ReferenceId: {Id}.",
                getCurrentInstantExtended (),
                actorName,
                context.MethodName,
                this.Id
            )

            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration_ms = getPaddedDuration_ms actorStartTime

            log.LogInformation(
                "{CurrentInstant}: CorrelationId: {correlationId}; Duration: {duration_ms}ms; Finished {ActorName}.{MethodName}; ReferenceId: {ReferenceId}.",
                getCurrentInstantExtended (),
                this.correlationId,
                duration_ms,
                actorName,
                context.MethodName,
                this.Id
            )

            logScope.Dispose()
            Task.CompletedTask

        interface IReferenceActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                (if referenceDto.IsSome then true else false) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId
                referenceDto.Value |> returnTask

            member this.GetReferenceType correlationId =
                this.correlationId <- correlationId
                referenceDto.Value.ReferenceType |> returnTask

            member this.Create (referenceId, branchId, directoryId, sha256Hash, referenceType, referenceText) correlationId =
                this.correlationId <- correlationId
                let stateManager = this.StateManager

                task {
                    referenceDto <-
                        Some
                            { ReferenceDto.Default with
                                ReferenceId = referenceId
                                BranchId = branchId
                                DirectoryId = directoryId
                                Sha256Hash = sha256Hash
                                ReferenceType = referenceType
                                ReferenceText = referenceText }

                    do! Storage.SaveState stateManager dtoStateName referenceDto.Value
                    return referenceDto.Value
                }

            member this.Delete correlationId =
                let stateManager = this.StateManager

                task {
                    let! deleteSucceeded = Storage.DeleteState stateManager dtoStateName
                    return Ok(GraceReturnValue.Create "Reference deleted." correlationId)
                }
