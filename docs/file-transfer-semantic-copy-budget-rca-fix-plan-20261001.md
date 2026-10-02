# Semantic evidence copy budget RCA and fix plan

2026-10-01. The first remaining native acquisition failure is now localized:
the original semantic-evidence copy exceeds the shared 10,000 ms active-work
cap. The original processing and cursor match succeed first. The copy then
rejects the expired original adoption and issues no owned copy. This is the
expected fail-closed behavior under the current limit, not successful
acquisition or evidence of a route-authority defect.

This step adds test-only diagnostics and saves a proposed fix. It does not
increase any limit, change runtime authority, or change acceptance. The
recommended next step is a separately approved increase of the shared cap to
15,000 ms, with the owned cursor parser still limited locally to 10,000 ms.

## Fresh diagnostic result

The single targeted offline scale profile processes 102,291 source rows. Its
`ProcessOwnedCursor` interval is 5,350 ms; cumulative active work is 7,560 ms
after the processing workflow and cursor match. Both applicable limits remain
10,000 ms. This interval is not isolated parser CPU time, and the traced fixture
is not a live transfer throughput benchmark.

During the original `TryCopySemanticEvidence` call, the first recorded rejected
predicate is `adoption_entry_budget`: a fresh cumulative-clock sample is
10,001 ms. That rejection occurs 2,430 ms into the copy interval; the call ends
at 2,431 ms. The copy writes 204,570,624 of 208,326,025 expected bytes. The
destination length agrees with the recorded byte count, but neither the
partial destination nor its contents constitute an owned success receipt.
`Completed=false`, `CopyReturned=false`, and `AdapterStarted=false`.

The fixture still fails with `Parent registration original semantic copy
missing`. Native content selection is not reached. The preceding normal-path
scale run failed at the same gate without this tracing; it remains preserved
as a separate observation rather than being rewritten with the new diagnosis.
The current observations are pinned in the
[verification receipt](../artifacts/native-semantic-copy-rca-20261001/verification.json).

## Root cause and evidence limits

The source adoption uses one cumulative stopwatch. `TryRunRecoveryRead` resumes
that same clock for semantic copying; it does not grant another ten seconds.
Each existing copy checkpoint evaluates `adoption.IsSourceVerified`, which
requires a current original adoption, fresh retained-file identity checks,
and the cumulative deadline. Expiry retires the adoption and makes the copy
return no receipt.

The successful processing workflow leaves about 2,440 ms of the shared allowance
in this run. The copy reaches that deadline before all bytes and final copy
validation finish. This directly identifies the first missing proof edge as
the original semantic copy under the cumulative cap. It does not identify a
dominant copy-cost component, prove that fifteen seconds is sufficient for
the entire acquisition chain, or demonstrate content-family completeness.
Fresh fixture sizes and timings vary; tracing adds overhead.

The diagnostic wrapper preserves each original deadline comparison and its
short-circuit position. It records the first failed comparison only during the
original copy thread. The byte observer runs after an existing copied chunk.
It takes no source, owner, custody receipt, or authority token and cannot
issue copy success. All new C# hooks are guarded by
`NLINK_TEST_NATIVE_CONTENT_ACQUISITION`; the normal adoption branch is unchanged.
No native polling substitution or reduced validation cadence is used.

## Focused verification

Four new tracing controls pass on PowerShell 5.1 and PowerShell 7. Successful
small copies still return the original owned receipt. Forced cumulative expiry
after the first copied chunk records `adoption_entry_budget` above eleven
seconds and returns no receipt. In these small controls all 1,030 bytes were
already written, demonstrating that complete bytes alone do not authorize
success.

Ten normal copy controls also pass on both hosts: the retained-handle primitive
lane and original-owner valid, budget, cancellation, and destination-collision
cases. These are 14 unique passing tests, plus one failing targeted scale
profile. The scale failure is retained as a failure, not converted into
acceptance because the diagnostic found its cause.

The verification reverses only the new C# tracing edits in memory and compares
the result with the pre-step file hashes. The normal Core and test assemblies
were rebuilt and are repinned; byte identity of those rebuilt assemblies is
not claimed. The Infra assembly, unrelated accumulated source changes, prior
receipts, preceding normal-scale artifacts, and all 99 historical live artifact
pins remain unchanged. No portable build, live GUI/NKN run, commit, or push
occurs in this step.

## Proposed bounded fix

Proposal only: raise `LocalFrontierFileAdoption.MaximumActiveMilliseconds`
from 10,000 to 15,000 ms. This is a shared allowance for original adoption,
processing, semantic copying, and later operations that charge the same clock;
it is not a copy-only allowance. The five-second extension is a conservative
candidate for validation, not a measured minimum or completion guarantee.

The owned cursor parser currently derives its local limit from that constant.
The implementation must therefore pin its local allowance separately at
10,000 ms when raising the shared cap. Otherwise a one-line constant change
would unintentionally raise the local parser allowance too. Generic readers,
generic cursor readers, and owned non-cursor readers must retain 5,000 ms.

Keep the same original clock without resets, fresh identity and cancellation
checks, managed wait APIs, copy chunk size, hash/line/byte checks, output
creation rules, handle ownership, and failure retirement. Do not accept a
partial or fully written file without the original owned receipt. Do not tune
transport or route behavior, or use cached evidence as authority.

After separate approval:

1. Implement only the shared-cap change and local-parser separation. Verify
   owned-cursor local expiry at eleven seconds and generic expiry at six seconds.
2. Update directly affected cumulative-deadline controls to reject above the
   new fifteen-second cap. Verify original copy success and retirement on expiry,
   cancellation, cloning, and file mutation without rerunning unrelated lanes.
3. Run one normal-path scale fixture. Require a genuine owned copy and then
   identify the next acquisition edge. Stop at any new first failure; do not
   infer full production readiness or run a live matrix from fixture progress.

The current acquisition gate and production integration remain incomplete.
Reported-candidate binding, content-family coverage, coordinator-issued route
proofs, and historical route verdicts are unchanged by this RCA.
