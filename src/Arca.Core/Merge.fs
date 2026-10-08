namespace Arca

/// One object's state in a record set: its content hash and whether it may change.
type Entry =
    { Hash: string
      Mutability: Mutability }

/// A record set: every object's entry, by namespace-relative path text.
type RecordSet = Map<string, Entry>

/// Why one path could not be merged (ARCA-CON-003). Arca reports; the
/// application decides.
[<RequireQualifiedAccess>]
type MergeConflict =
    /// Both sides changed the same object differently: an edit/edit,
    /// add/add or edit/delete divergence.
    | Divergent of path: string * ancestor: Entry option * ours: Entry option * theirs: Entry option
    /// A side changed or deleted an object declared immutable (ARCA-INT-003).
    | ImmutableChanged of path: string * ancestor: Entry * ours: Entry option * theirs: Entry option

/// The result of a three-way merge. `Merged` holds every path that resolved
/// cleanly; a conflicting path keeps `theirs` (the current, authoritative
/// state), so nothing on the authoritative side is overwritten while the
/// application decides. A clean result is a storage fact only: it is not
/// domain correctness, and the application reruns its validation
/// (ARCA-CON-002).
type MergeResult =
    { Merged: RecordSet
      Conflicts: MergeConflict list }

/// Three-way merge over record sets (ARCA-CON-003).
///
/// For each path, with `a` the common ancestor, `o` ours and `t` theirs:
/// - `o = t`: that value (both sides agree, including both absent);
/// - `o = a`: theirs (only they changed it);
/// - `t = a`: ours (only we changed it);
/// - otherwise a Divergent conflict.
/// Any change to an object whose ancestor is immutable is an
/// ImmutableChanged conflict, even when both sides agree. Distinct paths never
/// interact, so concurrent additions of distinct records commute.
[<RequireQualifiedAccess>]
module Merge =

    let private resolve path (ancestor: Entry option) (ours: Entry option) (theirs: Entry option) =
        match ancestor with
        | Some a when a.Mutability = Mutability.Immutable && (ours <> ancestor || theirs <> ancestor) ->
            Error(MergeConflict.ImmutableChanged(path, a, ours, theirs))
        | _ ->
            if ours = theirs then Ok ours
            elif ours = ancestor then Ok theirs
            elif theirs = ancestor then Ok ours
            else Error(MergeConflict.Divergent(path, ancestor, ours, theirs))

    /// Merges `ours` and `theirs` against their common `ancestor`.
    let threeWay (ancestor: RecordSet) (ours: RecordSet) (theirs: RecordSet) =
        let paths =
            [ ancestor; ours; theirs ]
            |> List.collect (Map.keys >> List.ofSeq)
            |> List.distinct
            |> List.sort

        paths
        |> List.fold
            (fun (merged, conflicts) path ->
                let a = Map.tryFind path ancestor
                let o = Map.tryFind path ours
                let t = Map.tryFind path theirs

                match resolve path a o t with
                | Ok(Some entry) -> Map.add path entry merged, conflicts
                | Ok None -> merged, conflicts
                | Error conflict ->
                    let kept =
                        match t with
                        | Some entry -> Map.add path entry merged
                        | None -> merged

                    kept, conflict :: conflicts)
            (Map.empty, [])
        |> fun (merged, conflicts) ->
            { Merged = merged
              Conflicts = List.rev conflicts }

    /// The path a conflict is about.
    let conflictPath =
        function
        | MergeConflict.Divergent(path, _, _, _)
        | MergeConflict.ImmutableChanged(path, _, _, _) -> path
