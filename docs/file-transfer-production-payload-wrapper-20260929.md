# Production authenticated GUI-to-wrapper payload handoff — 2026-09-29

## First missing connection

The production source, destination, and closed-writer association existed, but
the authenticated GUI-to-wrapper transfer-file exchange and wrapper custody
were compiled only by the explicit offline test loader. Both normal PowerShell
loaders reproduced `Production payload handoff missing` before implementation.

The predecessor is `production-canonical-writers-20260929/FINAL-VERIFICATION.json`,
SHA256 `53a9d69f21719bce9192d814285fe5b1556c4b9ae161476f0cdcd610bafec5f2`.
All 13,244 pinned files matched before edits. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`; the baseline had 663
dirty entries. Before-images preserve ten existing text files and five pinned
build outputs. No predecessor report or seal is edited.

## Bounded implementation

- The normal loader includes `LocalGuiWriterCanonicalPayloadWrapper.cs`. The
  implementation is moved from the existing fixture, not duplicated. Original
  launch reports are retained directly; starting the optional listener requires
  those exact reports and the original registered request and attempt.
- `TryOfferPayloadsToWrapper` accepts only the same attempt's original source,
  destination, and closed writers, already associated with each other. Cloned,
  foreign, repeated, stale, and retired objects cannot issue another offer.
- The pipe exchange retains native peer-process authentication, process creation
  identities, exact attempt bytes, both closed-writer identities and terminal
  sequences, nonce acknowledgment/commit/receipt, original native file handles,
  exact file identities, 256-MiB sizes, and matching SHA-256 declarations.
- Wrapper custody remains provisional until the original GUI exits successfully,
  original output readers drain, and the one-shot terminal take succeeds. Native
  handle checks and one-shot hashing use the original cumulative work clock.
- Request cancellation, failed GUI exit, exchange failure, and explicit disposal
  retire the listener and duplicated file handles. The existing 8,192-byte
  packet cap, 10-second connected exchange deadline, and 5-second parent active
  work allowance are unchanged. Idle listener waiting remains lifetime-bound.
- Fault injection and downstream native-writer terminal association remain in
  test-only partials. Their private partial hooks are absent from the normal
  compiled type; there is no production fault parameter, mutable public custody,
  parent payload offer, or analyzer forwarding capability.

All authority indicators remain false. This is local transfer-file custody,
not route acceptance or publication completion. No Core runtime path,
coordinator decision, route invariant, limit, timeout, retry, progress, or
throughput policy changed. Merely loading this module does not start a listener
or enable the live scenario.

## Verification

The stabilized test build passed with 339 existing warnings and zero errors.
Both normal loader checks and both valid 256-MiB offline handoffs passed (four
tests). The focused lane passed 30/30: fourteen transfer-rejection cases and
sixteen listener-lifetime cases across Windows PowerShell and PowerShell 7.
These cover bad acknowledgment, foreign file identity, cancellation before
acknowledgment, failed/killed GUI exit, spent budget, withheld commit, explicit
and connected disposal, request cancellation, wrong pipe-client PID, stalled
packet input, and listener startup failure.

The affected compatibility lane passed 14/14: the strengthened loader boundary
checks, all four exact/foreign native-writer cases, both valid parent relays,
the parent guard-order regression, parent/analyzer API exclusion checks, and
the retained canonical-source analyzer consumer. That consumer preserves
exactly three evaluations and one native read and stops before route acceptance.
Combined result: **46 distinct passing offline tests**, with no failures or skips
in the completed green runs. No aggregate suite or full GUI matrix was run.

The intentional missing-API red run and an intermediate missing loader-module
registration failure are retained separately; both loader cases passed after
the registration was corrected. The final verifier requires those exact failed
cases to have subsequent passing results.

The verifier checks every predecessor pin, exact dirty-file preservation,
matching before-images for allowed edits, syntax, whitespace, build/test results,
the unchanged live entry, and recorded child-process cleanup. Listener controls
intentionally unwind before ordinary finalization; their separate exit, report
drain, handle-release, and zero-idle-work assertions are checked in all sixteen
passing cases. Intentional nonzero/killed exits remain negative evidence rather
than being counted as successful transfers. The phase adds two files, bringing
the dirty count to 665.

## Next gate

Connect the wrapper's terminal payload take to the exact original accepted
native-writer custody. Wrapper-to-parent/analyzer forwarding remains test-only;
live execution remains disabled until the remaining production connections and
their gates are complete.

The top-level live call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`, without canonical
ownership enabled. No live NKN, live-256, desktop GUI matrix, portable package,
commit, or push is part of this phase. The second coordinator-published
`file_tuna_v4` route remains mandatory for the live route-cycle contract.
