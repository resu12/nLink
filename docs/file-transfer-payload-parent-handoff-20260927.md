# Wrapper-to-original-parent payload handoff — 2026-09-27

Status: bounded offline handoff implemented and verified; no live/acceptance claim.

## Contract

The user authorized the next step from the live-readiness review. This step
extends only the explicit `NLINK_TEST_ATTEMPT_DISPATCH` profile. The ordinary
live dispatch and normal-loader API remain unchanged.

The original wrapper may offer its already verified 256 MiB source/destination
pair only while it is bound to its exact accepted native GUI writer custody.
The offer is bound to the original parent attempt and native relay registration,
not an endpoint, PID, path, copied completion or caller-declared success flag.

Use a separate, one-shot, at-most-8192-byte metadata channel. The original
parent duplicates exactly two read-only handles from its already-held wrapper
process handle, checking identities, distinctness and exact length. No file or
process reopen establishes ownership. The existing diagnostic payload catalog
and its 256 KiB cap are untouched.

Order: wrapper offer → parent provisional duplicate/acknowledgment → wrapper
commit → existing native log/writer relay → successful original wrapper exit
and output-task completion → original relay terminal take and native writer
custody detach → exact pair/writer/attempt/completion association in the parent.
The parent duplicate exists before ordinary writer relay retires the wrapper
pair. No canceled/stale wrapper owner is revived or exempted from cleanup.

The pair remains provisional until the exact original terminal take succeeds.
The accepted parent pair then follows the detached native writer custody;
retiring that custody closes the two payload handles independently of a later
currentness query. Copied objects cannot take, forward or retire originals.
Disposing the pair does not dispose unrelated native writer custody.

Reuse the wrapper's original cumulative `parentWork` and the original parent's
relay `transferWork`, each still limited to 5000 ms. Metadata/duplication and
association are charged to those clocks. The one-shot rendezvous uses the
existing 20000 ms allowance; waiting does not reset or consume a fresh active
work budget. GUI 10000 ms, completion 250 ms and all other limits stay unchanged.
The original wrapper has already hashed the protected files; this hop does not
rehash or issue a transfer-integrity verdict. All authority flags stay false.

## Verification plan

Exercise the existing actual 256 MiB offline DevLocal process tree, not newly
fabricated file owners. Positives on both PowerShell hosts must continue past
the former wrapper-only cleanup boundary, retain protection after wrapper
copies close, and pass exact native parent writer/attempt association. Embedded
controls cover copied/foreign and repeated inputs, early take, clock identity,
and independent retirement. Separate fault cases cover canceled provisional
handoff, wrong writer lineage, exhausted parent work, abnormal wrapper exit
and wrapper death. Preserve failed runs and diagnose their first missing edge.

Run only affected controls and a small compatibility selection. No analyzer
consumption, selected-witness/pre-verdict promotion, portable build, live run,
full matrix, commit or push is part of this step. The second coordinator-published
`file_tuna_v4` requirement remains mandatory.

## Implementation

`FrontierAttemptPayloadParent.cs` contains the profile-only channel, provisional
parent owner and exact native relay association. `FrontierAttemptDispatchLoader.ps1`
alone includes this source. Two three-line conditional hooks remember the original
relay reports and signal native writer retirement; neither compiles into the
ordinary loader. The existing wrapper's native file-identity check is reused.

The fixture forwards the actual pair before the ordinary log relay closes the
wrapper's native writer owner. The parent waits for the original process exit and
original output tasks, invokes the original terminal take, checks the GUI request,
completion hash, attempt and both writer incarnations/sequences, and detaches the
exact accepted native custody. There is no PID/path reopen or replacement launch.
The copied completion and copied owners are rejected without retiring originals.

The parent pair's lifetime follows a cancellation signal from that native writer
custody. Its file handles close independently when custody retires. Queued request
or process-exit callbacks cannot consult a disposed request after successful
detachment. A pair's own disposal does not dispose the separate native writer owner.

## Full-size offline evidence

All seven actual 256 MiB DevLocal process-tree cases passed:

- Valid Windows PowerShell and PowerShell 7 handoffs.
- PowerShell 7 cancellation before commit, foreign writer incarnation, exhausted
  original parent work budget, nonzero wrapper exit and wrapper termination.

The positive cases reached the original analysis-call boundary with protected
payload handles after the wrapper had exited. The fixture did not invoke a real
analyzer or issue a verdict. Native-custody retirement closed both handles without
a subsequent currentness query. Every run then reopened both original files
exclusively and verified their 268435456-byte lengths.

Artifacts are under `artifacts/payload-parent-20260927`. The initial positives and
ordinary-loader guards are in `results/parent-initial.trx` (4 passed). The five
full-size negative cases are in `results/parent-negative.trx` (5 passed). That
second run also retained two channel-fixture failures: reflection received a
PowerShell-wrapped array instead of `System.Byte[]`, before channel execution.
The fixture was corrected to use native typed arrays; no handoff implementation
or budget changed to address those failures.

`results/parent-controls-compatibility.trx` passed all 10 checks: both-host channel
controls, original attempt dispatch, original native relay launch, and native
relay transfer valid/guard cases. This gives **19 distinct passing tests**, with
the two superseded fixture failures retained in the earlier TRX. No aggregate
contract suite or GUI matrix was run.

The channel controls reject oversized packets, name collisions, foreign binding,
reserved-header bytes and body/tail tampering, state regression, repeat acknowledgment,
premature/repeated commit and cancellation. A committed sender release preserves
the original parent's mapping and native file custody.

After timed runs completed, all 14 payload files were independently reopened
exclusively and hashed. Every file is 268435456 bytes with SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`.
This checks the retained test artifacts, not the live route-cycle contract.

## Preservation and next boundary

HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`.
Of 343 pre-existing dirty files, only four scoped support files changed; the
other 339 remain byte-identical. Two previously clean native relay source files
have only the three-line test-profile hooks described above. Five new files are
the handoff implementation, test class, two probe/control scripts and this report.
No changes were committed or pushed.

The reproducible checkpoint is
`artifacts/payload-parent-20260927/FINAL-VERIFICATION.json`. It pins current source,
reports, retained test evidence and payload hashes, and records the earlier
readiness checkpoint and the preserved-worktree audit.

The former wrapper-to-parent gap is closed **offline only**. The next bounded
step is to define and verify consumption of this same-attempt parent-owned pair
by the original native analyzer/selected-witness ownership flow. Existing native
primary/witness APIs must be used without reopening paths, inventing ownership,
or bypassing the frontier guard. The selected-failure pre-verdict reservation
and eventual live-dispatch/acceptance integration remain separate gates.

Do not start a live-256 test yet. Before one is appropriate, complete those
ownership/acceptance gates, run the affected tests, rebuild the portable package,
and require strict unsandboxed preflights. All route requirements, including the
second coordinator-published `file_tuna_v4`, remain unchanged.
