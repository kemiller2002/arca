/// The token-provider port, identity resolution and the capability snapshot
/// (ARCA-AUTH-001..005).
module Arca.Tests.CredentialTests

open System.Text.Json
open Arca
open Arca.GitHub
open Arca.Tests.FakeGitHub
open Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private secret = "ghp_secret0123456789abcdefghijABCDEFGH"

let private config (server: Server) =
    GitHubConfig.create (DataLocation.create server.Owner server.Name "main" "apps" |> ok)

[<Fact>]
let ``a token never shows its value when printed, formatted or serialized (ARCA-AUTH-002)`` () =
    let token = AccessToken.create secret |> ok
    Assert.Equal("AccessToken(redacted)", string token)
    Assert.DoesNotContain(secret, sprintf "%A" token)
    Assert.DoesNotContain(secret, sprintf "%A" (Some token, [ token ]))
    Assert.DoesNotContain(secret, JsonSerializer.Serialize token)

    let request =
        { Request = Api.request (config (Server("acme", "data"))) HttpMethod.Get "/user" None
          Credential = Some token }

    Assert.DoesNotContain(secret, sprintf "%A" request)
    Assert.DoesNotContain(secret, string request)

[<Fact>]
let ``the host transport alone reads the token, as an Authorization header`` () =
    let token = AccessToken.create secret |> ok
    Assert.Equal(("Authorization", "Bearer " + secret), AccessToken.authorization token)

[<Theory>]
[<InlineData("")>]
[<InlineData("has space")>]
[<InlineData("line\nbreak")>]
let ``malformed tokens are refused`` (text: string) =
    Assert.True(Result.isError (AccessToken.create text))

[<Fact>]
let ``without a token nothing is sent (ARCA-AUTH-001, ARCA-AUTH-004)`` () =
    let server = Server("acme", "data")

    let result, _ =
        Identity.resolve (config server)
        |> Conversation.simulate server.Send (fun () -> Error TokenUnavailable.Expired) []

    Assert.Equal(Error(ResolveError.CredentialUnavailable TokenUnavailable.Expired), result)
    Assert.Empty(server.Requests)

[<Fact>]
let ``the identity comes from GitHub, never from a typed username (ARCA-AUTH-003)`` () =
    let server = Server("acme", "data")
    let snapshot, _ = run server (Identity.resolve (config server))
    let snapshot = snapshot |> ok

    Assert.Equal(
        { Provider = "github"
          Subject = "583231"
          Login = Some "octocat"
          Kind = IdentityKind.User },
        snapshot.Identity
    )

    Assert.Equal("9001", snapshot.RepositoryId)
    Assert.Equal(RepositoryVisibility.Private, snapshot.Visibility)
    Assert.True(snapshot.CanRead && snapshot.CanWrite && not snapshot.Archived)
    Assert.Equal(BranchAccess.Writable, snapshot.Branch)
    Assert.Equal(Ok(), CapabilitySnapshot.permitsWrite snapshot)

[<Fact>]
let ``every request carries GitHub's headers and the credential, with branches escaped per segment`` () =
    let server = Server("acme", "data")
    server.CreateBranch "release/2026"
    let location = DataLocation.create "acme" "data" "release/2026" "" |> ok
    let result, _ = run server (Identity.resolve (GitHubConfig.create location))
    Assert.True(Result.isOk result)

    for sent in server.Requests do
        Assert.True(sent.Credential.IsSome)
        Assert.Contains(("Accept", "application/vnd.github+json"), sent.Request.Headers)
        Assert.Contains(("X-GitHub-Api-Version", "2022-11-28"), sent.Request.Headers)
        Assert.DoesNotContain(sent.Request.Headers, fun (name, _) -> name = "Authorization")

    Assert.Contains(server.Requests, fun sent -> sent.Request.Url.EndsWith "/repos/acme/data/branches/release/2026")

[<Fact>]
let ``a rejected token is CredentialRejected`` () =
    let server = Server("acme", "data")

    let result, _ =
        Identity.resolve (config server)
        |> Conversation.simulate server.Send (fun () -> AccessToken.create "ghp_wrong0123456789abcdefghij" |> Result.mapError (fun _ -> TokenUnavailable.NoToken)) []

    Assert.Equal(Error ResolveError.CredentialRejected, result)

[<Fact>]
let ``an installation token resolves to an installation identity`` () =
    let server = Server("acme", "data")
    server.Identity <- AsInstallation
    let snapshot = run server (Identity.resolve (config server)) |> fst |> ok
    Assert.Equal(IdentityKind.Installation, snapshot.Identity.Kind)
    Assert.Equal(None, snapshot.Identity.Login)

[<Fact>]
let ``a rate-limited 403 is a rate limit, not an installation identity`` () =
    let server = Server("acme", "data")
    server.Inject(request HttpMethod.Get "/user", Answer(403, [ "x-ratelimit-remaining", "0" ], "{}"))

    match run server (Identity.resolve (config server)) |> fst with
    | Error(ResolveError.Call(CallFailure.Failed(Aegis.Integration.GitHub.GitHubFailure.RateLimited _))) -> ()
    | other -> failwith $"expected a rate limit, got {other}"

[<Fact>]
let ``a missing or invisible repository is reported, not guessed`` () =
    let server = Server("acme", "data")
    let location = DataLocation.create "acme" "other" "main" "" |> ok
    let result = run server (Identity.resolve (GitHubConfig.create location)) |> fst
    Assert.Equal(Error(ResolveError.RepositoryNotFound "acme/other"), result)

[<Fact>]
let ``archived, read-only, rule-protected and missing branches refuse writes explicitly (ARCA-COMMIT-006)`` () =
    let snapshotOf (configure: Server -> unit) branch =
        let server = Server("acme", "data")
        configure server
        let location = DataLocation.create "acme" "data" branch "" |> ok
        run server (Identity.resolve (GitHubConfig.create location)) |> fst |> ok

    let archived = snapshotOf (fun s -> s.Archived <- true) "main"
    Assert.Equal(Error WriteRefusal.RepositoryArchived, CapabilitySnapshot.permitsWrite archived)

    let readOnly = snapshotOf (fun s -> s.CanPush <- false) "main"
    Assert.Equal(Error WriteRefusal.ReadOnlyAccess, CapabilitySnapshot.permitsWrite readOnly)

    let ruled = snapshotOf (fun s -> s.Rules <- [ "pull_request"; "non_fast_forward"; "required_status_checks" ]) "main"
    Assert.Equal(BranchAccess.NotWritable [ "pull_request"; "required_status_checks" ], ruled.Branch)
    Assert.Equal(Error WriteRefusal.BranchProtected, CapabilitySnapshot.permitsWrite ruled)

    let allowedRules = snapshotOf (fun s -> s.Rules <- [ "non_fast_forward"; "deletion" ]) "main"
    Assert.Equal(BranchAccess.Writable, allowedRules.Branch)

    let missing = snapshotOf ignore "does-not-exist"
    Assert.Equal(BranchAccess.Missing, missing.Branch)
    Assert.Equal(Error WriteRefusal.BranchProtected, CapabilitySnapshot.permitsWrite missing)

[<Fact>]
let ``a replaced credential that resolves to another repository is refused until confirmed (ARCA-AUTH-004)`` () =
    let server = Server("acme", "data")
    let first = run server (Identity.resolve (config server)) |> fst |> ok
    server.RepositoryId <- 777L
    let second = run server (Identity.resolve (config server)) |> fst |> ok

    Assert.Equal(Ok(), CapabilitySnapshot.checkRepository (Some first.RepositoryId) first)
    Assert.Equal(Error(WriteRefusal.RepositoryIdentityChanged("9001", "777")), CapabilitySnapshot.checkRepository (Some first.RepositoryId) second)

[<Fact>]
let ``the snapshot is non-secret: it holds no token (ARCA-AUTH-003)`` () =
    let server = Server("acme", "data")
    let snapshot = run server (Identity.resolve (config server)) |> fst |> ok
    Assert.DoesNotContain(server.Token, sprintf "%A" snapshot)

[<Fact>]
let ``conversations run through any host as async computations`` () =
    let server = Server("acme", "data")

    let host =
        { Send = fun request -> async { return server.Send request }
          Wait = fun _ -> async { return () }
          Tokens = fun () -> async { return server.ValidToken() } }

    let snapshot = Identity.resolve (config server) |> Conversation.run host |> Async.StartAsTask
    Assert.True(Result.isOk snapshot.Result)
