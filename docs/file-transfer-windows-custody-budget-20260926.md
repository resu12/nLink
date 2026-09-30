# Windows custody-budget RCA — 2026-09-26

Continues `file-transfer-payload-native-writers-20260926.md` on `v0.7.0`,
HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`.

## Finding and bounded change

Fresh original-process offline 256 MiB runs locate the Windows PowerShell
failure in the GUI's cumulative five-second work budget, before native parent
acknowledgment. Payload delivery is not the failing proof edge. The required
owned-ledger processing and its nested native custody validation consume most
of the available budget before parent facts publication begins.

Temporary test-only measurements, retained in the run artifacts:

| Measurement | Elapsed work |
| --- | ---: |
| Initial owned-ledger interval | 3758 ms |
| Parser-profile ledger reading | 2493.8086 ms |
| Parser-profile record parsing | 208.3247 ms |
| Parser-profile output write/hash | 70.3511 ms |
| Original file-channel checks, 188792 calls through parser completion | 1035.0675 ms |
| Word-comparison file-channel checks, same count | 873.4425 ms |

The parser read interval includes source validation, native reads and line
decoding; it is not disk-I/O time alone. The channel totals include earlier
work through parser completion, not only the parser interval. These are
elapsed-time diagnostics with instrumentation and scheduling overhead, not a
CPU profiler or performance guarantee. A further instrumented run measured
915.3454 ms in file-channel checks, 821.716 ms in app-request checks,
179.509 ms in GUI request checks and 286.9016 ms in ledger-origin checks.
Those sibling measurements demonstrate distributed validation cost; they do
not justify deleting a custody check or identify one remaining sufficient fix.

The only production change in this step is `LocalFrontierFileChannel.MatchesBytes`:
read complete 64-bit words, compare them against the actual retained packet on
every call, and compare every remaining tail byte. Both digest and submitted
body use the same complete comparison. No cached verdict, second expected
snapshot, sampling, new authority, or extra accepted state is introduced.
Registration/reserved-byte checks, state monotonicity, cancellation, submission
and ACK identities, native checks, read boundaries and clocks are unchanged.

All temporary timing hooks were removed. The original parser, cursor, launch,
app-request, ledger-origin and four diagnostic support files return to their
pre-step bytes. No fixture rejection controls were removed or moved outside
their original work accounting.

## Verification

`artifacts/payload-windows-budget-20260926/results/file-channel-comparison.trx`
passes all seven tests. The new normal-loader controls run on both PowerShell
hosts with packet lengths 1, 7, 8, 9, 1025, 32767 and 32768. They mutate every
header/body byte for the smaller packets; word/tail boundaries for maximum
packets; the retained digest/body; cancellation and view disposal. Existing
direct channel tests cover all control bytes, maximum length and revocation.

Six diagnostic full-size executions remain failed and retained. The word
comparison reduces measured cost but does not establish full-path acceptance.
The final uninstrumented Windows positive reaches semantic copy at 4487 ms,
persistence at 4536 ms and fails preparation at 5002 ms. It enters files/facts
publication but neither acknowledgment nor completion. It must not be counted
as a successful payload/native-writer association.

The final uninstrumented association run is **2 pass / 2 fail**:

| Case / artifact suffix under `artifacts/payload-writers-20260926` | Result |
| --- | --- |
| `powershell-writer-valid-ef9df8f86f1f45049a9723acddc871c8` | Copy 4487 ms, persist 4536 ms, preparation rejected at 5002 ms. |
| `powershell-writer-foreign-454ffb08ea0d4b73a8e0c275a0cea5a1` | Copy 4638 ms, persist 4678 ms, preparation rejected at 5001 ms; foreign-origin probe not reached. |
| `pwsh-writer-valid-b74b6fe4a0bc4b8ab6d3ead2740807c6` | Native handoff complete at 4525 ms; positive association, copied/repeated input rejection and independent handle retirement pass. |
| `pwsh-writer-foreign-e378deec3b8643b5955f75917505b1a4` | Native handoff complete at 4529 ms; intended foreign-writer rejection passes. |

The selected compatibility lane passes **20 / 20**: both hosts' original
handoff, body-before/body-after corruption, ACK nonce, state replay and
cancellation cases; buffered-reader budget/capture invalidation; native
closure ordering; and default-loader isolation. Build and whitespace checks
pass. There are **29 distinct passing tests**, two unresolved full-size cases,
and eight retained failed executions (six diagnostic and two final), across
37 executions. No live readiness is claimed while either Windows case is blocked.

## Remaining first edge and next bounded plan

The first missing proof edge remains completion of native parent facts and
acknowledgment within the original GUI work budget, before the wrapper can
associate the payload pair with accepted native writers. A foreign-writer test
that expires here has not exercised its intended rejection and is a failure.

1. Review the existing nested custody-check call graph for a boundary-scoped
   validation design. Determine exactly which repeated evaluations occur
   inside one original synchronous operation, including cancellation, native
   handles, immutable mapped bytes, state transitions and lock ordering.
2. Keep any proposal review-only. Reusing a validation result across nested
   calls changes observation granularity and requires separate approval; this
   optimization does not authorize it. No public token or caller-supplied
   success flag may replace original ownership. Both clocks stay at five
   seconds and every rejected operation must retire tentative outputs.
3. If another exact-equivalent implementation optimization is demonstrated,
   test its changed path first. Otherwise obtain approval for the reviewed
   boundary contract before implementation. Do not accumulate speculative
   packet, transport, retry or timing changes.
4. Rerun only the two blocked Windows cases and directly affected controls.
   Wrapper-to-original-parent payload adoption remains after this gate;
   primary/analyzer consumption and live validation are still later.

No portable rebuild, live GUI/NKN run, full matrix, commit, push, acceptance
relaxation or real analyzer invocation occurs in this step. The required second
coordinator-published `file_tuna_v4` route remains mandatory for the route cycle.

## Recovery and preservation

The prior checkpoint verified all 801 pins before work. Its SHA-256 is
`690b136829342280a159766259439157b521c30afc2323e17c020bb97ed8f265`.
Only the pre-existing channel source and its pre-existing test class change;
the other 302 baseline dirty files are preserved. A new comparison helper and
this report bring the dirty inventory to 306. Nothing is staged.

The new recovery seal is
`artifacts/payload-windows-budget-20260926/FINAL-VERIFICATION.json`.
It records final source/artifact hashes, exact test outcomes and the unresolved
boundary. Failed artifacts are preserved as failures, not reclassified or removed.
