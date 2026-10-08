---
id: DF-ARCA-2026-0008
title: Migrations always copy to a different location, record progress in the target manifest, and retire the source only by an explicit, conditional step
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - arca
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/ARCA-STORAGE-REQUIREMENTS.md
tags: [migration, relocation, derived-indexes, export, manifest]
provenance:
  contributions:
    EXE-20261008T102316191Z-42ea7eeb:
      operations: [created]
      at: 2026-10-08T10:40:00.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Design decision for backlog slice 10 (WI-0012), within ARCA-MIG-002 and ARCA-LOC-009"
---

# DF-ARCA-2026-0008: Migrations copy to a new location and retire the source explicitly

## Context

ARCA-MIG-002 asks for an explicit, versioned, resumable and idempotent
migration workflow: validate, copy, verify, activate. The source must stay
untouched until someone explicitly retires it. ARCA-LOC-009 says that
relocating a dataset must go through this workflow and never through an edit
of the configured path.

The manifest already records a namespace's location and an optional
migration state, and `Manifest.check` already reports a relocated or
migrating namespace.

There are two constraints:

- GitHub gives atomic commits within one branch, but not across repositories
  or branches.
- Arca cannot rewrite a namespace in place and still leave the source
  untouched.

## Decision

1. **Every migration copies into a different location.** That location is
   another repository, branch or base path.
   - A relocation is the identity transform.
   - A schema migration supplies a pure `Record -> Result<Record, string>`
     transform. The transform must keep each record's id and type.
   - The source is never written during the migration.
2. **Progress lives in the target manifest.**
   - The target manifest goes through the phases `validating`, `copying`,
     `verifying` and then `completed`.
   - While the phase is anything but `completed`, `Manifest.check` reports
     `MigrationInProgress`, so no application uses half-copied data.
   - Running the migration again resumes from whatever is recorded, so every
     step is idempotent:
     - each copy round computes the differences again;
     - each batch's idempotency key comes from its content;
     - an unknown outcome is reconciled, never guessed.
3. **Verification compares the whole target with what the source implies.**
   - Up to three copy-and-verify rounds absorb writes that still land on
     the source.
   - If the target still differs after that, the result is `Unverified`, and
     the target is not activated.
4. **Retirement is explicit and conditional.**
   - `Migration.retire` checks again that the target holds exactly what the
     source implies.
   - It then marks the source manifest `retired`, in a commit conditioned on
     the source's change token. A write that lands on the source after
     activation therefore blocks retirement with `SourceChanged` and the
     paths involved. Nothing is lost silently.
   - A retired source is refused by `Manifest.check` (`Retired`). Its objects
     stay until someone removes them by hand.
5. **The manifest gains two phase values, `completed` and `retired`.** Older
   readers refuse the unknown phase, which is the safe failure.
6. **Derived data is not copied.** Indexes record the source set they were
   built from (ARCA-MIG-001) and are rebuilt at the target.
7. **Export (ARCA-MIG-003) is a canonical JSON archive of a consistent
   snapshot.**
   - The snapshot is read between two equal change tokens.
   - Each object carries its SHA-256, so a backup is verified before it is
     trusted.
   - Corrupt objects are exported exactly as they are stored.

## Consequences

- An application should stop writing to the source while it migrates, for
  example with the read-only degraded mode from ARCA-OFF-005. Writes that
  still land are detected (rule 3 or 4) but must be reconciled by the
  application.
- A schema migration needs a new location, so the application switches its
  configured location when the target activates. ARCA-LOC-009 is enforced
  because the target manifest records the new location, and only a
  migration writes it.
- There is no in-place schema rewrite. Older records remain readable through
  `SchemaSupport` (ARCA-REC-005).
- Restoring from an export is not provided yet. An archive can seed a new
  location through an application-level import.
