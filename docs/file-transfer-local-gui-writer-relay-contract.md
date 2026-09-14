# C1p: one-hop accepted GUI writer relay

2026-09-14, branch `v0.7.0`, base HEAD `ef78e9d7`, with accumulated uncommitted
work. This is an opt-in offline ownership integration, not a live export gate.

## First missing edge and bounded fix

C1h already adopts a genuine GUI finalizer completion in the Wrapper process.
C1o can adopt a primary publication after the retained parent's original terminal
receipt. The RouteAcceptance launcher previously returned only the Wrapper exit
code; it could not carry a typed accepted GUI completion across that process
boundary. Manifest/ledger JSON and copied hashes cannot repair that custody gap.

The optional launcher now creates a parent-owned relay request before starting
its exact Wrapper child. It retains a duplicated process handle, bound PID and
creation time. The Wrapper attaches once to that request under the same actual
run/scenario/artifact root, before launching its GUI child. Missing or stale
requests do not affect ordinary GUI work.

The actual hard-deadline GUI parent keeps its request, completion output and relay
capability private. Only after its existing successful GUI exit, output capture
and `gui_child_exited` state persistence does it take the original GUI completion
and forward it. The relay accepts only that genuine CLR completion, marked as
adopted in this process. A decoded, copied, relayed or caller-authored substitute
does not have that private local adoption mark. The mark is not serialized.

This is trusted callsite custody, not authentication against malicious same-account
code, explicit reflection or raw named-mapping manipulation. The actual private
GUI parent must remain the only production caller forwarding its fresh accepted
completion; possession of matching public metadata is not an issuer capability.

## Separate process identities and binary format

The relay reuses the existing bounded C1h request/attachment/process-handle code,
but has a distinct `Local\nlink-gui-relay-...` namespace, magic `0x4e475232`,
attachment counter and typed issuer. A finalizer cannot attach to the relay as
a GUI writer or issue its scalar completion through it. Wrong take mode consumes
and omits the request, rather than interpreting one protocol as another.

The relay payload is the original assembly-private `Completion.WriteTo` record:
original GUI request ID, GUI PID/creation time, artifact root and already bounded
original binary completion body. No JSON serializer, file reread/hash, authority
evaluation, synthetic line or reconstructed view is involved.

RouteAcceptance accepts the relay only after complete Wrapper output capture,
its original output presentation, and an exact zero exit from the held Wrapper
handle. A committed record followed by nonzero Wrapper exit is withheld. Early,
repeated, cancelled, malformed, partial, wrong-process or wrong-scope takes omit
the optional result. Normal Wrapper cleanup closes its endpoint without deleting
a committed record; parent disposal seals and releases the channel.

The immutable `RelayedCompletion` keeps the relay request and Wrapper PID/creation
separate from `WriterContext`'s original GUI identity. It exposes no public
constructors or setters. The decoded writer context can enter the existing exact
source association, but is not marked as a fresh local GUI adoption and cannot
be forwarded as a second hop.

## Opt-in workflow and primary compatibility

`Invoke-RouteAcceptanceChildScriptNoThrow` accepts an optional empty relay output
reference and artifact root. Without these, or if request setup is unavailable,
its original native PowerShell invocation remains unchanged. The opt-in branch
uses an owned, hidden process with concurrently drained stdout/stderr. It retains
all output text and per-stream line order; cross-stream interleaving is not a
contract. Existing Wrapper GUI deadlines and cleanup remain the lifetime owner;
no new watchdog, timeout extension, grace period or retry is introduced.

The Wrapper main script attaches only to a valid parent-issued request when its
retained owner is RouteAcceptance. An environment string alone cannot enable a
relay: exact process/request/parent lifetime checks remain mandatory. Relay
environment variables are removed from GUI child startup, preventing inheritance
into the next process level. No CLI switch enables publication.

`Invoke-TunaRouteAcceptanceRun` accepts an optional empty `LocalPublication`
reference. Its normal single-Wrapper branch requests a relay and passes the exact
returned writer context to the existing retained analysis/source-capture/adoption
seam. No returned context means ordinary analysis. Prefilled outputs are untouched.
Wrapper failure still prevents retained analysis; failed analysis still prevents
manifest reconciliation. A proof-incomplete diagnostic may survive through the
caller reference while the original proof failure is thrown.

The restart/fallback retry branch remains disconnected. Direct Wrapper-owned
analysis, secondary-transfer/derived sources, and top-level live callers are not
enabled by this slice. Existing milestone merging or synthetic augmentation is
not removed to make an input eligible: the exact C1i source association must
succeed, otherwise the optional publication is omitted.

## Bounds and verification

The relay keeps the original fixed 16-KiB request and 64-KiB completion capacity.
The typed forwarding and adoption steps each have a cooperative 250-ms metadata
budget; all mutex acquisition remains zero-wait. Original C1h metadata/writer
bounds remain unchanged. These bounds cover the diagnostic relay, not the
existing primary stdout/stderr capture or GUI process lifetime.

Focused tests exercise the actual launcher and actual GUI hard-deadline parent
with a controlled offline finalizer child. They verify separate process identity,
scope, private custody, typed source-request consumption, output/argument
preservation and negative lifecycle cases. Workflow-order tests use the actual
RouteAcceptance function with explicitly controlled analyzer/manifest callbacks;
their marker is not a real publication or a live transfer result.

Verification passed ten unique xUnit tests plus six focused protocol/output
cases. Results: `artifacts/publication-gui-relay-20260914/RESULTS.md`.
No live GUI/NKN/256 MiB run, portable rebuild, commit or push is part of this step.

## Next verification boundary

The relay/lifecycle fixture and the existing primary publication fixture verify
their respective real ownership code, but are not one complete invocation of
the Wrapper main script, retained primary report writer and parent adoption.
The next bounded check is that combined offline integration with real primary
reports, including a genuine primary-selected failed-proof witness. A synthetic
workflow marker cannot stand in for that gate. Only afterward should top-level
opt-in consumption/persistence be considered. No live caller is enabled yet;
another live 256 MiB test is not required by this relay-only gate.

C1q has now fixed two real report-generation blockers and verified the combined
relay-to-real-reports **omission** path on both PowerShell versions. The complete
Wrapper main plus positive first-failure witness gate was still open then; see
[the real-report integration RCA](file-transfer-real-report-integration-rca.md)
and `artifacts/publication-offline-integration-20260914/RESULTS.md`.

C1r now completes the full-Wrapper/real-reports positive adoption gate on both
hosts, while preserving the failed acceptance verdict and original live-proof
rejection. See [the positive integration result](file-transfer-positive-publication-integration.md)
and `artifacts/publication-positive-integration-20260914/RESULTS.md`.
Top-level opt-in persistence remains disabled and is the next bounded step.
