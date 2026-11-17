# Alternative acceptance shadow: bounded admission implementation

2026-09-25. Branch `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4` plus existing uncommitted work.

Implemented the first bounded test-only shadow from
`file-transfer-alternative-acceptance-contract-review-20260925.md` following the
user's approval to proceed. Production runtime, classifier and acceptance remain
unchanged. This is candidate admission and decision-logic validation, not a
complete implementation or positive validation of the alternative scenario policy.

## Implemented scope

The opt-in is `NLINK_PUBLICATION_ALTERNATIVE_ACCEPTANCE_SHADOW=1` inside the
existing test-only primary observer. With it absent, no shadow artifact is
created. With it enabled, the original Service/yield probe invokes a small pure
decision helper while its original source, writer and completed authority-view
custody is still valid. The helper has no I/O or authority-evaluation capability.

It consumes the already computed original mismatch proof and already validated
yield, reciprocal, rejection, dispatch-window and recorded-security-span results.
The selected Event must be the same object selected by the original primary.
Both eligible strict reason/variant pairs remain unchanged and unrecovered.
The output is separately versioned `alternative-acceptance-shadow.json`; no
fields are added to the existing Service/yield diagnostic artifact.

Candidate coverage uses the original primary's **distinct** `CandidateRecords`
and verifies its `UnresolvedCount`. It does not deduplicate or run classification
again. Raw `EffectiveCandidateRecords` includes rejection companions and is not
the distinct-candidate set. If another distinct unresolved candidate exists,
one selected match cannot make coverage pass.

The original views, mismatch proof and preceding diagnostic results are checked
unchanged before publishing the shadow. Original input custody is checked before
and after, with the final cumulative work check after those validations. The
existing 4,096-record, 256-boundary/view, 64-operation/boundary, 64-witness and
20-second cumulative limits remain; no input reread, reader reopen, new witness
selection, extra evaluation, new runtime capture or larger budget is introduced.

## Observed result and remaining gap

The genuine two-process FakeNKN fixture produces:

- One original distinct unresolved candidate, admitted as
  `recorded_stale_route_rejection`, with the original 24 witness origins.
- `recorded_rejections=pass` and `candidate_coverage=pass` for that candidate.
- Overall shadow verdict **unproven**, not pass, recovered or waived.
- Original `missing_exact_tuna_receive` / `Recovered=false`, with exactly three
  original evaluations on both PowerShell 5.1 and 7.

Six independent gates remain explicitly unproven: canonical scenario scope,
full route cycle, first-Tuna payload, second-Tuna payload, terminal integrity,
and absence of other unresolved failures. The current caller has no owner-bound
whole-scenario gate handoff. It must not fill that gap from aggregate verdicts,
fixture switches, copied JSON, route labels or a final SHA flag.

The fixture itself lacks first-activation payload evidence, an exact second-Tuna
receipt and final whole-transfer completion/integrity. It cannot validate the
alternative's all-gates-positive case. Existing recorded security spans still
do not prove execution-time security lifetime or runtime retirement, and no
matching dispatch observation does not prove absence of unlogged effects.
All three stronger-claim flags remain false.

The detached decision table checks that every gate must be an exact known pass
before returning `would_pass`; missing, unknown, malformed and incomplete gates
remain unproven, and a failed required gate produces `would_fail`. Its fabricated
all-pass control is logic coverage only, not a genuine scenario positive or an
acceptance capability. The integrated caller cannot currently produce that pass.

## Verification

Five accepted focused test cases, zero failures in their final outcomes:

| Receipt under `artifacts/alternative-acceptance-shadow-20260925/results/` | Accepted evidence |
| --- | --- |
| `controls-final.trx` | Both PowerShell hosts pass the detached decision/admission controls. |
| `owned-shadow-final.trx` | Genuine admission and omitted-security cases pass; each covers both hosts. |
| `owned-shadow-integration.trx` | The default-off `PublicationSecurityLifecycle_OriginalSpansPreserveStrictPrimary` case passes on both hosts. Its two initial shadow failures are superseded by the final receipt above. |

Final genuine admission capture:
`artifacts/publication-alternative-acceptance-shadow-20260925/66a40e4297204b88822f45254b0d20ea/`.
It retains 343 full records, 162 compact authority records, complete passing
authority views and full/compact parity. The omitted-security capture is
`artifacts/publication-alternative-acceptance-shadow-20260925/ee6620308d6b47639c1d65a47d93a832/`:
no candidate is admitted; its exact missing prerequisite is reported without
changing the preceding diagnostics or primary failure. The default-off capture
is `artifacts/publication-security-lifecycle-consumer-20260925/244be4ca0b05497dbae6b74ef170e8c6/`.

Development receipts are retained. The initial detached run exposed Boolean
coercion in a test expectation; gate input types and the expectation were made
explicit. The initial integration exposed the shadow's raw-versus-distinct count
mistake. A follow-up detached expectation was corrected to recognize the new
count-consistency guard. These were new test/shadow implementation defects,
not runtime faults, and are not advertised as intentional red regressions.

PowerShell parsing and `git diff --check` pass. Tests were rebuilt once, followed
by focused reruns of affected script paths. No aggregate suite, portable rebuild,
live GUI/NKN run, historical reclassification, commit or push was performed.

## Preservation and next boundary

Four pre-existing test/support files changed: the consumer test declarations,
primary observer, Service/yield probe, and lifecycle default-off assertion.
The other 70 pre-existing dirty-file contents are preserved. The previous
review seal's only changed pin is the intentionally extended consumer test file.
New files are the shadow helper, detached controls, two integration scripts,
the focused test class and this document. Verification hashes are in
`artifacts/alternative-acceptance-shadow-20260925/FINAL-VERIFICATION.json`.

Stop at the reviewed scope boundary: a complete scenario shadow needs a reviewed
owner-bound handoff for the independent gates and a genuine positive offline
fixture containing their evidence. That is the recommended next contract review,
not permission to add production capture, waive a missing gate or run live-256.
Acceptance adoption remains a separate approval after those proofs are validated.
