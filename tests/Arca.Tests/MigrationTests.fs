/// Snapshots, rebuildable derived indexes, canonical export and the migration
/// workflow (ARCA-MIG-001..003, ARCA-LOC-009).
module Arca.Tests.MigrationTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Arca
open Arca.GitHub
open Arca.Tests.FakeGitHub

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private run computation = Async.RunSynchronously computation
let private oldHome = DataLocation.create "acme" "data" "main" "apps" |> ok
let private newHome = DataLocation.create "acme" "chrona-data" "main" "" |> ok

let private binding application location =
    { Application = AppId.create application |> ok
      Environment = { Kind = EnvironmentKind.Test; Name = "migration" }
      Location = location }

let private chronaAt location = Namespace.ofApplication (binding "chrona" location) |> ok
let private source = chronaAt oldHome
let private target = chronaAt newHome
let private path text = RelativePath.parse text |> ok
let private activity = RecordType.create "chrona.activity" |> ok
let private timer = RecordType.create "chrona.timer" |> ok
let private manifestPath = Layout.manifestPath |> ok
let private actor = { Kind = ActorKind.Agent; Id = ActorId.create "migrator" |> ok }

let private record recordType id minutes =
    { Id = RecordId.create id |> ok
      Type = recordType
      SchemaVersion = 1
      Mutability = Mutability.Mutable
      Body = Json.objectOf [ "minutes", Json.Number minutes ] }

let private recordPath (record: Record) =
    Layout.recordPath { Type = record.Type; Partition = []; Id = record.Id } |> ok

let private encoded record = Record.encode Record.DefaultMaxBytes record |> ok

let private manifestAt location =
    { Scope = ManifestScope.Application
      Application = AppId.create "chrona" |> ok
      StorageSchema = Manifest.StorageSchema
      ProviderContract = StorageContract.Version
      RecordSchemas = Map.ofList [ "chrona.activity", 1; "chrona.timer", 1 ]
      CreatedBy = actor
      CreatedAt = DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero)
      Location = location
      Migration = None }

let mutable private sequence = 0

let private metadata summary =
    sequence <- sequence + 1

    { Summary = summary
      Actor = actor
      ProviderIdentity = None
      ExecutionId = None
      CorrelationId = CorrelationId.create "test" |> ok
      IdempotencyKey = IdempotencyKey.create $"test-key-{sequence:D6}" |> ok }

let private write (provider: StorageProvider) ns summary changes =
    provider.Commit(Operation.create ns (metadata summary) changes |> ok) |> run |> ok |> ignore

let private records = [ record activity "A-1" 30m; record activity "A-2" 45m; record timer "T-1" 5m ]

/// A source namespace with a manifest, three records, a derived object and a
/// dataset of its own (which belongs to another namespace).
let private seeded () =
    let store = InMemoryStore()
    let provider = store.Provider

    write
        provider
        source
        "seed"
        ([ Change.Create(manifestPath, Manifest.encode (manifestAt oldHome))
           Change.Create(path "derived/cache.json", "{}")
           Change.Create(path "notes/readme.txt", "not a record") ]
         @ (records |> List.map (fun r -> Change.Create(recordPath r, encoded r))))

    store.WriteExternally(oldHome, "apps/chrona/datasets/org-1/records/chrona.activity/D-1.json", Some "{}")
    store

let private paths (snapshot: Snapshot) = snapshot.Objects |> List.map (_.Path >> RelativePath.render)

// ---------------------------------------------------------------------------
// Snapshots
// ---------------------------------------------------------------------------

[<Fact>]
let ``a snapshot holds every object of the namespace and nothing of its datasets`` () =
    let store = seeded ()
    let snapshot = Snapshot.take store.Provider source 3 |> run |> ok

    Assert.Equal<string list>(
        [ "arca-manifest.json"
          "derived/cache.json"
          "notes/readme.txt"
          "records/chrona.activity/A-1.json"
          "records/chrona.activity/A-2.json"
          "records/chrona.timer/T-1.json" ],
        paths snapshot
    )

    Assert.Equal(store.Provider.ChangeToken source |> run |> ok, snapshot.ChangeToken)

[<Fact>]
let ``a snapshot is never built on a partial listing`` () =
    let store = seeded ()
    store.Arrange(InMemoryFault.ListingLimit 1)

    match Snapshot.take store.Provider source 3 |> run with
    | Error(SnapshotError.Incomplete _) -> ()
    | other -> failwith $"expected Incomplete, got {other}"

[<Fact>]
let ``a namespace that keeps changing is reported Unstable`` () =
    let store = seeded ()
    let counter = ref 0

    let busy =
        { store.Provider with
            List =
                fun ns prefix ->
                    counter.Value <- counter.Value + 1
                    store.WriteExternally(oldHome, $"apps/chrona/notes/n{counter.Value}.txt", Some "x")
                    store.Provider.List ns prefix }

    Assert.Equal(Error(SnapshotError.Unstable 3), Snapshot.take busy source 3 |> run)

// ---------------------------------------------------------------------------
// Derived indexes (ARCA-MIG-001)
// ---------------------------------------------------------------------------

let private minutesIndex version =
    { Name = Segment.create "minutes-by-id" |> ok
      Version = version
      Sources = [ { Type = activity; OldestReadable = 1; Current = 1 } ]
      Project =
        fun record ->
            match Json.field "minutes" record.Body with
            | Some minutes -> [ RecordId.value record.Id, minutes ]
            | None -> [] }

let private validated (record: Record) =
    recordPath record,
    { Record = record
      Revision = Revision "r"
      ContentHash = Record.contentHash record }

[<Fact>]
let ``an index is stored under derived/, never where a record lives (ARCA-MIG-001, ARCA-REC-006)`` () =
    let indexPath = Derived.path (minutesIndex 1) |> ok
    Assert.Equal("derived/indexes/minutes-by-id.json", RelativePath.render indexPath)
    Assert.Equal(Some Authority.Derived, Layout.authorityOf indexPath)

[<Property>]
let ``an index depends on the records, not on the order they are read in`` () =
    let gen =
        gen {
            let! minutes = Gen.listOfLength 5 (Gen.choose (0, 500))
            let all = minutes |> List.mapi (fun i m -> record activity $"A-{i}" (decimal m))
            let! shuffled = Gen.shuffle all
            return all, List.ofArray shuffled
        }

    Prop.forAll (Arb.fromGen gen) (fun (ordered, shuffled) ->
        let first = Derived.build (minutesIndex 1) (ordered |> List.map validated)
        let second = Derived.build (minutesIndex 1) (shuffled |> List.map validated)
        first = second && Derived.decode (Derived.encode first) = Ok first)

[<Fact>]
let ``an index records its source set, is rebuilt when records change, and rebuilding is idempotent (ARCA-MIG-001)`` () =
    let store = seeded ()
    let provider = store.Provider
    let definition = minutesIndex 1
    let index, receipt = Derived.rebuild provider source (metadata "rebuild index") definition |> run |> ok
    Assert.True(receipt.IsSome)
    Assert.Equal(2, index.Source.Count)
    Assert.Equal<(string * Json) list>([ "A-1", Json.Number 30m; "A-2", Json.Number 45m ], index.Entries)

    let _, again = Derived.rebuild provider source (metadata "rebuild index") definition |> run |> ok
    Assert.True(again.IsNone, "a current index is not written again")

    let current snapshot =
        Derived.sources definition snapshot |> ok |> Derived.sourceSet

    let stored () =
        Derived.read provider source definition |> run |> ok |> Option.map fst

    let snapshot = Snapshot.take provider source 3 |> run |> ok
    Assert.Equal(IndexStatus.Current, Derived.check definition (current snapshot) (stored ()))

    // A record changes: the stored index is stale until rebuilt.
    let changed = record activity "A-2" 50m
    let revision = (Snapshot.find (recordPath changed) snapshot |> Option.get).Revision
    write provider source "edit" [ Change.Update(recordPath changed, encoded changed, revision) ]
    let snapshot = Snapshot.take provider source 3 |> run |> ok

    match Derived.check definition (current snapshot) (stored ()) with
    | IndexStatus.Stale(built, now) -> Assert.NotEqual(built, now)
    | other -> failwith $"expected Stale, got {other}"

    let rebuilt, _ = Derived.rebuild provider source (metadata "rebuild index") definition |> run |> ok
    let difference = Derived.compare index rebuilt
    Assert.Equal<(string * Json) list>([ "A-2", Json.Number 45m ], difference.Removed)
    Assert.Equal<(string * Json) list>([ "A-2", Json.Number 50m ], difference.Added)
    Assert.Equal(IndexStatus.Current, Derived.check definition (current (Snapshot.take provider source 3 |> run |> ok)) (stored ()))
    Assert.Equal(IndexStatus.OtherVersion(1, 2), Derived.check (minutesIndex 2) rebuilt.Source (Some rebuilt))
    Assert.Equal(IndexStatus.Missing, Derived.check definition rebuilt.Source None)

[<Fact>]
let ``an index is never built on a record that does not validate (ARCA-INT-001)`` () =
    let store = seeded ()
    store.WriteExternally(oldHome, "apps/chrona/records/chrona.activity/A-9.json", Some "{\"hand\":\"edited\"}")

    match Derived.rebuild store.Provider source (metadata "rebuild index") (minutesIndex 1) |> run with
    | Error(DerivedError.InvalidSource("records/chrona.activity/A-9.json", _)) -> ()
    | other -> failwith $"expected InvalidSource, got {other}"

[<Fact>]
let ``a stored index that is not canonical is corrupt`` () =
    match Derived.decode "{ \"arcaIndex\": 1 }" with
    | Error(DerivedError.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

// ---------------------------------------------------------------------------
// Export (ARCA-MIG-003)
// ---------------------------------------------------------------------------

[<Fact>]
let ``everything a namespace stores exports in canonical form and reads back verified (ARCA-MIG-003)`` () =
    let store = seeded ()
    store.WriteExternally(oldHome, "apps/chrona/records/chrona.activity/A-9.json", Some "corrupt, exported as stored")
    let archive = Export.take store.Provider source 3 |> run |> ok
    let text = Export.encode archive

    Assert.True(Json.isCanonical text)
    Assert.Equal(Ok archive, Export.decode text)
    Assert.Equal(7, archive.Objects.Length)
    Assert.Contains({ Path = "records/chrona.activity/A-9.json"; Content = "corrupt, exported as stored" }, archive.Objects)
    Assert.Contains({ Path = "records/chrona.activity/A-1.json"; Content = encoded records[0] }, archive.Objects)
    Assert.DoesNotContain(archive.Objects, fun item -> item.Path.StartsWith "datasets/")
    Assert.Equal(("acme", "data", "main", "apps"), (archive.Owner, archive.Repository, archive.Branch, archive.BasePath))

[<Fact>]
let ``a tampered export is refused`` () =
    let store = seeded ()
    let text = Export.take store.Provider source 3 |> run |> ok |> Export.encode
    let tampered = text.Replace("not a record", "not a recorD")
    Assert.Equal(Error(ExportError.HashMismatch "notes/readme.txt"), Export.decode tampered)

// ---------------------------------------------------------------------------
// Migration (ARCA-MIG-002, ARCA-LOC-009)
// ---------------------------------------------------------------------------

let private plan transform schemas =
    { Id = MigrationId.create "M-2026-10-relocate" |> ok
      Source = source
      Target = target
      RecordSchemas = schemas
      Transform = transform
      Actor = actor
      BatchSize = 2 }

let private relocation = plan Ok Map.empty

let private manifestOf (provider: StorageProvider) ns =
    match provider.Read ns manifestPath |> run |> ok with
    | ReadOutcome.Found stored -> Manifest.decode stored.Content |> ok |> Some
    | ReadOutcome.Absent -> None

let private contents (provider: StorageProvider) ns =
    Snapshot.take provider ns 3 |> run |> ok |> _.Objects |> List.map (fun item -> RelativePath.render item.Path, item.Content)

[<Fact>]
let ``a relocation copies, verifies and activates the target, leaving the source untouched (ARCA-MIG-002, ARCA-LOC-009)`` () =
    let store = seeded ()
    let provider = store.Provider
    let before = contents provider source

    // Pointing the configuration at the new location is not enough.
    Assert.Equal(None, manifestOf provider target)

    let report = Migration.run provider provider relocation |> run |> ok
    Assert.Equal(4, report.Written)
    Assert.Equal(2, report.Commits)

    Assert.Equal<(string * string) list>(before, contents provider source)
    Assert.Empty(Manifest.check source (manifestOf provider source |> Option.get))

    let activated = manifestOf provider target |> Option.get
    Assert.Empty(Manifest.check target activated)
    Assert.Equal(newHome, activated.Location)
    Assert.Equal(Some { MigrationId = "M-2026-10-relocate"; Phase = MigrationPhase.Completed }, activated.Migration)

    // Records and other objects are copied; derived data is rebuilt at the target instead.
    Assert.Equal<string list>(
        [ "arca-manifest.json"
          "notes/readme.txt"
          "records/chrona.activity/A-1.json"
          "records/chrona.activity/A-2.json"
          "records/chrona.timer/T-1.json" ],
        contents provider target |> List.map fst
    )

    Assert.Equal<(string * string) list>(
        before |> List.filter (fun (p, _) -> p.StartsWith "records/" || p.StartsWith "notes/"),
        contents provider target |> List.filter (fun (p, _) -> p <> "arca-manifest.json")
    )

[<Fact>]
let ``running a finished migration again changes nothing (idempotent)`` () =
    let store = seeded ()
    Migration.run store.Provider store.Provider relocation |> run |> ok |> ignore
    let history = store.State.History.Length
    let again = Migration.run store.Provider store.Provider relocation |> run |> ok
    Assert.Equal(0, again.Written)
    Assert.Equal(history, store.State.History.Length)

[<Fact>]
let ``an interrupted migration reports progress in the target manifest and resumes (ARCA-MIG-002)`` () =
    let store = seeded ()
    let commits = ref 0

    let flaky =
        { store.Provider with
            Commit =
                fun operation ->
                    commits.Value <- commits.Value + 1

                    if commits.Value = 3 then
                        async { return Error(StorageFailure.ProviderFailed("HTTP.502", true, "bad gateway")) }
                    else
                        store.Provider.Commit operation }

    match Migration.run store.Provider flaky relocation |> run with
    | Error(MigrationError.Provider(StorageFailure.ProviderFailed("HTTP.502", true, _))) -> ()
    | other -> failwith $"expected the injected failure, got {other}"

    let interrupted = manifestOf store.Provider target |> Option.get

    match Manifest.check target interrupted with
    | [ ManifestProblem.MigrationInProgress { Phase = MigrationPhase.Copying } ] -> ()
    | other -> failwith $"applications must refuse a half-copied target, got {other}"

    let report = Migration.run store.Provider flaky relocation |> run |> ok
    Assert.True(report.Written >= 1)
    Assert.Empty(Manifest.check target (manifestOf store.Provider target |> Option.get))
    Assert.Equal(5, (contents store.Provider target).Length)

[<Fact>]
let ``a schema migration transforms records and records the new versions`` () =
    let store = seeded ()

    let upgrade (record: Record) =
        if record.Type = activity then
            Ok
                { record with
                    SchemaVersion = 2
                    Body = Json.objectOf [ "minutes", Json.field "minutes" record.Body |> Option.defaultValue Json.Null; "billable", Json.Bool true ] }
        else
            Ok record

    let schema = plan upgrade (Map.ofList [ "chrona.activity", 2 ])
    Migration.run store.Provider store.Provider schema |> run |> ok |> ignore

    let migrated =
        match store.Provider.Read target (path "records/chrona.activity/A-1.json") |> run |> ok with
        | ReadOutcome.Found stored -> Record.decode Record.DefaultMaxBytes stored.Content |> ok
        | ReadOutcome.Absent -> failwith "copied"

    Assert.Equal(2, migrated.SchemaVersion)
    Assert.Equal(Some(Json.Bool true), Json.field "billable" migrated.Body)
    let manifest = manifestOf store.Provider target |> Option.get
    Assert.Equal<Map<string, int>>(Map.ofList [ "chrona.activity", 2; "chrona.timer", 1 ], manifest.RecordSchemas)

    // The source still holds version 1.
    match store.Provider.Read source (path "records/chrona.activity/A-1.json") |> run |> ok with
    | ReadOutcome.Found stored -> Assert.Equal(1, (Record.decode Record.DefaultMaxBytes stored.Content |> ok).SchemaVersion)
    | ReadOutcome.Absent -> failwith "kept"

[<Fact>]
let ``a transform may not change a record's identity, and nothing is activated`` () =
    let store = seeded ()
    let rename (record: Record) = Ok { record with Id = RecordId.create "OTHER" |> ok }

    match Migration.run store.Provider store.Provider (plan rename Map.empty) |> run with
    | Error(MigrationError.TransformFailed(_, _)) -> ()
    | other -> failwith $"expected TransformFailed, got {other}"

    match manifestOf store.Provider target with
    | Some manifest -> Assert.NotEmpty(Manifest.check target manifest)
    | None -> ()

[<Fact>]
let ``a target that holds other data is refused`` () =
    let store = seeded ()
    store.WriteExternally(newHome, "chrona/notes/other.txt", Some "someone else's")

    match Migration.run store.Provider store.Provider relocation |> run with
    | Error(MigrationError.TargetInUse _) -> ()
    | other -> failwith $"expected TargetInUse, got {other}"

[<Fact>]
let ``invalid plans are refused before anything is read`` () =
    let sameLocation = { relocation with Target = source }
    let otherApp = { relocation with Target = Namespace.ofApplication (binding "summa" newHome) |> ok }
    let hugeBatch = { relocation with BatchSize = 501 }

    for invalid in [ sameLocation; otherApp; hugeBatch ] do
        match Migration.validate invalid with
        | Error(MigrationError.InvalidPlan _) -> ()
        | other -> failwith $"expected InvalidPlan, got {other}"

    Assert.True(Result.isError (MigrationId.create ""))
    Assert.True(Result.isError (MigrationId.create (String('m', 65))))

[<Fact>]
let ``a source without a manifest, or one being migrated, is not migrated`` () =
    let empty = InMemoryStore()
    Assert.Equal(Error MigrationError.SourceManifestMissing, Migration.run empty.Provider empty.Provider relocation |> run |> Result.map ignore)

    let busy = InMemoryStore()
    let migrating = { manifestAt oldHome with Migration = Some { MigrationId = "M-other"; Phase = MigrationPhase.Copying } }
    write busy.Provider source "seed" [ Change.Create(manifestPath, Manifest.encode migrating) ]

    match Migration.run busy.Provider busy.Provider relocation |> run with
    | Error(MigrationError.SourceNotReady [ ManifestProblem.MigrationInProgress _ ]) -> ()
    | other -> failwith $"expected SourceNotReady, got {other}"

[<Fact>]
let ``the source is retired explicitly, only after activation, and only while unchanged (ARCA-MIG-002)`` () =
    let store = seeded ()
    let provider = store.Provider
    Assert.Equal(Error MigrationError.NotActivated, Migration.retire provider provider relocation |> run)

    Migration.run provider provider relocation |> run |> ok |> ignore

    // A write that landed on the source after activation blocks retirement.
    let late = record activity "A-3" 10m
    write provider source "late write" [ Change.Create(recordPath late, encoded late) ]
    Assert.Equal(Error(MigrationError.SourceChanged [ "records/chrona.activity/A-3.json" ]), Migration.retire provider provider relocation |> run)

    // Once reconciled at the target, the source can be retired.
    write provider target "reconcile" [ Change.Create(recordPath late, encoded late) ]
    Assert.Equal(Ok(), Migration.retire provider provider relocation |> run)
    Assert.Equal(Ok(), Migration.retire provider provider relocation |> run)

    Assert.Equal<ManifestProblem list>([ ManifestProblem.Retired "M-2026-10-relocate" ], Manifest.check source (manifestOf provider source |> Option.get))
    Assert.Empty(Manifest.check target (manifestOf provider target |> Option.get))

[<Fact>]
let ``a source that keeps changing during verification is reported, never activated`` () =
    let store = seeded ()
    let counter = ref 0

    let target' =
        { store.Provider with
            Commit =
                fun operation ->
                    async {
                        let! result = store.Provider.Commit operation

                        if operation.Metadata.Summary.EndsWith ": verify" then
                            counter.Value <- counter.Value + 1
                            store.WriteExternally(oldHome, $"apps/chrona/notes/late-{counter.Value}.txt", Some "late")

                        return result
                    } }

    match Migration.run store.Provider target' relocation |> run with
    | Error(MigrationError.Unverified paths) -> Assert.NotEmpty paths
    | other -> failwith $"expected Unverified, got {other}"

    Assert.Equal(Migration.MaxRounds, counter.Value)
    Assert.NotEmpty(Manifest.check target (manifestOf store.Provider target |> Option.get))

[<Fact>]
let ``a relocation between repositories runs through the GitHub adapter`` () =
    let server = Server("acme", "data")
    let other = Server("acme", "chrona-data")

    let host (server: Server) =
        { Send = fun request -> async { return server.Send request }
          Wait = fun _ -> async { return () }
          Tokens = fun () -> async { return server.ValidToken() } }

    let from = GitHubStorage.provider (host server) (GitHubConfig.create oldHome)
    let into = GitHubStorage.provider (host other) (GitHubConfig.create newHome)

    write
        from
        source
        "seed"
        ([ Change.Create(manifestPath, Manifest.encode (manifestAt oldHome)) ]
         @ (records |> List.map (fun r -> Change.Create(recordPath r, encoded r))))

    let report = Migration.run from into relocation |> run |> ok
    Assert.Equal(3, report.Written)
    Assert.Empty(Manifest.check target (manifestOf into target |> Option.get))
    Assert.Equal(Ok(), Migration.retire from into relocation |> run)
    Assert.Equal<ManifestProblem list>([ ManifestProblem.Retired "M-2026-10-relocate" ], Manifest.check source (manifestOf from source |> Option.get))
