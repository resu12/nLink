# Release Checklist

1. Update `VERSION` file.
2. Commit and push.
3. Run pre-release check (recommended):
   - `powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1`
4. Optional GUI smoke (interactive Windows session):
   - `powershell -ExecutionPolicy Bypass -File .\tools\PreRelease-Check.ps1 -RunGuiSmoke`
5. Optional beta hardening extras (installer/offline/permissions):
   - See `docs/BETA_HARDENING_EXTRAS.md`
6. Run smoke tests (if not using the pre-release check script):
   `dotnet test -c Release --filter Category=Smoke`
7. Build (if not using the pre-release check script):
   - `installer/Build-BridgeBundle.ps1`
   - `installer/Build-Portable.ps1`
   - `installer/Build-Installer.ps1`
8. Verify bridge bundled under `bridge/win-x64`.
9. Prepare release notes from `docs/RELEASE_NOTES_TEMPLATE.md` (include Diagnostics + Open logs folder guidance).
10. Draft GitHub pre-release:
   - Tag: `v<version>`
   - Mark as pre-release
   - Upload installer + portable zip.
