﻿namespace Grace.Server

open Dapr.Actors
open Giraffe
open Grace.Actors
open Grace.Actors.BranchName
open Grace.Actors.Organization
open Grace.Actors.OrganizationName
open Grace.Actors.Owner
open Grace.Actors.OwnerName
open Grace.Actors.Repository
open Grace.Actors.RepositoryName
open Grace.Actors.Constants
open Grace.Server.ApplicationContext
open Grace.Actors.Reference
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Dto.Repository
open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Azure.Cosmos
open Microsoft.Azure.Cosmos.Linq
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Net
open System.Threading.Tasks
open System.Text

module Services =

    /// Gets the CorrelationId from HttpContext.Items.
    let getCorrelationId (context: HttpContext) = (context.Items[Constants.CorrelationId] :?> string)

    /// Defines the type of all server queries in Grace.
    ///
    /// Takes an HttpContext, the MaxCount of results to return, and the ActorProxy to use for the query, and returns a Task containing the return value.
    type QueryResult<'T, 'U when 'T :> IActor> = HttpContext -> int -> 'T -> Task<'U>

    let actorProxyFactory = ApplicationContext.ActorProxyFactory()

    /// <summary>
    /// Creates common metadata for Grace events.
    /// </summary>
    /// <param name="context">The current HttpContext.</param>
    let createMetadata (context: HttpContext): EventMetadata = 
        {
            Timestamp = getCurrentInstant();
            CorrelationId = context.Items[Constants.CorrelationId].ToString()
            Principal = context.User.Identity.Name;
            Properties = new Dictionary<string, string>()
        }

    /// <summary>
    /// Parses the incoming request body into the specified type.
    /// </summary>
    /// <param name="context">The current HttpContext.</param>
    let parse<'T when 'T :> CommonParameters> (context: HttpContext) = 
        task {
            let! parameters = context.BindJsonAsync<'T>()
            if String.IsNullOrEmpty(parameters.CorrelationId) then
                parameters.CorrelationId <- getCorrelationId context
            return parameters
        }

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to return to the user.</param>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let returnResult<'T> (statusCode: int) (result: 'T) (context: HttpContext) =
        task {
            Activity.Current.AddTag("correlation_id", getCorrelationId context)
                            .AddTag("http.status_code", statusCode) |> ignore
            context.SetStatusCode(statusCode)
            return! context.WriteJsonAsync(result)
        }

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns a 404 Not found status.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to return to the user.</param>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result404NotFound (context: HttpContext) =
        task {
            Activity.Current.AddTag("correlation_id", getCorrelationId context)
                            .AddTag("http.status_code", StatusCodes.Status404NotFound) |> ignore
            context.SetStatusCode(StatusCodes.Status404NotFound)
            return Some context
        }

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 200 Ok status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result200Ok<'T> = returnResult<'T> StatusCodes.Status200OK

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 400 Bad request status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result400BadRequest<'T> = returnResult<'T> StatusCodes.Status400BadRequest

    // /// <summary>
    // /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 404 Not found status.
    // /// </summary>
    // /// <param name="result">The result value to serialize into JSON.</param>
    // /// <param name="context">The current HttpContext.</param>
    // let result404NotFound<'T> = returnResult<'T> StatusCodes.Status404NotFound

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 500 Internal server error status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result500ServerError<'T> = returnResult<'T> StatusCodes.Status500InternalServerError
