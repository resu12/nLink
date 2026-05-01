'use strict';

const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');

const HEADER_SIZE = 16;
const FRAME_MAGIC = 0x00;
const PROTOCOL_VERSION = 2;
const KIND_SEND = 1;
const KIND_MESSAGE = 2;
const CHANNELS = { control: 0, media: 1, bulk: 2 };

function parseArgs(argv) {
  const args = {
    nodePath: process.execPath,
    bridgePath: path.join(__dirname, 'nkn-bridge', 'index.js'),
    artifactDir: path.join(process.cwd(), 'artifacts', 'nkn-bridge-receive-probe', timestamp()),
    durationSeconds: 60,
    intervalMs: 1000,
    payloadBytes: 1024,
    mediaPayloadBytes: null,
    bulkPayloadBytes: null,
    bulkSendConcurrency: null,
    bulkBurstFrames: 1,
    bulkOnly: false,
    oneWayBulk: false,
    ignoreStdinBackpressure: false
  };

  for (let i = 0; i < argv.length; i++) {
    const key = argv[i];
    const value = argv[i + 1];
    if (key === '--node' && value) { args.nodePath = value; i++; }
    else if (key === '--bridge' && value) { args.bridgePath = value; i++; }
    else if (key === '--artifact-dir' && value) { args.artifactDir = value; i++; }
    else if (key === '--duration-seconds' && value) { args.durationSeconds = Math.max(5, Number(value) || args.durationSeconds); i++; }
    else if (key === '--interval-ms' && value) { args.intervalMs = Math.max(100, Number(value) || args.intervalMs); i++; }
    else if (key === '--payload-bytes' && value) { args.payloadBytes = Math.max(1, Math.min(60 * 1024, Number(value) || args.payloadBytes)); i++; }
    else if (key === '--media-payload-bytes' && value) { args.mediaPayloadBytes = Math.max(1, Math.min(60 * 1024, Number(value) || args.payloadBytes)); i++; }
    else if (key === '--bulk-payload-bytes' && value) { args.bulkPayloadBytes = Math.max(1, Math.min(60 * 1024, Number(value) || args.payloadBytes)); i++; }
    else if (key === '--bulk-send-concurrency' && value) { args.bulkSendConcurrency = Math.max(1, Math.min(8, Number(value) || 1)); i++; }
    else if (key === '--bulk-burst-frames' && value) { args.bulkBurstFrames = Math.max(1, Math.min(64, Number(value) || 1)); i++; }
    else if (key === '--bulk-only') { args.bulkOnly = true; }
    else if (key === '--one-way-bulk') { args.oneWayBulk = true; }
    else if (key === '--ignore-stdin-backpressure') { args.ignoreStdinBackpressure = true; }
  }

  args.mediaPayloadBytes = args.mediaPayloadBytes || args.payloadBytes;
  args.bulkPayloadBytes = args.bulkPayloadBytes || args.payloadBytes;
  return args;
}

function timestamp() {
  const d = new Date();
  const pad = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`;
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function buildFrame(destination, payload, channel) {
  const primary = Buffer.from(destination, 'utf8');
  const bodyLength = primary.length + payload.length;
  const frame = Buffer.alloc(HEADER_SIZE + bodyLength);
  frame[0] = FRAME_MAGIC;
  frame[1] = PROTOCOL_VERSION;
  frame[2] = KIND_SEND;
  frame[3] = CHANNELS[channel] || 0;
  frame[4] = 0;
  frame[5] = 0;
  frame.writeUInt16LE(primary.length, 6);
  frame.writeUInt16LE(0, 8);
  frame.writeInt32LE(payload.length, 10);
  primary.copy(frame, HEADER_SIZE);
  payload.copy(frame, HEADER_SIZE + primary.length);
  return frame;
}

function decodeFrame(buffer) {
  if (buffer.length < HEADER_SIZE || buffer[0] !== FRAME_MAGIC || buffer[1] !== PROTOCOL_VERSION) {
    return null;
  }

  const primaryLength = buffer.readUInt16LE(6);
  const secondaryLength = buffer.readUInt16LE(8);
  const payloadLength = buffer.readInt32LE(10);
  const totalLength = HEADER_SIZE + primaryLength + secondaryLength + payloadLength;
  if (buffer.length < totalLength) {
    return null;
  }

  const payloadStart = HEADER_SIZE + primaryLength + secondaryLength;
  return {
    totalLength,
    kind: buffer[2],
    channel: buffer[3] === 1 ? 'media' : buffer[3] === 2 ? 'bulk' : 'control',
    source: buffer.subarray(HEADER_SIZE, HEADER_SIZE + primaryLength).toString('utf8'),
    payloadLength
  };
}

class BridgeProbeClient {
  constructor(name, options, eventWriter) {
    this.name = name;
    this.options = options;
    this.eventWriter = eventWriter;
    this.buffer = Buffer.alloc(0);
    this.ready = null;
    this.helloOk = false;
    this.errors = [];
    this.stderr = [];
    this.sent = { control: 0, media: 0, bulk: 0 };
    this.sentBytes = { control: 0, media: 0, bulk: 0 };
    this.received = { control: 0, media: 0, bulk: 0 };
    this.receivedBytes = { control: 0, media: 0, bulk: 0 };
    this.health = [];
    this.stdinClosed = false;
  }

  start() {
    this.process = spawn(this.options.nodePath, [this.options.bridgePath], {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      env: { ...process.env }
    });
    this.process.stdout.on('data', chunk => this.onStdout(chunk));
    this.process.stdin.on('error', error => {
      this.stdinClosed = true;
      const message = `stdin_error:${error && error.code ? error.code : error.message}`;
      this.errors.push(message);
      this.writeEvent({ event: 'stdin_error', client: this.name, code: error && error.code ? error.code : null, message: String(error && error.message ? error.message : error), ts: Date.now() });
    });
    this.process.stderr.on('data', chunk => {
      for (const line of chunk.toString('utf8').split(/\r?\n/)) {
        if (line.trim()) {
          this.stderr.push(line.trim());
          this.writeEvent({ event: 'stderr', client: this.name, line: line.trim(), ts: Date.now() });
        }
      }
    });
    this.process.on('exit', (code, signal) => {
      this.writeEvent({ event: 'process_exit', client: this.name, code, signal, ts: Date.now() });
    });
  }

  writeEvent(evt) {
    this.eventWriter.write(JSON.stringify(evt) + '\n');
  }

  sendJson(obj) {
    if (this.stdinClosed || !this.process || !this.process.stdin.writable) {
      this.errors.push('stdin_closed:json');
      return false;
    }

    this.process.stdin.write(JSON.stringify(obj) + '\n');
    return true;
  }

  sendFrame(destination, payload, channel) {
    if (this.stdinClosed || !this.process || !this.process.stdin.writable) {
      this.errors.push(`stdin_closed:${channel}`);
      return false;
    }

    const frame = buildFrame(destination, payload, channel);
    this.sent[channel] += 1;
    this.sentBytes[channel] += payload.length;
    const accepted = this.process.stdin.write(frame);
    if (!accepted) {
      this.writeEvent({ event: 'stdin_backpressure', client: this.name, channel, frame_bytes: frame.length, ts: Date.now() });
    }

    return accepted;
  }

  async waitForDrain(timeoutMs = 2000) {
    if (!this.process || !this.process.stdin.writable || this.stdinClosed || !this.process.stdin.writableNeedDrain) {
      return;
    }

    await new Promise(resolve => {
      let settled = false;
      const onDrain = () => {
        if (settled) {
          return;
        }

        settled = true;
        clearTimeout(timer);
        resolve();
      };
      const timer = setTimeout(() => {
        if (settled) {
          return;
        }

        settled = true;
        this.process.stdin.off('drain', onDrain);
        resolve();
      }, timeoutMs);
      this.process.stdin.once('drain', onDrain);
    });
  }

  onStdout(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length > 0) {
      if (this.buffer[0] === FRAME_MAGIC) {
        const frame = decodeFrame(this.buffer);
        if (!frame) {
          return;
        }

        this.buffer = this.buffer.subarray(frame.totalLength);
        if (frame.kind === KIND_MESSAGE) {
          this.received[frame.channel] += 1;
          this.receivedBytes[frame.channel] += frame.payloadLength;
          this.writeEvent({ event: 'message', client: this.name, channel: frame.channel, payload_bytes: frame.payloadLength, ts: Date.now() });
        }
        continue;
      }

      const newline = this.buffer.indexOf(0x0a);
      if (newline < 0) {
        return;
      }

      const line = this.buffer.subarray(0, newline).toString('utf8').trim();
      this.buffer = this.buffer.subarray(newline + 1);
      if (!line) {
        continue;
      }

      try {
        const obj = JSON.parse(line);
        this.handleJson(obj);
      } catch (error) {
        this.errors.push(`json_parse_failed:${error.message}`);
      }
    }
  }

  handleJson(obj) {
    this.writeEvent({ ...obj, client: this.name, ts: Date.now() });
    if (obj.event === 'hello_ok') {
      this.helloOk = true;
    } else if (obj.event === 'ready') {
      this.ready = obj;
    } else if (obj.event === 'bridge_transport_health_summary') {
      this.health.push(obj);
    } else if (obj.event === 'error') {
      this.errors.push(String(obj.reason || 'bridge_error'));
    }

  }

  async waitFor(predicate, timeoutMs, description) {
    const started = Date.now();
    while (!predicate()) {
      if (Date.now() - started > timeoutMs) {
        throw new Error(`${this.name} timed out waiting for ${description}`);
      }

      await delay(50);
    }
  }

  async connect(identifier) {
    this.sendJson({ cmd: 'hello', id: `${this.name}-hello`, protocol: 2 });
    await this.waitFor(() => this.helloOk, 5000, 'hello_ok');
    const connect = { cmd: 'connect', id: `${this.name}-connect`, identifier };
    if (optionsNumber(this.options.bulkSendConcurrency) > 0) {
      connect.bulkSendConcurrency = this.options.bulkSendConcurrency;
    }

    this.sendJson(connect);
    await this.waitFor(() => this.ready && this.ready.controlAddress && this.ready.mediaAddress && this.ready.bulkAddress, 45000, 'ready');
  }

  async shutdown() {
    try {
      this.sendJson({ cmd: 'shutdown', id: `${this.name}-shutdown` });
    } catch {
      // best effort
    }

    await delay(500);
    if (this.process && !this.process.killed) {
      try { this.process.kill(); } catch {}
    }
  }
}

function optionsNumber(value) {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

function summarizeHealth(client) {
  let readySendingZeroReceive = 0;
  let maxControlAge = 0;
  let maxMediaAge = 0;
  let maxBulkAge = 0;
  for (const item of client.health) {
    const ready = Number(item.ready_emitted || 0) > 0 &&
      Number(item.control_ready || 0) > 0 &&
      Number(item.media_ready || 0) > 0 &&
      Number(item.bulk_ready || 0) > 0;
    const sending = Number(item.frames_sent_since_last || 0) > 0;
    const receiving = Number(item.total_messages_received_since_last || 0) > 0;
    if (ready && sending && !receiving) {
      readySendingZeroReceive += 1;
    }

    maxControlAge = Math.max(maxControlAge, Number(item.control_last_received_age_ms || 0));
    maxMediaAge = Math.max(maxMediaAge, Number(item.media_last_received_age_ms || 0));
    maxBulkAge = Math.max(maxBulkAge, Number(item.bulk_last_received_age_ms || 0));
  }

  return {
    summary_count: client.health.length,
    ready_sending_zero_receive_window_count: readySendingZeroReceive,
    max_control_last_received_age_ms: maxControlAge,
    max_media_last_received_age_ms: maxMediaAge,
    max_bulk_last_received_age_ms: maxBulkAge
  };
}

function bytesPerSecond(bytes, elapsedMs) {
  return elapsedMs > 0 ? Math.round(bytes / (elapsedMs / 1000)) : 0;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  fs.mkdirSync(options.artifactDir, { recursive: true });
  const eventsPath = path.join(options.artifactDir, 'bridge-receive-probe-events.jsonl');
  const eventWriter = fs.createWriteStream(eventsPath, { encoding: 'utf8' });
  const a = new BridgeProbeClient('a', options, eventWriter);
  const b = new BridgeProbeClient('b', options, eventWriter);
  const runId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const mediaPayload = Buffer.alloc(options.mediaPayloadBytes, 0x4d);
  const bulkPayload = Buffer.alloc(options.bulkPayloadBytes, 0x42);
  const controlPayload = Buffer.from('nlink-bridge-receive-probe-control-ping', 'utf8');

  try {
    a.start();
    b.start();
    await Promise.all([
      a.connect(`nlink-probe-a-${runId}`),
      b.connect(`nlink-probe-b-${runId}`)
    ]);

    const startedAt = Date.now();
    const deadline = startedAt + options.durationSeconds * 1000;
    while (Date.now() < deadline) {
      const sends = [];
      if (!options.bulkOnly) {
        sends.push(
          [a, b.ready.controlAddress, controlPayload, 'control'],
          [a, b.ready.mediaAddress, mediaPayload, 'media'],
          [b, a.ready.controlAddress, controlPayload, 'control'],
          [b, a.ready.mediaAddress, mediaPayload, 'media']
        );
      }

      const bulkDirections = options.oneWayBulk
        ? [[a, b.ready.bulkAddress]]
        : [[a, b.ready.bulkAddress], [b, a.ready.bulkAddress]];
      for (const [client, destination] of bulkDirections) {
        for (let i = 0; i < options.bulkBurstFrames; i++) {
          sends.push([client, destination, bulkPayload, 'bulk']);
        }
      }

      for (const [client, destination, payload, channel] of sends) {
        const accepted = client.sendFrame(destination, payload, channel);
        if (!accepted && !options.ignoreStdinBackpressure) {
          await client.waitForDrain();
        }
      }

      await delay(options.intervalMs);
    }

    await delay(3000);
    var probeElapsedMs = Date.now() - startedAt;
  } finally {
    await Promise.allSettled([a.shutdown(), b.shutdown()]);
    eventWriter.end();
  }

  const elapsedMs = typeof probeElapsedMs === 'number' && probeElapsedMs > 0
    ? probeElapsedMs
    : options.durationSeconds * 1000;
  const summary = {
    artifact_dir: options.artifactDir,
    duration_seconds: options.durationSeconds,
    elapsed_ms: elapsedMs,
    interval_ms: options.intervalMs,
    payload_bytes: options.payloadBytes,
    control_payload_bytes: controlPayload.length,
    media_payload_bytes: options.mediaPayloadBytes,
    bulk_payload_bytes: options.bulkPayloadBytes,
    bulk_send_concurrency: options.bulkSendConcurrency,
    bulk_burst_frames: options.bulkBurstFrames,
    bulk_only: options.bulkOnly,
    one_way_bulk: options.oneWayBulk,
    ignore_stdin_backpressure: options.ignoreStdinBackpressure,
    clients: {
      a: {
        address: a.ready ? a.ready.controlAddress : null,
        sent: a.sent,
        sent_bytes: a.sentBytes,
        sent_bytes_per_second: {
          control: bytesPerSecond(a.sentBytes.control, elapsedMs),
          media: bytesPerSecond(a.sentBytes.media, elapsedMs),
          bulk: bytesPerSecond(a.sentBytes.bulk, elapsedMs)
        },
        received: a.received,
        received_bytes: a.receivedBytes,
        received_bytes_per_second: {
          control: bytesPerSecond(a.receivedBytes.control, elapsedMs),
          media: bytesPerSecond(a.receivedBytes.media, elapsedMs),
          bulk: bytesPerSecond(a.receivedBytes.bulk, elapsedMs)
        },
        health: summarizeHealth(a),
        errors: a.errors,
        stderr_tail: a.stderr.slice(-20)
      },
      b: {
        address: b.ready ? b.ready.controlAddress : null,
        sent: b.sent,
        sent_bytes: b.sentBytes,
        sent_bytes_per_second: {
          control: bytesPerSecond(b.sentBytes.control, elapsedMs),
          media: bytesPerSecond(b.sentBytes.media, elapsedMs),
          bulk: bytesPerSecond(b.sentBytes.bulk, elapsedMs)
        },
        received: b.received,
        received_bytes: b.receivedBytes,
        received_bytes_per_second: {
          control: bytesPerSecond(b.receivedBytes.control, elapsedMs),
          media: bytesPerSecond(b.receivedBytes.media, elapsedMs),
          bulk: bytesPerSecond(b.receivedBytes.bulk, elapsedMs)
        },
        health: summarizeHealth(b),
        errors: b.errors,
        stderr_tail: b.stderr.slice(-20)
      }
    }
  };

  fs.writeFileSync(path.join(options.artifactDir, 'bridge-receive-probe-summary.json'), JSON.stringify(summary, null, 2));
  const lines = [
    'Bridge Receive Probe Summary',
    `artifact_dir=${options.artifactDir}`,
    `duration_seconds=${options.durationSeconds}`,
    `elapsed_ms=${elapsedMs}`,
    `payload_bytes=${options.payloadBytes}`,
    `control_payload_bytes=${controlPayload.length}`,
    `media_payload_bytes=${options.mediaPayloadBytes}`,
    `bulk_payload_bytes=${options.bulkPayloadBytes}`,
    `bulk_send_concurrency=${options.bulkSendConcurrency || '(default)'}`,
    `bulk_burst_frames=${options.bulkBurstFrames}`,
    `bulk_only=${options.bulkOnly ? 1 : 0}`,
    `one_way_bulk=${options.oneWayBulk ? 1 : 0}`,
    `ignore_stdin_backpressure=${options.ignoreStdinBackpressure ? 1 : 0}`,
    `a_sent_total=${Object.values(a.sent).reduce((x, y) => x + y, 0)}`,
    `a_bulk_sent_bytes_per_second=${bytesPerSecond(a.sentBytes.bulk, elapsedMs)}`,
    `a_received_total=${Object.values(a.received).reduce((x, y) => x + y, 0)}`,
    `a_bulk_received_bytes_per_second=${bytesPerSecond(a.receivedBytes.bulk, elapsedMs)}`,
    `a_ready_sending_zero_receive_window_count=${summarizeHealth(a).ready_sending_zero_receive_window_count}`,
    `b_sent_total=${Object.values(b.sent).reduce((x, y) => x + y, 0)}`,
    `b_bulk_sent_bytes_per_second=${bytesPerSecond(b.sentBytes.bulk, elapsedMs)}`,
    `b_received_total=${Object.values(b.received).reduce((x, y) => x + y, 0)}`,
    `b_bulk_received_bytes_per_second=${bytesPerSecond(b.receivedBytes.bulk, elapsedMs)}`,
    `b_ready_sending_zero_receive_window_count=${summarizeHealth(b).ready_sending_zero_receive_window_count}`,
    `events=${eventsPath}`
  ];
  fs.writeFileSync(path.join(options.artifactDir, 'bridge-receive-probe-summary.txt'), lines.join('\n') + '\n');
  console.log(`Bridge receive probe artifacts: ${options.artifactDir}`);

  const failures = a.errors.length + b.errors.length;
  process.exitCode = failures > 0 ? 1 : 0;
}

main().catch(error => {
  console.error(error && error.stack ? error.stack : String(error));
  process.exit(1);
});
