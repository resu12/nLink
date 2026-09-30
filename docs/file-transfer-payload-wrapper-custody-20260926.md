# Transfer payload custody: GUI to original wrapper

2026-09-26, `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-destination-gui-adoption-20260926.md`.

## Outcome

The first downstream hop is implemented in the explicit offline attempt
profile: both actual 256 MiB files reach the original wrapper with continuous
native protection. Both PowerShell hosts pass the positive and the exhausted
budget control. The focused rejection and selected compatibility lanes pass.
There are 22 distinct passing tests across the applicable runs below, with two
earlier failed executions preserved and explained separately.

This is GUI-to-wrapper custody, not completion of the entire parent/analyzer
relay. The wrapper's original request still owns the adopted pair's lifetime.

## Implemented boundary

The original GUI offers its protected source and adopted destination while both
are associated with the exact original closed-writer join. Copies and repeated
offers cannot consume or recreate that ownership. The original GUI request,
native process incarnation, immutable parent-attempt bytes, session/transfer,
and both writer origins accompany the bounded offer.

The wrapper uses its already-held original GUI process handle to duplicate both
files read-only. It checks the actual pipe client's PID, original request and
report objects, file identities and exact lengths. The GUI checks the actual
server against its still-current original parent. No process or file is reopened
by a serialized PID or pathname. The new channel is separate from the diagnostic
payload catalog; its 256 KiB limit is unchanged.

Offer, acknowledgment, commit, and receipt occur before GUI cleanup. Both files
remain protected when the GUI retires its original owners. Wrapper custody is
provisional until the original GUI exits with code zero and its original stdout
and stderr tasks finish successfully. A nonzero exit, interruption, malformed
offer, cancellation, stale request, or lost native file cannot issue accepted
custody. All route, integrity-authority and publication flags remain false.

The control packet is capped at 8192 bytes. The GUI's exchange has one ten-second
deadline; the wrapper has a bounded connection wait and a ten-second exchange
deadline, both canceled by owner retirement. Existing caller deadlines remain.
Active adoption and full-file verification use the exact original wrapper
`FrontierParentRegistration.parentWork` clock, under its ownership lock, with
the existing cumulative five-second limit. They do not reset it or allocate an
additional work allowance. Waiting for pipe I/O does not run that active-work
clock. The original GUI ledger-processing budget is unchanged.

After the normal-exit take, verification hashes both exact held files through
read-only native mappings and a 64 KiB buffer. It neither reopens paths nor
changes a shared file position. Verification and terminal take are one-shot.

Only the explicit test profile exposes the adopter. A three-line conditional
hook in the original launch method remembers its exact report object in that
profile; the normal loader compiles it out. Live dispatch remains unchanged.

## Verification

Results are under `artifacts/payload-wrapper-20260926/results/`:

- `wrapper-positive-initial.trx`: one failure. A fixture diagnostic string
  entered the wrapper function's return pipeline, so the caller could not read
  `TimedOut`. The marker now uses host output; no runtime behavior was changed.
- `wrapper-positives.trx`: two positives pass, before the final cumulative-clock
  correction.
- `wrapper-rejection-controls.trx`: ten pass. Both hosts reject wrong
  acknowledgment, mismatched destination identity, interruption before ACK,
  nonzero GUI exit, and forced death of the exact GUI child after provisional
  adoption. The child data processes have already exited before that kill.
- `wrapper-cumulative-budget.trx`: three pass and one fails. Both exhausted
  original-clock cases and the PowerShell 7 positive pass. The Windows
  PowerShell positive stopped at the pre-existing `Copy/repeat retired or
  reissued role join` check, before the new payload offer. Wrapper acceptance
  correctly remained unavailable.
- `wrapper-windows-positive-rerun.trx`: the exact failed Windows positive passes
  on an unchanged source rerun. The earlier upstream rejection is retained, not
  relabeled as a pass or claimed to have a proven root cause. No guard was
  weakened and no runtime workaround was added.
- `wrapper-compatibility.trx`: eight pass. Both default loaders exclude the new
  custody API; both hosts pass the original dispatch, sender-read, and bounded
  helper-to-GUI destination positives. No aggregate suite was run.

The final clock correction was verified with the two positives and two
exhausted-budget cases; it did not change the packet or rejection protocol, so
the ten preceding rejection cases were not repeated. Original object/copy,
repeat, early-take and disposal controls run inside the positives. Cleanup
opens both files exclusively only after the owning processes/resources retire.
All final selected runs have zero skips.

Final full-size positive artifacts:

- `artifacts/payload-wrapper-20260926/powershell-valid-8829ac877fa14f5eaafe73054aaae32c/`
- `artifacts/payload-wrapper-20260926/pwsh-valid-bc9a9d25f5344fb9a9f664fade1a714e/`

Each transfers 268435456 bytes and verifies source/destination SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
The wrapper's assertions use native handles, not these diagnostic strings.
No forced wrapper death, report-task fault, or concurrent disposal during the
mapped hash was injected in this step. This is not a same-account adversarial
security boundary or an exhaustive lifecycle proof.

## Scope still missing and next gate

This hop does not forward the pair to the original parent or analyzer. Writer
origin facts come from the original GUI join, but are not a substitute for the
eventual same-attempt association with downstream native log/writer custody.
No selected-failure witness reader is acquired or reopened here.

The next edge is association of this wrapper-owned pair with the original
native writer custody, then wrapper-to-original-parent provisional adoption
before wrapper cleanup. Do not replace that association with the carried writer
labels. If the upstream role-join rejection recurs, diagnose that first missing
edge before proceeding; its unchanged rerun is not a root-cause finding. Keep
normal-exit gates and the selected-failure pre-verdict reservation. SHA success
does not replace the required second coordinator-published `file_tuna_v4`.

All runs in this step are offline DevLocal transfers. No live NKN/Tuna run,
portable rebuild, GUI matrix, commit, push, or acceptance change is authorized
by this evidence.

## Preservation and recovery

All 1117 previous checkpoint pins matched before implementation. Only six
pre-existing dirty support files were edited; the other 287 remain byte-for-byte
unchanged. The conditional launch hook, three new implementation/test support
files, and this report bring the dirty inventory to 298. Branch and HEAD remain
unchanged. Script parsing and whitespace checks pass.

The recovery seal is
`artifacts/payload-wrapper-20260926/FINAL-VERIFICATION.json`. It chains to the
previous GUI-adoption seal and pins the current source inventory, unchanged
live/acceptance guards, new artifacts, complete test outcomes, and retained
development failures. Existing artifacts are preserved.
