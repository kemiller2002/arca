/// A Limen engine for the IndexedDB offline queue across real tabs (WI-0016,
/// Limen LCP-059, LCP-060). Each tab runs `EchelonFoundry.Arca.Limen`'s
/// composer for one namespace: IndexedDB through Limen's store pack
/// (`limen.store` v2), ownership through the coordination pack's Web Lock,
/// and localStorage through Limen Core's Storage effect. Requests cross as
/// the generated contract codecs write them; the engine has no browser or
/// interop code.
module Arca.Browser.LimenQueueEngine

open System
open System.IO
open System.Text
open System.Text.Json
open Arca
open Arca.GitHub
open Arca.Limen
open Limen.Contract

[<Literal>]
let StoreUnit = "limen.store"

[<Literal>]
let StoreFingerprint = Limen.Contract.Store.Contract.Fingerprint

/// An effect asked of the browser.
type Effect =
    | Storage of LocalStorageRequest
    | Lock of Limen.Contract.Coordination.Types.CoordinationRequest
    | Store of Limen.Contract.Store.Types.StoreRequest

/// What the browser answered.
type Answer =
    | Stored of LocalStorageOutcome
    | Locked of Limen.Contract.Coordination.Types.CoordinationResult
    | Kept of Limen.Contract.Store.Types.StoreResult

/// What the page shows.
[<NoEquality; NoComparison>]
type Page =
    { Ownership: string
      Owned: OwnedQueue option
      Queue: OfflineQueue
      Last: string
      Tag: string
      Coordination: bool
      Store: bool
      /// The read-cache conformance verdict, once run.
      Cache: string
      Runs: int }

let private ok result =
    match result with
    | Ok value -> value
    | Error _ -> invalidOp "verification fixture"

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "browser-verification" }
          Location = DataLocation.create "verify-owner" "verify-data" "main" "apps" |> ok }
    |> ok

let private empty = OfflineQueue.create OfflinePolicy.QueueWrites

let initial (tag: string) =
    { Ownership = "none"
      Owned = None
      Queue = empty
      Last = ""
      Tag = tag
      Coordination = false
      Store = false
      Cache = ""
      Runs = 0 }

let private operation (key: string) =
    Operation.create
        chrona
        { Summary = $"note {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "browser" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create key |> ok }
        [ Change.Create(RelativePath.parse $"notes/{key}.json" |> ok, "{}") ]
    |> ok

let private failureText =
    function
    | QueueStoreFailure.Unavailable -> "unavailable"
    | QueueStoreFailure.QuotaExceeded _ -> "quota-exceeded"
    | QueueStoreFailure.Corrupt _ -> "corrupt"

let private modeText =
    function
    | DurabilityMode.IndexedDb -> "indexeddb"
    | DurabilityMode.LocalStorage _ -> "localstorage"
    | DurabilityMode.MemoryOnly -> "memory"

let private ownershipText =
    function
    | OwnershipState.Owner _ -> "owned"
    | OwnershipState.OwnedElsewhere -> "fenced"
    | OwnershipState.ConnectionLost -> "connection-lost"
    | OwnershipState.Released -> "released"

let private opened (page: Page) (opening: QueueOpening) =
    async {
        match opening with
        | QueueOpening.Owned queue ->
            match! queue.Store.Load() with
            | Ok kept ->
                return
                    { page with
                        Ownership = "owned"
                        Owned = Some queue
                        Queue = kept |> Option.map OfflineQueue.recover |> Option.defaultValue empty
                        Last = "loaded" }
            | Error failure -> return { page with Ownership = "owned"; Owned = Some queue; Last = $"load {failureText failure}" }
        | QueueOpening.OwnedElsewhere -> return { page with Ownership = "owned-elsewhere" }
        | QueueOpening.OwnershipUnsupported -> return { page with Ownership = "unsupported" }
        | QueueOpening.NothingUsable _ -> return { page with Ownership = "nothing-usable" }
    }

/// What one page event does, given the host.
let onEvent (host: LimenHost) (page: Page) (name: string) =
    let options = { QueueOptions.standard with Order = [ DurabilityChoice.IndexedDb ] }

    async {
        match name, page.Owned with
        | "own", None ->
            let! opening = LimenQueue.own host options chrona
            return! opened page opening
        | "take-over", _ ->
            let! opening = LimenQueue.takeOver host options chrona
            return! opened page opening
        | "enqueue", _ ->
            let key = $"{page.Tag}-{page.Queue.NextSequence}"

            match OfflineQueue.enqueue DateTimeOffset.UnixEpoch (operation key) page.Queue with
            | Ok(queue, _) -> return { page with Queue = queue; Last = $"enqueued {key}" }
            | Error _ -> return { page with Last = "enqueue refused" }
        | "cache-conformance", _ ->
            // The read-cache conformance suite against IndexedDB in this
            // browser (WI-0022, LCP-082): each case on a fresh namespace's
            // entries, reopened as a new connection; a fault this page cannot
            // produce is reported unsupported, never passed.
            let run = page.Runs + 1

            let fresh () =
                async {
                    let ns =
                        Namespace.ofApplication
                            { Application = AppId.create $"cache{run}x{Guid.NewGuid():N}".[..30] |> ok
                              Environment = { Kind = EnvironmentKind.Test; Name = "browser-verification" }
                              Location = DataLocation.create "verify-owner" "verify-data" "main" "apps" |> ok }
                        |> ok

                    match! IndexedDbReadCache.openCache host IndexedDbReadCache.DefaultBudget with
                    | Error failure -> return invalidOp failure.Code
                    | Ok cache ->
                        return
                            { Namespace = ns
                              Store = cache.Store
                              Reopen =
                                fun () ->
                                    async {
                                        match! IndexedDbReadCache.openCache host IndexedDbReadCache.DefaultBudget with
                                        | Ok reopened -> return reopened.Store
                                        | Error failure -> return invalidOp failure.Code
                                    }
                              Arrange = fun _ -> async { return false } }
                }

            let! results = ReadCacheConformance.run fresh
            let count predicate = results |> List.filter (fun result -> predicate result.Outcome) |> List.length
            let passed = count (fun outcome -> outcome = ConformanceOutcome.Passed)
            let failed = count (function ConformanceOutcome.Failed _ -> true | _ -> false)
            let unsupported = count (function ConformanceOutcome.Unsupported _ -> true | _ -> false)

            let failures =
                results
                |> List.choose (fun result ->
                    match result.Outcome with
                    | ConformanceOutcome.Failed reason -> Some $"{result.Case}: {reason}"
                    | _ -> None)
                |> String.concat "; "

            return
                { page with
                    Runs = run
                    Cache = $"passed {passed}, failed {failed}, unsupported {unsupported}"
                    Last = if failures = "" then "cache conformance run" else failures }
        | "save", Some queue ->
            match! queue.Store.Save page.Queue with
            | Ok() -> return { page with Last = "saved" }
            | Error failure -> return { page with Last = $"save {failureText failure}" }
        | _ -> return page
    }

// ---------------------------------------------------------------------------
// Limen protocol.
// ---------------------------------------------------------------------------

let private text (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
    | _ -> None

let private child (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Object -> Some value
    | _ -> None

/// A capability result's payload, when the pack completed the request.
let private completed (outcome: JsonElement) =
    match text "kind" outcome, child "result" outcome with
    | Some "Completed", Some result -> Some(result.GetRawText())
    | _ -> None

let private writeCapability (writer: Utf8JsonWriter) (correlation: string) (unit: string) (version: int) (request: string) =
    writer.WriteString("kind", "Capability")
    writer.WriteString("correlationId", correlation)
    writer.WriteString("capability", unit)
    writer.WriteNumber("version", version)
    writer.WritePropertyName "request"
    use parsed = JsonDocument.Parse request
    parsed.RootElement.WriteTo writer

let private writeEffect (writer: Utf8JsonWriter) (correlation: string) (effect: Effect) =
    writer.WriteStartObject()

    match effect with
    | Storage request ->
        writer.WriteString("kind", "Storage")
        writer.WriteString("correlationId", correlation)

        match request with
        | LocalStorageRequest.Get key ->
            writer.WriteString("operation", "get")
            writer.WriteString("key", key)
        | LocalStorageRequest.Set(key, value) ->
            writer.WriteString("operation", "set")
            writer.WriteString("key", key)
            writer.WriteString("value", value)
        | LocalStorageRequest.Remove key ->
            writer.WriteString("operation", "remove")
            writer.WriteString("key", key)
    | Lock request -> writeCapability writer correlation QueueEngine.CoordinationUnit 1 (Limen.Contract.Coordination.Codec.serializeCoordinationRequest request)
    | Store request -> writeCapability writer correlation StoreUnit 2 (Limen.Contract.Store.Codec.serializeStoreRequest request)

    writer.WriteEndObject()

let private view (page: Page) =
    let diagnostics = page.Owned |> Option.map (fun queue -> queue.Diagnostics())

    [ "ownership",
      (match diagnostics with
       | Some d -> ownershipText d.Ownership
       | None -> page.Ownership)
      "mode", (page.Owned |> Option.map (_.Mode >> modeText) |> Option.defaultValue "")
      "epoch",
      (match diagnostics |> Option.map _.Ownership with
       | Some(OwnershipState.Owner epoch) -> string epoch
       | _ -> "")
      "entries", string page.Queue.Entries.Length
      "keys", (page.Queue.Entries |> List.map _.Operation.IdempotencyKey |> String.concat ",")
      "cache", page.Cache
      "last", page.Last ]

let reply (page: Page) (effects: (string * Effect) list) (handshake: (Utf8JsonWriter -> unit) option) =
    use stream = new MemoryStream()

    do
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writer.WriteStartObject()

        for name, value in view page do
            writer.WriteString(name, value)

        writer.WriteEndObject()
        writer.WritePropertyName "effects"
        writer.WriteStartArray()

        for correlation, effect in effects do
            writeEffect writer correlation effect

        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | Some write ->
            writer.WritePropertyName "handshake"
            write writer
        | None -> ()

        writer.WriteEndObject()

    Encoding.UTF8.GetString(stream.ToArray())

/// Accepts Limen Core and selects the coordination pack and the store pack
/// (version 2) when the kernel offers them.
let answer (offer: JsonElement option) =
    let number (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt32())
        | _ -> None

    let protocol = offer |> Option.bind (child "protocol")
    let contract = offer |> Option.bind (child "contract")

    let offered (id: string) (version: int) (fingerprint: string) =
        match offer |> Option.map (fun o -> o.TryGetProperty "capabilities") with
        | Some(true, list) when list.ValueKind = JsonValueKind.Array ->
            list.EnumerateArray()
            |> Seq.exists (fun item -> text "id" item = Some id && number "version" item = Some version && text "fingerprint" item = Some fingerprint)
        | _ -> false

    let coordination = offered QueueEngine.CoordinationUnit 1 QueueEngine.CoordinationFingerprint
    let store = offered StoreUnit 2 StoreFingerprint

    match protocol |> Option.bind (number "major"), protocol |> Option.bind (number "minor"), contract with
    | Some 1, Some minor, Some offeredContract when minor >= Engine.MinimumMinor && text "unit" offeredContract = Some Engine.CoreUnit && text "fingerprint" offeredContract = Some Engine.CoreFingerprint ->
        let raw = offeredContract.GetRawText()

        let select (writer: Utf8JsonWriter) (id: string) (version: int) (fingerprint: string) =
            writer.WriteStartObject()
            writer.WriteString("id", id)
            writer.WriteNumber("version", version)
            writer.WriteString("fingerprint", fingerprint)
            writer.WriteEndObject()

        Ok(
            (fun (writer: Utf8JsonWriter) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Accepted")
                writer.WritePropertyName "protocol"
                writer.WriteStartObject()
                writer.WriteNumber("major", 1)
                writer.WriteNumber("minor", min minor Engine.NewestMinor)
                writer.WriteEndObject()
                writer.WritePropertyName "contract"
                use parsed = JsonDocument.Parse raw
                parsed.RootElement.WriteTo writer
                writer.WritePropertyName "capabilities"
                writer.WriteStartArray()

                if coordination then
                    select writer QueueEngine.CoordinationUnit 1 QueueEngine.CoordinationFingerprint

                if store then
                    select writer StoreUnit 2 StoreFingerprint

                writer.WriteEndArray()
                writer.WriteEndObject()),
            coordination,
            store
        )
    | _ ->
        Error(fun (writer: Utf8JsonWriter) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "Rejected")
            writer.WritePropertyName "reason"
            writer.WriteStartObject()
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WriteEndObject()
            writer.WriteEndObject())

/// The one stateful edge: the page and the work waiting on the browser,
/// between WASM calls (one thread; work parks under a correlation id).
module Dispatch =
    let mutable private page = initial (Guid.NewGuid().ToString("N").Substring(0, 8))
    let mutable private waiting: Map<string, Effect * (Answer -> unit)> = Map.empty
    let mutable private outbox: (string * Effect) list = []
    let mutable private sequence = 0

    let private call (effect: Effect) =
        Async.FromContinuations(fun (resume, _, _) ->
            sequence <- sequence + 1
            let correlation = $"l{sequence}"
            waiting <- waiting |> Map.add correlation (effect, resume)
            outbox <- outbox @ [ correlation, effect ])

    let private host: LimenHost =
        { Store =
            fun request ->
                async {
                    if not page.Store then
                        return Limen.Contract.Store.Types.StoreResult.Unavailable "no store pack"
                    else
                        match! call (Store request) with
                        | Kept result -> return result
                        | _ -> return Limen.Contract.Store.Types.StoreResult.Unavailable "unexpected answer"
                }
          Lock =
            fun request ->
                async {
                    if not page.Coordination then
                        return Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
                    else
                        match! call (Lock request) with
                        | Locked result -> return result
                        | _ -> return Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
                }
          LocalStorage =
            fun request ->
                async {
                    match! call (Storage request) with
                    | Stored outcome -> return outcome
                    | _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
                }
          Now = fun () -> DateTimeOffset.UnixEpoch }

    let private start (work: Async<Page>) =
        Async.StartImmediate(
            async {
                let! next = work
                page <- next
            }
        )

    let private drain handshake =
        let effects = outbox
        outbox <- []
        reply page effects handshake

    /// The answer to a parked effect, decoded with the generated codec of the
    /// pack that effect went to.
    let private answerFor (effect: Effect) (result: JsonElement) =
        match effect, text "kind" result, child "outcome" result with
        | Storage _, Some "StorageResult", Some outcome -> Stored(QueueEngine.storageOutcome outcome)
        | Lock _, Some "CapabilityResult", Some outcome ->
            match completed outcome |> Option.map Limen.Contract.Coordination.Codec.parseCoordinationResult with
            | Some(Ok decoded) -> Locked decoded
            | _ -> Locked Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
        | Store _, Some "CapabilityResult", Some outcome ->
            match completed outcome |> Option.map Limen.Contract.Store.Codec.parseStoreResult with
            | Some(Ok decoded) -> Kept decoded
            | _ -> Kept(Limen.Contract.Store.Types.StoreResult.Unavailable "the pack did not complete the request")
        | Storage _, _, _ -> Stored(LocalStorageOutcome.Failure LocalStorageFailure.Unavailable)
        | Lock _, _, _ -> Locked Limen.Contract.Coordination.Types.CoordinationResult.Unsupported
        | Store _, _, _ -> Kept(Limen.Contract.Store.Types.StoreResult.Unavailable "no answer")

    /// One kernel message in, the engine's reply out.
    let handle (messageJson: string) =
        use document = JsonDocument.Parse messageJson
        let message = document.RootElement

        match text "kind" message with
        | Some "Initialize" ->
            match answer (child "handshake" message) with
            | Ok(accepted, coordination, store) ->
                page <- { page with Coordination = coordination; Store = store }
                drain (Some accepted)
            | Error rejected -> drain (Some rejected)
        | Some "Event" ->
            match child "event" message |> Option.bind (text "name") with
            | Some name when waiting.IsEmpty -> start (onEvent host page name)
            | _ -> ()

            drain None
        | Some "EffectResult" ->
            match child "result" message with
            | Some result ->
                match text "correlationId" result |> Option.bind (fun correlation -> waiting |> Map.tryFind correlation |> Option.map (fun parked -> correlation, parked)) with
                | Some(correlation, (effect, resume)) ->
                    waiting <- waiting |> Map.remove correlation
                    resume (answerFor effect result)
                | None -> ()
            | None -> ()

            drain None
        | _ -> drain None
