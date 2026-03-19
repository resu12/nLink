'use strict';

const BRIDGE_PROTOCOL_VERSION = 2;
const BINARY_FRAME_MAGIC = 0x00;
const BINARY_FRAME_HEADER_SIZE = 16;
const BINARY_FRAME_KIND_SEND = 1;
const BINARY_FRAME_KIND_MESSAGE = 2;
const BINARY_FLAG_IS_TOPIC = 0x01;
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
const SUPPORTED_CHANNELS = ['control', 'media', 'bulk'];
let BRIDGE_APP_VERSION = '';

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

try {
  const bridgePackage = require('./package.json');
  if (bridgePackage && typeof bridgePackage.version === 'string') {
    BRIDGE_APP_VERSION = bridgePackage.version.trim();
  }
} catch {
  BRIDGE_APP_VERSION = '';
}

const state = {
  controlClient: null,
  mediaClient: null,
  bulkClient: null,
  readyEmitted: false,
  controlReady: false,
  mediaReady: false,
  bulkReady: false,
  shuttingDown: false,
  subscriptions: new Set(),
  controlClientIdentifier: '',
  mediaClientIdentifier: '',
  bulkClientIdentifier: '',
  connectId: '',
  connectAttemptId: 0,
  preflightProgressEnabled: false,
  inboundScreenSharePolicy: {
    enabled: false,
    sessionId: null,
    sourceAddress: null,
    expiresAtUnixMs: 0
  },
  lastScreenShareDropLogTs: 0,
  lastScreenShareDropReason: '',
  lastScreenShareDropSessionId: ''
};

let rpcCandidateCursor = 0;
let stdinBuffer = Buffer.alloc(0);
let stdinProcessing = false;

function emitJson(obj) {
  // Control/status events stay on the JSONL control plane.
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function channelToByte(channel) {
  if (channel === 'media') {
    return 1;
  }

  if (channel === 'bulk') {
    return 2;
  }

  return 0;
}

function byteToChannel(value) {
  if (value === 1) {
    return 'media';
  }

  if (value === 2) {
    return 'bulk';
  }

  return 'control';
}

function buildBinaryFrame(kind, channel, flags, primaryText, secondaryText, payload) {
  const primary = Buffer.from(String(primaryText || ''), 'utf8');
  const secondary = secondaryText ? Buffer.from(String(secondaryText), 'utf8') : Buffer.alloc(0);
  const bodyLength = primary.length + secondary.length + payload.length;
  const frame = Buffer.alloc(BINARY_FRAME_HEADER_SIZE + bodyLength);
  frame[0] = BINARY_FRAME_MAGIC;
  frame[1] = BRIDGE_PROTOCOL_VERSION;
  frame[2] = kind;
  frame[3] = channelToByte(channel);
  frame[4] = flags & 0xff;
  frame[5] = 0;
  frame.writeUInt16LE(primary.length, 6);
  frame.writeUInt16LE(secondary.length, 8);
  frame.writeUInt32LE(payload.length, 10);
  frame[14] = 0;
  frame[15] = 0;
  primary.copy(frame, BINARY_FRAME_HEADER_SIZE);
  secondary.copy(frame, BINARY_FRAME_HEADER_SIZE + primary.length);
  payload.copy(frame, BINARY_FRAME_HEADER_SIZE + primary.length + secondary.length);
  return frame;
}

function emitBinaryMessage(channel, source, payload, isTopic, topic) {
  const frame = buildBinaryFrame(
    BINARY_FRAME_KIND_MESSAGE,
    channel,
    isTopic ? BINARY_FLAG_IS_TOPIC : 0,
    source,
    isTopic && topic ? topic : null,
    payload);
  process.stdout.write(frame);
}

function tryDecodeBinaryFrameHeader(buffer) {
  if (buffer.length < BINARY_FRAME_HEADER_SIZE) {
    return null;
  }

  if (buffer[0] !== BINARY_FRAME_MAGIC) {
    throw new Error('Invalid binary frame magic.');
  }

  if (buffer[1] !== BRIDGE_PROTOCOL_VERSION) {
    throw new Error(`Unsupported binary frame protocol: ${buffer[1]}`);
  }

  const kind = buffer[2];
  const channel = byteToChannel(buffer[3]);
  const flags = buffer[4];
  const primaryLength = buffer.readUInt16LE(6);
  const secondaryLength = buffer.readUInt16LE(8);
  const payloadLength = buffer.readUInt32LE(10);
  const totalLength = BINARY_FRAME_HEADER_SIZE + primaryLength + secondaryLength + payloadLength;
  return {
    kind,
    channel,
    flags,
    primaryLength,
    secondaryLength,
    payloadLength,
    totalLength
  };
}

function decodeBinaryFrame(buffer) {
  const header = tryDecodeBinaryFrameHeader(buffer);
  if (!header || buffer.length < header.totalLength) {
    return null;
  }

  const primaryStart = BINARY_FRAME_HEADER_SIZE;
  const secondaryStart = primaryStart + header.primaryLength;
  const payloadStart = secondaryStart + header.secondaryLength;
  return {
    kind: header.kind,
    channel: header.channel,
    flags: header.flags,
    primaryText: buffer.subarray(primaryStart, secondaryStart).toString('utf8'),
    secondaryText: header.secondaryLength > 0
      ? buffer.subarray(secondaryStart, payloadStart).toString('utf8')
      : null,
    payload: buffer.subarray(payloadStart, payloadStart + header.payloadLength)
  };
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

function buildChannelIdentifier(identifier, suffix) {
  const normalized = typeof identifier === 'string' ? identifier.trim() : '';
  if (!normalized) {
    return `nlink-${suffix}`;
  }

  const lower = normalized.toLowerCase();
  if (lower.endsWith(`-${suffix}`)) {
    return `${normalized}-${suffix}-client`;
  }

  return `${normalized}-${suffix}`;
}

function buildMediaIdentifier(identifier) {
  return buildChannelIdentifier(identifier, 'media');
}

function buildBulkIdentifier(identifier) {
  return buildChannelIdentifier(identifier, 'bulk');
}

function getClientByChannel(channel) {
  if (channel === 'media') {
    return state.mediaClient;
  }

  if (channel === 'bulk') {
    return state.bulkClient;
  }

  return state.controlClient;
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
    if (type !== 'screenshare.frame.v1' && type !== 'screenshare.stop.v1') {
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
  const markChannelReady = (isReady) => {
    if (channel === 'media') {
      state.mediaReady = isReady;
    } else if (channel === 'bulk') {
      state.bulkReady = isReady;
    } else {
      state.controlReady = isReady;
    }
  };

  const maybeEmitReady = () => {
    if (state.readyEmitted || !state.controlReady || !state.mediaReady || !state.bulkReady) {
      return;
    }

    state.readyEmitted = true;
    emitJson({
      event: 'ready',
      protocol: BRIDGE_PROTOCOL_VERSION,
      channels: SUPPORTED_CHANNELS,
      address: getClientAddress(state.controlClient),
      controlAddress: getClientAddress(state.controlClient),
      mediaAddress: getClientAddress(state.mediaClient),
      bulkAddress: getClientAddress(state.bulkClient),
      ...(BRIDGE_APP_VERSION ? { bridgeAppVersion: BRIDGE_APP_VERSION } : {}),
      ...(state.connectId ? { connectId: state.connectId } : {}),
    });
  };

  const onReady = () => {
    markChannelReady(true);
    maybeEmitReady();
  };

  const onDisconnected = (reason) => {
    markChannelReady(false);

    if (channel !== 'control') {
      logStderr(`Ignoring ${channel} disconnect while control channel remains authoritative (${reason || 'Disconnected'})`);
      return;
    }

    emitJson({
      event: 'disconnected',
      reason: reason || 'Disconnected'
    });
  };

  const onMessage = (...args) => {
    try {
      const msg = normalizeMessageEvent(args);
      if (channel === 'media' && shouldDropInboundScreenShare(msg)) {
        return;
      }
      emitBinaryMessage(channel, msg.source, msg.payload, Boolean(msg.isTopic), msg.topic || null);
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

async function closeClient() {
  const controlClient = state.controlClient;
  const mediaClient = state.mediaClient;
  const bulkClient = state.bulkClient;
  state.controlClient = null;
  state.mediaClient = null;
  state.bulkClient = null;
  state.readyEmitted = false;
  state.controlReady = false;
  state.mediaReady = false;
  state.bulkReady = false;
  state.subscriptions.clear();
  state.controlClientIdentifier = '';
  state.mediaClientIdentifier = '';
  state.bulkClientIdentifier = '';
  state.connectAttemptId = 0;
  state.inboundScreenSharePolicy = {
    enabled: false,
    sessionId: null,
    sourceAddress: null,
    expiresAtUnixMs: 0
  };

  await closeSingleClient(controlClient);
  if (mediaClient && mediaClient !== controlClient) {
    await closeSingleClient(mediaClient);
  }
  if (bulkClient && bulkClient !== controlClient && bulkClient !== mediaClient) {
    await closeSingleClient(bulkClient);
  }
}

async function handleConnect(command) {
  if (!nkn) {
    throw new Error('nkn-sdk is not loaded.');
  }

  await closeClient();
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

  const requestedIdentifier = typeof command.identifier === 'string' && command.identifier.trim().length > 0
    ? command.identifier.trim()
    : '';

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

  const controlOptions = {
    ...baseOptions,
    ...(requestedIdentifier ? { identifier: requestedIdentifier } : {})
  };
  const mediaOptions = {
    ...baseOptions,
    identifier: buildMediaIdentifier(requestedIdentifier || 'nlink')
  };
  const bulkOptions = {
    ...baseOptions,
    identifier: buildBulkIdentifier(requestedIdentifier || 'nlink')
  };

  const controlClient = new ClientCtor(controlOptions);
  const mediaClient = new ClientCtor(mediaOptions);
  const bulkClient = new ClientCtor(bulkOptions);
  state.controlClient = controlClient;
  state.mediaClient = mediaClient;
  state.bulkClient = bulkClient;
  state.controlClientIdentifier = typeof controlOptions.identifier === 'string' ? controlOptions.identifier : getClientIdentifier(controlClient);
  state.mediaClientIdentifier = typeof mediaOptions.identifier === 'string' ? mediaOptions.identifier : getClientIdentifier(mediaClient);
  state.bulkClientIdentifier = typeof bulkOptions.identifier === 'string' ? bulkOptions.identifier : getClientIdentifier(bulkClient);
  attachClientHandlers(controlClient, 'control');
  attachClientHandlers(mediaClient, 'media');
  attachClientHandlers(bulkClient, 'bulk');

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
      await closeClient();
      state.connectAttemptId = connectAttemptId;
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

      const requestedIdentifier = typeof originalCommand.identifier === 'string' && originalCommand.identifier.trim().length > 0
        ? originalCommand.identifier.trim()
        : '';

      const ClientCtor = nkn.MultiClient || nkn.Client;
      const controlOptions = {
        ...baseOptions,
        ...(requestedIdentifier ? { identifier: requestedIdentifier } : {})
      };
      const mediaOptions = {
        ...baseOptions,
        identifier: buildMediaIdentifier(requestedIdentifier || 'nlink')
      };
      const controlClient = new ClientCtor(controlOptions);
      const mediaClient = new ClientCtor(mediaOptions);
      const bulkOptions = {
        ...baseOptions,
        identifier: buildBulkIdentifier(requestedIdentifier || 'nlink')
      };
      const bulkClient = new ClientCtor(bulkOptions);
      state.controlClient = controlClient;
      state.mediaClient = mediaClient;
      state.bulkClient = bulkClient;
      state.readyEmitted = false;
      state.controlReady = false;
      state.mediaReady = false;
      state.bulkReady = false;
      state.controlClientIdentifier = typeof controlOptions.identifier === 'string' ? controlOptions.identifier : getClientIdentifier(controlClient);
      state.mediaClientIdentifier = typeof mediaOptions.identifier === 'string' ? mediaOptions.identifier : getClientIdentifier(mediaClient);
      state.bulkClientIdentifier = typeof bulkOptions.identifier === 'string' ? bulkOptions.identifier : getClientIdentifier(bulkClient);
      attachClientHandlers(controlClient, 'control');
      attachClientHandlers(mediaClient, 'media');
      attachClientHandlers(bulkClient, 'bulk');
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

async function callClientMethod(methodName, args, channel = 'control') {
  const client = getClientByChannel(channel);
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
    const identifier = String(state.controlClientIdentifier || getClientIdentifier(state.controlClient) || '').trim();
    try {
      await callClientMethod('subscribe', [topic, DEFAULT_SUBSCRIBE_DURATION, identifier], 'control');
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
    const identifier = String(state.controlClientIdentifier || getClientIdentifier(state.controlClient) || '').trim();
    try {
      await callClientMethod('unsubscribe', [topic, identifier], 'control');
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
  await callClientMethod('publish', [topic, payload, { txPool: true }], 'control');
}

async function handleSend(command) {
  const destination = String(command.destination || '').trim();
  if (!destination) {
    throw new Error('destination is required.');
  }

  const payload = toBufferFromBase64(command.payloadBase64);
  const normalizedChannel = typeof command.channel === 'string'
    ? command.channel.trim().toLowerCase()
    : '';
  const channel = normalizedChannel === 'media'
    ? 'media'
    : normalizedChannel === 'bulk'
      ? 'bulk'
      : 'control';
  await callClientMethod('send', [destination, payload, { noReply: true }], channel);
}

async function handleBinarySendFrame(frame) {
  if (frame.kind !== BINARY_FRAME_KIND_SEND) {
    throw new Error(`Unsupported binary frame kind: ${frame.kind}`);
  }

  const destination = String(frame.primaryText || '').trim();
  if (!destination) {
    throw new Error('binary send destination is required.');
  }

  if (!Buffer.isBuffer(frame.payload) && !(frame.payload instanceof Uint8Array)) {
    throw new Error('binary send payload is required.');
  }

  if (frame.payload.length > MAX_DECODED_PAYLOAD_BYTES) {
    throw new Error('binary send payload too large.');
  }

  await callClientMethod('send', [destination, frame.payload, { noReply: true }], frame.channel);
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
    sdk: SDK_LABEL,
    channels: SUPPORTED_CHANNELS,
    ...(BRIDGE_APP_VERSION ? { bridgeAppVersion: BRIDGE_APP_VERSION } : {})
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

async function processJsonLine(line) {
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
    if (shouldEmitAck !== false) {
      emitCommandAck(message);
    }
  } catch (error) {
    logStderr(`Command failed (${message.cmd || message.command || 'unknown'}): ${safeErrorMessage(error)}`);
    emitCommandError(message, error);
  }
}

async function processBinaryFrame(frame) {
  try {
    await handleBinarySendFrame(frame);
  } catch (error) {
    logStderr(`Binary frame failed (${frame.kind}): ${safeErrorMessage(error)}`);
  }
}

async function processStdinBuffer() {
  while (stdinBuffer.length > 0) {
    const first = stdinBuffer[0];

    if (first === 0x0a || first === 0x0d || first === 0x20 || first === 0x09) {
      stdinBuffer = stdinBuffer.subarray(1);
      return true;
    }

    if (first === BINARY_FRAME_MAGIC) {
      if (stdinBuffer.length < BINARY_FRAME_HEADER_SIZE) {
        return false;
      }

      let header;
      try {
        header = tryDecodeBinaryFrameHeader(stdinBuffer);
      } catch (error) {
        logStderr(`Invalid binary stdin frame header: ${safeErrorMessage(error)}`);
        stdinBuffer = Buffer.alloc(0);
        return false;
      }

      if (!header || stdinBuffer.length < header.totalLength) {
        return false;
      }

      const frameBuffer = stdinBuffer.subarray(0, header.totalLength);
      stdinBuffer = stdinBuffer.subarray(header.totalLength);
      const frame = decodeBinaryFrame(frameBuffer);
      if (!frame) {
        return false;
      }

      await processBinaryFrame(frame);
      return true;
    }

    const newlineIndex = stdinBuffer.indexOf(0x0a);
    if (newlineIndex < 0) {
      if (stdinBuffer.length > MAX_INPUT_LINE_BYTES) {
        emitLineTooLargeError(stdinBuffer.toString('utf8', 0, MAX_INPUT_LINE_BYTES));
        stdinBuffer = Buffer.alloc(0);
        return false;
      }

      return false;
    }

    const lineBuffer = stdinBuffer.subarray(0, newlineIndex);
    stdinBuffer = stdinBuffer.subarray(newlineIndex + 1);
    await processJsonLine(lineBuffer.toString('utf8'));
    return true;
  }

  return false;
}

function scheduleStdinProcessing() {
  if (stdinProcessing) {
    return;
  }

  stdinProcessing = true;
  (async () => {
    while (await processStdinBuffer()) { }
  })()
    .catch((error) => {
      logStderr(`stdin processing failed: ${safeErrorMessage(error)}`);
      emitJson({ event: 'disconnected', reason: `stdin_processing_failed: ${safeErrorMessage(error)}` });
    })
    .finally(() => {
      stdinProcessing = false;
      if (stdinBuffer.length > 0) {
        scheduleStdinProcessing();
      }
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

process.stdin.on('data', (chunk) => {
  if (!chunk || chunk.length === 0) {
    return;
  }

  stdinBuffer = stdinBuffer.length === 0 ? Buffer.from(chunk) : Buffer.concat([stdinBuffer, chunk]);
  scheduleStdinProcessing();
});

process.stdin.on('end', async () => {
  if (state.shuttingDown) {
    return;
  }

  try {
    await handleShutdown();
  } catch {
    process.exit(0);
  }
});

process.stdin.resume();

logStderr('Bridge started');
