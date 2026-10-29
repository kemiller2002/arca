/// Bounded synchronization scheduling and save state (ARCA-OFF-010, WI-0037):
/// the Rehearsal 6 defect R6-D-003 (an unsendable save retried 151 times in
/// 60 s while the page said "Saving to GitHub") reproduced on a simulated
/// clock, and the schedule that prevents it.
module Arca.Tests.SyncScheduleTests

open System
open Arca
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private causa =
    Namespace.ofApplication
        { Application = AppId.create "causa" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "schedule" }
          Location = location }
    |> ok

let private t0 = DateTimeOffset(2026, 10, 28, 22, 0, 0, TimeSpan.Zero)

let private change (key: string) =
    Operation.create
        causa
        { Summary = $"investigation {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"causa-save-{key}" |> ok }
        [ Change.Create(RelativePath.parse $"investigations/{key}.json" |> ok, $"{{\"id\":\"{key}\"}}") ]
    |> ok

let private queueOf (keys: string list) =
    keys
    |> List.fold (fun queue key -> OfflineQueue.enqueue t0 (change key) queue |> ok |> fst) (OfflineQueue.create OfflinePolicy.QueueWrites)

let private schedule connectivity =
    SyncSchedule.create RetryPolicy.standard connectivity |> ok

/// The host's entropy, as a pure function of the attempt's index.
let private sample (index: int) = Random(7919 + index).NextDouble()

/// What the network does to requests.
[<RequireQualifiedAccess>]
type private Network =
    /// Every request reaches GitHub.
    | Up
    /// Nothing reaches GitHub and the browser knows it (`navigator.onLine`
    /// is false): Limen fails every request as a network failure.
    | Down
    /// Rehearsal 6's case: `onLine` stays true, reads succeed, and every
    /// POST is aborted, which Limen reports as an unknown outcome.
    | WritesLost

let private networkFailure =
    StorageFailure.ProviderFailed(Deferral.NetworkUnavailable, true, "GitHub could not be reached; nothing was changed")

/// A provider seen through the network, counting the POSTs (commits) it
/// is asked to send. Test infrastructure: the counters are the only state.
type private Wire(store: InMemoryStore, network: unit -> Network) =
    let mutable posts = 0
    let mutable reads = 0
    member _.Posts = posts
    member _.Reads = reads

    member _.Provider: StorageProvider =
        let inner = store.Provider

        let down call =
            match network () with
            | Network.Down -> async { return Error networkFailure }
            | Network.Up
            | Network.WritesLost -> call ()

        { inner with
            ChangeToken =
                fun ns ->
                    reads <- reads + 1
                    down (fun () -> inner.ChangeToken ns)
            Reconcile =
                fun ns pending ->
                    reads <- reads + 1
                    down (fun () -> inner.Reconcile ns pending)
            Commit =
                fun operation ->
                    match network () with
                    | Network.Down -> async { return Error networkFailure }
                    | Network.Up ->
                        posts <- posts + 1
                        inner.Commit operation
                    | Network.WritesLost ->
                        posts <- posts + 1
                        store.Arrange InMemoryFault.OutcomeUnknownLost
                        inner.Commit operation }

/// A queue store holding the last saved queue.
let private memoryStore () =
    let saved = ref None

    { Load = fun () -> async { return Ok saved.Value }
      Save =
        fun queue ->
            async {
                saved.Value <- Some queue
                return Ok()
            } }

/// What a simulation produced.
type private Run =
    { Queue: OfflineQueue
      Schedule: SyncSchedule
      Attempts: (DateTimeOffset * SyncAttempt) list
      States: (DateTimeOffset * SaveState) list }

/// Drives the schedule on a simulated clock from `start` to `until`: the
/// host attempts only when `SyncSchedule.isDue`, wakes only at
/// `SyncSchedule.wakeAt` or at an external event, and each attempt takes
/// `roundTrip`.
let private simulate (provider: StorageProvider) (roundTrip: TimeSpan) (until: DateTimeOffset) (events: (DateTimeOffset * SyncEvent) list) (schedule: SyncSchedule) (queue: OfflineQueue) =
    let store = memoryStore ()

    let rec loop now events (run: Run) =
        let wake = SyncSchedule.wakeAt now run.Schedule

        let next =
            [ wake; events |> List.tryHead |> Option.map fst ]
            |> List.choose id
            |> List.filter (fun at -> at <= until)
            |> List.sortBy id
            |> List.tryHead

        match next, events with
        | None, _ -> run
        | Some at, (eventAt, event) :: rest when eventAt <= at ->
            let schedule = SyncSchedule.apply eventAt (sample run.Attempts.Length) event run.Schedule

            loop
                (max now eventAt)
                rest
                { run with
                    Schedule = schedule
                    States = (eventAt, SaveState.ofQueue schedule run.Queue) :: run.States }
        | Some at, _ ->
            let at = max now at

            if SyncSchedule.isDue at run.Schedule then
                let started = SyncSchedule.apply at 0.0 SyncEvent.Started run.Schedule
                let sending = SaveState.ofQueue started run.Queue
                let queue, attempt = OfflineSync.attempt provider store causa run.Queue |> Async.RunSynchronously
                let finishedAt = at + roundTrip
                let schedule = SyncSchedule.apply finishedAt (sample run.Attempts.Length) (SyncEvent.Finished attempt) started

                loop
                    finishedAt
                    events
                    { Queue = queue
                      Schedule = schedule
                      Attempts = (at, attempt) :: run.Attempts
                      States = (finishedAt, SaveState.ofQueue schedule queue) :: (at, sending) :: run.States }
            else
                // Woken but not due (a stale wake time): nothing to do yet.
                loop (at + TimeSpan.FromMilliseconds 1.0) events run

    let start =
        events |> List.tryHead |> Option.map fst |> Option.defaultValue t0

    let initial =
        { Queue = queue
          Schedule = SyncSchedule.apply start 0.0 SyncEvent.Changed schedule
          Attempts = []
          States = [] }

    loop start events initial

let private start = t0
let private window = TimeSpan.FromSeconds 60.0
let private roundTrip = TimeSpan.FromMilliseconds 400.0

/// The Rehearsal 6 host: re-runs synchronization whenever the last run did
/// not report Idle or Blocked, paced only by the round trip.
let private naiveHost (provider: StorageProvider) (until: DateTimeOffset) (queue: OfflineQueue) =
    let store = memoryStore ()

    let rec loop (now: DateTimeOffset) queue =
        if now >= until then
            queue
        else
            let queue, step = OfflineSync.step provider store causa queue |> Async.RunSynchronously

            match step with
            | SyncStep.Idle
            | SyncStep.Blocked _ -> queue
            | SyncStep.Progressed _
            | SyncStep.Deferred _ -> loop (now + roundTrip) queue

    loop start queue

[<Fact>]
let ``R6-D-003 reproduced: a host that re-runs synchronization on every result storms the provider with POSTs`` () =
    let wire = Wire(InMemoryStore(), (fun () -> Network.WritesLost))
    naiveHost wire.Provider (start + window) (queueOf [ "a" ]) |> ignore
    // The defect's signature: one change, a POST every other round trip.
    Assert.True(wire.Posts > 50, $"expected the naive host to reproduce the storm, saw {wire.Posts} POSTs")

[<Fact>]
let ``R6-D-003 corrected: lost writes while onLine stays true back off, at most 5 POSTs in 60 s`` () =
    let wire = Wire(InMemoryStore(), (fun () -> Network.WritesLost))
    let run = simulate wire.Provider roundTrip (start + window) [] (schedule Connectivity.Online) (queueOf [ "a" ])
    Assert.InRange(wire.Posts, 1, 5)
    // Every gap between attempts after the first failure is at least half the base delay.
    let times = run.Attempts |> List.rev |> List.map fst

    times
    |> List.pairwise
    |> List.iter (fun (a, b) -> Assert.True(b - a >= TimeSpan.FromSeconds 1.0, $"attempts {a:o} and {b:o} are too close"))

[<Fact>]
let ``R6-D-003 corrected: the page never reports a confirmed save that did not land`` () =
    let wire = Wire(InMemoryStore(), (fun () -> Network.WritesLost))
    let run = simulate wire.Provider roundTrip (start + window) [] (schedule Connectivity.Online) (queueOf [ "a" ])
    Assert.DoesNotContain(run.States, fun (_, state) -> SaveState.isConfirmed state)
    // Between attempts it says when it will try again, not that it is sending.
    Assert.Contains(run.States, fun (_, state) -> SaveState.name state = "retry-scheduled")

[<Fact>]
let ``offline (onLine false): no POST at all in 60 s, at most two probes, and the state says waiting for the connection`` () =
    let wire = Wire(InMemoryStore(), (fun () -> Network.Down))

    let run =
        simulate
            wire.Provider
            roundTrip
            (start + window)
            [ start, SyncEvent.ConnectivityChanged Connectivity.Offline ]
            (schedule Connectivity.Online)
            (queueOf [ "a" ])

    Assert.Equal(0, wire.Posts)
    Assert.InRange(run.Attempts.Length, 0, 2)
    Assert.Equal(SaveState.WaitingForConnection, SaveState.ofQueue run.Schedule run.Queue)

[<Fact>]
let ``offline without a connectivity signal: the first failure is enough to wait for the connection`` () =
    let wire = Wire(InMemoryStore(), (fun () -> Network.Down))
    let run = simulate wire.Provider roundTrip (start + window) [] (schedule Connectivity.Online) (queueOf [ "a" ])
    Assert.Equal(0, wire.Posts)
    Assert.InRange(run.Attempts.Length, 1, 2)
    Assert.Equal(Connectivity.Offline, run.Schedule.Connectivity)
    Assert.Equal("waiting-for-connection", SaveState.ofQueue run.Schedule run.Queue |> SaveState.name)

[<Fact>]
let ``connectivity returning sends at once, lands exactly one commit and reports confirmed`` () =
    let backOnline = start + TimeSpan.FromSeconds 30.0
    let memory = InMemoryStore()
    let mutable now = start
    let wire = Wire(memory, (fun () -> if now < backOnline then Network.Down else Network.Up))

    let provider = wire.Provider

    // Drive in two phases so the network model can follow the clock.
    let offline =
        simulate provider roundTrip (backOnline - TimeSpan.FromMilliseconds 1.0) [ start, SyncEvent.ConnectivityChanged Connectivity.Offline ] (schedule Connectivity.Online) (queueOf [ "a" ])

    now <- backOnline

    let online =
        simulate provider roundTrip (backOnline + TimeSpan.FromSeconds 5.0) [ backOnline, SyncEvent.ConnectivityChanged Connectivity.Online ] offline.Schedule offline.Queue

    Assert.Equal(1, wire.Posts)
    Assert.True((OfflineQueue.status online.Queue).Synchronized)
    Assert.Equal(SaveState.Confirmed, SaveState.ofQueue online.Schedule online.Queue)
    let firstOnline = online.Attempts |> List.map fst |> List.min
    Assert.True(firstOnline - backOnline < TimeSpan.FromSeconds 1.0, "the save waited after the connection returned")

[<Fact>]
let ``a revoked credential is attempted once and never retried; the state says a credential is needed`` () =
    let memory = InMemoryStore()
    memory.Arrange InMemoryFault.CredentialRevoked
    let wire = Wire(memory, (fun () -> Network.Up))
    let run = simulate wire.Provider roundTrip (start + TimeSpan.FromMinutes 10.0) [] (schedule Connectivity.Online) (queueOf [ "a" ])
    Assert.Equal(1, run.Attempts.Length)
    Assert.Equal(0, wire.Posts)
    Assert.Equal(SyncDirective.AwaitApplication, run.Schedule.Directive)
    Assert.Equal(SaveState.NeedsCredential, SaveState.ofQueue run.Schedule run.Queue)

[<Fact>]
let ``a permanent refusal of the write is sent once and reported as failed, never retried`` () =
    let memory = InMemoryStore()
    memory.Arrange InMemoryFault.ReadOnly
    let wire = Wire(memory, (fun () -> Network.Up))
    let run = simulate wire.Provider roundTrip (start + TimeSpan.FromMinutes 10.0) [] (schedule Connectivity.Online) (queueOf [ "a" ])
    Assert.Equal(1, wire.Posts)
    Assert.Equal(SyncDirective.AwaitApplication, run.Schedule.Directive)
    Assert.Equal("failed-permanently", SaveState.ofQueue run.Schedule run.Queue |> SaveState.name)

[<Fact>]
let ``a rate limit's retry-after is a floor the backoff never shortens`` () =
    let memory = InMemoryStore()
    memory.Arrange(InMemoryFault.RateLimited(Some(TimeSpan.FromSeconds 30.0)))
    let wire = Wire(memory, (fun () -> Network.Up))
    let run = simulate wire.Provider roundTrip (start + TimeSpan.FromMinutes 2.0) [] (schedule Connectivity.Online) (queueOf [ "a" ])

    match run.Attempts |> List.rev |> List.map fst with
    | first :: second :: _ -> Assert.True(second - first >= TimeSpan.FromSeconds 30.0)
    | other -> failwith $"expected a retry, got {other.Length} attempts"

    Assert.True((OfflineQueue.status run.Queue).Synchronized)

[<Fact>]
let ``edits during a backoff never shorten it (no retry per keystroke)`` () =
    let deferred =
        SyncSchedule.apply t0 0.5 (SyncEvent.Finished(SyncAttempt.Deferred(Deferral.Transient, "503"))) (schedule Connectivity.Online)

    let afterEdits =
        List.replicate 50 SyncEvent.Changed
        |> List.fold (fun schedule event -> SyncSchedule.apply t0 0.5 event schedule) deferred

    Assert.Equal(deferred.Directive, afterEdits.Directive)
    Assert.False(SyncSchedule.isDue t0 afterEdits)

[<Fact>]
let ``a change made during an attempt that found the queue idle is attempted next`` () =
    let attempting = schedule Connectivity.Online |> SyncSchedule.apply t0 0.0 SyncEvent.Changed |> SyncSchedule.apply t0 0.0 SyncEvent.Started
    let finished = attempting |> SyncSchedule.apply t0 0.0 SyncEvent.Changed |> SyncSchedule.apply t0 0.0 (SyncEvent.Finished SyncAttempt.Idle)
    Assert.Equal(SyncDirective.AttemptNow, finished.Directive)

[<Fact>]
let ``nothing is due while an attempt runs`` () =
    let attempting = schedule Connectivity.Online |> SyncSchedule.apply t0 0.0 SyncEvent.Changed |> SyncSchedule.apply t0 0.0 SyncEvent.Started
    Assert.False(SyncSchedule.isDue (t0 + TimeSpan.FromHours 1.0) attempting)
    Assert.Equal(SaveState.Sending, SaveState.ofQueue attempting (queueOf [ "a" ]))

[<Fact>]
let ``a policy that could spin is refused`` () =
    let refused policy = RetryPolicy.validate policy |> Result.isError
    let standard = RetryPolicy.standard
    Assert.True(refused { standard with BaseDelay = TimeSpan.Zero })
    Assert.True(refused { standard with MaxDelay = TimeSpan.FromSeconds 1.0 })
    Assert.True(refused { standard with Multiplier = 0.5 })
    Assert.True(refused { standard with Jitter = 1.0 })
    Assert.True(refused { standard with OfflineProbe = TimeSpan.Zero })
    Assert.True(RetryPolicy.validate standard |> Result.isOk)

[<Fact>]
let ``failures are classified by what retrying can achieve`` () =
    let cases =
        [ networkFailure, "offline"
          StorageFailure.ProviderFailed("AEGIS.GITHUB.SERVER_ERROR", true, "503"), "transient"
          StorageFailure.ProviderFailed("AEGIS.GITHUB.REPOSITORY_NOT_FOUND", false, "gone"), "permanent"
          StorageFailure.RateLimited(Some(TimeSpan.FromSeconds 5.0), None), "rate-limited"
          StorageFailure.Refused(WriteRefusal.CredentialUnavailable "expired"), "awaiting-credential"
          StorageFailure.Refused WriteRefusal.ReadOnlyAccess, "permanent"
          StorageFailure.Refused WriteRefusal.BranchProtected, "permanent" ]

    cases |> List.iter (fun (failure, expected) -> Assert.Equal(expected, Deferral.ofFailure failure |> Deferral.name))

[<Property>]
let ``every delay lies between (1 - jitter) x ceiling and the ceiling, and the ceiling never exceeds MaxDelay`` (failures: PositiveInt) (sample: NormalFloat) =
    let policy = RetryPolicy.standard
    let sample = abs (sample.Get % 1.0)
    let ceiling = RetryPolicy.ceiling policy failures.Get
    let delay = RetryPolicy.delay policy failures.Get sample
    let floor = TimeSpan.FromTicks(int64 (float ceiling.Ticks * (1.0 - policy.Jitter)))

    ceiling <= policy.MaxDelay
    && delay <= ceiling
    && delay >= floor - TimeSpan.FromTicks 1L
    && delay > TimeSpan.Zero

[<Property>]
let ``the ceiling never shrinks as failures accumulate`` (failures: PositiveInt) =
    RetryPolicy.ceiling RetryPolicy.standard failures.Get <= RetryPolicy.ceiling RetryPolicy.standard (failures.Get + 1)

/// Any interleaving of host events and attempt results.
let private events =
    let deferral =
        Gen.elements
            [ Deferral.Offline
              Deferral.Transient
              Deferral.RateLimited(Some(TimeSpan.FromSeconds 3.0), None)
              Deferral.AwaitingCredential
              Deferral.Permanent ]

    Gen.oneof
        [ Gen.constant SyncEvent.Changed
          Gen.constant SyncEvent.Resolved
          Gen.constant SyncEvent.Started
          Gen.constant(SyncEvent.Finished SyncAttempt.Idle)
          deferral |> Gen.map (fun d -> SyncEvent.Finished(SyncAttempt.Deferred(d, "simulated")))
          Gen.elements [ Connectivity.Online; Connectivity.Offline ] |> Gen.map SyncEvent.ConnectivityChanged ]
    |> Gen.listOf
    |> Arb.fromGen

[<Property>]
let ``under any event sequence, nothing is due again within half the base delay of a deferral, unless the connection returned or the application acted`` () =
    Prop.forAll events (fun sequence ->
        let final =
            sequence
            |> List.fold (fun schedule event -> SyncSchedule.apply t0 0.999 event schedule) (schedule Connectivity.Online)

        match final.Deferral, final.Directive with
        | Some _, SyncDirective.AttemptNow -> true // reached only through Resolved, Changed while resting, or a connectivity change
        | Some _, _ -> not (SyncSchedule.isDue (t0 + TimeSpan.FromMilliseconds 999.0) final)
        | None, _ -> true)

[<Property>]
let ``a schedule awaiting the application is never due on its own`` () =
    Prop.forAll events (fun sequence ->
        let final =
            sequence
            |> List.fold (fun schedule event -> SyncSchedule.apply t0 0.3 event schedule) (schedule Connectivity.Online)

        final.Directive <> SyncDirective.AwaitApplication || not (SyncSchedule.isDue (t0 + TimeSpan.FromDays 365.0) final))

/// A queue every change of which the provider confirmed.
let private synchronized =
    let store = InMemoryStore()
    OfflineSync.run store.Provider (memoryStore ()) causa 10 (queueOf [ "a"; "b" ]) |> Async.RunSynchronously |> fst

[<Property>]
let ``the save state is confirmed exactly when the queue says every change is synchronized`` () =
    Prop.forAll events (fun sequence ->
        let final =
            sequence
            |> List.fold (fun schedule event -> SyncSchedule.apply t0 0.3 event schedule) (schedule Connectivity.Online)

        (OfflineQueue.status synchronized).Synchronized
        && SaveState.isConfirmed (SaveState.ofQueue final synchronized)
        && not (SaveState.isConfirmed (SaveState.ofQueue final (queueOf [ "a" ]))))

[<Property>]
let ``a connectivity event repeated changes nothing: only a change acts`` () =
    Prop.forAll events (fun sequence ->
        let state =
            sequence
            |> List.fold (fun schedule event -> SyncSchedule.apply t0 0.3 event schedule) (schedule Connectivity.Online)

        [ Connectivity.Online; Connectivity.Offline ]
        |> List.forall (fun connectivity ->
            let once = SyncSchedule.apply t0 0.3 (SyncEvent.ConnectivityChanged connectivity) state
            let twice = SyncSchedule.apply t0 0.3 (SyncEvent.ConnectivityChanged connectivity) once
            once.Directive = twice.Directive && once.Failures = twice.Failures))

[<Fact>]
let ``an edit never retries what awaits the application; Resolved allows one attempt`` () =
    let awaiting =
        schedule Connectivity.Online
        |> SyncSchedule.apply t0 0.0 SyncEvent.Changed
        |> SyncSchedule.apply t0 0.0 SyncEvent.Started
        |> SyncSchedule.apply t0 0.0 (SyncEvent.Finished(SyncAttempt.Deferred(Deferral.AwaitingCredential, "revoked")))

    let edited = SyncSchedule.apply t0 0.0 SyncEvent.Changed awaiting
    Assert.Equal(SyncDirective.AwaitApplication, edited.Directive)
    Assert.Equal(SyncDirective.AttemptNow, (SyncSchedule.apply t0 0.0 SyncEvent.Resolved edited).Directive)
