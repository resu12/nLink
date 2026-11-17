# Historical archive

Start with the [current documentation index](../README.md) for operating, building and releasing nLink. This archive keeps investigation history out of the current documentation directory without deleting evidence or rewriting past outcomes.

- [File-transfer investigations and design notes](file-transfer/): 399 previously top-level `docs/file-transfer-*.md` reports. Their content is retained; relative links were adjusted where needed for relocation. They describe their recorded source, inputs and approval boundaries, not necessarily the current application.
- [Historical evidence summaries](evidence/filetransfer-route-ab/): four files previously tracked under `artifacts/filetransfer-route-ab/`. They are preserved byte-for-byte. Embedded artifact paths identify the original runs; moving these summaries does not move or recreate those run outputs.
- [Superseded screenshots](images/): eight original 0.4.5/0.7.0 images, preserved byte-for-byte. The [current gallery and provenance](../images/README.md) remain separate.

Some historical links refer to local, untracked evidence that is not present in a source checkout. Do not treat those missing artifacts as a passing result, reconstruct them as evidence, or run old commands simply because they appear in an archived note.

Archiving is not a new validation or a change to accepted contracts. Historical failures, limitations and seals remain unchanged; case 10323 remains paused. Current release gates are in the [release runbook](../ReleaseRunbook.md) and [RC checklist](../release/rc-validation-checklist.md), with test dispositions in [test-lanes.md](../test-lanes.md).
