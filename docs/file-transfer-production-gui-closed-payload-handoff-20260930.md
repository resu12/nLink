# Production GUI closed writer and payload handoff

The production GUI finalizer now joins the original closed role writers to the
attempt's held source and destination, and offers that pair before the original
parent completion. The GUI call is implemented and verified offline. This does
not complete the production wrapper or parent integration and is not live
route-cycle acceptance.

No application routing, acceptance rule, goodput policy, timeout, portable
package, live preflight, GUI/NKN run, commit, or push changed in this phase.

## Starting state and change

The predecessor is
`artifacts/production-gui-destination-lifetime-20260930/FINAL-VERIFICATION.json`,
SHA256 `20fd7da6751a747e21981b9696d7d19eb2c4b61c89fca584d5766f2a0aef62b9`.
All 682 dirty entries matched before editing. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with no staged changes.

Two production tooling files changed. In
`LocalGuiWriterCanonicalPayloadWrapper.cs`, the new
`TryOfferClosedPayloadsToWrapper` accepts only the original finalizer binding.
It uses the attempt's private source, destination, and existing role-writer
join. It releases the role lock before invoking the existing payload exchange.
It adds no file scan, hash pass, retry, reconstructed ownership, or authority.

`GuiSmoke-Windows.ps1` passes the original attempt privately into
`Write-FileTransferGuiLogLedgerEvidence`. After the existing native-capture and
persisted-evidence gates, it offers the pair before preparing and completing
the original parent handoff. Missing or rejected optional custody, including an
exception in the offer, does not skip ordinary completion. A native completion
failure is still not retried through ordinary completion.

One existing fixture gained a narrowly gated branch. Three focused test files
were added. Before-images of the edited files are retained under this phase's
artifact directory; the additional saved source-join fixture was not changed.

## Offline verification and limits

The initial contract probe failed with `Production closed-payload handoff
missing`. The four new test cases now pass on Windows PowerShell and PowerShell
7. The normal-loader cases check the binding-only API, private argument, and
original scenario forwarding. They execute the actual completion block with
six controlled states: absent attempt, rejected offer, throwing offer, accepted
offer, absent binding, and rejected preparation. Ordering and single completion
are checked independently of native custody.

The other two cases use real Core writers and a real local cross-process
256 MiB source/destination pair in the existing augmented fixture. They execute
the production completion block without fixture-side role binding, payload
binding, or payload offer calls. Null/copied bindings, a copied attempt, and a
missing source or destination are rejected without consuming the valid writer
join. The original wrapper then checks accepted native writers and file release.

The first Windows PowerShell integration attempt failed because a new test
control replaced the private source with a copied object. That invalidated the
genuine destination's source association. The composition API does not accept a
source object; that control was removed, not accommodated in production code.
The failing result is retained and the affected case passed on rerun.

Four existing valid/foreign-writer compatibility cases also passed, bringing
the phase total to eight distinct green cases. The build passed with 339
warnings and zero errors. It used escalation for the
existing Avalonia build-service filesystem requirement; tests remained offline
in the normal sandbox. No aggregate suite or full matrix was run.

These tests do not execute the whole production finalizer, nor a normal-loader
end-to-end payload chain. The pair fixture still supplies the wrapper listener
and terminal adoption calls. Its successful payload transfer is not proof of
live Tuna routing, production wrapper integration, or coordinator publication.

## Next production integration

The next missing caller is in the production wrapper. It must start the
original payload listener at the original GUI launch, then perform the existing
normal-exit, file-verification, and exact native-writer take before releasing
ownership. Inspection found no production PowerShell caller for
`TryStartPayloadWrapper` or `TryTakeWithWriters`; those calls remain in fixtures.
Until that integration exists, the new GUI offer can be omitted after its
existing connection budget. Do not treat this interim state as live-ready.

Verify those actual wrapper callers offline, then review the remaining parent
and primary handoffs. A reviewed checkpoint, fresh portable build, strict
escalated preflights, and targeted live validation still follow. Acceptance
continues to require the second coordinator-published `file_tuna_v4` route.

Evidence and the immutable final seal are under
`artifacts/production-gui-closed-payload-handoff-20260930`. The phase verifier
requires eight distinct latest passing cases, preserved predecessor pins and
dirty work, released payload files, terminated recorded children, PowerShell
syntax checks, and a clean whitespace diff before writing the seal.
