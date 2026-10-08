/// Optimistic concurrency, three-way merge and the commit format
/// (ARCA-CON-001..004, ARCA-COMMIT-001..005, ARCA-AUTH-002).
module Arca.Tests.ConcurrencyTests

open Arca
open Xunit
open FsCheck.Xunit
open FsCheck.FSharp
open FsCheck

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private path text = RelativePath.parse text |> ok

let private chronaSpace =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Production; Name = "production" }
          Location = DataLocation.create "acme" "data" "main" "" |> ok }
    |> ok

let private metadata =
    { Summary = "start timer for A-1"
      Actor = { Kind = ActorKind.Agent; Id = ActorId.create "anthropic/claude-code" |> ok }
      ProviderIdentity = Some "octocat"
      ExecutionId = Some "EXE-1"
      CorrelationId = CorrelationId.create "req-7" |> ok
      IdempotencyKey = IdempotencyKey.create "op-01J9Z8XK" |> ok }

let private create text content = Change.Create(path text, content)

[<Fact>]
let ``every change carries an expectation; there is no unconditional write (ARCA-CON-001)`` () =
    Assert.Equal(None, Change.expected (create "records/a.json" "{}"))
    Assert.Equal(Some(Revision "r1"), Change.expected (Change.Update(path "records/a.json", "{}", Revision "r1")))
    Assert.Equal(Some(Revision "r1"), Change.expected (Change.Delete(path "records/a.json", Revision "r1")))

[<Fact>]
let ``stale expectations become typed conflicts naming the record and its new revision (ARCA-CON-002)`` () =
    let current =
        function
        | p when RelativePath.render p = "records/a.json" -> Some(Revision "r2")
        | p when RelativePath.render p = "records/b.json" -> Some(Revision "b1")
        | _ -> None

    let changes =
        [ Change.Update(path "records/a.json", "{}", Revision "r1")
          create "records/b.json" "{}"
          Change.Delete(path "records/c.json", Revision "c1")
          create "records/d.json" "{}" ]

    let conflicts = Concurrency.conflicts current changes

    Assert.Equal<Conflict list>(
        [ { Path = path "records/a.json"; Expected = Some(Revision "r1"); Actual = Some(Revision "r2") }
          { Path = path "records/b.json"; Expected = None; Actual = Some(Revision "b1") }
          { Path = path "records/c.json"; Expected = Some(Revision "c1"); Actual = None } ],
        conflicts
    )

[<Fact>]
let ``an operation groups several changes into one atomic unit (ARCA-COMMIT-001)`` () =
    let operation =
        Operation.create chronaSpace metadata [ create "records/invoice.json" "{}"; create "records/journal.json" "{}"; create "records/audit.json" "{}" ]
        |> ok

    Assert.Equal(3, operation.Changes.Length)

[<Fact>]
let ``operations refuse emptiness, duplicate paths, root writes and bad summaries`` () =
    Assert.Equal(Error OperationError.NoChanges, Operation.create chronaSpace metadata [] |> Result.map ignore)

    Assert.Equal(
        Error(OperationError.DuplicatePath "records/a.json"),
        Operation.create chronaSpace metadata [ create "records/a.json" "{}"; Change.Delete(path "records/a.json", Revision "r") ]
        |> Result.map ignore
    )

    Assert.True(Result.isError (Operation.create chronaSpace metadata [ Change.Create(RelativePath.empty, "{}") ]))

    for summary in [ ""; " leading"; "two\nlines"; String.replicate 121 "x" ] do
        Assert.Equal(
            Error(OperationError.InvalidSummary summary),
            Operation.create chronaSpace { metadata with Summary = summary } [ create "records/a.json" "{}" ] |> Result.map ignore
        )

[<Fact>]
let ``credentials are refused in summaries, metadata and content (ARCA-AUTH-002, ARCA-COMMIT-004)`` () =
    let token = "ghp_" + String.replicate 36 "a"

    Assert.Equal(
        Error(OperationError.CredentialInContent "summary"),
        Operation.create chronaSpace { metadata with Summary = $"rotate {token}" } [ create "records/a.json" "{}" ] |> Result.map ignore
    )

    Assert.Equal(
        Error(OperationError.CredentialInContent "records/a.json"),
        Operation.create chronaSpace metadata [ create "records/a.json" $"{{\"note\":\"{token}\"}}" ] |> Result.map ignore
    )

    Assert.Equal(
        Error(OperationError.CredentialInContent "providerIdentity"),
        Operation.create chronaSpace { metadata with ProviderIdentity = Some("github_pat_" + String.replicate 30 "b") } [ create "records/a.json" "{}" ]
        |> Result.map ignore
    )

[<Theory>]
[<InlineData("ghs_0123456789abcdefghijABCDEFGHIJ")>]
[<InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz")>]
[<InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")>]
[<InlineData("-----BEGIN RSA PRIVATE KEY-----")>]
let ``credential shapes are recognized`` (text: string) = Assert.True(Secrets.looksLikeCredential text)

[<Theory>]
[<InlineData("start timer for A-1")>]
[<InlineData("sha256:015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862")>]
[<InlineData("token count 42")>]
let ``ordinary text is not mistaken for a credential`` (text: string) = Assert.False(Secrets.looksLikeCredential text)

[<Fact>]
let ``the commit message is deterministic, namespace-prefixed and carries the trailers (ARCA-COMMIT-002)`` () =
    let operation = Operation.create chronaSpace metadata [ create "records/a.json" "{}" ] |> ok

    let expected =
        "chrona: start timer for A-1\n\nArca-Format: 1\nArca-Namespace: chrona\nArca-Actor-Kind: agent\nArca-Actor: anthropic/claude-code\nArca-Provider-Identity: octocat\nArca-Execution: EXE-1\nArca-Correlation: req-7\nArca-Idempotency-Key: op-01J9Z8XK\n"

    Assert.Equal(expected, Commit.message operation)

[<Fact>]
let ``trailers round-trip and keep an agent an agent (ARCA-COMMIT-003)`` () =
    let operation = Operation.create chronaSpace metadata [ create "records/a.json" "{}" ] |> ok
    let parsed = Commit.trailers (Commit.message operation) |> Option.get
    Assert.Equal(ActorKind.Agent, parsed.Actor.Kind)
    Assert.Equal("anthropic/claude-code", ActorId.value parsed.Actor.Id)
    Assert.Equal(metadata.IdempotencyKey, parsed.IdempotencyKey)
    Assert.Equal(Some "octocat", parsed.ProviderIdentity)
    Assert.True(Commit.carriesKey metadata.IdempotencyKey (Commit.message operation))

[<Fact>]
let ``a commit without Arca trailers, or with duplicated ones, is not an Arca commit`` () =
    Assert.Equal(None, Commit.trailers "fix typo by hand\n")

    let operation = Operation.create chronaSpace metadata [ create "records/a.json" "{}" ] |> ok
    let doubled = Commit.message operation + "Arca-Actor-Kind: human\n"
    Assert.Equal(None, Commit.trailers doubled)

let private entry hash = { Hash = hash; Mutability = Mutability.Mutable }
let private frozen hash = { Hash = hash; Mutability = Mutability.Immutable }

[<Fact>]
let ``independent changes merge; same-record edits conflict and keep the current state (ARCA-CON-003)`` () =
    let ancestor = Map.ofList [ "a", entry "a0"; "b", entry "b0"; "c", entry "c0" ]
    let ours = Map.ofList [ "a", entry "a1"; "b", entry "b0"; "c", entry "c1"; "x", entry "x1" ]
    let theirs = Map.ofList [ "a", entry "a0"; "b", entry "b2"; "c", entry "c2"; "y", entry "y2" ]

    let result = Merge.threeWay ancestor ours theirs

    Assert.Equal<RecordSet>(Map.ofList [ "a", entry "a1"; "b", entry "b2"; "c", entry "c2"; "x", entry "x1"; "y", entry "y2" ], result.Merged)
    Assert.Equal<MergeConflict list>([ MergeConflict.Divergent("c", Some(entry "c0"), Some(entry "c1"), Some(entry "c2")) ], result.Conflicts)

[<Fact>]
let ``edit against delete is a conflict, not a silent resurrection or loss`` () =
    let ancestor = Map.ofList [ "a", entry "a0" ]
    let result = Merge.threeWay ancestor (Map.ofList [ "a", entry "a1" ]) Map.empty
    Assert.Equal<MergeConflict list>([ MergeConflict.Divergent("a", Some(entry "a0"), Some(entry "a1"), None) ], result.Conflicts)
    Assert.True(result.Merged.IsEmpty)

[<Fact>]
let ``changing an immutable record is reported even when both sides agree`` () =
    let ancestor = Map.ofList [ "e", frozen "e0" ]
    let changed = Map.ofList [ "e", frozen "e1" ]
    let result = Merge.threeWay ancestor changed changed
    Assert.Equal<MergeConflict list>([ MergeConflict.ImmutableChanged("e", frozen "e0", Some(frozen "e1"), Some(frozen "e1")) ], result.Conflicts)

// Property-based tests (ARCA-CON-004).

let private recordSet =
    let key = Gen.elements [ "a"; "b"; "c"; "d"; "e"; "f" ]
    let value = Gen.zip (Gen.elements [ "h1"; "h2"; "h3" ]) (Gen.frequency [ 4, Gen.constant Mutability.Mutable; 1, Gen.constant Mutability.Immutable ])

    Gen.listOf (Gen.zip key value)
    |> Gen.map (List.map (fun (k, (h, m)) -> k, { Hash = h; Mutability = m }) >> Map.ofList)

let private triple =
    Gen.zip3 recordSet recordSet recordSet |> Arb.fromGen

let private paths (result: MergeResult) =
    result.Conflicts |> List.map Merge.conflictPath |> Set.ofList

[<Property>]
let ``merge is deterministic`` () =
    Prop.forAll triple (fun (a, o, t) -> Merge.threeWay a o t = Merge.threeWay a o t)

[<Property>]
let ``merge is symmetric: swapping sides changes neither the conflicting paths nor the clean results`` () =
    Prop.forAll triple (fun (a, o, t) ->
        let forward = Merge.threeWay a o t
        let backward = Merge.threeWay a t o
        let conflicted = paths forward

        conflicted = paths backward
        && (forward.Merged |> Map.filter (fun key _ -> not (conflicted.Contains key))) = (backward.Merged
                                                                                          |> Map.filter (fun key _ -> not (conflicted.Contains key))))

[<Property>]
let ``no lost updates: every change on either side is in the result or reported`` () =
    Prop.forAll triple (fun (a, o, t) ->
        let result = Merge.threeWay a o t
        let conflicted = paths result

        let kept side path =
            conflicted.Contains path || Map.tryFind path result.Merged = Map.tryFind path side

        let changed (side: RecordSet) =
            Set.union (side |> Map.keys |> Set.ofSeq) (a |> Map.keys |> Set.ofSeq)
            |> Set.filter (fun path -> Map.tryFind path side <> Map.tryFind path a)

        changed o |> Set.forall (kept o) && changed t |> Set.forall (kept t))

[<Property>]
let ``concurrent additions of distinct records commute`` () =
    let additions =
        Gen.zip recordSet recordSet
        |> Gen.map (fun (left, right) ->
            let left = left |> Map.map (fun _ entry -> entry)
            let right = right |> Map.toList |> List.map (fun (key, entry) -> key + "-r", entry) |> Map.ofList
            left, right)
        |> Arb.fromGen

    Prop.forAll additions (fun (ours, theirs) ->
        let forward = Merge.threeWay Map.empty ours theirs
        let backward = Merge.threeWay Map.empty theirs ours

        forward.Conflicts.IsEmpty
        && forward.Merged = backward.Merged
        && forward.Merged.Count = ours.Count + theirs.Count)

[<Property>]
let ``a one-sided change is taken as is`` () =
    Prop.forAll (Gen.zip recordSet recordSet |> Arb.fromGen) (fun (a, t) ->
        let mutableAncestor = a |> Map.map (fun _ entry -> { entry with Mutability = Mutability.Mutable })
        let result = Merge.threeWay mutableAncestor mutableAncestor t
        result.Conflicts.IsEmpty && result.Merged = t)
