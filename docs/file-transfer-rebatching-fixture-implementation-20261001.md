# Rebatching fixture implementation and verification

2026-10-01. Base checkpoint `7ddbe15c3b894cf2497207cc39a08189b7578cc0`.
This implements the test-only slice in the
[contract review](file-transfer-rebatching-contract-review-20261001.md).

## Result

The two-process adapter fixture now holds one Regular-v4 chunk, sends a
three-chunk Tuna-v4 batch under the second publication, receives that batch
through the data-session API, and then releases the held packet. The existing
checks confirm that rejection adds no dispatch and leaves the captured adapter
state unchanged.

The receiver privately decrypts the exact held wire and compares its decoded
segment with the actual Tuna segment. In `rebatch_same`, the bytes match. In
`rebatch_changed`, one byte differs at the same chunk index while lengths,
range and batch count remain identical. Both cases hold chunk 12 with 1,024
bytes and receive chunks 12 through 14 with 3,072 bytes.

Both cases retain the original primary result `missing_tuna_resend`, with
`Recovered=false`. The fixture comparison is separate test evidence; it does
not supply authority or change acceptance. Its JSON records the comparison
result, held encrypted-payload fingerprint, shape and receiver process, without
exporting plaintext or keys. The result is written only after the existing
rejection checks finish.

## Verification

Nine distinct focused and affected cases were verified:

- The two new rebatching cases, each processed on PowerShell 5.1 and 7.
- The three existing strict exact-send-and-receive cases: exact, missing
  receive and wrong range, on both hosts.
- The two queued Regular rejection provenance cases.
- The two independent Tuna-pair cases, on both hosts. The positive pair case
  also passed its existing 17 detached negative controls per host.

The new cases retain three original authority evaluations per host, one selected
ingress candidate, original source custody and full/compact parity. No second
correlator or authority evaluation was added.

The first `rebatch_same` attempt failed the existing optional publication
adoption budget: 625 ms exceeded its 250 ms bound. The private byte comparison
had completed successfully. A targeted rerun passed on both hosts; the initial
failure receipt remains retained. No timing limit was changed. The changed-byte
case passed on its first attempt, and all seven existing cases passed together.

The [verification receipt](../artifacts/rebatching-implementation-20261001/verification.json)
pins the modified test sources, test results and original analysis artifacts.
It also reverified all 99 original live-artifact pins. Runtime sources, tools,
acceptance and the historical live verdict remain unchanged. `git diff --check`
passed. This slice required no portable rebuild, live run or full matrix.

## Remaining evidence gap

Private fixture equality does not establish equality in historical production
logs or prove durable storage. Production evidence still lacks a content
identity that can connect differently batched encrypted sends.

The next bounded step is to review the proposed sender-local content commitment
in the contract review, including privacy, secret lifetime, capture atomicity
and logging bounds. Production capture and acceptance adoption remain separate
implementation decisions. No new live test is needed for that review.

Changes are local and uncommitted. No push was performed.
