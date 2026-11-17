# Dispatch-observation exclusions: RCA and fix

2026-09-25. Branch `v0.7.0`, base HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.

## Finding

Review of the completed default-off dispatch query found a false-exclusion
path in `DGenericExcluded`. Any nonempty frame label different from the selected
v4 chunk-batch name was treated as an unrelated frame. Consequently `(none)`,
`(unknown)`, arbitrary text, unsupported names and wrong-case names could hide an
unbound generic dispatch observation. The raw-scope alternative also accepted
whitespace or unrecognized placeholder forms as a different hashed identity.

This is a test-only diagnostic defect, not evidence of a runtime dispatch,
authentication or delivery defect. It could incorrectly return
`no_matching_bound_dispatch_observed_in_capture` for malformed constructed
evidence. Classifier/recovery/acceptance remained disabled. The previous genuine
capture contained no generic dispatch in the interval; this review does not
reclassify it or claim that it exercised the defect.

The previous positive exclusion control used `filetransfer.feedback.v4`, which
is not a defined `FileTransferDataFrame` type. It therefore reinforced the loose
comparison instead of checking a known unrelated frame.

## Reproduction and bounded change

A read-only scalar probe reproduced the exclusion for `(none)`, `(unknown)`,
`garbage` and `filetransfer.feedback.v4` on PowerShell 5.1 and 7. The new end-to-end
structural regression then failed at
`dispatch_negative_admitted:generic_none_frame`, while the original yield,
reciprocal and rejection-chain matches remained intact. That intentional red
case is `artifacts/publication-dispatch-window-20260925/e119d6e5396e43dba1d063d207560af9/`.

The fix changes only the matcher and its detached controls:

- Frame-based exclusion requires an exact recognized data-frame name, different
  from the candidate. The bounded list of 24 names was checked against
  `src/nLink.Core/FileTransfer/FileTransferProtocol.cs`; no runtime or authority
  catalog is changed. Unknown/future types remain unproven unless another
  independently usable field clearly excludes the observation.
- Scope-based exclusion cannot use whitespace or bracketed/parenthesized
  placeholders. It still hashes an otherwise usable raw scope with the existing
  canonical projection; it does not borrow another record's identity.
- The unrelated-frame positive uses the real `filetransfer.state.v4` type.
  A separately different raw-scope positive remains covered.
- Nine added negatives cover missing/placeholder/invalid/unsupported/wrong-case
  frame names and blank/unknown/partially redacted scope values. All generic
  negatives require the exact `dispatch_unbound_generic_observation` reason,
  not merely any exception.

An intermediate fixed run completed all 10 positive/exclusion and 50 negative
controls, then failed the original custody/origin recheck with
`omission_control:dispatch_capture_custody:primary_origins`. This check also
enforces the cumulative work budget. The controls were repeatedly serializing
the complete capture, including large unchanged Snapshots, twice per case.
They now clone the raw string array once per case and serialize only the
dispatch-surface rows they construct, sharing that detached array between views.
The passing rerun retains the original custody and 20-second cumulative guards;
no budget or source-ownership check was relaxed. The intermediate failure is
retained, not counted as accepted verification.

## Verification

Accepted final case:
`artifacts/publication-dispatch-window-20260925/96add5b297e84b8ab268925523b75b79/`.
The focused `PublicationDispatchWindow_OriginalCapturePreservesStrictPrimary`
integration passes on both PowerShell hosts with 10 positive/exclusion controls
and 50 negatives. The fresh original query still reports no matching bound
dispatch in capture, full/compact agreement, unchanged inputs, 21 witnesses and
three original evaluations. `missing_exact_tuna_receive` remains unrecovered.

The scalar post-fix probe also passes on both hosts: empty, placeholder,
unsupported and wrong-case frame names do not alone exclude an observation;
the known unrelated state frame does. Parsing, protocol-list equality and
whitespace/diff checks pass.

Receipts are in `artifacts/publication-dispatch-exclusion-20260925/results/`:
`dispatch-exclusion-red.trx` is the intentional reproduction;
`dispatch-exclusion-green.trx` is the intermediate custody/work-guard failure;
only `dispatch-exclusion-final.trx` is the accepted final integration. No
unrelated/default-off lanes were rerun: the observer, prerequisite gates and
production code did not change. Their prior receipts remain historical evidence.

The final seal is
`artifacts/publication-dispatch-exclusion-20260925/FINAL-VERIFICATION.json`.
All 58 other pre-existing dirty files remain byte-for-byte unchanged. Of the
previous seal's 126 pins, only the two intentionally edited support scripts
changed. That prior seal itself is unchanged.

## Remaining boundary

This closes a fail-closed exclusion gap, not the broader stale-safety contract.
Captured no-match is still not runtime retirement, callback-effect attribution,
durable delivery or an acceptance exemption. No runtime changes, new authority
evaluation, portable rebuild, live GUI/NKN test, commit or push occurred.
