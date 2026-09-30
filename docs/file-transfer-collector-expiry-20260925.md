# Controlled capture expiry in the original finalizer

2026-09-25, `v0.7.0`, HEAD
`a653d4063070235c71fe211480aeb37863d9b2a4`.
Continues `file-transfer-collector-capture-rca-20260925.md`.
Offline/test-only. Production, acceptance and all existing timing limits/scopes
remain unchanged.

## Implemented and verified

The explicit `capture-expired` fixture uses the existing real 262,144-byte
Regular-v4 loopback producer, native-file parent, GUI child and original
finalizer. Only after the finalizer claims the original collector and completes
its owned stream processing does the guarded helper inject a 300-ms delay.
It consumes the same original stopwatch; it neither replaces nor resets the
clock and does not raise the 250-ms capture limit or 5-second stream limit.
The injection defaults off and is selected only by this named negative case.

The unchanged native capture factory returns no capture. The original finalizer
records the bounded inner exception, then retains its existing generic
`filetransfer_gui_evidence_capture_not_persisted` failure. Diagnostics now mark
`expiry_injected=True`, distinguishing these controlled failures from an
uninjected recurrence. Repeated processing on the same failed finalizer is
rejected without another read or delay.

Before the GUI child exits, the fixture checks that the original finalizer's
cleanup ran, the collector is no longer current, and write opens succeed for
the collector copy and all three processor outputs. No bytes are changed by
these opens. This proves release while the child is alive, rather than relying
on automatic handle closure at process exit. The fourth semantic-copy output
must not exist, and no native capture is published.

After child exit, the original parent accepts neither completion nor an output
owner. It independently verifies that the three existing output files are
released, the source copy is absent, both request/output memory maps and the
request mutex are gone, and the existing closed/ready/ack event checks pass.
It then returns before primary launch. The test also verifies original producer
file release and the absence of any primary analysis directory.

Expiry diagnostics deliberately report three released outputs, not four:
`RetainedOutputCount=3`, `RetainedOutputsReleased=true`,
`FourOutputsReleased=false`. Existing later-failure fixtures still require all
four outputs and retain their original successful-child or nonzero-exit rules.
No historical result is relabeled.

## Test results

All **eight targeted tests passed**, with no failures or skips:

- `collector-expiry-focused.trx`: the new expiry case on PowerShell 7 and
  Windows PowerShell.
- `collector-expiry-contract.trx`: six directly affected regressions, run once:
  two successful collector-output handoffs, two existing rejected-output cases,
  and both non-retained collector-finalizer integrations.

Both TRX files are under `artifacts/collector-expiry-20260925/results/`.

| Host | Fixture | Before capture | After rejection | Limit |
| --- | --- | ---: | ---: | ---: |
| PowerShell 7 | `82f60330-588b-48d4-b485-83e0e8c4d4d0` | 325 ms | 326 ms | 250 ms |
| Windows PowerShell | `96c3db43-41c2-48e5-baea-4f96acbbf946` | 355 ms | 356 ms | 250 ms |

For both controlled cases, native capture is absent, the child exits with code
1, primary authority evaluations are zero, no semantic copy or ledger report is
created, and parent completion/output adoption is false. Both bounded diagnostic
files retain the exact original capture-missing exception and timing fields.
Cleanup/retry assertions pass in both the child and parent.

The ordinary success regressions still execute the original three primary
authority evaluations and preserve the existing unproven/failing acceptance
outcome. The new negative test does not manufacture a failure witness or grant
runtime authority. No unsolicited spontaneous capture failure occurred in this
turn's targeted tests.

## RCA conclusion and timing-contract review

Capture-clock expiry is now a proven cause of this generic persistence failure
in a controlled execution of the actual owned finalizer path, with verified
cleanup. It remains **unconfirmed as the cause of the two historical intermittent
failures**, because their original inner exceptions and clock values were lost.
Those historical attempts remain failed and are not declared fixed.

The current test-only clock intentionally includes stream processing and durable
flushes. Production begins its native-capture clock later, at output metadata
capture. The controlled test demonstrates that enforcing the stricter scope can
reject otherwise processed evidence, but it does not by itself authorize
changing that scope.

The next decision is whether to retain this stricter whole-processing constraint
or align the test capture scope with production. The latter is worth a bounded
contract review: preserve the original 250-ms metadata budget and separate
5-second stream bound, original ownership, one-shot read, complete EOF/hash
checks, rejection-before-adoption and cleanup. A fresh clock supplied after a
failed attempt must not become a retry or a way to revive rejected evidence.
No timing-scope change is implemented or accepted here.

After that decision and any required targeted verification, the outstanding
owned membership/origin and terminal/file-integrity joins remain. This turn
does not prove writer-copy provenance, full primary handoff, payload integrity,
canonical route-cycle scope or live-256 readiness. The primary first missing
edge remains `file_writer_provenance_unproven`.

## Preservation

All 8,194 preceding file pins matched before work. Seven previously dirty test
files changed; all 138 other baseline dirty files remain byte-for-byte unchanged.
This document is the only new workspace file. The only other prior-pin change
is the rebuilt test DLL. Production/native processor/factory sources, earlier
artifacts and the preceding seal are unchanged.

The focused build disabled optional Avalonia telemetry for that invocation only;
no settings changed. PowerShell parsing and `git diff --check` pass. Verification
is recorded in `artifacts/collector-expiry-20260925/FINAL-VERIFICATION.json`.
No aggregate suite, portable rebuild, real GUI/network test, full matrix, commit
or push was performed.
