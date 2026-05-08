# File Transfer Stabilization Protocol

File sharing is V5-only. Keep stabilization work focused on the current V5 path and preserve the stable build as rollback.

## Principles

- Keep bridge default fanout.
- Keep mixed screen-share transfer guarded by the legacy-named `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE=1` until that flag is renamed.
- Keep file-only V5 behavior unchanged while tuning mixed transfer.
- Do not add or rename wire frames during stabilization unless the change is part of the V5 handoff/recovery protocol.
- Treat obsolete protocol traffic as incompatible legacy input and log/drop it cleanly.

## Evidence Lanes

Track these separately when reviewing a soak:

- Transfer completion and SHA integrity.
- V5 protocol shape and chunk-batch evidence.
- Payload fill and bridge bulk health.
- Repair/reorder pressure.
- Screen-share coexistence.
- External NKN bridge health.

Progress-timeout analysis should first decide whether the receiver reached all chunks, finalization/hash stalled, `complete.v5` send stalled, or the harness over-reported receiver progress.

Legacy names such as `v4_default_21k`, `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE`, and `v4_file_only_required` may still appear in logs or scripts during the V5 cleanup window. Treat them as naming debt, not as evidence that V4 was negotiated.
