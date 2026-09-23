# Initial Tuna TCP refusal: RCA and bounded diagnostic plan — 2026-09-23

## Result and scope

The latest `second-transfer-after-reactivation` attempt failed during initial
Tuna preparation, before a transfer route or payload began. The retained dial
error is a TCP connection refusal, not the previous attempt's 25-second timeout.
The exact cause of that refusal is **unresolved**. A separate, confirmed
diagnostic defect prevents correlating the failed endpoint with the listener's
provider-path history: the app discards those endpoint fingerprints and the
dialer's individual SDK error lines.

This milestone is an investigation and fix plan only. No application runtime,
SDK dependency, classifier, authority, retry, readiness, or timeout behavior was
changed. No live probe, historical authority reevaluation, commit, or push was
performed. The full GUI matrix remains gated.

## Evidence provenance

- Checkpoint: `490d0b37fdcf0bab9bb53de26d6dd753efd6ad9d`, branch `v0.7.0`.
- Latest live artifact: `artifacts/gui-classification-live-20260923-490d0b37`;
  campaign `live/20260923T150712Z`. Fresh portable package and both strict
  preflights passed; one escalated attempt ran, without setup/goodput reruns.
- All 132 existing live evidence/source pins still match. The packaged sidecar
  records this checkpoint with `vcs.modified=false`, and pins
  `github.com/nknorg/nkn-tuna-session@v0.2.6`. Local `go mod verify` passed.
- Investigation artifacts: `artifacts/tuna-initial-dial-rca-20260923`.
  `Inspect-Evidence.ps1` produces `evidence-audit.json` without network probes
  or another classifier/analyzer evaluation. Wallet, identity, and runtime-state
  files are excluded from the evidence scan.

The classifier-shape fix is already live-verified: the completed 76-property
response was accepted and retained. That does not validate the retry-retirement
runtime fix, which this setup failure never exercised. The campaign remains
`FAIL/setup/incomplete_proof`; its performance field is not measured transfer
success. Existing primary outputs must not be rewritten or reclassified.

## Observed timeline

Line references below identify the scenario's original
`live-operational-log/nlink.log`. Times are UTC; process output can interleave.

| Time | Source lines | Retained observation |
| --- | --- | --- |
| 15:08:14 | 365 | Listener sidecar started, PID 38440. |
| 15:08:20 | 510–522 | Four usable provider entries; listener ready. Four initial path-change events and an aggregate path event were logged without their endpoint metadata. |
| 15:08:20 | 545, 570 | Fresh dialer sidecar PID 47112; one dial started. |
| 15:08:25 | 662–666 | Four stderr records retained only lengths; one aggregate dial failure retained TCP connection refused. Failed endpoint fingerprint: `03a7284c89aae09c`. |
| 15:08:25 | 669–674 | Dialer exited; two listener path-change observations and an aggregate path event were recorded, again without endpoint metadata. |
| 15:08:35–40 | 843–845, 918–919 | Further listener path-change observations. |
| 15:09:14–15 | 1443, 1454–1455 | Listener stop requested for preparation revocation, then force-killed/stopped. |

The listener stop request occurred **49.3996458 seconds after** the failure.
There was exactly one listener process start, one dialer start, and one dial
start. The four stderr record contents cannot be reconstructed from their
lengths; they must not be represented as four confirmed refusals.

## Source findings and limits

1. **No early app-driven listener shutdown is shown.** The stop follows the
   refusal by 49.4 seconds. This does not prove that the provider's reverse TCP
   endpoint remained reachable while the listener process was running.
2. **A reused dialer SDK endpoint cache does not explain the first dial.**
   `NknTunaAccelerationLane.cs:374` starts a fresh process; sidecar
   `main.go:686` constructs a new `TunaSessionClient`. The pinned SDK constructor
   starts with empty endpoint/connection maps. Its first
   `getPubAddrsFromRemote` request obtains the remote endpoint set. This does
   not exclude stale provider-side advertisement/state or independently prove
   the remote target's identity from retained diagnostics.
3. **The SDK tries the endpoint set, not one selected provider.** In the pinned
   SDK's `client.go:553`, `DialWithConfig` concurrently attempts every nonempty
   endpoint, logs individual failures, then returns an aggregate error if no
   connection succeeds. `client.go:952` performs the TCP dial before session
   metadata exchange. One returned refusal does not identify every path's
   outcome. Source inspection also finds shared aggregate-error writes in that
   SDK method; this is not established as the cause of the observed refusal
   and is outside this fix plan.
4. **Readiness is metadata, not a TCP reachability result.** Sidecar
   `main.go:2227` defines a usable provider entry as nonempty IP and nonzero
   port. The SDK listener publishes its reverse endpoint metadata
   (`client.go:332,360`). Four usable entries do not prove four reachable TCP
   ports.
5. **Endpoint changes cannot yet be correlated.** Sidecar
   `main.go:1904,2076` emits per-index current/previous endpoint hashes and
   observes provider paths every five seconds. A change logged after the
   refusal may have occurred before it. The C# listener handler
   (`NknTunaListenerSidecarSupervisor.cs:539,668`) retains event names,
   lengths, and selected summary counts, not those hashes. Dialer stderr
   (`NknTunaAccelerationLane.cs:456`) retains only length. Searching 54 scoped
   scenario diagnostic files and nine extra GUI failure files found no raw
   endpoint-hash/provider-set/SDK per-path dial details to recover.

The first unresolved edge is therefore **advertised listener provider endpoint
to successful initial dialer TCP connection**. Provider churn, an unusable
published reverse port, a publication/selection mismatch, and network refusal
remain distinguishable hypotheses, not established root causes. Probing the
ephemeral port after process cleanup would not diagnose its earlier state.
The stored GUI label `preflight_listener_unavailable` is not stronger evidence
than the underlying readiness and refusal records.

## Bounded fix plan: retain diagnostic evidence only

1. Preserve an allowlisted projection of existing listener provider-path
   events: path index, current/previous endpoint hash, usable flag, state reason,
   and observation timestamp. Include the existing immutable process-owner
   correlation, so replacement processes and late callbacks cannot be conflated.
   Preserve the existing ownership checks; rejected/stale callbacks stay
   diagnostic-only and must not update current-owner path state.
2. Recognize only the pinned SDK's per-path dial-error stderr format. Retain a
   derived endpoint hash and a bounded error category such as refusal, timeout,
   or other. Match Go's existing hash exactly: first eight SHA-256 bytes of the
   literal `ip:port`, encoded as 16 lowercase hexadecimal characters. Do not
   retain raw stderr, IP/port, peer addresses, wallet data, or arbitrary JSON.
   Hashes are correlation fingerprints, not a claim of irreversible anonymity.
3. Bound detail retention to 64 records per immutable process owner, with an
   explicit truncation marker. Malformed, oversized, or unrecognized input must
   not throw into process I/O handling or manufacture a recognized failure.
   Keep existing generic diagnostics and runtime outcomes unchanged.
4. Add focused tests for listener field projection, the pinned SDK error format,
   hash agreement, redaction, malformed/oversized input, concurrent callbacks,
   the cap/truncation marker, and replacement/stale-owner isolation. Verify that
   logging cannot complete readiness, install runtime, schedule retries, publish
   a route, or alter progress. Reuse existing listener/dialer ownership test
   seams; no new transport abstraction or SDK fork is needed.
5. After focused tests and directly affected Infra/ownership regressions pass,
   rebuild the portable package, verify freshness, run escalated strict
   preflights, and execute one `second-transfer-after-reactivation` attempt.
   No automatic setup retry, full matrix, or historical reclassification.

Success for this diagnostic change means that a failed attempt either provides
owner-correlated endpoint fingerprints and per-path error categories, or
explicitly reports missing/truncated diagnostics. It does **not** mean readiness
or the transfer passed. Compare failed endpoint fingerprints to the listener
timeline before proposing a transport fix; if the targeted scenario advances,
stop at the next first missing proof edge.

No extra TCP probes, altered provider selection, cache resets, retries,
deadlines, readiness rules, throughput changes, or coordinator-authority changes
are included. These observations remain evidence only.
