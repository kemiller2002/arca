namespace Arca

open System
open System.Globalization

/// Whether an application may queue writes while offline (ARCA-OFF-005).
[<RequireQualifiedAccess>]
type OfflinePolicy =
    /// Writes made while offline are queued and synchronized later.
    | QueueWrites
    /// Mutating capabilities are unavailable while offline: a read-only
    /// degraded mode (Signal's administrator, SIG ADM-070).
    | ReadOnlyWhenOffline

/// A stable identity for the account that made a queued change, used to
/// match its entries at sign-out (LCP-070, ARCA-OFF-007). Never a display
/// name: two people may share one.
[<RequireQualifiedAccess>]
type AccountId =
    /// The provider's stable subject, such as GitHub's numeric user id
    /// (`CapabilitySnapshot.Identity`, ARCA-AUTH-003).
    | ProviderSubject of provider: string * subject: string
    /// An actor id the application supplies and keeps stable.
    | Actor of ActorId

/// Construction and the stored form of account ids.
[<RequireQualifiedAccess>]
module AccountId =

    /// The account the provider resolved for the credential in use.
    let ofIdentity (identity: ProviderIdentity) = AccountId.ProviderSubject(identity.Provider, identity.Subject)

    /// An application-supplied stable actor id.
    let ofActor (actor: ActorId) = AccountId.Actor actor

    /// The stored form: `subject:<provider>:<subject>` or `actor:<id>`.
    let toWire =
        function
        | AccountId.ProviderSubject(provider, subject) -> $"subject:{provider}:{subject}"
        | AccountId.Actor actor -> $"actor:{ActorId.value actor}"

    /// An account id from its stored form.
    let ofWire (text: string) =
        match text.Split(':', 3) with
        | [| "subject"; provider; subject |] when provider <> "" && subject <> "" -> Some(AccountId.ProviderSubject(provider, subject))
        | [| "actor"; actor |] -> ActorId.create actor |> Result.toOption |> Option.map AccountId.Actor
        | [| "actor"; first; rest |] -> ActorId.create $"{first}:{rest}" |> Result.toOption |> Option.map AccountId.Actor
        | _ -> None

/// One change as stored in the queue.
[<RequireQualifiedAccess>]
type QueuedChange =
    | Create of path: string * content: string
    | Update of path: string * content: string * expected: string
    | Delete of path: string * expected: string

/// An operation as plain, serializable data: its namespace identity, its
/// metadata and its changes. It holds no credential (ARCA-AUTH-002).
type QueuedOperation =
    { Application: string
      Dataset: string option
      Owner: string
      Repository: string
      Branch: string
      BasePath: string
      Summary: string
      ActorKind: ActorKind
      ActorId: string
      ProviderIdentity: string option
      ExecutionId: string option
      CorrelationId: string
      IdempotencyKey: string
      Changes: QueuedChange list
      ExpectedChangeToken: string option
      /// The namespace condition (ARCA-CON-005). Persisted only when set, so
      /// a queue without one keeps its exact earlier text.
      ExpectedNamespaceToken: string option
      /// The account that made it (`AccountId.toWire`), recorded by
      /// `OfflineQueue.enqueueFor`; None for entries queued without one.
      /// Persisted only when set.
      AccountId: string option }

/// The cases of an entry state, internal to Arca (ARCA-OFF-009).
[<RequireQualifiedAccess>]
type internal EntryCase =
    | Pending
    | InFlight of baseToken: string
    | Synchronized of changeToken: string
    | Conflicted of paths: string list
    | OutcomeUnknown of baseToken: string
    | Refused of reason: string
    | Abandoned of reason: string

/// Where one queued operation stands (ARCA-OFF-001).
///
/// Read it with the `EntryState` patterns (`EntryState.Pending`,
/// `EntryState.InFlight baseToken`, `EntryState.Synchronized token`,
/// `EntryState.Conflicted paths`, `EntryState.OutcomeUnknown baseToken`,
/// `EntryState.Refused reason`, `EntryState.Abandoned reason`). Only Arca
/// creates one (ARCA-OFF-009, DF-ARCA-2026-0014): an entry becomes
/// Synchronized only through `OfflineQueue.recordResult` or
/// `OfflineQueue.recordReconciliation` with a provider's receipt for that
/// entry's operation, so application code cannot claim that a write landed.
type EntryState =
    internal
        { case: EntryCase }

/// Reading entry states, and (internally) making them.
[<RequireQualifiedAccess>]
module EntryState =

    /// The state's case. Every entry is in exactly one:
    /// - `Pending`: waiting to be sent.
    /// - `InFlight baseToken`: being sent, built on this change token.
    ///   Recorded before sending, so a restart can reconcile instead of
    ///   resending blindly (ARCA-OFF-004).
    /// - `Synchronized changeToken`: the provider accepted it, as its receipt
    ///   for this entry's operation said.
    /// - `Conflicted paths`: the provider's state moved under it; the
    ///   application reloads, revalidates and decides (ARCA-CON-002).
    /// - `OutcomeUnknown baseToken`: it may have landed; it must be
    ///   reconciled before anything else (ARCA-OUT-001).
    /// - `Refused reason`: the provider refused it permanently; the
    ///   application decides.
    /// - `Abandoned reason`: the application explicitly gave it up. Kept
    ///   until pruned, so nothing is ever dropped silently.
    let (|Pending|InFlight|Synchronized|Conflicted|OutcomeUnknown|Refused|Abandoned|) (state: EntryState) =
        match state.case with
        | EntryCase.Pending -> Pending
        | EntryCase.InFlight baseToken -> InFlight baseToken
        | EntryCase.Synchronized changeToken -> Synchronized changeToken
        | EntryCase.Conflicted paths -> Conflicted paths
        | EntryCase.OutcomeUnknown baseToken -> OutcomeUnknown baseToken
        | EntryCase.Refused reason -> Refused reason
        | EntryCase.Abandoned reason -> Abandoned reason

    /// True when the provider accepted the entry (it is Synchronized).
    let isSynchronized (state: EntryState) =
        match state.case with
        | EntryCase.Synchronized _ -> true
        | _ -> false

    /// The stored name of the state's case (`pending`, `in-flight`,
    /// `synchronized`, `conflicted`, `outcome-unknown`, `refused`,
    /// `abandoned`), for display and diagnostics.
    let name (state: EntryState) =
        match state.case with
        | EntryCase.Pending -> "pending"
        | EntryCase.InFlight _ -> "in-flight"
        | EntryCase.Synchronized _ -> "synchronized"
        | EntryCase.Conflicted _ -> "conflicted"
        | EntryCase.OutcomeUnknown _ -> "outcome-unknown"
        | EntryCase.Refused _ -> "refused"
        | EntryCase.Abandoned _ -> "abandoned"

    let internal pending = { case = EntryCase.Pending }
    let internal inFlight baseToken = { case = EntryCase.InFlight baseToken }
    let internal synchronized changeToken = { case = EntryCase.Synchronized changeToken }
    let internal conflicted paths = { case = EntryCase.Conflicted paths }
    let internal outcomeUnknown baseToken = { case = EntryCase.OutcomeUnknown baseToken }
    let internal refused reason = { case = EntryCase.Refused reason }
    let internal abandoned reason = { case = EntryCase.Abandoned reason }

/// One queued operation and its state. Read it through `Sequence`,
/// `Operation`, `EnqueuedAt` and `State`; only OfflineQueue's commands
/// change it (ARCA-OFF-009).
type QueueEntry =
    internal
        { sequence: int64
          operation: QueuedOperation
          enqueuedAt: DateTimeOffset
          state: EntryState }

    /// The entry's position: assigned at enqueue, never reused.
    member this.Sequence = this.sequence

    /// The queued operation, as plain data.
    member this.Operation = this.operation

    /// When it was queued.
    member this.EnqueuedAt = this.enqueuedAt

    /// Where it stands.
    member this.State = this.state

/// The offline change queue: ordered entries, as data (ARCA-OFF-001). Read
/// it through `Policy`, `NextSequence` and `Entries`; only OfflineQueue's
/// commands (and `OfflineQueue.decode` of a queue Arca persisted) make or
/// change one (ARCA-OFF-009).
type OfflineQueue =
    internal
        { policy: OfflinePolicy
          nextSequence: int64
          entries: QueueEntry list }

    /// Whether the application opted in to offline writes.
    member this.Policy = this.policy

    /// The sequence the next enqueued entry gets.
    member this.NextSequence = this.nextSequence

    /// Oldest first.
    member this.Entries = this.entries

/// Why the queue refused a request.
[<RequireQualifiedAccess>]
type QueueError =
    /// The application did not opt in to offline writes (ARCA-OFF-005).
    | OfflineWritesDisabled
    | UnknownEntry of sequence: int64
    /// The entry is not in a state the request applies to.
    | NotApplicable of sequence: int64
    /// The stored queue is not one this Arca reads.
    | Corrupt of reason: string
    /// The operation stored in an entry no longer validates.
    | InvalidOperation of reason: string
    /// The receipt confirms another operation: its idempotency key is not
    /// the entry's (ARCA-OFF-009). Nothing is recorded.
    | ReceiptMismatch of sequence: int64

/// What the application can observe about synchronization (ARCA-OFF-003).
type SyncStatus =
    { Pending: int
      InFlight: int
      OutcomeUnknown: int
      Conflicted: int
      Refused: int
      /// True only when nothing waits to reach the provider and nothing needs
      /// attention: unsynchronized data is never reported as synchronized.
      Synchronized: bool }

/// The offline change queue as pure transitions (ARCA-OFF-001..006). The
/// queue holds operations, never record state: reads always go to the
/// provider, which stays authoritative (ARCA-OFF-006).
[<RequireQualifiedAccess>]
module OfflineQueue =

    /// The queue format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// An empty queue under a policy.
    let create policy =
        { policy = policy
          nextSequence = 1L
          entries = [] }

    /// The operation as plain data.
    let describe (operation: Operation) : QueuedOperation =
        let ns = operation.Namespace
        let metadata = operation.Metadata
        let render = RelativePath.render
        let revision (Revision value) = value

        { Application = AppId.value ns.Application
          Dataset = ns.Dataset |> Option.map DatasetId.value
          Owner = ns.Location.Repository.Owner
          Repository = ns.Location.Repository.Name
          Branch = BranchName.value ns.Location.Branch
          BasePath = render ns.Location.BasePath
          Summary = metadata.Summary
          ActorKind = metadata.Actor.Kind
          ActorId = ActorId.value metadata.Actor.Id
          ProviderIdentity = metadata.ProviderIdentity
          ExecutionId = metadata.ExecutionId
          CorrelationId = CorrelationId.value metadata.CorrelationId
          IdempotencyKey = IdempotencyKey.value metadata.IdempotencyKey
          Changes =
            operation.Changes
            |> List.map (function
                | Change.Create(path, content) -> QueuedChange.Create(render path, content)
                | Change.Update(path, content, expected) -> QueuedChange.Update(render path, content, revision expected)
                | Change.Delete(path, expected) -> QueuedChange.Delete(render path, revision expected))
          ExpectedChangeToken = operation.ExpectedChangeToken |> Option.map (fun (ChangeToken token) -> token)
          ExpectedNamespaceToken = operation.ExpectedNamespaceToken |> Option.map (fun (NamespaceToken token) -> token)
          AccountId = None }

    /// True when the queued operation belongs to `ns`: same application,
    /// dataset, repository, branch and base path.
    let belongsTo (ns: Namespace) (queued: QueuedOperation) =
        let location = ns.Location

        queued.Application = AppId.value ns.Application
        && queued.Dataset = (ns.Dataset |> Option.map DatasetId.value)
        && RepositoryRef.create queued.Owner queued.Repository = Ok location.Repository
        && queued.Branch = BranchName.value location.Branch
        && queued.BasePath = RelativePath.render location.BasePath

    /// The operation an entry describes, in the namespace the application
    /// resolved from its deployment, validated again as if it were new.
    let operationOf (ns: Namespace) (queued: QueuedOperation) =
        if not (belongsTo ns queued) then
            Error(QueueError.InvalidOperation "the entry belongs to another namespace or location")
        else
            let changes =
                queued.Changes
                |> List.map (fun change ->
                    let parse text = RelativePath.parse text |> Result.mapError LocationError.describe

                    match change with
                    | QueuedChange.Create(path, content) -> parse path |> Result.map (fun p -> Change.Create(p, content))
                    | QueuedChange.Update(path, content, expected) -> parse path |> Result.map (fun p -> Change.Update(p, content, Revision expected))
                    | QueuedChange.Delete(path, expected) -> parse path |> Result.map (fun p -> Change.Delete(p, Revision expected)))

            match changes |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some error -> Error(QueueError.InvalidOperation error)
            | None ->
                match ActorId.create queued.ActorId, CorrelationId.create queued.CorrelationId, IdempotencyKey.create queued.IdempotencyKey with
                | Ok actor, Ok correlation, Ok key ->
                    let metadata =
                        { Summary = queued.Summary
                          Actor = { Kind = queued.ActorKind; Id = actor }
                          ProviderIdentity = queued.ProviderIdentity
                          ExecutionId = queued.ExecutionId
                          CorrelationId = correlation
                          IdempotencyKey = key }

                    Operation.create ns metadata (changes |> List.choose (function Ok c -> Some c | Error _ -> None))
                    |> Result.mapError (fun _ -> QueueError.InvalidOperation "the queued operation no longer validates")
                    |> Result.map (fun operation ->
                        let withChangeToken =
                            match queued.ExpectedChangeToken with
                            | Some token -> Operation.requireChangeToken (ChangeToken token) operation
                            | None -> operation

                        match queued.ExpectedNamespaceToken with
                        | Some token -> Operation.requireNamespaceToken (NamespaceToken token) withChangeToken
                        | None -> withChangeToken)
                | _ -> Error(QueueError.InvalidOperation "the queued metadata no longer validates")

    /// Queues an operation, when the application opted in (ARCA-OFF-005).
    let enqueue (at: DateTimeOffset) (operation: Operation) (queue: OfflineQueue) =
        match queue.Policy with
        | OfflinePolicy.ReadOnlyWhenOffline -> Error QueueError.OfflineWritesDisabled
        // An erasure is a deliberate, audited act against current state: it is
        // sent online, never queued (ARCA-INT-005).
        | OfflinePolicy.QueueWrites when operation.IsErasure -> Error(QueueError.InvalidOperation "an erasure is sent online, never queued")
        | OfflinePolicy.QueueWrites ->
            let entry =
                { sequence = queue.NextSequence
                  operation = describe operation
                  enqueuedAt = at
                  state = EntryState.pending }

            Ok(
                { queue with
                    nextSequence = queue.NextSequence + 1L
                    entries = queue.Entries @ [ entry ] },
                entry.Sequence
            )

    /// Queues an operation made by `account`, so sign-out can match the
    /// account's entries by a stable id rather than a display name (LCP-070,
    /// ARCA-OFF-007).
    let enqueueFor (account: AccountId) (at: DateTimeOffset) (operation: Operation) (queue: OfflineQueue) =
        enqueue at operation queue
        |> Result.map (fun (next: OfflineQueue, sequence) ->
            { next with
                entries =
                    next.Entries
                    |> List.map (fun entry ->
                        if entry.Sequence = sequence then
                            { entry with operation = { entry.Operation with AccountId = Some(AccountId.toWire account) } }
                        else
                            entry) },
            sequence)

    let private needsAttention (state: EntryState) =
        match state with
        | EntryState.Pending
        | EntryState.Synchronized _
        | EntryState.Abandoned _ -> false
        | EntryState.InFlight _
        | EntryState.OutcomeUnknown _
        | EntryState.Conflicted _
        | EntryState.Refused _ -> true


    /// The entry synchronization works on next: the oldest one that is not
    /// synchronized or abandoned. Order is strict, because a later operation
    /// may depend on an earlier one: an entry that needs attention (unknown,
    /// conflicted, refused, in flight) blocks every entry after it.
    let next (queue: OfflineQueue) =
        queue.Entries
        |> List.tryFind (fun entry ->
            match entry.State with
            | EntryState.Synchronized _
            | EntryState.Abandoned _ -> false
            | _ -> true)

    let private update (sequence: int64) (change: QueueEntry -> Result<QueueEntry, QueueError>) (queue: OfflineQueue) =
        match queue.Entries |> List.tryFind (fun entry -> entry.Sequence = sequence) with
        | None -> Error(QueueError.UnknownEntry sequence)
        | Some entry ->
            change entry
            |> Result.map (fun changed ->
                { queue with
                    entries = queue.Entries |> List.map (fun existing -> if existing.Sequence = sequence then changed else existing) })

    /// Records, before sending, that an entry is being sent on top of
    /// `baseToken` (write-ahead, ARCA-OFF-004). Only the entry `next` names,
    /// while Pending, may be sent.
    let markInFlight (sequence: int64) (ChangeToken baseToken) (queue: OfflineQueue) =
        match next queue with
        | Some entry when entry.Sequence = sequence && (match entry.State with EntryState.Pending -> true | _ -> false) ->
            update sequence (fun entry -> Ok { entry with state = EntryState.inFlight baseToken }) queue
        | Some _
        | None -> Error(QueueError.NotApplicable sequence)

    /// True when the receipt confirms this entry's operation: a provider
    /// issued it for the same idempotency key (ARCA-OFF-009).
    let private confirms (receipt: CommitReceipt) (entry: QueueEntry) =
        IdempotencyKey.value receipt.IdempotencyKey = entry.Operation.IdempotencyKey

    /// Records what sending an in-flight entry produced. Only the entry that is
    /// in flight takes a result: a result for an entry that is no longer in
    /// flight (a stale or out-of-order acknowledgment) is NotApplicable, and a
    /// receipt that confirms another operation is ReceiptMismatch. Neither
    /// changes the queue (ARCA-OFF-009).
    let recordResult (sequence: int64) (result: Result<CommitReceipt, StorageFailure>) (queue: OfflineQueue) =
        update
            sequence
            (fun entry ->
                match entry.State, result with
                | EntryState.InFlight _, Ok receipt when not (confirms receipt entry) -> Error(QueueError.ReceiptMismatch sequence)
                | EntryState.InFlight _, Ok receipt ->
                    let (ChangeToken token) = receipt.ChangeToken
                    Ok { entry with state = EntryState.synchronized token }
                | EntryState.InFlight baseToken, Error failure ->
                    let state =
                        match failure with
                        | StorageFailure.Conflicted conflicts -> EntryState.conflicted(conflicts |> List.map (_.Path >> RelativePath.render))
                        | StorageFailure.OutcomeUnknown _ -> EntryState.outcomeUnknown baseToken
                        // Nothing was applied and trying later may succeed.
                        | StorageFailure.RateLimited _
                        | StorageFailure.ProviderFailed(_, true, _) -> EntryState.pending
                        | StorageFailure.Refused(WriteRefusal.CredentialUnavailable _) -> EntryState.pending
                        | StorageFailure.StaleChangeToken _
                        | StorageFailure.StaleNamespaceToken _ -> EntryState.conflicted []
                        | StorageFailure.Refused _ -> EntryState.refused "the provider refused the write"
                        | StorageFailure.IntegrityRefused(path, _) -> EntryState.refused $"what is stored at {path} makes the write unsafe"
                        | StorageFailure.ObjectTooLarge(path, _, _) -> EntryState.refused $"{path} is too large"
                        | StorageFailure.WrongLocation _ -> EntryState.refused "the namespace is at another location"
                        | StorageFailure.ProviderFailed(code, false, _) -> EntryState.refused $"provider failure {code}"

                    Ok { entry with state = state }
                | _ -> Error(QueueError.NotApplicable sequence))
            queue

    /// The reconciliation obligation of an entry whose outcome is unknown.
    let pendingOf (entry: QueueEntry) =
        match entry.State, IdempotencyKey.create entry.Operation.IdempotencyKey with
        | EntryState.OutcomeUnknown baseToken, Ok key ->
            Some
                { IdempotencyKey = key
                  Base = ChangeToken baseToken
                  Candidate = None
                  Revisions = Map.empty }
        | _ -> None

    /// Records what reconciling an entry found: landed is synchronized,
    /// not landed may be sent again, still unknown stays (ARCA-OFF-004). A
    /// landed receipt must confirm this entry's operation (ARCA-OFF-009).
    let recordReconciliation (sequence: int64) (outcome: ReconcileOutcome) (queue: OfflineQueue) =
        update
            sequence
            (fun entry ->
                match entry.State, outcome with
                | EntryState.OutcomeUnknown _, ReconcileOutcome.Landed receipt when not (confirms receipt entry) -> Error(QueueError.ReceiptMismatch sequence)
                | EntryState.OutcomeUnknown _, ReconcileOutcome.Landed receipt ->
                    let (ChangeToken token) = receipt.ChangeToken
                    Ok { entry with state = EntryState.synchronized token }
                | EntryState.OutcomeUnknown _, ReconcileOutcome.NotLanded -> Ok { entry with state = EntryState.pending }
                | EntryState.OutcomeUnknown _, ReconcileOutcome.StillUnknown _ -> Ok entry
                | _ -> Error(QueueError.NotApplicable sequence))
            queue

    /// After a restart, an entry that was in flight may or may not have
    /// landed: it becomes OutcomeUnknown, to be reconciled, never resent
    /// blindly (ARCA-OFF-004).
    let recover (queue: OfflineQueue) =
        { queue with
            entries =
                queue.Entries
                |> List.map (fun entry ->
                    match entry.State with
                    | EntryState.InFlight baseToken -> { entry with state = EntryState.outcomeUnknown baseToken }
                    | _ -> entry) }

    /// The entry with its operation replaced by `operation`. Only what the
    /// operation itself determines changes (namespace, metadata, changes,
    /// conditions); what belongs to the entry is kept: its sequence, enqueue
    /// time and the account that made it (ARCA-OFF-007). `describe` knows
    /// nothing of the entry, so every rebuild goes through here.
    let private replaceOperation (operation: Operation) (entry: QueueEntry) =
        { entry with operation = { describe operation with AccountId = entry.Operation.AccountId } }

    /// Replaces a conflicted or refused entry with the application's revised
    /// operation, at the same position, as Pending (ARCA-CON-002). The entry
    /// keeps its sequence, enqueue time and account. An erasure is refused,
    /// as `enqueue` refuses it (ARCA-INT-005).
    let revise (sequence: int64) (operation: Operation) (queue: OfflineQueue) =
        update
            sequence
            (fun entry ->
                match entry.State with
                | _ when operation.IsErasure -> Error(QueueError.InvalidOperation "an erasure is sent online, never queued")
                | EntryState.Conflicted _
                | EntryState.Refused _ -> Ok { replaceOperation operation entry with state = EntryState.pending }
                | _ -> Error(QueueError.NotApplicable sequence))
            queue

    /// The application explicitly gives up an entry that needs attention.
    /// It stays visible until pruned; nothing is dropped silently.
    let abandon (sequence: int64) (reason: string) (queue: OfflineQueue) =
        update
            sequence
            (fun entry ->
                match entry.State with
                | EntryState.Conflicted _
                | EntryState.Refused _
                | EntryState.Pending -> Ok { entry with state = EntryState.abandoned reason }
                | _ -> Error(QueueError.NotApplicable sequence))
            queue

    /// Removes the entries `owned` selects that have not reached the provider
    /// and cannot have: Pending, Conflicted and Refused ones. In-flight and
    /// outcome-unknown entries may have landed, so they stay, to be
    /// reconciled; synchronized and abandoned ones are left to `prune`.
    /// Returns the queue and how many entries were removed (sign-out,
    /// LCP-070, ARCA-OFF-007).
    let discardUnsent (owned: QueueEntry -> bool) (queue: OfflineQueue) =
        let discardable (entry: QueueEntry) =
            owned entry
            && (match entry.State with
                | EntryState.Pending
                | EntryState.Conflicted _
                | EntryState.Refused _ -> true
                | _ -> false)

        let kept = queue.Entries |> List.filter (discardable >> not)
        { queue with entries = kept }, queue.Entries.Length - kept.Length

    /// Removes synchronized and abandoned entries.
    let prune (queue: OfflineQueue) =
        { queue with
            entries =
                queue.Entries
                |> List.filter (fun entry ->
                    match entry.State with
                    | EntryState.Synchronized _
                    | EntryState.Abandoned _ -> false
                    | _ -> true) }

    /// The synchronization status the application shows (ARCA-OFF-003).
    let status (queue: OfflineQueue) =
        let count predicate = queue.Entries |> List.filter (fun entry -> predicate entry.State) |> List.length

        let pending = count (function EntryState.Pending -> true | _ -> false)
        let inFlight = count (function EntryState.InFlight _ -> true | _ -> false)
        let unknown = count (function EntryState.OutcomeUnknown _ -> true | _ -> false)
        let conflicted = count (function EntryState.Conflicted _ -> true | _ -> false)
        let refused = count (function EntryState.Refused _ -> true | _ -> false)

        { Pending = pending
          InFlight = inFlight
          OutcomeUnknown = unknown
          Conflicted = conflicted
          Refused = refused
          Synchronized = pending + inFlight + unknown + conflicted + refused = 0 }

    /// True when the entry needs the application's or a reconciliation's attention.
    let blocked (queue: OfflineQueue) =
        next queue |> Option.filter (fun entry -> needsAttention entry.State)

    // -----------------------------------------------------------------------
    // Persistence format: canonical JSON, versioned, credential-free.
    // -----------------------------------------------------------------------

    let private timestamp (at: DateTimeOffset) =
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let private optionalText value =
        match value with
        | Some text -> Json.String text
        | None -> Json.Null

    let private changeJson =
        function
        | QueuedChange.Create(path, content) -> Json.objectOf [ "kind", Json.String "create"; "path", Json.String path; "content", Json.String content ]
        | QueuedChange.Update(path, content, expected) ->
            Json.objectOf [ "kind", Json.String "update"; "path", Json.String path; "content", Json.String content; "expected", Json.String expected ]
        | QueuedChange.Delete(path, expected) -> Json.objectOf [ "kind", Json.String "delete"; "path", Json.String path; "expected", Json.String expected ]

    let private stateJson =
        function
        | EntryState.Pending -> Json.objectOf [ "kind", Json.String "pending" ]
        | EntryState.InFlight baseToken -> Json.objectOf [ "kind", Json.String "in-flight"; "base", Json.String baseToken ]
        | EntryState.Synchronized token -> Json.objectOf [ "kind", Json.String "synchronized"; "token", Json.String token ]
        | EntryState.Conflicted paths -> Json.objectOf [ "kind", Json.String "conflicted"; "paths", Json.Array(paths |> List.map Json.String) ]
        | EntryState.OutcomeUnknown baseToken -> Json.objectOf [ "kind", Json.String "outcome-unknown"; "base", Json.String baseToken ]
        | EntryState.Refused reason -> Json.objectOf [ "kind", Json.String "refused"; "reason", Json.String reason ]
        | EntryState.Abandoned reason -> Json.objectOf [ "kind", Json.String "abandoned"; "reason", Json.String reason ]

    let private operationJson (operation: QueuedOperation) =
        let optionalField name value =
            match value with
            | Some text -> [ name, Json.String text ]
            | None -> []

        let namespaceCondition =
            optionalField "expectedNamespaceToken" operation.ExpectedNamespaceToken
            @ optionalField "accountId" operation.AccountId

        let fields =
            [ "application", Json.String operation.Application
              "dataset", optionalText operation.Dataset
              "owner", Json.String operation.Owner
              "repository", Json.String operation.Repository
              "branch", Json.String operation.Branch
              "basePath", Json.String operation.BasePath
              "summary", Json.String operation.Summary
              "actorKind", Json.String(ActorKind.toWire operation.ActorKind)
              "actorId", Json.String operation.ActorId
              "providerIdentity", optionalText operation.ProviderIdentity
              "executionId", optionalText operation.ExecutionId
              "correlationId", Json.String operation.CorrelationId
              "idempotencyKey", Json.String operation.IdempotencyKey
              "changes", Json.Array(operation.Changes |> List.map changeJson)
              "expectedChangeToken", optionalText operation.ExpectedChangeToken ]

        Json.objectOf (fields @ namespaceCondition)

    /// The queue as canonical text to persist. A queue that would carry
    /// anything that looks like a credential is refused (ARCA-AUTH-002).
    let encode (queue: OfflineQueue) =
        let value =
            Json.objectOf
                [ "arcaQueue", Json.Number(decimal Format)
                  "policy",
                  Json.String(
                      match queue.Policy with
                      | OfflinePolicy.QueueWrites -> "queue-writes"
                      | OfflinePolicy.ReadOnlyWhenOffline -> "read-only-when-offline"
                  )
                  "nextSequence", Json.Number(decimal queue.NextSequence)
                  "entries",
                  Json.Array(
                      queue.Entries
                      |> List.map (fun entry ->
                          Json.objectOf
                              [ "sequence", Json.Number(decimal entry.Sequence)
                                "enqueuedAt", Json.String(timestamp entry.EnqueuedAt)
                                "state", stateJson entry.State
                                "operation", operationJson entry.Operation ])
                  ) ]

        let text = Json.canonicalText value

        if Secrets.looksLikeCredential text then
            Error(QueueError.Corrupt "the queue would carry something that looks like a credential")
        else
            Ok text

    let private field name value =
        match Json.field name value with
        | Some found -> Ok found
        | None -> Error(QueueError.Corrupt $"missing {name}")

    let private str name value =
        field name value
        |> Result.bind (function
            | Json.String text -> Ok text
            | _ -> Error(QueueError.Corrupt $"{name} is not text"))

    let private optional name value =
        match Json.field name value with
        | Some(Json.String text) -> Ok(Some text)
        | Some Json.Null
        | None -> Ok None
        | Some _ -> Error(QueueError.Corrupt $"{name} is not text")

    let private integer name value =
        field name value
        |> Result.bind (function
            | Json.Number number when number = Math.Floor number -> Ok(int64 number)
            | _ -> Error(QueueError.Corrupt $"{name} is not an integer"))

    let private items name value =
        field name value
        |> Result.bind (function
            | Json.Array values -> Ok values
            | _ -> Error(QueueError.Corrupt $"{name} is not a list"))

    let private all (results: Result<'a, QueueError> list) =
        List.foldBack (fun next state -> Result.bind (fun values -> Result.map (fun value -> value :: values) next) state) results (Ok [])

    let private map2 f first second =
        first |> Result.bind (fun a -> second |> Result.map (fun b -> f a b))

    let private parseChange value =
        str "kind" value
        |> Result.bind (function
            | "create" -> map2 (fun path content -> QueuedChange.Create(path, content)) (str "path" value) (str "content" value)
            | "update" ->
                str "expected" value
                |> Result.bind (fun expected -> map2 (fun path content -> QueuedChange.Update(path, content, expected)) (str "path" value) (str "content" value))
            | "delete" -> map2 (fun path expected -> QueuedChange.Delete(path, expected)) (str "path" value) (str "expected" value)
            | other -> Error(QueueError.Corrupt $"unknown change kind {other}"))

    let private parseState value =
        str "kind" value
        |> Result.bind (function
            | "pending" -> Ok EntryState.pending
            | "in-flight" -> str "base" value |> Result.map EntryState.inFlight
            | "synchronized" -> str "token" value |> Result.map EntryState.synchronized
            | "conflicted" ->
                items "paths" value
                |> Result.bind (List.map (function Json.String path -> Ok path | _ -> Error(QueueError.Corrupt "conflict path")) >> all)
                |> Result.map EntryState.conflicted
            | "outcome-unknown" -> str "base" value |> Result.map EntryState.outcomeUnknown
            | "refused" -> str "reason" value |> Result.map EntryState.refused
            | "abandoned" -> str "reason" value |> Result.map EntryState.abandoned
            | other -> Error(QueueError.Corrupt $"unknown state {other}"))

    let private parseOperation value : Result<QueuedOperation, QueueError> =
        let text name = str name value
        let opt name = optional name value

        match
            text "application", opt "dataset", text "owner", text "repository", text "branch", text "basePath", text "summary"
        with
        | Ok application, Ok dataset, Ok owner, Ok repository, Ok branch, Ok basePath, Ok summary ->
            match text "actorKind" |> Result.bind (fun kind -> ActorKind.ofWire kind |> Option.map Ok |> Option.defaultValue (Error(QueueError.Corrupt "actor kind"))),
                  text "actorId",
                  opt "providerIdentity",
                  opt "executionId",
                  text "correlationId",
                  text "idempotencyKey",
                  items "changes" value |> Result.bind (List.map parseChange >> all),
                  opt "expectedChangeToken",
                  opt "expectedNamespaceToken",
                  opt "accountId" with
            | Ok kind, Ok actor, Ok identity, Ok execution, Ok correlation, Ok key, Ok changes, Ok expected, Ok expectedNamespace, Ok account ->
                Ok
                    { Application = application
                      Dataset = dataset
                      Owner = owner
                      Repository = repository
                      Branch = branch
                      BasePath = basePath
                      Summary = summary
                      ActorKind = kind
                      ActorId = actor
                      ProviderIdentity = identity
                      ExecutionId = execution
                      CorrelationId = correlation
                      IdempotencyKey = key
                      Changes = changes
                      ExpectedChangeToken = expected
                      ExpectedNamespaceToken = expectedNamespace
                      AccountId = account }
            | _ -> Error(QueueError.Corrupt "an operation's metadata or changes")
        | _ -> Error(QueueError.Corrupt "an operation's namespace or summary")

    /// A queue from persisted text, refusing anything this Arca did not write.
    let decode (text: string) =
        match Json.parse text with
        | Error error -> Error(QueueError.Corrupt(JsonError.describe error))
        | Ok value ->
            match integer "arcaQueue" value with
            | Error error -> Error error
            | Ok format when format <> int64 Format -> Error(QueueError.Corrupt $"queue format {format}, not {Format}")
            | Ok _ ->
                let policy =
                    str "policy" value
                    |> Result.bind (function
                        | "queue-writes" -> Ok OfflinePolicy.QueueWrites
                        | "read-only-when-offline" -> Ok OfflinePolicy.ReadOnlyWhenOffline
                        | other -> Error(QueueError.Corrupt $"unknown policy {other}"))

                let entries =
                    items "entries" value
                    |> Result.bind (
                        List.map (fun entry ->
                            match integer "sequence" entry, str "enqueuedAt" entry, field "state" entry |> Result.bind parseState, field "operation" entry |> Result.bind parseOperation with
                            | Ok sequence, Ok at, Ok state, Ok operation ->
                                match DateTimeOffset.TryParseExact(at, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                                | true, parsed ->
                                    Ok
                                        { sequence = sequence
                                          operation = operation
                                          enqueuedAt = parsed.ToUniversalTime()
                                          state = state }
                                | _ -> Error(QueueError.Corrupt "enqueuedAt")
                            | Error error, _, _, _
                            | _, Error error, _, _
                            | _, _, Error error, _
                            | _, _, _, Error error -> Error error)
                        >> all
                    )

                match policy, integer "nextSequence" value, entries with
                | Ok policy, Ok nextSequence, Ok entries ->
                    Ok
                        { policy = policy
                          nextSequence = nextSequence
                          entries = entries }
                | Error error, _, _
                | _, Error error, _
                | _, _, Error error -> Error error

/// Why a queue store could not load or save (ARCA-OFF-002).
[<RequireQualifiedAccess>]
type QueueStoreFailure =
    /// The browser offers no usable storage here (for example some private modes).
    | Unavailable
    /// The queue would not fit; nothing was truncated or dropped.
    | QuotaExceeded of bytes: int64 * budget: int64
    /// The stored queue cannot be read.
    | Corrupt of QueueError

/// The queue-store port (ARCA-OFF-002, DF-ARCA-2026-0005): durable storage
/// of the whole queue. A localStorage adapter implements it now; a Limen
/// IndexedDB adapter can replace it without touching the core.
[<NoEquality; NoComparison>]
type QueueStore =
    { Load: unit -> Async<Result<OfflineQueue option, QueueStoreFailure>>
      Save: OfflineQueue -> Async<Result<unit, QueueStoreFailure>> }

/// What one synchronization step did.
[<RequireQualifiedAccess>]
type SyncStep =
    /// Nothing waits to be sent.
    | Idle
    /// The next entry needs the application (conflicted or refused).
    | Blocked of QueueEntry
    /// One entry advanced; call again.
    | Progressed of QueueEntry
    /// The provider or the store failed transiently; try again later.
    | Deferred of reason: string

/// Why a synchronization step stopped short, typed so that the host knows
/// when, and whether, to try again (ARCA-OFF-010). `SyncSchedule` turns it
/// into a directive; a host never decides by looping.
[<RequireQualifiedAccess>]
type Deferral =
    /// The provider could not be reached: the device is offline. Nothing was
    /// sent; wait for the connection.
    | Offline
    /// The provider's rate limit is exhausted; its evidence (ARCA-API-003).
    | RateLimited of retryAfter: TimeSpan option * resetAt: int64 option
    /// Trying later may succeed: a provider failure marked transient, an
    /// outcome not yet settled, or a local queue save that failed.
    | Transient
    /// No credential is available. Retrying cannot help until the
    /// application supplies one.
    | AwaitingCredential
    /// Retrying cannot help: the provider refused, or failed permanently.
    | Permanent

/// Classifying storage failures as deferrals.
[<RequireQualifiedAccess>]
module Deferral =

    /// Aegis's fault code for a provider that could not be reached.
    [<Literal>]
    let NetworkUnavailable = "AEGIS.NETWORK.UNAVAILABLE"

    /// The deferral a failure calls for. Only the network fault means
    /// offline; only a missing credential waits for one; a transient
    /// provider failure, an unknown outcome and stale tokens may succeed
    /// later; every other refusal or failure is permanent.
    let ofFailure (failure: StorageFailure) =
        match failure with
        | StorageFailure.ProviderFailed(code, _, _) when code = NetworkUnavailable -> Deferral.Offline
        | StorageFailure.RateLimited(retryAfter, resetAt) -> Deferral.RateLimited(retryAfter, resetAt)
        | StorageFailure.Refused(WriteRefusal.CredentialUnavailable _) -> Deferral.AwaitingCredential
        | StorageFailure.ProviderFailed(_, true, _)
        | StorageFailure.OutcomeUnknown _
        | StorageFailure.Conflicted _
        | StorageFailure.StaleChangeToken _
        | StorageFailure.StaleNamespaceToken _ -> Deferral.Transient
        | StorageFailure.ProviderFailed(_, false, _)
        | StorageFailure.Refused _
        | StorageFailure.ObjectTooLarge _
        | StorageFailure.WrongLocation _
        | StorageFailure.IntegrityRefused _ -> Deferral.Permanent

    /// The stored name: `offline`, `rate-limited`, `transient`,
    /// `awaiting-credential` or `permanent`.
    let name (deferral: Deferral) =
        match deferral with
        | Deferral.Offline -> "offline"
        | Deferral.RateLimited _ -> "rate-limited"
        | Deferral.Transient -> "transient"
        | Deferral.AwaitingCredential -> "awaiting-credential"
        | Deferral.Permanent -> "permanent"

    /// True when only the application can unblock it: never retried
    /// automatically.
    let needsApplication (deferral: Deferral) =
        match deferral with
        | Deferral.AwaitingCredential
        | Deferral.Permanent -> true
        | Deferral.Offline
        | Deferral.RateLimited _
        | Deferral.Transient -> false

/// What one synchronization attempt did: `SyncStep` with the deferral typed
/// (ARCA-OFF-010).
[<RequireQualifiedAccess>]
type SyncAttempt =
    | Idle
    | Blocked of QueueEntry
    | Progressed of QueueEntry
    | Deferred of deferral: Deferral * reason: string

[<RequireQualifiedAccess>]
module SyncAttempt =

    /// The untyped step, with the same reason text.
    let toStep (attempt: SyncAttempt) =
        match attempt with
        | SyncAttempt.Idle -> SyncStep.Idle
        | SyncAttempt.Blocked entry -> SyncStep.Blocked entry
        | SyncAttempt.Progressed entry -> SyncStep.Progressed entry
        | SyncAttempt.Deferred(_, reason) -> SyncStep.Deferred reason

/// Synchronizes the queue with a provider, one write-ahead step at a time
/// (ARCA-OFF-004): the entry is persisted as in flight before it is sent,
/// and an unknown outcome is reconciled before anything else is sent.
[<RequireQualifiedAccess>]
module OfflineSync =

    let private persist (store: QueueStore) (queue: OfflineQueue) =
        async {
            match! store.Save queue with
            | Ok() -> return Ok queue
            | Error failure -> return Error failure
        }

    let private transient reason = SyncAttempt.Deferred(Deferral.Transient, reason)

    /// Advances the next entry by one step, persists the result and says,
    /// typed, why it stopped short (ARCA-OFF-010). Offline, the provider's
    /// change token cannot be read, so nothing is committed.
    let attempt (provider: StorageProvider) (store: QueueStore) (ns: Namespace) (queue: OfflineQueue) : Async<OfflineQueue * SyncAttempt> =
        async {
            match OfflineQueue.next queue with
            | None -> return queue, SyncAttempt.Idle
            | Some entry ->
                match entry.State, OfflineQueue.operationOf ns entry.Operation with
                | (EntryState.Conflicted _ | EntryState.Refused _), _ -> return queue, SyncAttempt.Blocked entry
                | _, Error _ ->
                    let refused =
                        { entry with state = EntryState.refused "the queued operation no longer validates" }

                    let queue = { queue with entries = queue.Entries |> List.map (fun e -> if e.Sequence = entry.Sequence then refused else e) }
                    let! _ = persist store queue
                    return queue, SyncAttempt.Blocked refused
                | (EntryState.OutcomeUnknown _ | EntryState.InFlight _), Ok operation ->
                    let queue = OfflineQueue.recover queue
                    let entry = OfflineQueue.next queue |> Option.get

                    match OfflineQueue.pendingOf entry with
                    | None -> return queue, transient "no reconciliation obligation"
                    | Some pending ->
                        match! provider.Reconcile operation.Namespace pending with
                        | Error failure -> return queue, SyncAttempt.Deferred(Deferral.ofFailure failure, "reconciliation did not complete")
                        | Ok(ReconcileOutcome.StillUnknown _) -> return queue, transient "the outcome is still unknown"
                        | Ok outcome ->
                            match OfflineQueue.recordReconciliation entry.Sequence outcome queue with
                            | Error _ -> return queue, transient "the reconciliation could not be recorded"
                            | Ok next ->
                                match! persist store next with
                                | Error _ -> return queue, transient "the queue could not be saved"
                                | Ok saved -> return saved, SyncAttempt.Progressed(saved.Entries |> List.find (fun e -> e.Sequence = entry.Sequence))
                | _, Ok operation ->
                    match! provider.ChangeToken operation.Namespace with
                    | Error failure -> return queue, SyncAttempt.Deferred(Deferral.ofFailure failure, "the provider is unavailable")
                    | Ok baseToken ->
                        match OfflineQueue.markInFlight entry.Sequence baseToken queue with
                        | Error _ -> return queue, transient "the entry is not pending"
                        | Ok inFlight ->
                            // Write-ahead: the in-flight state is durable before sending.
                            match! persist store inFlight with
                            | Error _ -> return queue, transient "the queue could not be saved"
                            | Ok inFlight ->
                                let! result = provider.Commit operation

                                match OfflineQueue.recordResult entry.Sequence result inFlight with
                                | Error _ -> return inFlight, transient "the result could not be recorded"
                                | Ok next ->
                                    match! persist store next with
                                    | Error _ -> return next, transient "the queue could not be saved"
                                    | Ok saved ->
                                        let sent = saved.Entries |> List.find (fun e -> e.Sequence = entry.Sequence)

                                        match sent.State, result with
                                        // Nothing was applied; the failure says when to try again.
                                        | EntryState.Pending, Error failure ->
                                            return saved, SyncAttempt.Deferred(Deferral.ofFailure failure, "the provider is unavailable for now")
                                        | EntryState.Pending, Ok _ -> return saved, transient "the provider is unavailable for now"
                                        | EntryState.Conflicted _, _
                                        | EntryState.Refused _, _ -> return saved, SyncAttempt.Blocked sent
                                        | _ -> return saved, SyncAttempt.Progressed sent
        }

    /// Advances the next entry by one step and persists the result.
    let step (provider: StorageProvider) (store: QueueStore) (ns: Namespace) (queue: OfflineQueue) : Async<OfflineQueue * SyncStep> =
        async {
            let! queue, result = attempt provider store ns queue
            return queue, SyncAttempt.toStep result
        }

    /// Attempts until the queue is idle, blocked or deferred, at most `limit`
    /// attempts. Hitting the limit is a transient deferral.
    let drive (provider: StorageProvider) (store: QueueStore) (ns: Namespace) (limit: int) (queue: OfflineQueue) =
        let rec loop remaining queue =
            async {
                if remaining = 0 then
                    return queue, transient "step limit reached"
                else
                    let! next, result = attempt provider store ns queue

                    match result with
                    | SyncAttempt.Progressed _ -> return! loop (remaining - 1) next
                    | other -> return next, other
            }

        loop limit queue

    /// Steps until the queue is idle, blocked or deferred, at most `limit` steps.
    let run (provider: StorageProvider) (store: QueueStore) (ns: Namespace) (limit: int) (queue: OfflineQueue) =
        async {
            let! queue, result = drive provider store ns limit queue
            return queue, SyncAttempt.toStep result
        }
