# nLink Pre-Release Notes (Template)

## What's new

- Improved connection stability for helper + helpee sessions
- Cleaner, simpler screens with less visual clutter
- Better diagnostics information for troubleshooting
- Small usability fixes (copy feedback, clearer status messages)

## How to use

1. Open `nLink` on both computers.
2. The helper clicks **I want to help someone** and copies the helper address.
3. The helpee clicks **I need help**, enters that helper address, confirms the verification code, and shares the helper-bound invite code.
4. The helper pastes the invite code, or scans the QR code, and clicks **Connect**.

## Known issues

- Connection can still be slow on some networks.
- If connection fails, retry with a refreshed invite code and confirm the helper address and verification code were entered correctly.
- This is a pre-release and may still have rough edges.

## If something goes wrong

Open **Diagnostics** in the app and click **Copy diagnostics**.
Paste that text into the GitHub issue/report.
For hangs or freezes, click **Save Hang Report** and include the generated report when requested.
For screenshare issues, include the Diagnostics screenshare evidence block or the latest `screenshare-operator-verdict.txt` when available.
You can also click **Open logs folder** and attach the latest `nlink.log` files if needed.
See `docs/supportability.md` for the full support evidence checklist.

For a release-safe build, diagnostics should show:
- `invite_security_mode: issued_one_time_secret_invites`
- `invite_public_flow: verified_helper_required`
- `invite_security_release_ready: Yes`
- `invite_security_warning: none`
