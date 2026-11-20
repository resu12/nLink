# Known Issues

These notes distinguish [published 0.7.0 hotfix 2](https://github.com/resu12/nLink/releases/tag/v0.7.0-hotfix.2) from the [0.7.1 review build](ReleaseRunbook.md#071-local-review-build). Use [supportability.md](supportability.md) when reporting an issue.

## Release-specific notes

- The original 0.7.0 screen-sharing encoder startup hang is addressed by hotfix 1. This does not claim that every Media Foundation or transport failure is fixed.
- On builds before hotfix 2, wait for the full `nlinkh1.` Helper address before copying it. Hotfix 2 and the 0.7.1 candidate hide the short bootstrap value and disable sharing until ready.
- Hotfix 2 includes fixes for idle Tuna startup and re-establishment after completed or canceled transfers. A ready indicator alone is not proof that screen or file data used Tuna.
- In 0.7.1, [Beta Readiness case 10323 remains unpassed](ReleaseRunbook.md#071-beta-readiness-decision-2026-11-19). The release owner's risk acceptance applies only to this release.
- Hotfix 2 also contains wallet unlock/relink race and verifier-cancellation fixes, plus fixed-position Wallet feedback. Do not attribute these changes to hotfix 1.

## Current notes

- Some failure and recovery flows may still feel abrupt compared with a final polished support product.
- Options -> Diagnostics content is intended for troubleshooting and may change between builds.
- Performance and layout should be checked on the target machine if you use unusual scaling or window sizes.
- Screenshare streaming is available, but live NKN performance can vary with transport conditions. Include Options -> Diagnostics and any available `screenshare-operator-verdict.txt` evidence when reporting screenshare issues.
- File transfer supports one file at a time, requires receiver acceptance, and has a `25 GiB` default cap. The 0.7.1 candidate accepts one Explorer file dropped onto chat. Folder transfer, multi-file drops, and resume after restart remain unsupported.
- Tuna is off by default. It can incur provider costs when explicitly enabled and unlocked; local payment accounting is best-effort, not the wallet's lifetime on-chain total.
- Tuna and fallback throughput are not guaranteed. Historical GUI performance FAIL/WARN outcomes remain preserved and do not become passes because CI #102 passed. See the [release record and historical evidence](ReleaseRunbook.md#current-070-release-record-2026-11-15) for source/payload scope; CI #102 did not run the optional live GUI campaign.
- Progress smoothing cannot eliminate network ACK latency between peers. Final checksum/terminal completion matters more than identical instantaneous percentages; report sustained divergence or a stuck terminal card.
- Historical payer-handoff issue: the listener-retry/dialer-start collision was subsequently covered by the October 15 `cf8ea296` seven-scenario correctness gate. That dated result supersedes the earlier pending-check status only for its covered payload; it is not a fresh exact-binary GUI result for CI #102. See [historical qualification](ReleaseRunbook.md#current-qualification-update-2026-10-15).
- The original 0.7.0 and both hotfix releases have separate unsigned-artifact exceptions. Verify downloads against the `SHA256SUMS.txt` from the same tag; Windows may show an unrecognized-publisher warning. None of these exceptions covers 0.7.1.

## How to report an issue

- Describe what you clicked and what you expected to happen.
- Include whether you were the helper, the person receiving help, or both.
- Paste `Options -> Diagnostics -> Copy diagnostics` output.
- For hangs or freezes, include the Save Hang Report output when available.
- For screenshare issues, include the screenshare evidence block from Options -> Diagnostics or the latest `screenshare-operator-verdict.txt` when available.
- For file/Tuna issues, include both transfer states, direction, size, and any manual switch/pause/cancel action times. Do not attach wallet files or secrets.
