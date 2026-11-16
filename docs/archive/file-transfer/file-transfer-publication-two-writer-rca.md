# C2d: genuine two-writer delayed ingress

2026-09-15. Continues C2c with a fresh deterministic two-process fixture. C2d
changes test/support code only. The earlier uncommitted C2b resolver change is
preserved. No live run, production authority/correlation change or reevaluation
of C2a's sealed evidence is performed.

## Bounded implementation

Each peer runs in its own isolated .NET test host and owns its original logger
from startup through completed drain. The existing FakeNkn client hooks exchange
actual encoded packets over one local named pipe. The real invite approval,
session security, offer/accept/open and data codecs run separately in each peer.
Packet source, destination, channel and payload are preserved; the pipe does not
transport log records or grant route authority. No outbound NKN connection runs.

The sender's real regular-v4 submission at epoch 0 is held by the fake network.
Both peers advance through genuine coordinator-issued regular/Tuna/fallback/
second-Tuna publications. Only after both reach epoch 3 does the pipe deliver
the exact original packet twice. The receiver rejects the first before dispatch
and deduplicates the second. The test captures receiver state before releasing
the packet and verifies unchanged state, no dispatched frame and no data delivery.

The C2c coordinator history driver is reused without changes. Its Snapshot
boundaries and receipts are coordinator-authored, while receipt-effect records
are controlled projections through the real emitter. This is still not a full
SessionFileTransferService/bridge transition execution.

After both original logs drain, a separate test input merges their unchanged
records by original writer-attempt UTC ticks. The test independently verifies
actual distinct process IDs, one writer per process, startup/drain records,
contiguous per-writer sequences, and exact preservation of every original record
in writer order. The selected source and receive lines must be exact members of
their respective original process logs. No writer label or event field is edited.

## First missing proof edge

The original primary now passes the different-writer source-send predicate.
Its already-computed selected diagnostic reports:

- `RejectionChainMatched=true`
- `Recovered=false`
- `Reason=missing_receiver_route_transition`

The source and receive observations match the exact envelope, security payload,
security sequence, establishment, scope, frame and range. They have separate
security installations and real writer identities. The local receiver publication
witness and adopted companion are issued on both PowerShell hosts. Original
Snapshot validity, complete history and full/compact authority parity pass.

The missing edge is expected for this fixture: it publishes coordinator-backed
transport hints and Snapshot history but never executes the service path that
emits `filetransfer_route_transitioned`. The production correlator additionally
requires that inbound transition between the earlier send and rejected receive,
with the exact current route/protocol/epoch/runtime/leg/publication/commit tuple.
Passing local Snapshot publication evidence does not silently replace this
separate recovery predicate. C2d explicitly asserts the event is absent.

This is not a newly proven application defect, a recovery result, or C2a's live
root cause. In particular, the two-writer fixture does not show that C2a's 13
unresolved records lack this same edge. Original primary FAIL remains unchanged,
with exactly its three existing evaluator calls and no extra recovery evaluation.

## Next bounded step

Exercise the receiver service's actual publication-backed transition/disposition
path in the controlled two-peer setup, retaining the same delayed packet. The
relevant production projection entry is
`EmitInboundFileTransferRouteTransitionCompatibilityProjectionsLocked` in
`SessionFileTransferService.RouteTransitionFallback.cs`; it first validates that
the context, transition authority, route decision and disposition are current.
Its accepted path emits the transition once using the disposition's publication
token. Calling only `LogFileTransferRouteTransitioned` or inserting a matching
log line would bypass those conditions and is not an acceptable reproduction.

Keep the C2c single-writer negative and C2d missing-transition negative intact.
Observe the next diagnostic inside the original primary before proposing any
production fix. Do not loosen receive rejection, substitute observation for
authority, infer all live failures share a cause, or alter C2a artifacts. If the
needed service/bridge input cannot be produced offline, stop at the new targeted
live 256-MiB gate rather than launching it automatically.

Verification details: `artifacts/publication-two-writer-integration-20260915/RESULTS.md`.

## C2i continuation

The separate service-ingress integration now exercises this missing edge with
the real receiver service commit/disposition and a yield frame received from
the original sender process. The primary advances to `missing_tuna_resend`;
the C2d missing-transition negative remains unchanged in its expected outcome.
See `docs/file-transfer-service-ingress-integration-rca.md` for scope and results.
