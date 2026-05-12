# File Transfer Stabilization Protocol

File sharing is V6-only. Keep stabilization work focused on the current V6 receiver-driven path and preserve the stable build as rollback.

## Principles

- Keep bridge default fanout.
- Keep mixed screen-share transfer guarded by the legacy-named `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE=1` until that flag is renamed.
- Keep file-only V6 behavior unchanged while tuning mixed transfer.
- Do not add or rename wire frames during stabilization unless the change is part of the V6 transport-epoch recovery protocol.
- Treat obsolete protocol traffic as incompatible legacy input and log/drop it cleanly.

## Evidence Lanes

Track these separately when reviewing a soak:

- Transfer completion and SHA integrity.
- V6 protocol shape and chunk-batch evidence.
- Payload fill and bridge bulk health.
- Repair/reorder pressure.
- Screen-share coexistence.
- External NKN bridge health.

Progress-timeout analysis should first decide whether the receiver reached all chunks, finalization/hash stalled, regular-control `complete` send stalled, or the harness over-reported receiver progress.

Legacy names such as `v4_default_21k`, `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE`, and V4-named test files may still appear in logs or scripts during the V6 cleanup window. Treat them as naming debt, not as evidence that V4 was negotiated.
