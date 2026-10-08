# File Transfer Stabilization Protocol

File sharing is route-aware. Keep stabilization work focused on the current production routes and preserve the stable build as rollback:

- regular NKN -> `regular_nkn_v4_fast`, protocol `4`,
- active file Tuna -> `file_tuna_v4`, protocol `4`,
- live post-Tuna fallback -> one-shot `post_tuna_fallback_v6`, protocol `6`, for the affected transfer,
- diagnostic regular-NKN V6 -> explicit unsafe opt-in only.

## Principles

- `FileTransferCoordinator` and its issued DTOs/tokens are the only route/runtime authority. Listener readiness, ACKs, transport observations, pressure, and queue acceptance are evidence, not grants. Rejected/stale callbacks are diagnostic-only and must not cause runtime effects.
- Fix only the first demonstrated missing authority-to-delivery edge. Keep security, coordinator publication, protocol, integrity, and terminal GUI proofs strict; keep performance separate.
- Run focused affected tests first, rebuild the portable package before targeted GUI validation, and use escalated live preflights outside restricted sandboxes. Never bypass failed preflights. Reserve the full matrix for a stable release candidate.
- After at most two focused fix-and-validation iterations without closing a blocker, stop expanding patches and review the implicated architectural boundary. Do not turn optional shadow/companion analysis into automatic release prerequisites; approved dispositions are in [test-lanes.md](test-lanes.md).
- Keep the bridge default stable on the observed V4/V6-parity topology: control/media/bulk `4/8/4`, bulk concurrency `4`, fanout mode. Use alternate bridge profiles only as explicit operator A/B diagnostics with live evidence.
- Keep mixed screen-share transfer guarded by the legacy-named `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE=1` until that flag is renamed.
- Keep file-only V4 behavior unchanged while tuning mixed transfer.
- Do not add or rename wire frames during stabilization unless the change is part of the controlled V6 fallback recovery protocol.
- Treat obsolete protocol traffic as incompatible legacy input and log/drop it cleanly.

## Evidence Lanes

Track these separately when reviewing a soak:

- Transfer completion and SHA integrity.
- Route token, protocol, frame family, runtime profile, and bridge policy evidence.
- Payload fill and bridge bulk health.
- Repair/reorder pressure.
- Screen-share coexistence.
- External NKN bridge health.

Progress-timeout analysis should first decide whether the receiver reached all chunks, finalization/hash stalled, regular-control `complete` send stalled, or the harness over-reported receiver progress.

Obsolete V5 evidence and `file_tuna_v6` route evidence are hard failures in current retained analysis. Historical regular-NKN V6 artifacts may be useful for comparison, but release-default regular NKN must stay on `regular_nkn_v4_fast` / protocol `4`.
