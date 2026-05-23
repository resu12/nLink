# File Transfer Stabilization Protocol

File sharing is route-aware. Keep stabilization work focused on the current production routes and preserve the stable build as rollback:

- regular NKN -> `regular_nkn_v4_fast`, protocol `4`,
- active file Tuna -> `file_tuna_v4`, protocol `4`,
- controlled post-Tuna fallback -> fresh one-shot `post_tuna_fallback_v6`, protocol `6`,
- diagnostic regular-NKN V6 -> explicit unsafe opt-in only.

## Principles

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
