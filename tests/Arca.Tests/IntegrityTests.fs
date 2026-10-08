/// Storage content is untrusted input (ARCA-INT-001..004).
module Arca.Tests.IntegrityTests

open Xunit
open Arca

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private activity = RecordType.create "chrona.activity" |> ok

let private record id mutability version =
    { Id = RecordId.create id |> ok
      Type = activity
      SchemaVersion = version
      Mutability = mutability
      Body = Json.objectOf [ "minutes", Json.Number 30m ] }

let private stored (record: Record) =
    { Path = RelativePath.parse "records/chrona.activity/A-1.json" |> ok
      Content = Record.encode Record.DefaultMaxBytes record |> ok
      Revision = Revision "r1" }

let private key id =
    { Type = activity
      Partition = []
      Id = RecordId.create id |> ok }

let private support =
    { Type = activity
      OldestReadable = 1
      Current = 2 }

[<Fact>]
let ``a valid record validates, with its revision and content hash (ARCA-INT-001)`` () =
    let original = record "A-1" Mutability.Mutable 2
    let validated = Integrity.validate (key "A-1") support Record.DefaultMaxBytes (stored original) |> ok
    Assert.Equal(original, validated.Record)
    Assert.Equal(Revision "r1", validated.Revision)
    Assert.Equal(Record.contentHash original, validated.ContentHash)

[<Fact>]
let ``every way stored content can be wrong is a typed failure (ARCA-INT-001)`` () =
    let valid = stored (record "A-1" Mutability.Mutable 2)

    Assert.Equal(
        Error(IntegrityFailure.IdentityMismatch("A-2", "A-1")),
        Integrity.validate (key "A-2") support Record.DefaultMaxBytes valid |> Result.map ignore
    )

    let otherType = { key "A-1" with Type = RecordType.create "chrona.timer" |> ok }

    Assert.Equal(
        Error(IntegrityFailure.TypeMismatch("chrona.timer", "chrona.activity")),
        Integrity.validate otherType support Record.DefaultMaxBytes valid |> Result.map ignore
    )

    let future = stored (record "A-1" Mutability.Mutable 3)

    Assert.Equal(
        Error(IntegrityFailure.UnsupportedSchema(SchemaAccess.UnsupportedFuture(3, 2))),
        Integrity.validate (key "A-1") support Record.DefaultMaxBytes future |> Result.map ignore
    )

    let edited = { valid with Content = valid.Content.Replace(":", ": ") }

    Assert.Equal(
        Error(IntegrityFailure.Invalid DecodeError.NotCanonical),
        Integrity.validate (key "A-1") support Record.DefaultMaxBytes edited |> Result.map ignore
    )

    Assert.True(Result.isError (Integrity.validate (key "A-1") support 10L valid))

[<Fact>]
let ``a changed content hash is reported, never accepted silently (ARCA-INT-002)`` () =
    let first = Integrity.validate (key "A-1") support Record.DefaultMaxBytes (stored (record "A-1" Mutability.Mutable 2)) |> ok
    Assert.Equal(Ok(), Integrity.unchanged first.ContentHash first)

    let changed =
        { record "A-1" Mutability.Mutable 2 with
            Body = Json.objectOf [ "minutes", Json.Number 31m ] }

    let later = Integrity.validate (key "A-1") support Record.DefaultMaxBytes (stored changed) |> ok
    Assert.Equal(Error(IntegrityFailure.HashMismatch(first.ContentHash, later.ContentHash)), Integrity.unchanged first.ContentHash later)

[<Fact>]
let ``commits without Arca's trailers are external edits (ARCA-INT-002)`` () =
    Assert.Equal(CommitOrigin.External, Integrity.origin "fix typo\n")

    let history =
        [ { ChangeToken = ChangeToken "c2"; Origin = CommitOrigin.External }
          { ChangeToken = ChangeToken "c1"; Origin = Integrity.origin "chrona: x\n\nArca-Format: 1\nArca-Namespace: chrona\nArca-Actor-Kind: human\nArca-Actor: u-1\nArca-Correlation: c\nArca-Idempotency-Key: key-0001\n" } ]

    Assert.Equal<HistoryEntry list>([ history.Head ], Integrity.externalEdits history)

    match history[1].Origin with
    | CommitOrigin.Arca trailers -> Assert.Equal(ActorKind.Human, trailers.Actor.Kind)
    | CommitOrigin.External -> failwith "an Arca commit must be recognized"

[<Fact>]
let ``an immutable record that changed is detected (ARCA-INT-003)`` () =
    let first = Integrity.validate (key "A-1") support Record.DefaultMaxBytes (stored (record "A-1" Mutability.Immutable 2)) |> ok

    let tampered =
        { record "A-1" Mutability.Immutable 2 with
            Body = Json.objectOf [ "minutes", Json.Number 300m ] }

    let now = Integrity.validate (key "A-1") support Record.DefaultMaxBytes (stored tampered) |> ok
    Assert.Equal(Error(IntegrityFailure.ImmutableChanged "A-1"), Integrity.immutableUnchanged first now)
    Assert.Equal(Ok(), Integrity.immutableUnchanged first first)

[<Fact>]
let ``writes over immutable or unreadable records are refused; other writes pass (ARCA-INT-003, ARCA-INT-004)`` () =
    let recordPath = RelativePath.parse "records/chrona.activity/A-1.json" |> ok
    let immutable = (stored (record "A-1" Mutability.Immutable 2)).Content
    let mutableText = (stored (record "A-1" Mutability.Mutable 2)).Content

    Assert.Equal(Error IntegrityRefusal.ImmutableRecord, Integrity.guard (Change.Update(recordPath, mutableText, Revision "r")) (Some immutable))
    Assert.Equal(Error IntegrityRefusal.ImmutableRecord, Integrity.guard (Change.Delete(recordPath, Revision "r")) (Some immutable))

    match Integrity.guard (Change.Update(recordPath, mutableText, Revision "r")) (Some "hand edited") with
    | Error(IntegrityRefusal.CorruptRecord _) -> ()
    | other -> failwith $"expected CorruptRecord, got {other}"

    Assert.Equal(Ok(), Integrity.guard (Change.Update(recordPath, mutableText, Revision "r")) (Some mutableText))
    Assert.Equal(Ok(), Integrity.guard (Change.Create(recordPath, mutableText)) None)

    let derivedPath = RelativePath.parse "derived/index.json" |> ok
    Assert.Equal(Ok(), Integrity.guard (Change.Update(derivedPath, "{}", Revision "r")) (Some "not a record"))
