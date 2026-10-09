namespace Arca

/// Every object a namespace holds, as of one change token: a consistent view
/// of the provider's state, for indexes, export and migration.
type Snapshot =
    { Namespace: Namespace
      ChangeToken: ChangeToken
      /// Every live object, relative to the namespace, ordered by path (ordinal).
      Objects: StoredObject list
      /// Every erased record's tombstone, ordered by path (ARCA-INT-005). Not
      /// in `Objects`, so indexes never read them; exports and migrations
      /// carry them, so the records cannot be recreated.
      Erased: ErasedObject list }

/// Why a snapshot could not be taken.
[<RequireQualifiedAccess>]
type SnapshotError =
    | Provider of StorageFailure
    /// The provider returned a partial listing for this folder; a snapshot is
    /// never built on part of the data (ARCA-API-004).
    | Incomplete of folder: string
    /// The namespace kept changing while it was read, on every attempt.
    | Unstable of attempts: int

/// Consistent walks over a namespace through the provider-neutral interface.
[<RequireQualifiedAccess>]
module Snapshot =

    /// True when a path belongs to the namespace itself. An application
    /// namespace's `datasets/` folder holds its datasets' own namespaces,
    /// which are walked separately (ARCA-LOC-005).
    let owns (ns: Namespace) (path: RelativePath) =
        match ns.Dataset, RelativePath.segments path |> List.map Segment.value with
        | None, first :: _ when first = Namespace.DatasetsFolder -> false
        | _ -> true

    let private walk (provider: StorageProvider) (ns: Namespace) =
        let byPath (path: 'a -> RelativePath) (items: 'a list) =
            items |> List.sortWith (fun a b -> System.String.CompareOrdinal(RelativePath.render (path a), RelativePath.render (path b)))

        let rec folder (pending: RelativePath list) (found: StoredObject list, erased: ErasedObject list) =
            async {
                match pending with
                | [] -> return Ok(byPath (fun (item: StoredObject) -> item.Path) found, byPath (fun (item: ErasedObject) -> item.Path) erased)
                | prefix :: rest ->
                    match! provider.List ns prefix with
                    | Error failure -> return Error(SnapshotError.Provider failure)
                    | Ok listing when not listing.Complete -> return Error(SnapshotError.Incomplete(RelativePath.render prefix))
                    | Ok listing ->
                        let entries = listing.Entries |> List.filter (fun entry -> owns ns entry.Path)
                        let folders = entries |> List.filter _.IsFolder |> List.map _.Path
                        let files = entries |> List.filter (fun entry -> not entry.IsFolder) |> List.map _.Path

                        let rec read (paths: RelativePath list) (found: StoredObject list, erased: ErasedObject list) =
                            async {
                                match paths with
                                | [] -> return Ok(found, erased)
                                | path :: more ->
                                    match! provider.Read ns path with
                                    | Error failure -> return Error(SnapshotError.Provider failure)
                                    // Removed since it was listed: the token check below retries.
                                    | Ok ReadOutcome.Absent -> return! read more (found, erased)
                                    | Ok(ReadOutcome.Found stored) -> return! read more ({ stored with Path = path } :: found, erased)
                                    | Ok(ReadOutcome.Erased gone) ->
                                        return! read more (found, { gone with Path = path; Stored = { gone.Stored with Path = path } } :: erased)
                            }

                        match! read files (found, erased) with
                        | Error error -> return Error error
                        | Ok gathered -> return! folder (folders @ rest) gathered
            }

        folder [ RelativePath.empty ] ([], [])

    /// Every object the namespace holds, read between two equal change tokens
    /// so the view is consistent; retried up to `attempts` times while the
    /// namespace keeps changing.
    let take (provider: StorageProvider) (ns: Namespace) (attempts: int) =
        let rec attempt remaining =
            async {
                if remaining <= 0 then
                    return Error(SnapshotError.Unstable attempts)
                else
                    match! provider.ChangeToken ns with
                    | Error failure -> return Error(SnapshotError.Provider failure)
                    | Ok before ->
                        match! walk provider ns with
                        | Error error -> return Error error
                        | Ok(objects, erased) ->
                            match! provider.ChangeToken ns with
                            | Error failure -> return Error(SnapshotError.Provider failure)
                            | Ok after when after = before ->
                                return
                                    Ok
                                        { Namespace = ns
                                          ChangeToken = before
                                          Objects = objects
                                          Erased = erased }
                            | Ok _ -> return! attempt (remaining - 1)
            }

        attempt attempts

    /// The object at a path, if the snapshot holds one.
    let find (path: RelativePath) (snapshot: Snapshot) =
        snapshot.Objects |> List.tryFind (fun item -> item.Path = path)
