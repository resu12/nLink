# C2i — service-owned second-Tuna transition with genuine delayed ingress

## Scope and result

This is an offline integration of the C2d two-writer packet capture and C2f/C2g
receiver service transition. It is separate from the existing single-writer and
missing-service-transition negatives. No production runtime, analyzer, recovery
predicate, catalog, or live artifact is changed in C2i.

The original primary advances from `missing_receiver_route_transition` to
`missing_tuna_resend`. Its rejection chain matches, but recovery remains false.
The absence of an exact-range Tuna resend is intentional in this case. The
required exact Tuna send **and receive** predicate is unchanged.

## Controlled integration

Two actual test hosts run independent loggers and NKN signaling transports over
the existing offline named-pipe packet link. The initial encrypted regular-v4
batch (chunks 12–13, 2,048 raw bytes) is held at epoch 0. Original packet bytes,
writer identities and writer sequences are never rewritten.

The sender uses the existing Coordinator history driver through its fallback
publication, then obtains an actual Coordinator-issued yield decision and local
proof receipt. The production frame encoder creates its yield frame; the real
NKN data-session send crosses the offline packet link. No replacement peer is
created in the receiver process. This sender remains a controlled Core fixture,
not a complete sender-service workflow or a cross-peer-lineage certification.

The receiver uses the existing service fixture's initial regular publication,
first Tuna transition, peer-first fallback/checkpoint, paired release, responder
handoff, physical drain, second-Tuna commit and applied disposition. Its bridge
proofs and checkpoint delivery are controlled fixture inputs. The received
peer-yield frame is applied by the actual receiver service, and its authority ID
must match the resulting paired receipt.

The integration wires `RoutePublicationAuthorityForwarder` to the production
`ForwardCoordinatorIssuedRoutePublicationAuthorityLocked` method, as normal
service context initialization does. The service's original notifications are
passed unchanged to the real NKN transport. The actual accepted compatibility
projection emits the second-Tuna `filetransfer_route_transitioned` record; the
fixture does not invoke the logging function directly or insert a matching row.

Only then is the original regular-v4 packet delivered twice. The first is
rejected before dispatch and the second deduplicated. Receiver transport state,
service state fingerprint, service frame count, route-hint count and transition
rows remain unchanged. No payload reaches the data-session consumer.

## Fixture issue found during integration

The first attempt reached a valid service commit but could not find its
epoch-3 transport notification. The standalone C2f context had no publication
forwarder: its scoped Snapshot test did not consume transport notifications.
Waiting did not produce a notification. Source inspection confirmed that normal
service initialization installs this delegate synchronously. Adding that exact
wiring only for the new integration fixed the omitted fixture edge; no runtime
fallback or fabricated hint was introduced.

The first completed primary run passed all 56 full and compact Snapshot
boundaries and reported `missing_tuna_resend`. An initial test expectation of
`missing_exact_tuna_receive` was corrected: the existing classifier checks for
the resend first, and only reports the missing receive when a resend exists.

## Verification contract

Both PowerShell hosts must use the original three authority evaluations,
complete full/compact Snapshot history and parity, and the primary-owned exact
failure witness. Post-run assertions read already-produced artifacts only.
They verify original writer startup/drain, contiguous writer sequences, exact
merged-record preservation, matching delayed-packet/security provenance,
matching yield-packet provenance across the actual sender/receiver writers,
and the exact current receiver transition tuple before the delayed rejection.

Detailed execution results are recorded in
`artifacts/publication-service-ingress-integration-20260915/RESULTS.md`.

Verification passed: **11/11 affected tests**, then **2/2 targeted reruns** after
tightening the dispatch-preservation assertion. Both PowerShell hosts pass the
new integration and the retained negatives. Each new integration analysis has
56 full/compact boundaries, passing parity, and exactly three authority
evaluations. No aggregate suite, live test or commit was run.

## Next bounded step

Add a separate controlled exact-range Tuna payload case: first establish an
authorized current sender Tuna publication, then submit and receive the same
range on the actual test Tuna path before releasing the old regular packet.
Retain missing-resend and missing-exact-receive controls, and observe the original
primary's result without a second authority evaluation. Do not substitute an
accepted queue, a bridge observation, fallback delivery or SHA success for the
required Tuna send/receive. If the transport prerequisite requires a live run,
stop at the next live-256 gate; do not launch it automatically.

C2i does not reclassify C2a's live failures or claim the live route cycle passes.

## C2j continuation

The controlled exact-Tuna payload case and its missing-receive/wrong-range
controls are implemented in `docs/file-transfer-tuna-payload-integration-rca.md`.
The original primary recovers only the exact send/receive case. The next runtime
gate is targeted live-256 validation, not a change to the recovery predicate.
