# nkn-bridge

Minimal Node.js bridge for the official NKN JS SDK (`nkn-sdk-js` aliasing npm package `nkn-sdk`).

It reads line-delimited JSON commands from `stdin` and writes line-delimited JSON events/results to `stdout`.

## How to run

```powershell
cd tools\nkn-bridge
npm install
npm start
```

## Protocol (JSONL)

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
- `shutdown`

## Events / responses on stdout

Only JSON lines are written to `stdout`.

- Events:
  - `{"event":"ready","address":"..."}`
  - `{"event":"message","source":"...","payloadBase64":"...","isTopic":false}`
  - `{"event":"disconnected","reason":"..."}`
- Command responses:
  - `{"event":"ok","id":"...","cmd":"..."}`
  - `{"event":"error","id":"...","cmd":"...","reason":"..."}`

## Notes

- Human-readable logs go to `stderr` only.
- `payloadBase64` is used so binary payloads can pass through safely.
- The bridge is intentionally minimal and keeps a single active client connection.
