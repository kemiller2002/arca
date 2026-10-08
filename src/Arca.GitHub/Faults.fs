namespace Arca.GitHub

open System
open Aegis
open Aegis.Integration.GitHub

/// Classification of unexpected GitHub failures through Aegis (ARCA-ARCH-007).
///
/// Arca reuses Aegis's GitHub failure model (`GitHubFailure.T` and its
/// mapping) rather than redefining it. Only operational failures are
/// classified here. A conflict Arca detected, an OutcomeUnknown and a
/// validation refusal are typed results of their own that the application
/// decides on; they are not faults.
[<RequireQualifiedAccess>]
module Faults =

    let private retryAfter (headers: Map<string, string>) =
        Http.header "retry-after" headers
        |> Option.bind (fun value ->
            match Int32.TryParse value with
            | true, seconds when seconds >= 0 -> Some(TimeSpan.FromSeconds(float seconds))
            | _ -> None)

    /// GitHub answers an exhausted rate limit with 403 or 429 and either
    /// `x-ratelimit-remaining: 0` or a `retry-after` header (the secondary
    /// limit). That is not an authentication failure, whatever its status.
    let isRateLimited status (headers: Map<string, string>) =
        (status = 403 || status = 429)
        && (Http.header "x-ratelimit-remaining" headers = Some "0"
            || (Http.header "retry-after" headers).IsSome)

    /// The Aegis failure an HTTP outcome represents, when it is an operational
    /// failure. `repository` names the repository as `owner/name` (never a URL
    /// carrying credentials). Success, Cancelled and OutcomeUnknown are not
    /// faults and give None.
    let ofOutcome (repository: string) (outcome: HttpOutcome) : GitHubFailure.T option =
        match outcome with
        | HttpOutcome.Response(status, headers, _) when isRateLimited status headers ->
            Some(GitHubFailure.RateLimited(retryAfter headers))
        | HttpOutcome.Response(status, headers, _) -> GitHubFailure.ofStatus repository status (retryAfter headers)
        | HttpOutcome.Failed HttpFailure.Network -> Some GitHubFailure.NetworkUnavailable
        | HttpOutcome.Failed HttpFailure.InvalidResponse -> Some(GitHubFailure.InvalidResponse "unreadable response")
        | HttpOutcome.Failed HttpFailure.TooLarge -> Some(GitHubFailure.InvalidResponse "response exceeded the host's size limit")
        | HttpOutcome.Failed HttpFailure.Aborted
        | HttpOutcome.Cancelled
        | HttpOutcome.OutcomeUnknown _ -> None

    /// The Aegis fault for a GitHub failure. Identity (fault id, correlation id
    /// and time) is an input, so this stays deterministic. Context values must
    /// already be classified by the caller; Arca adds no token, URL or
    /// business content (ARCA-AUTH-002, ARCA-COMMIT-004).
    let toFault
        (identity: FaultId * CorrelationId * DateTimeOffset)
        (application: string, version: string option)
        (operation: string)
        (context: Map<string, ContextValue>)
        (failure: GitHubFailure.T)
        =
        Translation.toFault GitHubFailure.mapping identity (application, version) operation context None failure
