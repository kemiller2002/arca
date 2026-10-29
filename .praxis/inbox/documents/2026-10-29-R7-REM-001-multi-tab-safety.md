# R7-REM-001: multi-tab safety evidence for Rehearsal 6 defect R6-D-002 (Arca)

Source: Indy-init `docs/rehearsal-007/REMEDIATION-PLAN.md`, work package R7-REM-001.

## Defect

In Rehearsal 6 the application kept one localStorage record for the device and read, merged and wrote it from several tabs without exclusion when `navigator.locks` was missing. Acknowledged changes were lost on every run (AR-035-O4, R6-D-002). Two localStorage lease fallbacks were tried and neither was correct; localStorage has no compare-and-swap. The application did not use Arca (deviation D-07).

## What Arca already provides

`EchelonFoundry.Arca.Limen` (0.3.0+): one owner per namespace through a Web Lock, a fencing epoch raised by a compare-and-put inside an IndexedDB transaction, and a fenced save for any older owner. Without Web Locks, `LimenQueue.own` returns `OwnershipUnsupported` (or a declared per-tab memory-only queue): durable stores are never written by several tabs at once.

## This work item

Real-browser evidence (Chromium in this repository's browser verification, WebKit in CI) that:

1. without Web Locks no tab owns the durable queue and nothing is written;
2. under seeded random interleavings of three tabs (enqueue, save, steal, a late save from a taken-over tab, closing the owner, reopening), no acknowledged save is lost and a fenced save writes nothing;
3. acknowledged changes survive the browser's storage being restored into a new context.

No product code changes. Applications must adopt Arca's queue rather than their own store (Indy-init R7 requirement).
