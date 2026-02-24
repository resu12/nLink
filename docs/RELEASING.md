# Releasing (Windows Alpha)

Simple checklist for a Windows alpha release.

## 1. Build bridge bundle

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-BridgeBundle.ps1
```

## 2. Build portable ZIP

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Portable.ps1
```

Output:
- `artifacts/portable/nLink-Portable-win-x64-<version>.zip`

## 3. Build installer

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

Output:
- `artifacts/installer/nLink-Setup-win-x64-<version>.exe`

## 4. Verify bridge runtime is bundled

Check that `bridge/win-x64` exists in both outputs:

- Portable folder:
  - `artifacts/portable/nLink/win-x64/bridge/win-x64`
- Installer staging folder:
  - `artifacts/portable/helper/win-x64/bridge/win-x64`

## 5. Smoke test (manual, two PCs)

On two PCs:

1. Start `nLink` on both.
2. On helpee PC: click `I need help`.
3. On helper PC: click `I want to help someone`, enter the code, click `Connect`.
4. On helpee PC: click `Allow`.
5. Send chat messages both directions.

Expected:
- Connect succeeds
- `Allow` appears on helpee
- Chat works both ways

## 6. Create GitHub Release (manual)

Create a GitHub Release and upload these files as release assets:

- Installer EXE: `artifacts/installer/nLink-Setup-win-x64-<version>.exe`
- Portable ZIP: `artifacts/portable/nLink-Portable-win-x64-<version>.zip`

## 7. Do not commit build outputs

Do **not** commit `artifacts/` or generated binaries to git.

Upload the installer/ZIP as GitHub Release assets instead.
