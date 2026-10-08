namespace Arca

open System

/// A record's stable internal identifier (ARCA-REC-003). It is stored inside
/// the record, so moving the record never changes it. The application
/// supplies it; Arca draws no randomness. `A-Z a-z 0-9 _ -`, 1 to 100
/// characters, starting with a letter or digit, so it is also a valid file
/// name.
type RecordId = private RecordId of string

/// Construction of record identifiers.
[<RequireQualifiedAccess>]
module RecordId =

    /// A validated record identifier.
    let create (text: string) =
        let valid =
            not (String.IsNullOrEmpty text)
            && text.Length <= 100
            && Char.IsAsciiLetterOrDigit text[0]
            && text |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-')

        if valid then Ok(RecordId text) else Error text

    /// The identifier's text.
    let value (RecordId text) = text

/// A record's type, owned by the application: lower-case dotted words, the
/// first naming the application, for example `chrona.activity`.
type RecordType = private RecordType of string

/// Construction of record types.
[<RequireQualifiedAccess>]
module RecordType =

    /// A validated record type.
    let create (text: string) =
        let word (part: string) =
            part.Length > 0
            && Char.IsAsciiLetterLower part[0]
            && part |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')

        let parts = text.Split '.'

        if text.Length <= 100 && parts.Length >= 2 && Array.forall word parts then
            Ok(RecordType text)
        else
            Error text

    /// The type's text.
    let value (RecordType text) = text

/// Whether a record may change after it is first written (ARCA-INT-003).
[<RequireQualifiedAccess>]
type Mutability =
    | Mutable
    /// Written once; any later change is a detected integrity violation.
    | Immutable

/// One authoritative record (ARCA-REC-001..005). The body is the
/// application's; Arca never interprets it.
[<NoComparison>]
type Record =
    { Id: RecordId
      Type: RecordType
      /// The version of the record type's schema the body follows (ARCA-REC-005).
      SchemaVersion: int
      Mutability: Mutability
      Body: Json }

/// Why stored bytes are not a valid record or manifest. Storage content is
/// untrusted input (ARCA-INT-001): every failure is typed.
[<RequireQualifiedAccess>]
type DecodeError =
    | InvalidJson of JsonError
    /// The bytes are valid JSON but not in canonical form.
    | NotCanonical
    /// A required field is missing or has the wrong type.
    | MissingField of field: string
    | InvalidField of field: string * detail: string
    /// A field this format version does not define, for example an
    /// encryption envelope written by a newer Arca (ARCA-LOC-010).
    | UnknownField of field: string
    /// A newer envelope or manifest format than this Arca reads.
    | UnsupportedFormat of found: int * supported: int
    /// The bytes exceed the size limit (ARCA-REC-007).
    | TooLarge of bytes: int64 * limit: int64

/// Why a record could not be encoded for storage.
[<RequireQualifiedAccess>]
type EncodeError =
    /// The canonical encoding exceeds the limit. Large artifacts are stored
    /// elsewhere and referenced (ARCA-REC-007).
    | TooLarge of bytes: int64 * limit: int64
    | InvalidSchemaVersion of version: int

/// Decoding helpers shared by the record and manifest codecs.
module internal Decode =

    let field name value =
        match Json.field name value with
        | Some found -> Ok found
        | None -> Error(DecodeError.MissingField name)

    let text name value =
        field name value
        |> Result.bind (function
            | Json.String text -> Ok text
            | _ -> Error(DecodeError.InvalidField(name, "expected a string")))

    let integer name value =
        field name value
        |> Result.bind (function
            | Json.Number number when number = Math.Floor number && number >= decimal Int32.MinValue && number <= decimal Int32.MaxValue ->
                Ok(int number)
            | _ -> Error(DecodeError.InvalidField(name, "expected an integer")))

    let members value =
        match value with
        | Json.Object members -> Ok members
        | _ -> Error(DecodeError.InvalidField("$", "expected an object"))

    let closed (allowed: string list) value =
        members value
        |> Result.bind (fun members ->
            match members |> List.tryFind (fun (key, _) -> not (List.contains key allowed)) with
            | Some(key, _) -> Error(DecodeError.UnknownField key)
            | None -> Ok())

    let validated name create text =
        create text |> Result.mapError (fun _ -> DecodeError.InvalidField(name, $"'{text}' is not valid"))

    /// Parses bytes as canonical JSON within a size limit.
    let canonical (limit: int64) (text: string) =
        let size = int64 (Text.Encoding.UTF8.GetByteCount text)

        if size > limit then
            Error(DecodeError.TooLarge(size, limit))
        else
            match Json.parse text with
            | Error error -> Error(DecodeError.InvalidJson error)
            | Ok value when not (String.Equals(Json.canonicalText value, text, StringComparison.Ordinal)) -> Error DecodeError.NotCanonical
            | Ok value -> Ok value

/// The stored record envelope (ARCA-REC-001, ARCA-REC-003, ARCA-REC-005).
///
/// ```json
/// {"arcaRecord":1,"body":{...},"id":"A-1","mutability":"mutable","schemaVersion":3,"type":"chrona.activity"}
/// ```
///
/// The envelope is closed: a field this format does not define is refused,
/// so a later format (an encryption envelope, for example) fails explicitly
/// in an older reader instead of being misread (ARCA-LOC-010).
[<RequireQualifiedAccess>]
module Record =

    /// The envelope format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// The default size limit for one stored record, in bytes: GitHub's
    /// inline content limit (1 MiB).
    [<Literal>]
    let DefaultMaxBytes = 1048576L

    let private fields = [ "arcaRecord"; "body"; "id"; "mutability"; "schemaVersion"; "type" ]

    /// The record as an envelope value.
    let toJson (record: Record) =
        Json.objectOf
            [ "arcaRecord", Json.Number(decimal Format)
              "id", Json.String(RecordId.value record.Id)
              "type", Json.String(RecordType.value record.Type)
              "schemaVersion", Json.Number(decimal record.SchemaVersion)
              "mutability",
              Json.String(
                  match record.Mutability with
                  | Mutability.Mutable -> "mutable"
                  | Mutability.Immutable -> "immutable"
              )
              "body", record.Body ]

    /// The canonical text to store, or a refusal when it exceeds `maxBytes`.
    let encode (maxBytes: int64) (record: Record) =
        if record.SchemaVersion < 1 then
            Error(EncodeError.InvalidSchemaVersion record.SchemaVersion)
        else
            let text = Json.canonicalText (toJson record)
            let size = int64 (Text.Encoding.UTF8.GetByteCount text)
            if size > maxBytes then Error(EncodeError.TooLarge(size, maxBytes)) else Ok text

    /// The content hash of the record's canonical encoding.
    let contentHash (record: Record) = Json.contentHash (toJson record)

    /// A record from an envelope value.
    let ofJson (value: Json) =
        Decode.integer "arcaRecord" value
        |> Result.bind (fun format ->
            if format > Format || format < 1 then
                Error(DecodeError.UnsupportedFormat(format, Format))
            else
                Decode.closed fields value)
        |> Result.bind (fun () -> Decode.text "id" value |> Result.bind (Decode.validated "id" RecordId.create))
        |> Result.bind (fun id ->
            Decode.text "type" value
            |> Result.bind (Decode.validated "type" RecordType.create)
            |> Result.map (fun recordType -> id, recordType))
        |> Result.bind (fun (id, recordType) ->
            Decode.integer "schemaVersion" value
            |> Result.bind (fun version ->
                if version < 1 then Error(DecodeError.InvalidField("schemaVersion", "must be at least 1")) else Ok version)
            |> Result.map (fun version -> id, recordType, version))
        |> Result.bind (fun (id, recordType, version) ->
            Decode.text "mutability" value
            |> Result.bind (function
                | "mutable" -> Ok Mutability.Mutable
                | "immutable" -> Ok Mutability.Immutable
                | other -> Error(DecodeError.InvalidField("mutability", $"'{other}' is not mutable or immutable")))
            |> Result.map (fun mutability -> id, recordType, version, mutability))
        |> Result.bind (fun (id, recordType, version, mutability) ->
            Decode.field "body" value
            |> Result.map (fun body ->
                { Id = id
                  Type = recordType
                  SchemaVersion = version
                  Mutability = mutability
                  Body = body }))

    /// A record from stored text, which must be canonical and within `maxBytes`.
    let decode (maxBytes: int64) (text: string) =
        Decode.canonical maxBytes text |> Result.bind ofJson

/// How the running software can use one schema version of a record type (ARCA-REC-005).
[<RequireQualifiedAccess>]
type SchemaAccess =
    /// The current version: read and write.
    | ReadWrite
    /// An older version the software still reads. Writing it would need an
    /// explicit migration; Arca never silently rewrites older data.
    | ReadOnly
    /// A newer version than the software knows: refuse, never guess.
    | UnsupportedFuture of found: int * newest: int
    /// An older version the software no longer reads: migrate first.
    | UnsupportedPast of found: int * oldest: int

/// The schema versions of one record type the running software supports.
type SchemaSupport =
    { Type: RecordType
      /// The oldest version it still reads.
      OldestReadable: int
      /// The version it writes, which is also the newest it reads.
      Current: int }

/// Schema compatibility decisions (ARCA-REC-005).
[<RequireQualifiedAccess>]
module SchemaSupport =

    /// How `version` may be used under `support`.
    let access (support: SchemaSupport) (version: int) =
        if version > support.Current then SchemaAccess.UnsupportedFuture(version, support.Current)
        elif version < support.OldestReadable then SchemaAccess.UnsupportedPast(version, support.OldestReadable)
        elif version = support.Current then SchemaAccess.ReadWrite
        else SchemaAccess.ReadOnly

    /// Ok when a record may be read; the refusal otherwise.
    let canRead support version =
        match access support version with
        | SchemaAccess.ReadWrite
        | SchemaAccess.ReadOnly -> Ok()
        | refusal -> Error refusal

    /// Ok when a record at `version` may be written as is.
    let canWrite support version =
        match access support version with
        | SchemaAccess.ReadWrite -> Ok()
        | refusal -> Error refusal

/// Whether an object is authoritative or derived (ARCA-REC-006). The two live
/// under different folders of a namespace and are distinct types, so derived
/// data can never be mistaken for, or overwrite, authoritative records.
[<RequireQualifiedAccess>]
type Authority =
    | Authoritative
    | Derived

/// A record's address inside its namespace: its type, the application's
/// partition (for example organization, actor, year, month) and its id.
type RecordKey =
    { Type: RecordType
      Partition: Segment list
      Id: RecordId }

/// Deterministic paths inside a namespace (ARCA-REC-002, ARCA-REC-006).
///
/// - authoritative: `records/<type>/<partition…>/<id>.json`;
/// - derived: `derived/<name…>`;
/// - manifest: `arca-manifest.json` at the namespace root (ARCA-REC-004).
[<RequireQualifiedAccess>]
module Layout =

    /// The folder for authoritative records.
    [<Literal>]
    let RecordsFolder = "records"

    /// The folder for derived data: indexes, projections, caches.
    [<Literal>]
    let DerivedFolder = "derived"

    /// The manifest's file name at a namespace root.
    [<Literal>]
    let ManifestFile = "arca-manifest.json"

    let private segment text =
        match Segment.create text with
        | Ok segment -> segment
        | Error error -> invalidOp $"internal: not a valid segment: {error}"

    /// The path of an authoritative record, relative to its namespace.
    let recordPath (key: RecordKey) =
        RelativePath.ofSegments (
            [ segment RecordsFolder; segment (RecordType.value key.Type) ]
            @ key.Partition
            @ [ segment (RecordId.value key.Id + ".json") ]
        )

    /// The path of a derived object, relative to its namespace.
    let derivedPath (name: Segment list) =
        if List.isEmpty name then
            Error(LocationError.EmptySegment "")
        else
            RelativePath.ofSegments (segment DerivedFolder :: name)

    /// The manifest's path, relative to its namespace.
    let manifestPath = RelativePath.ofSegments [ segment ManifestFile ]

    /// Which kind of object a namespace-relative path names, if any.
    let authorityOf (path: RelativePath) =
        match RelativePath.segments path |> List.map Segment.value with
        | folder :: _ :: _ when folder = RecordsFolder -> Some Authority.Authoritative
        | folder :: _ :: _ when folder = DerivedFolder -> Some Authority.Derived
        | _ -> None

/// A reference to an artifact stored outside Arca, such as a file in object
/// storage (ARCA-REC-007). Arca stores the reference, never the bytes.
type ExternalReference =
    { /// An absolute `https` URI.
      Uri: string
      MediaType: string
      /// The artifact's content hash, when known, so a fetch can be verified.
      ContentHash: string option
      SizeBytes: int64 option }

/// Encoding of external references inside record bodies (ARCA-REC-007).
[<RequireQualifiedAccess>]
module ExternalReference =

    /// A reference, when the URI is absolute https and the media type is present.
    let create uri mediaType contentHash sizeBytes =
        match Uri.TryCreate(uri, UriKind.Absolute) with
        | true, NonNull parsed when parsed.Scheme = Uri.UriSchemeHttps && not (String.IsNullOrWhiteSpace mediaType) ->
            Ok
                { Uri = uri
                  MediaType = mediaType
                  ContentHash = contentHash
                  SizeBytes = sizeBytes }
        | _ -> Error uri

    /// The reference as a JSON value for a record body.
    let toJson (reference: ExternalReference) =
        Json.objectOf (
            [ "uri", Json.String reference.Uri; "mediaType", Json.String reference.MediaType ]
            @ (reference.ContentHash |> Option.map (fun hash -> "contentHash", Json.String hash) |> Option.toList)
            @ (reference.SizeBytes |> Option.map (fun size -> "sizeBytes", Json.Number(decimal size)) |> Option.toList)
        )
