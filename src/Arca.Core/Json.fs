namespace Arca

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

/// A JSON value with exact numbers (ARCA-REC-001).
///
/// Numbers are decimals, so equal values have one representation and nothing
/// is rounded. An object's members are kept sorted by key and keys are unique,
/// so structurally equal values are equal F# values. Build objects with
/// `Json.object`, which enforces both.
[<RequireQualifiedAccess; CustomEquality; NoComparison>]
type Json =
    | Null
    | Bool of bool
    | Number of decimal
    | String of string
    | Array of Json list
    | Object of (string * Json) list

    override this.Equals(other: obj) =
        match other with
        | :? Json as that ->
            match this, that with
            | Null, Null -> true
            | Bool a, Bool b -> a = b
            | Number a, Number b -> a = b
            | String a, String b -> String.Equals(a, b, StringComparison.Ordinal)
            | Array a, Array b -> a.Length = b.Length && List.forall2 (fun (x: Json) y -> x.Equals y) a b
            | Object a, Object b ->
                a.Length = b.Length
                && List.forall2 (fun (k1: string, v1: Json) (k2, v2) -> String.Equals(k1, k2, StringComparison.Ordinal) && v1.Equals v2) a b
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | Null -> 0
        | Bool value -> hash value
        | Number value -> hash value
        | String value -> StringComparer.Ordinal.GetHashCode value
        | Array items -> items |> List.fold (fun acc item -> HashCode.Combine(acc, item.GetHashCode())) 17
        | Object members ->
            members
            |> List.fold (fun acc (key, value) -> HashCode.Combine(acc, StringComparer.Ordinal.GetHashCode key, value.GetHashCode())) 31

/// Why text is not acceptable Arca JSON.
[<RequireQualifiedAccess>]
type JsonError =
    /// Not well-formed JSON, or trailing content after the value.
    | Malformed of detail: string
    /// An object repeats a key.
    | DuplicateKey of key: string
    /// A number that cannot be held exactly: an exponent form, or more
    /// precision or range than a decimal holds. Arca never rounds.
    | UnsupportedNumber of text: string
    /// A string holding a lone surrogate, which has no UTF-8 encoding.
    | InvalidString
    /// Nesting deeper than the limit.
    | TooDeep of limit: int

/// Canonical JSON: construction, parsing, the canonical encoding and content hashes.
///
/// The canonical form (Arca canonical JSON, format 1):
/// - UTF-8 without a byte-order mark, with no insignificant whitespace;
/// - object members sorted by key in ordinal (UTF-16 code unit) order, keys unique;
/// - numbers in plain decimal notation with no exponent, no leading `+`,
///   no trailing fractional zeros, and `0` for negative zero;
/// - strings escaping only `"`, `\` and control characters (`\b \f \n \r \t`,
///   otherwise `\u00xx` in lower case); every other character is literal.
/// Equal values therefore always produce equal bytes and equal hashes.
[<RequireQualifiedAccess>]
module Json =

    /// The deepest nesting accepted.
    [<Literal>]
    let MaxDepth = 64

    let private ordinal = StringComparer.Ordinal

    /// An object from members, sorted by key. Duplicate keys are refused.
    let object (members: (string * Json) list) =
        let sorted = members |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

        match sorted |> List.pairwise |> List.tryFind (fun ((a, _), (b, _)) -> ordinal.Equals(a, b)) with
        | Some((key, _), _) -> Error(JsonError.DuplicateKey key)
        | None -> Ok(Json.Object sorted)

    /// An object from members whose keys the caller guarantees are distinct
    /// (for example, literal field names). Throws on a duplicate, which is a
    /// programming error, not a data error.
    let objectOf (members: (string * Json) list) =
        match object members with
        | Ok value -> value
        | Error error -> invalidArg (nameof members) $"duplicate key: {error}"

    /// A member's value, when the value is an object that has it.
    let field (key: string) (value: Json) =
        match value with
        | Json.Object members -> members |> List.tryFind (fun (k, _) -> ordinal.Equals(k, key)) |> Option.map snd
        | _ -> None

    /// The canonical text of a decimal: plain notation, no trailing fractional zeros, no negative zero.
    let numberText (value: decimal) =
        if value = 0m then
            "0"
        else
            (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)

    let private hasLoneSurrogate (text: string) =
        let rec scan i =
            if i >= text.Length then false
            elif Char.IsHighSurrogate text[i] then
                if i + 1 < text.Length && Char.IsLowSurrogate text[i + 1] then scan (i + 2) else true
            elif Char.IsLowSurrogate text[i] then true
            else scan (i + 1)

        scan 0

    let private writeString (builder: StringBuilder) (text: string) =
        builder.Append '"' |> ignore

        for c in text do
            match c with
            | '"' -> builder.Append "\\\"" |> ignore
            | '\\' -> builder.Append "\\\\" |> ignore
            | '\b' -> builder.Append "\\b" |> ignore
            | '\f' -> builder.Append "\\f" |> ignore
            | '\n' -> builder.Append "\\n" |> ignore
            | '\r' -> builder.Append "\\r" |> ignore
            | '\t' -> builder.Append "\\t" |> ignore
            | c when c < ' ' -> builder.Append("\\u").Append((int c).ToString("x4", CultureInfo.InvariantCulture)) |> ignore
            | c -> builder.Append c |> ignore

        builder.Append '"' |> ignore

    let rec private write (builder: StringBuilder) (value: Json) =
        match value with
        | Json.Null -> builder.Append "null" |> ignore
        | Json.Bool true -> builder.Append "true" |> ignore
        | Json.Bool false -> builder.Append "false" |> ignore
        | Json.Number number -> builder.Append(numberText number) |> ignore
        | Json.String text -> writeString builder text
        | Json.Array items ->
            builder.Append '[' |> ignore

            items
            |> List.iteri (fun index item ->
                if index > 0 then builder.Append ',' |> ignore
                write builder item)

            builder.Append ']' |> ignore
        | Json.Object members ->
            builder.Append '{' |> ignore

            members
            |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
            |> List.iteri (fun index (key, item) ->
                if index > 0 then builder.Append ',' |> ignore
                writeString builder key
                builder.Append ':' |> ignore
                write builder item)

            builder.Append '}' |> ignore

    /// The canonical text of a value.
    let canonicalText (value: Json) =
        let builder = StringBuilder()
        write builder value
        builder.ToString()

    /// The canonical UTF-8 bytes of a value.
    let canonicalBytes (value: Json) =
        UTF8Encoding(false, true).GetBytes(canonicalText value)

    /// `sha256:<lower-case hex>` of the canonical bytes (ARCA-REC-001).
    let contentHash (value: Json) =
        let digest = SHA256.HashData(canonicalBytes value)
        "sha256:" + Convert.ToHexStringLower digest

    let private plainNumber (text: string) =
        let digits (s: string) = s.Length > 0 && s |> Seq.forall Char.IsAsciiDigit
        let unsigned = if text.StartsWith '-' then text.Substring 1 else text

        let integer, fraction =
            match unsigned.IndexOf '.' with
            | -1 -> unsigned, None
            | dot -> unsigned.Substring(0, dot), Some(unsigned.Substring(dot + 1))

        digits integer
        && (integer = "0" || integer[0] <> '0')
        && (fraction |> Option.forall digits)

    let private exactNumber (text: string) (value: decimal) =
        let expected =
            let unsigned = if text.StartsWith '-' then text.Substring 1 else text

            let trimmed =
                if unsigned.Contains '.' then unsigned.TrimEnd('0').TrimEnd('.') else unsigned

            if trimmed = "0" then "0"
            elif text.StartsWith '-' then "-" + trimmed
            else trimmed

        numberText value = expected

    let rec private convert depth (element: JsonElement) : Result<Json, JsonError> =
        if depth > MaxDepth then
            Error(JsonError.TooDeep MaxDepth)
        else
            match element.ValueKind with
            | JsonValueKind.Null -> Ok Json.Null
            | JsonValueKind.True -> Ok(Json.Bool true)
            | JsonValueKind.False -> Ok(Json.Bool false)
            | JsonValueKind.Number ->
                let text = element.GetRawText()

                match plainNumber text, element.TryGetDecimal() with
                | true, (true, value) when exactNumber text value -> Ok(Json.Number value)
                | _ -> Error(JsonError.UnsupportedNumber text)
            | JsonValueKind.String ->
                match element.GetString() with
                | null -> Error JsonError.InvalidString
                | text when hasLoneSurrogate text -> Error JsonError.InvalidString
                | text -> Ok(Json.String text)
            | JsonValueKind.Array ->
                element.EnumerateArray()
                |> Seq.fold
                    (fun state item ->
                        state |> Result.bind (fun items -> convert (depth + 1) item |> Result.map (fun value -> value :: items)))
                    (Ok [])
                |> Result.map (List.rev >> Json.Array)
            | JsonValueKind.Object ->
                element.EnumerateObject()
                |> Seq.fold
                    (fun state property ->
                        state
                        |> Result.bind (fun members ->
                            if hasLoneSurrogate property.Name then
                                Error JsonError.InvalidString
                            else
                                convert (depth + 1) property.Value |> Result.map (fun value -> (property.Name, value) :: members)))
                    (Ok [])
                |> Result.bind (List.rev >> object)
            | other -> Error(JsonError.Malformed $"unexpected {other}")

    /// Parses JSON text, refusing duplicate keys, inexact numbers, lone
    /// surrogates and excessive depth. The input need not be canonical.
    let parse (text: string) =
        try
            let options = JsonDocumentOptions(MaxDepth = MaxDepth + 1, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow)
            use document = JsonDocument.Parse(text, options)
            convert 0 document.RootElement
        with
        | :? JsonException as error -> Error(JsonError.Malformed error.Message)
        // An escaped lone surrogate (`"\ud800"`) fails when the string is read.
        | :? InvalidOperationException -> Error JsonError.InvalidString

    /// True when `text` is exactly the canonical encoding of the value it holds.
    let isCanonical (text: string) =
        match parse text with
        | Ok value -> String.Equals(canonicalText value, text, StringComparison.Ordinal)
        | Error _ -> false
