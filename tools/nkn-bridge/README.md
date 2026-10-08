# nkn-bridge

Minimal Node.js bridge for the official NKN JS SDK (`nkn-sdk-js` aliasing npm package `nkn-sdk`).

It uses JSONL for commands/control/status and bounded binary frames for app payload delivery over `stdin`/`stdout`. This is the regular NKN bridge, not the Go Tuna sidecar. See [file-transfer architecture](../../docs/file-transfer-implementation.md) for application route/security boundaries.

## How to run

```powershell
cd tools\nkn-bridge
npm ci
npm start
```

Release bridge bundles are built through `installer\Build-BridgeBundle.ps1`, which restores from `package-lock.json`, bundles `index.js` with `ncc`, verifies the pinned Node runtime archive, and ships no `node_modules` tree.

## Control Protocol (JSONL)

Send one JSON object per line to `stdin`.

Example commands:

```json
{"id":"1","cmd":"connect","identifier":"nlink-test"}
{"id":"2","cmd":"subscribe","topic":"demo-topic"}
{"id":"3","cmd":"publish","topic":"demo-topic","payloadBase64":"SGVsbG8="}
{"id":"4","cmd":"send","destination":"<nkn-address>","payloadBase64":"SGVsbG8="}
{"id":"5","cmd":"unsubscribe","topic":"demo-topic"}
{"id":"6","cmd":"shutdown"}
```

## Supported commands

- `connect` with optional:
  - `seedHex`
  - `seedBase64`
  - `identifier`
  - `seedRpc`
- `subscribe` with `topic`
- `unsubscribe` with `topic`
- `publish` with `topic`, `payloadBase64`
- `send` with `destination`, `payloadBase64`
- `hello` / `ping` for bridge protocol/readiness checks
- `setScreenSharePolicy` / `setBulkSendPolicy` for app-issued queue policies
- `shutdown`

## Events / responses on stdout

Control/status responses are JSONL. Payload messages may use the binary frame path; do not parse all stdout as text or inject human-readable output there.

- Events:
  - `{"event":"ready","address":"..."}`
  - `{"event":"message","source":"...","payloadBase64":"...","isTopic":false}`
  - `{"event":"disconnected","reason":"..."}`
- Command responses:
  - `{"event":"ok","id":"...","cmd":"..."}`
  - `{"event":"error","id":"...","cmd":"...","reason":"..."}`

## Notes

- Human-readable logs go to `stderr` only.
- `payloadBase64` is the JSON command form; the app also uses binary payload framing with a `64 KiB` payload ceiling and bounded text/body lengths.
- The bridge has separate control/media/bulk lanes with NKN MultiClients (default subclient counts `4/8/4`, bulk send concurrency `4`). These observations/queue accepts are not route authority or end-to-end delivery proof.
- File-transfer route commits, runtime unlock, fallback, and recovery effects remain owned by `FileTransferCoordinator`; the bridge cannot grant them. App session-envelope validation and SHA/terminal checks remain required regardless of transport.
- Direct bridge network experiments require live connectivity outside restricted sandboxes with explicit escalation when applicable. They do not qualify authenticated Tuna application delivery.
