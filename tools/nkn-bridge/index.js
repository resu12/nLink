'use strict';

const readline = require('readline');
const BRIDGE_PROTOCOL_VERSION = 1;
const SDK_LABEL = 'nkn-sdk@1.3.6';
const MAX_INPUT_LINE_BYTES = 256 * 1024;
const MAX_DECODED_PAYLOAD_BYTES = 64 * 1024;
const DEFAULT_SUBSCRIBE_DURATION = 1440;
const DEFAULT_CONNECT_READY_TIMEOUT_MS = 12000;
const DEFAULT_RPC_SERVERS = [
  'https://mainnet-rpc-node-0001.nkn.org/mainnet/api/wallet',
  'https://seed.nkn.org:30003',
  'http://seed.nkn.org:30003'
];

// Redirect accidental console output (including some library logs) away from stdout.
const stderrWrite = (msg) => {
  try {
    process.stderr.write(String(msg));
  } catch {
    // ignore stderr failures
  }
};

console.log = (...args) => stderrWrite(args.join(' ') + '\n');
console.info = (...args) => stderrWrite(args.join(' ') + '\n');
console.warn = (...args) => stderrWrite(args.join(' ') + '\n');
console.error = (...args) => stderrWrite(args.join(' ') + '\n');

let nkn = null;
try {
  nkn = require('nkn-sdk');
} catch (error) {
  // Keep process alive and report protocol-level error to stdout.
  emitJson({
    event: 'disconnected',
    reason: `Failed to load nkn-sdk: ${safeErrorMessage(error)}`
  });
}

const state = {
  client: null,
  readyEmitted: false,
  shuttingDown: false,
  subscriptions: new Set(),
  clientIdentifier: ''
};

let rpcCandidateCursor = 0;

function emitJson(obj) {
  // stdout must be JSONL only.
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function logStderr(message) {
  stderrWrite(`[nkn-bridge] ${message}\n`);
}

function safeErrorMessage(error) {
  if (!error) {
    return 'Unknown error';
  }

  if (typeof error === 'string') {
    return error;
  }

  return error.message || error.name || 'Unknown error';
}

function decodeSeed(seedHex, seedBase64) {
  if (seedHex && seedBase64) {
    throw new Error('Provide only one of seedHex or seedBase64.');
  }

  if (seedHex) {
    const normalized = String(seedHex).trim();
    if (!/^[0-9a-fA-F]+$/.test(normalized) || normalized.length % 2 !== 0) {
      throw new Error('seedHex must be even-length hex.');
    }

    return normalized.toLowerCase();
  }

  if (seedBase64) {
    const buf = Buffer.from(String(seedBase64), 'base64');
    if (buf.length === 0) {
      throw new Error('seedBase64 is empty or invalid.');
    }

    return buf.toString('hex');
  }

  return undefined;
}

function parseRpcCandidates(seedRpc) {
  if (typeof seedRpc === 'string' && seedRpc.trim().length > 0) {
    return seedRpc
      .split(/[;,]/g)
      .map((x) => x.trim())
      .filter((x) => x.length > 0);
  }

  return [...DEFAULT_RPC_SERVERS];
}

function rotateCandidates(candidates) {
  if (!Array.isArray(candidates) || candidates.length <= 1) {
    return candidates || [];
  }

  const offset = rpcCandidateCursor % candidates.length;
  rpcCandidateCursor = (rpcCandidateCursor + 1) % candidates.length;
  return [...candidates.slice(offset), ...candidates.slice(0, offset)];
}

function toBufferFromBase64(payloadBase64) {
  if (typeof payloadBase64 !== 'string' || payloadBase64.length === 0) {
    throw new Error('payloadBase64 is required.');
  }

  const buffer = Buffer.from(payloadBase64, 'base64');
  if (buffer.length > MAX_DECODED_PAYLOAD_BYTES) {
    throw new Error('payload too large');
  }

  return buffer;
}

function toBase64Payload(value) {
  if (value == null) {
    return Buffer.alloc(0).toString('base64');
  }

  if (Buffer.isBuffer(value)) {
    return value.toString('base64');
  }

  if (value instanceof Uint8Array) {
    return Buffer.from(value).toString('base64');
  }

  if (typeof value === 'string') {
    return Buffer.from(value, 'utf8').toString('base64');
  }

  if (typeof value === 'object' && value.payload != null) {
    return toBase64Payload(value.payload);
  }

  return Buffer.from(String(value), 'utf8').toString('base64');
}

function getClientAddress(client) {
  try {
    if (!client) {
      return '';
    }

    if (typeof client.addr === 'string' && client.addr.length > 0) {
      return client.addr;
    }

    if (typeof client.address === 'string' && client.address.length > 0) {
      return client.address;
    }

    if (typeof client.getAddr === 'function') {
      const value = client.getAddr();
      if (typeof value === 'string') {
        return value;
      }
    }
  } catch {
    // ignore
  }

  return '';
}

function getClientIdentifier(client) {
  try {
    if (!client) {
      return '';
    }

    if (typeof client.identifier === 'string') {
      return client.identifier;
    }

    const addr = getClientAddress(client);
    const dot = addr.indexOf('.');
    if (dot > 0) {
      return addr.slice(0, dot);
    }
  } catch {
    // ignore
  }

  return '';
}

function normalizeMessageEvent(args) {
  if (args.length === 1 && args[0] && typeof args[0] === 'object') {
    const msg = args[0];
    const source = msg.src || msg.source || msg.from || '';
    const topic = typeof msg.topic === 'string' ? msg.topic : undefined;
    const isTopic = Boolean(msg.isTopic || msg.isTopicMessage || topic);
    return {
      source: String(source || ''),
      payloadBase64: toBase64Payload(msg.payload != null ? msg.payload : msg.data),
      isTopic,
      topic
    };
  }

  // Common fallback shape: (src, payload)
  const src = args[0];
  const payload = args[1];
  return {
    source: src == null ? '' : String(src),
    payloadBase64: toBase64Payload(payload),
    isTopic: false
  };
}

function attachClientHandlers(client) {
  const onReady = () => {
    if (state.readyEmitted) {
      return;
    }
    state.readyEmitted = true;
    emitJson({
      event: 'ready',
      address: getClientAddress(client)
    });
  };

  const onDisconnected = (reason) => {
    emitJson({
      event: 'disconnected',
      reason: reason || 'Disconnected'
    });
  };

  const onMessage = (...args) => {
    try {
      const msg = normalizeMessageEvent(args);
      const evt = {
        event: 'message',
        source: msg.source,
        payloadBase64: msg.payloadBase64,
        isTopic: Boolean(msg.isTopic),
        ts: Date.now()
      };

      if (msg.topic) {
        evt.topic = msg.topic;
      }

      emitJson(evt);
    } catch (error) {
      logStderr(`Failed to normalize message event: ${safeErrorMessage(error)}`);
    }
  };

  // SDK method-style callbacks (common in nkn-sdk-js)
  if (typeof client.onConnect === 'function') {
    try {
      client.onConnect(onReady);
    } catch (error) {
      logStderr(`onConnect hook failed: ${safeErrorMessage(error)}`);
    }
  }

  if (typeof client.onMessage === 'function') {
    try {
      client.onMessage(onMessage);
    } catch (error) {
      logStderr(`onMessage hook failed: ${safeErrorMessage(error)}`);
    }
  }

  if (typeof client.onConnectFailed === 'function') {
    try {
      client.onConnectFailed(() => {
        // MultiClient can report connect-failed during bootstrap/reconnect attempts.
        // Do not immediately tear down the bridge; let the SDK continue retrying and
        // let our RPC fallback attempts run before .NET times out waiting for ready.
        logStderr('Connect failed (SDK reported onConnectFailed)');
      });
    } catch (error) {
      logStderr(`onConnectFailed hook failed: ${safeErrorMessage(error)}`);
    }
  }

  if (typeof client.onWsError === 'function') {
    try {
      client.onWsError((e) => {
        // WebSocket errors can occur during bootstrap/reconnect attempts.
        // Do not force a protocol-level disconnect here; let the SDK recover
        // or emit a real connect/disconnect/close event.
        logStderr(`WebSocket error: ${safeErrorMessage(e)}`);
      });
    } catch (error) {
      logStderr(`onWsError hook failed: ${safeErrorMessage(error)}`);
    }
  }

  // EventEmitter-style fallback, if exposed.
  if (typeof client.on === 'function') {
    try { client.on('connect', onReady); } catch {}
    try { client.on('ready', onReady); } catch {}
    try { client.on('message', onMessage); } catch {}
    try { client.on('disconnect', () => onDisconnected('Disconnected')); } catch {}
    try { client.on('close', () => onDisconnected('Closed')); } catch {}
    try { client.on('error', (e) => logStderr(`Client error: ${safeErrorMessage(e)}`)); } catch {}
  }

  // Do not emit "ready" just because an address string exists.
  // MultiClient often has an address before the underlying connection is actually ready,
  // which causes a false-ready race ("client not ready" on subscribe/publish).
}

async function closeClient() {
  const client = state.client;
  state.client = null;
  state.readyEmitted = false;
  state.subscriptions.clear();
  state.clientIdentifier = '';

  if (!client) {
    return;
  }

  const closeCandidates = ['close', 'stop', 'disconnect'];
  for (const fn of closeCandidates) {
    if (typeof client[fn] === 'function') {
      try {
        const result = client[fn]();
        if (result && typeof result.then === 'function') {
          await result;
        }
        return;
      } catch (error) {
        logStderr(`Client ${fn} failed: ${safeErrorMessage(error)}`);
      }
    }
  }
}

async function handleConnect(command) {
  if (!nkn) {
    throw new Error('nkn-sdk is not loaded.');
  }

  await closeClient();

  const seed = decodeSeed(command.seedHex, command.seedBase64);
  const options = {
    // MultiClient reliability defaults inspired by production NKN apps.
    numSubClients: 4,
    originalClient: true,
    reconnectIntervalMin: 1000,
    reconnectIntervalMax: 16000,
    responseTimeout: 5000,
    // Do not force WSS in the desktop bridge. Some predecessor nodes do not support
    // WSS, and forcing tls=true causes bootstrap failures on otherwise healthy networks.
    tls: false
  };

  if (seed) {
    options.seed = seed;
  }

  if (typeof command.identifier === 'string' && command.identifier.trim().length > 0) {
    options.identifier = command.identifier.trim();
  }

  const rpcCandidates = rotateCandidates(parseRpcCandidates(command.seedRpc));
  if (rpcCandidates.length > 0) {
    // Keep both keys for compatibility with SDK versions/docs naming differences.
    options.rpcServerAddr = rpcCandidates[0];
    options.seedRPCServerAddr = rpcCandidates[0];
  }

  logStderr(`Creating NKN client (rpc=${options.rpcServerAddr || 'default'})`);
  const ClientCtor = nkn.MultiClient || nkn.Client;
  if (typeof ClientCtor !== 'function') {
    throw new Error('NKN Client constructor not found in SDK.');
  }

  const client = new ClientCtor(options);
  state.client = client;
  state.clientIdentifier = typeof options.identifier === 'string' ? options.identifier : getClientIdentifier(client);
  attachClientHandlers(client);

  // If the first bootstrap RPC is unhealthy for this network, try a few alternatives
  // before .NET bridge connect times out.
  if (rpcCandidates.length > 1) {
    const connectAttemptId = Date.now() + Math.random();
    state.connectAttemptId = connectAttemptId;
    tryFallbackRpcCandidates(connectAttemptId, command, rpcCandidates.slice(1));
  }
}

async function tryFallbackRpcCandidates(connectAttemptId, originalCommand, remainingRpcCandidates) {
  for (const rpc of remainingRpcCandidates) {
    await delay(DEFAULT_CONNECT_READY_TIMEOUT_MS);

    if (state.shuttingDown) {
      return;
    }

    if (state.connectAttemptId !== connectAttemptId) {
      return;
    }

    if (state.readyEmitted) {
      return;
    }

    try {
      logStderr(`Retrying NKN client bootstrap with alternate rpc=${rpc}`);
      await closeClient();

      const seed = decodeSeed(originalCommand.seedHex, originalCommand.seedBase64);
      const options = {
        numSubClients: 4,
        originalClient: true,
        reconnectIntervalMin: 1000,
        reconnectIntervalMax: 16000,
        responseTimeout: 5000,
        tls: false,
        rpcServerAddr: rpc,
        seedRPCServerAddr: rpc
      };

      if (seed) {
        options.seed = seed;
      }

      if (typeof originalCommand.identifier === 'string' && originalCommand.identifier.trim().length > 0) {
        options.identifier = originalCommand.identifier.trim();
      }

      const ClientCtor = nkn.MultiClient || nkn.Client;
      const client = new ClientCtor(options);
      state.client = client;
      state.readyEmitted = false;
      state.clientIdentifier = typeof options.identifier === 'string' ? options.identifier : getClientIdentifier(client);
      attachClientHandlers(client);
    } catch (error) {
      logStderr(`Alternate bootstrap failed: ${safeErrorMessage(error)}`);
    }
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function callClientMethod(methodName, args) {
  if (!state.client) {
    throw new Error('Not connected.');
  }

  const fn = state.client[methodName];
  if (typeof fn !== 'function') {
    throw new Error(`Client method not available: ${methodName}`);
  }

  const result = fn.apply(state.client, args);
  if (result && typeof result.then === 'function') {
    return await result;
  }

  return result;
}

async function handleSubscribe(command) {
  const topic = String(command.topic || '').trim();
  if (!topic) {
    throw new Error('topic is required.');
  }

  // Prefer SDK method, but track locally either way.
  if (state.client && typeof state.client.subscribe === 'function') {
    const identifier = String(state.clientIdentifier || getClientIdentifier(state.client) || '').trim();
    await callClientMethod('subscribe', [topic, DEFAULT_SUBSCRIBE_DURATION, identifier]);
  } else if (!state.client) {
    throw new Error('Not connected.');
  } else {
    throw new Error('subscribe is not supported by this SDK client type.');
  }

  state.subscriptions.add(topic);
}

async function handleUnsubscribe(command) {
  const topic = String(command.topic || '').trim();
  if (!topic) {
    throw new Error('topic is required.');
  }

  if (state.client && typeof state.client.unsubscribe === 'function') {
    const identifier = String(state.clientIdentifier || getClientIdentifier(state.client) || '').trim();
    try {
      await callClientMethod('unsubscribe', [topic, identifier]);
    } catch (error) {
      if (!isBenignUnsubscribeError(error)) {
        throw error;
      }
      // Treat known txpool duplicate subscription unsubscribe races as success.
    }
  } else if (!state.client) {
    throw new Error('Not connected.');
  } else {
    throw new Error('unsubscribe is not supported by this SDK client type.');
  }

  state.subscriptions.delete(topic);
}

function isBenignUnsubscribeError(error) {
  const text = safeErrorMessage(error).toLowerCase();
  return text.includes('duplicate subscription exist in block') ||
    text.includes('subscription does not exist') ||
    text.includes('no subscription');
}

async function handlePublish(command) {
  const topic = String(command.topic || '').trim();
  if (!topic) {
    throw new Error('topic is required.');
  }

  const payload = toBufferFromBase64(command.payloadBase64);
  await callClientMethod('publish', [topic, payload, { txPool: true }]);
}

async function handleSend(command) {
  const destination = String(command.destination || '').trim();
  if (!destination) {
    throw new Error('destination is required.');
  }

  const payload = toBufferFromBase64(command.payloadBase64);
  await callClientMethod('send', [destination, payload, { noReply: true }]);
}

async function handleShutdown() {
  state.shuttingDown = true;
  await closeClient();
  emitJson({ event: 'disconnected', reason: 'shutdown' });
  process.exit(0);
}

async function handleHello(command) {
  const protocol = Number(command.protocol);
  if (!Number.isFinite(protocol) || protocol !== BRIDGE_PROTOCOL_VERSION) {
    throw new Error(`Unsupported protocol: ${command.protocol}`);
  }

  emitJson({
    event: 'hello_ok',
    id: command.id ?? null,
    protocol: BRIDGE_PROTOCOL_VERSION,
    sdk: SDK_LABEL
  });
}

async function handlePing(command) {
  emitJson({
    type: 'pong',
    id: command.id ?? null,
    ts: Date.now()
  });
}

async function dispatchCommand(message) {
  const cmd = String(message.cmd || message.command || message.type || '').trim();
  if (!cmd) {
    throw new Error('Missing cmd.');
  }

  switch (cmd) {
    case 'connect':
      await handleConnect(message);
      return true;
    case 'subscribe':
      await handleSubscribe(message);
      return true;
    case 'unsubscribe':
      await handleUnsubscribe(message);
      return true;
    case 'publish':
      await handlePublish(message);
      return true;
    case 'send':
      await handleSend(message);
      return true;
    case 'hello':
      await handleHello(message);
      return false;
    case 'ping':
      await handlePing(message);
      return false;
    case 'shutdown':
      await handleShutdown();
      return false;
    default:
      throw new Error(`Unsupported cmd: ${cmd}`);
  }
}

function emitCommandAck(message) {
  emitJson({
    event: 'ok',
    id: message.id ?? null,
    cmd: message.cmd || message.command || null
  });
}

function emitCommandError(message, error) {
  emitJson({
    event: 'error',
    id: message && Object.prototype.hasOwnProperty.call(message, 'id') ? message.id : null,
    cmd: message ? (message.cmd || message.command || null) : null,
    reason: safeErrorMessage(error)
  });
}

function emitLineTooLargeError(line) {
  const preview = String(line || '').slice(0, 4096);
  const idMatch = preview.match(/"id"\s*:\s*("([^"]*)"|(-?\d+))/);
  const cmdMatch = preview.match(/"(cmd|command)"\s*:\s*"([^"]*)"/);

  let id = '';
  if (idMatch) {
    id = typeof idMatch[2] === 'string' ? idMatch[2] : (idMatch[3] || '');
  }

  const cmd = cmdMatch && typeof cmdMatch[2] === 'string' && cmdMatch[2].length > 0
    ? cmdMatch[2]
    : '<unknown>';

  emitJson({
    event: 'error',
    id,
    cmd,
    reason: 'line too large'
  });
}

process.on('uncaughtException', (error) => {
  logStderr(`uncaughtException: ${safeErrorMessage(error)}`);
  emitJson({ event: 'disconnected', reason: `uncaughtException: ${safeErrorMessage(error)}` });
});

process.on('unhandledRejection', (error) => {
  logStderr(`unhandledRejection: ${safeErrorMessage(error)}`);
  emitJson({ event: 'disconnected', reason: `unhandledRejection: ${safeErrorMessage(error)}` });
});

process.on('SIGINT', async () => {
  if (state.shuttingDown) {
    process.exit(0);
    return;
  }

  try {
    await handleShutdown();
  } catch {
    process.exit(0);
  }
});

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
  terminal: false
});

rl.on('line', async (line) => {
  if (Buffer.byteLength(line, 'utf8') > MAX_INPUT_LINE_BYTES) {
    emitLineTooLargeError(line);
    return;
  }

  const trimmed = line.trim();
  if (trimmed.length === 0) {
    return;
  }

  let message;
  try {
    message = JSON.parse(trimmed);
  } catch (error) {
    emitJson({
      event: 'error',
      id: null,
      cmd: null,
      reason: `Invalid JSON: ${safeErrorMessage(error)}`
    });
    return;
  }

  try {
    const shouldEmitAck = await dispatchCommand(message);

    // "shutdown" exits before here. "hello"/"ping" emit their own responses.
    if (shouldEmitAck !== false) {
      emitCommandAck(message);
    }
  } catch (error) {
    logStderr(`Command failed (${message.cmd || message.command || 'unknown'}): ${safeErrorMessage(error)}`);
    emitCommandError(message, error);
  }
});

rl.on('close', async () => {
  if (state.shuttingDown) {
    return;
  }

  try {
    await handleShutdown();
  } catch {
    process.exit(0);
  }
});

logStderr('Bridge started');
