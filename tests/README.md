# Tests

The default release baseline is the main solution:

```powershell
dotnet test -c Release --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal
```

Use project-level runs when diagnosing a focused change:

| Project | Lane | Protects | Typical command |
| --- | --- | --- | --- |
| `nLink.SmokeTests.Contracts` | Contracts | public contracts, golden snapshots, test architecture | `dotnet test tests\nLink.SmokeTests.Contracts\nLink.SmokeTests.Contracts.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal` |
| `nLink.SmokeTests.Core` | Core | runtime lifecycle, authorization, file transfer, diagnostics, bridge policy | `dotnet test tests\nLink.SmokeTests.Core\nLink.SmokeTests.Core.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal` |
| `nLink.SmokeTests.Gui` | GUI | Avalonia view-models, command bindings, headless UI behavior | `dotnet test tests\nLink.SmokeTests.Gui\nLink.SmokeTests.Gui.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal` |
| `nLink.SmokeTests.ScreenShare` | ScreenShare | capture, encode/decode, transport pressure, recovery | `dotnet test tests\nLink.SmokeTests.ScreenShare\nLink.SmokeTests.ScreenShare.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal` |
| `nLink.SmokeTests.RemoteControl` | RemoteControl | input guard, reducer, transport priority, display mapping | `dotnet test tests\nLink.SmokeTests.RemoteControl\nLink.SmokeTests.RemoteControl.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --verbosity minimal` |

The latest Phase 4 release baseline was:

- Contracts: 35 passed
- Core: 667 passed
- GUI: 252 passed
- ScreenShare: 573 passed
- RemoteControl: 87 passed

Treat new failures in the default projects as release-blocking unless the test is proven flaky and passes in an isolated rerun with diagnostic evidence.

## Opt-in projects

Opt-in tests live in separate projects so the default baseline does not report expected skips:

| Project | Purpose | Gate |
| --- | --- | --- |
| `nLink.OptInTests.BridgeManual` | real bridge lifecycle/manual restart diagnostics | `NLINK_RUN_MANUAL_BRIDGE=1` |
| `nLink.OptInTests.GuiSmoke` | Windows GUI smoke harness | `NLINK_RUN_GUI_SMOKE=1` |
| `nLink.OptInTests.MediaFoundationDiagnostics` | Windows Media Foundation/H.264 diagnostics | `NLINK_RUN_MF_DIAGNOSTIC=1` |

Run an opt-in project explicitly, for example:

```powershell
dotnet test tests\nLink.OptInTests.GuiSmoke\nLink.OptInTests.GuiSmoke.csproj -c Release
```

Do not move opt-in projects back into the default solution baseline unless they are deterministic, non-interactive, and environment-independent.

## Diagnostic expectations

- Soak-style tests should write enough failure context to identify mode, artifact directory, stdout, stderr, and the relevant summary/log tail.
- Avalonia headless tests should find controls by automation id where possible and assert visibility/enabled state before clicking.
- Redaction and diagnostics tests should keep asserting that copied diagnostics omit secrets, raw session identifiers, raw peer identities, and raw local paths.
