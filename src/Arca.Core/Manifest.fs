namespace Arca

open System
open System.Globalization

/// Who acted (ARCA-COMMIT-002, ARCA-COMMIT-003). The application supplies
/// the kind; Arca never upgrades an agent to a human.
[<RequireQualifiedAccess>]
type ActorKind =
    | Human
    | Agent
    | Service
    | Integration

/// An actor's identifier: `A-Z a-z 0-9 . _ : / -`, 1 to 128 characters. It is
/// an identifier, never a display name or an e-mail address, so `@` and
/// spaces are refused (ARCA-REC-004, ARCA-COMMIT-004).
type ActorId = private ActorId of string

/// Construction of actor identifiers.
[<RequireQualifiedAccess>]
module ActorId =

    /// A validated actor identifier.
    let create (text: string) =
        let valid =
            not (String.IsNullOrEmpty text)
            && text.Length <= 128
            && text
               |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = ':' || c = '/' || c = '-')

        if valid then Ok(ActorId text) else Error text

    /// The identifier's text.
    let value (ActorId text) = text

/// An actor: kind and identifier.
type Actor = { Kind: ActorKind; Id: ActorId }

/// Wire names for actor kinds.
[<RequireQualifiedAccess>]
module ActorKind =

    /// The kind's wire name.
    let toWire =
        function
        | ActorKind.Human -> "human"
        | ActorKind.Agent -> "agent"
        | ActorKind.Service -> "service"
        | ActorKind.Integration -> "integration"

    /// The kind for a wire name.
    let ofWire =
        function
        | "human" -> Some ActorKind.Human
        | "agent" -> Some ActorKind.Agent
        | "service" -> Some ActorKind.Service
        | "integration" -> Some ActorKind.Integration
        | _ -> None

/// The phase of a migration (ARCA-MIG-002: validate, copy, verify, activate),
/// as recorded in a manifest.
[<RequireQualifiedAccess>]
type MigrationPhase =
    | Validating
    | Copying
    | Verifying
    | Activating
    /// The target of a finished migration: active, recording which migration
    /// produced it.
    | Completed
    /// The source of a finished migration, explicitly retired: no longer used.
    | Retired

/// A migration in progress, recorded in the manifest so it can be resumed.
type MigrationState =
    { MigrationId: string
      Phase: MigrationPhase }

/// What a manifest describes.
[<RequireQualifiedAccess>]
type ManifestScope =
    | Application
    | Dataset of DatasetId

/// An application or dataset manifest (ARCA-REC-004, ARCA-LOC-006). It names
/// the namespace and every version a reader must check before touching the
/// data. It holds identifiers and versions only: no secrets and no personal
/// data.
[<NoComparison>]
type Manifest =
    { Scope: ManifestScope
      Application: AppId
      /// The version of Arca's storage layout the namespace uses.
      StorageSchema: int
      /// The storage contract version the data was written under.
      ProviderContract: int
      /// The current schema version of each record type.
      RecordSchemas: Map<string, int>
      CreatedBy: Actor
      /// Supplied by the caller; Arca reads no clock.
      CreatedAt: DateTimeOffset
      /// Where the data lives. A configured location that differs is a
      /// relocation, which needs a migration (ARCA-LOC-009).
      Location: DataLocation
      Migration: MigrationState option }

/// A manifest that does not fit the running software or its configuration.
[<RequireQualifiedAccess>]
type ManifestProblem =
    | WrongApplication of found: string * expected: string
    | WrongScope
    | UnsupportedStorageSchema of found: int * supported: int
    | UnsupportedProviderContract of found: int * supported: int
    | Relocated of RelocationRequired
    /// A migration is in progress; the data is not ready for normal use.
    | MigrationInProgress of MigrationState
    /// The data was migrated elsewhere and this copy explicitly retired
    /// (ARCA-MIG-002, ARCA-LOC-009).
    | Retired of migrationId: string

/// The manifest format and its checks.
[<RequireQualifiedAccess>]
module Manifest =

    /// The manifest format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// The storage layout version this Arca writes.
    [<Literal>]
    let StorageSchema = 1

    /// Manifests are small; this limit keeps them so.
    [<Literal>]
    let MaxBytes = 65536L

    let private fields =
        [ "application"
          "arcaManifest"
          "createdAt"
          "createdBy"
          "dataset"
          "location"
          "migration"
          "providerContract"
          "recordSchemas"
          "scope"
          "storageSchema" ]

    let private timestamp (at: DateTimeOffset) =
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let private phaseWire =
        function
        | MigrationPhase.Validating -> "validating"
        | MigrationPhase.Copying -> "copying"
        | MigrationPhase.Verifying -> "verifying"
        | MigrationPhase.Activating -> "activating"
        | MigrationPhase.Completed -> "completed"
        | MigrationPhase.Retired -> "retired"

    /// The manifest as a JSON value.
    let toJson (manifest: Manifest) =
        Json.objectOf
            [ "arcaManifest", Json.Number(decimal Format)
              "scope",
              Json.String(
                  match manifest.Scope with
                  | ManifestScope.Application -> "application"
                  | ManifestScope.Dataset _ -> "dataset"
              )
              "application", Json.String(AppId.value manifest.Application)
              "dataset",
              (match manifest.Scope with
               | ManifestScope.Dataset id -> Json.String(DatasetId.value id)
               | ManifestScope.Application -> Json.Null)
              "storageSchema", Json.Number(decimal manifest.StorageSchema)
              "providerContract", Json.Number(decimal manifest.ProviderContract)
              "recordSchemas",
              Json.objectOf (manifest.RecordSchemas |> Map.toList |> List.map (fun (key, version) -> key, Json.Number(decimal version)))
              "createdBy",
              Json.objectOf
                  [ "kind", Json.String(ActorKind.toWire manifest.CreatedBy.Kind)
                    "id", Json.String(ActorId.value manifest.CreatedBy.Id) ]
              "createdAt", Json.String(timestamp manifest.CreatedAt)
              "location",
              Json.objectOf
                  [ "owner", Json.String manifest.Location.Repository.Owner
                    "repository", Json.String manifest.Location.Repository.Name
                    "branch", Json.String(BranchName.value manifest.Location.Branch)
                    "basePath", Json.String(RelativePath.render manifest.Location.BasePath) ]
              "migration",
              (match manifest.Migration with
               | None -> Json.Null
               | Some state ->
                   Json.objectOf [ "id", Json.String state.MigrationId; "phase", Json.String(phaseWire state.Phase) ]) ]

    /// The canonical text to store.
    let encode (manifest: Manifest) = Json.canonicalText (toJson manifest)

    let private decodeScope value =
        Decode.text "scope" value
        |> Result.bind (function
            | "application" ->
                match Json.field "dataset" value with
                | Some Json.Null
                | None -> Ok ManifestScope.Application
                | Some _ -> Error(DecodeError.InvalidField("dataset", "an application manifest names no dataset"))
            | "dataset" ->
                Decode.text "dataset" value
                |> Result.bind (Decode.validated "dataset" DatasetId.create)
                |> Result.map ManifestScope.Dataset
            | other -> Error(DecodeError.InvalidField("scope", $"'{other}' is not application or dataset")))

    let private decodeSchemas value =
        Decode.field "recordSchemas" value
        |> Result.bind Decode.members
        |> Result.bind (fun members ->
            members
            |> List.fold
                (fun state (key, version) ->
                    state
                    |> Result.bind (fun schemas ->
                        match RecordType.create key, version with
                        | Ok _, Json.Number number when number >= 1m && number = Math.Floor number && number <= 1000000m ->
                            Ok(Map.add key (int number) schemas)
                        | _ -> Error(DecodeError.InvalidField("recordSchemas", $"'{key}' needs a record type and a version of at least 1"))))
                (Ok Map.empty))

    let private decodeActor value =
        Decode.field "createdBy" value
        |> Result.bind (fun actor ->
            Decode.closed [ "id"; "kind" ] actor
            |> Result.bind (fun () -> Decode.text "kind" actor)
            |> Result.bind (fun kind ->
                match ActorKind.ofWire kind with
                | Some kind -> Ok kind
                | None -> Error(DecodeError.InvalidField("createdBy.kind", $"'{kind}' is not an actor kind")))
            |> Result.bind (fun kind ->
                Decode.text "id" actor
                |> Result.bind (Decode.validated "createdBy.id" ActorId.create)
                |> Result.map (fun id -> { Kind = kind; Id = id })))

    let private decodeTimestamp value =
        Decode.text "createdAt" value
        |> Result.bind (fun text ->
            match DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, at -> Ok(at.ToUniversalTime())
            | _ -> Error(DecodeError.InvalidField("createdAt", "expected yyyy-MM-ddTHH:mm:ss.fffZ")))

    let private decodeLocation value =
        Decode.field "location" value
        |> Result.bind (fun location ->
            Decode.closed [ "basePath"; "branch"; "owner"; "repository" ] location
            |> Result.bind (fun () ->
                match Decode.text "owner" location, Decode.text "repository" location, Decode.text "branch" location, Decode.text "basePath" location with
                | Ok owner, Ok repository, Ok branch, Ok basePath ->
                    DataLocation.create owner repository branch basePath
                    |> Result.mapError (fun error -> DecodeError.InvalidField("location", LocationError.describe error))
                | Error error, _, _, _
                | _, Error error, _, _
                | _, _, Error error, _
                | _, _, _, Error error -> Error error))

    let private decodeMigration value =
        match Json.field "migration" value with
        | None
        | Some Json.Null -> Ok None
        | Some migration ->
            Decode.closed [ "id"; "phase" ] migration
            |> Result.bind (fun () -> Decode.text "id" migration)
            |> Result.bind (fun id ->
                Decode.text "phase" migration
                |> Result.bind (function
                    | "validating" -> Ok MigrationPhase.Validating
                    | "copying" -> Ok MigrationPhase.Copying
                    | "verifying" -> Ok MigrationPhase.Verifying
                    | "activating" -> Ok MigrationPhase.Activating
                    | "completed" -> Ok MigrationPhase.Completed
                    | "retired" -> Ok MigrationPhase.Retired
                    | other -> Error(DecodeError.InvalidField("migration.phase", $"'{other}' is not a migration phase")))
                |> Result.map (fun phase -> Some { MigrationId = id; Phase = phase }))

    /// A manifest from a JSON value.
    let ofJson (value: Json) =
        Decode.integer "arcaManifest" value
        |> Result.bind (fun format ->
            if format > Format || format < 1 then Error(DecodeError.UnsupportedFormat(format, Format)) else Decode.closed fields value)
        |> Result.bind (fun () ->
            match
                decodeScope value,
                Decode.text "application" value |> Result.bind (Decode.validated "application" AppId.create),
                Decode.integer "storageSchema" value,
                Decode.integer "providerContract" value,
                decodeSchemas value
            with
            | Ok scope, Ok application, Ok storage, Ok contract, Ok schemas ->
                match decodeActor value, decodeTimestamp value, decodeLocation value, decodeMigration value with
                | Ok actor, Ok at, Ok location, Ok migration ->
                    Ok
                        { Scope = scope
                          Application = application
                          StorageSchema = storage
                          ProviderContract = contract
                          RecordSchemas = schemas
                          CreatedBy = actor
                          CreatedAt = at
                          Location = location
                          Migration = migration }
                | Error error, _, _, _
                | _, Error error, _, _
                | _, _, Error error, _
                | _, _, _, Error error -> Error error
            | Error error, _, _, _, _
            | _, Error error, _, _, _
            | _, _, Error error, _, _
            | _, _, _, Error error, _
            | _, _, _, _, Error error -> Error error)

    /// A manifest from stored text, which must be canonical.
    let decode (text: string) =
        Decode.canonical MaxBytes text |> Result.bind ofJson

    /// Every way a manifest does not fit the namespace the application is
    /// configured for, before any record is read or written (ARCA-REC-005,
    /// ARCA-LOC-006, ARCA-LOC-009). Empty means the namespace is usable.
    let check (ns: Namespace) (manifest: Manifest) =
        [ if manifest.Application <> ns.Application then
              ManifestProblem.WrongApplication(AppId.value manifest.Application, AppId.value ns.Application)
          match manifest.Scope, ns.Dataset with
          | ManifestScope.Application, None -> ()
          | ManifestScope.Dataset found, Some expected when found = expected -> ()
          | _ -> ManifestProblem.WrongScope
          if manifest.StorageSchema <> StorageSchema then
              ManifestProblem.UnsupportedStorageSchema(manifest.StorageSchema, StorageSchema)
          if manifest.ProviderContract > StorageContract.Version then
              ManifestProblem.UnsupportedProviderContract(manifest.ProviderContract, StorageContract.Version)
          match Relocation.check manifest.Location ns.Location with
          | Ok() -> ()
          | Error relocation -> ManifestProblem.Relocated relocation
          match manifest.Migration with
          | None
          | Some { Phase = MigrationPhase.Completed } -> ()
          | Some { Phase = MigrationPhase.Retired; MigrationId = id } -> ManifestProblem.Retired id
          | Some state -> ManifestProblem.MigrationInProgress state ]
