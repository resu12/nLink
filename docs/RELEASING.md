# Release Checklist

1. Update `VERSION` file.
2. Commit and push.
3. Run smoke tests:
   `dotnet test -c Release --filter Category=Smoke`
4. Build:
   - `installer/Build-BridgeBundle.ps1`
   - `installer/Build-Portable.ps1`
   - `installer/Build-Installer.ps1`
5. Verify bridge bundled under `bridge/win-x64`.
6. Draft GitHub pre-release:
   - Tag: `v<version>`
   - Mark as pre-release
   - Upload installer + portable zip.
