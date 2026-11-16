# Destination connected cancellation fix

The connected destination-cleanup defect is repaired. All 35 focused regression
and compatibility cases pass on Windows PowerShell 5.1 and PowerShell 7. The
prior review's cancellation blocker is resolved. This is a bounded cleanup fix,
not approval of the accumulated checkpoint or live route acceptance.

## Cause and implementation

Windows PowerShell can leave an already-pending asynchronous pipe read running
after its cancellation token is cancelled. The original destination completion
method then joins that read indefinitely. The prior review reproduced the read
behavior; the new normal-loader regression reproduced the failure through the
original public adoption cleanup, with an authenticated original helper process
that stayed connected after sending only two header bytes.

Before the fix, the Windows PowerShell header-cancellation case exceeded the
five-second test observation window. The test recorded failure before closing
the exact server pipe to release the blocked task. This safe cleanup is not
counted as a passing result.

`tools/FileTransferOps/LocalGuiWriterCanonicalDestination.cs` now registers
`pipe.Dispose` on the existing linked deadline token. The registration is scoped
inside the pipe and cancellation-source lifetime and is disposed before candidate
cleanup acquires ownership locks. Its callback only closes I/O; it does not join
tasks, evaluate ownership, or publish custody or route state.

The production change is two added lines. The scenario budget, 75,000 ms GUI
handoff limit, ten-second receiver limit, timer start positions, identity checks,
source association, final nonce commit, and authority flags are unchanged.

## Focused verification

The new cases reuse the normal-loader headless scenario fixture and original
helper identity. Each PowerShell host covers three stalled boundaries and three
ways to end the attempt: partial header, incomplete packet body, or withheld
final commit; explicit cancellation, scenario timer expiry, or attempt disposal.

The peer keeps its pipe open without a read cancellation token. Passing requires
the original cleanup to finish within five seconds without the test watchdog,
no destination adoption, a closed server handle, and peer-observed closure before
app shutdown. Commit-stall cases also prove the provisional duplicated file
handle is released by reopening the destination exclusively. The deadline tests
shorten only the fixture's original scenario timer after the stall is reached;
they do not alter a production timeout.

All 18 connected-stall cases passed without forced cleanup. All 17 selected
compatibility cases also passed:

| Coverage | Passing cases |
| --- | --- |
| Connected header, body, and commit stalls across three cleanup modes and both hosts | 18 |
| Existing destination lifetime, including the real 76-second delayed receiver | 11 |
| Normal-loader boundary and same-attempt source/helper ownership | 6 |

The before-fix reproduction is retained separately as one expected failure.
The fix-specific review found no further issue in cancellation ordering, candidate
release, timer placement, or the unchanged destination publication checks.

The test project compiled with 339 warnings and zero compilation errors. The
initial sandboxed build was blocked by Avalonia's external build-service log;
the escalated build completed. Its shell invocation then reported a stray
unquoted logging-option token; the retained MSBuild log confirms successful
compilation. No second build was needed for the two-line native-source fix:
the normal loader compiles that source afresh in each PowerShell test process.

## Preservation and next gate

The preceding checkpoint-review seal is
`artifacts/production-handoff-checkpoint-review-20260930/FINAL-VERIFICATION.json`,
SHA256 `1283662a2e305f380fed06026b41cd306b82946325d63d6403519bc5ffb7c616`.
All 19,522 preceding file pins were checked. Exactly five differ: the destination
source, three directly affected test files, and the rebuilt test assembly.
Before-images of the four source/test files are retained; unrelated pinned work
is unchanged. The implementation, results, and verification artifacts are under
`artifacts/production-destination-connected-cancellation-20260930`.

Resume review of the intended accumulated checkpoint. Do not treat this scoped
fix as exhaustive approval of all dirty
work. A reviewed clean checkpoint, fresh portable build, and strict escalated
preflights are still required before the targeted
`regular-v4-live-activation-off-on-off-256mb` run. Its second coordinator-published
`file_tuna_v4` route remains mandatory. No commit, push, portable build, live NKN
run, or full GUI matrix was performed for this fix.
