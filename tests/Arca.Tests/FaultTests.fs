/// Operational GitHub failures are classified through Aegis (ARCA-ARCH-007).
module Arca.Tests.FaultTests

open System
open Aegis
open Aegis.Integration.GitHub
open Arca.GitHub
open Xunit

let private response status headers =
    HttpOutcome.Response(status, Http.normalizeHeaders headers, "{}")

[<Fact>]
let ``an exhausted primary rate limit is RateLimited, not an authentication failure`` () =
    let outcome = response 403 [ "X-RateLimit-Remaining", "0" ]
    Assert.Equal(Some(GitHubFailure.RateLimited None), Faults.ofOutcome "o/r" outcome)

[<Fact>]
let ``a secondary rate limit carries its retry-after`` () =
    let outcome = response 429 [ "Retry-After", "30" ]
    Assert.Equal(Some(GitHubFailure.RateLimited(Some(TimeSpan.FromSeconds 30.))), Faults.ofOutcome "o/r" outcome)

[<Fact>]
let ``other statuses use Aegis's own mapping`` () =
    Assert.Equal(Some GitHubFailure.AuthenticationFailed, Faults.ofOutcome "o/r" (response 401 []))
    Assert.Equal(Some(GitHubFailure.RepositoryNotFound "o/r"), Faults.ofOutcome "o/r" (response 404 []))
    Assert.Equal(None, Faults.ofOutcome "o/r" (response 200 []))

[<Fact>]
let ``transport failures that cannot have changed anything are classified`` () =
    Assert.Equal(Some GitHubFailure.NetworkUnavailable, Faults.ofOutcome "o/r" (HttpOutcome.Failed HttpFailure.Network))

    match Faults.ofOutcome "o/r" (HttpOutcome.Failed HttpFailure.TooLarge) with
    | Some(GitHubFailure.InvalidResponse _) -> ()
    | other -> failwith $"expected InvalidResponse, got {other}"

[<Fact>]
let ``OutcomeUnknown and cancellation are not faults: they are typed results the caller reconciles`` () =
    Assert.Equal(None, Faults.ofOutcome "o/r" (HttpOutcome.OutcomeUnknown UnknownReason.TimeoutAfterDispatch))
    Assert.Equal(None, Faults.ofOutcome "o/r" HttpOutcome.Cancelled)
    Assert.Equal(None, Faults.ofOutcome "o/r" (HttpOutcome.Failed HttpFailure.Aborted))

[<Fact>]
let ``a fault is built deterministically from supplied identity, with Aegis's GitHub code`` () =
    let at = DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero)

    let fault =
        Faults.toFault
            (FaultId "F-1", CorrelationId "C-1", at)
            ("chrona", Some "1.0.0")
            "arca.commit"
            Map.empty
            (GitHubFailure.RateLimited None)

    Assert.Equal(FaultId "F-1", fault.Id)
    Assert.Equal(at, fault.Timestamp)
    Assert.Equal(FaultCode "AEGIS.GITHUB.RATE_LIMITED", fault.Code)
    Assert.Equal("arca.commit", fault.Operation)
