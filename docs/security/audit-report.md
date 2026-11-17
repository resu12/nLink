# Security Audit Report

## Post-Handshake Security Contract

This section defines the intended security boundary for session traffic after the invite and handshake complete.

### Transport security vs app-layer security

- `Transport security` means confidentiality and integrity provided by the underlying transport implementation.
- `App-layer security` means confidentiality, integrity, sender/session binding, and replay resistance enforced by nLink message processing itself.

These are separate guarantees. nLink must not claim session-wide end-to-end protection unless the application layer protects the relevant message family.

### Required post-handshake guarantees

For public-release security claims, the following message families must be treated as `session-key authenticated` application traffic:

- chat
- remote control
- screen share
- file transfer
- session lifecycle messages such as approve, reject, and session end

At minimum, authenticated post-handshake messages must bind:

- message family or type
- session id
- sender role or peer identity
- replay/ordering metadata appropriate to that message family

### Current repository status

Based on the current code:

- handshake challenge/response has application-layer MAC verification
- chat has application-layer encryption and authentication
- remote control uses the shared post-handshake session-key secure envelope
- screen share uses the shared post-handshake session-key secure envelope
- file transfer uses the post-handshake session envelope, transfer/session validation, explicit accept/decline, and negotiated bulk-source binding for V4 data frames
- lifecycle messages (`approve`, `reject`, and `session_end`) use the shared post-handshake session-key secure envelope

Because of that, the current repository can support this application-layer claim:

- `chat, remote control, screen share, file transfer, and session lifecycle traffic are protected by nLink after session approval`

### NKN transport assumptions

The NKN path still relies on transport and local-runtime assumptions that are distinct from nLink's own message crypto:

- the bundled local bridge process is trusted to report the authoritative connected NKN address after `ready`
- the bundled local bridge process is trusted to forward transport traffic correctly; nLink file-transfer bulk data now validates against the negotiated remote bulk endpoint rather than accepting bridge-reported source identity as authoritative
- the bundled local bridge process enforces payload-size limits before dispatch into nLink
- the underlying NKN transport is still a separate security boundary from the nLink application envelope

The current split is therefore:

- `nLink app-layer security`: post-handshake session-key protection for chat, control, screen share, file transfer, and lifecycle traffic
- `transport/runtime assumptions`: bridge correctness, reported source identity fidelity, and transport delivery behavior

### Release rule

Release/security docs must distinguish:

- what the underlying transport and bundled bridge may protect or guarantee
- what nLink itself protects cryptographically

No release doc should imply that transport encryption alone is equivalent to nLink-managed end-to-end protection, and no release doc should omit the remaining trust placed in the local bundled bridge/runtime.

### Release hardening limit matrix

Current transport-local abuse bounds:

- `NknSignalingTransport` high-priority control queue: `256`
- `NknSignalingTransport` low-priority control queue: `256`
- `NknSignalingTransport` file-transfer data-session queue: `512` frames and `32 MiB` estimated queued bytes per active data session
- file-transfer queue overflow policy: fail closed with `ReceiverBufferExhausted`, log `filetransfer_data_session_overflow`, remove the active data-session registration, and require resume/reopen
- file-transfer V4 bulk data source binding: expected sender/source comes from the negotiated remote bulk endpoint, not the bridge-reported inbound `source`
- high-priority overflow policy:
  - `ControlStop` may evict queued non-stop work
  - `ControlDisplayInfo` and `ControlStateSnapshot` may coalesce when full
  - other non-stop high-priority control messages are rejected at capacity
- screen-share outbound gate wait budget: `25 ms`
- replay windows are bounded separately for control, lifecycle, and screen-share traffic

Hard payload-size enforcement is split across lower layers and must be reviewed together with the transport:

- bridge binary framing limits: `64 KiB` payload cap, `65,535` primary text bytes, `65,535` secondary text bytes, and `196,606` body bytes before allocation
- secure-envelope validation limits
- screen-share payload and chunk budgets

Release review must treat those queue limits and lower-layer payload limits as one abuse-resistance contract, not as isolated guarantees.

## Dependency Risk

### Scope reviewed

- .NET project manifests in:
  - `Directory.Build.props`
  - `src/*.csproj`
  - `tests/*.csproj`
- Node bridge manifests in:
  - `tools/nkn-bridge/package.json`
  - `tools/nkn-bridge/package-lock.json`
- Bridge bundling and package manifests in:
  - `installer/Build-BridgeBundle.ps1`
  - `installer/package-manifest.win-x64.txt`
  - `build/verify-package-manifest.ps1`

### Findings

#### High: Bridge dependency stack still includes deprecated legacy packages through `nkn-sdk`

The bundled NKN bridge remains pinned to `nkn-sdk` `1.3.6`, and the committed lockfile shows deprecated legacy transitive packages:

- `@babel/polyfill` `7.12.1` is explicitly marked deprecated
- `@babel/polyfill` pulls `core-js` `2.6.12`, which is explicitly marked deprecated and no longer maintained

Evidence:

- `tools/nkn-bridge/package.json`
- `tools/nkn-bridge/package-lock.json`

Why it matters:

- deprecated packages are a supply-chain maintenance warning
- `core-js@2` in particular is an old compatibility layer that should not remain buried inside a release-critical bridge stack indefinitely
- the bridge is part of the trusted transport/runtime boundary, so stale JS dependencies matter more than a normal build-only tool dependency

Recommended action:

- update or replace the bridge dependency chain so the shipped bridge no longer relies on `@babel/polyfill` and `core-js@2`
- if `nkn-sdk` is the blocker, track that as an explicit third-party dependency risk in release notes/checklists until removed

#### Resolved: Bridge build no longer commits or ships a `node_modules` dependency tree

The bridge dependency-tree packaging issue is corrected in the current release state:

- `tools/nkn-bridge/node_modules` is ignored and not tracked
- `tools/nkn-bridge/.nlink-bundle` is ignored and not tracked
- the tracked portable Node runtime under `tools/node` is removed from the release source tree
- `installer/Build-BridgeBundle.ps1` requires `tools/nkn-bridge/package-lock.json`
- bridge bundling restores with clean `npm ci --ignore-scripts --no-audit --no-fund`
- the pinned Node `24.13.1` archive is SHA-256 verified before extraction into ignored `artifacts/toolcache/node/<rid>/`
- shipped bridge artifacts include `node.exe`, bundled `index.js`, `package.json`, `package-lock.json`, `bridge-manifest.json`, and `bridge-dependencies.json`
- `bridge-manifest.json` and dependency evidence record `nodeModulesShipped=false`

Remaining risk:

- the bundled `ncc` output still contains the runtime code pulled from `nkn-sdk` `1.3.6`
- deprecated transitive packages remain an accepted third-party bridge risk until the SDK is replaced or upgraded

#### Medium: .NET SDK version is not pinned in-repo

There is no `global.json` in the repository.

Why it matters:

- builds depend on whatever compatible .NET SDK is installed on the build machine
- that weakens reproducibility for release builds and security review

Recommended action:

- add a `global.json` that pins the expected .NET 8 SDK family for development and release builds

#### Medium: Production app path depends on a preview Windows SDK package

The app references `Microsoft.Windows.SDK.NET` `10.0.18362.6-preview` in the Windows build path.

Evidence:

- `src/nLink.App/nLink.App.csproj`

Why it matters:

- preview packages are a weaker release posture than stable packages
- even if this package is only used for Windows-specific functionality, it is still on the production app path

Recommended action:

- move to a stable supported Windows SDK package if possible
- if not possible, document why the preview package remains required and treat it as an explicit release exception

#### Medium: Bridge runtime is bundled separately and must be tracked like a dependency

The release bridge bundle includes a pinned Node runtime that is bootstrapped into the ignored tool cache and copied into the packaged bridge output.

Why it matters:

- this is part of the shipped attack surface
- it needs explicit update cadence and release review, not just packaging convenience
- the build must keep proving the Node archive hash and shipped manifest metadata

Recommended action:

- keep the bundled Node runtime version and archive SHA-256 in the release checklist
- treat runtime updates as dependency maintenance, not only build tooling maintenance

#### Low: .NET dependency surface is comparatively small and pinned

The .NET side uses exact package versions in project files and has a relatively small direct dependency surface:

- Avalonia `11.3.12`
- CommunityToolkit.Mvvm `8.2.1`
- QRCoder `1.6.0`
- ZXing.Net `0.16.10`
- `System.Security.Cryptography.ProtectedData` `9.0.0`
- test packages pinned explicitly

Why it matters:

- this is the stronger part of the dependency posture
- the primary dependency-risk pressure is on the Node bridge stack, not the .NET app manifests

### Direct answer

Based on repository contents only:

- the main dependency risk is now the NKN bridge SDK stack itself, especially deprecated transitive JS packages pulled through `nkn-sdk` `1.3.6`
- the .NET dependency surface is smaller and pinned, but release reproducibility is weakened by the lack of `global.json`
- the bundled Node runtime must be tracked as a shipped dependency, not just a build helper, but the source tree no longer vendors that runtime

I am not claiming specific known CVEs from repository contents alone. That would require an external vulnerability feed or package audit outside the repo itself.

### Actual bridge/runtime dependency audit

The current bridge supply-chain contract is:

- bundled Node runtime: `v24.13.1`, pinned by SHA-256
- locked bridge restore: `npm ci --ignore-scripts --no-audit --no-fund`
- dependency evidence: `bridge-dependencies.json`
- manifest evidence: `nodeModulesShipped=false`
- optional advisory evidence: `npm audit --omit=dev` may be recorded when network access is available, but online advisory lookup is not required for local/offline builds

This improves the dependency picture in one important way:

- releases are no longer reviewed against a checked-in or shipped `node_modules` tree

## Current Status

### Areas that now look materially stronger in the current repository

- invite and join flow is helper-bound on the release-default path
- invite replay protection is present
- post-handshake chat, remote control, screen share, and lifecycle traffic are application-layer protected
- execute-path authorization and consent enforcement are present in runtime handlers, not only in UI
- remote control shutdown on revoke, disconnect, and display change is enforced promptly
- plaintext NKN seed storage at rest has been replaced with protected local storage on Windows
- release hardening now includes explicit queue caps, diagnostics for risky overrides, hash-verified bridge/runtime bundling, no shipped `node_modules`, and documented installer-signing expectations

### Areas that still need caution in release messaging

- remote clipboard looks partially wired rather than clearly shipped as a transport feature
- the bundled NKN bridge and runtime remain an explicit trust boundary
- dependency posture is significantly weaker on the bridge side than on the .NET side

### Addressed release blockers in the current repository

- `NLINK-SEC-001`: inbound file-transfer data-session queues are bounded to `512` frames / `32 MiB` and fail closed with `ReceiverBufferExhausted`.
- `NLINK-SEC-002`: file-transfer V4 bulk data source/sender validation is bound to the negotiated remote bulk endpoint.
- `NLINK-SEC-003`: bridge binary frame length caps are enforced before body allocation, including the `64 KiB` payload cap.
- Release-affecting unsafe overrides now require `NLINK_UNSAFE_DEVELOPER_MODE=1` in Release builds; suppressed overrides are logged as `release_override_suppressed` and surfaced through diagnostics evidence.

## Public Release Blockers

Based on the repository contents reviewed so far, the remaining likely public-release blockers are:

### 1. Trusted bridge dependency stack still carries stale/deprecated packages

The shipped bridge dependency chain still includes deprecated legacy packages through `nkn-sdk`.

Why this is a blocker:

- the bridge is part of the trusted runtime boundary
- deprecated legacy packages in that path are not just maintenance debt; they are release risk
- the bridge remains trusted local runtime code even though the shipped dependency evidence is now cleaner

Affected paths:

- `tools/nkn-bridge/package.json`
- `tools/nkn-bridge/package-lock.json`

### 2. Public release still depends on bridge/runtime assumptions that are not proven by repository code alone

The security model now has stronger application-layer protection, but it still depends on the bundled bridge to:

- report the authoritative connected NKN identity correctly
- report inbound source identity correctly
- behave safely as a local trusted transport adapter

Why this is a blocker unless explicitly accepted and verified:

- this is a real trust boundary
- repo code review alone cannot fully prove those runtime assumptions

Affected areas:

- `RealNknClientAdapter`
- `tools/nkn-bridge/index.js`
- NKN runtime packaging

## Out Of Scope Features For This Release

The following feature does not currently appear fully shipped in the audited code and should be treated as intentionally out of scope unless separately completed and audited:

- remote clipboard

Release rule:

- it must not be advertised as a shipped secure feature in this release
- it is not a blocker if it remains intentionally unshipped and unclaimed

## Uncertain / Requires Manual Verification

These items cannot be fully closed from repository reading alone and should be treated as manual verification work before public release:

### 1. Actual NKN transport guarantees

Need manual verification of:

- transport confidentiality/integrity properties
- sender authenticity properties
- any relay/network-level trust assumptions relied on by the bundled bridge path

### 2. Real bridge/runtime behavior in packaged release

Need manual verification of:

- bridge startup and shutdown behavior in the signed installer build
- absence of unexpected bridge-side logs outside the audited source path
- runtime identity/address behavior across clean installs and upgrades

### 3. Non-Windows secret-store behavior

The current secret-storage hardening was implemented for Windows packaging.

Need manual verification of:

- whether non-Windows distribution is in scope
- what protected storage strategy is used there
- whether release claims should remain Windows-only until that is resolved

### 4. Installer signing and release pipeline enforcement

The runbook now requires signing, but repo review alone does not prove the actual release pipeline enforces it.

Need manual verification of:

- installer signing in the real release pipeline
- binary signing status of shipped artifacts
- checksum/signature publication workflow

### 5. Payload-limit enforcement across all lower layers

Transport-level bounds are now documented, but final abuse-resistance still depends on lower-layer enforcement in:

- bridge framing
- secure-envelope parsing
- screen-share payload/chunk codecs

Need manual verification that the shipped build uses those lower-layer limits exactly as documented.

## Release Decision

### Repository-only conclusion

The repository is in a much stronger state than at the start of the audit, especially in:

- invite binding
- post-handshake traffic protection
- execute-path consent enforcement
- seed storage at rest
- transport abuse bounds

### Conservative release conclusion

I would not sign off a public-release security approval yet until:

- the bridge dependency stack is cleaned up or explicitly accepted as a documented release exception
- the manual-verification items above are completed for the real packaged release path

## Final Release Go/No-Go Checklist

Use this checklist as the final release gate for a public build.

### Go / No-Go decision

- `NO-GO` if any item in the `Required for GO` section is not satisfied
- `GO WITH EXCEPTION` only if an item in `Documented release exceptions` is explicitly accepted and recorded in release notes/runbook
- `GO` only if all required items are satisfied and all manual verification items are completed

### Required for GO

#### Identity, invite, and session binding

- [ ] Release-default invite mode is helper-bound and one-time
- [ ] Legacy insecure invite mode is not active
- [ ] Insecure unbound public invite mode is not active
- [ ] Helper identity is cryptographically bound to the invite/session on the release-default path
- [ ] Invite replay is rejected

#### Handshake, authorization, and consent

- [ ] Post-handshake chat traffic is application-layer protected
- [ ] Post-handshake remote-control traffic is application-layer protected
- [ ] Post-handshake screen-share traffic is application-layer protected
- [ ] Post-handshake file-transfer traffic is application-layer protected and requires explicit accept/decline
- [ ] Post-handshake lifecycle traffic (`approve`, `reject`, `session_end`) is application-layer protected
- [ ] Execute-path authorization is enforced in runtime handlers, not only in UI
- [ ] Remote control is disabled promptly on revoke
- [ ] Remote control is disabled promptly on disconnect
- [ ] Remote control is disabled promptly on display-change stop path

#### Secret storage and diagnostics

- [ ] NKN seed is not stored in plaintext on disk
- [ ] Legacy plaintext seed files are migrated or rejected without silent rotation
- [ ] Bridge connect no longer depends on reading a plaintext disk seed
- [ ] Shareable diagnostics omit/redact stable peer identity and session metadata
- [ ] Operational logs do not emit `key_path`, raw seed, private key, or payload contents

#### Abuse resistance and release hardening

- [ ] High-priority control queue is explicitly bounded
- [ ] Low-priority control queue is explicitly bounded
- [ ] Screen-share send path is bounded under transport pressure
- [ ] File-transfer data-session queue is bounded to `512` frames / `32 MiB` and overflows fail closed
- [ ] File-transfer V4 bulk sender/source is bound to the negotiated remote bulk endpoint
- [ ] Bridge binary frame caps reject oversized bodies before allocation
- [ ] Replay windows are enabled for control, lifecycle, and screen-share traffic
- [ ] Security-relevant risky overrides are surfaced in diagnostics
- [ ] Release build suppresses unsafe override env/appsettings values unless `NLINK_UNSAFE_DEVELOPER_MODE=1` is set
- [ ] Release build fails closed on insecure remote-control sequence-gate override
- [ ] Release runbook includes payload/queue limit matrix
- [ ] Public release installer signing requirement is documented and enforced in the real release process, unless explicitly accepted as a release exception

### Documented release exceptions

These items must be either resolved before release or explicitly accepted as release exceptions.

- [ ] Public Windows artifacts are unsigned for this release
- [ ] Bridge dependency stack still relies on deprecated legacy packages through `nkn-sdk`
- [x] Bridge artifacts are built from lockfile restore and ship no `node_modules` tree (`nodeModulesShipped=false`)
- [ ] `Microsoft.Windows.SDK.NET` preview dependency remains on the production Windows path

If any of the above remain unresolved:

- [ ] release notes document the exception
- [ ] release runbook documents owner and follow-up plan
- [ ] product/release owner explicitly accepts the risk

### Accepted release exceptions

The following release exceptions are now treated as accepted for this release train:

- Public Windows installer and portable artifacts are unsigned for this release
- Bridge dependency stack still relies on deprecated legacy packages through `nkn-sdk`
- Bridge SDK replacement remains deferred; committed/shipped `node_modules` has been corrected and is no longer an accepted exception
- `Microsoft.Windows.SDK.NET` preview dependency remains on the production Windows path

This means the remaining release decision is now driven primarily by the manual-verification checklist below, not by these exception items being unresolved.

### Shipped feature claims must stay within audited scope

- [ ] File transfer is claimed only as the audited `0.6.2` shipped scope: V4-only, single-file, explicit accept/decline, session-envelope protected

### Out-of-scope features must remain unclaimed unless separately verified

- [ ] Remote clipboard is either fully audited as shipped or omitted from release claims

### Manual verification before GO

- [ ] Real packaged release verifies helper-bound invite flow end to end
- [ ] Real packaged release verifies approval and remote-control consent flow end to end
- [ ] Real packaged release verifies secure screen-share flow end to end
- [ ] Real packaged release verifies secure file-transfer flow end to end with a live NKN soak
- [ ] Real packaged release verifies no unexpected bridge-side logs outside audited paths
- [ ] Real release artifacts are Authenticode-signed and signature status is `Valid`, or unsigned Windows artifacts are recorded as the accepted release exception
- [ ] Real packaged release diagnostics are reviewed for accidental identity/session leakage
- [ ] Real packaged release uses the documented payload/queue bounds
- [ ] If non-Windows release is in scope, protected secret storage is verified there too

### Current audit-based recommendation

- `Current recommendation: GO WITH EXCEPTION` only after the manual verification checklist is completed

Reason:

- the repository is substantially improved and many earlier hard blockers are now addressed
- but public release should still wait until:
  - the manual verification checklist is completed on the real packaged release path
