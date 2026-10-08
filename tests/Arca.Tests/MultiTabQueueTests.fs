/// Several browser tabs of one application share one localStorage, so they
/// share one persisted offline queue per namespace (ARCA-OFF-002, WI-0024).
/// The QueueStore port saves whole snapshots: a tab that saves a snapshot
/// built from a stale load must never overwrite an entry another tab's save
/// already acknowledged.
module Arca.Tests.MultiTabQueueTests

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private run work = Async.RunSynchronously work

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "multi-tab" }
          Location = location }
    |> ok

let private at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)

let private create (key: string) =
    Operation.create
        chrona
        { Summary = $"note {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"offline-{key}" |> ok }
        [ Change.Create(RelativePath.parse $"notes/{key}.json" |> ok, $"{{\"n\":\"{key}\"}}") ]
    |> ok

let private enqueue key queue = OfflineQueue.enqueue at (create key) queue |> ok |> fst

let private keysOf (queue: OfflineQueue) = queue.Entries |> List.map _.Operation.IdempotencyKey

/// One origin's localStorage, shared by every tab that executes against it.
type SharedLocalStorage() =
    let items = Dictionary<string, string>()
    member _.Items = items

    member _.Execute(request: LocalStorageRequest) =
        async {
            match request with
            | LocalStorageRequest.Get key ->
                match items.TryGetValue key with
                | true, value -> return LocalStorageOutcome.Success(Some value)
                | _ -> return LocalStorageOutcome.Success None
            | LocalStorageRequest.Set(key, value) ->
                items[key] <- value
                return LocalStorageOutcome.Success None
            | LocalStorageRequest.Remove key ->
                items.Remove key |> ignore
                return LocalStorageOutcome.Success None
        }

    /// What a freshly opened tab would load.
    member this.Stored(ns: Namespace) =
        LocalStorageQueue.store this.Execute LocalStorageQueue.DefaultBudget ns |> fun store -> store.Load() |> run

/// Two tabs load the same (empty) queue, each enqueues one change and saves.
/// With one whole snapshot and no ownership, the second save overwrote the
/// first, and tab A's acknowledged entry was gone.
[<Fact>]
let ``a second tab's save never overwrites an entry the first tab's save acknowledged`` () =
    let browser = SharedLocalStorage()
    let tabA = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    let tabB = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona

    let queueA = tabA.Load() |> run |> ok |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)
    let queueB = tabB.Load() |> run |> ok |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)

    Assert.Equal(Ok(), tabA.Save(enqueue "a" queueA) |> run)
    // Tab B's snapshot was built before tab A saved; it does not hold "a".
    let _ = tabB.Save(enqueue "b" queueB) |> run

    match browser.Stored chrona with
    | Ok(Some stored) -> Assert.Contains("offline-a", keysOf stored)
    | other -> failwith $"expected the stored queue, got {other}"

/// One step of one tab.
type private TabStep =
    | Load of tab: int
    | Enqueue of tab: int
    | Save of tab: int

/// Any interleaving of two tabs loading, enqueuing and saving: every entry a
/// Save acknowledged is still stored at the end (nothing here synchronizes or
/// prunes, so nothing may leave the store).
[<Property(MaxTest = 300)>]
let ``no interleaving of two tabs loses an acknowledged entry`` () =
    let step =
        Gen.zip (Gen.elements [ 0; 1 ]) (Gen.elements [ 0; 1; 2 ])
        |> Gen.map (fun (tab, kind) ->
            match kind with
            | 0 -> Load tab
            | 1 -> Enqueue tab
            | _ -> Save tab)

    Prop.forAll (Arb.fromGen (Gen.listOf step)) (fun steps ->
        let browser = SharedLocalStorage()
        let tabs = [| for _ in 0..1 -> LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona |]
        let empty = OfflineQueue.create OfflinePolicy.QueueWrites

        let _, acknowledged, _ =
            steps
            |> List.fold
                (fun (queues: Map<int, OfflineQueue>, acknowledged: Set<string>, counter: int) step ->
                    match step with
                    | Load tab ->
                        match tabs[tab].Load() |> run with
                        | Ok loaded -> queues |> Map.add tab (loaded |> Option.defaultValue empty), acknowledged, counter
                        | Error _ -> queues, acknowledged, counter
                    | Enqueue tab ->
                        let queue = queues |> Map.tryFind tab |> Option.defaultValue empty
                        queues |> Map.add tab (enqueue $"t{tab}-{counter}" queue), acknowledged, counter + 1
                    | Save tab ->
                        let queue = queues |> Map.tryFind tab |> Option.defaultValue empty

                        match tabs[tab].Save queue |> run with
                        | Ok() -> queues, Set.union acknowledged (Set.ofList (keysOf queue)), counter
                        | Error _ -> queues, acknowledged, counter)
                (Map.empty, Set.empty, 0)

        let stored =
            match browser.Stored chrona with
            | Ok(Some queue) -> Set.ofList (keysOf queue)
            | _ -> Set.empty

        Set.isSubset acknowledged stored |> Prop.label $"acknowledged {acknowledged}, stored {stored}")
