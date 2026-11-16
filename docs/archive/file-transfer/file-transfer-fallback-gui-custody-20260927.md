# Native fallback destination GUI custody and origin-limit RCA — 2026-09-27

Status: receiver-to-GUI implementation verified on both PowerShell hosts.
Canonical fallback handoff to the parent/primary is blocked by the
existing aggregate source-origin limit. It is not a route-acceptance pass.

Baseline: `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`,
376 existing dirty entries. Previous checkpoint:
`artifacts/payload-native-fallback-20260927/FINAL-VERIFICATION.json`, SHA-256
`20338ec27d47ce1b113a3a0a5aa35734c17c52111957205985287986f3e4b2e1`.

## Implemented boundary

The dedicated `writer-source-read-cross-process-carrier-full-gui-valid` fixture
uses the original GUI-held 256 MiB source, original app processes, production
Core/NKN coordinator paths and the existing offline native pipe carrier. The
actual helper destination is handed off using `NativeDestinationEvidence` while
its original one-shot read remains held. The GUI validates the original helper
request/process, native file identity, length and full digest. Repeated handoff
and reopening the evidence read remain rejected. The GUI-held destination then
joins the same original closed-writer owner as the source.

A fixture-only readiness event starts the GUI's existing 75-second handoff wait
after the helper has verified its destination. This is scheduling, not evidence
or authority. The full-size fixture's existing 180/185/210-second app/GUI/parent
allowances are unchanged, as are all native active-work budgets. No path reopen
is used to acquire payload custody. Exclusive path probes test release only.

## First failed proof edge

Two fresh exploratory canonical process trees completed the transfer and GUI
destination adoption, but stopped in `TryProcessObservedLedger`, before the
closed-writer join, wrapper/parent acceptance or original primary launch:

| Run | Source lines | Retained origins | Writer origins | Semantic origins | Total stored origins |
| --- | ---: | ---: | ---: | ---: | ---: |
| `pwsh-cda4dc57e1d34f06b7a9b5c43950549e` | 101,792 | 96,275 | 96,277 | 69,592 | 262,144 |
| `pwsh-546c6fc082484244b31082a641e83e67` | 101,808 | 96,275 | 96,276 | 69,593 | 262,144 |

Both are under `artifacts/payload-fallback-primary-20260927/`.
The diagnostic rerun reported `active_ms=9950` and
`source_bytes_read=87261184`. The byte/line counts above come from the original
source and partial outputs, not a reconstructed accepted capture.

`GuiSmokeLedgerStreamRead.AddOrigin` limits the sum of all three output origin
lists to 262,144. Each source line may occupy more than one list. The partial
outputs reach exactly that cap while source lines remain unread. The next
origin is rejected. Thus a source below the separate 262,144-line maximum can
still exceed the aggregate origin bound. The diagnostic run was close to the
10-second GUI work budget too; this does not justify increasing that budget or
claiming that an origin-cap change alone will complete the pipeline.

The ordinary canonical event catalog is retained unchanged. No evidence is
removed, no failure/witness is manufactured, and no diagnostic receipt is
imported from another transfer. Exploratory failed runs remain failed artifacts.
The new negative integration regression verifies this specific refusal and
cleanup; it is not a replacement acceptance criterion.

## Bounded fix-plan review required

Do not simply increase the processor constant. The parent facts decoder also
enforces a cumulative `MaximumLines` origin count across its three roles
(`LocalFrontierParentFactsValidation.cs`), and the native facts channel has a
separate 4 MiB serialized-body cap. A coherent revision must preserve exact
physical origins, ordering, counts, immutable ownership and downstream decoding.

Recommended next step: review a bounded origin-representation contract across
processor, serializer, parent decoder and primary consumer. Compare a lossless
compact representation against a revised aggregate reference budget, account
for worst-case metadata size, and define exact-boundary/one-over controls before
implementation. Keep the existing source/line bounds, work budgets, authority
rules and route acceptance unchanged unless explicitly approved otherwise.
Dropping canonical events merely to fit the current cap is not recommended.

After a reviewed implementation: run focused representation/mutation/cap tests,
then only this canonical full-size offline process tree. Preserve any newly
observed first edge. Only after original primary custody is established should
the same-transfer native diagnostic receipt/marker and primary-selected failure
witness be integrated. A second coordinator-published Tuna route and live route
acceptance remain separate, unproven obligations.

No production code, primary/parent acceptance rules, payload limits, transport
settings, live dispatch or default loader is changed in this step. No live test,
full matrix, portable rebuild, commit or push is performed.

## Final focused verification

Eight checks passed, zero final failures or skips:

- Two full-size native fallback GUI handoffs, PowerShell 7 and Windows
  PowerShell: original native source/destination, equal 268,435,456-byte length
  and SHA-256 `3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
  matching app session/transfer, closed-writer join, copied/repeated input
  rejection and exclusive file access after retirement.
- One full canonical fallback rejection: aggregate origins stop at 262,144,
  original source remains longer, no primary stdout or analysis receipt is
  produced, and both 256 MiB files are released. This final run is
  `pwsh-aa77cfa82e0d455593f05bf4e5a9f4d8`; GUI active work was 9,367 ms,
  below the unchanged 10,000-ms limit. The integration remains blocked.
- Two existing processor output/origin-limit controls, one per host.
- One earlier Regular-only full-size GUI handoff and two default-loader
  exclusion checks.

Results: `fallback-gui-boundary.trx`, `origin-cap-controls.trx` and
`handoff-compatibility.trx` under
`artifacts/payload-fallback-primary-20260927/results/`. The two exploratory
positive-custody failures are preserved separately as `primary-pwsh.trx` and
`primary-pwsh-diagnostic.trx`; neither is reclassified as a successful handoff
to the primary. Unverified downstream fixture edits were removed.

Five changed PowerShell fixtures parse without errors; whitespace checks pass.
Seven baseline fixture/test files changed and one report was added. All other
baseline dirty files and evidence pins are preserved. The final seal is
`artifacts/payload-fallback-primary-20260927/FINAL-VERIFICATION.json`.
