# Known Issues

These notes apply to [published nLink 0.7.1](https://github.com/resu12/nLink/releases/tag/v0.7.1). Use the [support guide](supportability.md) when reporting an issue.

## Release-specific notes

- Live Tuna payload delivery and controlled fallback were not verified on the released portable. The 64 MiB regular-NKN test passed correctness with a transport warning but failed its separate performance gate. See the [validation record](ReleaseRunbook.md#published-071-2026-11-22).
- [Beta Readiness case 10323 remains unpassed](ReleaseRunbook.md#071-beta-readiness-decision-2026-11-19). The release owner's risk acceptance applies only to 0.7.1.
- Windows may warn that the publisher is unknown because the 0.7.1 artifacts are unsigned. Verify downloads against `SHA256SUMS.txt` from the same release.

## Current notes

- Screen sharing and file transfer speeds depend on network conditions. A preview on the Helpee does not show that the Helper is receiving a changing image.
- Each offer contains one file, requires receiver approval, and has a default cap of `25 GiB`. One unfinished file in each direction is supported; folders, multi-file drops, and resume after restart are not.
- Tuna is off by default. Starting a paid listener can incur provider costs; local payment accounting is best-effort. **Tuna is ready** does not establish a particular file's route or throughput.
- The two transfer progress bars can differ briefly. Report a sustained discrepancy or a transfer that never completes.

## How to report an issue

- Describe what you clicked and what you expected to happen.
- Include whether you were the helper, the person receiving help, or both.
- Under **Options → Diagnostics**, refresh **Session snapshot**, then copy and review the diagnostics before sharing.
- For hangs or freezes, include the Save Hang Report output when available.
- For screenshare issues, include the screenshare evidence block from Options -> Diagnostics or the latest `screenshare-operator-verdict.txt` when available.
- For file/Tuna issues, include both transfer states, direction, size, and any manual switch/pause/cancel action times. Do not attach wallet files or secrets.
