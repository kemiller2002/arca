/// Explicit erasure of immutable records for retention (ARCA-INT-005): the
/// request rules, the tombstone format, the capability gate, the offline
/// queue, and how snapshots, exports, indexes and migrations treat tombstones.
module Arca.Tests.ErasureTests

open System
open Xunit
open Arca

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private run computation = Async.RunSynchronously computation
let private home = DataLocation.create "acme" "data" "main" "apps" |> ok
let private elsewhere = DataLocation.create "acme" "signal-archive" "main" "" |> ok

let private signalAt location =
    Namespace.ofApplication
        { Application = AppId.create "signal" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "erasure" }
          Location = location }
    |> ok

let private signal = signalAt home
let private at = DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.Zero)
let private responseType = RecordType.create "signal.response" |> ok
let private personal = "respondent-email@example.org"

let private response id mutability =
    { Id = RecordId.create id |> ok
      Type = responseType
      SchemaVersion = 1
      Mutability = mutability
      Body = Json.objectOf [ "answer", Json.String personal ] }

let private pathOf (record: Record) =
    Layout.recordPath { Type = record.Type; Partition = []; Id = record.Id } |> ok

let mutable private sequence = 0

let private metadata () =
    sequence <- sequence + 1

    { Summary = "retention"
      Actor = { Kind = ActorKind.Service; Id = ActorId.create "signal/retention" |> ok }
      ProviderIdentity = None
      ExecutionId = None
      CorrelationId = CorrelationId.create "retention" |> ok
      IdempotencyKey = IdempotencyKey.create $"erasure-{sequence:D6}" |> ok }

let private manifest =
    { Scope = ManifestScope.Application
      Application = AppId.create "signal" |> ok
      StorageSchema = Manifest.StorageSchema
      ProviderContract = StorageContract.Version
      RecordSchemas = Map.ofList [ "signal.response", 1 ]
      CreatedBy = { Kind = ActorKind.Service; Id = ActorId.create "signal/setup" |> ok }
      CreatedAt = at
      Location = home
      Migration = None }

/// A store holding one immutable and one mutable response, and the immutable
/// one validated as Signal would read it.
let private seeded () =
    let store = InMemoryStore()
    let kept = response "R-1" Mutability.Immutable
    let mutableOne = response "R-2" Mutability.Mutable

    store.Provider.Commit(
        Operation.create
            signal
            (metadata ())
            [ Change.Create(Layout.manifestPath |> ok, Manifest.encode manifest)
              Change.Create(pathOf kept, Record.encode Record.DefaultMaxBytes kept |> ok)
              Change.Create(pathOf mutableOne, Record.encode Record.DefaultMaxBytes mutableOne |> ok) ]
        |> ok
    )
    |> run
    |> ok
    |> ignore

    let validated =
        match store.Provider.Read signal (pathOf kept) |> run |> ok with
        | ReadOutcome.Found stored ->
            Integrity.validate (Layout.keyOf stored.Path |> Option.get) ({ Type = responseType; OldestReadable = 1; Current = 1 }) Record.DefaultMaxBytes stored |> ok
        | other -> failwith $"expected Found, got {other}"

    store, kept, validated

let private eraseIn ns (provider: StorageProvider) (path: RelativePath) (validated: ValidatedRecord) =
    let request = Erasure.request path validated at "retention rule SIG-RET-30D" |> ok
    Erasure.commit provider (Erasure.operation ns (metadata ()) [ request ] |> ok) |> run

[<Fact>]
let ``only an immutable record at an authoritative path is erased, for a one-line reason (ARCA-INT-005)`` () =
    let _, kept, validated = seeded ()
    let path = pathOf kept

    match Erasure.request path { validated with Record = { validated.Record with Mutability = Mutability.Mutable } } at "rule" with
    | Error(ErasureError.NotImmutable _) -> ()
    | other -> failwith $"a mutable record was accepted: {other}"

    match Erasure.request (RelativePath.parse "derived/indexes/x.json" |> ok) validated at "rule" with
    | Error(ErasureError.NotARecord _) -> ()
    | other -> failwith $"a derived path was accepted: {other}"

    for reason in [ ""; " padded "; "two\nlines"; String('r', Erasure.MaxReasonLength + 1); "ghp_" + String('A', 36) ] do
        match Erasure.request path validated at reason with
        | Error(ErasureError.InvalidReason _) -> ()
        | other -> failwith $"the reason {reason} was accepted: {other}"

    let request = Erasure.request path validated at "retention rule SIG-RET-30D" |> ok
    Assert.Equal(validated.ContentHash, request.Tombstone.ErasedContentHash)
    Assert.Equal(validated.Revision, request.Tombstone.ErasedRevision)

[<Fact>]
let ``a tombstone round-trips and carries the hash, never the content (ARCA-INT-005)`` () =
    let _, _, validated = seeded ()

    let tombstone =
        { ErasedContentHash = validated.ContentHash
          ErasedRevision = validated.Revision
          ErasedAt = at
          Reason = "retention rule SIG-RET-30D" }

    let text = Tombstone.encode tombstone
    Assert.Equal(Some tombstone, Tombstone.decode text)
    Assert.DoesNotContain(personal, text)
    Assert.Equal(None, Tombstone.decode (Record.encode Record.DefaultMaxBytes validated.Record |> ok))
    Assert.Equal(None, Tombstone.decode "not json")

[<Fact>]
let ``erasure is refused before anything is sent by a provider without the Erase capability (ARCA-INT-005)`` () =
    let store, kept, validated = seeded ()

    let without =
        { store.Provider with
            Capabilities =
                { store.Provider.Capabilities with
                    States = store.Provider.Capabilities.States |> Map.add Capability.Erase (CapabilityState.Unavailable "not offered") } }

    match eraseIn signal without (pathOf kept) validated with
    | Error(StorageFailure.Refused(WriteRefusal.CapabilityUnavailable refusal)) -> Assert.Equal(Capability.Erase, refusal.Capability)
    | other -> failwith $"expected CapabilityUnavailable, got {other}"

    match store.Provider.Read signal (pathOf kept) |> run |> ok with
    | ReadOutcome.Found _ -> ()
    | other -> failwith $"the record changed: {other}"

[<Fact>]
let ``an ordinary delete still never touches an immutable record (ARCA-INT-003)`` () =
    let store, kept, validated = seeded ()

    match store.Provider.Commit(Operation.create signal (metadata ()) [ Change.Delete(pathOf kept, validated.Revision) ] |> ok) |> run with
    | Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.ImmutableRecord)) -> ()
    | other -> failwith $"expected ImmutableRecord, got {other}"

[<Fact>]
let ``an erasure is sent online, never queued`` () =
    let _, kept, validated = seeded ()
    let request = Erasure.request (pathOf kept) validated at "rule" |> ok
    let erasure = Erasure.operation signal (metadata ()) [ request ] |> ok
    Assert.True(erasure.IsErasure)

    match OfflineQueue.enqueue at erasure (OfflineQueue.create OfflinePolicy.QueueWrites) with
    | Error(QueueError.InvalidOperation _) -> ()
    | other -> failwith $"an erasure was queued: {other}"

[<Fact>]
let ``snapshots list tombstones apart, exports keep them, and indexes never read them (ARCA-INT-005)`` () =
    let store, kept, validated = seeded ()
    eraseIn signal store.Provider (pathOf kept) validated |> ok |> ignore

    let snapshot = Snapshot.take store.Provider signal 3 |> run |> ok
    Assert.DoesNotContain(pathOf kept, snapshot.Objects |> List.map _.Path)
    Assert.Equal<RelativePath list>([ pathOf kept ], snapshot.Erased |> List.map _.Path)

    let archive = Export.ofSnapshot snapshot
    let exported = archive.Objects |> List.find (fun item -> item.Path = RelativePath.render (pathOf kept))
    Assert.True((Tombstone.decode exported.Content).IsSome)
    Assert.DoesNotContain(personal, exported.Content)
    Assert.Equal(Ok archive, Export.encode archive |> Export.decode)

    let index =
        { Name = Segment.create "responses" |> ok
          Version = 1
          Sources = [ { Type = responseType; OldestReadable = 1; Current = 1 } ]
          Project = fun record -> [ RecordId.value record.Id, Json.Null ] }

    let sources = Derived.sources index snapshot |> ok
    Assert.Equal<string list>([ "R-2" ], sources |> List.map (fun (_, record) -> RecordId.value record.Record.Id))

[<Fact>]
let ``a migrated namespace keeps the tombstone, so the record stays erased at the target (ARCA-INT-005)`` () =
    let store, kept, validated = seeded ()
    eraseIn signal store.Provider (pathOf kept) validated |> ok |> ignore
    let target = signalAt elsewhere

    let plan =
        { Id = MigrationId.create "M-2026-10-archive" |> ok
          Source = signal
          Target = target
          RecordSchemas = Map.empty
          Transform = Ok
          Actor = { Kind = ActorKind.Service; Id = ActorId.create "signal/migrator" |> ok }
          BatchSize = 10 }

    Migration.run store.Provider store.Provider plan |> run |> ok |> ignore

    match store.Provider.Read target (pathOf kept) |> run |> ok with
    | ReadOutcome.Erased erased -> Assert.Equal(validated.ContentHash, erased.Tombstone.ErasedContentHash)
    | other -> failwith $"expected Erased at the target, got {other}"

    let recreated =
        Operation.create target (metadata ()) [ Change.Create(pathOf kept, Record.encode Record.DefaultMaxBytes (response "R-1" Mutability.Immutable) |> ok) ]
        |> ok

    match store.Provider.Commit recreated |> run with
    | Error _ -> ()
    | Ok _ -> failwith "an erased record was recreated at the migration target"
