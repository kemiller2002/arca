/// The record envelope, schema versions, layout, references and manifests
/// (ARCA-REC-001..007, ARCA-LOC-006).
module Arca.Tests.RecordTests

open System
open Arca
open Xunit
open FsCheck.Xunit
open FsCheck.FSharp
open FsCheck

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

/// Canonical text with one more top-level member, still canonical.
let private withMember key value (text: string) =
    match Json.parse text with
    | Ok(Json.Object members) -> Json.canonicalText (Json.objectOf ((key, value) :: members))
    | other -> failwith $"{other}"

let private recordType = RecordType.create "chrona.activity" |> ok

let private record body =
    { Id = RecordId.create "A-01J9Z" |> ok
      Type = recordType
      SchemaVersion = 3
      Mutability = Mutability.Mutable
      Body = body }

let private sample =
    record (Json.objectOf [ "minutes", Json.Number 90m; "note", Json.String "design review" ])

[<Fact>]
let ``a record is stored as a closed, canonical envelope (ARCA-REC-001)`` () =
    let text = Record.encode Record.DefaultMaxBytes sample |> ok

    Assert.Equal(
        """{"arcaRecord":1,"body":{"minutes":90,"note":"design review"},"id":"A-01J9Z","mutability":"mutable","schemaVersion":3,"type":"chrona.activity"}""",
        text
    )

    Assert.Equal(Ok sample, Record.decode Record.DefaultMaxBytes text)

[<Fact>]
let ``equal records have equal hashes whatever the body's member order`` () =
    let reordered =
        record (Json.objectOf [ "note", Json.String "design review"; "minutes", Json.Number 90.0m ])

    Assert.Equal(Record.contentHash sample, Record.contentHash reordered)

[<Fact>]
let ``the id lives inside the record, not only in its path (ARCA-REC-003)`` () =
    let text = Record.encode Record.DefaultMaxBytes sample |> ok
    let decoded = Record.decode Record.DefaultMaxBytes text |> ok
    Assert.Equal("A-01J9Z", RecordId.value decoded.Id)

[<Fact>]
let ``non-canonical, unknown-field, future-format and oversized records are refused with typed errors`` () =
    let canonical = Record.encode Record.DefaultMaxBytes sample |> ok
    Assert.Equal(Error DecodeError.NotCanonical, Record.decode Record.DefaultMaxBytes (canonical.Replace(":", ": ")))

    let withExtra = withMember "encryption" Json.Null canonical
    Assert.Equal(Error(DecodeError.UnknownField "encryption"), Record.decode Record.DefaultMaxBytes withExtra)

    let future = canonical.Replace("\"arcaRecord\":1", "\"arcaRecord\":2")
    Assert.Equal(Error(DecodeError.UnsupportedFormat(2, 1)), Record.decode Record.DefaultMaxBytes future)

    Assert.Equal(Error(DecodeError.TooLarge(int64 canonical.Length, 10L)), Record.decode 10L canonical)

[<Fact>]
let ``a record larger than the limit cannot be encoded, so large binaries are not authoritative (ARCA-REC-007)`` () =
    let big = record (Json.String(String('x', 2000)))
    Assert.True(Result.isError (Record.encode 1000L big))

[<Fact>]
let ``schema versions decide read and write access explicitly (ARCA-REC-005)`` () =
    let support =
        { Type = recordType
          OldestReadable = 2
          Current = 4 }

    Assert.Equal(SchemaAccess.ReadWrite, SchemaSupport.access support 4)
    Assert.Equal(SchemaAccess.ReadOnly, SchemaSupport.access support 3)
    Assert.Equal(SchemaAccess.UnsupportedFuture(5, 4), SchemaSupport.access support 5)
    Assert.Equal(SchemaAccess.UnsupportedPast(1, 2), SchemaSupport.access support 1)
    Assert.Equal(Error(SchemaAccess.ReadOnly), SchemaSupport.canWrite support 3)
    Assert.Equal(Ok(), SchemaSupport.canRead support 3)

[<Fact>]
let ``record paths are deterministic and partitioned; derived data lives elsewhere (ARCA-REC-002, ARCA-REC-006)`` () =
    let partition = [ "org_1"; "u-7"; "2026"; "10" ] |> List.map (Segment.create >> ok)

    let path =
        Layout.recordPath
            { Type = recordType
              Partition = partition
              Id = RecordId.create "A-1" |> ok }
        |> ok

    Assert.Equal("records/chrona.activity/org_1/u-7/2026/10/A-1.json", RelativePath.render path)
    Assert.Equal(Some Authority.Authoritative, Layout.authorityOf path)

    let derived = Layout.derivedPath [ Segment.create "by-week.json" |> ok ] |> ok
    Assert.Equal("derived/by-week.json", RelativePath.render derived)
    Assert.Equal(Some Authority.Derived, Layout.authorityOf derived)
    Assert.Equal(None, Layout.authorityOf (Layout.manifestPath |> ok))

[<Theory>]
[<InlineData("Chrona.Activity")>]
[<InlineData("activity")>]
[<InlineData("chrona..activity")>]
[<InlineData("chrona.activity!")>]
let ``record types are lower-case dotted names`` (text: string) =
    Assert.Equal(Error text, RecordType.create text |> Result.map RecordType.value)

[<Fact>]
let ``external artifacts are references with an https URI, never inline bytes (ARCA-REC-007)`` () =
    let reference =
        ExternalReference.create "https://files.example/receipts/r1.pdf" "application/pdf" (Some "sha256:ab") (Some 2048L) |> ok

    Assert.Equal(
        """{"contentHash":"sha256:ab","mediaType":"application/pdf","sizeBytes":2048,"uri":"https://files.example/receipts/r1.pdf"}""",
        Json.canonicalText (ExternalReference.toJson reference)
    )

    Assert.True(Result.isError (ExternalReference.create "http://insecure.example/x" "text/plain" None None))
    Assert.True(Result.isError (ExternalReference.create "relative/x" "text/plain" None None))

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private chronaSpace =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Production; Name = "production" }
          Location = location }
    |> ok

let private manifest =
    { Scope = ManifestScope.Application
      Application = AppId.create "chrona" |> ok
      StorageSchema = Manifest.StorageSchema
      ProviderContract = StorageContract.Version
      RecordSchemas = Map.ofList [ "chrona.activity", 3; "chrona.timer", 1 ]
      CreatedBy = { Kind = ActorKind.Agent; Id = ActorId.create "anthropic/claude-code" |> ok }
      CreatedAt = DateTimeOffset(2026, 10, 8, 9, 30, 0, 125, TimeSpan.Zero)
      Location = location
      Migration = None }

[<Fact>]
let ``a manifest records namespace, versions, provenance and location, and round-trips (ARCA-REC-004)`` () =
    let text = Manifest.encode manifest
    Assert.True(Json.isCanonical text)
    Assert.Contains("\"createdAt\":\"2026-10-08T09:30:00.125Z\"", text)
    Assert.Contains("\"location\":{\"basePath\":\"apps\",\"branch\":\"main\",\"owner\":\"acme\",\"repository\":\"data\"}", text)
    Assert.Equal(Ok manifest, Manifest.decode text)

[<Fact>]
let ``a dataset manifest names its dataset and round-trips`` () =
    let dataset =
        { manifest with
            Scope = ManifestScope.Dataset(DatasetId.create "org_1" |> ok)
            Migration = Some { MigrationId = "M-1"; Phase = MigrationPhase.Copying } }

    Assert.Equal(Ok dataset, Manifest.decode (Manifest.encode dataset))

[<Fact>]
let ``the namespace is found through its manifest at a deterministic address (ARCA-LOC-006)`` () =
    let address = Namespace.resolve chronaSpace (Layout.manifestPath |> ok) |> ok
    Assert.Equal("apps/chrona/arca-manifest.json", address.Path)

[<Fact>]
let ``a fitting manifest has no problems`` () =
    Assert.Empty(Manifest.check chronaSpace manifest)

[<Fact>]
let ``manifest checks report wrong application, future layout, relocation and active migrations`` () =
    let moved = DataLocation.create "acme" "elsewhere" "main" "apps" |> ok

    let problems =
        Manifest.check
            chronaSpace
            { manifest with
                Application = AppId.create "summa" |> ok
                StorageSchema = 2
                ProviderContract = 9
                Location = moved
                Migration = Some { MigrationId = "M-1"; Phase = MigrationPhase.Verifying } }

    Assert.Equal<ManifestProblem list>(
        [ ManifestProblem.WrongApplication("summa", "chrona")
          ManifestProblem.UnsupportedStorageSchema(2, 1)
          ManifestProblem.UnsupportedProviderContract(9, 1)
          ManifestProblem.Relocated { Recorded = moved; Configured = location }
          ManifestProblem.MigrationInProgress { MigrationId = "M-1"; Phase = MigrationPhase.Verifying } ],
        problems
    )

[<Fact>]
let ``manifests refuse unknown fields and actor ids that look like e-mail addresses or names`` () =
    let text = Manifest.encode manifest
    Assert.Equal(Error(DecodeError.UnknownField "secret"), Manifest.decode (withMember "secret" (Json.String "x") text))
    Assert.True(Result.isError (ActorId.create "jane@example.com"))
    Assert.True(Result.isError (ActorId.create "Jane Doe"))

[<Property>]
let ``any record with an arbitrary body round-trips through its stored form`` () =
    Prop.forAll Generators.jsonArb (fun body ->
        let value = record body

        match Record.encode Int64.MaxValue value with
        | Ok text -> Record.decode Int64.MaxValue text = Ok value
        | Error _ -> false)
