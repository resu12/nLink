# nLink Release Notes (Template)

State whether this is a draft/candidate or a published release. Replace placeholders with verified changes; do not invent a release date, performance claim, or qualification result. Maintain both `docs/releases/<version>.md` and its `-github.md` body consistently.

## What's new

- `<User-visible change and its supported scope>`
- `<Fix with verified behavior, not a list of diagnostic prerequisites>`

## How to use

1. Open `nLink` on both computers.
2. The helper clicks **I want to help** and copies the helper address.
3. The helpee clicks **I need help**, enters that address, and clicks **Request help**.
4. The helper accepts the incoming request.
5. Both sides compare the session verification symbols, then the helpee clicks **Allow**.

## File transfer / Tuna (when relevant)

- State single-file scope, explicit accept/decline, active-session pause/resume/cancel, file-size cap, and lack of resume after restart.
- Preserve regular NKN V4 / Tuna V4 / controlled same-transfer fallback V6 route invariants and coordinator authority. Do not describe active Tuna V6 as supported.
- Describe Tuna as default-off/experimental, distinguish wallet validation from paid runtime use, and explain payer/cap/accounting limitations.
- Distinguish application session-envelope protection from transport encryption. Do not imply listener readiness, a colored fish, or queued bytes prove authenticated delivery.

## Qualification and remaining limits

- `<Covered source/payload and dated correctness evidence: delivery, SHA, routes, authentication, fallback, terminal GUIs>`
- `<Separate performance verdict, warnings, contamination, and scope; no unsupported throughput guarantee>`
- `<Installer/upgrade/uninstall evidence and exact preservation scope>`
- `<Remaining sign-off or explicitly approved deferrals; do not relabel failures as passes>`

## Known issues

- `<Current demonstrated limitation and practical workaround, if any>`
- Link [KnownIssues.md](KnownIssues.md) for current support notes.

## Assets

- `nLink-Setup-win-x64-<version>.exe`
- `nLink-Portable-win-x64-<version>.zip`
- `SHA256SUMS.txt`

State actual signing status and any approved exception. Candidate asset names are not a published-download announcement.

## If something goes wrong

Open **Options -> Diagnostics** in the app and click **Copy diagnostics**.
Paste that text into the GitHub issue/report.
For hangs or freezes, click **Save Hang Report** and include the generated report when requested.
For screenshare issues, include the Diagnostics screenshare evidence block or the latest `screenshare-operator-verdict.txt` when available.
You can also click **Open logs folder** and attach the latest `nlink.log` files if needed.
Use [supportability.md](supportability.md) for the full evidence checklist. Fix relative links when copying this template into `docs/releases/`. Never request wallet files or secrets in a support report.

For a release-safe build, diagnostics should show:

- `invite_security_mode: issued_one_time_secret_invites`
- `invite_public_flow: verified_helper_required`
- `invite_security_release_ready: Yes`
- `invite_security_warning: none`
