---
id: ARCA-REQ-STORAGE
title: Arca shared storage requirements
status: draft
version: 0.4.0
created: 2026-10-08
updated: 2026-10-09
owners:
  - arca
related_documents:
  - docs/requirements/README.md
  - research/decisions/DF-ARCA-2026-0001--github-only-behind-a-storage-interface.md
  - research/decisions/DF-ARCA-2026-0002--per-deployment-data-location-and-app-owned-namespaces.md
tags: [requirements, storage, github, wasm, nuget]
provenance:
  contributions:
    EXE-20261008T080342508Z-ce85c57b:
      operations: [created]
      at: 2026-10-08T08:04:42.330Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Derived from Signal, Chrona and Summa requirements and user decisions of 2026-10-08"
    EXE-20261008T084806354Z-6573ebbf:
      operations: [modified]
      at: 2026-10-08T08:54:37.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record decisions D-008..D-011 and resolve OQ-ARCA-002..004"
    EXE-20261008T151448783Z-b01c05a2:
      operations: [modified]
      at: 2026-10-08T15:15:06.859Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-reference Limen LCP-043..087 and DF-LIMEN-2026-0005 from ARCA-OFF-002 and OQ-ARCA-003; record the two-tab snapshot hazard"
    EXE-20261008T233209539Z-934d158a:
      operations: [modified]
      at: 2026-10-09T00:20:03.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Add ARCA-CON-005, namespace-scoped change tokens (WI-0018)"
---

# Arca shared storage requirements

Arca is the shared data layer for Echelon applications. It is published as a
NuGet package (`EchelonFoundry.Arca`). It is the "GitHub storage" that Signal,
Chrona and Summa each list as missing in their
`docs/requirements/implementation-gap-analysis.md`.

This document derives Arca's requirements from those three applications'
requirement corpora. Every requirement cites the consumer requirement IDs it
comes from:

| Prefix | Source |
|---|---|
| `SIG ADM-NNN` | Signal `input-documents/survey-engine-administrator-console-storage-analytics-visualization-requirements.txt` (gap-analysis rows ADM-001..077, ledger items WI-0012/0013/0017/0018) |
| `SIG AER-NNN` | Signal `input-documents/signal-aegis-installation-and-usage-requirements.txt` |
| `CHX-NNM` | Chrona `docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt` section `NN.M` (identifiers from Chrona's gap analysis) |
| `SUM0-NNN`, `SUM3-NNN` | Summa `input-documents/summa-requirement-set-0.txt` §0.NNN and `summa-v0.3-requirements.txt` §NNN (identifiers from Summa's gap analysis) |

Keywords follow RFC 2119. **Arca owns storage mechanics; each application
owns its domain meaning.** Arca never decides what a record means, whether a
domain transition is legal, or how to resolve a semantic conflict. It reports
facts (what changed, what conflicted, what outcome is unknown) and the
application decides.

## 1. Decisions and assumptions

| ID | Statement | Status |
|---|---|---|
| ARCA-D-001 | Arca supports one provider, GitHub, behind a clean, provider-neutral storage interface. Other providers (an installable service, SIG ADM-006) can be added later without changing consumer code. | **Decision**, confirmed by the user 2026-10-08 ([DF-ARCA-2026-0001](../../research/decisions/DF-ARCA-2026-0001--github-only-behind-a-storage-interface.md)) |
| ARCA-D-002 | Arca does not depend on Fides. It takes a token-provider abstraction; Fides (or anything else) supplies the token. | Decision (user direction) |
| ARCA-D-003 | Arca has a pure F# core (record formats, versioning, conflict detection and merge, commit/audit format, the offline change queue as data) and a thin GitHub adapter (API calls, retries, rate limits, browser persistence through Limen interop). | Decision (user direction) |
| ARCA-D-004 | Arca runs in browser WebAssembly, because the consuming applications are Limen/Forma F# WASM UIs. | Decision (user direction) |
| ARCA-D-005 | The data repository is configurable per deployment. Each application owns its own namespace inside it, and an application's data must be separable under different permissions. | **Decision**, confirmed by the user 2026-10-08 ([DF-ARCA-2026-0002](../../research/decisions/DF-ARCA-2026-0002--per-deployment-data-location-and-app-owned-namespaces.md)) |
| ARCA-D-007 | **No at-rest encryption for now.** Permission separation between applications is achieved with separate repositories per permission boundary, using the configurable per-application data location (ARCA-LOC-004). Per-application encryption is deferred: it is a possible future work item, not a requirement. | **Decision**, confirmed by the user 2026-10-08 (DF-ARCA-2026-0002) |
| ARCA-D-006 | Arca is written in a functional style: immutable data, total functions returning results, effects as data interpreted at the adapter edge. | Decision (user preference) |
| ARCA-D-008 | Two packages: `EchelonFoundry.Arca.Core` (pure, no I/O) and `EchelonFoundry.Arca.GitHub`. The adapter describes GitHub conversations as data; the host (a Limen kernel's Http effect in the browser) executes them. | **Decision**, user 2026-10-08 ([DF-ARCA-2026-0003](../../research/decisions/DF-ARCA-2026-0003--two-packages-core-and-github-adapter-with-effects-as-data.md)) |
| ARCA-D-009 | Arca stays separate from Aegis's GitHub event store (different concerns); it reuses only Aegis's GitHub failure model. | **Decision**, user 2026-10-08 ([DF-ARCA-2026-0004](../../research/decisions/DF-ARCA-2026-0004--arca-stays-separate-from-aegis-github-event-store.md)) |
| ARCA-D-010 | The offline queue persists to localStorage through Limen's Storage effect, behind a queue-store port, until a Limen IndexedDB adapter replaces it. | **Decision**, user-approved 2026-10-08 ([DF-ARCA-2026-0005](../../research/decisions/DF-ARCA-2026-0005--offline-queue-persists-to-localstorage-behind-a-port.md)) |
| ARCA-D-011 | Until nuget.org Trusted Publishing exists, releases ship as Sigstore-attested GitHub release assets, registered in echelon-registry and installed by Conditor through a local feed. | **Decision**, user 2026-10-08 ([DF-ARCA-2026-0006](../../research/decisions/DF-ARCA-2026-0006--interim-distribution-through-attested-github-release-assets.md)) |
| ARCA-D-012 | Migrations always copy to a different location (repository, branch or base path), record their progress in the target manifest, and retire the source only by an explicit step conditioned on the source being unchanged. Derived data is rebuilt at the target, not copied. | **Design**, slice 10 ([DF-ARCA-2026-0008](../../research/decisions/DF-ARCA-2026-0008--migrations-copy-to-a-new-location-and-retire-the-source-explicitly.md)) |

Open design questions that need the user are in [section 14](#14-open-questions-for-the-user).

## 2. Architecture and runtime (ARCA-ARCH)

**ARCA-ARCH-001** Arca MUST be split into a pure core and an adapter. The
core MUST perform no I/O. It MUST NOT read the clock or draw randomness, and
it MUST NOT touch the browser or the network. Time, identifiers and entropy
are inputs. The adapter interprets effect requests that the core describes as
data. *Sources: CHX-002, CHX-004, SUM0-001 (0.1.3 "external effects should be
explicit"), SIG ADM-003.*

**ARCA-ARCH-002** Arca MUST run inside the .NET browser-WASM runtime that
Limen hosts. It MUST NOT need threads, sockets, a file system or any API that
is unavailable there. Network access MUST go through an HTTP abstraction that
the browser host can satisfy (Limen `Http`). *Sources: CHX-003, SIG ARX-003,
SUM0-001 (0.1.3 WASM kernel).*

**ARCA-ARCH-003** Arca MUST expose a provider-neutral storage interface with
explicit, versioned capabilities, for example ReadObject, ConditionalWrite,
ListPrefix, BatchWrite, ChangeToken and MaxObjectSize. A capability the
provider does not have MUST surface as explicitly unavailable, never as a
hidden fallback. *Sources: SIG ADM-003, SIG ADM-006.*

**ARCA-ARCH-004** Domain types that consumers persist MUST NOT need GitHub
concepts such as commits, branches or file paths. Those stay inside the
locator and the adapter. *Sources: SIG ADM-006, SUM0-046.*

**ARCA-ARCH-005** Arca MUST NOT depend on Fides or any other identity
package. Credentials arrive through ARCA-AUTH-001. *Source: ARCA-D-002.*

**ARCA-ARCH-006** Arca MUST be published as versioned NuGet packages
(`EchelonFoundry.Arca.Core`, `EchelonFoundry.Arca.GitHub`) through the Echelon
`nuget-library` release contract. A consumer MUST be
able to pin an exact version. *Sources: echelon-registry REG-REL-010/012;
shared-foundations dependency pinning.*

**ARCA-ARCH-007** Unexpected operational failures at Arca's boundaries
(GitHub calls, browser persistence, deserialization) MUST be classified
through Aegis. The GitHub failure model SHOULD reuse
`EchelonFoundry.Aegis.Integration.GitHub` instead of redefining it.
*Sources: SIG AER-002, SIG AER-003, SIG AER-032, SIG ADM-077, CHX-005,
SUM0-001.*

## 3. Data location, application namespaces and permission separation (ARCA-LOC)

This section records the user's decision ARCA-D-005. Every consuming
application carries a matching requirement in its own backlog.

**ARCA-LOC-001** The data repository MUST be configurable per deployment:
owner, repository, branch and base path. Arca and its consumers MUST NOT
hard-code a repository, an owner, a branch or a root path. *Sources: CHX-024,
SUM0-005, SUM0-006, SUM0-041, SIG ADM-004.*

**ARCA-LOC-002** Each application MUST create and own its own folder
structure (its namespace) beneath the configured base path, and MUST keep all
of its reads and writes inside that namespace. *Sources: CHX-024, SUM0-005,
SUM0-006, SUM0-043, SIG ADM-004 ("optional storage namespace").*

**ARCA-LOC-003** An application MUST NOT assume it is the only application
using a repository, or that it owns the repository root. Arca MUST refuse a
write whose resolved path escapes the application's namespace, including path
traversal and root writes. *Sources: CHX-024, SUM0-005, SUM0-034, SUM0-043.*

**ARCA-LOC-004** Each application's data MUST be separable under different
permissions. Summa and Chrona, for example, need different permissions. The
configuration MUST let each application point at its **own repository** or at
a **shared** one. Because GitHub grants permissions per repository, an
application that needs a permission boundary MUST be able to use a different
repository from the others. This is option (a), which the user chose
(ARCA-D-007). It is supported from the first release. *Sources: user decision 2026-10-08;
CHX-025 ("different organizations to use different repositories"), SUM0-009,
SIG ADM-057.*

> **Technical caveat (recorded honestly).** GitHub permissions apply per
> repository, not per folder. Folders inside one repository cannot by
> themselves enforce different permissions. Any token that can write to the
> repository can write to every application's namespace in it. ARCA-LOC-002
> and ARCA-LOC-003 therefore keep well-behaved applications apart, but they
> are not a security boundary between applications that share a repository.
> The user considered three options: (a) separate repositories per
> permission boundary, (b) per-application encryption with
> application-specific keys (possibly through Aegis), and (c) both. They
> chose **(a)** and deferred encryption (ARCA-D-007). Applications that need
> different permissions, such as Summa and Chrona, are therefore deployed
> against different repositories. Applications that share a repository share
> its permissions.

**ARCA-LOC-010** Arca MUST NOT encrypt records at rest in the first release.
The record format (ARCA-REC-001) and the provider capability model
(ARCA-ARCH-003) MUST leave room for a later, explicit encryption capability.
That capability is captured as the future work item "Per-application
at-rest encryption (deferred)", not as a requirement. *Source: user decision
2026-10-08 (ARCA-D-007); SIG ADM-025 (encryption "MAY be introduced later").*

**ARCA-LOC-005** Within an application namespace, Arca MUST support
per-organization (or per-dataset) sub-namespaces with an immutable identifier
that is independent of display names and slugs. An organization MUST be able
to live in a different repository from other organizations. *Sources:
CHX-025, CHX-026, SUM0-008, SUM0-009, SUM0-010, SIG ADM-057.*

**ARCA-LOC-006** An application MUST locate its namespace through explicit
configuration and manifests (ARCA-REC-004), never by scanning arbitrary
repository content. Discovery MUST be deterministic. *Sources: SUM0-007,
SUM0-033, CHX-022.*

**ARCA-LOC-007** Separate environments (local, test, staging, production)
MUST use separate locations and credentials. Arca MUST expose the configured
environment identity so applications can display it. *Sources: SUM0-038,
SUM0-039, SUM0-040, SUM3-040.*

**ARCA-LOC-008** Arca MUST detect repository visibility and expose it. A
consumer MUST be able to refuse to initialize production data in a public
repository unless the user explicitly overrides. *Sources: CHX-027,
SUM0-043.*

**ARCA-LOC-009** Changing an existing dataset's location MUST go through the
migration workflow (ARCA-MIG-002). It MUST NOT be done by editing the
configured path. *Source: SIG ADM-057.*

## 4. Record format and versioning (ARCA-REC)

**ARCA-REC-001** Authoritative records MUST be stored as canonical JSON:
deterministic key order, number format and encoding. Equal content MUST
produce equal bytes and equal content hashes. Markdown MUST NOT be an
authoritative format. *Sources: SUM0-013, SIG ADM-004, SIG ADM-025,
CHX-390.*

**ARCA-REC-002** The storage model MUST favour many small records, one per
authoritative object or event, over monolithic files. Paths MUST be
deterministic and support partitioning, for example by organization, actor
and year/month. *Sources: SUM0-012, SUM0-044, CHX-220, SIG ADM-004.*

**ARCA-REC-003** Each record MUST carry a stable internal identifier. The
identifier MUST NOT be derived from its path alone. Relocating a record
through a migration MUST NOT change its identity. *Sources: SUM0-045,
CHX-220.*

**ARCA-REC-004** Arca MUST define an application manifest and a
dataset/organization manifest. Each records the namespace, storage schema
version, record schema versions, provider contract version, creation
provenance and any active migration state. A manifest MUST NOT contain
secrets or PII. *Sources: SUM0-007, SUM0-011, CHX-026, SIG ADM-005.*

**ARCA-REC-005** Every record and manifest MUST declare its schema version.
Before reading or writing, Arca MUST determine whether the running software
can safely read and write that version. An unsupported future version MUST
fail explicitly. Arca MUST NOT silently rewrite older data. *Sources:
SUM0-031, SIG ADM-005, SIG ADM-034, CHX-390.*

**ARCA-REC-006** Arca MUST distinguish authoritative records from derived
data (indexes, projections, caches), both in the type system and in path
conventions. *Sources: SUM0-014, SUM0-015, CHX-400, SIG ADM-027.*

**ARCA-REC-007** Records MAY reference external artifacts by URL or another
stable reference. Arca MUST NOT store large binary artifacts as authoritative
records. *Sources: SUM0-035, SUM0-036, SUM0-037, CHX-240.*

## 5. Optimistic concurrency, conflict detection and merge (ARCA-CON)

**ARCA-CON-001** Every write MUST be conditioned on expected repository
state: an expected commit SHA, blob SHA or change token. Arca MUST NEVER
overwrite newer state blindly. *Sources: CHX-210, SUM0-020, SIG ADM-009.*

**ARCA-CON-002** When the expected state is stale, Arca MUST return a typed
conflict that names the records that changed and their new revisions. It
MUST NOT auto-resolve the conflict. The application reloads, reruns its domain
validation and decides. A clean Git merge MUST NOT be reported as domain
correctness. *Sources: CHX-210, SUM0-021, SIG ADM-009.*

**ARCA-CON-003** The core MUST provide a pure three-way merge over record
sets that keeps independent changes. Concurrent additions of distinct
immutable records MUST commute. Concurrent edits to the same mutable record
MUST be reported as a semantic conflict, never merged silently. *Sources:
CHX-210, CHX-120 (same-record divergence is a RevisionConflict), SIG ADM-009,
SUM0-021.*

**ARCA-CON-004** Conflict detection and merge MUST be property-tested for
determinism, commutativity of independent additions, and the absence of lost
updates. *Sources: SIG ADM-037, SUM3-042, CHX-430 (scenarios 10, 32, 34).*

**ARCA-CON-005** Where several applications share one repository
(ARCA-LOC-002/003), Arca MUST offer a change token scoped to one namespace.
A commit outside the namespace MUST leave it unchanged, and any change inside
it MUST change it. A write conditioned on it MUST keep the atomicity and
OutcomeUnknown semantics of ARCA-COMMIT-001 and ARCA-OUT-001, and MUST be
refused with a typed stale-token failure when the namespace changed. The
read cache MUST revalidate against it by default, so another application's
commit does not mark a partition stale (Limen LCP-084). The repository-wide
token MUST remain available only as an explicit fallback. *Sources: Chrona
DF-CHRONA-2026-0005 (coordinator decision 2026-10-08), LCP-084,
CHX-210.*

## 6. Commit and audit format (ARCA-COMMIT)

**ARCA-COMMIT-001** One logical application operation MUST map to one
atomic Git commit that may touch several records, for example an invoice, its
journal entry and its audit event. A partially prepared operation MUST NOT
become visible as complete. *Sources: SUM0-016, SUM0-017, SIG ADM-009.*

**ARCA-COMMIT-002** Arca MUST define a deterministic commit format: a
message prefixed by the application namespace (for example
`summa: issue invoice INV-2026-0042`) and structured trailers. The trailers
carry actor kind (human/agent/service/integration), actor id, provider
identity, execution id, correlation id and idempotency key. *Sources: SUM0-016,
SUM0-023, CHX-030, CHX-250, SIG ADM-030.*

**ARCA-COMMIT-003** An agent's operation MUST NEVER be recorded as a human's.
The commit and audit format MUST keep the actor kind the application
supplies. *Sources: SUM0-023, CHX-030, SUM3-014.*

**ARCA-COMMIT-004** Commit metadata and audit records MUST NOT contain
tokens, PII the application did not explicitly supply, or business content
beyond identifiers. *Sources: CHX-023, CHX-420, SUM0-004, SIG ADM-030.*

**ARCA-COMMIT-005** Git history is supporting evidence. Arca MUST NOT ask
consumers to derive domain state from commit history. *Sources: SUM0-046,
CHX-400, SIG ADM-004.*

**ARCA-COMMIT-006** Normal operations write directly to the configured data
branch. When the branch does not allow direct writes (protected, archived,
read-only), Arca MUST detect it and fail explicitly. Pull-request-mode
storage is out of scope for the first release. *Sources: SUM0-018,
SUM0-019, SIG ADM-073.*

## 7. Unknown outcomes and idempotency (ARCA-OUT)

**ARCA-OUT-001** A write whose outcome cannot be determined (timeout,
dropped connection after send) MUST be reported as `OutcomeUnknown`, distinct
from `Failed`, and MUST create a reconciliation obligation. *Sources: SIG
ADM-009, CHX-230, CHX-430 (scenario 31), SUM3-002.*

**ARCA-OUT-002** Every write MUST carry an idempotency key. Before retrying
an unknown outcome, Arca MUST inspect provider state to decide whether the
intended commit already landed. Blind retry that could duplicate or overwrite
is forbidden. *Sources: SIG ADM-009, CHX-230, SUM1-019, SUM3-033.*

## 8. Offline change queue (ARCA-OFF)

**ARCA-OFF-001** The core MUST model a pending-change queue as plain data:
ordered entries with idempotency keys, expected base revisions and states
(pending, in-flight, synchronized, conflicted, outcome-unknown).
Applications MUST be able to inspect it. *Sources: CHX-230, CHX-105,
SIG ADM-070.*

**ARCA-OFF-002** The adapter MUST persist the queue durably in the browser
through Limen interop, behind a queue-store port, so pending work survives
refresh and restart. The first adapter uses localStorage through Limen's
Storage effect; a Limen IndexedDB adapter replaces it later without touching
the core (ARCA-D-010). *Sources: CHX-101, CHX-102,
CHX-230.*

> **IndexedDB follow-up (2026-10-08).** The Limen side is specified in
> kemiller2002/limen `docs/requirements/LIMEN-INDEXEDDB-REQUIREMENTS.md`
> (LCP-043..087), and the adapter's placement in DF-LIMEN-2026-0005. The
> adapter is a new package, `EchelonFoundry.Arca.Limen`, and the
> `QueueStore` port does not change. The work is captured as follows:
>
> - WI-0016: the adapter (LCP-046, 059, 060, 062, 065);
> - WI-0019: queue-store conformance;
> - WI-0020: the one-time localStorage migration (LCP-066, 067);
> - WI-0021 and WI-0022: the offline-start read cache Chrona asked for
>   (LCP-082..087).
>
> **Known hazard of the interim adapter.** The port saves the whole queue as
> one snapshot. Two tabs of one application each save their own snapshot, and
> the last save wins, so an entry held only by the overwritten tab can be lost
> from storage. The IndexedDB adapter removes this with one fenced queue
> owner per namespace (LCP-059).
>
> **Fixed in the interim (0.2.1, WI-0024, DF-ARCA-2026-0009).**
> `LocalStorageQueue.own` makes one tab the owner of a namespace's queue,
> through a Web Lock, with LCP-059's semantics. Other tabs get
> `OwnedElsewhere`. Every save, with or without the lock, is fenced on the
> stored text, so a stale snapshot never overwrites an entry another tab
> saved. The layout is unchanged, so no migration is needed.

**ARCA-OFF-003** Unsynchronized data MUST NEVER be presented as globally
synchronized. Sync state MUST be observable. *Sources: CHX-230, SIG ADM-070.*

**ARCA-OFF-004** Reconnection MUST reconcile safely. Retries are idempotent,
conflicts become visible reconciliation obligations, and neither side is
silently discarded. *Sources: CHX-230, CHX-200, CHX-430 (scenarios 33, 34).*

**ARCA-OFF-005** Offline writes MUST be opt-in per application. An
application that does not opt in, such as Signal's administrator (SIG ADM-070
"mutating capabilities must become unavailable"), MUST get a read-only
degraded mode instead. *Sources: SIG ADM-070, CHX-230.*

**ARCA-OFF-006** Local browser storage MUST NOT silently become a competing
shared authority. GitHub stays authoritative. *Sources: CHX-021, SUM0-002.*

## 9. Integrity: storage content is untrusted input (ARCA-INT)

**ARCA-INT-001** Everything read from the provider MUST be validated before
use: locator/path validity, object size, canonical encoding, schema version,
content hash, dataset/namespace identity and object type. Failures MUST be
typed, never ignored. *Sources: SIG ADM-046, SIG ADM-025, CHX-390, SUM0-029.*

**ARCA-INT-002** Manual edits made outside the application MUST be treated as
untrusted external mutations. Arca MUST make them detectable, for example
through a changed content hash or a commit lacking Arca trailers. *Sources:
CHX-410, SUM0-029, SUM0-030, SIG ADM-046.*

**ARCA-INT-003** Arca MUST support immutable-record checks so unexpected
modification of a record declared immutable is detected. Its design MUST NOT
prevent a later tamper-evident hash chain. *Sources: SUM0-027, SUM0-030,
SIG ADM-025.*

**ARCA-INT-004** When Arca cannot establish that state is valid enough for a
write, the write MUST be refused rather than guessed (integrity before
availability). *Source: SUM0-047.*

## 10. GitHub adapter efficiency and limits (ARCA-API)

**ARCA-API-001** The adapter MUST avoid repository-wide scans. It reads by
deterministic path and partition, with paging and batching. *Sources: CHX-380,
SIG ADM-026, SUM0-049.*

**ARCA-API-002** The adapter MUST use conditional requests (ETag /
If-None-Match) where useful and MUST cache immutable objects by content
hash. *Sources: CHX-380, SIG ADM-026.*

**ARCA-API-003** Rate limits MUST be first-class. The adapter observes and
exposes the remaining budget and reset evidence, honours retry-after, backs
off exponentially with jitter, and never spins. *Sources: CHX-380, SIG
ADM-026, SUM3-032.*

**ARCA-API-004** The adapter MUST report object-size failures, partial
listings and stale change tokens as typed results. *Source: SIG ADM-026.*

**ARCA-API-005** Arca SHOULD expose repository growth measures (object
count, dataset size) so applications can warn before scale degrades GitHub
operations. *Source: SIG ADM-058.*

## 11. Credentials (ARCA-AUTH)

**ARCA-AUTH-001** Arca MUST take credentials only through a token-provider
abstraction: a function that yields a current token or a typed failure (none,
expired, revoked). Arca MUST NOT acquire, refresh or store tokens itself.
*Sources: ARCA-D-002, CHX-022, SUM0-003, SIG ADM-056.*

**ARCA-AUTH-002** Tokens MUST NEVER be written to records, commits, the
offline queue, logs, telemetry, exports, URLs or serialized state. *Sources:
CHX-023, SUM0-004, SIG ADM-004, SIG ADM-071.*

**ARCA-AUTH-003** Arca MUST resolve the acting identity from GitHub for the
token in use and never trust a typed username. It MUST derive a non-secret
capability snapshot (read, write, branch writable, archived, visibility) that
is revalidated after provider errors. *Sources: CHX-022, SIG ADM-056, SIG
ADM-073.*

**ARCA-AUTH-004** A failed credential MUST NOT mutate stored data. A
replaced credential that resolves the configured location to a different
repository identity MUST be refused until the application resolves it.
*Source: SIG ADM-056.*

**ARCA-AUTH-005** Authentication (who) is separate from application
authorization (what they may do). Arca reports the identity; each application
enforces its own capabilities. *Sources: CHX-022, CHX-030, SUM0-003,
SUM0-022.*

## 12. Derived state, migration and recovery (ARCA-MIG)

**ARCA-MIG-001** Arca MUST support rebuildable derived indexes. Each index
records the authoritative source set (hash or revision) it was built from,
can be validated and rebuilt, and never corrupts authoritative records.
*Sources: SUM0-015, CHX-400, SIG ADM-027.*

**ARCA-MIG-002** Arca MUST provide an explicit, versioned, resumable and
idempotent migration workflow. Schema migrations and location migrations
(repository to repository) follow validate, copy, verify, activate. The
source is left untouched until it is explicitly retired. *Sources: SUM0-032,
SIG ADM-028, SIG ADM-058, CHX-300.*

**ARCA-MIG-003** Everything Arca stores MUST be exportable in its canonical
form for backup and escape-hatch use. *Sources: SUM1-020, SIG ADM-029,
SUM3-006.*

## 13. Verification (ARCA-TEST)

**ARCA-TEST-001** Arca MUST ship a provider conformance suite that any
provider runs: round-trip, conditional write, compare-and-swap, concurrent
modification, idempotent repeats, OutcomeUnknown reconciliation, missing and
corrupt objects, unsupported capability, pagination, rate limit, oversized
object, authentication failure, read-only mode and stale change token. It
MUST include an in-memory provider for consumer tests. *Sources: SIG ADM-044,
SUM3-043.*

**ARCA-TEST-002** The pure core MUST have property and model-based tests
(merge, queue, canonical encoding round-trip). *Sources: SIG ADM-037,
SUM3-042.*

**ARCA-TEST-003** The adapter MUST be verified in a real browser WASM host
through Limen, not only on the server runtime. *Sources: CHX-003, SIG ARX-003.*

**ARCA-TEST-004** Telemetry and faults MUST carry identifiers and
operational measures only, never tokens or business content. *Sources:
CHX-420, SUM3-022, SIG AER-021..031.*

## 14. Open questions for the user

| ID | Question | Options | Recommendation |
|---|---|---|---|
| OQ-ARCA-001 | *Resolved 2026-10-08.* How should co-located application data be protected, given that GitHub permissions are per repository and not per folder? | (a) separate repositories, (b) per-application encryption, (c) both | **Decided: (a).** Encryption is deferred to a possible future work item (ARCA-D-007, ARCA-LOC-010). |
| OQ-ARCA-002 | *Resolved 2026-10-08.* One package or two? | One / two | **Decided: two** (ARCA-D-008, DF-ARCA-2026-0003). |
| OQ-ARCA-003 | *Resolved 2026-10-08.* Durable browser storage for the offline queue (ARCA-OFF-002). Correction: Limen 0.7.x *does* ship an IndexedDB store pack (`limen.store`), but its F# binding is not published as a consumable package. | IndexedDB through Limen / localStorage at first | **Decided: localStorage behind a queue-store port now**; a Limen IndexedDB adapter is a follow-up work item (ARCA-D-010, DF-ARCA-2026-0005). Limen's requirements are LCP-043..087 (limen `docs/requirements/LIMEN-INDEXEDDB-REQUIREMENTS.md`). The adapter lives in `EchelonFoundry.Arca.Limen` (DF-LIMEN-2026-0005), and the work is WI-0016 and WI-0019..WI-0022. |
| OQ-ARCA-004 | *Resolved 2026-10-08.* Reuse Aegis's GitHub event store, or keep separate? | Reuse / align later / keep separate | **Decided: keep separate** (different concerns); reuse only the failure model (ARCA-D-009, DF-ARCA-2026-0004). |
| OQ-ARCA-005 | Chrona's legacy monolithic per-user ledger (`kemiller2002/time-tracking-application`, data in `time-tracking-data`) may need read compatibility (CHX-220). Is that Arca's job or Chrona's? | Arca / Chrona | Chrona's. The user expects Chrona to be reconstructed rather than migrated; any data import is a Chrona application-level migration on top of ARCA-MIG-002. |

## 14a. Build order

The user's build order (2026-10-08): **Chrona first, then Summa, then the
rest**, with Helix in parallel. Arca's minimal slices (location and
namespace configuration, record format, concurrency and commit, the GitHub
adapter with the token-provider port) come before Chrona's storage items.
Arca's backlog is ordered to match.

## 15. Traceability by consumer

| Consumer | Consumer requirements that Arca serves | Arca requirements |
|---|---|---|
| Signal | ADM-003, 004, 005, 006, 009, 025, 026, 027, 028, 029, 034, 044, 046, 056, 057, 058, 070, 071, 072, 073; AER-002, 003, 032 | ARCH, LOC, REC, CON, COMMIT-006, OUT, OFF-005, INT, API, AUTH, MIG, TEST |
| Chrona | CHX-021, 022, 023, 024, 025, 026, 027, 101, 102, 105, 200, 210, 220, 230, 240, 250, 380, 390, 400, 410, 420 | ARCH, LOC, REC, CON, COMMIT, OUT, OFF, INT, API, AUTH, MIG |
| Summa | SUM0-002..021, 023, 027, 029..047, 049; SUM1-019, 020; SUM3-002, 006, 014, 022, 032, 033, 040, 042, 043 | ARCH, LOC, REC, CON, COMMIT, OUT, INT, API, AUTH, MIG, TEST |
