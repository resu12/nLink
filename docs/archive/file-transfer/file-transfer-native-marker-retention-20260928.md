# Native payload marker retention — 2026-09-28

## Result and scope

Implemented the bounded diagnostic-retention fix on branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`. The GUI default semantic catalog now
retains `filetransfer_frontier_payload_marker` in its explicit diagnostic list.
The pinned LegacyV0 catalog, coordinator authority catalogs, native validators,
witness resolver, acceptance predicates, runtime code and timing bounds are
unchanged. No commit, push, portable build, live transfer or full matrix was run.

The preceding finding and scope are recorded in
`docs/file-transfer-failure-witness-feasibility-20260928.md`. The previous
1414-pin / 481-dirty-entry baseline matched before this work began.

## Change and regression coverage

- `tools/GuiSmokeEvidenceCatalog.ps1`: add the existing payload-marker event to
  the diagnostic-only semantic retention list (one line).
- `FileTransferGuiHarnessScriptTests.cs`: require the event in the default
  catalog and exclude it, alongside the completion receipt event, from both
  SnapshotAuthorityV1 and GenerationStampedEffects.
- `FrontierEvidencePersistenceTests.cs` and
  `Support/FrontierGuiEvidencePersistence.ps1`: add one native fixture mode on
  Windows PowerShell and PowerShell 7. It uses the actual default catalog and
  genuine Core-generated native marker, checks exactly one original and retained
  marker, exact line text (including writer identity/sequence), its original
  one-based physical source index, and the original shared copy-origin object.
  Existing persistence checks bind all output hashes, lengths, identities and
  writer ranges. Capture, copy and persisted evidence retain false authority
  flags; existing handle/payload cleanup checks also execute.

This fixture uses seeded Core observations and original local native processes.
It proves retention/copy/persistence, not live witness eligibility or a successful
same-transfer failure-witness run. Test-only reads inspect the protected files;
they are not imported as native custody or supplied to an authority evaluator.

## Verification

The test project rebuilt with project-reference rebuilding disabled: zero errors,
338 warnings. No production binary hash changed against the previous pin set.

Before the catalog fix, the catalog regression failed for the missing event.
The first native assertions used incompatible file-sharing flags; those test
reads were corrected to respect the already-open evidence handles. Both native
host cases then failed at the expected missing semantic-marker assertion. All
attempts are retained; the initial fixture error is not evidence of the defect.

After the one-line catalog fix:

- Seven focused tests passed: catalog (1), native retention/copy/persistence (2),
  wrong-role receipt rejection (2), native payload finalization (2).
- The single existing altered-marker-range rejection test passed.
- Four existing native fixture controls passed: duplicate marker and mutated
  completion-source bytes, each on both PowerShell hosts. Duplicate insertion
  revokes its writer registration; altered source bytes do not acquire source
  custody. Retaining marker text does not relax these checks.
- Scoped `git diff --check` passed. The source diff was compared against
  pre-edit copies, not only against HEAD, to preserve accumulated work.

Evidence, TRX files, build output, pre-edit copies and preservation checks are in
`artifacts/native-marker-retention-20260928/`. The final verification manifest
records the exact changed paths and chains the preceding seal. Apart from the
four scoped source/test files, only this report and generated test artifacts
are new or changed; previous saved captures and reports remain unchanged.

## Remaining boundary and next step

The old successful 168 KiB diagnostic capture still has zero semantic payload
markers; it is not rewritten or retroactively accepted. This fix is demonstrated
by fresh retention fixtures, not by reinterpreting that saved capture.

Next, review the watchdog-root compatibility contract: the ordinary classifier
can select `frontier_stall_repair_due`, whereas the native witness resolver
currently requires `frontier_recent_progress_grace_elapsed`. Specify exact
ordering, identity, interval and negative controls before changing that resolver.
Do not merely allow both names. The separately bounded all-copy physical-send
delay design, small characterization, canonical 256 MiB witness and live GUI
enablement remain later gates. No approval or implementation of those changes
is implied by this retention fix.
