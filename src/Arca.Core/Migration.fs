namespace Arca

open System

/// A migration's identifier: `A-Z a-z 0-9 . _ : -`, 1 to 64 characters. It is
/// recorded in both manifests and in every commit the migration makes.
type MigrationId = private MigrationId of string

/// Construction of migration identifiers.
[<RequireQualifiedAccess>]
module MigrationId =

    /// A validated identifier.
    let create (text: string) =
        if Identifier.valid 1 text && text.Length <= 64 then Ok(MigrationId text) else Error text

    /// The identifier's text.
    let value (MigrationId text) = text

/// How a schema migration changes one record; `Ok` for a relocation. It must
/// keep the record's id and type.
type RecordTransform = Record -> Result<Record, string>

/// An explicit, versioned migration of one namespace (ARCA-MIG-002) from its
/// source location to a target location: a relocation (ARCA-LOC-009), a
/// schema migration, or both. The target is a different location (another
/// repository, branch or base path); the source is never changed until it is
/// explicitly retired.
[<NoEquality; NoComparison>]
type MigrationPlan =
    { Id: MigrationId
      Source: Namespace
      Target: Namespace
      /// The target manifest's schema versions for the record types the
      /// transform upgrades; other types keep the source's versions.
      RecordSchemas: Map<string, int>
      Transform: RecordTransform
      Actor: Actor
      /// Changes per commit while copying, 1 to 500.
      BatchSize: int }

/// Why a migration step did not complete. Every step can be run again: the
/// migration resumes from the phase the target manifest records.
[<RequireQualifiedAccess>]
type MigrationError =
    | InvalidPlan of reason: string
    | SourceManifestMissing
    | ManifestCorrupt of location: string * DecodeError
    /// The source is not usable as configured (wrong application, relocated,
    /// already being migrated or retired).
    | SourceNotReady of ManifestProblem list
    /// The target holds data that this migration did not write.
    | TargetInUse of reason: string
    | Snapshot of SnapshotError
    | Provider of StorageFailure
    | InvalidSourceRecord of path: string * DecodeError
    | TransformFailed of path: string * reason: string
    /// The target still differs from the source after every copy round,
    /// because the source kept changing.
    | Unverified of paths: string list
    /// Retirement: the target is not an active copy made by this migration.
    | NotActivated
    /// Retirement: the source changed since the target was activated; the
    /// differences must be reconciled before the source is retired.
    | SourceChanged of paths: string list

/// What a completed migration did.
type MigrationReport =
    { Id: string
      /// Objects written to the target, over every run.
      Written: int
      Commits: int
      /// The source state the target was verified against.
      VerifiedSource: ChangeToken }

/// The migration workflow (ARCA-MIG-002, ARCA-LOC-009): validate, copy,
/// verify, activate, then an explicit retirement of the source. Progress is
/// recorded in the target manifest, so every step is resumable and idempotent:
/// running a migration again continues where it stopped, and running a
/// finished one changes nothing. While a migration is in progress the target
/// manifest reports it (`ManifestProblem.MigrationInProgress`), so no
/// application uses half-copied data. Applications should stop writing to
/// the source while it is migrated; writes that still land are detected,
/// copied again in another round, or reported at retirement.
[<RequireQualifiedAccess>]
module Migration =

    /// How many copy-and-verify rounds a run makes before reporting Unverified.
    [<Literal>]
    let MaxRounds = 3

    /// The largest batch accepted.
    [<Literal>]
    let MaxBatchSize = 500

    let private manifestPath =
        match Layout.manifestPath with
        | Ok path -> path
        | Error error -> invalidOp ("internal: manifest path: " + LocationError.describe error)

    let private sameOwner (plan: MigrationPlan) =
        plan.Source.Application = plan.Target.Application && plan.Source.Dataset = plan.Target.Dataset

    /// Ok when the plan describes a migration Arca can run.
    let validate (plan: MigrationPlan) =
        if not (sameOwner plan) then
            Error(MigrationError.InvalidPlan "the source and target must be the same application and dataset")
        elif plan.Source.Location = plan.Target.Location then
            Error(MigrationError.InvalidPlan "the target must be a different location from the source")
        elif plan.BatchSize < 1 || plan.BatchSize > MaxBatchSize then
            Error(MigrationError.InvalidPlan $"the batch size must be 1 to {MaxBatchSize}")
        elif plan.RecordSchemas |> Map.exists (fun _ version -> version < 1) then
            Error(MigrationError.InvalidPlan "schema versions start at 1")
        else
            Ok()

    /// What the target must hold for a source snapshot: every object except
    /// the manifest and derived data (rebuilt at the target), with records
    /// transformed. Objects that are not records are copied byte for byte.
    let expected (plan: MigrationPlan) (source: Snapshot) =
        let rec collect (objects: StoredObject list) acc =
            match objects with
            | [] -> Ok(List.rev acc)
            | item :: rest when item.Path = manifestPath -> collect rest acc
            | item :: rest when Layout.authorityOf item.Path = Some Authority.Derived -> collect rest acc
            | item :: rest ->
                let path = RelativePath.render item.Path

                match Layout.keyOf item.Path with
                | None -> collect rest ((item.Path, item.Content) :: acc)
                | Some key ->
                    match Record.decode Record.DefaultMaxBytes item.Content with
                    | Error error -> Error(MigrationError.InvalidSourceRecord(path, error))
                    | Ok record when record.Id <> key.Id || record.Type <> key.Type ->
                        Error(MigrationError.TransformFailed(path, "the stored record is not the one its path names"))
                    | Ok record ->
                        match plan.Transform record with
                        | Error reason -> Error(MigrationError.TransformFailed(path, reason))
                        | Ok changed when changed.Id <> record.Id || changed.Type <> record.Type ->
                            Error(MigrationError.TransformFailed(path, "the transform changed the record's id or type"))
                        | Ok changed ->
                            match Record.encode Record.DefaultMaxBytes changed with
                            | Error _ -> Error(MigrationError.TransformFailed(path, "the transformed record is too large"))
                            | Ok content -> collect rest ((item.Path, content) :: acc)

        collect source.Objects []

    /// The changes that make the target hold exactly `wanted` (besides its
    /// manifest), each conditioned on the target's current revisions.
    let differences (wanted: (RelativePath * string) list) (target: Snapshot) =
        let existing =
            target.Objects
            |> List.filter (fun item -> item.Path <> manifestPath)
            |> List.map (fun item -> item.Path, item)
            |> Map.ofList

        let wantedPaths = wanted |> List.map fst |> Set.ofList

        let writes =
            wanted
            |> List.choose (fun (path, content) ->
                match existing |> Map.tryFind path with
                | None -> Some(Change.Create(path, content))
                | Some item when item.Content = content -> None
                | Some item -> Some(Change.Update(path, content, item.Revision)))

        let deletes =
            existing
            |> Map.toList
            |> List.filter (fun (path, _) -> not (wantedPaths.Contains path))
            |> List.map (fun (path, item) -> Change.Delete(path, item.Revision))

        writes @ deletes

    /// The target manifest for a phase: the source's, at the target location,
    /// with the plan's schema versions and the migration's state.
    let targetManifest (plan: MigrationPlan) (source: Manifest) (phase: MigrationPhase) =
        { source with
            Location = plan.Target.Location
            RecordSchemas = plan.RecordSchemas |> Map.fold (fun schemas name version -> Map.add name version schemas) source.RecordSchemas
            Migration =
                Some
                    { MigrationId = MigrationId.value plan.Id
                      Phase = phase } }

    let private metadata (plan: MigrationPlan) (summary: string) (changes: Change list) =
        let fingerprint =
            changes
            |> List.map (fun change ->
                let content = Change.content change |> Option.defaultValue ""
                let expected = Change.expected change |> Option.map (fun (Revision value) -> value) |> Option.defaultValue ""
                Json.Array [ Json.String(RelativePath.render (Change.path change)); Json.String content; Json.String expected ])
            |> Json.Array
            |> Json.contentHash

        let id = MigrationId.value plan.Id
        let key = $"mig-{id}-{fingerprint.Substring(7, 16)}"

        match CorrelationId.create id, IdempotencyKey.create key with
        | Ok correlation, Ok key ->
            Ok
                { Summary = $"migration {id}: {summary}"
                  Actor = plan.Actor
                  ProviderIdentity = None
                  ExecutionId = None
                  CorrelationId = correlation
                  IdempotencyKey = key }
        | _ -> Error(MigrationError.InvalidPlan "the migration id does not form valid commit metadata")

    /// Commits one batch, reconciling an unknown outcome instead of guessing.
    let private commit (provider: StorageProvider) (ns: Namespace) (plan: MigrationPlan) (summary: string) (changes: Change list) =
        async {
            match metadata plan summary changes |> Result.bind (fun meta -> Operation.create ns meta changes |> Result.mapError (fun _ -> MigrationError.InvalidPlan "a write is not a valid operation")) with
            | Error error -> return Error error
            | Ok operation ->
                match! provider.Commit operation with
                | Ok _ -> return Ok()
                | Error(StorageFailure.OutcomeUnknown pending) ->
                    match! provider.Reconcile ns pending with
                    | Ok(ReconcileOutcome.Landed _) -> return Ok()
                    | Ok _ -> return Error(MigrationError.Provider(StorageFailure.OutcomeUnknown pending))
                    | Error failure -> return Error(MigrationError.Provider failure)
                | Error failure -> return Error(MigrationError.Provider failure)
        }

    let private readManifest (provider: StorageProvider) (ns: Namespace) (where: string) =
        async {
            match! provider.Read ns manifestPath with
            | Error failure -> return Error(MigrationError.Provider failure)
            | Ok ReadOutcome.Absent -> return Ok None
            | Ok(ReadOutcome.Found stored) ->
                match Manifest.decode stored.Content with
                | Error error -> return Error(MigrationError.ManifestCorrupt(where, error))
                | Ok manifest -> return Ok(Some(manifest, stored.Revision))
        }

    let private writeManifest provider ns plan (manifest: Manifest) (current: Revision option) (summary: string) =
        let content = Manifest.encode manifest

        let change =
            match current with
            | Some revision -> Change.Update(manifestPath, content, revision)
            | None -> Change.Create(manifestPath, content)

        commit provider ns plan summary [ change ]

    let private snapshot provider ns =
        async {
            match! Snapshot.take provider ns 3 with
            | Error error -> return Error(MigrationError.Snapshot error)
            | Ok taken -> return Ok taken
        }

    let private setPhase target plan source phase summary =
        async {
            match! readManifest target plan.Target "target" with
            | Error error -> return Error error
            | Ok current ->
                let wanted = targetManifest plan source phase

                match current with
                | Some(manifest, _) when manifest = wanted -> return Ok()
                | _ -> return! writeManifest target plan.Target plan wanted (current |> Option.map snd) summary
        }

    /// Validates the plan and both manifests; the source manifest, and the
    /// phase to resume from (None when the migration already completed).
    let private prepare (source: StorageProvider) (target: StorageProvider) (plan: MigrationPlan) =
        async {
            match validate plan with
            | Error error -> return Error error
            | Ok() ->
                match! readManifest source plan.Source "source" with
                | Error error -> return Error error
                | Ok None -> return Error MigrationError.SourceManifestMissing
                | Ok(Some(manifest, _)) ->
                    match Manifest.check plan.Source manifest with
                    | _ :: _ as problems -> return Error(MigrationError.SourceNotReady problems)
                    | [] ->
                        match! readManifest target plan.Target "target" with
                        | Error error -> return Error error
                        | Ok(Some(found, _)) ->
                            match found.Migration with
                            | Some state when state.MigrationId = MigrationId.value plan.Id && found.Application = manifest.Application ->
                                return Ok(manifest, Some state.Phase)
                            | _ -> return Error(MigrationError.TargetInUse "the target holds another manifest")
                        | Ok None ->
                            match! snapshot target plan.Target with
                            | Error error -> return Error error
                            | Ok existing when not existing.Objects.IsEmpty -> return Error(MigrationError.TargetInUse "the target holds data but no manifest")
                            | Ok _ ->
                                match! setPhase target plan manifest MigrationPhase.Validating "validate" with
                                | Error error -> return Error error
                                | Ok() -> return Ok(manifest, Some MigrationPhase.Validating)
        }

    /// Runs (or resumes) the migration up to activation: validate the source
    /// and the transform, copy in batches, verify that the target holds
    /// exactly what the source implies, then activate the target. The source
    /// is not changed.
    let run (source: StorageProvider) (target: StorageProvider) (plan: MigrationPlan) =
        let rec round number (written: int) (commits: int) (sourceManifest: Manifest) =
            async {
                match! snapshot source plan.Source with
                | Error error -> return Error error
                | Ok sourceState ->
                    match expected plan sourceState with
                    | Error error -> return Error error
                    | Ok wanted ->
                        match! setPhase target plan sourceManifest MigrationPhase.Copying "copy" with
                        | Error error -> return Error error
                        | Ok() ->
                            match! snapshot target plan.Target with
                            | Error error -> return Error error
                            | Ok targetState ->
                                let changes = differences wanted targetState
                                let batches = changes |> List.chunkBySize plan.BatchSize

                                let rec copy (pending: Change list list) =
                                    async {
                                        match pending with
                                        | [] -> return Ok()
                                        | batch :: rest ->
                                            match! commit target plan.Target plan $"copy {batch.Length} objects" batch with
                                            | Error error -> return Error error
                                            | Ok() -> return! copy rest
                                    }

                                match! copy batches with
                                | Error error -> return Error error
                                | Ok() ->
                                    let written = written + changes.Length
                                    let commits = commits + batches.Length

                                    match! setPhase target plan sourceManifest MigrationPhase.Verifying "verify" with
                                    | Error error -> return Error error
                                    | Ok() ->
                                        match! snapshot source plan.Source with
                                        | Error error -> return Error error
                                        | Ok verifiedSource ->
                                            match expected plan verifiedSource, Snapshot.take target plan.Target 3 with
                                            | Error error, _ -> return Error error
                                            | Ok wanted, takeTarget ->
                                                match! takeTarget with
                                                | Error error -> return Error(MigrationError.Snapshot error)
                                                | Ok copied ->
                                                    match differences wanted copied with
                                                    | [] ->
                                                        match! setPhase target plan sourceManifest MigrationPhase.Completed "activate" with
                                                        | Error error -> return Error error
                                                        | Ok() ->
                                                            return
                                                                Ok
                                                                    { Id = MigrationId.value plan.Id
                                                                      Written = written
                                                                      Commits = commits
                                                                      VerifiedSource = verifiedSource.ChangeToken }
                                                    | remaining when number >= MaxRounds ->
                                                        return Error(MigrationError.Unverified(remaining |> List.map (Change.path >> RelativePath.render)))
                                                    | _ -> return! round (number + 1) written commits sourceManifest
            }

        async {
            match! prepare source target plan with
            | Error error -> return Error error
            | Ok(_, Some MigrationPhase.Completed) ->
                match! snapshot source plan.Source with
                | Error error -> return Error error
                | Ok state ->
                    return
                        Ok
                            { Id = MigrationId.value plan.Id
                              Written = 0
                              Commits = 0
                              VerifiedSource = state.ChangeToken }
            | Ok(_, Some MigrationPhase.Retired) -> return Error(MigrationError.TargetInUse "the target is a retired copy")
            | Ok(manifest, _) -> return! round 1 0 0 manifest
        }

    /// Explicitly retires the source after activation: checks that the target
    /// still holds exactly what the source implies, then marks the source
    /// manifest Retired, conditioned on the source not having changed since
    /// that check. Applications then refuse the source (`ManifestProblem.Retired`).
    /// Its objects stay in place until removed by hand. Retiring twice is a no-op.
    let retire (source: StorageProvider) (target: StorageProvider) (plan: MigrationPlan) =
        async {
            match validate plan with
            | Error error -> return Error error
            | Ok() ->
                match! readManifest source plan.Source "source" with
                | Error error -> return Error error
                | Ok None -> return Error MigrationError.SourceManifestMissing
                | Ok(Some(manifest, _)) when manifest.Migration = Some { MigrationId = MigrationId.value plan.Id; Phase = MigrationPhase.Retired } -> return Ok()
                | Ok(Some(manifest, revision)) ->
                    match! readManifest target plan.Target "target" with
                    | Error error -> return Error error
                    | Ok(Some(found, _)) when found.Migration = Some { MigrationId = MigrationId.value plan.Id; Phase = MigrationPhase.Completed } ->
                        match! snapshot source plan.Source with
                        | Error error -> return Error error
                        | Ok sourceState ->
                            match expected plan sourceState with
                            | Error error -> return Error error
                            | Ok wanted ->
                                match! snapshot target plan.Target with
                                | Error error -> return Error error
                                | Ok targetState ->
                                    match differences wanted targetState with
                                    | _ :: _ as changed -> return Error(MigrationError.SourceChanged(changed |> List.map (Change.path >> RelativePath.render)))
                                    | [] ->
                                        let retired =
                                            { manifest with
                                                Migration =
                                                    Some
                                                        { MigrationId = MigrationId.value plan.Id
                                                          Phase = MigrationPhase.Retired } }

                                        match metadata plan "retire the source" [] with
                                        | Error error -> return Error error
                                        | Ok meta ->
                                            match Operation.create plan.Source meta [ Change.Update(manifestPath, Manifest.encode retired, revision) ] with
                                            | Error _ -> return Error(MigrationError.InvalidPlan "the retirement is not a valid operation")
                                            | Ok operation ->
                                                match! source.Commit(Operation.requireChangeToken sourceState.ChangeToken operation) with
                                                | Ok _ -> return Ok()
                                                | Error(StorageFailure.StaleChangeToken _) -> return Error(MigrationError.SourceChanged [])
                                                | Error failure -> return Error(MigrationError.Provider failure)
                    | Ok _ -> return Error MigrationError.NotActivated
        }
