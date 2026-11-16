# Accumulated file transfer checkpoint review

The risk-focused production review found no additional blocking issue after the
connected destination cancellation fix. The next step is to prepare the local
checkpoint, then build a fresh portable package and run the targeted route-cycle
scenario with strict preflights. This conclusion is not live route acceptance
or an exhaustive line-by-line approval of every accumulated fixture and report.

## State reviewed

The review used branch `v0.7.0` at
`a653d4063070235c71fe211480aeb37863d9b2a4`, with an empty index. All 702 existing
dirty paths matched the statuses and SHA256 values in
`artifacts/production-destination-connected-cancellation-20260930/FINAL-VERIFICATION.json`.
That seal's SHA256 is
`642fc229d7c508556bf118a9170450e50e34a5f666227247e9a4e45f930ee2cb`.
Its 20,311 retained file pins were not all rehashed during this review; the
702-path worktree comparison and seal check are the new preservation checks.
This report is the only additional worktree file from the review.

The review traced the production changes most exposed to ownership, lifetime,
and completion regressions:

- Windows guard-owned destination finalization and the retained read handle.
- Native sender selection, metadata opening, and current coordinator-owned
  runtime validation when opening and using a payload stream.
- Destination evidence, automatic app handoff, scenario adoption, cancellation,
  and cleanup outside ownership locks.
- GUI closed-writer binding, wrapper payload adoption, parent payload transfer,
  terminal completion, and optional primary-analysis binding.
- Fallback startup ownership checks and checkpoint response handling, plus the
  affected diagnostic propagation and offline observation hooks.

The earlier handoff review, cancellation fix report, and relevant production
verification reports supplied historical coverage. Reports and test counts were
not treated as substitutes for tracing the production calls. Not every changed
fixture or historical report was independently reviewed line by line.

## Findings and verification limits

The previously reported destination cleanup blocker is resolved by registering
closure of the exact pipe on the existing linked deadline token. The callback
does not acquire ownership locks or publish custody, progress, or route state.
The cancellation registration is disposed before subsequent candidate cleanup.

The GUI payload client also uses token-based pipe reads, but its authenticated
wrapper server already closes the exact pipe on its own ten-second deadline.
The same unbounded connected-read defect was therefore not established in that
path. No speculative timeout or cancellation change is recommended from this
inspection.

Optional diagnostic payload rejection does not replace the original completion
result in the reviewed wrapper and parent take paths. Diagnostic custody does
not itself authorize a route transition. Native sender access remains tied to
the original service/context and current coordinator ownership; ordinary
destination completion releases its retained read handle when diagnostic
custody is not taken.

The retained cancellation TRX files contain 18 passing connected-stall cases
and 17 passing compatibility cases. The separate before-fix reproduction
contains one expected failure. These are recorded results from the preceding
implementation, not tests rerun during this review. The tracked diff passes
`git diff --check`. No source or test behavior changed, so no aggregate test
lane was repeated.

The remaining end-to-end uncertainty is the real, fully assembled live path.
Offline fixtures establish their stated boundaries, not successful live NKN
bootstrap, a complete production GUI route cycle, or the second Tuna activation.

## Next validation gate

1. Prepare the intended local checkpoint from the preserved inventory. Do not
   include generated payloads, portable output, or ignored diagnostic artifacts,
   and verify the staged inventory before committing. No push is part of this
   step.
2. Build a fresh portable package into a new workspace-contained output location
   that cannot overwrite retained evidence. Establish clean source provenance;
   the acceptance checker still rejects `source_dirty`.
3. Run strict GUI and bridge readiness preflights outside the restricted sandbox,
   then only `regular-v4-live-activation-off-on-off-256mb`. Do not bypass a failed
   preflight or infer application failure from sandbox-blocked networking.

The targeted run still requires a second coordinator-published `file_tuna_v4`
route. SHA correctness or fallback completion alone cannot satisfy that
contract. A full GUI matrix remains a later gate after the targeted correctness
path passes or exposes a new first missing proof edge.

No commit, push, portable build, live network check, or GUI run was performed
during this review. Acceptance rules, runtime authority, and limits are unchanged.
