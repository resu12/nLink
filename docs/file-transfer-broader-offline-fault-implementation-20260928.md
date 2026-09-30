# Small offline frontier schedule: implementation and characterization

Date: 2026-09-28  
Branch: `v0.7.0`; HEAD: `a653d4063070235c71fe211480aeb37863d9b2a4`  
Result: approved small-profile milestone passed; no live-256 or diagnostic acceptance claimed.

## Scope and implementation

The user's latest approval authorizes the exception, bounded implementation and one small offline characterization proposed in `file-transfer-broader-offline-fault-contract-review-20260928.md`.

- Added a separate `frontier-schedule` Debug-only profile: 172,032 bytes, eight existing 21 KiB chunks, and the existing one-segment environment override scoped/restored inside the native child. Developer mode is not enabled.
- Added a default-off internal envelope observer. It exposes only immutable session/transfer, repair classification, chunk geometry, epoch and encrypted-envelope length/hash. It cannot return a routing decision, mutate a payload, supply authority, or change the send result through a callback exception.
- Added bounded ordinary-frame descriptor correlation and opt-in pipe framing. The original peer/process checks, real write/flush, encrypted bytes and normal application callbacks remain in use. Existing profiles keep their framing and selection branches.
- Added receive-side drop/hold scheduling: drop ordinary 0; hold 1, 2, 4; pass 3, 5, 6, 7; leave repair/control traffic unchanged. Duplicate ordinary frames fail. Three frames/1 MiB retained aggregate, eight descriptors/1 KiB each, and original three-second per-frame deadlines are enforced.
- Releases use read-only original-context observations under the service gate. The sequence requires the genuine exact repair, recent grace/deferred event, original recheck, pre-deadline write and closed-positive completion. No timer, authority, receive algorithm, coordinator, analyzer or acceptance rule changed.

Only four existing files were modified: the envelope-construction hook, test pipe client, carrier profile and native-child dispatch. Five implementation/test files were added. No commit or push was performed.

## Verification

Focused tests passed: 21 new scheduler/profile tests, 6 metadata/transport/authority tests and 8 existing frame-hold regressions. The final scheduler and metadata follow-ups reran only their directly affected tests. The Debug native harness was rebuilt before the single escalated characterization; no portable GUI package or live network run was needed.

The first compile attempt needed a missing namespace import. A later negative test incorrectly expected rejection from the legacy no-authority send overload; source inspection located the actual authority-checking overload, and the corrected stale-authority regression passes. Neither issue prompted a runtime change. Logs retain those intermediate failures as well as the final passing results.

Characterization test: `OriginalApps_CharacterizeSingleSegmentFrontierSchedule`; **1/1 passed**, approximately 28 seconds, one original-pair run only.

Run directory: `artifacts/broader-offline-fault-implementation-20260928/pwsh-frontier-schedule-2e1f240a326e4c889b7f147286a3ab26`.

Original operational log: 1,649 lines; SHA-256 `716bc2be2296b81bae423a495d380d5f43f7fce2e91e1912f13ccbb57d9c7d2e`.

## Original proof sequence

| Observation | Original evidence |
| --- | --- |
| Exact eight original one-chunk envelopes; one drop and three holds | Peer stdout records matching envelope hashes; each encrypted envelope is 21,921 bytes. Receiver reaches frontier 0/highest 7 before repair. |
| Real repair 0 -> 1, range [0,1), original continuity | Operational log line 1241, receiver writer sequence 564; recent grace at line 1243. |
| Fresh below-highest write after releasing 2 | Line 1300: `filetransfer_v6_receiver_state_deferred`, `reason=frontier_stalled`, frontier 1/highest 7, within original recent grace. |
| Real recent recheck and original stall root | Lines 1371 and 1376: root `09:38:29.2177811Z`, deadline `09:38:29.9677811Z`. |
| Release 1 produces real 1 -> 4 progress | Original committed-progress time `09:38:29.2246263Z` (line 1395): 6.8452 ms after root, 743.1548 ms before deadline. Chunk 4 remains held. |
| Accepted original publication completion | Line 1444, receiver sequence 661: original snapshot decodes to `FallbackControlPlaneSendCompleted`, reason `chunk_batch_committed`, status `Accepted`, decision time `09:38:29.504Z`. Line 1445 records its delivery observation. |
| Closed-positive producer before final release | Receiver stdout line 125: `PublishedBeforeOriginalDeadline`; the test checks original observation closure and first-write timing before releasing 4. |
| Native marker and writer shutdown | Line 1648: one 6,537-byte frontier payload marker; sequence 661 completion hash matches the exact original UTF-8 completion record (without newline). Writer drains are lines 1647/1649. |

Both original peers completed the 172,032-byte transfer with SHA-256 `67331c29dda482392a6413e0b35c3a7fa7692b5db1bfd1496da5575752f49a0b`. The saved native destination matches. The original GUI-owned writer shutdown/adoption, two-writer closure matching, role binding and source-custody checks passed (`attempt-role-writers-ok`, `attempt-source-custody-ok`). Finalizer cleanup retired the original custody objects. The two native children and GUI child exited; the received file could be opened exclusively afterward.

The original fixture still creates/holds its canonical 256 MiB source and retains a canonical scenario projection in wrapper logs. **Only 168 KiB was transferred in this offline profile.** Those wrapper labels are not a live-256 result, a default-three-segment test, or proof of a second Tuna route publication.

## Remaining boundary / recommended next step

This closes the previously missing original native-observation and writer-handoff milestone. It does not establish the full primary diagnostic integration: this profile does not invoke retained primary analysis, select 1-64 exact primary-owned failure witnesses, or export a completed diagnostic result through the separate payload wrapper/parent path.

The observed outcome is naturally `PublishedBeforeOriginalDeadline`. Do not delay publication or manufacture an unresolved failure to qualify it for another contract. Next, review how to connect this positive native profile to the existing diagnostic custody/primary-selection path while preserving its honest no-supported-failure outcome. Any additional characterization requires its own bounded scope; no second run, live-256, full matrix, acceptance relaxation or production timing fix is inferred from this success.

Preservation and exact read-only evidence are recorded under `artifacts/broader-offline-fault-implementation-20260928/`. All prior files outside the four scoped edits and rebuilt harness remain hash-preserved; the prior approved proposal and original negative run are unchanged.
