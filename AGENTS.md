# Agent Instructions

## Live GUI And NKN Network Runs

- Do not treat restricted-sandbox failures as authoritative evidence for live NKN, GUI bridge bootstrap, or Phase 5 route-acceptance behavior.
- Keep deterministic code inspection and focused `dotnet test` runs in the normal sandbox when possible.
- Run `tools\Run-FileTransferRouteAcceptance.ps1`, GUI smoke scenarios, bridge readiness preflight, and live NKN connectivity checks outside the restricted sandbox with explicit escalation, because the sandbox can block outbound TCP and make bridge bootstrap fail falsely.
- If a sandboxed live-network check fails, confirm it outside the sandbox before writing an RCA or changing runtime code.
- Never bypass a failed Phase 5 preflight by running scenarios anyway; first decide whether the failure is a real app/setup failure or a sandbox/network isolation artifact.
