# Fixed-4s late-publication characterization: native receipt reached, witness unavailable

Date: 2026-09-28. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Result

Ran the approved single small offline original-process characterization after
the Debug rebuild and isolated preflights. **One failed test, zero passed,
66.588 seconds; no retry.** The fixed 4-second hold overcame the previous
admission blocker in this episode. The failure advanced to the original primary
diagnostic witness resolver, not the transfer or native late-publication timing.

Original run:
`artifacts/native-late-publication-characterization-20260928/pwsh-frontier-late-publication-2d576d91e9524cae9f5e625becfb5165/`.

The selected primary failure and verdict remain unchanged:
`filetransfer_fallback_frontier_recent_progress_grace_recheck_missing`,
`FAIL_PROTOCOL_OR_INTEGRITY`, gate `fail`.
Do not accept this run as the completed witness milestone or route-cycle proof.

## Established by this original run

- Receiver stdout line 79 records exactly
  `late-publication-fixed-4s-v1; max_hold_ms=4000`.
- Receiver line 130 records `closed-within-bound; physical-copies=3; root=unchanged`.
  Sender line 143 and receiver line 156 report the same 172,032 bytes and SHA-256
  `67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`.
  The released destination independently has that size/hash.
- The original wrapper completed native writer/source handoff and both writer
  checks, then exited 0 without timeout. The primary analyzer ran once, recording
  `evaluations=3 nativeReads=1` and 1,232 original events/origins.
- `late-publication-primary-observation.json` records the admitted native
  `PublishedWithinObservationBound` receipt: 6,537 bytes, observation
  `4882592fa4214a9e9352c6815a6d0f61`, writer `p56472r1e46ef4c24f34d84`,
  completion physical line 1419 and marker physical line 1634.
  The original capture probe checked receipt identity, root/write/completion
  ordering and both original coordinator views. Its authority flags stay false.
- The first probe error is `AfterResolve: Original optional resolver produced
  no witness`. There was exactly one resolver call, zero witnesses and no
  diagnostic output commit. Later AfterOptional/Closed failures follow from this.
- The analyzer's original input hash is unchanged. Its receipt records complete
  proof artifacts, authoritative snapshot mode, parity pass, exit 1 and no timeout.
  Complete artifacts are not a passing primary verdict.

## Read-only RCA: confirmed mismatch and remaining uncertainty

The exact primary-selected failure is semantic line 971, writer sequence 584,
physical source line 1273. The original recorded native line equals retained
line 971. Both its `session_id` and `transfer_id` are `[redacted]`.

`LocalNativeFrontierWitness.ps1:110-115` requires the receipt's session/transfer
tokens to equal tokens computed from those raw fields. The token helper at
`CoordinatorSnapshotEvidence.ps1:16523` hashes the supplied text literally.
Thus both row tokens are `fte1-017ad73325fcf108a972edac`, while the original
receipt has two different tokens:
`fte1-1e222d75e166ffb8001f47cd` and `fte1-81befef2f545c31a869718bb`.
Hashing the original sender completion marker's IDs matches the receipt, not
the redacted row. The writer identity matches.

This proves a concrete scope-join obstruction in the saved inputs, without
calling a resolver, authority evaluator, receipt producer or analyzer again.
It does **not** prove the exact first internal rejection reached by the original
call: `AnalyzerOrchestration.ps1:9189` does not request the resolver's existing
`OmissionReason`, and custody/work-limit checks can also return null.
The run did not retain that reason. Do not label it conclusively as
`native_frontier_receipt_ambiguity`, a work-budget failure or a runtime fault.

`scope-join-analysis.json` under the runner evidence directory records these
read-only comparisons. No source lines were rewritten, redaction disabled,
native receipts reconstructed or retired evaluator state replayed.

## Recommended next bounded step

1. Preserve the existing omission reason from the **single original resolver
   call** in the test-only primary observation, with focused tests for reporting
   success/rejection and unchanged cleanup. Do not invoke the resolver twice,
   grant authority or add a fallback match.
2. Review a privacy-preserving exact-scope binding for the selected failure.
   The contract must bind original writer/source/observation and session/transfer
   without treating `[redacted]` as an identity or exposing raw IDs. First
   determine whether existing native evidence can establish that exact binding;
   any new protected diagnostic field requires explicit scope review.
3. Add focused positive/redacted/foreign/ambiguous controls before a separately
   gated characterization. Retain the 1-64 witness cap, original selected failure,
   three evaluations/one native read, report/view preservation and output custody.
   Keep both hold policies and every production deadline unchanged.

No production or resolver fix is implemented in this turn. No timing sweep,
automatic retry, live-256 or full GUI matrix is justified by this result.

## Preflight, cleanup and preservation

Entry and runner preflights matched all 2,057 saved pins and 513 dirty entries.
The incremental Debug rebuild passed with zero warnings/errors and unchanged
pinned binaries. The 71 focused controls were verified from their pinned TRX,
not rerun or represented as native proof. Native process checks/run were
explicitly escalated outside the restricted sandbox; developer mode was false,
no conflicting processes existed, and inherited NLINK settings were cleared only
in the runner's process. The create-new attempt marker prevents a second run.

The unchanged parent retains canonical 256 MiB/GUI/live labels and creates its
canonical source. Actual carrier traffic here was the small 168 KiB Debug
named-pipe fixture: no live NKN, portable GUI or live-256 validation is claimed.

At 16:06:40 UTC, sender 35540, receiver 56472, wrapper 42444 and analyzer 32096
were absent; no conflicting project process remained. All 50 original run files
opened read-only with exclusive sharing. The original primary recorded zero
open pins and cleared retained text. All ten report hashes still match their
original before/after values. No process was killed.

No source/test/binary, prior capture, timer, acceptance rule, commit or push
changed. Only this handoff and ignored runner/evidence files were added.
The final preservation seal and independent check are under
`artifacts/late-publication-fixed-hold-characterization-20260928/`.
