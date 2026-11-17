# Owned-source validation profile and bounded-read optimization

2026-09-26, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-cross-process-native-receive-20260926.md`.

## Result

The bounded file-channel optimization preserves validation semantics and passes
21 distinct focused/compatibility cases. It does **not** close the full-size
offline attempt gate. The Windows PowerShell case still reaches the original
five-second retained-log work limit. Two PowerShell 7 attempts instead fail
earlier with `metadata_not_provided`, before payload transfer or log adoption.
All failures remain retained; none is waived or counted as a pass.

## Measurement and implementation

The explicit offline original-owner fixture now has an opt-in `writer-profile`
case. It measures 32 calls to each currentness check using bound delegates,
before processing work starts. It supplies no validation result to production,
does not cache authority, and does not reset the cumulative work clock.

The source `CanRead` check traverses the original binding, finalizer, launch
owner, sealed-file adoption, native app channels, and retained file identities.
`LocalFrontierFileChannel` previously read the memory-mapped registration,
reserved bytes, digest, and body one byte at a time. These reads now use fresh,
bounded `ReadArray` snapshots on every invocation. Every byte comparison,
state range/monotonicity check, submission/ACK identity, cancellation check,
and existing validation call is retained. The body remains bounded to 32768
bytes. No route, transport, ledger parser, timeout, or acceptance rule changes.

Small original-owner samples, in milliseconds for 32 calls:

| Check | pwsh before | pwsh after | powershell after |
| --- | ---: | ---: | ---: |
| File channels | 0.556 | 0.244 | 0.412 |
| Adoption | 8.929 | 5.543 | 7.755 |
| Binding | 10.896 | 12.762 | 11.943 |
| Source CanRead | 14.983 | 7.643 | 10.838 |

These short, noisy samples are diagnostic, not an SLA or a claim that every
component became faster. The binding sample illustrates that limitation.

A separate diagnostic-only generic replay counted **275734 `CanRead` checks
for 1248 physical reads**, visiting all 54648 lines of the 40814301-byte prior
source in 501 ms. That stream deliberately lacks original custody; it cannot
establish acceptance. The count demonstrates per-record validation
amplification, not the elapsed time of the original owned-source path.
`GuiSmokeLedgerStreamRead` checks currentness during line reads, buffer fills,
origin recording, and result capture. No such call was removed in this step.

## Full-size rerun evidence and next missing edges

Artifacts are under `artifacts/frontier-cross-process-receive-20260926/`:

- `powershell-writer-source-read-cross-process-valid-f50f628a4c5c4c48b96678122941f6c8`:
  native data/hash assertions complete; retained-log processing fails at
  `active_ms=5001`, with 40814303 source bytes and 54648 lines.
- `pwsh-writer-source-read-cross-process-valid-ec6aa309ecf84ef5a1202832b377652a`:
  sender prepares metadata, but receiver terminates with
  `metadata_not_provided`; payload bytes remain zero.
- `pwsh-writer-source-read-cross-process-valid-f71557581e4042c6be79670e71ee038a`:
  unchanged isolated rerun reproduces that earlier metadata failure. Offer and
  accept cross the local transport, sender metadata preparation completes,
  session-open is received, a data frame is logged sent, and the V4 receiver
  starts. The receiver nevertheless never obtains metadata and times out.

The logs narrow the first missing edge to metadata delivery/consumption after
session-open; they do not prove a particular race. Data-frame receive logging
is not symmetrical for ordinary DevLocal frames, so absence of that log alone
is not proof of wire loss. Inspection finds separate get-or-create paths in
`DevLocalTransport.OpenFileTransferDataSessionAsync` and
`DeliverFileTransferDataFrame`, both using the same dictionary. A concurrent
session-creation race is a hypothesis to reproduce, not an established RCA.
No DevLocal or transfer runtime code is changed on that hypothesis.

Next bounded work:

1. Deterministically reproduce the metadata delivery/consumption edge, including
   concurrent first-frame arrival and initial data-session opening. Preserve
   coordinator-issued runtime ownership; transport dictionaries remain evidence,
   never authority. Fix only the demonstrated cause, then rerun the affected
   offline case. Do not hide the failure with retry delays or longer timeouts.
2. The log-processing boundary remains independently unresolved. If further
   same-semantics optimization is insufficient, review a separate contract for
   full custody checks at bounded source reads and capture/publication boundaries,
   with per-record deadline/cancellation checks and tentative outputs discarded
   on invalidation. This changes validation granularity and is **not implemented
   or approved by this report**. Keep the cumulative five-second budget.
3. Full offline success must include original data/hash assertions, original
   writer/source custody join, and all existing completion checks. Only then
   continue the outstanding GUI/parent destination-evidence handoff.

## Verification and preservation

Results are in `artifacts/owned-source-validation-20260926/results/`:

- `bulk-read-focused.trx`: 7 pass, 0 fail, 0 skip (five mapped-channel mutation,
  maximum-bound, cancellation/disposal tests plus two original-owner profiles).
- `file-channel-compatibility.trx`: 14 pass, 0 fail, 0 skip (both hosts' valid,
  body-before/body-after, ACK nonce, state replay, and cancellation cases, plus
  two native-mutex publication-order tests).
- `full-size-after.trx`: 2 fail; `pwsh-isolation-repeat.trx`: 1 repeated failure.
  These are two distinct unresolved end-to-end tests, three failed executions.
- Before-change `channel-before.trx` and `profile-before.trx` are retained as
  development evidence, not additional distinct final passes.

All 1206 prior checkpoint pins matched before work. Of the 272 pre-existing
dirty files, only the two explicit profiling loader/join support files change;
the other 270 retain their bytes and status. One previously clean production
file, three new test/helper sources, and this report bring the dirty inventory
to 277. Two previous pins change intentionally; the other 1204 remain intact.
The final checkpoint records the prior seal hash, complete dirty inventory,
new evidence pins, test outcomes, and unresolved boundaries. It is recovery
evidence, not route/custody authority. Build and `git diff --check` succeed.

No portable rebuild, live GUI/NKN test, full matrix, commit, or push occurs.
No live-test readiness is claimed. Route-cycle acceptance still requires a
second coordinator-published `file_tuna_v4`; offline SHA success is not a
substitute. Parent analysis/reconciliation remain explicit test stand-ins.
