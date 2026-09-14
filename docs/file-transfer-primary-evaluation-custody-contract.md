# C1l: private custody of the original evaluation invocations

Date: 2026-09-14. Base HEAD `ef78e9d7`, with accumulated uncommitted work.
This closes the missing private invocation-identity edge ahead of completion
issuance. It does not issue publication completion or enable live export.

Follow-up: [C1m private publication completion](file-transfer-primary-publication-completion-contract.md)
adds the separately bounded issuer to this private scope. C1l's invocation token
alone still cannot authenticate arbitrary prepared JSON or candidate bytes.

## Why the prepared result was insufficient

C1j's context and C1k's JSON remain mutable preparation data. A context containing
two plausible views, or matching hashes copied from a prepared result, cannot
establish that the primary received those objects from its original calls. Signing
that JSON or adding an arbitrary expected-digest setter to C1a would not close
this ownership gap.

The actual writer-bound retained worker now admits one `TryBeginPrimary` call.
It requires its exact completed source reader, the primary's exact input context,
and the original full/reduced arrays within the existing record admission cap.
A source-only, incomplete, cancelled or already claimed worker cannot admit it.
An unsuccessful claim is also one-shot; there is no retry with substituted data.

## Primary-only capability and immutable pair

The primary keeps the returned `PrimaryEvaluationCustody` in a private variable.
It is not a property of the mutable input context, and it is never passed to an
evaluator, publication resolver or formatter. Immediately after each unchanged
original evaluator call, the primary records that call's exact array and returned
view, in full-then-compact order. Failed or replayed recording omits the custody.

After C1j's original association check succeeds, one `Take` binds the exact context,
both arrays and both returned views. Its immutable `CompletedEvaluations` token
contains the actual retained request ID and distinct full/compact invocation IDs.
Neither type has a public constructor; the token has no public setters. These
IDs name trusted-primary call-site associations, not additional evaluations or
independent claims about Coordinator authority. The two calls may legitimately
return the same view reference; their invocation identities still differ.

The primary checks this token and all exact references immediately before and
after C1k preparation. Replacing a context, array or view with a copy does not
satisfy custody, even when every serialized field or checksum agrees. A rejected
read-only ownership query does not invalidate the genuine token. Taking twice
returns no second token. Duplicate evaluation recording invalidates the scope.

The source reader, actual worker and original parent cancellation lifetime remain
required on every record, take and ownership check. Primary `finally` disposes
custody on normal/report-failure/evaluator-failure exits. Worker disposal also
disposes it. Cleanup drops its context, array, view and token references. The
separately retained immutable token does not confer ownership after cleanup.

## Bounds and unchanged behavior

The guard performs a constant number of reference/state checks and zero-time
checks against the already-held process/cancellation handles. It allocates one
scope, one small immutable pair and two GUID values. It performs no input scan,
hash, view copy, serialization, file/network I/O, new handle acquisition, IPC
write, wait, retry, or authority evaluation. Existing C1j/C1k work limits remain
unchanged; this is not a new preparation budget or watchdog extension.

All acquisition, recording, taking and preparation checks occur before compact
report publication. After reports, only bounded reference cleanup is added.
Missing custody omits the optional prepared result; it does not alter original
failure selection, report bytes, receipts, evaluator calls or runtime behavior.
Existing external failure-witness reference behavior is unchanged.

An ownership check that throws after preparation also clears the private candidate,
not only the external failure selection. The regression executes the actual
first-failure block with omission/exception controls before and after preparation;
a post-check exception must never leave a candidate available for later adoption.

## Remaining completion boundary

This pair deliberately has no publication row, payload digest, completion method,
serializer, control channel or parent adoption API. It cannot convert arbitrary
prepared JSON into authenticated completion. Direct test calls with opaque views
test custody only; only the trusted actual primary call sites establish invocation
provenance. This is local-worker capability isolation, not a same-account malicious
process security boundary.

Next, the private issuer must bind this pair to the independently checked native/
source descriptors and exact selected semantic rows, prepare bounded immutable
completion before reports, and use a separately parent-owned completion channel.
C1a's pre-known-digest slot remains unchanged and disconnected. Upstream GUI-owner
relay and post-terminal-receipt adoption are still required before enabling live
export. A new live 256 MiB test is not yet required.

Verification: `artifacts/publication-primary-custody-20260914/RESULTS.md`.

The completed focused gate passed 11 unique tests: 42 actual retained-worker
custody cases across PS5.1/7, 12 actual-seam omission/exception controls, 60 generated
publication-witness cases, and the directly affected primary-report and retained-
parent/writer regressions. The post-check exception regression first reproduced
the stale optional candidate and passes after the catch clears it. Original
reports, evaluation counts, receipt behavior and watchdogs remain unchanged.
