namespace Arca.GitHub

/// HTTP as data (ARCA-ARCH-002). Arca.GitHub never sends a request itself: it
/// describes requests with these types, and the host executes them through
/// whatever transport it has. In the browser that is a Limen kernel's Http
/// effect, whose request and outcome these types mirror one to one (Limen
/// `limen.core` HttpEffectRequest and EffectOutcome, protocol 1.4).
[<RequireQualifiedAccess>]
type HttpMethod =
    | Get
    | Post
    | Patch
    | Put
    | Delete

/// One request the host is asked to send.
type HttpRequest =
    { Method: HttpMethod
      /// Absolute https URL.
      Url: string
      /// Request headers other than credentials. Credentials are added by the
      /// host transport from the token the conversation obtained, so a request
      /// value never carries a token (ARCA-AUTH-002).
      Headers: (string * string) list
      /// Pre-serialized body, when there is one.
      Body: string option
      /// How long the host waits before reporting OutcomeUnknown or a failure.
      TimeoutMs: int
      /// Response headers the conversation needs returned (ETag, rate-limit headers).
      ResponseHeaders: string list }

/// Why a request failed in a way that cannot have changed anything.
[<RequireQualifiedAccess>]
type HttpFailure =
    /// The request never reached the server (offline, DNS, refused, or a safe method).
    | Network
    /// The host aborted the request before it was sent.
    | Aborted
    /// The response could not be read.
    | InvalidResponse
    /// The response body exceeded the host's limit.
    | TooLarge

/// Why a request's effect is unknown.
[<RequireQualifiedAccess>]
type UnknownReason =
    /// The request timed out after it was dispatched.
    | TimeoutAfterDispatch
    /// The connection was lost after a mutating request was dispatched.
    | ConnectionLost

/// What the host observed. OutcomeUnknown is never collapsed into Failed: a
/// request that may have reached GitHub must be reconciled, not retried blindly
/// (ARCA-OUT-001).
[<RequireQualifiedAccess>]
type HttpOutcome =
    /// A response arrived. Header names are lower case.
    | Response of status: int * headers: Map<string, string> * body: string
    | Failed of failure: HttpFailure
    | Cancelled
    | OutcomeUnknown of reason: UnknownReason

/// Construction helpers for requests and lookups on outcomes.
[<RequireQualifiedAccess>]
module Http =

    /// The response headers every GitHub conversation asks for: conditional
    /// requests and rate-limit evidence (ARCA-API-002, ARCA-API-003).
    let standardResponseHeaders =
        [ "etag"
          "retry-after"
          "x-ratelimit-limit"
          "x-ratelimit-remaining"
          "x-ratelimit-reset"
          "x-ratelimit-resource" ]

    /// A header's value, by case-insensitive name.
    let header (name: string) (headers: Map<string, string>) =
        headers |> Map.tryFind (name.ToLowerInvariant())

    /// Lower-cases header names so lookups do not depend on the host's casing.
    let normalizeHeaders (headers: (string * string) seq) =
        headers
        |> Seq.map (fun (name, value) -> name.ToLowerInvariant(), value)
        |> Map.ofSeq
