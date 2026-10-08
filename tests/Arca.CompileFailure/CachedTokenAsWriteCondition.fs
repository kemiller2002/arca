/// LCP-085: a cached value's token is not a ChangeToken, so it cannot
/// condition a write. This must fail with FS0001.
module Arca.CompileFailure.CachedTokenAsWriteCondition

open Arca

let condition (cached: Cached<CacheEntry>) (operation: Operation) =
    Operation.requireChangeToken (Cached.asOf cached) operation
