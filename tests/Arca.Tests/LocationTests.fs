/// Data location, application namespaces and path safety (ARCA-LOC-001..010).
module Arca.Tests.LocationTests

open Arca
open Xunit
open FsCheck.Xunit
open FsCheck.FSharp
open FsCheck

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private location owner repository basePath =
    DataLocation.create owner repository "main" basePath |> ok

let private binding application location =
    { Application = AppId.create application |> ok
      Environment = { Kind = EnvironmentKind.Production; Name = "production" }
      Location = location }

let private chrona = binding "chrona" (location "acme" "data" "apps")
let private chronaSpace = Namespace.ofApplication chrona |> ok

[<Fact>]
let ``a location is built entirely from configuration, nothing hard-coded (ARCA-LOC-001)`` () =
    let configured = DataLocation.create "Acme-Co" "time.data" "release/2026" "deployments/eu" |> ok
    Assert.Equal("Acme-Co", configured.Repository.Owner)
    Assert.Equal("time.data", configured.Repository.Name)
    Assert.Equal("release/2026", BranchName.value configured.Branch)
    Assert.Equal("deployments/eu", RelativePath.render configured.BasePath)

[<Theory>]
[<InlineData("-acme")>]
[<InlineData("acme-")>]
[<InlineData("ac--me")>]
[<InlineData("ac_me")>]
[<InlineData("")>]
let ``invalid owners are refused`` (owner: string) =
    Assert.Equal(Error(LocationError.InvalidOwner owner), RepositoryRef.create owner "data" |> Result.map string)

[<Theory>]
[<InlineData("..")>]
[<InlineData("main..dev")>]
[<InlineData("/main")>]
[<InlineData("main/")>]
[<InlineData("feature/.hidden")>]
[<InlineData("main.lock")>]
[<InlineData("has space")>]
[<InlineData("a:b")>]
[<InlineData("@")>]
let ``invalid branches are refused`` (branch: string) =
    Assert.Equal(Error(LocationError.InvalidBranch branch), BranchName.create branch |> Result.map BranchName.value)

[<Fact>]
let ``repository identity is case-insensitive, as on GitHub`` () =
    Assert.Equal(RepositoryRef.create "Acme" "Data" |> ok, RepositoryRef.create "acme" "data" |> ok)

[<Fact>]
let ``each application owns the folder named after it under the base path (ARCA-LOC-002)`` () =
    Assert.Equal("apps/chrona", RelativePath.render chronaSpace.Root)

    let address = Namespace.resolveText chronaSpace "records/2026/10/a.json" |> ok
    Assert.Equal("apps/chrona/records/2026/10/a.json", address.Path)
    Assert.Equal("acme/data", string address.Repository)

[<Theory>]
[<InlineData("../summa/ledger.json")>]
[<InlineData("records/../../summa/x.json")>]
[<InlineData("/etc/passwd")>]
[<InlineData("records//x.json")>]
[<InlineData("records/./x.json")>]
[<InlineData("records\\..\\x.json")>]
[<InlineData(".github/workflows/x.yml")>]
[<InlineData("records/.git/config")>]
let ``traversal, absolute, hidden and malformed paths never resolve (ARCA-LOC-003)`` (path: string) =
    Assert.True(Result.isError (Namespace.resolveText chronaSpace path))

[<Fact>]
let ``a write to the namespace root itself is refused (ARCA-LOC-003)`` () =
    Assert.Equal(Error(LocationError.NamespaceRootWrite "apps/chrona"), Namespace.resolveText chronaSpace "" |> Result.map _.Path)

[<Fact>]
let ``an application never claims another application's objects (ARCA-LOC-003)`` () =
    let summa = Namespace.ofApplication (binding "summa" chrona.Location) |> ok
    let summaObject = Namespace.resolveText summa "ledger/x.json" |> ok
    Assert.Equal(None, Namespace.relativeOf chronaSpace summaObject)

    let own = Namespace.resolveText chronaSpace "records/x.json" |> ok
    Assert.Equal(Some "records/x.json", Namespace.relativeOf chronaSpace own |> Option.map RelativePath.render)

[<Fact>]
let ``applications may share a repository, or each use their own for a permission boundary (ARCA-LOC-004)`` () =
    let shared = location "acme" "data" ""

    let sameRepository =
        Deployment.namespaces [ binding "chrona" shared; binding "summa" shared ] |> ok

    Assert.Equal<string list>([ "chrona"; "summa" ], sameRepository |> List.map (fun ns -> RelativePath.render ns.Root))

    let separate =
        Deployment.namespaces [ binding "chrona" (location "acme" "time-data" ""); binding "summa" (location "acme-finance" "ledger" "") ]
        |> ok

    Assert.Equal<string list>([ "acme/time-data"; "acme-finance/ledger" ], separate |> List.map (fun ns -> string ns.Location.Repository))

[<Fact>]
let ``overlapping namespaces on one branch and duplicate applications are refused`` () =
    let overlapping =
        Deployment.namespaces [ binding "chrona" (location "acme" "data" ""); binding "summa" (location "acme" "data" "chrona") ]

    Assert.Equal(Error [ LocationError.NamespaceOverlap("chrona", "chrona/summa") ], overlapping)

    let duplicate =
        Deployment.namespaces [ binding "chrona" (location "acme" "data" ""); binding "chrona" (location "acme" "other" "") ]

    Assert.Equal(Error [ LocationError.DuplicateApplication "chrona" ], duplicate)

[<Fact>]
let ``the same paths on different branches do not overlap`` () =
    let main = location "acme" "data" ""
    let other = { main with Branch = BranchName.create "staging" |> ok }
    Assert.True(Result.isOk (Deployment.namespaces [ binding "chrona" main; binding "summa" { other with BasePath = RelativePath.parse "summa-root" |> ok } ]))

[<Fact>]
let ``a dataset has an immutable id sub-namespace and may live in another repository (ARCA-LOC-005)`` () =
    let dataset = DatasetId.create "org_01J9Z8" |> ok
    let local = Namespace.ofDataset chrona dataset None |> ok
    Assert.Equal("apps/chrona/datasets/org_01J9Z8", RelativePath.render local.Root)

    let elsewhere = location "acme-eu" "eu-data" ""
    let remote = Namespace.ofDataset chrona dataset (Some elsewhere) |> ok
    Assert.Equal("chrona/datasets/org_01J9Z8", RelativePath.render remote.Root)
    Assert.Equal("acme-eu/eu-data", string remote.Location.Repository)

[<Theory>]
[<InlineData("Org Name")>]
[<InlineData("org/1")>]
[<InlineData("")>]
[<InlineData("-org")>]
let ``dataset ids are identifiers, not display names or paths`` (text: string) =
    Assert.True(Result.isError (DatasetId.create text))

[<Fact>]
let ``the environment identity is part of every binding (ARCA-LOC-007)`` () =
    let staging =
        { chrona with
            Environment = { Kind = EnvironmentKind.Staging; Name = "staging-eu" } }

    Assert.Equal(EnvironmentKind.Staging, staging.Environment.Kind)
    Assert.Equal("staging-eu", staging.Environment.Name)

[<Fact>]
let ``production data in a public repository needs an explicit, reasoned override (ARCA-LOC-008)`` () =
    Assert.Equal(Error VisibilityRefusal.PublicProductionRepository, Visibility.permitsInitialization EnvironmentKind.Production RepositoryVisibility.Public None)
    Assert.Equal(Error VisibilityRefusal.OverrideWithoutReason, Visibility.permitsInitialization EnvironmentKind.Production RepositoryVisibility.Public (Some { Reason = " " }))
    Assert.Equal(Ok(), Visibility.permitsInitialization EnvironmentKind.Production RepositoryVisibility.Public (Some { Reason = "open data set" }))
    Assert.Equal(Ok(), Visibility.permitsInitialization EnvironmentKind.Production RepositoryVisibility.Private None)
    Assert.Equal(Ok(), Visibility.permitsInitialization EnvironmentKind.Test RepositoryVisibility.Public None)

[<Fact>]
let ``changing an existing dataset's location requires a migration, not a config edit (ARCA-LOC-009)`` () =
    let recorded = location "acme" "data" "apps"
    let moved = location "acme" "data-2" "apps"
    Assert.Equal(Ok(), Relocation.check recorded (location "ACME" "Data" "apps"))
    Assert.Equal(Error { Recorded = recorded; Configured = moved }, Relocation.check recorded moved)

[<Fact>]
let ``at-rest encryption is not offered in the first release (ARCA-LOC-010)`` () =
    Assert.True(Result.isError (ProviderCapabilities.require Capability.AtRestEncryption Arca.GitHub.Provider.capabilities))

/// Arbitrary path text built from adversarial pieces.
let private pathText =
    Gen.elements [ ".."; "."; ""; "a"; "b.json"; ".git"; "x/../y"; "\\"; "/"; "ok-1"; "%2e%2e"; "~"; "a b" ]
    |> Gen.listOf
    |> Gen.map (String.concat "/")
    |> Arb.fromGen

[<Property>]
let ``no text ever resolves outside the namespace or onto its root`` () =
    Prop.forAll pathText (fun text ->
        match Namespace.resolveText chronaSpace text with
        | Error _ -> true
        | Ok address ->
            address.Path.StartsWith("apps/chrona/")
            && not (address.Path.Contains "..")
            && not (address.Path.Contains "//")
            && address.Path.Split('/') |> Array.forall (fun part -> part <> "" && not (part.StartsWith ".")))

[<Property>]
let ``resolution round-trips through relativeOf`` () =
    let segment = Gen.elements [ "a"; "b"; "records"; "2026"; "x.json"; "A_1-b" ]

    let relative =
        Gen.nonEmptyListOf segment
        |> Gen.filter (fun parts -> parts.Length <= 8)
        |> Gen.map (String.concat "/")
        |> Arb.fromGen

    Prop.forAll relative (fun text ->
        let address = Namespace.resolveText chronaSpace text |> ok
        Namespace.relativeOf chronaSpace address |> Option.map RelativePath.render = Some text)
