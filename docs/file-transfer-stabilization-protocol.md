# File-Transfer Stabilization Protocol

## Purpose

The goal of file-transfer work is reliable, integrity-preserving file delivery with acceptable throughput and clean coexistence with screen-share.

Better counters or faster apparent throughput do not count as success if terminal completion, hash/size integrity, retry behavior, or media coexistence regresses.

## Core Rules

- Start with retained evidence before changing transport or transfer tuning.
- Use one hypothesis per branch.
- Freeze unrelated layers and list them in the change card.
- Do not tune chunk/window/retry constants from one live NKN run.
- Keep V3 batching, bridge bulk behavior, repair/reorder behavior, and screen-share coexistence as separate evidence lanes.
- Prefer removing a proven bottleneck over stacking new fallback modes.

## Required Change Card

Every file-transfer behavior change must begin with:

- Bottleneck or failure being targeted
- Touched subsystem(s)
- Frozen subsystem(s)
- Expected mechanism proof in logs
- Expected completion/integrity proof
- Expected coexistence proof, when screen-share is relevant
- Rollback condition

If this card cannot be written clearly, the change is too broad.

## Promotion Gate

A file-transfer change may be kept only if:

- The intended mechanism activates in logs.
- Focused tests for the touched subsystem pass.
- Retained-log analysis produces no `FAIL_PROTOCOL_OR_INTEGRITY`.
- Deterministic local soak passes for the touched file-transfer path when runtime/core behavior is affected.
- Deterministic impaired or mixed local soak passes when the change touches repair/reorder behavior or screen-share coexistence.
- Live `NknFast` or `NknMixed` operator soak passes when the change touches the NKN transport, bridge bulk lane, packaged-app wiring, or real screen-share coexistence.
- Safe baseline comparison stays clean for the matching local/live mode when `-FailOnGate` is used.
- Terminal evidence shows clean completion with `error_code=(none)`.
- No payload budget rejection, decode failure, replay/security rejection, or bridge bulk send failure appears.
- Mixed screen-share evidence does not show file-transfer-induced media queue starvation.

## Review Order

1. Did the intended mechanism activate?
2. Did the transfer complete cleanly?
3. Did integrity and protocol guardrails stay clean?
4. Did retry, reorder, timeout, or degraded-mode behavior remain bounded?
5. Did screen-share coexistence stay healthy?
6. Did throughput improve without moving the bottleneck elsewhere?

## Stop Rules

- If a branch fails protocol or integrity gates, do not tune around it.
- If a branch improves throughput but increases retry storms or terminal failures, discard or narrow it.
- If live NKN evidence is noisy, reproduce deterministically before changing core tuning.
- If `LocalFast`, `LocalImpaired`, `LocalMixed`, `NknFast`, or `NknMixed` regresses against a matching safe baseline, read `baseline-comparison.txt` and fix that regression before interpreting throughput.
- If only one side of a transfer is visible in retained logs, classify the run as inconclusive rather than guessing.

## Related Docs

- `docs/file-transfer-operability.md`
- `docs/file-transfer-soak.md`
- `tools\FileTransfer-Ops.ps1`
