---
id: DF-ARCA-2026-0011
title: Namespace-scoped change tokens are a distinct NamespaceToken, the Git tree SHA of the namespace root on GitHub; the read cache revalidates against it by default and the repository-wide token remains only as an explicit fallback
status: accepted
version: 1.0.0
created: 2026-10-09
updated: 2026-10-09
owners:
  - arca
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/ARCA-STORAGE-REQUIREMENTS.md
  - docs/consuming-arca.md
  - research/decisions/DF-ARCA-2026-0002--per-deployment-data-location-and-app-owned-namespaces.md
  - research/decisions/DF-ARCA-2026-0010--limen-bridge-package-indexeddb-queue-layout-and-composer.md
tags: [concurrency, change-token, namespace, read-cache, offline, github]
provenance:
  contributions:
    EXE-20261008T233209539Z-934d158a:
      operations: [created]
      at: 2026-10-09T00:20:02.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Namespace-scoped change tokens (WI-0018, ARCA-CON-005)"
---

# DF-ARCA-2026-0011 — Namespace-scoped change tokens

- **Date:** 2026-10-09
- **Status:** accepted
- **Decision type:** architecture and API (WI-0018, ARCA-CON-005)

## Context

ARCA-LOC-002/003 let several applications keep their namespaces in one
repository. Until 0.3.0 the only whole-state token was the branch head
(`ChangeToken`). In a shared repository, then, every other application's
commit:

- made `ReadCache.revalidate` answer `Refresh` for every cached partition,
  so Chrona re-read data that had not changed (Limen LCP-084);
- made a write held with `Operation.requireChangeToken` fail with
  `StaleChangeToken`. Chrona reloaded and decided again. That was safe, but
  it cost reloads and rate limit.

Chrona asked for a token scoped to its namespace (DF-CHRONA-2026-0005,
coordinator decision 2026-10-08). The token had to keep the commit's
atomicity and OutcomeUnknown semantics. A conformance case had to prove that
a concurrent change inside the namespace is still refused and one outside it
is not.

## Decision

### 1. A distinct type, observed together with the repository token

`NamespaceToken` is a new single-case type, not a `ChangeToken`. Neither can
be passed where the other is meant. `StorageProvider.NamespaceState ns`
returns `NamespaceState { RepositoryToken; NamespaceToken }`, observed at
one state. Providers declare the new `Capability.NamespaceToken`.

Rejected: tagging scope inside `ChangeToken`'s text. A namespace token would
then type-check as a repository condition and always fail, silently.

### 2. GitHub: the Git tree SHA of the namespace root

The adapter lists the parent folder of the namespace root at the head commit
(one contents request) and takes the root's entry SHA. That SHA is the
root's Git tree SHA, and it is content-addressed, so a commit that changes
nothing under the root leaves it unchanged. An absent root is
`git-tree:none`: Git has no empty folders, so absent and empty are the same
state. If the root is missing from a listing that may be partial (at the
listing limit), the adapter fails. It never guesses that the namespace is
empty.

The in-memory provider hashes the sorted paths and revisions under the
root. That is also content-addressed, so both providers agree on what
changes the token.

An application namespace's token covers its `datasets/` folder, which holds
its datasets' namespaces. A dataset commit therefore moves the application's
token. This is conservative: the token can say "changed" when nothing the
application reads changed, but it never says "unchanged" when something did.

### 3. The write condition keeps atomicity and OutcomeUnknown

`Operation.requireNamespaceToken` is checked at the exact head commit the
new commit is built on. The commit is published by the same fast-forward-only
ref update. If the branch moves between the check and the publish, the
update races, and the next attempt checks the namespace again at the new
head. Reconciliation is unchanged, because the idempotency key and candidate
commit identify the operation, not the token. A change inside the namespace
gives `StorageFailure.StaleNamespaceToken(expected, actual)`. The offline
queue maps it to `Conflicted`, as it maps `StaleChangeToken`.

The queue persists the condition as `expectedNamespaceToken`, and only when
it is set. An existing queue's text is therefore byte-identical. An Arca
older than 0.4.0 would read such an entry without its condition, so
`docs/consuming-arca.md` says not to downgrade while one is pending.

### 4. The read cache defaults to the namespace scope; the old behaviour is explicit

Entries carry `TokenScope.Namespace` or `TokenScope.Repository`, persisted
as `tokenScope`. An entry without the field was written by 0.3.x, and it
reads as `Repository`.

- `Fresh.read` now takes a `NamespaceState`, and
  `ProviderObservation.Current` carries one. The 0.3.0 calls stop
  compiling, so no consumer keeps repository-wide revalidation by accident.
- The repository-wide behaviour remains only as an explicit fallback,
  `Fresh.readRepositoryWide` and `ProviderObservation.CurrentRepository`,
  for a provider without the capability.
- `revalidate` compares a namespace-scoped entry with the namespace token,
  and a repository-scoped entry with the repository token. A
  namespace-scoped entry is never confirmed by a repository token alone.
- When the namespace tokens are equal, the namespace's content is identical
  at the current repository token. The confirmed `Fresh` value therefore
  carries the current repository token as well as the namespace token, and
  a write may be conditioned on either.

The read-cache format stays `1`. A 0.3.x reader ignores `tokenScope` and
compares a namespace token with a commit SHA. They never match, so the
worst case is a refresh, never a false confirmation.

## Consequences

- 0.4.0 is a minor release that changes the API: the `Fresh.read` and
  `ProviderObservation.Current` signatures, new record fields and a new
  `StorageFailure` case. The CHANGELOG and the consumer guide list the
  changes.
- A namespace-conditioned GitHub commit costs one extra contents request per
  attempt. `NamespaceState` costs two requests: the ref and the parent
  listing.
- Evidence: storage conformance passes on the in-memory provider and on the
  GitHub adapter over FakeGitHub. Read-cache conformance passes on the
  in-memory cache and the Limen FakeStore. Negative tests show that the new
  cases fail a provider that reports the repository token as the namespace
  token, and one that ignores the namespace condition.
