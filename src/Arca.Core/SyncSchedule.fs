namespace Arca

open System

/// How queue synchronization backs off (ARCA-OFF-010).
type RetryPolicy =
    { /// The delay after the first consecutive transient failure.
      BaseDelay: TimeSpan
      /// No delay is longer than this.
      MaxDelay: TimeSpan
      /// Each further consecutive failure multiplies the delay by this.
      Multiplier: float
      /// The fraction of each delay that is randomized, from 0 (none) to 1
      /// (full jitter), so that many devices do not retry in step.
      Jitter: float
      /// While offline, how long to wait for the host's connectivity signal
      /// before probing the provider anyway. A probe reads the change token;
      /// offline it fails there, so it never commits.
      OfflineProbe: TimeSpan }

/// Why a retry policy was refused.
[<RequireQualifiedAccess>]
type RetryPolicyError =
    | NonPositiveBaseDelay
    | MaxDelayBelowBaseDelay
    | MultiplierBelowOne
    | JitterOutOfRange
    | NonPositiveOfflineProbe

[<RequireQualifiedAccess>]
module RetryPolicy =

    /// 2 s doubling to at most 5 min, half of each delay jittered, and an
    /// offline probe once a minute.
    let standard =
        { BaseDelay = TimeSpan.FromSeconds 2.0
          MaxDelay = TimeSpan.FromMinutes 5.0
          Multiplier = 2.0
          Jitter = 0.5
          OfflineProbe = TimeSpan.FromMinutes 1.0 }

    /// The policy, when every bound holds: a policy that could spin is
    /// refused.
    let validate (policy: RetryPolicy) =
        if policy.BaseDelay <= TimeSpan.Zero then Error RetryPolicyError.NonPositiveBaseDelay
        elif policy.MaxDelay < policy.BaseDelay then Error RetryPolicyError.MaxDelayBelowBaseDelay
        elif Double.IsNaN policy.Multiplier || policy.Multiplier < 1.0 then Error RetryPolicyError.MultiplierBelowOne
        elif Double.IsNaN policy.Jitter || policy.Jitter < 0.0 || policy.Jitter >= 1.0 then Error RetryPolicyError.JitterOutOfRange
        elif policy.OfflineProbe <= TimeSpan.Zero then Error RetryPolicyError.NonPositiveOfflineProbe
        else Ok policy

    /// The un-jittered delay after `failures` consecutive failures (at least
    /// one): BaseDelay × Multiplier^(failures − 1), at most MaxDelay.
    let ceiling (policy: RetryPolicy) (failures: int) =
        let exponent = float (max 1 failures - 1)
        let ticks = float policy.BaseDelay.Ticks * Math.Pow(policy.Multiplier, exponent)

        if Double.IsInfinity ticks || ticks >= float policy.MaxDelay.Ticks then
            policy.MaxDelay
        else
            TimeSpan.FromTicks(int64 ticks)

    /// The delay after `failures` consecutive failures. `sample` is the
    /// host's entropy in [0, 1) (Arca's core draws none): the delay lies
    /// between (1 − Jitter) × ceiling and ceiling.
    let delay (policy: RetryPolicy) (failures: int) (sample: float) =
        let sample = if Double.IsNaN sample then 0.0 else min (max sample 0.0) 1.0
        let ceiling = ceiling policy failures
        let fraction = 1.0 - policy.Jitter * sample
        TimeSpan.FromTicks(int64 (float ceiling.Ticks * fraction))

/// Whether the host believes the device can reach the network (the browser's
/// `online` and `offline` events). It is a hint: a probe still runs.
[<RequireQualifiedAccess>]
type Connectivity =
    | Online
    | Offline

/// What the host does next (ARCA-OFF-010). Arca never sleeps or loops; the
/// host runs `OfflineSync.attempt` only when `SyncSchedule.isDue` says so.
[<RequireQualifiedAccess>]
type SyncDirective =
    /// Nothing waits to be sent.
    | Rest
    /// Attempt now.
    | AttemptNow
    /// Attempt at this time, after a transient failure or a rate limit.
    | AttemptAt of DateTimeOffset
    /// Wait for the connection; probe at this time if no signal came.
    | AwaitConnection of probeAt: DateTimeOffset
    /// Only the application can unblock synchronization: a conflict, a
    /// refused entry, a missing credential or a permanent failure. Never
    /// retried automatically, nor on an edit; `Resolved` allows one attempt.
    | AwaitApplication

/// What the host reports to the schedule.
[<RequireQualifiedAccess>]
type SyncEvent =
    /// The application queued another change (an edit). It never shortens
    /// a backoff and never retries what awaits the application.
    | Changed
    /// The application resolved what synchronization was waiting for: it
    /// revised or abandoned a conflicted or refused entry, or supplied a
    /// credential. It allows one attempt. Send it for that act only, never
    /// on an edit or a keystroke: each one is an attempt.
    | Resolved
    /// The host began the attempt the schedule made due.
    | Started
    /// The attempt finished.
    | Finished of SyncAttempt
    /// The connectivity signal changed.
    | ConnectivityChanged of Connectivity

/// When to synchronize, as pure data the host keeps (ARCA-OFF-010). Read it
/// through its members; only `SyncSchedule`'s functions change it.
type SyncSchedule =
    internal
        { policy: RetryPolicy
          /// What the attempts found: Offline after a network failure, Online
          /// after any answer from the provider.
          connectivity: Connectivity
          /// What the host last reported. Only a change of it acts, so a host
          /// that re-reports `online` (focus, visibility, polling) while
          /// requests keep failing cannot reset the backoff.
          reported: Connectivity
          failures: int
          deferral: Deferral option
          directive: SyncDirective
          attempting: bool
          changedDuringAttempt: bool }

    member this.Policy = this.policy
    member this.Connectivity = this.connectivity
    member this.Reported = this.reported

    /// Consecutive attempts that deferred since the last one that did not.
    member this.Failures = this.failures

    /// Why the last attempt stopped short, when it did.
    member this.Deferral = this.deferral
    member this.Directive = this.directive

    /// True between `Started` and `Finished`.
    member this.Attempting = this.attempting

/// The synchronization schedule's transitions (ARCA-OFF-010).
///
///     let schedule = SyncSchedule.create RetryPolicy.standard Connectivity.Online |> Result.get
///     // when SyncSchedule.isDue now schedule:
///     let schedule = SyncSchedule.apply now sample SyncEvent.Started schedule
///     let! queue, attempt = OfflineSync.attempt provider store ns queue
///     let schedule = SyncSchedule.apply now sample (SyncEvent.Finished attempt) schedule
///     // wake again at SyncSchedule.wakeAt schedule, on `online`/`offline`, or on a change.
[<RequireQualifiedAccess>]
module SyncSchedule =

    /// A resting schedule under a valid policy.
    let create (policy: RetryPolicy) (connectivity: Connectivity) =
        RetryPolicy.validate policy
        |> Result.map (fun policy ->
            { policy = policy
              connectivity = connectivity
              reported = connectivity
              failures = 0
              deferral = None
              directive = SyncDirective.Rest
              attempting = false
              changedDuringAttempt = false })

    let private awaitConnection (now: DateTimeOffset) (schedule: SyncSchedule) =
        SyncDirective.AwaitConnection(now + schedule.policy.OfflineProbe)

    /// Work to do: attempt now, or wait for the connection while offline.
    let private ready (now: DateTimeOffset) (schedule: SyncSchedule) =
        match schedule.connectivity with
        | Connectivity.Online -> SyncDirective.AttemptNow
        | Connectivity.Offline -> awaitConnection now schedule

    let private resetAt (seconds: int64) =
        DateTimeOffset.FromUnixTimeSeconds seconds

    /// The directive after an attempt deferred, with `failures` counting it.
    let private afterDeferral (now: DateTimeOffset) (sample: float) (failures: int) (deferral: Deferral) (schedule: SyncSchedule) =
        let backoff = now + RetryPolicy.delay schedule.policy failures sample

        match deferral with
        | Deferral.Offline -> awaitConnection now schedule
        | Deferral.Transient -> SyncDirective.AttemptAt backoff
        | Deferral.RateLimited(retryAfter, reset) ->
            // The provider's evidence is a floor, never shortened by backoff.
            let candidates =
                [ Some backoff
                  retryAfter |> Option.map (fun wait -> now + wait)
                  reset |> Option.map resetAt ]
                |> List.choose id

            SyncDirective.AttemptAt(List.max candidates)
        | Deferral.AwaitingCredential
        | Deferral.Permanent -> SyncDirective.AwaitApplication

    let private finished (now: DateTimeOffset) (sample: float) (attempt: SyncAttempt) (schedule: SyncSchedule) =
        // An answer from the provider means the device is online, whatever
        // the host last reported (re-review N2). Only results the provider
        // answered count: an idle or blocked queue, or a transient failure
        // that may be local (a queue save), prove nothing (re-review L3).
        let reached =
            match attempt with
            | SyncAttempt.Progressed _
            | SyncAttempt.Deferred((Deferral.RateLimited _ | Deferral.AwaitingCredential | Deferral.Permanent), _) -> Connectivity.Online
            | SyncAttempt.Idle
            | SyncAttempt.Blocked _
            | SyncAttempt.Deferred((Deferral.Offline | Deferral.Transient), _) -> schedule.connectivity

        let settled =
            { schedule with
                attempting = false
                changedDuringAttempt = false
                connectivity = reached }

        match attempt with
        | SyncAttempt.Progressed entry when EntryState.isSynchronized entry.State ->
            { settled with
                failures = 0
                deferral = None
                directive = ready now settled }
        // An entry advanced without a receipt: its commit's outcome is unknown,
        // or reconciling found it not landed. That cycle is a failure, not
        // progress: resetting here is how a send that never lands, then is
        // reconciled and resent, spins (Rehearsal 6, R6-D-003).
        | SyncAttempt.Progressed _ ->
            let failures = schedule.failures + 1

            let settled =
                { settled with
                    failures = failures
                    deferral = Some Deferral.Transient }

            { settled with directive = afterDeferral now sample failures Deferral.Transient settled }
        | SyncAttempt.Idle when schedule.changedDuringAttempt ->
            { settled with
                failures = 0
                deferral = None
                directive = ready now settled }
        | SyncAttempt.Idle ->
            { settled with
                failures = 0
                deferral = None
                directive = SyncDirective.Rest }
        | SyncAttempt.Blocked _ ->
            { settled with
                failures = 0
                deferral = None
                directive = SyncDirective.AwaitApplication }
        | SyncAttempt.Deferred(deferral, _) ->
            let failures = schedule.failures + 1

            let connectivity =
                match deferral with
                | Deferral.Offline -> Connectivity.Offline
                | _ -> schedule.connectivity

            let settled =
                { settled with
                    connectivity = connectivity
                    failures = failures
                    deferral = Some deferral }

            { settled with directive = afterDeferral now sample failures deferral settled }

    let private retry (now: DateTimeOffset) (schedule: SyncSchedule) =
        { schedule with
            failures = 0
            deferral = None
            directive = ready now schedule }

    let private changed (now: DateTimeOffset) (schedule: SyncSchedule) =
        if schedule.attempting then
            { schedule with changedDuringAttempt = true }
        else
            match schedule.directive with
            // A backoff or a connection wait already holds the change; a new
            // edit never shortens it (no spinning on keystrokes). Nor does an
            // edit retry what awaits the application: a revoked credential is
            // not sent again on every keystroke.
            | SyncDirective.AttemptAt _
            | SyncDirective.AwaitConnection _
            | SyncDirective.AttemptNow
            | SyncDirective.AwaitApplication -> schedule
            | SyncDirective.Rest -> retry now schedule

    let private resolved (now: DateTimeOffset) (schedule: SyncSchedule) =
        if schedule.attempting then
            { schedule with changedDuringAttempt = true }
        else
            match schedule.directive with
            // The application acted: one attempt, no loop.
            | SyncDirective.AwaitApplication
            | SyncDirective.Rest -> retry now schedule
            | SyncDirective.AttemptAt _
            | SyncDirective.AwaitConnection _
            | SyncDirective.AttemptNow -> schedule

    let private connectivityChanged (now: DateTimeOffset) (connectivity: Connectivity) (schedule: SyncSchedule) =
        let reported = { schedule with connectivity = connectivity; reported = connectivity }

        match connectivity, schedule.directive, schedule.deferral with
        // Only a change of the host's report acts: a host that reports
        // `online` again (on focus, visibility, or by polling
        // navigator.onLine) must not reset the backoff each time, even after
        // an attempt found the network failing (independent review finding 2).
        | _ when connectivity = schedule.reported -> schedule
        // The connection is back: send at once, backoff forgotten.
        | Connectivity.Online, SyncDirective.AwaitConnection _, _
        | Connectivity.Online, SyncDirective.AttemptAt _, Some Deferral.Transient ->
            { reported with
                failures = 0
                directive = SyncDirective.AttemptNow }
        // Gone offline: wait for it instead of attempting.
        | Connectivity.Offline, SyncDirective.AttemptNow, _
        | Connectivity.Offline, SyncDirective.AttemptAt _, Some Deferral.Transient ->
            { reported with directive = awaitConnection now reported }
        | _ -> reported

    /// The schedule after `event`, at `now`. `sample` is the host's entropy
    /// in [0, 1), used only for jitter.
    let apply (now: DateTimeOffset) (sample: float) (event: SyncEvent) (schedule: SyncSchedule) =
        match event with
        | SyncEvent.Changed -> changed now schedule
        | SyncEvent.Resolved -> resolved now schedule
        | SyncEvent.Started ->
            { schedule with
                attempting = true
                changedDuringAttempt = false }
        | SyncEvent.Finished attempt -> finished now sample attempt schedule
        | SyncEvent.ConnectivityChanged connectivity -> connectivityChanged now connectivity schedule

    /// True when the host should begin an attempt now. Never while one runs.
    let isDue (now: DateTimeOffset) (schedule: SyncSchedule) =
        not schedule.attempting
        && match schedule.directive with
           | SyncDirective.AttemptNow -> true
           | SyncDirective.AttemptAt at
           | SyncDirective.AwaitConnection at -> at <= now
           | SyncDirective.Rest
           | SyncDirective.AwaitApplication -> false

    /// When the host should next check `isDue`, if a time applies. Rest and
    /// AwaitApplication wake only on an event.
    let wakeAt (now: DateTimeOffset) (schedule: SyncSchedule) =
        match schedule.directive with
        | SyncDirective.AttemptNow -> Some now
        | SyncDirective.AttemptAt at
        | SyncDirective.AwaitConnection at -> Some at
        | SyncDirective.Rest
        | SyncDirective.AwaitApplication -> None

/// What a person is told about their changes (ARCA-OFF-003, ARCA-OFF-010):
/// derived from the queue and the schedule, never set by the application.
[<RequireQualifiedAccess>]
type SaveState =
    /// Every queued change was confirmed by the provider's receipt.
    | Confirmed
    /// Saved on this device, not sent yet.
    | SavedOnDevice
    /// Being sent now.
    | Sending
    /// A change may have landed; Arca is checking before sending anything
    /// else (ARCA-OUT-001).
    | CheckingOutcome
    /// Offline: saved on this device, waiting for the connection.
    | WaitingForConnection
    /// Saved on this device; the next attempt is at this time.
    | RetryScheduled of at: DateTimeOffset
    /// The provider's state moved under a change; the person decides.
    | Conflict of paths: string list
    /// A credential is needed before anything can be sent.
    | NeedsCredential
    /// The provider refused a change or failed permanently.
    | FailedPermanently of reason: string

[<RequireQualifiedAccess>]
module SaveState =

    /// The state of the queue under the schedule. A conflict or a refusal at
    /// the head of the queue comes first, because nothing behind it is sent;
    /// `Confirmed` only when the queue's own status says synchronized.
    let ofQueue (schedule: SyncSchedule) (queue: OfflineQueue) =
        let status = OfflineQueue.status queue
        let head = OfflineQueue.next queue

        match head |> Option.map _.State with
        | Some(EntryState.Conflicted paths) -> SaveState.Conflict paths
        | Some(EntryState.Refused reason) -> SaveState.FailedPermanently reason
        | _ when status.Synchronized -> SaveState.Confirmed
        | _ ->
            match schedule.Deferral, schedule.Directive with
            | Some Deferral.AwaitingCredential, SyncDirective.AwaitApplication -> SaveState.NeedsCredential
            | Some Deferral.Permanent, SyncDirective.AwaitApplication -> SaveState.FailedPermanently "the provider refused the change"
            | _ when schedule.Attempting && status.OutcomeUnknown > 0 -> SaveState.CheckingOutcome
            | _ when schedule.Attempting || status.InFlight > 0 -> SaveState.Sending
            // Offline comes before an unsettled outcome: nothing can be
            // checked until the connection returns (review finding 4).
            | _, SyncDirective.AwaitConnection _ -> SaveState.WaitingForConnection
            | _ when schedule.Connectivity = Connectivity.Offline -> SaveState.WaitingForConnection
            | _ when status.OutcomeUnknown > 0 -> SaveState.CheckingOutcome
            | _, SyncDirective.AttemptAt at -> SaveState.RetryScheduled at
            | _ -> SaveState.SavedOnDevice

    /// True only for `Confirmed`: the one state that may say the provider
    /// has every change.
    let isConfirmed (state: SaveState) =
        match state with
        | SaveState.Confirmed -> true
        | _ -> false

    /// The stable name, for display mapping and diagnostics.
    let name (state: SaveState) =
        match state with
        | SaveState.Confirmed -> "confirmed"
        | SaveState.SavedOnDevice -> "saved-on-device"
        | SaveState.Sending -> "sending"
        | SaveState.CheckingOutcome -> "checking-outcome"
        | SaveState.WaitingForConnection -> "waiting-for-connection"
        | SaveState.RetryScheduled _ -> "retry-scheduled"
        | SaveState.Conflict _ -> "conflict"
        | SaveState.NeedsCredential -> "needs-credential"
        | SaveState.FailedPermanently _ -> "failed-permanently"
