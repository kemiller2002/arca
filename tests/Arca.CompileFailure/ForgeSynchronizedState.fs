/// ARCA-OFF-009: application code cannot construct a Synchronized entry
/// state. `EntryState.Synchronized` is a read-only pattern, not a
/// constructor. This must fail with FS0039.
module Arca.CompileFailure.ForgeSynchronizedState

open Arca

let forged: EntryState = EntryState.Synchronized "a-change-token-the-provider-never-issued"

/// Nor through the internal constructor the compiler's suggestion names
/// (FS1094: not accessible).
let forgedInternally: EntryState = EntryState.synchronized "a-change-token-the-provider-never-issued"
