# C1e: optional read-only input identity

Date: 2026-09-14. Base HEAD: `ef78e9d7` plus uncommitted A/B1/C0/C1 work.

`LocalInputReadLease` pins a read-only Windows file object without blocking the
primary's existing ordinary readers. It is an input identity primitive, not a
parent/worker binding, validated writer ledger, evaluator owner or completion
issuer. Production retained parents do not request it yet.

## RCA and bounded change

The missing source-ownership edge cannot be closed by hashing a path and closing
it before the worker reads it: the path can subsequently name changed bytes.
Retaining the streaming writer's original write-access handles was prototyped and
rejected. Local tests demonstrated that their original sharing/access combination
blocks ordinary primary readers. Requiring a new owned-reader path to compensate
would make optional setup a primary prerequisite. That prototype, including its
parser changes, was removed; the streaming ledger and parser remain unchanged.

The retained parent's existing source SHA pass can instead opt into
`Get-FileTransferEvidenceSha256 -ReadLease ([ref]$emptyOwner)`. It retains that
pass's `File.OpenRead` handle, digest, size and volume/file-index identity. No new
source traversal is added. Read sharing remains compatible; normal write opens,
rename and deletion are denied while held. Optional type/identity acquisition
failure preserves the original hash result without a lease. Existing primary
hash/read errors retain their original semantics. The caller owns disposal.
An incompatible typed output reference also omits the optional lease and releases
its handle without replacing the primary hash result.

`HashAndHold` verifies bytes, not decoded line count (`VerifiedLineCount=-1`).
The parent's existing line-count read remains separate and compatible. This is
not a cross-process identity receipt or protection against malicious same-account
code, existing writable mappings, or a process killing its owner.

## Expected writer input is not verified input

`OpenExpected` takes the already trusted ledger owner's digest/count and pins a
read-only candidate without scanning it. A declared digest alone cannot open a
verified reader. Its one-shot `VerifyAndReadLines` hashes actual bytes during the
evidence traversal. Only reaching EOF with the exact digest and decoded count
sets verified state. Yielded strings before then are provisional, not proof.
Even consuming exactly the declared number of lines without advancing to EOF
does not verify. Partial enumeration, mismatch, cancellation, replay or budget
exhaustion cannot revive a failed candidate. Disposal invalidates ownership.

Limits: 262,144 records; 67,108,864 decoded code units; 262,144 units per line;
67,108,864 writer bytes; 250 ms cooperative elapsed verification budget. The line
reader checks before growing past its cap and at 2,048-unit checkpoints. The
clock includes time between enumeration calls, so a slow consumer conservatively
omits. This is not a hard deadline or proof of capacity for a live 256 MiB run.
Opened readers have independent positions and must be disposed by their callers.

Writer ledger trust, run membership and source/native/evaluator association remain
separate requirements under [C1b](file-transfer-local-producer-request-contract.md).
Do not add a new pre-launch scan to manufacture missing trusted writer context.
The test's explicit verification traversal is not permission to add whole-input
work after primary reports. No completion token or output payload is created.

## Verification

Five new focused tests pass: two-host unit controls, two-host retained-parent
lifecycle controls, and generated service evidence tested on both hosts. Each
parent fixture executes the actual parent body with only test transport/watchdog
substitutions: 24 cases per host preserve primary readers, exit handling, reports,
proof checks and terminal receipts. Generated evidence has 232 source/writer
lines, 269,190 writer code units and eight A/B1 uses, with exactly two original
Snapshot evaluations and no added authority evaluation. Five directly related
regressions also pass. Details: `artifacts/publication-read-inputs-20260914/RESULTS.md`.

No live run, rebuild, route/runtime change, completion issuance or production
export integration is claimed by this slice.

Follow-up: [C1f source request](file-transfer-local-retained-source-request-contract.md)
uses this verified lease to associate the frozen file with the actual retained
worker. Its separate handle preserves the freeze independently of this lease;
it still does not supply trusted writer, evaluator or publication completion.
