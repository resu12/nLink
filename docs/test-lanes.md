# Test Lanes

`tools\Test-Lanes.ps1` is the stable entry point for local and CI test selection after the Track C project split.

## Common Commands

Run all domain ownership lanes:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane Core,Gui,ScreenShare,RemoteControl,Contracts
```

Run the release smoke lane:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane Smoke -Configuration Release
```

Run optional GUI smoke in an interactive Windows session:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane GuiSmoke -Configuration Release -GuiScenarios "A,B,C"
```

## Lane Matrix

| Lane | Target | Use |
|---|---|---|
| `Core` | `tests\nLink.SmokeTests.Core` | Core runtime, security, diagnostics, reliability, file transfer, bridge lifecycle. |
| `Gui` | `tests\nLink.SmokeTests.Gui` | View-model, shell, session UI, GUI support services. |
| `ScreenShare` | `tests\nLink.SmokeTests.ScreenShare` | Screen-share runtime, analyzers, receive path, H264, recovery, soak-adjacent tests. |
| `RemoteControl` | `tests\nLink.SmokeTests.RemoteControl` | Remote-control reducer, display mapping, input guards, transport priority tests. |
| `Contracts` | `tests\nLink.SmokeTests.Contracts` | Contract freeze and architecture guardrails. |
| `Smoke` | Sequential domain-project filter `Category=Smoke` | Release smoke compatibility lane across all domain projects. |
| `NonGui` | `Core`, `ScreenShare`, `RemoteControl`, and `Contracts` lanes | Beta readiness non-Gui domain gate. |
| `GuiSmoke` | Gui project filter `Category=GuiSmoke` | Interactive Windows GUI smoke; the script sets `NLINK_RUN_GUI_SMOKE=1`. |
| `ContractFreeze` | Contracts project filter `Category=ContractFreeze` | Contract approval/update lane. |
| `BridgeStabilityPromotion` | Core project filter `Category=BridgeStabilityPromotion` | Bridge stability promotion subset. |
| `TrackBRetained` | ScreenShare focused filter | Retained Track B analyzer/JSONL/receive-path safety slice. |
| `All` | All five domain projects | Full domain test sweep without relying on the old monolith project. |

The old performance category is not an active lane. Reintroduce it only with real performance tests and update this file, CI, and architecture guardrails together.

## Track C Closeout Guardrails

- Domain projects are the ownership boundary for normal test selection.
- `Area` traits are secondary filters and architecture guardrails, not a replacement for project ownership.
- `nLink.TestCommon` is harness-only and must not contain test methods or collection definitions.
- Adding a future test domain requires updating the solution, `tools\Test-Lanes.ps1`, this lane matrix, and `TestArchitectureContractTests` in the same change.
