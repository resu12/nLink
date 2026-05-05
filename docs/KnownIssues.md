# Known Issues

This file lists current support notes for the active release. Use `docs/supportability.md` for evidence collection and issue-reporting details.

## Current notes

- Some failure and recovery flows may still feel abrupt compared with a final polished support product.
- Options -> Diagnostics content is intended for troubleshooting and may change between builds.
- Performance and layout should be checked on the target machine if you use unusual scaling or window sizes.
- Screenshare streaming is available, but live NKN performance can vary with transport conditions. Include Options -> Diagnostics and any available `screenshare-operator-verdict.txt` evidence when reporting screenshare issues.

## How to report an issue

- Describe what you clicked and what you expected to happen.
- Include whether you were the helper, the person receiving help, or both.
- Paste `Options -> Diagnostics -> Copy diagnostics` output.
- For hangs or freezes, include the Save Hang Report output when available.
- For screenshare issues, include the screenshare evidence block from Options -> Diagnostics or the latest `screenshare-operator-verdict.txt` when available.
