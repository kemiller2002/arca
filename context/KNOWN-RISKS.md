# Arca known risks

| Risk | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|
| Process overhead exceeds decision value | Medium | High | Measure time and rework; use artifact thresholds | Unassigned |
| “Communication Engineering” is treated as validated before evidence exists | Medium | High | Keep boundary claims provisional and comparative | Unassigned |
| Sensitive communication data enters repository artifacts | Medium | High | Define data classes and use synthetic fixtures until reviewed | Unassigned |
| Documentation becomes detached from implementation | Medium | High | Link decisions to tests and refresh handoffs at milestones | Unassigned |
| Baseline is selected after results are known | Medium | Medium | Register baseline and measures before the first slice | Unassigned |
| Offline queue in localStorage: a caller of `LocalStorageQueue.store` without `own` can still race when two tabs save within the browser's cross-tab propagation delay | Low | Medium | `LocalStorageQueue.own` (Web Lock, one owner per namespace) closes it; consumers adopt `own`; the IndexedDB adapter (WI-0016) replaces the medium (DF-ARCA-2026-0009) | arca |
