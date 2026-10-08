---
id: DF-ARCA-2026-0002
title: The data repository is configured per deployment, each application owns its namespace, permission separation uses separate repositories, and there is no at-rest encryption for now
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
tags: [storage, data-location, permissions, encryption, multi-application]
provenance:
  contributions:
    EXE-20261008T080342508Z-ce85c57b:
      operations: [created]
      at: 2026-10-08T08:04:43.010Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Derived from Signal, Chrona and Summa requirements and user decisions of 2026-10-08"
---

# DF-ARCA-2026-0002 — Per-deployment data location and application-owned namespaces

- **Date:** 2026-10-08
- **Status:** accepted (user decisions of 2026-10-08)
- **Decision type:** requirement and architecture

## Context

Several Echelon applications store data in GitHub through Arca. Some need
different permissions; Summa's financial records and Chrona's time records,
for example, have different readers and writers. **GitHub permissions apply
per repository, not per folder.** Folders inside one repository cannot
enforce different permissions by themselves: a token that can write the
repository can write every folder in it.

The options considered were:

- **(a)** separate repositories per permission boundary, with the configurable
  data location allowing a different repository per application;
- **(b)** per-application encryption with application-specific keys,
  possibly through Aegis, so co-located data stays unreadable to other
  applications;
- **(c)** both.

## Decision

1. The data repository is **configurable per deployment** and never
   hard-coded (ARCA-LOC-001).
2. Each application **creates and owns its own folder structure
   (namespace)** in the repository and must not assume it is the only
   application using it (ARCA-LOC-002, ARCA-LOC-003). This applies to Arca and
   to every application: Signal, Chrona, Summa and future applications. Each
   carries a matching requirement in its own backlog.
3. Each application's data must be **separable under different
   permissions**. This is achieved with **option (a): separate repositories
   per permission boundary**. Each application can point at its own
   repository or at a shared one (ARCA-LOC-004).
4. **No at-rest encryption for now** (ARCA-LOC-010). Option (b) is deferred.
   It is recorded as a possible future work item, not a requirement.

## Consequences

- Deployments that need Summa and Chrona under different permissions use
  different repositories. Applications that share a repository share its
  permissions, and namespaces then only keep well-behaved applications apart.
- The record format and the provider capability model leave room for a later
  explicit encryption capability without a format break.
- Moving an existing dataset to another repository uses Arca's migration
  workflow (ARCA-LOC-009, ARCA-MIG-002).
