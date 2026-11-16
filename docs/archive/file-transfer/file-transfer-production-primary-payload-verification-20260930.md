# Production primary payload verification

The existing production primary now has offline integration coverage from the
original exported parent custody through transfer binding and cleanup. Ten
distinct focused checks passed. No production code needed changing in this
phase. This verifies the handoff and rejection behavior, not a genuine fallback
witness or live Tuna route acceptance.

No routing, acceptance rule, goodput policy, timeout, portable package, live
preflight, GUI/NKN run, commit, or push changed in this phase.

## Starting state and scope

The predecessor is
`artifacts/production-parent-payload-handoff-20260930/FINAL-VERIFICATION.json`,
SHA256 `d31c60460a7846e5f05a847ac864d74cf8a09f67794d478038251b0f17c82441`.
All 695 dirty entries matched at the start. HEAD remains
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with no staged changes.

The primary transfer-pair implementation and post-report analyzer call already
existed. Earlier pair fixtures bound the transfer from a test probe before that
production diagnostic call. The new fixture instead exercises the production
call with the original parent export. It does not introduce another handoff or
an authority evaluation.

## Verified behavior

Six integration cases cover valid pairs on Windows PowerShell and PowerShell 7,
plus a foreign completion, an already-disposed pair, parent rejection, and an
unselected diagnostic gate on PowerShell 7. Four existing checks cover the
normal loader on both hosts, original-reference guards, and the evaluation
allowance without extending producer attachment.

The integration fixture uses the existing headless 256 MiB transfer and
test-augmented native loading. Original production GUI completion, wrapper,
parent, primary preparation, worker attachment, and analyzer binding calls
carry the pair. The fixture observes the first binding rather than performing
it itself. A subsequent binding attempt must be rejected.

For the selected diagnostic cases, the fixture substitutes only the diagnostic
failure class after the original reports have been written. The source,
candidates, proof, and evaluated views stay unchanged. The valid pair must be
current at the production binding boundary, with the expected byte count and
SHA-256, while all live-proof, integrity-authority, and publication flags remain
false. The controlled failure has no genuine witness and must be rejected.
That rejection retires the native inputs, so the transfer must no longer be
current afterward.

Rejected optional payloads preserve the ordinary analysis outcome. JSON summary
hashes stay unchanged across the diagnostic call; there are still exactly three
original evaluations and one native source read. Every integration case retains
the ordinary `proof_incomplete` receipt with unchanged analyzer input and no
completed proof artifacts. The unselected gate does not attempt binding.
Final cleanup releases the tracked native handles, and both payload files can
be reopened exclusively.

These tests do not establish a complete normal-loader GUI transfer, a genuine
fallback witness, or a second coordinator-published `file_tuna_v4` route.
Controlled diagnostic selection is not route evidence.

## Fixture corrections and verification

The new integration initially exposed three fixture issues. The preceding GUI
fixture needed the separate ordinary analyzer-input copy already used by the
component fixture; its hash, length, and line count are checked without weakening
native log protection. The first probe checked transfer currentness after
witness rejection had correctly closed the inputs; it now checks identity
before resolution and retirement afterward. Windows PowerShell could not resolve
the probe's `Get-FileHash` command; the probe now explicitly loads and reuses the
repository's existing SHA-256 helper.

Only one existing fixture changed, with its predecessor-matching before-image
retained. Four test files were added. The final six-case integration lane passed
after the shared hash correction; the four unaffected loader/guard checks were
not repeated. Earlier failed runs remain in the evidence directory.

The focused build completed with 339 warnings and zero errors. Build escalation
was limited to the existing Avalonia build-service requirement; all tests ran
offline in the normal sandbox. No aggregate suite or full GUI matrix ran.

The phase verifier checks all 18,843 predecessor pins, preserves unrelated dirty
work, verifies the fixture before-image, folds the latest test outcomes, and
checks recorded child termination and payload release. It also checks PowerShell
syntax, whitespace, unchanged Git identity, and an empty index before creating
the final verification seal. Exact audit counts are stored in that seal.

## Next checkpoint and live validation

Review the accumulated production handoffs and offline evidence as a local
checkpoint candidate. Then prepare the reviewed checkpoint, rebuild the
portable package, perform strict escalated preflights, and run the targeted live
scenario. Do not promote these offline checks to live acceptance or widen to
the full matrix before the targeted correctness path qualifies.

Acceptance still requires the second coordinator-published `file_tuna_v4`
route. Payload integrity or fallback completion alone remains insufficient.

Evidence and the final verification seal are under
`artifacts/production-primary-payload-verification-20260930`.
