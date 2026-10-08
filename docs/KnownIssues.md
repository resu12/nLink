# Known Issues

This file lists current support notes for the `0.7.0` candidate; it is not a publication announcement. Use [supportability.md](supportability.md) for evidence collection and issue-reporting details.

## Current notes

- Some failure and recovery flows may still feel abrupt compared with a final polished support product.
- Options -> Diagnostics content is intended for troubleshooting and may change between builds.
- Performance and layout should be checked on the target machine if you use unusual scaling or window sizes.
- Screenshare streaming is available, but live NKN performance can vary with transport conditions. Include Options -> Diagnostics and any available `screenshare-operator-verdict.txt` evidence when reporting screenshare issues.
- File transfer supports single files only, with explicit receiver acceptance and a `25 GiB` default cap. Folders, drag-and-drop, and resume after app restart are not supported.
- Tuna remains experimental and default-off. It can incur provider costs when explicitly enabled and unlocked; local payment accounting is best-effort, not the wallet's lifetime on-chain total.
- The October 7 strict file-transfer GUI matrix passed correctness, but its separate performance verdict remained **FAIL**. Tuna and fallback throughput are not guaranteed. See [candidate evidence](ReleaseRunbook.md#070-candidate-evidence-2026-10-08) for scope; final CI sign-off remains pending.
- Progress smoothing cannot eliminate network ACK latency between peers. Final checksum/terminal completion matters more than identical instantaneous percentages; report sustained divergence or a stuck terminal card.
- Switching the paying side from helper to helpee during a transfer exposed a listener-retry/dialer-start collision. The startup guard passes focused regressions, but end-to-end recovery on the rebuilt payload is not yet verified; do not treat the earlier matrix as coverage of this handoff.

## How to report an issue

- Describe what you clicked and what you expected to happen.
- Include whether you were the helper, the person receiving help, or both.
- Paste `Options -> Diagnostics -> Copy diagnostics` output.
- For hangs or freezes, include the Save Hang Report output when available.
- For screenshare issues, include the screenshare evidence block from Options -> Diagnostics or the latest `screenshare-operator-verdict.txt` when available.
- For file/Tuna issues, include both transfer states, direction, size, and any manual switch/pause/cancel action times. Do not attach wallet files or secrets.
