# Origin-capacity contract review — 2026-09-27

Status: review complete; proposal only, not implemented or approved. The
canonical fallback-to-primary integration remains blocked. No live test is due
at this boundary.

## Recommendation

Separate the aggregate reference bound from the source/per-output line bound:
allow at most **786,432 origin references across three roles**, at most
**262,144 per role**, and retain the **4 MiB native facts-body limit**. Keep the
existing flat Int32 origin format. Apply the same count contract in the
processor and native facts decoder, with explicit per-role enforcement.

This is a bounded resource-policy revision, not an acceptance relaxation. It
requires approval before implementation. It does not promise that every
combination of maximum counts fits the byte or elapsed-work bounds.

## Verified evidence

Baseline: HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`, branch `v0.7.0`,
377 existing dirty entries. All 1,462 pins in the preceding checkpoint matched:
`artifacts/payload-fallback-primary-20260927/FINAL-VERIFICATION.json`, SHA-256
`9c36d5cb0f74109a6575c74842b44bc15078aba4c82670936e9c7c0878c6c9e3`.

Read-only accounting of the final failed canonical run
`pwsh-aa77cfa82e0d455593f05bf4e5a9f4d8` used the unchanged parser and canonical
event catalog. It did not run the processor, reconstruct native custody, or
perform a coordinator/primary evaluation. The original source hash is
`95baece822f619c5bb77c863c4b4c20c049a61ccb996474136356b8e7007eacc`.

| Measurement | Full source projection |
| --- | ---: |
| Original bytes / physical lines | 92,090,758 / 101,878 |
| Bookmark | 2 |
| Retained / writer / semantic references | 101,876 / 101,878 / 73,734 |
| Total references | 277,488 |
| Excess over the current aggregate cap | 15,344 |
| Semantic text code units, excluding separators | 69,710,644 |

The first retained record equals original source line 3, and the partial writer
and retained counts differ by two. All source records have a positive writer
sequence and nonduplicated writer evidence fields; two writer IDs are present.
These accounting observations do not replace full writer/cursor validation.
The machine-readable projection is
`artifacts/origin-capacity-review-20260927/REFERENCE-COUNT-PROJECTION.json`.

## Contract findings

1. `GuiSmokeLogLedgerStreamingProcessor.cs:364,453` limits the sum of the three
   origin lists, not distinct physical lines. One line can legitimately appear
   once in each role. The processor already limits source records separately.
2. `LocalFrontierParentFactsValidation.cs:58` independently applies the same
   cumulative 262,144 cap. A processor-only change would move the failure to
   native decoding rather than establish handoff.
3. `GuiSmokeLogLedgerStreamingProcessor.cs:306` also uses the aggregate constant
   when checking the *single semantic output*. Raising that constant without
   separating the per-role bound would unintentionally loosen that check.
4. `LocalFrontierParentFacts.cs:69,92` writes Int64 physical byte offsets and
   Int32 references into a fixed 4 MiB buffer. The facts channel independently
   enforces that body length and rechecks its full bytes, padding, digest,
   binding, monotonic state and cancellation. None of those checks should change.
5. The primary consumes the validated semantic mapping with original-string
   reference checks. Its separate 262,144 input-record, 268,435,456 code-unit,
   2,097,152 producer-visit and 20-second work limits remain in force. More
   aggregate references do not authorize more primary records or evaluations.

## Capacity and alternatives

Let N be source lines and R/W/S the three origin counts. The array and count
portion of the unchanged facts body is `8*N + 4*(R+W+S) + 16` bytes. The observed
full source therefore needs **1,924,992 bytes** for this portion.

A conservative 1 MiB allowance covers valid non-array facts: up to 786,432
UTF-8 bytes under the existing 262,144-code-unit text cap, two manifest blobs
of at most 32,768 bytes each, the at-most-2,248-byte native packet, fewer than
5,000 bytes of text-length prefixes, and fewer than 20,000 bytes of remaining
numeric/count fields. This is a size bound for valid decoded field counts,
not an actual native serialization or an increase to any limit.

The observed arrays plus that allowance total **2,973,568 bytes**, below
4,194,304. Thus the existing byte envelope can accommodate this count profile;
actual original-custody serialization and all work gates must still be tested.

At the full count maximum, `8*262144 + 4*786432 = 5,242,880` bytes before other
metadata. Such a dense packet must still be rejected by the 4 MiB cap. The
proposed contract is the intersection of count, byte and work limits, not a
guarantee that all maxima are simultaneously admissible.

| Option | Assessment |
| --- | --- |
| Coordinated aggregate cap, flat format, unchanged 4 MiB cap | Recommended: smallest change that removes the observed count blocker; oversized native packets still fail closed. |
| Lossless per-role bitmap / compact format | Can reduce wire size, but needs a new codec/version, canonical unused-bit rules, count/population checks and compatibility tests. Compression alone cannot remove the current logical-count rejection. Defer until a byte-cap failure demonstrates a need. |
| Increase count and native packet byte limits together | Unnecessary for this source and broadens memory/cross-process budgets. Not recommended. |
| Trim events, move the bookmark, reuse another transfer's facts, or relax acceptance | Does not preserve this original proof chain. Rejected. |

The flat origin lists would contain at most 3 MiB of Int32 elements rather
than 1 MiB in aggregate. This is not a total-process memory bound: list growth,
source offsets, text, packet copies and decoded semantic arrays remain separate
bounded allocations. The capture uses read-only views of the original lists,
not a second copy of all three lists.

## Proposed implementation contract

- Define one aggregate reference limit of `3 * MaximumLines`; retain a separate
  per-role/source-line limit. Do not add a configurable knob or new framework.
- The processor must reject a per-role or aggregate overflow before appending
  the offending reference. Preserve its physical one-based mapping, visit order,
  source/line limits, original-owner checks and cancellation/work checks.
- Bind semantic-copy count validation to the per-role bound, not the larger
  aggregate bound. Preserve its original file, hash, length and output checks.
- Give the native facts parser the same aggregate bound. Check each role count
  against actual source lines and remaining aggregate capacity before allocation;
  require exact output-line counts, strictly increasing in-range origins, and
  the exact retained suffix `bookmark+1 .. N`. Keep all five output joins and
  source offset checks.
- Preserve the current fixed-size encoder, packet layout, complete consumption
  check, text bounds, exact native packet linkage, writer/recovery matches and
  channel checks. Never retry an oversized packet with a larger buffer, a new
  clock, truncated evidence or ordinary path-based custody.
- No wire layout/version change is needed for the proposed flat representation.
  Older decoders intentionally reject above-old-cap maps; they must not silently
  truncate or downgrade. This is fail-closed compatibility, not mixed-version
  interoperability. Validate with matching producer/consumer binaries.
- No primary algorithm, witness selection, 1–64 witness cap, coordinator
  evaluation count, receipt authority or route acceptance change is authorized.

The likely production edit surface is the stream processor, the facts-limit
declaration and the facts decoder. The serializer/channel/primary reader should
remain unchanged unless a separately demonstrated first edge requires a review.
Test fixtures may require updates for the newly admitted above-old-cap input.

## Verification and stop rules after approval

1. Add focused count tests at the old boundary and just above it, per-role
   maximum/one-over, aggregate maximum/one-over, zero/negative/mismatched counts,
   duplicate/regressing/out-of-range origins and retained-bookmark mismatch.
   Separate pure count/codec boundaries from elapsed-work integration tests;
   do not require all maximum dimensions to pass the full pipeline together.
2. Verify native facts round trips above 262,144 total references but below all
   other bounds. Reject a body one byte over 4 MiB, truncated counts/data,
   malformed tails and oversized declarations before allocating from them.
   Keep the legacy below-cap representation compatible and test old-policy
   refusal explicitly. Keep semantic-copy per-role overflow rejected.
3. Run directly affected source/semantic-copy/facts/primary-origin mutation and
   cancellation guards on both hosts. Reuse existing focused cases; do not rerun
   unrelated aggregate lanes. Preserve the original clocks throughout.
4. Update the cap-specific integration expectation only after the revised
   contract is approved. Retain new overflow refusal controls. Run one targeted
   full canonical fallback process tree, not the GUI matrix. Require genuine
   writer closure, native handoffs and unchanged primary evaluation ownership
   before claiming primary success; save any newly reached first failure.

The final failed run used 9,367 ms of its 10,000-ms GUI budget before consuming
the full source. The processor also has a separate 5-second bound. Passing the
new count gate may expose a work-budget failure. Do not pre-emptively increase
budgets, cache ownership checks or tune transfer behavior to avoid that result.

This review changed no implementation, tests, limits or acceptance rules and ran
no transfers or test suites. It preserves the eight prior focused passes and
the failed canonical-primary integration as distinct outcomes. No live run,
portable rebuild, commit or push was performed. Next action requires approval
of the bounded count-policy revision described above, then focused implementation.
