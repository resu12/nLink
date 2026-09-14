# C1t: pre-checkpoint live-capacity RCA and fix plan

2026-09-14. Branch `v0.7.0`, HEAD `ef78e9d7`, accumulated changes uncommitted.
Status: **not ready to approve the live-capture checkpoint or rebuild**.
This is a blocking finding from the ongoing review, not a clean review of all
accumulated changes and not a runtime/transport RCA.

## Finding: the bounded input path excludes the known live input

The default-off C1s persistence workflow can save a genuine small-fixture
publication. It cannot obtain one for inputs shaped like the latest targeted
live 256 MiB run. The first failing edge is retained-source request admission:
`LocalRetainedSourceRequest.TryCreateCore` rejects sources above 67,108,864 bytes.
The last live retained source is 108,501,606 bytes. Its actual verified read lease
is valid, but the current source request returns null. No writer/native primary
custody, publication completion, adoption or companion can follow that omission.

The code is enforcing its reviewed bounds correctly; the integration/readiness
assumption was wrong. C1j explicitly did not establish live-scale capacity, but
the later small positive fixture did not close that gap before C1s proposed
checkpoint/build/live validation. The new switch does not bypass any bound.

There are additional deterministic admission constraints, so changing only the
first constant would merely move the omission downstream:

| Gate | Known input / lower bound | Current limit |
| --- | ---: | ---: |
| Retained source bytes | 108,501,606 | 67,108,864 |
| Writer evidence bytes | 140,661,859 | 67,108,864 |
| Primary input record work, `7*F + 4*C` | at least 573,587 | 262,144 |
| Witness record work, `8*F + 5*C + 2*W` | at least 890,672 | 262,144 |
| Native input hashed UTF-16 units, three full roles plus compact | at least 325,013,178 | 67,108,864 |

Here F=81,941 full retained records, W=117,572 writer records, and the lower
bounds use C=0. The retained source contains 108,337,726 decoded UTF-16 units,
excluding line terminators. No compact count, private completed view or authority
evaluation is needed to establish these failures. Native and writer 250-ms clocks
and the shared 2,000-ms optional-work clock were not benchmarked by this review;
they may impose further limits. Passing only the byte/record gates would not
establish capacity or guarantee a future live diagnostic.

## Evidence and reproduction

Use the latest completed live attempt:
`artifacts/filetransfer-route-acceptance/publication-use-live-20260913/20260913T180334Z/regular-v4-live-activation-off-on-off-256mb`.
Its runtime checkpoint is current HEAD; the uncommitted affected changes are
diagnostic/tooling paths, not new runtime evidence. Historical files are used
only to inspect input shape, not to reconstruct current GUI/worker ownership.
The historical run did not use C1s and is not being reported as a C1s failure.

The artifact-local `Test-LiveCaptureAdmission.ps1` rechecks the pinned receipt,
retained source and writer hashes. It obtains the genuine source hash/read lease
and calls the current common `TryCreate` admission, which rejects it. It separately
calls actual `OpenExpected` for the writer, also rejected by the byte limit.
The arithmetic rows above are static downstream constraints, not claims that
later primary functions were executed with a reconstructed capability.

Both Windows PowerShell 5.1.26100.9444 and PowerShell 7.6.6 exit 0 after confirming
these expected rejections. No GUI/retained worker, network check, Snapshot
evaluation, completed publication or primary artifact write is performed.
Details: `artifacts/publication-precheckpoint-review-20260914/RESULTS.md`.

## Recommended bounded fix sequence

1. Revise and review the **live-scale input/work contract** before implementation.
   Define a finite supported evidence envelope using these measured inputs.
   Distinguish constant-size source-identity admission from expensive input
   fingerprinting and writer-membership validation. Account for every traversal,
   native role hash, retained array and optional-work clock; specify explicit
   omission outside the envelope. A larger source cap alone is not an adequate fix.
2. Benchmark a bounded native, single-pass/shared-input design offline, without
   authority evaluation. Preserve each role's exact input order, raw strings,
   physical locations and framed hash semantics; sharing must depend on actual
   owned array identity, not equal digests. Preserve complete writer history and
   EOF validation. Use the results to set/review finite budgets, not to disable
   watchdogs or substitute sampled/subset input for original primary input.
3. Implement only the reviewed ownership/input-work changes. Verify boundary and
   over-cap rejection, cancellation, mutation, EOF/identity, original evaluator
   count, receipt provenance, failure isolation and default-off behavior on both
   hosts. Add a live-shaped offline positive integration gate with real owned
   producer/primary data; copied saved reports cannot satisfy it. Keep the existing
   small-fixture gates, but do not treat them as the large-input gate.
4. Resume the remaining accumulated review, run only the directly affected lanes
   and at most one relevant broader contract gate, then checkpoint and rebuild.
   Stop before the new live 256 MiB run.

This is a proposed contract revision, not permission to raise or remove the
existing limits. The accepted 1-64 primary-owned exact failure witnesses and
256-KiB companion cap remain unchanged. No later-failure selection, second
authority evaluation, saved-view reconstruction, transport/runtime tuning,
relaxed acceptance predicate or live-test authorization is proposed.

No production source, limit, classifier, receipt or verdict was changed in this
review. No commit, stage, portable rebuild, live run or push was performed.

Follow-up: the user approved the resource-contract revision. [C1u's measured
input/work contract](file-transfer-publication-live-input-contract.md) now defines
finite revised limits and retains both-host offline sizing evidence. It also
accounts for the 9,180-record selection bucket and 4,114 operation rows. Production
implementation and a genuine live-shaped positive integration remain required;
the original live-readiness blocker is not marked resolved by the prototype.
