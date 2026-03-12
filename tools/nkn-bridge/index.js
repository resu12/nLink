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

const CONTROL_CHANNEL = 'control';
const MEDIA_CHANNEL = 'media';

const state = {
  controlClient: null,
  mediaClient: null,
  controlReady: false,
  mediaReady: false,
  readyEmitted: false,
  shuttingDown: false,
  subscriptions: new Set(),
  controlIdentifier: '',
  mediaIdentifier: '',
  connectId: '',
  preflightProgressEnabled: false,
  inboundScreenSharePolicy: {
    enabled: false,
    sessionId: null,
    sourceAddress: null,
    expiresAtUnixMs: 0
  },
  lastScreenShareDropLogTs: 0,
  lastScreenShareDropReason: '',
  lastScreenShareDropSessionId: '',
  connectAttemptId: 0
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

function toBufferPayload(value) {
  if (value == null) {
    return Buffer.alloc(0);
  }

  if (Buffer.isBuffer(value)) {
    return value;
  }

  if (value instanceof Uint8Array) {
    return Buffer.from(value);
  }

  if (typeof value === 'string') {
    return Buffer.from(value, 'utf8');
  }

  if (typeof value === 'object' && value.payload != null) {
    return toBufferPayload(value.payload);
  }

  return Buffer.from(String(value), 'utf8');
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

function getMediaIdentifier(identifier) {
  const normalized = typeof identifier === 'string' ? identifier.trim() : '';
  if (!normalized) {
    return 'nlink-media';
  }

  if (normalized.endsWith('-media')) {
    return normalized;
  }

  return `${normalized}-media`;
}

function getClientForChannel(channel) {
  return channel === MEDIA_CHANNEL ? state.mediaClient : state.controlClient;
}

function resetBridgeClientState() {
  state.controlClient = null;
  state.mediaClient = null;
  state.controlReady = false;
  state.mediaReady = false;
  state.readyEmitted = false;
  state.subscriptions.clear();
  state.controlIdentifier = '';
  state.mediaIdentifier = '';
  state.inboundScreenSharePolicy = {
    enabled: false,
    sessionId: null,
    sourceAddress: null,
    expiresAtUnixMs: 0
  };
}

function maybeEmitReady() {
  if (state.readyEmitted || !state.controlReady || !state.mediaReady) {
    return;
  }

  state.readyEmitted = true;
  emitJson({
    event: 'ready',
    address: getClientAddress(state.controlClient),
    controlAddress: getClientAddress(state.controlClient),
    mediaAddress: getClientAddress(state.mediaClient),
    ...(state.connectId ? { connectId: state.connectId } : {})
  });
}

function normalizeMessageEvent(args) {
  if (args.length === 1 && args[0] && typeof args[0] === 'object') {
    const msg = args[0];
    const source = msg.src || msg.source || msg.from || '';
    const topic = typeof msg.topic === 'string' ? msg.topic : undefined;
    const isTopic = Boolean(msg.isTopic || msg.isTopicMessage || topic);
    return {
      source: String(source || ''),
      payload: toBufferPayload(msg.payload != null ? msg.payload : msg.data),
      isTopic,
      topic
    };
  }

  // Common fallback shape: (src, payload)
  const src = args[0];
  const payload = args[1];
  return {
    source: src == null ? '' : String(src),
    payload: toBufferPayload(payload),
    isTopic: false
  };
}

function normalizePolicyAddress(value) {
  if (typeof value !== 'string') {
    return '';
  }

  return value.trim();
}

function getAddressTail(address) {
  const normalized = normalizePolicyAddress(address);
  if (!normalized) {
    return '';
  }

  const lastDot = normalized.lastIndexOf('.');
  if (lastDot < 0 || lastDot === normalized.length - 1) {
    return normalized;
  }

  return normalized.slice(lastDot + 1);
}

function looksLikeNknPubKeyTail(value) {
  return typeof value === 'string' &&
    value.length >= 32 &&
    /^[0-9a-fA-F]+$/.test(value);
}

function addressesLikelySamePeer(left, right) {
  const normalizedLeft = normalizePolicyAddress(left);
  const normalizedRight = normalizePolicyAddress(right);
  if (!normalizedLeft || !normalizedRight) {
    return false;
  }

  if (normalizedLeft === normalizedRight) {
    return true;
  }

  const leftTail = getAddressTail(normalizedLeft);
  const rightTail = getAddressTail(normalizedRight);
  return looksLikeNknPubKeyTail(leftTail) &&
    looksLikeNknPubKeyTail(rightTail) &&
    leftTail === rightTail;
}

function tryParseInboundScreenShare(payload) {
  if (!Buffer.isBuffer(payload) || payload.length < 32 || payload[0] !== 0x7b) {
    return null;
  }

  const text = payload.toString('utf8');
  if (!text.includes('screenshare')) {
    return null;
  }

  try {
    const parsed = JSON.parse(text);
    if (!parsed || typeof parsed !== 'object') {
      return null;
    }

    if (typeof parsed.kind === 'string' && parsed.kind.trim() && parsed.kind.trim() !== 'screenshare') {
      return null;
    }

    const type = typeof parsed.type === 'string' ? parsed.type.trim() : '';
    if (type !== 'screenshare.frame.v1' &&
        type !== 'screenshare.frame.v2' &&
        type !== 'screenshare.stop.v1') {
      return null;
    }

    const sessionId = typeof parsed.sessionId === 'string' ? parsed.sessionId.trim() : '';
    if (!sessionId) {
      return null;
    }

    return {
      type,
      sessionId
    };
  } catch {
    return null;
  }
}

function maybeLogScreenShareDrop(reason, sessionId) {
  const now = Date.now();
  const normalizedSessionId = typeof sessionId === 'string' && sessionId.trim().length > 0
    ? sessionId.trim()
    : '(none)';
  if (now - state.lastScreenShareDropLogTs < 2000 &&
      state.lastScreenShareDropReason === reason &&
      state.lastScreenShareDropSessionId === normalizedSessionId) {
    return;
  }

  state.lastScreenShareDropLogTs = now;
  state.lastScreenShareDropReason = reason;
  state.lastScreenShareDropSessionId = normalizedSessionId;
  logStderr(`Dropped inbound screenshare before stdout (reason=${reason}, sessionId=${normalizedSessionId})`);
}

function shouldDropInboundScreenShare(msg) {
  const screenShare = tryParseInboundScreenShare(msg.payload);
  if (!screenShare) {
    return false;
  }

  const policy = state.inboundScreenSharePolicy;
  if (!policy.enabled) {
    maybeLogScreenShareDrop('policy_disabled', screenShare.sessionId);
    return true;
  }

  if (!policy.expiresAtUnixMs || Date.now() >= policy.expiresAtUnixMs) {
    maybeLogScreenShareDrop('approval_expired', screenShare.sessionId);
    return true;
  }

  if (policy.sessionId !== screenShare.sessionId) {
    maybeLogScreenShareDrop('session_id_mismatch', screenShare.sessionId);
    return true;
  }

  if (!addressesLikelySamePeer(msg.source, policy.sourceAddress)) {
    maybeLogScreenShareDrop('source_mismatch', screenShare.sessionId);
    return true;
  }

  return false;
}

function attachClientHandlers(client, channel) {
  const onReady = () => {
    if (channel === MEDIA_CHANNEL) {
      state.mediaReady = true;
    } else {
      state.controlReady = true;
    }
    maybeEmitReady();
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
      if (channel === MEDIA_CHANNEL && shouldDropInboundScreenShare(msg)) {
        return;
      }

      const evt = {
        event: 'message',
        channel,
        source: msg.source,
        payloadBase64: msg.payload.toString('base64'),
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

async function closeSingleClient(client) {
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

async function closeClients() {
  const controlClient = state.controlClient;
  const mediaClient = state.mediaClient;
  resetBridgeClientState();

  await closeSingleClient(mediaClient);
  await closeSingleClient(controlClient);
}

async function handleConnect(command) {
  if (!nkn) {
    throw new Error('nkn-sdk is not loaded.');
  }

  await closeClients();
  state.connectId = typeof command.connectId === 'string' ? command.connectId : '';
  state.preflightProgressEnabled = Boolean(command.preflightRpcEnabled);

  const seed = decodeSeed(command.seedHex, command.seedBase64);
  const baseOptions = {
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
    baseOptions.seed = seed;
  }

  const controlIdentifier = typeof command.identifier === 'string' && command.identifier.trim().length > 0
    ? command.identifier.trim()
    : '';
  const mediaIdentifier = getMediaIdentifier(controlIdentifier);

  const rpcCandidates = rotateCandidates(parseRpcCandidates(command.seedRpc));
  if (rpcCandidates.length > 0) {
    // Keep both keys for compatibility with SDK versions/docs naming differences.
    baseOptions.rpcServerAddr = rpcCandidates[0];
    baseOptions.seedRPCServerAddr = rpcCandidates[0];
  }

  if (state.preflightProgressEnabled) {
    emitJson({
      event: 'rpc_preflight',
      connectId: state.connectId || null,
      timeoutMs: Number(command.preflightTimeoutMs) || null,
      concurrency: Number(command.preflightConcurrency) || null,
      cacheTtlMs: Number(command.preflightCacheTtlMs) || null,
      ts: Date.now()
    });
    if (baseOptions.rpcServerAddr) {
      emitJson({
        event: 'rpc_selected',
        connectId: state.connectId || null,
        rpc: baseOptions.rpcServerAddr,
        stage: 'initial',
        ts: Date.now()
      });
    }
  }

  logStderr(`Creating NKN clients (rpc=${baseOptions.rpcServerAddr || 'default'})`);
  const ClientCtor = nkn.MultiClient || nkn.Client;
  if (typeof ClientCtor !== 'function') {
    throw new Error('NKN Client constructor not found in SDK.');
  }

  const controlOptions = { ...baseOptions };
  if (controlIdentifier) {
    controlOptions.identifier = controlIdentifier;
  }

  const mediaOptions = {
    ...baseOptions,
    identifier: mediaIdentifier
  };

  const controlClient = new ClientCtor(controlOptions);
  const mediaClient = new ClientCtor(mediaOptions);
  state.controlClient = controlClient;
  state.mediaClient = mediaClient;
  state.controlIdentifier = typeof controlOptions.identifier === 'string' ? controlOptions.identifier : getClientIdentifier(controlClient);
  state.mediaIdentifier = typeof mediaOptions.identifier === 'string' ? mediaOptions.identifier : getClientIdentifier(mediaClient);
  attachClientHandlers(controlClient, CONTROL_CHANNEL);
  attachClientHandlers(mediaClient, MEDIA_CHANNEL);

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
      if (state.preflightProgressEnabled) {
        emitJson({
          event: 'rpc_fallback_attempt',
          connectId: state.connectId || null,
          rpc,
          ts: Date.now()
        });
      }
      await closeClients();
      state.connectId = typeof originalCommand.connectId === 'string' ? originalCommand.connectId : '';
      state.preflightProgressEnabled = Boolean(originalCommand.preflightRpcEnabled);

      const seed = decodeSeed(originalCommand.seedHex, originalCommand.seedBase64);
      const baseOptions = {
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
        baseOptions.seed = seed;
      }

      const controlIdentifier = typeof originalCommand.identifier === 'string' && originalCommand.identifier.trim().length > 0
        ? originalCommand.identifier.trim()
        : '';
      const mediaIdentifier = getMediaIdentifier(controlIdentifier);

      const ClientCtor = nkn.MultiClient || nkn.Client;
      const controlOptions = { ...baseOptions };
      if (controlIdentifier) {
        controlOptions.identifier = controlIdentifier;
      }

      const mediaOptions = {
        ...baseOptions,
        identifier: mediaIdentifier
      };

      const controlClient = new ClientCtor(controlOptions);
      const mediaClient = new ClientCtor(mediaOptions);
      state.controlClient = controlClient;
      state.mediaClient = mediaClient;
      state.controlReady = false;
      state.mediaReady = false;
      state.readyEmitted = false;
      state.controlIdentifier = typeof controlOptions.identifier === 'string' ? controlOptions.identifier : getClientIdentifier(controlClient);
      state.mediaIdentifier = typeof mediaOptions.identifier === 'string' ? mediaOptions.identifier : getClientIdentifier(mediaClient);
      attachClientHandlers(controlClient, CONTROL_CHANNEL);
      attachClientHandlers(mediaClient, MEDIA_CHANNEL);
      if (state.preflightProgressEnabled) {
        emitJson({
          event: 'rpc_selected',
          connectId: state.connectId || null,
          rpc,
          stage: 'fallback',
          ts: Date.now()
        });
      }
    } catch (error) {
      logStderr(`Alternate bootstrap failed: ${safeErrorMessage(error)}`);
    }
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function callClientMethod(methodName, args) {
  const client = state.controlClient;
  if (!client) {
    throw new Error('Not connected.');
  }

  const fn = client[methodName];
  if (typeof fn !== 'function') {
    throw new Error(`Client method not available: ${methodName}`);
  }

  const result = fn.apply(client, args);
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
  if (state.controlClient && typeof state.controlClient.subscribe === 'function') {
    const identifier = String(state.controlIdentifier || getClientIdentifier(state.controlClient) || '').trim();
    try {
      await callClientMethod('subscribe', [topic, DEFAULT_SUBSCRIBE_DURATION, identifier]);
    } catch (error) {
      if (!isBenignSubscribeError(error)) {
        throw error;
      }
      // Treat known txpool duplicate subscription races as success.
    }
  } else if (!state.controlClient) {
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

  if (state.controlClient && typeof state.controlClient.unsubscribe === 'function') {
    const identifier = String(state.controlIdentifier || getClientIdentifier(state.controlClient) || '').trim();
    try {
      await callClientMethod('unsubscribe', [topic, identifier]);
    } catch (error) {
      if (!isBenignUnsubscribeError(error)) {
        throw error;
      }
      // Treat known txpool duplicate subscription unsubscribe races as success.
    }
  } else if (!state.controlClient) {
    throw new Error('Not connected.');
  } else {
    throw new Error('unsubscribe is not supported by this SDK client type.');
  }

  state.subscriptions.delete(topic);
}

function isBenignSubscribeError(error) {
  const text = safeErrorMessage(error).toLowerCase();
  return text.includes('duplicate subscription exist in block') ||
    text.includes('duplicate subscription');
}

function isBenignUnsubscribeError(error) {
  const text = safeErrorMessage(error).toLowerCase();
  return text.includes('duplicate subscription exist in block') ||
    text.includes('subscription does not exist') ||
    (text.includes('subscription') && text.includes("doesn't exist")) ||
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

  const channel = typeof command.channel === 'string' ? command.channel.trim().toLowerCase() : CONTROL_CHANNEL;
  if (channel !== CONTROL_CHANNEL && channel !== MEDIA_CHANNEL) {
    throw new Error('channel must be control or media.');
  }

  const client = getClientForChannel(channel);
  if (!client) {
    throw new Error(`Client not connected for channel: ${channel}`);
  }

  const payload = toBufferFromBase64(command.payloadBase64);
  const fn = client.send;
  if (typeof fn !== 'function') {
    throw new Error(`Client method not available: send (${channel})`);
  }

  const result = fn.apply(client, [destination, payload, { noReply: true }]);
  if (result && typeof result.then === 'function') {
    await result;
  }
}

async function handleSetScreenSharePolicy(command) {
  const sessionId = typeof command.sessionId === 'string' && command.sessionId.trim().length > 0
    ? command.sessionId.trim()
    : null;
  const sourceAddress = typeof command.sourceAddress === 'string' && command.sourceAddress.trim().length > 0
    ? command.sourceAddress.trim()
    : null;
  const expiresAtUnixMs = Number(command.expiresAtUnixMs);
  const enabled = Boolean(command.enabled) &&
    Boolean(sessionId) &&
    Boolean(sourceAddress) &&
    Number.isFinite(expiresAtUnixMs) &&
    expiresAtUnixMs > 0;

  state.inboundScreenSharePolicy = {
    enabled,
    sessionId: enabled ? sessionId : null,
    sourceAddress: enabled ? sourceAddress : null,
    expiresAtUnixMs: enabled ? expiresAtUnixMs : 0
  };
}

async function handleShutdown() {
  state.shuttingDown = true;
  await closeClients();
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
    case 'setScreenSharePolicy':
      await handleSetScreenSharePolicy(message);
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
