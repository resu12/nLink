# Scoped native source-capture budget

The authorized isolated change gives an originally attached frontier worker a
fixed **60,000 ms cumulative source-capture allowance**. This is a resource-policy
revision, not an optimization, route-authority grant or acceptance-policy change.

The latest `route-cycle-facts-header1` attempt exhausted 20,000 ms after
157,723 of 173,987 source lines, before EOF. Same-shape linear read extrapolation
is approximately 22.06 seconds; it does not measure completed origin traversal
or initial registration, nor establish a worst-case bound. Sixty seconds leaves
about 2.7 times that read estimate for those operations and host/load variation.
It is finite, fixed and not machine-tuned. Simultaneous maximum record, byte,
line and facts sizes are not guaranteed to complete; expiry still fails closed.

| Phase | Cumulative worker ceiling |
| --- | ---: |
| Original attachment, facts decoding, initial pin/pair pulling and final attachment admission | 20,000 ms, unchanged |
| After successful attachment: source open/begin, fragments, every line and EOF publication, currentness/origins and initial evaluation registration | 60,000 ms |
| After completed source and successful original evaluation registration: existing retained consumers | 180,000 ms, unchanged |

All phases use the same original Stopwatch. Sixty seconds is not 20 + 60;
180 seconds includes earlier work. There is no reset, refill, replacement clock,
public limit parameter, retry, source reopening or fresh evaluation after expiry.
The attached flag selects an allowance only; original worker/reader references,
one-shot state, completed EOF, original strings/ordinals and every fresh base,
scope, file/facts and native-pin predicate remain mandatory. No check is sampled
or cached. Full entry/publication/exit validation and abort-only fragment checks
retain their original roles and cadence. The diagnostic profile uses the same
explicit source ceiling as normal code.

Shared `ProducerMilliseconds` remains 20,000: startup/rendezvous, parent-owned
clocks, legacy publication APIs and attachment do not inherit this change.
Existing retained content/evaluation visit/count/code-unit/byte limits,
watchdogs, route timing, retry/concurrency and acceptance limits are unchanged.
Coordinator-issued authority remains the only authority; observations, ownership
checks and resource allowance cannot grant route, repair or publication rights.

Focused verification covers both PowerShell hosts, stopped-clock exact limits
and plus-one rejection, reading/EOF/origins beyond the old ceiling, initial
registration with inherited work, retained expiry/no revival, copied/stale
resources and unchanged false authority flags. Synthetic elapsed values are
small-test controls only, never scale or composition evidence. The prior failed
composition remains failed; no successful end-to-end or release gate is inferred.
