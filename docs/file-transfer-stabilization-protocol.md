# File Transfer Stabilization Protocol

File sharing is V4-only. Keep stabilization work focused on the current V4 path and preserve the stable build as rollback.

## Principles

- Keep bridge default fanout.
- Keep V4 mixed screen-share transfer guarded by `NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE=1`.
- Keep file-only V4 behavior unchanged while tuning mixed transfer.
- Do not add or rename wire frames during stabilization.
- Treat obsolete protocol traffic as incompatible legacy input and log/drop it cleanly.

## Evidence Lanes

Track these separately when reviewing a soak:

- Transfer completion and SHA integrity.
- V4 protocol shape and chunk-batch evidence.
- Payload fill and bridge bulk health.
- Repair/reorder pressure.
- Screen-share coexistence.
- External NKN bridge health.

Progress-timeout analysis should first decide whether the receiver reached all chunks, finalization/hash stalled, `complete.v4` send stalled, or the harness over-reported receiver progress.
