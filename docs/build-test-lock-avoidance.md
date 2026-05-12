# Windows Build And Test Lock Avoidance

Windows can keep generated `.NET` files locked after live/manual tests because MSBuild nodes, Roslyn compiler servers, testhost processes, or the app under test may still be winding down. Prefer command shapes that minimize writes to `bin/` and `obj/`, especially during paid Tuna or other long opt-in runs.

## Default Pattern

For normal validation, run builds/tests serially and disable reusable build servers:

```powershell
dotnet build .\nLink.sln -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --verbosity minimal
dotnet build-server shutdown
```

Use `dotnet build-server shutdown` after long test batches, before deleting `bin/obj`, and before switching between sandboxed and elevated shells.

## Repeated Paid Or Manual Cells

When running repeated opt-in cells, build the test project once, then run each cell without rebuilding or restoring:

```powershell
dotnet build tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false

$env:NLINK_RUN_MANUAL_BRIDGE = "1"
$env:NLINK_RUN_TUNA_PHASE6_SHORT_MATRIX = "1"
$env:NLINK_TUNA_TEST_WALLET_PASSWORD = "<session-only test wallet password>"
$env:NLINK_TUNA_SOAK_CELL_FILTER = "<phase6-cell-id>"
dotnet test tests\nLink.OptInTests.BridgeManual\nLink.OptInTests.BridgeManual.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~TunaSidecarPhase6_ShortPaidMatrix"

dotnet build-server shutdown
```

This keeps MSBuild from touching `bin/` or `obj/` for every paid cell and avoids trusting stale binaries because the explicit build step happens first.

## If A Lock Still Happens

1. Stop leftover app/test processes from the same run.
2. Run `dotnet build-server shutdown`.
3. Re-run the command with `-m:1 -nr:false -p:UseSharedCompilation=false`.
4. Delete only generated repo-local `bin/` or `obj/` scratch directories when needed. Never delete Downloads or external user data folders.

`.git/index.lock` is different from generated-output locks. It usually means another Git process is active or the sandbox cannot write `.git`; in Codex, stage/commit commands may need the approved escalated Git path.
