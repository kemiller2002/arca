namespace Arca

open System

/// A queue store in memory (ARCA-OFF-002). It persists exactly what a durable
/// store persists, the queue's canonical text checked against a budget and
/// the namespace, so it behaves like one in every respect but durability:
/// nothing it holds survives closing the tab. It serves tests, and it is the
/// `MemoryOnly` durability mode an application falls back to when no browser
/// storage is usable (Limen LCP-065).
[<RequireQualifiedAccess>]
module MemoryQueueStore =

    /// What a memory store holds, so a harness can inspect or replace it.
    [<NoEquality; NoComparison>]
    type Cell =
        { mutable Text: string option
          mutable Unavailable: bool }

    /// A fresh, empty cell.
    let cell () = { Text = None; Unavailable = false }

    /// The queue as stored text within a budget (UTF-16 code units of its
    /// canonical text), or the typed failure. Nothing is truncated or dropped.
    let encodeWithin (budget: int64) (queue: OfflineQueue) =
        match OfflineQueue.encode queue with
        | Error error -> Error(QueueStoreFailure.Corrupt error)
        | Ok text when int64 text.Length > budget -> Error(QueueStoreFailure.QuotaExceeded(int64 text.Length, budget))
        | Ok text -> Ok text

    /// A stored text as the namespace's queue: refused as Corrupt when it is
    /// not a queue this Arca reads or holds another namespace's entries.
    let decodeFor (ns: Namespace) (text: string) =
        match OfflineQueue.decode text with
        | Error error -> Error(QueueStoreFailure.Corrupt error)
        | Ok queue when queue.Entries |> List.forall (fun entry -> OfflineQueue.belongsTo ns entry.Operation) -> Ok queue
        | Ok _ -> Error(QueueStoreFailure.Corrupt(QueueError.Corrupt "the stored queue holds another namespace's entries"))

    /// A queue store over a cell.
    let over (cell: Cell) (budget: int64) (ns: Namespace) : QueueStore =
        { Load =
            fun () ->
                async {
                    if cell.Unavailable then
                        return Error QueueStoreFailure.Unavailable
                    else
                        match cell.Text with
                        | None -> return Ok None
                        | Some text -> return decodeFor ns text |> Result.map Some
                }
          Save =
            fun queue ->
                async {
                    if cell.Unavailable then
                        return Error QueueStoreFailure.Unavailable
                    else
                        match encodeWithin budget queue with
                        | Error failure -> return Error failure
                        | Ok text ->
                            cell.Text <- Some text
                            return Ok()
                } }

    /// A queue store over a fresh cell of its own.
    let create (budget: int64) (ns: Namespace) = over (cell ()) budget ns

/// What a queue-store harness is asked to arrange.
[<RequireQualifiedAccess>]
type QueueStoreFault =
    /// Storage stops answering: loads and saves fail.
    | Unavailable
    /// Storage holds this text for the namespace, as if something other than
    /// this store wrote it (another Arca version, another application, a
    /// manual edit).
    | Stored of text: string

/// One queue store under test, fresh and empty, with the means to act on its
/// storage from outside the port.
[<NoEquality; NoComparison>]
type QueueStoreSubject =
    { Namespace: Namespace
      /// The store under test. It is the only writer of its storage.
      Store: QueueStore
      /// Another store over the same storage, as after a reload. Called only
      /// once the store under test has stopped writing.
      Reopen: unit -> Async<QueueStore>
      /// The budget the store was given, in UTF-16 code units of the queue's
      /// canonical text (`OfflineQueue.encode`).
      Budget: int64
      /// Arranges a fault; false when the harness cannot produce it, which is
      /// reported as Unsupported, never as passed.
      Arrange: QueueStoreFault -> Async<bool> }

/// The queue-store conformance suite (WI-0019; ARCA-OFF-002; Limen LCP-046,
/// LCP-060, LCP-075): one executable contract for every `QueueStore`, the
/// in-memory and localStorage stores and the IndexedDB adapter alike. Each
/// case gets a fresh subject and goes through the port only.
[<RequireQualifiedAccess>]
module QueueStoreConformance =

    let private fixture result =
        match result with
        | Ok value -> value
        | Error _ -> invalidOp "queue-store conformance fixture"

    let private metadata (key: string) summary =
        { Summary = summary
          Actor =
            { Kind = ActorKind.Service
              Id = ActorId.create "arca/queue-conformance" |> fixture }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"queue-conformance-{key}" |> fixture
          IdempotencyKey = IdempotencyKey.create $"queue-conformance-{key}" |> fixture }

    let private operation (ns: Namespace) (key: string) (content: string) =
        Operation.create ns (metadata key $"queued {key}") [ Change.Create(RelativePath.parse $"notes/{key}.json" |> fixture, content) ]
        |> fixture

    let private at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)

    /// A queue of `keys` in `ns`, each entry in the matching state.
    let queueOf (ns: Namespace) (entries: (string * EntryState) list) =
        let queue =
            entries
            |> List.fold
                (fun queue (key, _) -> OfflineQueue.enqueue at (operation ns key $"{{\"key\":\"{key}\"}}") queue |> fixture |> fst)
                (OfflineQueue.create OfflinePolicy.QueueWrites)

        { queue with Entries = List.map2 (fun entry (_, state) -> { entry with State = state }) queue.Entries entries }

    /// Queues that between them hold every entry state, both policies, an
    /// empty queue and text that needs escaping.
    let samples (ns: Namespace) =
        [ OfflineQueue.create OfflinePolicy.QueueWrites
          OfflineQueue.create OfflinePolicy.ReadOnlyWhenOffline
          queueOf ns [ "a", EntryState.Pending ]
          queueOf
              ns
              [ "s", EntryState.Synchronized "t-1"
                "f", EntryState.InFlight "t-1"
                "u", EntryState.OutcomeUnknown "t-2"
                "c", EntryState.Conflicted [ "notes/c.json"; "x/\"y\"" ]
                "r", EntryState.Refused "refused \"quoted\"\n"
                "x", EntryState.Abandoned "é 😀"
                "p", EntryState.Pending ] ]

    let private failure =
        function
        | QueueStoreFailure.Unavailable -> "Unavailable"
        | QueueStoreFailure.QuotaExceeded(bytes, budget) -> $"QuotaExceeded({bytes}, {budget})"
        | QueueStoreFailure.Corrupt _ -> "Corrupt"

    let private loaded (result: Result<OfflineQueue option, QueueStoreFailure>) =
        match result with
        | Ok None -> "nothing"
        | Ok(Some queue) -> $"a queue of {queue.Entries.Length} entries"
        | Error error -> failure error

    let private arranged (subject: QueueStoreSubject) fault (body: Async<ConformanceOutcome>) =
        async {
            match! subject.Arrange fault with
            | true -> return! body
            | false -> return ConformanceOutcome.Unsupported "the harness cannot arrange this fault"
        }

    /// Saves `queue` through `store` and loads it back, then through a
    /// reopened store; answers the verdict and the reopened store, which is
    /// the one to keep writing through (the old one has stopped).
    let private roundTripThrough (subject: QueueStoreSubject) (store: QueueStore) (queue: OfflineQueue) =
        async {
            match! store.Save queue with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}", store
            | Ok() ->
                let! direct = store.Load()
                let! reopened = subject.Reopen()
                let! reloaded = reopened.Load()

                let verdict =
                    match direct, reloaded with
                    | Ok(Some a), Ok(Some b) when a = queue && b = queue -> ConformanceOutcome.Passed
                    | Ok(Some a), _ when a <> queue -> ConformanceOutcome.Failed "the loaded queue differs from the saved one"
                    | _, Ok(Some _) -> ConformanceOutcome.Failed "the reopened store's queue differs from the saved one"
                    | _ -> ConformanceOutcome.Failed $"load gave {loaded direct}, reopened gave {loaded reloaded}"

                return verdict, reopened
        }

    /// Saving `queue` then loading it, through the store and through a
    /// reopened one, gives back exactly `queue`: order, sequences, states and
    /// policy (LCP-060). Exposed for property tests over generated queues.
    let roundTrip (subject: QueueStoreSubject) (queue: OfflineQueue) =
        async {
            let! verdict, _ = roundTripThrough subject subject.Store queue
            return verdict
        }

    /// A revised entry keeps the account that made it, its sequence and its
    /// enqueue time, through a save and a reload (ARCA-OFF-007): otherwise it
    /// no longer matches its account at sign-out.
    let private revisedKeepsAccount (subject: QueueStoreSubject) =
        async {
            let account = AccountId.ProviderSubject("github", "1001")

            let queued, sequence =
                OfflineQueue.enqueueFor account at (operation subject.Namespace "r1" "{\"key\":\"r1\"}") (OfflineQueue.create OfflinePolicy.QueueWrites)
                |> fixture

            let conflicted =
                { queued with Entries = queued.Entries |> List.map (fun entry -> { entry with State = EntryState.Conflicted [ "notes/r1.json" ] }) }

            match OfflineQueue.revise sequence (operation subject.Namespace "r2" "{\"key\":\"r2\"}") conflicted with
            | Error error -> return ConformanceOutcome.Failed $"revise: {error}"
            | Ok revised ->
                let before = conflicted.Entries.Head
                let after = revised.Entries.Head

                if after.Operation.AccountId <> Some(AccountId.toWire account) then
                    return ConformanceOutcome.Failed "revise dropped the entry's account"
                elif after.Sequence <> before.Sequence || after.EnqueuedAt <> before.EnqueuedAt then
                    return ConformanceOutcome.Failed "revise changed the entry's sequence or enqueue time"
                else
                    match! roundTripThrough subject subject.Store revised with
                    | ConformanceOutcome.Passed, _ -> return ConformanceOutcome.Passed
                    | other, _ -> return other
        }

    let private absent (subject: QueueStoreSubject) =
        async {
            match! subject.Store.Load() with
            | Ok None -> return ConformanceOutcome.Passed
            | other -> return ConformanceOutcome.Failed $"a fresh store loaded {loaded other}"
        }

    let private roundTrips (subject: QueueStoreSubject) =
        async {
            let rec each (store: QueueStore) queues =
                async {
                    match queues with
                    | [] -> return ConformanceOutcome.Passed
                    | queue :: rest ->
                        match! roundTripThrough subject store queue with
                        | ConformanceOutcome.Passed, reopened -> return! each reopened rest
                        | other, _ -> return other
                }

            return! each subject.Store (samples subject.Namespace)
        }

    let private replaces (subject: QueueStoreSubject) =
        async {
            let first = queueOf subject.Namespace [ "a", EntryState.Pending; "b", EntryState.Pending ]
            let second = { first with Entries = first.Entries |> List.tail }

            match! subject.Store.Save first with
            | Error error -> return ConformanceOutcome.Failed $"first save: {failure error}"
            | Ok() ->
                match! subject.Store.Save second with
                | Error error -> return ConformanceOutcome.Failed $"second save: {failure error}"
                | Ok() ->
                    let! reopened = subject.Reopen()

                    match! reopened.Load() with
                    | Ok(Some queue) when queue = second -> return ConformanceOutcome.Passed
                    | other -> return ConformanceOutcome.Failed $"after replacing, a reload gave {loaded other}"
        }

    let private overBudget (subject: QueueStoreSubject) =
        async {
            let kept = queueOf subject.Namespace [ "kept", EntryState.Pending ]
            let large = String('x', int (min subject.Budget (int64 Int32.MaxValue - 1L)) + 1)

            let tooLarge =
                OfflineQueue.enqueue at (operation subject.Namespace "large" large) kept |> fixture |> fst

            match! subject.Store.Save kept with
            | Error error -> return ConformanceOutcome.Failed $"saving a small queue: {failure error}"
            | Ok() ->
                match! subject.Store.Save tooLarge with
                | Error(QueueStoreFailure.QuotaExceeded(bytes, budget)) when bytes > budget ->
                    let! reopened = subject.Reopen()

                    match! reopened.Load() with
                    | Ok(Some queue) when queue = kept -> return ConformanceOutcome.Passed
                    | other -> return ConformanceOutcome.Failed $"after the refused save, a reload gave {loaded other}, not the last saved queue"
                | Error error -> return ConformanceOutcome.Failed $"over budget gave {failure error}, not QuotaExceeded"
                | Ok() -> return ConformanceOutcome.Failed "a queue over the budget was saved"
        }

    let private foreign (subject: QueueStoreSubject) =
        let other =
            Namespace.ofApplication
                { Application = AppId.create "queue-conformance-other" |> fixture
                  Environment = { Kind = EnvironmentKind.Test; Name = "queue-conformance" }
                  Location = subject.Namespace.Location }
            |> fixture

        let text = queueOf other [ "theirs", EntryState.Pending ] |> OfflineQueue.encode |> fixture

        arranged
            subject
            (QueueStoreFault.Stored text)
            (async {
                match! subject.Store.Load() with
                | Error(QueueStoreFailure.Corrupt _) -> return ConformanceOutcome.Passed
                | other -> return ConformanceOutcome.Failed $"another namespace's queue loaded as {loaded other}"
            })

    let private corrupt (subject: QueueStoreSubject) =
        arranged
            subject
            (QueueStoreFault.Stored "{\"arcaQueue\":999}")
            (async {
                match! subject.Store.Load() with
                | Error(QueueStoreFailure.Corrupt _) -> return ConformanceOutcome.Passed
                | other -> return ConformanceOutcome.Failed $"an unreadable queue loaded as {loaded other}"
            })

    let private unavailable (subject: QueueStoreSubject) =
        arranged
            subject
            QueueStoreFault.Unavailable
            (async {
                let! load = subject.Store.Load()
                let! save = subject.Store.Save(queueOf subject.Namespace [ "a", EntryState.Pending ])

                match load, save with
                | Error QueueStoreFailure.Unavailable, Error QueueStoreFailure.Unavailable -> return ConformanceOutcome.Passed
                | _, Ok() -> return ConformanceOutcome.Failed "a save succeeded on unavailable storage"
                | _ -> return ConformanceOutcome.Failed $"unavailable storage loaded {loaded load}"
            })

    let private credentialFree (subject: QueueStoreSubject) =
        async {
            let queue = queueOf subject.Namespace [ "a", EntryState.Pending ]
            let token = "ghp_" + String('A', 36)

            let leaking =
                { queue with
                    Entries = queue.Entries |> List.map (fun entry -> { entry with Operation = { entry.Operation with Summary = token } }) }

            match! subject.Store.Save leaking with
            | Error(QueueStoreFailure.Corrupt _) ->
                let! reopened = subject.Reopen()

                match! reopened.Load() with
                | Ok None -> return ConformanceOutcome.Passed
                | other -> return ConformanceOutcome.Failed $"after refusing the queue, storage holds {loaded other}"
            | Error error -> return ConformanceOutcome.Failed $"a queue carrying a credential gave {failure error}, not Corrupt"
            | Ok() -> return ConformanceOutcome.Failed "a queue carrying a credential was saved"
        }

    /// Every case: its name, the requirement it proves and its check.
    let cases: (string * string * (QueueStoreSubject -> Async<ConformanceOutcome>)) list =
        [ "absent queue loads nothing", "ARCA-OFF-002", absent
          "round-trip keeps order, sequences and states", "LCP-060", roundTrips
          "a save replaces the whole snapshot", "ARCA-OFF-002", replaces
          "over budget is QuotaExceeded, nothing truncated", "ARCA-OFF-002", overBudget
          "another namespace's queue is Corrupt", "LCP-060", foreign
          "an unreadable queue is Corrupt", "ARCA-OFF-002", corrupt
          "unavailable storage is Unavailable", "ARCA-OFF-002", unavailable
          "a queue carrying a credential is refused", "ARCA-AUTH-002", credentialFree
          "a revised entry keeps its account through a reload", "ARCA-OFF-007", revisedKeepsAccount ]

    /// Runs every case, each against a fresh subject.
    let run (fresh: unit -> Async<QueueStoreSubject>) : Async<ConformanceResult list> =
        async {
            let results = Collections.Generic.List<ConformanceResult>()

            for name, requirement, check in cases do
                let! subject = fresh ()
                let! outcome = check subject

                results.Add
                    { Case = name
                      Requirement = requirement
                      Outcome = outcome }

            return List.ofSeq results
        }

    /// The in-memory store as a conformance subject.
    let inMemory (budget: int64) (ns: Namespace) : Async<QueueStoreSubject> =
        async {
            let cell = MemoryQueueStore.cell ()

            return
                { Namespace = ns
                  Store = MemoryQueueStore.over cell budget ns
                  Reopen = fun () -> async { return MemoryQueueStore.over cell budget ns }
                  Budget = budget
                  Arrange =
                    fun fault ->
                        async {
                            match fault with
                            | QueueStoreFault.Unavailable -> cell.Unavailable <- true
                            | QueueStoreFault.Stored text -> cell.Text <- Some text

                            return true
                        } }
        }
