namespace Arca

open System.Text.RegularExpressions

/// A last line of defence against credentials leaking into stored data,
/// commits or the offline queue (ARCA-AUTH-002, ARCA-COMMIT-004).
///
/// Arca never handles tokens in its data paths at all (the token provider's
/// value is never part of a record, an operation or a commit). This check
/// additionally refuses any text that looks like a credential, so a caller's
/// mistake, such as putting a token into a commit summary, fails loudly
/// instead of being published.
[<RequireQualifiedAccess>]
module Secrets =

    let private patterns =
        [ // GitHub personal, OAuth, user-to-server, server-to-server and refresh tokens.
          @"\bgh[pousr]_[A-Za-z0-9]{20,}"
          // GitHub fine-grained personal access tokens.
          @"\bgithub_pat_[A-Za-z0-9_]{20,}"
          // An HTTP authorization value.
          @"(?i)\b(bearer|token|basic)\s+[A-Za-z0-9._~+/=-]{16,}"
          // A JSON Web Token (three base64url parts, the first a JSON header).
          @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"
          // A private key block.
          @"-----BEGIN [A-Z ]*PRIVATE KEY-----" ]
        |> List.map (fun pattern -> Regex(pattern, RegexOptions.CultureInvariant))

    /// True when the text contains something that looks like a credential.
    let looksLikeCredential (text: string) =
        patterns |> List.exists (fun pattern -> pattern.IsMatch text)
