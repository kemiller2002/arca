namespace Arca.GitHub

open System
open Arca

/// A request as sent: the request and the credential to send it with. The
/// credential is an `AccessToken`, which never prints or serializes its value.
[<NoComparison>]
type Authorized =
    { Request: HttpRequest
      Credential: AccessToken option }

/// A GitHub conversation described as data (DF-ARCA-2026-0003): what to ask
/// the host to do next, and how to continue with the answer. Nothing here
/// performs the effect. A message-driven host (a Limen engine) matches on the
/// current step, emits the effect, and continues with the result when it
/// arrives; any other host can use `Conversation.run`.
[<NoEquality; NoComparison>]
type Conversation<'a> =
    /// The conversation finished with this value.
    | Done of 'a
    /// Send this request and continue with what the host observed.
    | Send of Authorized * (HttpOutcome -> Conversation<'a>)
    /// Wait this long (a back-off) and continue.
    | Wait of TimeSpan * (unit -> Conversation<'a>)
    /// Ask the token provider for a token and continue with its answer.
    | RequestToken of (Result<AccessToken, TokenUnavailable> -> Conversation<'a>)

/// What a host supplies to run a conversation as an async computation.
[<NoEquality; NoComparison>]
type Host =
    { /// Execute one request. The transport adds `AccessToken.authorization`
      /// when a credential is present.
      Send: Authorized -> Async<HttpOutcome>
      /// Wait for the given time.
      Wait: TimeSpan -> Async<unit>
      Tokens: TokenProvider }

/// Composition and interpretation of conversations.
[<RequireQualifiedAccess>]
module Conversation =

    /// A finished conversation.
    let ret value = Done value

    /// Continues a conversation with a function of its result.
    let rec bind (f: 'a -> Conversation<'b>) (conversation: Conversation<'a>) : Conversation<'b> =
        match conversation with
        | Done value -> f value
        | Send(request, next) -> Send(request, next >> bind f)
        | Wait(delay, next) -> Wait(delay, next >> bind f)
        | RequestToken next -> RequestToken(next >> bind f)

    /// Maps a conversation's result.
    let map f conversation = bind (f >> Done) conversation

    /// Sends one request.
    let send credential request =
        Send({ Request = request; Credential = credential }, Done)

    /// Waits.
    let wait delay = Wait(delay, Done)

    /// Obtains a token.
    let token = RequestToken Done

    /// Runs the conversation against a host, one step at a time.
    let rec run (host: Host) (conversation: Conversation<'a>) : Async<'a> =
        async {
            match conversation with
            | Done value -> return value
            | Send(request, next) ->
                let! outcome = host.Send request
                return! run host (next outcome)
            | Wait(delay, next) ->
                do! host.Wait delay
                return! run host (next ())
            | RequestToken next ->
                let! token = host.Tokens()
                return! run host (next token)
        }

    /// Runs a conversation in which every effect is answered synchronously by
    /// pure functions; for tests and simulations. Waits are recorded, not slept.
    let rec simulate (send: Authorized -> HttpOutcome) (token: unit -> Result<AccessToken, TokenUnavailable>) (waits: TimeSpan list) conversation =
        match conversation with
        | Done value -> value, List.rev waits
        | Send(request, next) -> simulate send token waits (next (send request))
        | Wait(delay, next) -> simulate send token (delay :: waits) (next ())
        | RequestToken next -> simulate send token waits (next (token ()))

/// Computation-expression syntax for conversations.
type ConversationBuilder() =
    member _.Return value = Done value
    member _.ReturnFrom(conversation: Conversation<'a>) = conversation
    member _.Bind(conversation, f) = Conversation.bind f conversation
    member _.Zero() = Done()
    member _.Delay(f: unit -> Conversation<'a>) = f ()

/// The `conversation { }` builder.
[<AutoOpen>]
module ConversationSyntax =
    /// Builds a conversation.
    let conversation = ConversationBuilder()
