// Dependency-free test runner: every check prints PASS/FAIL and the
// process exits non-zero when any check failed, so CI cannot read a skipped
// or empty run as success.
open System

let mutable failures = 0

let check name condition =
    if condition then
        Console.WriteLine $"PASS {name}"
    else
        failures <- failures + 1
        Console.Error.WriteLine $"FAIL {name}"

check "scaffold builds and links the library" Arca.Library.scaffoldReady

[<EntryPoint>]
let main _ =
    if failures = 0 then 0
    else
        Console.Error.WriteLine $"{failures} check(s) failed."
        1
