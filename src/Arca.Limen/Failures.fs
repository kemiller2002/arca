namespace Arca.Limen

open Limen.Store

/// The class of an adapter failure (Limen LCP-072).
[<RequireQualifiedAccess>]
type FailureClass =
    /// No usable storage, or it stopped answering.
    | Unavailable
    /// The browser's quota, a Limen size limit or the adapter's budget.
    | Quota
    /// A compare-and-put found other content than expected.
    | Conflict
    /// The stored database is newer than this code, or its schema differs.
    | Version
    /// A request the pack refused as malformed.
    | Invalid
    /// A stored value that does not decode.
    | Undecodable
    /// The browser closed the connection under the page (storage cleared or
    /// evicted), or another page upgraded or deleted the database.
    | ConnectionLost
    /// Another tab took the queue over; this tab's saves are fenced.
    | OwnedElsewhere

/// A failure the adapter reports in its diagnostics: what was attempted,
/// where, its class and a stable code. It never carries a stored value or a
/// credential; the detail holds names, counts and sizes only (LCP-068,
/// LCP-072).
type AdapterFailure =
    { Operation: string
      Database: string
      Store: string option
      Class: FailureClass
      /// Stable: `arca.limen.<class>.<specific>`.
      Code: string
      Detail: string }

[<RequireQualifiedAccess>]
module AdapterFailure =

    let private family =
        function
        | FailureClass.Unavailable -> "unavailable"
        | FailureClass.Quota -> "quota"
        | FailureClass.Conflict -> "conflict"
        | FailureClass.Version -> "version"
        | FailureClass.Invalid -> "invalid"
        | FailureClass.Undecodable -> "undecodable"
        | FailureClass.ConnectionLost -> "connection-lost"
        | FailureClass.OwnedElsewhere -> "owned-elsewhere"

    /// The stable code of a class and a specific reason.
    let code (failureClass: FailureClass) (specific: string) = $"arca.limen.{family failureClass}.{specific}"

    let create operation database store failureClass specific detail =
        { Operation = operation
          Database = database
          Store = store
          Class = failureClass
          Code = code failureClass specific
          Detail = detail }

    /// An open that did not open.
    let ofOpen (database: string) (failure: OpenFailure) =
        let make = create "open" database None

        match failure with
        | OpenFailure.VersionBlocked -> make FailureClass.Version "blocked" "another page holds an older version open"
        | OpenFailure.Outdated stored -> make FailureClass.Version "outdated" $"the stored version {stored} is newer than this code's"
        | OpenFailure.SchemaMismatch problems -> make FailureClass.Version "schema-mismatch" $"{problems.Length} schema differences"
        | OpenFailure.Unavailable reason -> make FailureClass.Unavailable "open" reason
        | OpenFailure.Invalid _ -> make FailureClass.Invalid "open" "the pack refused the open request"
        | OpenFailure.Cancelled -> make FailureClass.Unavailable "cancelled" "cancelled"
        | OpenFailure.Unexpected kind -> make FailureClass.Unavailable "unexpected" kind

    /// A transaction that applied nothing. A conflict's current value is
    /// dropped: it is a stored value.
    let ofTransact (operation: string) (database: string) (store: string) (failure: TransactFailure) =
        let make = create operation database (Some store)

        match failure with
        | TransactFailure.Aborted(AbortCause.Conflict, _, _) -> make FailureClass.Conflict "conflict" "the stored value changed"
        | TransactFailure.Aborted(AbortCause.Constraint, _, _) -> make FailureClass.Conflict "constraint" "a unique index refused the write"
        | TransactFailure.Aborted(AbortCause.InvalidKey, _, _) -> make FailureClass.Invalid "invalid-key" "a record has no valid key"
        | TransactFailure.Aborted(AbortCause.UnknownStore, _, _) -> make FailureClass.Version "unknown-store" "the store is not in the database"
        | TransactFailure.Aborted(AbortCause.Other, _, _) -> make FailureClass.Unavailable "aborted" "the browser aborted the transaction"
        | TransactFailure.QuotaExceeded -> make FailureClass.Quota "exceeded" "the browser's storage quota"
        | TransactFailure.NotOpen -> make FailureClass.ConnectionLost "not-open" "the database is not open on this page"
        | TransactFailure.Cancelled -> make FailureClass.Unavailable "cancelled" "cancelled"
        | TransactFailure.Invalid _ -> make FailureClass.Invalid "request" "the pack refused the request"
        | TransactFailure.Unavailable reason -> make FailureClass.Unavailable "transact" reason
        | TransactFailure.Unexpected kind -> make FailureClass.Unavailable "unexpected" kind

    /// The Aegis classification of an adapter failure (ARCA-ARCH-007): an
    /// application classifies the adapter's unexpected failures as it does
    /// Arca's GitHub failures, without parsing text.
    let mapping: Aegis.Translation.Mapping<AdapterFailure> =
        let category failure =
            match failure.Class with
            | FailureClass.Undecodable -> Aegis.DataFailure
            | FailureClass.Version -> Aegis.ConfigurationFailure
            | FailureClass.Conflict
            | FailureClass.OwnedElsewhere -> Aegis.DomainFailure
            | FailureClass.Unavailable
            | FailureClass.Quota
            | FailureClass.ConnectionLost
            | FailureClass.Invalid -> Aegis.InfrastructureFailure

        let recovery failure =
            match failure.Class with
            | FailureClass.Unavailable -> Aegis.Retry(3, Aegis.Exponential(System.TimeSpan.FromSeconds 1.0))
            | FailureClass.ConnectionLost -> Aegis.Reload
            | FailureClass.Version -> Aegis.Reload
            | FailureClass.OwnedElsewhere -> Aegis.Continue
            | FailureClass.Conflict -> Aegis.Refresh
            | FailureClass.Quota -> Aegis.ManualIntervention
            | FailureClass.Undecodable -> Aegis.ReadOnlyRecovery
            | FailureClass.Invalid -> Aegis.AbortOperation

        { Code = fun failure -> Aegis.FaultCode failure.Code
          Category = category
          Severity =
            fun failure ->
                match failure.Class with
                | FailureClass.OwnedElsewhere -> Aegis.FaultSeverity.Warning
                | FailureClass.Undecodable
                | FailureClass.Quota -> Aegis.FaultSeverity.Critical
                | _ -> Aegis.FaultSeverity.Error
          Impact =
            fun failure ->
                match failure.Class with
                | FailureClass.Quota
                | FailureClass.Undecodable
                | FailureClass.ConnectionLost -> Aegis.DegradedApplication
                | _ -> Aegis.FeatureUnavailable
          Persistence =
            fun failure ->
                match failure.Class with
                | FailureClass.Unavailable
                | FailureClass.Conflict -> Aegis.Transient
                | FailureClass.OwnedElsewhere
                | FailureClass.ConnectionLost -> Aegis.Persistent
                | _ -> Aegis.RequiresIntervention
          Recovery = recovery
          UserMessage =
            fun failure ->
                match failure.Class with
                | FailureClass.OwnedElsewhere -> "Another tab holds this device's unsent changes."
                | FailureClass.Quota -> "This device is out of space for unsent changes."
                | FailureClass.ConnectionLost -> "The browser closed this device's storage; reload to continue."
                | _ -> "Changes could not be kept on this device."
          Owner = "Arca"
          Dependencies = [ "Arca.Limen"; "Limen.Store"; "IndexedDB" ]
          Domain = fun _ -> Aegis.EnvironmentDomain
          Radius = fun _ -> Aegis.OneFeature
          Retention = fun _ -> Aegis.RetainDays 30 }
