# Production GUI destination handoff lifetime

The production GUI now arms a destination listener before Send and joins it
before app shutdown. The first missing integration edge from the checkpoint
review is implemented. Twenty-one distinct offline tests passed, including a
receiver delayed for 76 seconds and pending-listener disposal on both PowerShell
hosts. Closed role-writer binding and the payload offer remain unconnected in
the production GUI; this is not approval for a live diagnostic run.

No application routing, acceptance, goodput policy, portable package, live
preflight, GUI/NKN run, commit, or push changed in this phase.

## Starting state and scope

The predecessor is
`artifacts/production-gui-checkpoint-review-20260930/FINAL-VERIFICATION.json`,
SHA256 `3ffb9c6fb8e5208253d7013f6d53934609c6ceea31804da89bfd40ad0285a55f`.
All 678 dirty entries matched before editing. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with no staged changes.
Seven edited files have before-images in this phase's artifact directory.

Only three production tooling files changed:
`LocalGuiWriterCanonicalDestination.cs`, `LocalGuiWriterCanonicalDispatch.cs`,
and `GuiSmoke-Windows.ps1`. The remaining edits and additions are focused tests,
fixture compatibility, this report, and verification artifacts.

## Timing and ownership contract

The new `TryStartDestinationAdoption` entry creates the pipe before returning
and runs adoption on a native worker task. Its wait uses the GUI's remaining
scenario budget, calculated after source acquisition and hashing. It does not
block GUI route actions while the transfer runs.

This deliberately separates connection readiness from the handoff timer. For
the new asynchronous entry, the GUI's 75,000 ms verification timer starts after
connection; the remaining scenario budget continues to cap the entire operation.
The receiver's existing ten-second limit, including connection, is unchanged.
The existing synchronous `TryAdoptDestination` entry retains its original
75,000 ms total budget. No numeric limit was raised, but the new listener's
timer placement differs from the old synchronous call.

The native attempt derives the expected bytes and hash from its original held
source. It retains the original helper request, process checks, source checks,
destination-handle validation, hash verification, nonce exchange, and one-use
admission. The background task receives no PowerShell callback. Diagnostic
custody still grants no route, transfer-integrity, or publication authority.

The scenario calls `TryCompleteDestinationAdoption` in its `finally` block,
before app shutdown and evidence finalization. Failure cancels the listener;
normal completion joins within the scenario budget and the existing GUI cap.
Explicit attempt disposal also cancels and joins before retiring held files.
Owner teardown signals cancellation without joining under its lock. Capture-off
does not invoke either native operation. The admitted destination stays inside
the original native attempt for the next integration edge.

## Verification and fixture correction

The initial loader check failed with `Production destination lifetime missing`.
After implementation, eleven new normal-loader cases passed: success, explicit
cancellation, scenario deadline, malformed packet, and pending attempt disposal
on Windows PowerShell and PowerShell 7, plus a PowerShell 7 case that delays the
receiver for 76 seconds before a successful handoff.

These tests execute the production cycle's start block after its source-hash
call and before its Send boundary, and the production scenario's cleanup call.
The desktop and transfer are replaced by headless role processes. The helper
supplies a synthetic local destination packet under its original process
identity. This tests GUI custody and lifetime, not a live transfer or route
verdict. Repeated/copied attempts, unchanged authority flags, and joining before
app shutdown are checked.

Six affected source, loader, and dispatch tests passed. Four existing destination
compatibility cases initially failed during writer shutdown: their fixture was
injecting `ReceiverRequest` in addition to the new production argument. The
resulting duplicate parameter prevented the transfer fixture from running, and
cleanup obscured that earlier failure. Removing only the duplicate fixture
argument resolved all four cases on rerun. Those cases retain coverage of the
existing source/helper association and source/attempt retirement behavior.

The final build passed with 339 warnings and zero errors. Its first sandboxed
attempt was blocked by Avalonia build-service filesystem access; the escalated
build succeeded. Tests ran offline in the normal sandbox. No aggregate suite
or full GUI matrix was run. The phase verifier also executes both production
capture-off guards, checks PowerShell syntax and whitespace, checks child exit
and synthetic destination release, and verifies preserved work and evidence.

## Resume at the writer and payload handoff

Evidence and the final seal are under
`artifacts/production-gui-destination-lifetime-20260930`.
`Verify-Phase.ps1` checks predecessor pins, allows only the seven documented
edits and generated build updates, verifies the four new worktree files, and
requires the latest result for all 21 tested cases to pass. Earlier reports and
seals are not rewritten.

Next, connect the original closed role writers and the original source and
destination payload offer before the original parent completion and owner
disposal. Do not reconstruct custody from paths or scalar evidence. Validate
those production callers before checkpoint review, a fresh portable build,
strict escalated preflights, and the targeted live scenario. The second
coordinator-published `file_tuna_v4` remains mandatory for route-cycle acceptance.
