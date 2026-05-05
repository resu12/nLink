# NKN Tuna Phase 0 POC Runbook

This is a standalone proof of concept. It must not be treated as an nLink transport integration and it must not be used with real support-session payloads.

## Build

```powershell
go -C tools/nkn-tuna-poc build -o ..\..\artifacts\tuna-poc\nlink-tuna-poc.exe .
```

## Safety Rules

- Use a low-balance test wallet only.
- Link `wallet.json` by path; do not copy it into the repo or artifacts.
- Use `--password-prompt`; the tool has no password environment variable.
- `--password-stdin` exists only for local automation where an interactive console is unavailable. Do not put the password in scripts, logs, or committed files.
- Use `--allow-remote` for the exact expected dialer address. Use `--allow-remote-regex` only when intentionally allowing a public-key pattern.
- Use `--unsafe-allow-any` only for a short local smoke test.
- Always set listener caps: `--max-price-nkn-per-mb`, `--max-total-mib`, and `--max-duration-sec`.

## Address Discovery

Create a new low-balance test wallet for the POC:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe create-wallet `
  --out "artifacts\tuna-poc\wallet-test-nkn.json" `
  --password-prompt `
  --jsonl
```

Fund only the `walletAddress` printed by this command with a small amount for testing.

For a listener that uses the payment wallet identity:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe address `
  --wallet "C:\path\wallet.json" `
  --password-prompt `
  --jsonl
```

For a stable dialer identity, provide a temporary 32-byte hex seed to both `address` and `dial`/`baseline --role dial`:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe address `
  --seed-hex "<32-byte-hex-seed>" `
  --jsonl
```

The seed is never persisted by the tool. Keep it out of logs and chat.

## Baseline NKN Session Benchmark

Listener:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe baseline `
  --role listen `
  --allow-remote "<dialer-address>" `
  --max-total-mib 64 `
  --max-duration-sec 120 `
  --accept-timeout-sec 90 `
  --jsonl
```

Dialer:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe baseline `
  --role dial `
  --to "<listener-address>" `
  --profile file `
  --duration-sec 60 `
  --dial-timeout-ms 60000 `
  --max-total-mib 64 `
  --write-size 32768 `
  --file-ack-every-kib 32 `
  --file-inflight-kib 128 `
  --file-pace-mbps 4 `
  --jsonl
```

## Tuna Benchmark

Listener:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe listen `
  --wallet "C:\path\wallet.json" `
  --password-prompt `
  --allow-remote "<dialer-address>" `
  --max-price-nkn-per-mb "0.0002" `
  --max-total-mib 64 `
  --max-duration-sec 120 `
  --accept-timeout-sec 90 `
  --jsonl
```

Dialer:

```powershell
.\artifacts\tuna-poc\nlink-tuna-poc.exe dial `
  --to "<listener-address>" `
  --profile file `
  --duration-sec 60 `
  --dial-timeout-ms 60000 `
  --max-total-mib 64 `
  --write-size 32768 `
  --file-ack-every-kib 32 `
  --file-inflight-kib 128 `
  --file-pace-mbps 4 `
  --jsonl
```

Repeat for `--profile screen`, `--profile ping`, and `--profile mixed`.

For the file profile, start with bounded ACK pacing before testing raw burst mode. `--file-ack-every-kib` controls how often the listener acknowledges received file data, `--file-inflight-kib` caps unacknowledged bytes on the dialer, and `--file-pace-mbps` optionally caps sender rate. Passing `--file-inflight-kib 0 --file-ack-every-kib 0 --file-pace-mbps 0` restores the original burst-style stress test.

The POC treats the paid Tuna listener as a short-lived sidecar process. After Tuna listening has started, it avoids calling `nkn-tuna-session` listener shutdown because v0.2.6 has been observed to panic during close on Windows; the operating system reclaims resources when the POC process exits.

The benchmark frame writer sends each synthetic frame as one contiguous header+payload buffer and loops until the full frame is accepted. Partial or split frame writes would corrupt the synthetic stream and make throughput or latency numbers meaningless.

For automated runs, set `--accept-timeout-sec` on listeners so a failed dial attempt cannot leave a benchmark listener waiting forever.

## Artifacts

Each completed run writes a summary JSON file under:

```text
artifacts/tuna-poc/<utc-timestamp>/
```

The artifact summary contains transport/profile counters only. It must not contain wallet passwords, wallet seeds, private keys, or full wallet paths.

## Go / No-Go Reading

Proceed to Phase 1 only if:

- Windows build is clean.
- Tuna connects reliably with the linked low-balance wallet.
- Caps stop the run without a stuck process.
- Baseline and Tuna summaries are comparable.
- File throughput improves by roughly 25% or screen-like paced delivery is materially smoother without severe `mixed` ping p95 regression.
