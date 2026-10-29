/// The synchronization schedule over the real GitHub adapter (ARCA-OFF-010,
/// WI-0037): `GitHubStorage.provider` on FakeGitHub, with the host's waits
/// advancing a simulated clock. The harness comes from the independent review
/// of WI-0037, which found that classification tests built from hand-made
/// failures missed what the adapter really returns (5xx as network loss or as
/// permanent), that repeated `online` events reset the backoff, and that edits
/// retried a revoked credential.
module Arca.Tests.AdapterScheduleTests

open System
open Arca
open Arca.GitHub
open Arca.Tests.FakeGitHub
open Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"{error}"

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private causa =
    Namespace.ofApplication
        { Application = AppId.create "causa" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "adapter-schedule" }
          Location = location }
    |> ok

let private t0 = DateTimeOffset(2026, 10, 28, 22, 0, 0, TimeSpan.Zero)
let private window = t0 + TimeSpan.FromSeconds 60.0

let private change (key: string) =
    Operation.create
        causa
        { Summary = "investigation"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"causa-save-{key}" |> ok }
        [ Change.Create(RelativePath.parse $"investigations/{key}.json" |> ok, "{}") ]
    |> ok

let private queueOf (keys: string list) =
    keys
    |> List.fold (fun queue key -> OfflineQueue.enqueue t0 (change key) queue |> ok |> fst) (OfflineQueue.create OfflinePolicy.QueueWrites)

let private memoryStore () =
    let saved = ref None

    { Load = fun () -> async { return Ok saved.Value }
      Save =
        fun queue ->
            async {
                saved.Value <- Some queue
                return Ok()
            } }

/// What the network does.
type private Mode =
    | Up
    /// `navigator.onLine` is false: Limen fails every request as a network failure.
    | NavigatorOffline
    /// Reads reach GitHub; every write is aborted, an unknown outcome (Rehearsal 6).
    | WritesLost
    /// GitHub answers every request with this status.
    | Answers of int

/// The adapter over FakeGitHub. Each request takes 100 ms of simulated time,
/// and the adapter's own waits advance it too. The counters are the only state.
type private Harness(mode: unit -> Mode) =
    let server = Server("acme", "data")
    let mutable elapsed = TimeSpan.Zero
    let mutable writes = 0
    member _.Server = server
    member _.Writes = writes

    member _.Elapsed
        with get () = elapsed
        and set value = elapsed <- value

    member _.Provider =
        let host =
            { Send =
                fun (sent: Authorized) ->
                    async {
                        elapsed <- elapsed + TimeSpan.FromMilliseconds 100.0
                        let read = sent.Request.Method = HttpMethod.Get

                        if not read then
                            writes <- writes + 1

                        match mode () with
                        | Up -> return server.Send sent
                        | NavigatorOffline -> return HttpOutcome.Failed HttpFailure.Network
                        | WritesLost -> return (if read then server.Send sent else HttpOutcome.OutcomeUnknown UnknownReason.ConnectionLost)
                        | Answers status -> return HttpOutcome.Response(status, Map.empty, "{\"message\":\"x\"}")
                    }
              Wait = fun wait -> async { elapsed <- elapsed + wait }
              Tokens = fun () -> async { return server.ValidToken() } }

        GitHubStorage.provider host (GitHubConfig.create location)

type private Run =
    { Schedule: SyncSchedule
      Queue: OfflineQueue
      Attempts: (DateTimeOffset * SyncAttempt) list
      States: string list }

    member this.Final = SaveState.ofQueue this.Schedule this.Queue |> SaveState.name

let private simulate (h: Harness) (until: DateTimeOffset) (events: (DateTimeOffset * SyncEvent) list) (queue: OfflineQueue) =
    let provider = h.Provider
    let store = memoryStore ()
    let sample (index: int) = Random(17 + index).NextDouble()

    let rec loop (now: DateTimeOffset) events (run: Run) =
        let next =
            [ SyncSchedule.wakeAt now run.Schedule; events |> List.tryHead |> Option.map fst ]
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
                    States = (SaveState.ofQueue schedule run.Queue |> SaveState.name) :: run.States }
        | Some at, _ ->
            let at = max now at

            if SyncSchedule.isDue at run.Schedule then
                let started = SyncSchedule.apply at 0.0 SyncEvent.Started run.Schedule
                h.Elapsed <- TimeSpan.Zero
                let queue, attempt = OfflineSync.attempt provider store causa run.Queue |> Async.RunSynchronously
                let finished = at + h.Elapsed
                let schedule = SyncSchedule.apply finished (sample run.Attempts.Length) (SyncEvent.Finished attempt) started

                loop
                    finished
                    events
                    { Schedule = schedule
                      Queue = queue
                      Attempts = (at, attempt) :: run.Attempts
                      States = (SaveState.ofQueue schedule queue |> SaveState.name) :: run.States }
            else
                loop (at + TimeSpan.FromMilliseconds 1.0) events run

    let schedule =
        SyncSchedule.create RetryPolicy.standard Connectivity.Online |> ok |> SyncSchedule.apply t0 0.0 SyncEvent.Changed

    loop t0 events { Schedule = schedule; Queue = queue; Attempts = []; States = [] }

let private deferral (run: Run) = run.Schedule.Deferral |> Option.map Deferral.name

[<Fact>]
let ``navigator offline: no write leaves the device in 60 s, and the state waits for the connection`` () =
    let h = Harness(fun () -> NavigatorOffline)
    let run = simulate h window [] (queueOf [ "a" ])
    Assert.Equal(0, h.Writes)
    Assert.InRange(run.Attempts.Length, 1, 2)
    Assert.Equal("waiting-for-connection", run.Final)

[<Fact>]
let ``R6-D-003 through the adapter: lost writes stay bounded (at most 25 write requests in 60 s) and never confirmed`` () =
    let h = Harness(fun () -> WritesLost)
    let run = simulate h window [] (queueOf [ "a" ])
    Assert.InRange(h.Writes, 1, 25)
    Assert.InRange(run.Attempts.Length, 1, 8)
    Assert.DoesNotContain("confirmed", run.States)

[<Fact>]
let ``a 503 is a transient server error: retried with backoff, shown as a scheduled retry, not as a lost connection`` () =
    let h = Harness(fun () -> Answers 503)
    let run = simulate h window [] (queueOf [ "a" ])
    Assert.Equal(Some "transient", deferral run)
    Assert.Equal(Connectivity.Online, run.Schedule.Connectivity)
    Assert.Equal("retry-scheduled", run.Final)
    Assert.InRange(run.Attempts.Length, 2, 8)

[<Fact>]
let ``a 500 is transient, not a permanent failure: it is retried`` () =
    let h = Harness(fun () -> Answers 500)
    let run = simulate h window [] (queueOf [ "a" ])
    Assert.Equal(Some "transient", deferral run)
    Assert.NotEqual<string>("failed-permanently", run.Final)
    Assert.InRange(run.Attempts.Length, 2, 8)

[<Fact>]
let ``after a server outage the save lands once and is confirmed`` () =
    // GitHub answers 503 to its first 12 requests, then recovers.
    let requests = ref 0

    let mode () =
        requests.Value <- requests.Value + 1
        if requests.Value <= 12 then Answers 503 else Up

    let h = Harness mode
    let run = simulate h (t0 + TimeSpan.FromMinutes 5.0) [] (queueOf [ "a" ])
    Assert.Equal("confirmed", run.Final)
    // One ref update reached GitHub (the 503s never did), and the file is on main.
    Assert.Equal(1, h.Server.Requests |> List.filter (fun (a: Authorized) -> a.Request.Method = HttpMethod.Patch) |> List.length)
    Assert.Contains(h.Server.Files "main" |> Map.keys, fun path -> path.EndsWith "investigations/a.json")

[<Fact>]
let ``a 403 refusal is sent once and reported as failed permanently`` () =
    let h = Harness(fun () -> Answers 403)
    let run = simulate h window [] (queueOf [ "a" ])
    Assert.Equal(1, run.Attempts.Length)
    Assert.Equal("failed-permanently", run.Final)

[<Fact>]
let ``a 401 is attempted once and edits every 2 s never send it again; resolving allows exactly one more attempt`` () =
    let h = Harness(fun () -> Answers 401)
    let edits = [ for i in 1..29 -> t0 + TimeSpan.FromSeconds(2.0 * float i), SyncEvent.Changed ]
    let run = simulate h window edits (queueOf [ "a" ])
    Assert.Equal(1, run.Attempts.Length)
    Assert.Equal("needs-credential", run.Final)

    let resolved = simulate h window (edits @ [ t0 + TimeSpan.FromSeconds 59.0, SyncEvent.Resolved ]) (queueOf [ "a" ])
    Assert.Equal(2, resolved.Attempts.Length)

[<Fact>]
let ``repeated online events (focus, visibility, polling) never reset the backoff`` () =
    let quiet = simulate (Harness(fun () -> WritesLost)) window [] (queueOf [ "a" ])
    let online = [ for i in 1..239 -> t0 + TimeSpan.FromMilliseconds(250.0 * float i), SyncEvent.ConnectivityChanged Connectivity.Online ]
    let noisy = simulate (Harness(fun () -> WritesLost)) window online (queueOf [ "a" ])
    Assert.Equal(quiet.Attempts.Length, noisy.Attempts.Length)

[<Fact>]
let ``a write lost and then the device offline: the state waits for the connection, not an endless check`` () =
    let offline = ref false
    let h = Harness(fun () -> if offline.Value then NavigatorOffline else Up)

    h.Server.Inject(
        (fun (a: Authorized) ->
            let patch = a.Request.Method = HttpMethod.Patch
            if patch then offline.Value <- true
            patch),
        Lose UnknownReason.ConnectionLost
    )

    let run = simulate h (t0 + TimeSpan.FromSeconds 120.0) [] (queueOf [ "a" ])
    Assert.Equal("waiting-for-connection", run.Final)
