namespace Arca.Limen

open System
open System.Text
open Arca
open Limen.Contract
open Limen.Store

/// One cached partition as IndexedDB holds it.
type CacheRecord =
    { Account: string
      Namespace: string
      Partition: string
      /// The entry's canonical text (`ReadCache.encode`).
      Entry: string
      /// The entry's UTF-8 size, counted against the namespace's budget.
      Size: int64
      /// When the partition was last saved or shown, in milliseconds since
      /// the Unix epoch (the host's clock), for least-recently-used eviction.
      LastUsed: int64 }

/// What the application can show about the read cache (LCP-073, LCP-087).
type CacheDiagnostics =
    { Budget: int64
      /// Bytes held for the namespace last measured; None until measured.
      Size: int64 option
      /// Partitions evicted to stay within the budget, or to free space for
      /// the queue.
      Evictions: int
      /// The last failure; a failed cache write never fails a read.
      LastFailure: AdapterFailure option }

/// The IndexedDB read cache (WI-0022; Limen LCP-082..LCP-087). Any tab may
/// write it: each entry is a whole provider-validated partition written with
/// `put`, and two tabs writing one partition both hold valid states.
[<NoEquality; NoComparison>]
type IndexedDbCache =
    { /// The read-cache port.
      Store: ReadCacheStore
      /// Saves a partition just read from the provider, best-effort: a
      /// failure (quota, size limit, unavailable) is reported in diagnostics
      /// only, and never fails the read that produced the entry.
      Keep: CacheEntry -> Async<unit>
      /// Saves many partitions in one IndexedDB transaction, all or nothing
      /// (ARCA-OFF-008, arca#38). The order, contents, evictions and
      /// diagnostics are those of calling `Store.Save` for each entry in
      /// turn; a later entry for the same key replaces an earlier one. One
      /// entry that cannot be stored (over the budget, or one `ReadCache.encode`
      /// refuses) refuses the whole batch before anything is written, and a
      /// transaction that fails leaves every partition as it was.
      SaveMany: CacheEntry list -> Async<Result<unit, ReadCacheFailure>>
      /// `SaveMany`, best-effort: a failure is reported in diagnostics only,
      /// as with `Keep`.
      KeepMany: CacheEntry list -> Async<unit>
      /// Loads a partition through `ReadCache.load` (validated, a corrupt
      /// entry dropped) and marks it used for eviction.
      Show: CacheKey -> Async<Result<Cached<CacheEntry> option, ReadCacheFailure>>
      Diagnostics: unit -> CacheDiagnostics
      /// Evicts every cached partition, to make room for the queue (LCP-087).
      /// True when anything was freed.
      FreeSpace: unit -> Async<bool> }

[<RequireQualifiedAccess>]
module IndexedDbReadCache =

    [<Literal>]
    let Database = "arca-cache"

    [<Literal>]
    let Entries = "entries"

    [<Literal>]
    let ByNamespace = "byNamespace"

    /// The default per-namespace budget: 4 MiB of cached entries.
    [<Literal>]
    let DefaultBudget = 4_194_304L

    let schema =
        Schema.create
            Database
            1L
            [ { Name = Entries
                KeyPath = KeyPath.Compound [ "account"; "namespace"; "partition" ]
                Indexes =
                  [ { Name = ByNamespace
                      KeyPath = KeyPath.Compound [ "namespace"; "lastUsed" ]
                      Unique = false
                      MultiEntry = false } ] } ]
            []

    let codec: Codec<CacheRecord> =
        Codec.record
            (fun record ->
                Codec.fields
                    [ Codec.field "account" Codec.string record.Account
                      Codec.field "namespace" Codec.string record.Namespace
                      Codec.field "partition" Codec.string record.Partition
                      Codec.field "entry" Codec.string record.Entry
                      Codec.field "size" Codec.int64 record.Size
                      Codec.field "lastUsed" Codec.int64 record.LastUsed ])
            (fun fields ->
                match
                    Codec.required "account" Codec.string fields,
                    Codec.required "namespace" Codec.string fields,
                    Codec.required "partition" Codec.string fields,
                    Codec.required "entry" Codec.string fields,
                    Codec.required "size" Codec.int64 fields,
                    Codec.required "lastUsed" Codec.int64 fields
                with
                | Ok account, Ok ns, Ok partition, Ok entry, Ok size, Ok lastUsed ->
                    Ok
                        { Account = account
                          Namespace = ns
                          Partition = partition
                          Entry = entry
                          Size = size
                          LastUsed = lastUsed }
                | Error e, _, _, _, _, _
                | _, Error e, _, _, _, _
                | _, _, Error e, _, _, _
                | _, _, _, Error e, _, _
                | _, _, _, _, Error e, _
                | _, _, _, _, _, Error e -> Error e)

    /// The compound key `[account, namespace, partition]` (LCP-083).
    let key (cacheKey: CacheKey) =
        Key.Tuple [ Key.Text cacheKey.Account; Key.Text cacheKey.Namespace; Key.Text cacheKey.Partition ]

    /// The keys a clear covers: one `deleteRange` over a compound-key prefix
    /// (LCP-086), or the whole store.
    let range (scope: CacheScope) =
        match scope with
        | CacheScope.Everything -> Ok None
        | CacheScope.Account account -> Range.prefix [ Key.Text account ] |> Result.map Some
        | CacheScope.AccountNamespace(account, ns) -> Range.prefix [ Key.Text account; Key.Text ns ] |> Result.map Some

    let private millis (at: DateTimeOffset) = at.ToUnixTimeMilliseconds()

    /// The record for an entry, used at `at`.
    let recordOf (at: DateTimeOffset) (entry: CacheEntry) =
        ReadCache.encode entry
        |> Result.map (fun text ->
            { Account = entry.Key.Account
              Namespace = entry.Key.Namespace
              Partition = entry.Key.Partition
              Entry = text
              Size = int64 (Encoding.UTF8.GetByteCount text)
              LastUsed = millis at })

    /// Which partitions to evict, least recently used first, so that `needed`
    /// more bytes fit the budget. `held` is the namespace's records in
    /// last-used order; the partition being written is not counted.
    let evictions (budget: int64) (needed: int64) (writing: CacheKey) (held: CacheRecord list) =
        let others =
            held |> List.filter (fun record -> not (record.Account = writing.Account && record.Partition = writing.Partition))

        let total = others |> List.sumBy _.Size

        let rec take (excess: int64) (records: CacheRecord list) chosen =
            match records with
            | _ when excess <= 0L -> List.rev chosen
            | [] -> List.rev chosen
            | record :: rest -> take (excess - record.Size) rest (record :: chosen)

        take (total + needed - budget) others []

    /// A cache key's parts, as a record carries them.
    let private keyOf (record: CacheRecord) =
        { Account = record.Account
          Namespace = record.Namespace
          Partition = record.Partition }

    /// The order the `byNamespace` index keeps a namespace's records in: last
    /// used, then the primary key, each text compared ordinally.
    let private lastUsedOrder (a: CacheRecord) (b: CacheRecord) =
        [ compare a.LastUsed b.LastUsed
          String.CompareOrdinal(a.Account, b.Account)
          String.CompareOrdinal(a.Namespace, b.Namespace)
          String.CompareOrdinal(a.Partition, b.Partition) ]
        |> List.tryFind ((<>) 0)
        |> Option.defaultValue 0

    /// What saving `records` in turn does to what each namespace holds
    /// (`held`, each in last-used order): for each record, in order, the
    /// partitions it evicts, and what every namespace holds afterwards. A
    /// record larger than the whole budget refuses the batch (ARCA-OFF-008).
    let plan (budget: int64) (held: Map<string, CacheRecord list>) (records: CacheRecord list) =
        let step (evictedBefore: (CacheRecord list * CacheRecord) list, holding: Map<string, CacheRecord list>) (record: CacheRecord) =
            let current = holding |> Map.tryFind record.Namespace |> Option.defaultValue []
            let evicted = evictions budget record.Size (keyOf record) current

            let kept =
                current
                |> List.filter (fun r -> not (evicted |> List.contains r) && not (r.Account = record.Account && r.Partition = record.Partition))

            (evicted, record) :: evictedBefore, holding.Add(record.Namespace, kept @ [ record ] |> List.sortWith lastUsedOrder)

        match records |> List.tryFind (fun record -> record.Size > budget) with
        | Some oversized -> Error oversized
        | None ->
            let steps, after = records |> List.fold step ([], held)
            Ok(List.rev steps, after)

    let private failureOf (failure: TransactFailure) =
        match failure with
        | TransactFailure.QuotaExceeded -> ReadCacheFailure.QuotaExceeded
        | TransactFailure.Invalid _ -> ReadCacheFailure.QuotaExceeded
        | _ -> ReadCacheFailure.Unavailable

    let private problem (operation: string) (failure: BuildProblem) =
        AdapterFailure.create operation Database (Some Entries) FailureClass.Invalid "request" failure.Problem

    /// Opens the cache database for a namespace's budget. Any tab may.
    let openCache (host: LimenHost) (budget: int64) =
        async {
            match schema with
            | Error failure -> return Error(AdapterFailure.create "open" Database None FailureClass.Invalid "schema" failure.Problem)
            | Ok schema ->
                match! Store.openDatabase host.Store schema with
                | Error failure -> return Error(AdapterFailure.ofOpen Database failure)
                | Ok opened ->
                    let connection = ref (Connection.Open opened)

                    let diagnostics =
                        ref
                            { Budget = budget
                              Size = None
                              Evictions = 0
                              LastFailure = None }

                    let diagnose f = diagnostics.Value <- f diagnostics.Value
                    let fail (failure: AdapterFailure) = diagnose (fun d -> { d with LastFailure = Some failure })

                    let transact operation (transaction: Result<Transaction<'Mode>, BuildProblem>) =
                        async {
                            match transaction with
                            | Error failure ->
                                fail (problem operation failure)
                                return Error ReadCacheFailure.Unavailable
                            | Ok transaction ->
                                match! Store.transact host.Store connection.Value transaction with
                                | Ok results -> return Ok results
                                | Error failure ->
                                    if failure = TransactFailure.NotOpen then
                                        connection.Value <- Connection.Lost

                                    fail (AdapterFailure.ofTransact operation Database Entries failure)
                                    return Error(failureOf failure)
                        }

                    /// What each namespace holds, in last-used order, read in
                    /// one transaction.
                    let held (namespaces: string list) =
                        async {
                            let queries =
                                namespaces
                                |> List.map (fun ns ->
                                    Range.prefix [ Key.Text ns ] |> Result.bind (fun range -> Op.query Entries (Some ByNamespace) (Some range) 1000 false))

                            match! transact "measure" (Transaction.readOnly Database queries) with
                            | Ok results when results.Length = namespaces.Length ->
                                let decoded = results |> List.map (Read.values codec Database Entries)

                                match decoded |> List.tryPick (function Error _ -> Some() | Ok _ -> None) with
                                | Some() -> return Error(ReadCacheFailure.Corrupt "a cache record does not decode")
                                | None ->
                                    return
                                        Ok(
                                            List.zip namespaces decoded
                                            |> List.choose (fun (ns, records) -> records |> Result.toOption |> Option.map (fun records -> ns, records))
                                            |> Map.ofList
                                        )
                            | Ok _ -> return Error ReadCacheFailure.Unavailable
                            | Error failure -> return Error failure
                        }

                    let load (cacheKey: CacheKey) =
                        async {
                            match! transact "load" (Transaction.readOnly Database [ Op.get Entries (key cacheKey) ]) with
                            | Ok [ result ] ->
                                match Read.value codec Database Entries (key cacheKey) result with
                                | Ok None -> return Ok None
                                | Ok(Some record) -> return ReadCache.decode record.Entry |> Result.map Some
                                | Error _ -> return Error(ReadCacheFailure.Corrupt "the cache record does not decode")
                            | Ok _ -> return Error ReadCacheFailure.Unavailable
                            | Error failure -> return Error failure
                        }

                    /// Saves entries in one transaction, all or nothing, as
                    /// saving each in turn would (ARCA-OFF-008). `Save` is the
                    /// batch of one, so the two cannot drift apart.
                    let saveMany (entries: CacheEntry list) =
                        let encoded =
                            entries
                            |> List.fold
                                (fun acc entry -> acc |> Result.bind (fun records -> recordOf (host.Now()) entry |> Result.map (fun record -> record :: records)))
                                (Ok [])
                            |> Result.map List.rev

                        async {
                            match encoded with
                            | Error failure -> return Error failure
                            | Ok [] -> return Ok()
                            | Ok records ->
                                match records |> List.tryFind (fun record -> record.Size > budget) with
                                | Some oversized ->
                                    fail (AdapterFailure.create "save" Database (Some Entries) FailureClass.Quota "budget" $"{oversized.Size} bytes over the budget of {budget}")
                                    return Error ReadCacheFailure.QuotaExceeded
                                | None ->
                                    match! held (records |> List.map _.Namespace |> List.distinct) with
                                    | Error failure -> return Error failure
                                    | Ok current ->
                                        match plan budget current records with
                                        | Error _ -> return Error ReadCacheFailure.QuotaExceeded
                                        | Ok(steps, after) ->
                                            let operations =
                                                steps
                                                |> List.collect (fun (evicted, record) ->
                                                    (evicted |> List.map (fun old -> Op.delete Entries (key (keyOf old)))) @ [ Op.put Entries codec record ])

                                            match! transact "save" (Transaction.readWrite Database operations) with
                                            | Ok _ ->
                                                let last = List.last records

                                                diagnose (fun d ->
                                                    { d with
                                                        Evictions = d.Evictions + (steps |> List.sumBy (fst >> List.length))
                                                        Size = after |> Map.tryFind last.Namespace |> Option.map (List.sumBy _.Size) })

                                                return Ok()
                                            | Error failure -> return Error failure
                        }

                    let save (entry: CacheEntry) = saveMany [ entry ]

                    let remove (cacheKey: CacheKey) =
                        async {
                            match! transact "remove" (Transaction.readWrite Database [ Op.delete Entries (key cacheKey) ]) with
                            | Ok _ -> return Ok()
                            | Error failure -> return Error failure
                        }

                    let partitions (account: string) (ns: string) =
                        async {
                            match Range.prefix [ Key.Text account; Key.Text ns ] with
                            | Error failure ->
                                fail (problem "partitions" failure)
                                return Error ReadCacheFailure.Unavailable
                            | Ok range ->
                                match! transact "partitions" (Transaction.readOnly Database [ Op.query Entries None (Some range) 1000 false ]) with
                                | Ok [ result ] ->
                                    match Read.values codec Database Entries result with
                                    | Ok records ->
                                        return
                                            Ok(
                                                records
                                                |> List.map (fun r ->
                                                    { Account = r.Account
                                                      Namespace = r.Namespace
                                                      Partition = r.Partition })
                                            )
                                    | Error _ -> return Error(ReadCacheFailure.Corrupt "a cache record does not decode")
                                | Ok _ -> return Error ReadCacheFailure.Unavailable
                                | Error failure -> return Error failure
                        }

                    let clear (scope: CacheScope) =
                        async {
                            match range scope with
                            | Error failure ->
                                fail (problem "clear" failure)
                                return Error ReadCacheFailure.Unavailable
                            | Ok within ->
                                match! transact "clear" (Transaction.readWrite Database [ Op.deleteRange Entries within ]) with
                                | Ok _ -> return Ok()
                                | Error failure -> return Error failure
                        }

                    let store: ReadCacheStore =
                        { Load = load
                          Save = save
                          Remove = remove
                          Partitions = partitions
                          Clear = clear }

                    return
                        Ok
                            { Store = store
                              Keep =
                                fun entry ->
                                    async {
                                        let! _ = save entry
                                        return ()
                                    }
                              SaveMany = saveMany
                              KeepMany =
                                  fun entries ->
                                      async {
                                          let! _ = saveMany entries
                                          return ()
                                      }
                              Show =
                                fun cacheKey ->
                                    async {
                                        match! ReadCache.load store cacheKey with
                                        | Ok(Some cached) ->
                                            // Best-effort: marks the partition used.
                                            let! _ = save (ReadCache.touch (host.Now()) (Cached.value cached))
                                            return Ok(Some cached)
                                        | other -> return other
                                    }
                              Diagnostics = fun () -> diagnostics.Value
                              FreeSpace =
                                fun () ->
                                    async {
                                        match! transact "count" (Transaction.readOnly Database [ Op.count Entries None None ]) with
                                        | Ok [ result ] when Read.count result |> Option.exists (fun n -> n > 0L) ->
                                            let freed = Read.count result |> Option.defaultValue 0L

                                            match! clear CacheScope.Everything with
                                            | Ok() ->
                                                diagnose (fun d -> { d with Evictions = d.Evictions + int freed; Size = Some 0L })
                                                return true
                                            | Error _ -> return false
                                        | _ -> return false
                                    } }
        }

/// "Clear this device" (LCP-070, LCP-086): every Arca database in the
/// application's namespace, the queue and the cache. Typed: Blocked while
/// another tab holds a connection, never a silent partial clear.
[<RequireQualifiedAccess>]
module LimenDevice =

    let clear (host: LimenHost) =
        async {
            let delete database =
                async {
                    match! Store.deleteDatabase host.Store Connection.NotOpened database with
                    | Ok() -> return Ok()
                    | Error DeleteFailure.VersionBlocked -> return Error(database, ReadCacheFailure.Blocked)
                    | Error _ -> return Error(database, ReadCacheFailure.Unavailable)
                }

            match! delete IndexedDbQueue.Database with
            | Error failure -> return Error failure
            | Ok() -> return! delete IndexedDbReadCache.Database
        }
