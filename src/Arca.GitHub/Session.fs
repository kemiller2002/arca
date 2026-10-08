namespace Arca.GitHub

open System
open System.Text
open Aegis
open Aegis.Integration.GitHub
open Arca

/// The rate-limit evidence GitHub last reported (ARCA-API-003).
type RateBudget =
    { Limit: int option
      Remaining: int option
      /// When the window resets, in Unix seconds.
      ResetAt: int64 option
      Resource: string option }

/// How safe requests are retried (ARCA-API-003). Back-off is exponential
/// with jitter; a rate limit is waited out only when GitHub's own retry-after
/// is short, and otherwise returned as a typed result. The adapter never spins.
type RetryPolicy =
    { /// Attempts per request, including the first.
      MaxAttempts: int
      BaseDelay: TimeSpan
      MaxDelay: TimeSpan
      /// The longest retry-after the adapter waits out itself.
      MaxRateLimitWait: TimeSpan
      /// Fast-forward races retried before contention is reported.
      MaxCommitAttempts: int }

/// One cached GET response (ARCA-API-002).
type CacheEntry =
    { ETag: string option
      Body: string
      /// The URL names content-addressed state (a commit SHA), so the body
      /// can never change and is served without asking GitHub again.
      Immutable: bool }

/// A bounded response cache, newest first.
type ResponseCache =
    { Entries: Map<string, CacheEntry>
      Order: string list
      Capacity: int }

/// The adapter's state between calls: configuration, cache, rate evidence and
/// the capability snapshot. A plain immutable value: each operation returns
/// the next session.
[<NoEquality; NoComparison>]
type GitHubSession =
    { Config: GitHubConfig
      Policy: RetryPolicy
      Cache: ResponseCache
      Budget: RateBudget option
      /// The last capability snapshot; cleared after a provider refusal so it is revalidated (ARCA-AUTH-003).
      Snapshot: CapabilitySnapshot option
      /// The repository id this session first resolved (ARCA-AUTH-004).
      PinnedRepositoryId: string option }

/// A step of a GitHub operation: reads and returns the session, and ends in
/// a value or a storage failure.
type Op<'a> = GitHubSession -> Conversation<Result<'a, StorageFailure> * GitHubSession>

/// Composition of operation steps.
[<RequireQualifiedAccess>]
module Op =

    /// A finished step.
    let ret value : Op<'a> = fun session -> Done(Ok value, session)

    /// A failed step.
    let fail failure : Op<'a> = fun session -> Done(Error failure, session)

    /// Sequences two steps; a failure short-circuits.
    let bind (f: 'a -> Op<'b>) (step: Op<'a>) : Op<'b> =
        fun session ->
            step session
            |> Conversation.bind (fun (result, next) ->
                match result with
                | Ok value -> f value next
                | Error failure -> Done(Error failure, next))

    /// Runs a step and returns its result, success or failure, as a value.
    let attempt (step: Op<'a>) : Op<Result<'a, StorageFailure>> =
        fun session -> step session |> Conversation.map (fun (result, next) -> Ok result, next)

    /// A conversation as a step.
    let lift (conversation: Conversation<'a>) : Op<'a> =
        fun session -> conversation |> Conversation.map (fun value -> Ok value, session)

    /// The session.
    let session: Op<GitHubSession> = fun session -> Done(Ok session, session)

    /// Replaces the session.
    let update (f: GitHubSession -> GitHubSession) : Op<unit> =
        fun session -> Done(Ok(), f session)

/// Computation-expression syntax for operation steps.
type OpBuilder() =
    member _.Return value = Op.ret value
    member _.ReturnFrom(step: Op<'a>) = step
    member _.Bind(step: Op<'a>, f: 'a -> Op<'b>) = Op.bind f step
    member _.Zero() = Op.ret ()
    member _.Delay(f: unit -> Op<'a>) : Op<'a> = fun session -> f () session

/// The `op { }` builder.
[<AutoOpen>]
module OpSyntax =
    /// Builds an operation step.
    let op = OpBuilder()

/// Sessions, the response cache, rate evidence and retried requests.
[<RequireQualifiedAccess>]
module Session =

    /// The default retry policy.
    let standardPolicy =
        { MaxAttempts = 4
          BaseDelay = TimeSpan.FromSeconds 1.
          MaxDelay = TimeSpan.FromSeconds 30.
          MaxRateLimitWait = TimeSpan.FromSeconds 60.
          MaxCommitAttempts = 5 }

    /// A new session for a configuration.
    let create config =
        { Config = config
          Policy = standardPolicy
          Cache =
            { Entries = Map.empty
              Order = []
              Capacity = 512 }
          Budget = None
          Snapshot = None
          PinnedRepositoryId = None }

    /// The rate-limit evidence in a response's headers, if any.
    let observe (headers: Map<string, string>) =
        let number name =
            Http.header name headers
            |> Option.bind (fun value ->
                match Int64.TryParse value with
                | true, number -> Some number
                | _ -> None)

        match number "x-ratelimit-remaining" with
        | None -> None
        | Some remaining ->
            Some
                { Limit = number "x-ratelimit-limit" |> Option.map int
                  Remaining = Some(int remaining)
                  ResetAt = number "x-ratelimit-reset"
                  Resource = Http.header "x-ratelimit-resource" headers }

    /// A stable pseudo-random fraction in [0, 1) from a seed and an attempt
    /// number (FNV-1a). Jitter spreads retries without drawing randomness, so
    /// the adapter stays deterministic and testable.
    let jitter (seed: string) (attempt: int) =
        let bytes = Encoding.UTF8.GetBytes(seed + "#" + string attempt)
        let mutable hash = 14695981039346656037UL

        for b in bytes do
            hash <- (hash ^^^ uint64 b) * 1099511628211UL

        float (hash % 1000000UL) / 1000000.

    /// The back-off before retry `attempt` (1-based): exponential, capped, with
    /// jitter between half and all of the delay.
    let backoff (policy: RetryPolicy) (seed: string) (attempt: int) =
        let exponential = policy.BaseDelay.TotalMilliseconds * Math.Pow(2., float (attempt - 1))
        let capped = min exponential policy.MaxDelay.TotalMilliseconds
        TimeSpan.FromMilliseconds(capped * (0.5 + 0.5 * jitter seed attempt))

    let private remember (url: string) (entry: CacheEntry) (cache: ResponseCache) =
        let order = url :: (cache.Order |> List.filter ((<>) url))
        let kept, dropped = if order.Length > cache.Capacity then List.splitAt cache.Capacity order else order, []

        { cache with
            Entries = dropped |> List.fold (fun entries key -> Map.remove key entries) (Map.add url entry cache.Entries)
            Order = kept }

    /// The Aegis classification of a GitHub failure as a storage failure.
    let failed (failure: GitHubFailure.T) =
        match failure with
        | GitHubFailure.RateLimited retryAfter -> StorageFailure.RateLimited(retryAfter, None)
        | _ ->
            let (FaultCode code) = GitHubFailure.code failure
            StorageFailure.ProviderFailed(code, GitHubFailure.persistence failure = Transient, GitHubFailure.userMessage failure)

    /// An unexpected response as a storage failure.
    let unexpected (session: GitHubSession) (response: Response) =
        match Api.unexpected session.Config response with
        | CallFailure.Failed failure -> failed failure
        | CallFailure.Unknown _ -> StorageFailure.ProviderFailed("ARCA.GITHUB.UNKNOWN", true, "the request's outcome is unknown")
        | CallFailure.Cancelled -> StorageFailure.ProviderFailed("ARCA.CANCELLED", true, "the request was cancelled")

    let private retryAfter (headers: Map<string, string>) =
        Http.header "retry-after" headers
        |> Option.bind (fun value ->
            match Int32.TryParse value with
            | true, seconds when seconds >= 0 -> Some(TimeSpan.FromSeconds(float seconds))
            | _ -> None)

    let private resetAt (headers: Map<string, string>) =
        Http.header "x-ratelimit-reset" headers
        |> Option.bind (fun value ->
            match Int64.TryParse value with
            | true, seconds -> Some seconds
            | _ -> None)

    /// Sends a request that is safe to repeat (it changes nothing visible):
    /// network failures, unknown outcomes and 5xx are retried with back-off,
    /// short rate limits are waited out, and a long one is returned as
    /// RateLimited with GitHub's evidence.
    let sendSafe (token: AccessToken) (request: HttpRequest) : Op<Response> =
        let rec loop attempt : Op<Response> =
            fun session ->
                let policy = session.Policy
                let canRetry = attempt < policy.MaxAttempts

                Conversation.send (Some token) request
                |> Conversation.bind (fun outcome ->
                    match outcome with
                    | HttpOutcome.Response(status, headers, body) ->
                        let session =
                            match observe headers with
                            | Some budget -> { session with Budget = Some budget }
                            | None -> session

                        if Faults.isRateLimited status headers then
                            match retryAfter headers with
                            | Some wait when canRetry && wait <= policy.MaxRateLimitWait ->
                                Conversation.wait wait |> Conversation.bind (fun () -> loop (attempt + 1) session)
                            | wait -> Done(Error(StorageFailure.RateLimited(wait, resetAt headers)), session)
                        elif (status = 502 || status = 503 || status = 504) && canRetry then
                            Conversation.wait (backoff policy request.Url attempt)
                            |> Conversation.bind (fun () -> loop (attempt + 1) session)
                        else
                            Done(
                                Ok
                                    { Status = status
                                      Headers = headers
                                      Body = body },
                                session
                            )
                    | HttpOutcome.Failed HttpFailure.Network
                    | HttpOutcome.OutcomeUnknown _ when canRetry ->
                        Conversation.wait (backoff policy request.Url attempt)
                        |> Conversation.bind (fun () -> loop (attempt + 1) session)
                    | HttpOutcome.Cancelled -> Done(Error(StorageFailure.ProviderFailed("ARCA.CANCELLED", true, "the request was cancelled")), session)
                    | other ->
                        let failure =
                            Faults.ofOutcome (string session.Config.Location.Repository) other
                            |> Option.defaultValue GitHubFailure.Timeout

                        Done(Error(failed failure), session))

        loop 1

    /// A cached GET (ARCA-API-002). An immutable entry is served without a
    /// request; a mutable one is revalidated with If-None-Match, and a 304 is
    /// answered from the cache without spending rate budget.
    let get (token: AccessToken) (immutable: bool) (path: string) : Op<Response> =
        fun session ->
            let request = Api.request session.Config HttpMethod.Get path None
            let cached = Map.tryFind request.Url session.Cache.Entries

            match cached with
            | Some entry when entry.Immutable ->
                Done(
                    Ok
                        { Status = 200
                          Headers = Map.empty
                          Body = entry.Body },
                    session
                )
            | _ ->
                let conditional =
                    match cached |> Option.bind _.ETag with
                    | Some etag -> { request with Headers = request.Headers @ [ "If-None-Match", etag ] }
                    | None -> request

                sendSafe token conditional session
                |> Conversation.map (fun (result, next) ->
                    match result, cached with
                    | Ok response, Some entry when response.Status = 304 ->
                        Ok { response with Status = 200; Body = entry.Body }, next
                    | Ok response, _ when response.Status = 200 ->
                        let entry =
                            { ETag = Http.header "etag" response.Headers
                              Body = response.Body
                              Immutable = immutable }

                        let next =
                            if entry.Immutable || entry.ETag.IsSome then
                                { next with Cache = remember request.Url entry next.Cache }
                            else
                                next

                        Ok response, next
                    | other, _ -> other, next)
