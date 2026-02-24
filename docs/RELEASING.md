# Releasing (Windows Alpha)

Follow this checklist for every Windows alpha release.

## 1. Bump version and push

1. Update `VERSION` (example: `0.1.0-alpha.2`).
2. Commit and push.

## 2. Run smoke tests

```powershell
dotnet test -c Release --filter Category=Smoke
```

## 3. Build release artifacts

Run in this order:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-BridgeBundle.ps1
powershell -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

## 4. Verify bridge is bundled

Check `bridge/win-x64` exists in outputs:

- `artifacts/portable/nLink/win-x64/bridge/win-x64`
- `artifacts/portable/helper/win-x64/bridge/win-x64`

## 5. Verify final release files

Confirm final files are in:

- `artifacts/releases/<version>/`

Expected files:

- `nLink-Portable-win-x64-<version>.zip`
- `nLink-Setup-win-x64-<version>.exe`
- `SHA256SUMS.txt` (if present)

## 6. Manual smoke test (two PCs)

1. Start `nLink` on both PCs.
2. Helpee: `I need help`.
3. Helper: `I want to help someone` -> enter code -> `Connect`.
4. Helpee: `Allow`.
5. Send chat messages both ways.

## 7. Draft GitHub Release (manual)

1. Create tag `v<version>`.
2. Mark as **pre-release**.
3. Upload:
   - installer EXE
   - portable ZIP
   - `SHA256SUMS.txt` (if present)

## 8. Do not commit build outputs

Do **not** commit `artifacts/` or generated binaries to git. Upload them as GitHub Release assets.
