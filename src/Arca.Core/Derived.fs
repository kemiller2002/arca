namespace Arca

open System

/// The authoritative records an index was built from, as a fingerprint
/// (ARCA-MIG-001): how many, and a hash over each record's path and content
/// hash, in path order.
type SourceSet = { Count: int; Hash: string }

/// What an index is: its name, its definition's version, the record types it
/// reads and the entries it projects from each record. The projection is a
/// pure function, so the same records always give the same index.
[<NoEquality; NoComparison>]
type IndexDefinition =
    { /// A path segment; the index is stored at `derived/indexes/<name>.json`.
      Name: Segment
      /// Change it whenever `Project` changes, so stored indexes are rebuilt.
      Version: int
      /// The record types the index reads, with the schema versions this
      /// software can read (ARCA-REC-005).
      Sources: SchemaSupport list
      /// The index entries (key, value) for one record.
      Project: Record -> (string * Json) list }

/// A built index: derived data, never authoritative (ARCA-REC-006).
[<NoComparison>]
type DerivedIndex =
    { Name: string
      Version: int
      Source: SourceSet
      /// Ordered by key, then by the value's canonical text.
      Entries: (string * Json) list }

/// Whether a stored index still reflects the authoritative records.
[<RequireQualifiedAccess>]
type IndexStatus =
    | Current
    /// The records changed since the index was built.
    | Stale of built: SourceSet * current: SourceSet
    /// The index was built by another version of its definition.
    | OtherVersion of found: int * expected: int
    /// The index is not stored yet.
    | Missing

/// Why an index could not be read, built or written.
[<RequireQualifiedAccess>]
type DerivedError =
    | Corrupt of reason: string
    | Snapshot of SnapshotError
    /// A source record failed validation; an index is never built on records
    /// that do not validate (ARCA-INT-001).
    | InvalidSource of path: string * IntegrityFailure
    | Provider of StorageFailure
    | InvalidDefinition of reason: string

/// The difference between two builds of an index.
[<NoComparison>]
type IndexDifference =
    { /// Entries only the first index has.
      Removed: (string * Json) list
      /// Entries only the second index has.
      Added: (string * Json) list }

/// Rebuildable derived indexes (ARCA-MIG-001). An index records the source
/// set it was built from, so it can be validated against the records, rebuilt
/// from them and compared; it is written only under `derived/`, so it can
/// never overwrite an authoritative record.
[<RequireQualifiedAccess>]
module Derived =

    /// The index format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// The folder under `derived/` that holds indexes.
    [<Literal>]
    let IndexesFolder = "indexes"

    let private segment text =
        match Segment.create text with
        | Ok segment -> segment
        | Error error -> invalidOp ("internal: not a valid segment: " + LocationError.describe error)

    /// Where an index is stored, relative to its namespace.
    let path (definition: IndexDefinition) =
        Segment.create (Segment.value definition.Name + ".json")
        |> Result.bind (fun file -> Layout.derivedPath [ segment IndexesFolder; file ])

    /// The fingerprint of a set of validated records, keyed by path.
    let sourceSet (records: (RelativePath * ValidatedRecord) list) =
        let ordered =
            records
            |> List.map (fun (path, record) -> RelativePath.render path, record.ContentHash)
            |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        { Count = ordered.Length
          Hash = Json.contentHash (Json.Array(ordered |> List.map (fun (path, hash) -> Json.Array [ Json.String path; Json.String hash ]))) }

    let private orderEntries (entries: (string * Json) list) =
        entries
        |> List.map (fun (key, value) -> (key, Json.canonicalText value), (key, value))
        |> List.sortWith (fun ((k1, v1), _) ((k2, v2), _) ->
            match String.CompareOrdinal(k1, k2) with
            | 0 -> String.CompareOrdinal(v1, v2)
            | order -> order)
        |> List.map snd

    let private reads (definition: IndexDefinition) (record: ValidatedRecord) =
        definition.Sources |> List.exists (fun support -> support.Type = record.Record.Type)

    /// The index of a set of validated records. Records of other types are
    /// not read and do not count towards the source set.
    let build (definition: IndexDefinition) (records: (RelativePath * ValidatedRecord) list) =
        let read = records |> List.filter (snd >> reads definition)

        { Name = Segment.value definition.Name
          Version = definition.Version
          Source = sourceSet read
          Entries = read |> List.collect (fun (_, record) -> definition.Project record.Record) |> orderEntries }

    /// The records a definition reads from a snapshot, each validated as the
    /// record its path names (ARCA-INT-001).
    let sources (definition: IndexDefinition) (snapshot: Snapshot) =
        let rec collect (objects: StoredObject list) acc =
            match objects with
            | [] -> Ok(List.rev acc)
            | item :: rest ->
                match Layout.keyOf item.Path with
                | None -> collect rest acc
                | Some key ->
                    match definition.Sources |> List.tryFind (fun support -> support.Type = key.Type) with
                    | None -> collect rest acc
                    | Some support ->
                        match Integrity.validate key support Record.DefaultMaxBytes item with
                        | Error failure -> Error(DerivedError.InvalidSource(RelativePath.render item.Path, failure))
                        | Ok record -> collect rest ((item.Path, record) :: acc)

        collect snapshot.Objects []

    /// The index as canonical text.
    let encode (index: DerivedIndex) =
        Json.canonicalText (
            Json.objectOf
                [ "arcaIndex", Json.Number(decimal Format)
                  "name", Json.String index.Name
                  "version", Json.Number(decimal index.Version)
                  "source", Json.objectOf [ "count", Json.Number(decimal index.Source.Count); "hash", Json.String index.Source.Hash ]
                  "entries", Json.Array(index.Entries |> List.map (fun (key, value) -> Json.Array [ Json.String key; value ])) ]
        )

    /// An index from stored text; anything this Arca did not write is Corrupt.
    let decode (text: string) =
        let corrupt reason = Error(DerivedError.Corrupt reason)

        let integer name value =
            match Json.field name value with
            | Some(Json.Number number) when number = Math.Floor number && number >= 0m && number <= decimal Int32.MaxValue -> Ok(int number)
            | _ -> corrupt $"{name} is not a non-negative integer"

        let str name value =
            match Json.field name value with
            | Some(Json.String found) -> Ok found
            | _ -> corrupt $"{name} is not text"

        if not (Json.isCanonical text) then
            corrupt "the index is not canonical JSON"
        else
            match Json.parse text with
            | Error error -> corrupt (JsonError.describe error)
            | Ok value ->
                match integer "arcaIndex" value, str "name" value, integer "version" value, Json.field "source" value, Json.field "entries" value with
                | Ok format, _, _, _, _ when format <> Format -> corrupt $"index format {format}, not {Format}"
                | Ok _, Ok name, Ok version, Some source, Some(Json.Array entries) ->
                    match integer "count" source, str "hash" source with
                    | Ok count, Ok hash ->
                        let pairs =
                            entries
                            |> List.map (function
                                | Json.Array [ Json.String key; value ] -> Some(key, value)
                                | _ -> None)

                        if pairs |> List.exists Option.isNone then
                            corrupt "an entry is not a [key, value] pair"
                        else
                            Ok
                                { Name = name
                                  Version = version
                                  Source = { Count = count; Hash = hash }
                                  Entries = pairs |> List.choose id }
                    | _ -> corrupt "the source set is malformed"
                | Error error, _, _, _, _
                | _, Error error, _, _, _
                | _, _, Error error, _, _ -> Error error
                | _ -> corrupt "the index is malformed"

    /// Whether a stored index still reflects the current source set.
    let check (definition: IndexDefinition) (current: SourceSet) (stored: DerivedIndex option) =
        match stored with
        | None -> IndexStatus.Missing
        | Some index when index.Version <> definition.Version -> IndexStatus.OtherVersion(index.Version, definition.Version)
        | Some index when index.Source <> current -> IndexStatus.Stale(index.Source, current)
        | Some _ -> IndexStatus.Current

    /// The entries that differ between two builds (for example the stored
    /// index and a fresh rebuild).
    let compare (first: DerivedIndex) (second: DerivedIndex) =
        let canonical (entries: (string * Json) list) =
            entries |> List.map (fun (key, value) -> key, Json.canonicalText value)

        let difference (a: (string * Json) list) (b: (string * Json) list) =
            let others = canonical b |> List.countBy id |> Map.ofList

            a
            |> List.fold
                (fun (remaining: Map<string * string, int>, kept) (key, value) ->
                    let id = key, Json.canonicalText value

                    match remaining |> Map.tryFind id with
                    | Some count when count > 0 -> remaining |> Map.add id (count - 1), kept
                    | _ -> remaining, (key, value) :: kept)
                (others, [])
            |> snd
            |> List.rev

        { Removed = difference first.Entries second.Entries
          Added = difference second.Entries first.Entries }

    /// The stored index, if any.
    let read (provider: StorageProvider) (ns: Namespace) (definition: IndexDefinition) =
        async {
            match path definition with
            | Error error -> return Error(DerivedError.InvalidDefinition(LocationError.describe error))
            | Ok indexPath ->
                match! provider.Read ns indexPath with
                | Error failure -> return Error(DerivedError.Provider failure)
                | Ok ReadOutcome.Absent -> return Ok None
                | Ok(ReadOutcome.Found stored)
                | Ok(ReadOutcome.Erased { Stored = stored }) -> return decode stored.Content |> Result.map (fun index -> Some(index, stored.Revision))
        }

    /// Rebuilds the index from the authoritative records and stores it, unless
    /// the stored index is already current (idempotent). The write applies
    /// only while the records are still exactly as read; otherwise it fails
    /// with StaleChangeToken and nothing is written (ARCA-CON-001).
    let rebuild (provider: StorageProvider) (ns: Namespace) (metadata: OperationMetadata) (definition: IndexDefinition) =
        async {
            match path definition with
            | Error error -> return Error(DerivedError.InvalidDefinition(LocationError.describe error))
            | Ok indexPath ->
                match! Snapshot.take provider ns 3 with
                | Error error -> return Error(DerivedError.Snapshot error)
                | Ok snapshot ->
                    match sources definition snapshot with
                    | Error error -> return Error error
                    | Ok records ->
                        let index = build definition records

                        let stored =
                            Snapshot.find indexPath snapshot
                            |> Option.map (fun item -> item, decode item.Content |> Result.toOption)

                        match stored with
                        | Some(_, Some existing) when existing = index -> return Ok(index, None)
                        | _ ->
                            let change =
                                match stored with
                                | Some(item, _) -> Change.Update(indexPath, encode index, item.Revision)
                                | None -> Change.Create(indexPath, encode index)

                            match Operation.create ns metadata [ change ] with
                            | Error _ -> return Error(DerivedError.InvalidDefinition "the index write is not a valid operation")
                            | Ok operation ->
                                match! provider.Commit(Operation.requireChangeToken snapshot.ChangeToken operation) with
                                | Error failure -> return Error(DerivedError.Provider failure)
                                | Ok receipt -> return Ok(index, Some receipt)
        }
