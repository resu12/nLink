# Known Issues

This file lists support notes for the published [0.7.0 hotfix revision 1](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.1) and distinguishes the unreleased [hotfix-2 candidate](releases/0.7.0-hotfix.2.md). Use [supportability.md](supportability.md) for evidence collection and issue-reporting details.

## Release-specific notes

- The original 0.7.0 screen-sharing encoder startup hang is addressed by hotfix 1. This does not claim that every Media Foundation or transport failure is fixed.
- In published builds, the Helper view can briefly show a short bootstrap address before the full `nlinkh1.` address. Wait for the full address before copying it. The hotfix-2 candidate hides the short value and disables sharing until ready.
- Idle Tuna startup and re-establishment after completed/canceled transfers have candidate fixes in hotfix 2. These are not yet a published or live-qualified fix. A Tuna connection indicator alone is not proof that a particular screen or file payload used Tuna.
- Hotfix 2 also contains wallet unlock/relink race and verifier-cancellation fixes, plus fixed-position Wallet feedback. Do not attribute these changes to hotfix 1.

## Current notes

- Some failure and recovery flows may still feel abrupt compared with a final polished support product.
- Options -> Diagnostics content is intended for troubleshooting and may change between builds.
- Performance and layout should be checked on the target machine if you use unusual scaling or window sizes.
- Screenshare streaming is available, but live NKN performance can vary with transport conditions. Include Options -> Diagnostics and any available `screenshare-operator-verdict.txt` evidence when reporting screenshare issues.
- File transfer supports single files only, with explicit receiver acceptance and a `25 GiB` default cap. Folders, drag-and-drop, and resume after app restart are not supported.
- Tuna is off by default. It can incur provider costs when explicitly enabled and unlocked; local payment accounting is best-effort, not the wallet's lifetime on-chain total.
- Tuna and fallback throughput are not guaranteed. Historical GUI performance FAIL/WARN outcomes remain preserved and do not become passes because CI #102 passed. See the [release record and historical evidence](ReleaseRunbook.md#current-070-release-record-2026-11-15) for source/payload scope; CI #102 did not run the optional live GUI campaign.
- Progress smoothing cannot eliminate network ACK latency between peers. Final checksum/terminal completion matters more than identical instantaneous percentages; report sustained divergence or a stuck terminal card.
- Historical payer-handoff issue: the listener-retry/dialer-start collision was subsequently covered by the October 15 `cf8ea296` seven-scenario correctness gate. That dated result supersedes the earlier pending-check status only for its covered payload; it is not a fresh exact-binary GUI result for CI #102. See [historical qualification](ReleaseRunbook.md#current-qualification-update-2026-10-15).
- The original 0.7.0 and hotfix-1 Windows artifacts are unsigned under their explicit release exceptions. Verify downloads against the `SHA256SUMS.txt` from the same tag; Windows may show an unrecognized-publisher warning. Future hotfix bytes need a separate signing disposition.

## How to report an issue

- Describe what you clicked and what you expected to happen.
- Include whether you were the helper, the person receiving help, or both.
- Paste `Options -> Diagnostics -> Copy diagnostics` output.
- For hangs or freezes, include the Save Hang Report output when available.
- For screenshare issues, include the screenshare evidence block from Options -> Diagnostics or the latest `screenshare-operator-verdict.txt` when available.
- For file/Tuna issues, include both transfer states, direction, size, and any manual switch/pause/cancel action times. Do not attach wallet files or secrets.
