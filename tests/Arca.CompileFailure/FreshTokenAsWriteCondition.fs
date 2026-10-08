/// The control: a token from a provider read conditions a write. This file
/// compiles, so the only error the project reports is the cached one.
module Arca.CompileFailure.FreshTokenAsWriteCondition

open Arca

let condition (fresh: Fresh<StoredObject list>) (operation: Operation) =
    Operation.requireChangeToken (Fresh.token fresh) operation
