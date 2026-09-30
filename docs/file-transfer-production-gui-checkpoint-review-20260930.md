# Production GUI checkpoint review and missing custody callers

The accumulated changes are not ready for checkpoint approval or the owned
diagnostic live run. Production GUI dispatch is connected, but the GUI does not
call destination adoption, closed role-writer binding, or payload offer. Offline
fixtures supply those calls themselves. The first missing edge is the original
GUI receiver-destination adoption lifetime.

This is a source-backed integration finding, not a newly observed live timeout
or transfer failure. No production code, acceptance rule, or timeout changed in
this review. No tests, portable build, live preflight, GUI/NKN run, commit, or
push were performed.

## Starting checkpoint and review scope

The review began from
`artifacts/production-gui-scenario-dispatch-20260930/FINAL-VERIFICATION.json`,
SHA256 `6a7c2676532e333f4f5209db373bcc4d00d113122a011bec1b2ab47db187492f`.
All 16,135 predecessor pins and 677 dirty entries matched before the report was
added. HEAD remains `a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`.
The index was empty.

The starting worktree contained 139 modified tracked files and 538 untracked
files: 217 documentation, 33 application-source, 372 test, and 55 tooling files.
This review traced production custody boundaries and packaging prerequisites;
it is not an exhaustive approval of all 677 files. Earlier reports and evidence
remain unchanged.

The previous milestone's 38 passing offline tests remain valid for dispatch,
identity, privacy, capture-off behavior, rejection, and the other recorded
component scopes. They do not establish a complete production GUI custody path.
The next step in that sealed report is superseded by this finding.

## Evidence and first missing edge

Production source acquisition and sender handoff are called by
`Get-FileTransferTunaSourceHash` in `tools/GuiSmoke-Windows.ps1:2571`, including
`TryHoldSource` at line 2581 and `TrySendToHelpee` at line 2585. The top-level
action obtains the admitted original owner through `TryTakeScenarioOwner` at
line 43048. The source helper returns a hash; it does not return the custody
object to the scenario. The native attempt retains the source internally.

The receiver starts its automatic destination handoff from
`src/nLink.Core/FileTransfer/SessionFileTransferService.OutboundTransferController.cs:594`.
`SessionFileTransferService.NativeDestinationLifetime.cs:30` starts that work,
with a ten-second deadline at line 91. The GUI listener for that handoff is
created inside `AdoptDestinationCore` in
`tools/FileTransferOps/LocalGuiWriterCanonicalDestination.cs`.

A scan of production C# and PowerShell under `src` and `tools` found only the
declarations of the following methods, with no callers:

| Missing production operation | Declaration | Valid fixture call |
| --- | --- | --- |
| Adopt the receiver destination | `tools/FileTransferOps/LocalGuiWriterCanonicalDestination.cs:19` | `tests/nLink.SmokeTests.Core/Support/FrontierAttemptSenderReadGui.ps1:46` |
| Bind original closed role writers | `tools/FileTransferOps/LocalGuiWriterCanonicalWriters.cs:37` | `tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersJoin.ps1:106` |
| Offer the original payload pair to the wrapper | `tools/FileTransferOps/LocalGuiWriterCanonicalPayloadWrapper.cs:39` | `tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapperProbe.ps1:49` |

Source and destination closed-writer binding is likewise supplied by
`FrontierAttemptSourceJoin.ps1:8` and `FrontierAttemptDestinationJoin.ps1:5` in
the test support directory. The production GUI finalizer prepares and completes
its existing parent handoff, but does not perform these custody operations.
The wrapper payload listener serves a different pipe; it is not a receiver
destination listener.

Without the GUI adopter, the receiver has no production GUI listener for its
`nlink-destination-*` pipe. The inferred result is expiry of the optional handoff
and disposal of its held evidence, even if the ordinary file transfer succeeds.
That would leave the protected destination and payload pair unavailable to the
original primary analysis. No live run was used to claim this outcome.

## Root cause and test coverage boundary

The native custody primitives and downstream wrapper/parent/primary plumbing
were implemented and tested as components. The original production GUI
orchestration that joins them is still incomplete.

The latest dispatch probe executes the production runner and action, but
`tests/nLink.SmokeTests.Core/Support/FrontierCanonicalGuiConsumerProbe.ps1:88`
replaces the scenario body with an argument/identity assertion. Successful
canonical-tail fixtures explicitly invoke destination adoption, writer binding,
and payload offer. Those tests cannot detect absence of those same calls from
the actual GUI scenario.

## Bounded fix plan

1. Review the destination-adoption lifetime and timing contract first. The GUI
   adopter's 75,000 ms total budget starts before waiting for a connection
   (`LocalGuiWriterCanonicalDestination.cs:56`); the receiver has ten seconds
   including connection. Starting that adopter at the beginning of a long
   256 MiB transfer can expire it before the receiver is ready. Define a bounded
   original-GUI start, cancellation, join, and disposal sequence that covers
   slow transfer completion without silently increasing either limit.
2. Add a failing normal-loader regression that executes the actual production
   GUI adoption caller. Keep the original attempt, source, role request, and
   adopted destination private. Cover delayed receiver readiness, cancellation,
   rejection, capture-off, and cleanup. Fixture-injected adoption must not stand
   in for proof of that caller.
3. Implement only that first missing edge and run its directly affected tests.
   Preserve native identity, process-lifetime, source-hash, and destination-handle
   checks. Do not reopen a destination by path, reconstruct custody from scalar
   values, or treat diagnostic custody as coordinator authority.
4. Once adoption passes, connect original closed role writers and the original
   source/destination pair to the existing wrapper offer before parent handoff
   completion and owner disposal. Verify these production callers through the
   normal loader, including unsuccessful handoff and cleanup. Keep this a
   separate downstream proof edge, not a reason to modify unrelated runtime
   routing or performance behavior.
5. Review the resulting accumulated checkpoint, then rebuild a fresh portable
   package and run strict escalated preflights. A dirty-source seal is not a
   clean-source checkpoint: `tools/FileTransferRouteAcceptanceEvidence.ps1:2054`
   still records `source_dirty`. Verify any new build output paths are separate
   from retained evidence before invoking packaging cleanup.

No fix is implemented by this review. Keep the current diagnostic limits,
route acceptance, coordinator authority, and goodput policy unchanged. Run only
the affected offline lanes during implementation; do not repeat aggregate
suites. After the missing production chain is verified and a fresh portable
build passes strict preflight, validate the targeted route-cycle scenario before
considering the full GUI matrix. The second coordinator-published
`file_tuna_v4` remains mandatory; SHA OK or fallback completion does not replace
that proof.

## Review verification and resumption

`artifacts/production-gui-checkpoint-review-20260930/Verify-Review.ps1` rechecks
every predecessor pin and dirty entry, records the production source inventory
and hashes, verifies declaration-only references and the GUI AST, records the
fixture calls, checks the existing deadlines, and checks syntax/whitespace.
No tested production or fixture code changed, so no dotnet tests were rerun.

The review observation and final seal are saved alongside that verifier. Resume
from `artifacts/production-gui-checkpoint-review-20260930/FINAL-VERIFICATION.json`
and address the original GUI destination-adoption lifetime first. Do not treat
this review seal as checkpoint approval or a new live acceptance result.
