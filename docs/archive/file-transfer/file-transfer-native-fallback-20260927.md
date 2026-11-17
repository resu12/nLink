# Retained 256 MiB source over the cross-process fallback carrier — 2026-09-27

Status: focused verification complete. No live run or route-cycle acceptance.

Baseline: `v0.7.0`, HEAD `a653d4063070235c71fe211480aeb37863d9b2a4`,
375 dirty entries. All 1,009 pins in the preceding cross-process carrier seal
matched before work. Its SHA-256 is
`191f1a319d8aa28046ab9556791fa32016574fe08de159cf721d5364178d7c98`.

## Bounded integration

The existing carrier gains a dedicated full-size case using the original
GUI-held source delivered to the original helpee over its native stdin channel.
The helper waits before accepting until the sender binds the existing
`RetainedRuntimeReadProbe` to its actual app-owned service/context. This is
fixture scheduling only; neither the event nor the bind grants read authority.

The unchanged retained reader factory supplies exactly metadata, Regular v4,
Tuna v4 and fallback v6 readers. Each read still requires its captured current
coordinator owner/publication and exact pending-or-installed payload runtime.
It reopens only the held native handle, not the source pathname. Existing
copy, overlap, replay and legacy-path exclusion checks run on these real opens.
Prior readers must be retired at each successor; all readers must be unusable
after completion while the source anchor remains held.

Production Core/NKN services run in separate original app processes over the
previously verified offline pipe carrier. Each coordinator independently proves
Regular v4, Tuna v4 and correlated fallback v6. Payload reads remain gated until
both sides have published fallback. The helper writes through its runtime's
guard-owned native destination, retains evidence during rename/finalization,
rejects premature/repeated reads and hashes the actual held final destination.
Both endpoints must finish with the same bytes, SHA-256, session and transfer
identifiers; the parent additionally checks their hash against the original
GUI source measurement. No generated 64 KiB buffer substitutes for this source.

The negative case disposes only the helpee's retained source at the fourth open.
It must reject the fallback reader, deliver `read_failed` to both services with
zero payload bytes, never reach destination rename, and leave all old readers
retired. The GUI's separate original source custody remains protected.

## First run and completion deadline

The first full-size run reached both valid fallback publications and continued
committing data until the existing 70-second app-fixture deadline canceled it.
The last emitted committed total was 261,574,656 of 268,435,456 bytes (97.44%).
That is an incomplete result, not a completion or acceptance pass.

Only this newly introduced full-size case now uses the existing offline
full-file correctness allowance of 180 seconds. Its test GUI waits at most
185 seconds, and its actual parent/wrapper projection carries 210 seconds.
The fixture checks that same actual timeout in the GUI instead of pretending
the old 100-second projection is still in use. Existing small/legacy cases keep
70/75/100 seconds; route-edge checks keep eight seconds. Production timeouts,
progress policy, chunking, retries, native analysis/custody work budgets and
route acceptance are unchanged. The completion allowance is not a performance
claim or a reason to relax route proof.

## Verification

Six distinct final tests passed, with zero remaining failures or skipped cases:

- `full-completion-allowance.trx`: original native 256 MiB source to the helper's
  native destination through fallback, using PowerShell 7.
- `full-focused-compatibility.trx`: the same full-size case using Windows
  PowerShell; source-retirement rejection; the previous small carrier case;
  and original launch-projection success/tampered-argv rejection controls.

Both completed native destinations contain 268,435,456 bytes with SHA-256
`3a89beeb39ed326161a0cce72dfb5bf64120888a0162d2e76c1bf7b541145699`,
matching the separately retained GUI source measurement. Each run also matches
the original sender/receiver session and transfer identifiers. Positive runs:

- `pwsh-valid-dfecf7895f494211be21d41a5638a39b`
- `powershell-valid-4d565d3e1b2d4829af099e572fa23c59`

Artifacts and TRX files are retained under
`artifacts/payload-native-fallback-20260927`. The original 70-second timeout
failure is preserved in `full-first.trx`; it is not relabeled as a pass.
The source-retirement case reports `read_failed` on both original services,
zero transferred bytes, no destination rename/finalized path and no usable
retired reader. The GUI-held original source remains protected.

All three changed PowerShell scripts parse with zero errors and scoped
whitespace checks pass. Of the 375 baseline dirty entries, 370 remain
byte-identical and five have scoped test/fixture edits. This report is the sole
new entry, making 376 total. No production code or unrelated work changed.
The final evidence/preservation seal is
`artifacts/payload-native-fallback-20260927/FINAL-VERIFICATION.json`.

## Remaining boundary

This step verifies file transfer and helper-local native destination evidence.
It does not yet hand that destination into the GUI's original source/destination
pair and onward through the original primary. That custody join, followed by
a genuine same-transfer lifecycle receipt/marker eligible for primary selection,
is next. Do not transplant another transfer's witness or synthesize a failure.
The second Tuna publication required by the live route-cycle contract remains
unproven. No live dispatch, portable rebuild, commit or push is performed.
