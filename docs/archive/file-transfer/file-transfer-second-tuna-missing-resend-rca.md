# Second-Tuna delayed ingress: missing-resend diagnostic

2026-09-20. Base HEAD `7c28d20701d3dd253d6c1c16f8f673eb3aef082a`.
Scope: fresh live evidence inspection and a diagnostic-only correction. No new
acceptance exemption, runtime change, live run or historical evaluation.

## Finding

Run `20260920T170220Z` passed snapshot integrity, full/compact parity, the required
second coordinator-published Tuna route, SHA integrity, performance and provenance.
Its completed original receipt nevertheless retains correctness FAIL. The first
failure is `file_tuna_data_path_mismatch` at retained line 18804, receiver writer
`p14428r50cd0dfb83874cd8`, sequence 8354, with variant `(none)`.

The frame was rejected before dispatch. This is not evidence of accepted regular
NKN payload under Tuna authority. The reviewed recovery contract separately
requires an exact Tuna resend and receive for a fallback-to-Tuna reactivation.
The old envelope's provenance does not satisfy that delivery requirement.

## Exact evidence

Source: `artifacts/terminal-checkpoint-live-20260920/live/20260920T170220Z/regular-v4-live-activation-off-on-off-256mb`.
The original retained input has 115213 lines, SHA256
`b6fc6d07a3c93404d0208a927eaae54c4cb5dfe4e817cd5e10b7f4d5696e7c61`.
Original receipt: `8fed3b6fc79943469193832b1176d584`, status `completed`,
`completedProofArtifacts=true`, unchanged input, analyzer exit 0, no timeout.

| Retained line | Exact observation |
| --- | --- |
| 5086 | Sender sequence 6536, regular-v4 epoch 0/publication 1: chunks 321..323, 64512 bytes, security sequence 985, sent over regular NKN. |
| 18584 | Receiver sequence 8276: actual service projection of fallback-v6 to Tuna-v4, epoch 3, runtime/leg/publication 4, commit 2. |
| 18804 | Receiver sequence 8354: the same envelope arrives over regular NKN under that Tuna tuple, `receive_guard`, `rejected_before_dispatch`, `authority_effect=none`. |
| 18805 / 18806 | Same writer sequences 8355 / 8356: explicit mismatch and exact ignored-frame companion. |

The source and rejection share envelope fingerprint
`ftenv1-187e577416ba2a1a44985049a3038b54023ef82e5cb1cd8276619d8750473636`
and payload fingerprint
`ftpay1-05a5223b74b46bd10d0ca42932bb4807b988a52a13aceb1f32bde31716002028`.
Their local transport and installation IDs differ as expected for opposite peers;
neither those IDs nor equal local generations establish peer lineage.

Read-only inspection of the original full summary finds the captured sender
publication at boundary `fte1-a8b542d12ee895e531d8665e`, generation 3, with the
accepted publication-1 fingerprint `fte1-4a820103b125d0869718e9a2`. Both recorded
generation-43 observations preserve the same regular route tuple. The receiver
publication is the accepted commit boundary `fte1-28d76fb003b347bdba86b5ab`,
generation 362, with publication-4 fingerprint `fte1-056ab026c8395b460e99680c`.
These are local facts from already validated rows, not a new publication consumer,
authority evaluation, forwarding certification or cross-peer proof.

Exact-range inspection finds earlier regular-NKN dispatch entries at 3783 and
6027, with security sequences 111 and 984 and different envelope fingerprints.
It finds no Tuna send/receive pair for 321..323. An earlier dispatch entry is not
a coordinator-authenticated committed receive frontier. Broader range coverage,
retained committed continuity and the other twelve unresolved candidates have
not been independently adjudicated here.

## Cause and bounded correction

`ShouldRejectFileTunaV4PayloadReceive` uses the captured coordinator publication
and returns before file-transfer dispatch. Keep it unchanged.

`Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult` distinguishes
`missing_tuna_resend` internally, but unlike `missing_exact_tuna_receive` it leaves
the existing diagnostic Variant field as `(none)`. The primary preserves that
empty variant when reporting the selected failure. This loses the distinction
between the two missing delivery edges; it is not a demonstrated routing defect.

The existing C2i two-writer/service fixture already reproduces the missing-resend
branch using actual coordinator publications and delayed encrypted ingress.
Therefore no duplicate producer fixture or speculative runtime correction is
needed. Add only
`second_tuna_rejected_regular_ingress_exact_tuna_resend_missing` to that existing
branch and verify its propagation through original primary reports.

All predicates, Boolean recovery outcomes, candidate membership/counts, selected
locations, primary evaluation count, route policy and public schemas stay intact.
The exact-Tuna positive case still requires both send and receive; a wrong range
or missing receive still fails. Missing source/transition/companion and terminal
or ambiguous histories must not borrow the new variant.

## Verification and fixture repair

The new expectation failed before the change with `Reason=missing_tuna_resend`,
`Recovered=false`, matched rejection chain and `Variant=(none)`. The shared
diagnostic matrix then passed on both PowerShell hosts after the correction.

The original integration attempt exposed an independent stale test setting:
the current explicit persistence opt-in requests the native frontier relay, but
the older worker settings omitted `FrontierLaunch`. The worker correctly failed
`Default GUI parent inherited native readiness` before primary analysis began.
The fixture now declares that expected opt-in for `write`, `existing` and
`write-error` modes, matching the actual parent invocation. It uses the existing
strict native attachment/retirement assertions; the default no-readiness guard
and production launch code are unchanged. No environment marker is erased or
forged to bypass the check.

The first attempted rebuild also encountered stale restore paths into an
unavailable sandbox NuGet cache. No test ran in that attempt. All edits here are
PowerShell or documentation; the existing compiled test harness reads these
scripts from the workspace. Focused verification uses `--no-build --no-restore`,
not a claimed fresh C# build. No dependency changes or network restore.

Detailed final verification: `artifacts/rejected-ingress-resend-20260920/RESULTS.md`.
Seven unique selected tests now pass: the 22-case diagnostic matrix, genuine
missing-resend service integration, exact/missing-receive/wrong-range payload
cases, four-mode persistence workflow and default positive workflow. Both
PowerShell hosts are covered. The final affected lane passed 5/5 in 5m32s;
the earlier diagnostic and service tests supply the other two final passes.
Parse and whitespace checks pass. The original live input and primary summaries
remain byte-identical; no commit or live rerun was performed.

## Still open

The finalized live run remains failed: 13 unresolved data-path candidates, one
stall-grace obligation and one fallback-coordinator aggregate finding. This
diagnostic correction cannot turn any of them into a recovered incident.

Do not remove the exact-Tuna delivery predicate just because an old envelope was
rejected or final SHA passed. The reviewed publication consumer contract explicitly
separates local provenance, cross-peer causation, rejection safety and delivery.
Any alternative stale-rejection correctness disposition requires a separate
contract decision and exact coordinator-owned continuity/lineage proof with
negative controls before implementation. Until then, investigate the remaining
fresh stall-grace and fallback-coordinator witnesses independently. No new live
test is needed for this diagnostic-only step.
