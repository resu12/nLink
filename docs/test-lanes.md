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

Run deterministic Core and file-transfer contract lanes:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane CoreContract,FileTransferContract,FileTransferAcceptanceFake -Configuration Release
```

Run all retained file-transfer analyzer contracts, or one shard while iterating:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane FileTransferAnalyzer -Configuration Release
powershell -ExecutionPolicy Bypass -File .\tools\Test-Lanes.ps1 -Lane FileTransferAnalyzerRuntimeUnlock -Configuration Release
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
| `CoreContract` | Core `Category=Smoke` compatibility subset excluding file-transfer contract, analyzer, and fake-acceptance ownership | Deterministic Core contract coverage. |
| `FileTransferContract` | Four isolated file-transfer contract sublanes | Complete coordinator/Core/Infra/fake-NKN contract sweep without shared-log interference. |
| `FileTransferCoordinatorContract` | `FileTransferCoordinatorTests` | Coordinator authority and state-machine contracts. |
| `FileTransferInfraContract` | `NknAccelerationTransportTests` | Infra acceleration, retry, settlement, and bridge contracts. |
| `FileTransferNknTransportContract` | `NknFileTransferTransportTests` | Fake-NKN file-transfer transport contracts. |
| `FileTransferCoreRuntimeContract` | Session file-transfer runtime and retry-authority classes | Core lock, bridge-event, and context synchronization contracts. |
| `FileTransferAnalyzer` | Five retained-analyzer shards | Complete deterministic file-transfer analyzer contract sweep. |
| `FileTransferAnalyzerAuthority` | `OpsSuite=RetainedAuthorityProof` | Retained authority and rejected-effect isolation analyzer shard. |
| `FileTransferAnalyzerRouteLaw` | `OpsSuite=RetainedRouteLaw` | Route-law analyzer shard. |
| `FileTransferAnalyzerFallback` | `OpsSuite=RetainedFallback` | Fallback analyzer shard. |
| `FileTransferAnalyzerRuntimeUnlock` | `OpsSuite=RetainedRuntimeUnlock` | Runtime-unlock and retry proof-chain analyzer shard. |
| `FileTransferAnalyzerV4Transport` | `OpsSuite=RetainedV4Transport` | V4 transport and efficiency analyzer shard. |
| `FileTransferAcceptanceFake` | File-transfer script, fake acceptance, provenance, and harness `OpsSuite` filters | Deterministic wrapper and artifact-contract coverage without GUI/network execution. |
| `Smoke` | Non-Core domain-project filter `Category=Smoke` | Release smoke compatibility lane; deterministic Core coverage is owned by explicit contract lanes. |
| `NonGui` | `Core`, `ScreenShare`, `RemoteControl`, and `Contracts` lanes | Beta readiness non-Gui domain gate. |
| `GuiSmoke` | Gui project filter `Category=GuiSmoke` | Interactive Windows GUI smoke; the script sets `NLINK_RUN_GUI_SMOKE=1`. |
| `ContractFreeze` | Contracts project filter `Category=ContractFreeze` | Contract approval/update lane. |
| `BridgeStabilityPromotion` | Core project filter `Category=BridgeStabilityPromotion` | Bridge stability promotion subset. |
| `TrackBRetained` | ScreenShare focused filter | Retained Track B analyzer/JSONL/receive-path safety slice. |
| `All` | All five domain projects | Full domain test sweep without relying on the old monolith project. |

The old performance category is not an active lane. Reintroduce it only with real performance tests and update this file, CI, and architecture guardrails together.

`Category=Smoke` remains on some Core methods as compatibility metadata, but it is not the scheduling authority. Use the explicit lanes above. Live file-transfer GUI/NKN validation remains scenario-based through `Run-FileTransferRouteAcceptance.ps1`; run only the targeted failing scenario after rebuilding the portable package. The complete Phase 5 live matrix is a pre-release gate, not part of deterministic CI.

## File-transfer audit disposition

- **Required:** coordinator, Core, Infra, fake-NKN transport, retained-analyzer, harness, provenance, and fake-acceptance contracts remain scheduled in named deterministic lanes.
- **Redundant:** no active test was removed solely for equivalent or slow coverage; no deletion candidate had sufficient proof of identical boundaries.
- **Obsolete:** ten already-skipped Infra tests for the pre-coordinator retry-dispatch grantor were removed because their authority path is unreachable and their stated replacement contracts are active.
- **Misclassified:** deterministic Core file-transfer tests are no longer scheduled through the compatibility `Smoke` lane; analyzer suites are independently selectable by proof domain.
- **Expensive but valid:** live GUI/NKN route scenarios remain narrowly targeted during remediation, while the complete Phase 5 matrix remains a pre-release gate.

For screenshare-specific validation and support capture decisions, use `docs/screenshare-operability.md` and `tools\ScreenShare-Ops.ps1` before reaching for live soak or retained Track B evidence directly.

## Track C Closeout Guardrails

- Domain projects are the ownership boundary for normal test selection.
- `Area` traits are secondary filters and architecture guardrails, not a replacement for project ownership.
- `nLink.TestCommon` is harness-only and must not contain test methods or collection definitions.
- Adding a future test domain requires updating the solution, `tools\Test-Lanes.ps1`, this lane matrix, and `TestArchitectureContractTests` in the same change.
