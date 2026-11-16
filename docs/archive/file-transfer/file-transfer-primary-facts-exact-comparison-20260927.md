# Exact facts-body comparison

Date: 2026-09-27. Status: 15 focused checks pass. The single rebuilt offline
canonical run now reaches EOF and two analyzer evaluations, but still fails
at the original cumulative work guard during post-EOF physical-origin lookup.

## Cause and bounded change

The previous canonical run attributed 76.131% of native reader work to the
file/facts currentness path. A separate mapped-channel control now isolates
the facts-body comparator as the dominant component. This control creates
codec-free transport bytes, never analyzer facts or native custody authority.

The control uses 2,097,152 bytes and 147,443 checks (twice 73,721 source lines,
plus one). This is a representative envelope, not a measurement of the previous
canonical facts length; real reads also perform fragment-boundary checks.

| Component | Before | After |
| --- | ---: | ---: |
| Full facts currentness | 25.5893 s | 9.4224 s |
| Original guarded body method | 24.3746 s | 8.2841 s |
| Header comparison alone | 0.0927 s | 0.0878 s |
| Fixed file offer, no nested facts | 1.2124 s | 1.2040 s |

These are sequential component measurements, not a partition of one call or
CPU-only timings. Scheduling can perturb them. The preliminary candidate-only
measurement was 8.3012 s with lifetime pins held around the measurement loop;
the post-change production method above includes its original per-call pins.
The full facts control is approximately 63.2% faster in this observation.

Only three production lines change in
`tools/FileTransferOps/LocalFrontierParentFactsChannel.cs`: the native comparison
call and its import/signature. `msvcrt.dll` `memcmp == 0` replaces the
`RtlCompareMemory == count` equality test. The import explicitly uses the C
calling convention and pointer-sized byte count.

The entire live body is still compared on every invocation. The same 262,144
byte chunks, intermediate cancellation callbacks, ordering, retained body,
GC pin, safe-handle reference, bounds checks and final lifetime checks remain.
Header/state/ready/cancelled/acknowledgment checks and publication/open padding
checks are unchanged. No cached verdict, sampling, reopened path, larger budget
or authority change is introduced. Reversing those three lines reconstructs
the previous sealed source SHA-256 exactly:
`c185fda38ff6d9a24f3d20c614b19adba090d7c97e3c88d70cfb8f902e0b5181`.

The test-only primary profile additionally reports retained facts byte count
and acknowledgment presence, without another currentness evaluation.

## Focused verification

`artifacts/primary-facts-exact-20260927/results/facts-exact-comparison.trx`:
15/15 pass on the first run (1 minute 18 seconds).

- Exact body comparisons on Windows PowerShell and PowerShell 7, including
  all bytes of small bodies, word/tail/chunk boundaries, 2 MiB and maximum
  4 MiB bodies, retained-array mutations, and publication/open padding.
- Header mutation, missing ready signal, original state transition, regressed
  and invalid states, cancellation, disposed view, and disposal during a
  comparison. Mid-comparison mutations at the next chunk are rejected.
- Existing first-refusal/first-close and work-profile contracts, two unchanged
  default source projections, seven native source controls, and two default
  loader isolation controls.

Smoke rebuild: zero warnings/errors. Changed PowerShell scripts parse, and the
focused diff check passes. Component control results are retained as
`baseline-control.json` and `optimized-control.json` in the artifact directory.

## Canonical verification

Artifact:
`artifacts/origin-capacity-implementation-20260927/pwsh-canonical-08578769f9ff4ed4a11cd5c3ed68c165`.

The native transfer handoff succeeds and repeated take remains rejected. The
primary reads all 73,726 lines (69,712,349 code units), observes genuine EOF, and
reaches two analyzer evaluations with one native parser invocation. Previously
it stopped before EOF with zero evaluations. The overall test still fails;
three original evaluations and completed proof artifacts have not been reached.

Native read work is 18.5055 seconds across 73,727 calls. Its original checks
spend 1.4790 s on base liveness, 0.4808 s on scope, 10.3397 s on files/facts,
5.7924 s on pins, and 0.1012 s on mapped reads. Files/facts are checked 149,581
times. The actual facts body is 1,928,659 bytes; no nested acknowledgment is
present on this primary channel. A subsequent isolated control at this exact
size and call count takes 8.7533 s for full facts, 7.7539 s for the guarded
body, and 1.2832 s for the fixed file offer. This control does not rerun the
canonical scenario or replay native authority.

First rejection is `resource:budget` at 20,001 ms; first close is `SourceOrigin`,
with `started=True` and `ended=True`. The later diagnostic total is 20,005.4567
ms. The generic payload-catalog error is downstream of this retirement, not
evidence of a new payload handoff failure. The 20,000 ms allowance is unchanged.

### New first edge and follow-up

The `merged-native` test worker explicitly calls
`GetFrontierPhysicalOrigin` for every parsed event after EOF to write
`analysis-observed-source.json`. Each call performs the original live resource
check under the same cumulative work clock. The artifact contains all 73,726
lines, but only the first 12,945 origins are positive; ordinal 12,946 is the
first zero, followed by 60,781 zero origins in total. This matches the first
`SourceOrigin` budget refusal. There are 13,137 successful active-other
resource-check sequences overall, including setup, not just origin calls.

This all-origin diagnostic is also an asserted compatibility contract in
`FrontierRelayAnalysis.ps1` and `FrontierMilestoneCompatibility.ps1`; it must not
simply be removed, sampled, or replaced with positive fabricated origins to
make the gate pass. The next bounded step is to isolate this post-EOF origin
phase with the actual facts size and complete line count, measure its original
checks separately, and review a minimal exact-validation optimization against
the full-origin and small mismatch/foreign-reader/budget controls. Preserve
check frequency/order, reference identity, lifetime and the cumulative limit.
Any proposal to change the all-origin contract or checking frequency instead
requires separate contract review before implementation. Do not tune the pin
path solely because it is now the second-largest reading cost.

No canonical retry, live GUI/NKN run, portable rebuild, full matrix, commit or
push occurred. EOF and payload SHA do not prove the live route cycle: the
second coordinator-published `file_tuna_v4` remains mandatory.

### Cleanup and input integrity

Primary PID 37408 exits 1 without timeout. GUI PID 56492 exits 0 with complete
output capture and no deadline extension; GUI active work is 6,361.3928 ms of
10,000. Both native roles (57620 and 47708) report 268,435,456 bytes on
`post_tuna_fallback_v6`. Both payload hashes independently match
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
exclusive read/write access succeeds, and all four processes are gone.
Source/retained/writer/semantic counts are 101,872 / 101,870 / 101,872 / 73,726.
The primary input is unchanged before/after, SHA-256
`7e301d79add0b4b04e7df9ace7c359b580acc7283df199b620d295ff91f6b71b`.

## Preservation

Preflight verified all 6,935 pins from
`artifacts/primary-work-profile-20260927/FINAL-VERIFICATION.json`, SHA-256
`8ac32c5712ae95bf1f9b5d033695669576c6ab38f193cc4b9a441b438984fe49`.
HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
The starting workspace had 424 dirty entries; unrelated work is preserved.
The final dirty audit has 427 entries: only the three intended existing files
changed, with this report and the two component-control files added. No paths
were removed and no pre-existing status changed. The only additional changed
pin is the rebuilt smoke assembly. Checkpoint and final hash audit are in
`artifacts/primary-facts-exact-20260927/FINAL-VERIFICATION.json` and
`artifacts/primary-facts-exact-20260927/FINAL-CHECK.json`.
