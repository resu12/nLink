'use strict';

let AsyncLocalStorage = null;
try {
  ({ AsyncLocalStorage } = require('node:async_hooks'));
} catch {
  try {
    ({ AsyncLocalStorage } = require('async_hooks'));
  } catch {
    AsyncLocalStorage = null;
  }
}

let monitorEventLoopDelay = null;
try {
  ({ monitorEventLoopDelay } = require('node:perf_hooks'));
} catch {
  monitorEventLoopDelay = null;
}

let cryptoRuntime = null;
try {
  cryptoRuntime = require('node:crypto');
} catch {
  cryptoRuntime = null;
}

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
const MIN_CONNECT_FALLBACK_DELAY_MS = 1000;
const MAX_CONNECT_FALLBACK_DELAY_MS = DEFAULT_CONNECT_READY_TIMEOUT_MS;
const DEFAULT_NUM_SUBCLIENTS = 4;
const DEFAULT_MEDIA_NUM_SUBCLIENTS = 8;
const MIN_NUM_SUBCLIENTS = 1;
const MAX_NUM_SUBCLIENTS = 16;
const OWNER_PID_CHECK_INTERVAL_MS = 2000;
const BRIDGE_EVENT_LOOP_SAMPLE_WINDOW_MS = 2000;
const BRIDGE_EVENT_LOOP_RESOLUTION_MS = 20;
const BRIDGE_CONTROL_SEND_SAMPLE_WINDOW_MS = 2000;
const BRIDGE_MEDIA_SEND_SAMPLE_WINDOW_MS = 2000;
const BRIDGE_BULK_SEND_SAMPLE_WINDOW_MS = 2000;
const BRIDGE_TRANSPORT_HEALTH_SAMPLE_WINDOW_MS = 2000;
const DEFAULT_CONTROL_SEND_TIMEOUT_MS = 5000;
const MIN_CONTROL_SEND_TIMEOUT_MS = 1000;
const MAX_CONTROL_SEND_TIMEOUT_MS = 30000;
const SCREEN_SHARE_QUEUE_MAX_MESSAGES = 24;
const SCREEN_SHARE_QUEUE_MAX_BYTES = 384 * 1024;
const SCREEN_SHARE_QUEUE_CONGESTED_MESSAGES = 8;
const SCREEN_SHARE_QUEUE_CONGESTED_BYTES = 128 * 1024;
const SCREEN_SHARE_QUEUE_CONGESTED_AGE_MS = 250;
const SCREEN_SHARE_QUEUE_SEVERE_MESSAGES = 16;
const SCREEN_SHARE_QUEUE_SEVERE_BYTES = 256 * 1024;
const SCREEN_SHARE_QUEUE_SEVERE_AGE_MS = 500;
const SCREEN_SHARE_CATCH_UP_QUEUE_MAX_MESSAGES = 4;
const SCREEN_SHARE_CATCH_UP_QUEUE_MAX_BYTES = 96 * 1024;
const BULK_QUEUE_CONGESTED_MESSAGES = 64;
const BULK_QUEUE_CONGESTED_BYTES = 4 * 1024 * 1024;
const BULK_QUEUE_CONGESTED_AGE_MS = 250;
const BULK_QUEUE_SEVERE_MESSAGES = 192;
const BULK_QUEUE_SEVERE_BYTES = 12 * 1024 * 1024;
const BULK_QUEUE_SEVERE_AGE_MS = 1000;
const BULK_QUEUE_TRANSIENT_RETRY_MAX_ATTEMPTS = 4;
const BULK_QUEUE_TRANSIENT_RETRY_DELAY_MS = 150;
const DEFAULT_BULK_SEND_CONCURRENCY = 4;
const MIN_BULK_SEND_CONCURRENCY = 1;
const MAX_BULK_SEND_CONCURRENCY = 8;
const DEFAULT_BULK_SEND_MODE = 'round_robin';
const BULK_SEND_MODE_FANOUT = 'fanout';
const BULK_SEND_MODE_ROUND_ROBIN = 'round_robin';
const BULK_SEND_MODE_SINGLE = 'single';
const BULK_SEND_MODE_REDUNDANT2 = 'redundant2';
const DEFAULT_RPC_SERVERS = [
  'https://mainnet-rpc-node-0001.nkn.org/mainnet/api/wallet',
  'http://seed.nkn.org:30003'
];
const SUPPORTED_CHANNELS = ['control', 'media', 'bulk'];
const RECEIVE_TIMING_METADATA_SYMBOL = Symbol('nlink.bridge.receiveTimingMetadata');
const RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL = Symbol('nlink.bridge.receiveTimingHooksInstalled');
const RECEIVE_TIMING_WRAPPER_SYMBOL = Symbol('nlink.bridge.receiveTimingWrapper');
const RECEIVE_TIMING_NKN_SOCKET_SYMBOL = Symbol('nlink.bridge.receiveTimingNknSocket');
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
if (process.env.NLINK_BRIDGE_FAKE_NKN_RUNTIME === '1') {
  nkn = createFakeNknRuntime();
} else {
  try {
    nkn = require('nkn-sdk');
  } catch (error) {
    // Keep process alive and report protocol-level error to stdout.
    emitJson({
      event: 'disconnected',
      reason: `Failed to load nkn-sdk: ${safeErrorMessage(error)}`
    });
  }
}

let wsRuntime = null;
try {
  wsRuntime = require('ws');
} catch {
  wsRuntime = null;
}

let netRuntime = null;
try {
  netRuntime = require('net');
} catch {
  netRuntime = null;
}

try {
  const bridgePackage = require('./package.json');
  if (bridgePackage && typeof bridgePackage.version === 'string') {
    BRIDGE_APP_VERSION = bridgePackage.version.trim();
  }
} catch {
  BRIDGE_APP_VERSION = '';
}

installNknReceiveTimingHooks();
startBridgeEventLoopMonitor();
startBridgeControlSendSummaryMonitor();
startBridgeMediaSendSummaryMonitor();
startBridgeBulkSendSummaryMonitor();
startBridgeTransportHealthSummaryMonitor();

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
  controlNumSubClients: DEFAULT_NUM_SUBCLIENTS,
  mediaNumSubClients: DEFAULT_MEDIA_NUM_SUBCLIENTS,
  bulkNumSubClients: DEFAULT_NUM_SUBCLIENTS,
  connectId: '',
  connectAttemptId: 0,
  preflightProgressEnabled: false,
  clientReadyAtMs: 0,
  lastDisconnectReason: '',
  selectedRpc: '',
  selectedRpcKey: '',
  selectedRpcStage: 'none',
  screenShareQueue: [],
  screenShareQueuedBytes: 0,
  screenShareQueueMode: 'normal',
  screenShareQueueGeneration: 0,
  screenShareQueueDrainActive: false,
  screenShareQueueInFlight: false,
  screenShareQueueDroppedSinceLast: 0,
  lastEmittedScreenShareQueueStateKey: '',
  lastEmittedScreenShareQueueStateAt: 0,
  bulkSendQueue: [],
  bulkQueuedBytes: 0,
  bulkQueueInFlight: 0,
  bulkQueueInFlightBytes: 0,
  bulkSendConcurrency: DEFAULT_BULK_SEND_CONCURRENCY,
  bulkSendMode: DEFAULT_BULK_SEND_MODE,
  bulkRoundRobinCursor: 0,
  bulkQueueClearedSinceLast: 0,
  lastEmittedBulkQueueStateKey: '',
  lastEmittedBulkQueueStateAt: 0,
  controlSendQueue: [],
  controlQueuedBytes: 0,
  controlQueueDrainActive: false,
  controlQueueInFlight: false,
  controlQueueClearedSinceLast: 0,
  controlLastMessageReceivedAtMs: 0,
  mediaLastMessageReceivedAtMs: 0,
  bulkLastMessageReceivedAtMs: 0,
  bridgeControlSendSummaryWindow: createBridgeControlSendSummaryWindow(),
  bridgeMediaSendSummaryWindow: createBridgeMediaSendSummaryWindow(),
  bridgeBulkSendSummaryWindow: createBridgeBulkSendSummaryWindow(),
  bridgeTransportHealthSummaryWindow: createBridgeTransportHealthSummaryWindow()
};

const inboundReceiveContextStorage = AsyncLocalStorage ? new AsyncLocalStorage() : null;

let rpcCandidateCursor = 0;
let stdinBuffer = Buffer.alloc(0);
let stdinProcessing = false;
const ownerPid = Number.parseInt(process.env.NLINK_BRIDGE_OWNER_PID || '', 10);
let ownerPidMonitor = null;

function emitJson(obj) {
  // Control/status events stay on the JSONL control plane.
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function parseNonNegativeEnvInt(name, fallback) {
  const raw = Number.parseInt(process.env[name] || '', 10);
  return Number.isFinite(raw) && raw >= 0 ? raw : fallback;
}

function clampNumber(value, fallback, min, max) {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }

  return Math.max(min, Math.min(max, Math.floor(parsed)));
}

function getControlSendTimeoutMs() {
  return clampNumber(
    process.env.NLINK_NKN_CONTROL_SEND_TIMEOUT_MS,
    DEFAULT_CONTROL_SEND_TIMEOUT_MS,
    MIN_CONTROL_SEND_TIMEOUT_MS,
    MAX_CONTROL_SEND_TIMEOUT_MS);
}

function createFakeNknRuntime() {
  let fakeBulkClientNotReadyFailuresRemaining = parseNonNegativeEnvInt(
    'NLINK_BRIDGE_FAKE_BULK_SEND_CLIENT_NOT_READY_COUNT',
    0);

  class FakeClient {
    constructor(options = {}) {
      this.identifier = typeof options.identifier === 'string' && options.identifier.length > 0
        ? options.identifier
        : 'nlink-fake';
      const lowerIdentifier = this.identifier.toLowerCase();
      this.channel = lowerIdentifier.includes('bulk')
        ? 'bulk'
        : lowerIdentifier.includes('media')
          ? 'media'
          : 'control';
      this.addr = `${this.identifier}.fake-${this.channel}.addr`;
      this.address = this.addr;
      this.connected = false;
      this.connectHandlers = [];
      this.messageHandlers = [];
      this.numSubClients = Math.max(1, Number(options.numSubClients) || DEFAULT_NUM_SUBCLIENTS);

      setTimeout(() => {
        this.connected = true;
        for (const handler of this.connectHandlers.slice()) {
          try {
            handler();
          } catch (error) {
            logStderr(`Fake NKN connect handler failed: ${safeErrorMessage(error)}`);
          }
        }
      }, parseNonNegativeEnvInt('NLINK_BRIDGE_FAKE_READY_DELAY_MS', 0));
    }

    onConnect(handler) {
      if (typeof handler !== 'function') {
        return;
      }

      this.connectHandlers.push(handler);
      if (this.connected) {
        setTimeout(handler, 0);
      }
    }

    onMessage(handler) {
      if (typeof handler === 'function') {
        this.messageHandlers.push(handler);
      }
    }

    onConnectFailed() {
    }

    onWsError() {
    }

    on(eventName, handler) {
      if (eventName === 'connect' || eventName === 'ready') {
        this.onConnect(handler);
      } else if (eventName === 'message') {
        this.onMessage(handler);
      }

      return this;
    }

    send() {
      const delayMs = this.channel === 'bulk'
        ? parseNonNegativeEnvInt('NLINK_BRIDGE_FAKE_BULK_SEND_DELAY_MS', 0)
        : this.channel === 'media'
          ? parseNonNegativeEnvInt('NLINK_BRIDGE_FAKE_MEDIA_SEND_DELAY_MS', 0)
          : parseNonNegativeEnvInt('NLINK_BRIDGE_FAKE_CONTROL_SEND_DELAY_MS', 0);

      return new Promise((resolve, reject) => setTimeout(() => {
        if (this.channel === 'bulk' && process.env.NLINK_BRIDGE_FAKE_BULK_SEND_FAIL === '1') {
          reject(new Error('fake bulk send failure'));
          return;
        }

        if (this.channel === 'bulk' && fakeBulkClientNotReadyFailuresRemaining > 0) {
          fakeBulkClientNotReadyFailuresRemaining -= 1;
          reject(new Error('client not ready'));
          return;
        }

        resolve({});
      }, delayMs));
    }

    readyClientIDs() {
      const ids = [''];
      for (let index = 0; index < this.numSubClients; index++) {
        ids.push(`__${index}__`);
      }

      return ids;
    }

    sendWithClient() {
      return this.send();
    }

    publish() {
      return Promise.resolve({});
    }

    subscribe() {
      return Promise.resolve({});
    }

    unsubscribe() {
      return Promise.resolve({});
    }

    close() {
      this.connected = false;
      return Promise.resolve();
    }

    stop() {
      return this.close();
    }

    disconnect() {
      return this.close();
    }
  }

  return {
    Client: FakeClient,
    MultiClient: FakeClient
  };
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

function isObjectLike(value) {
  return value !== null && (typeof value === 'object' || typeof value === 'function');
}

function getReceiveTimingMetadata(payload) {
  if (!isObjectLike(payload)) {
    return null;
  }

  return payload[RECEIVE_TIMING_METADATA_SYMBOL] || null;
}

function ensureReceiveTimingMetadata(payload) {
  if (!isObjectLike(payload)) {
    return null;
  }

  let metadata = payload[RECEIVE_TIMING_METADATA_SYMBOL];
  if (!metadata) {
    metadata = {};
    Object.defineProperty(payload, RECEIVE_TIMING_METADATA_SYMBOL, {
      value: metadata,
      enumerable: false,
      configurable: true,
      writable: false
    });
  }

  return metadata;
}

function recordReceiveTimingMetadata(payload, patch) {
  if (!patch) {
    return null;
  }

  const metadata = ensureReceiveTimingMetadata(payload);
  if (!metadata) {
    return null;
  }

  if (Number.isFinite(patch.sdkHandleMsgEnteredUtcMs) &&
      patch.sdkHandleMsgEnteredUtcMs > 0 &&
      (!Number.isFinite(metadata.sdkHandleMsgEnteredUtcMs) || metadata.sdkHandleMsgEnteredUtcMs <= 0)) {
    metadata.sdkHandleMsgEnteredUtcMs = Math.trunc(patch.sdkHandleMsgEnteredUtcMs);
  }

  if (Number.isFinite(patch.socketDataEventEmittedUtcMs) &&
      patch.socketDataEventEmittedUtcMs > 0 &&
      (!Number.isFinite(metadata.socketDataEventEmittedUtcMs) || metadata.socketDataEventEmittedUtcMs <= 0)) {
    metadata.socketDataEventEmittedUtcMs = Math.trunc(patch.socketDataEventEmittedUtcMs);
  }

  if (Number.isFinite(patch.wsReceiverWriteEnteredUtcMs) &&
      patch.wsReceiverWriteEnteredUtcMs > 0 &&
      (!Number.isFinite(metadata.wsReceiverWriteEnteredUtcMs) || metadata.wsReceiverWriteEnteredUtcMs <= 0)) {
    metadata.wsReceiverWriteEnteredUtcMs = Math.trunc(patch.wsReceiverWriteEnteredUtcMs);
  }

  if (Number.isFinite(patch.wsMessageEmittedUtcMs) &&
      patch.wsMessageEmittedUtcMs > 0 &&
      (!Number.isFinite(metadata.wsMessageEmittedUtcMs) || metadata.wsMessageEmittedUtcMs <= 0)) {
    metadata.wsMessageEmittedUtcMs = Math.trunc(patch.wsMessageEmittedUtcMs);
  }

  if (Number.isFinite(patch.clientMessageDispatchUtcMs) && patch.clientMessageDispatchUtcMs > 0) {
    metadata.clientMessageDispatchUtcMs = Math.trunc(patch.clientMessageDispatchUtcMs);
  }

  if (Number.isFinite(patch.multiClientMessageDispatchUtcMs) && patch.multiClientMessageDispatchUtcMs > 0) {
    metadata.multiClientMessageDispatchUtcMs = Math.trunc(patch.multiClientMessageDispatchUtcMs);
  }

  return metadata;
}

function readReceiveTimingMetadata(value) {
  const metadata = getReceiveTimingMetadata(value);
  if (!metadata) {
    return null;
  }

  const normalized = {};
  if (Number.isFinite(metadata.socketDataEventEmittedUtcMs) && metadata.socketDataEventEmittedUtcMs > 0) {
    normalized.socketDataEventEmittedUtcMs = Math.trunc(metadata.socketDataEventEmittedUtcMs);
  }
  if (Number.isFinite(metadata.wsReceiverWriteEnteredUtcMs) && metadata.wsReceiverWriteEnteredUtcMs > 0) {
    normalized.wsReceiverWriteEnteredUtcMs = Math.trunc(metadata.wsReceiverWriteEnteredUtcMs);
  }
  if (Number.isFinite(metadata.wsMessageEmittedUtcMs) && metadata.wsMessageEmittedUtcMs > 0) {
    normalized.wsMessageEmittedUtcMs = Math.trunc(metadata.wsMessageEmittedUtcMs);
  }
  if (Number.isFinite(metadata.sdkHandleMsgEnteredUtcMs) && metadata.sdkHandleMsgEnteredUtcMs > 0) {
    normalized.sdkHandleMsgEnteredUtcMs = Math.trunc(metadata.sdkHandleMsgEnteredUtcMs);
  }
  if (Number.isFinite(metadata.clientMessageDispatchUtcMs) && metadata.clientMessageDispatchUtcMs > 0) {
    normalized.clientMessageDispatchUtcMs = Math.trunc(metadata.clientMessageDispatchUtcMs);
  }
  if (Number.isFinite(metadata.multiClientMessageDispatchUtcMs) && metadata.multiClientMessageDispatchUtcMs > 0) {
    normalized.multiClientMessageDispatchUtcMs = Math.trunc(metadata.multiClientMessageDispatchUtcMs);
  }

  return Object.keys(normalized).length > 0 ? normalized : null;
}

function buildMediaReceiveTimingMetadataString(bridgeMessageObservedUtcMs, receiveTimingMetadata) {
  const parts = ['ver=4'];
  let hasValue = false;

  if (Number.isFinite(bridgeMessageObservedUtcMs) && bridgeMessageObservedUtcMs > 0) {
    parts.push(`b=${Math.trunc(bridgeMessageObservedUtcMs)}`);
    hasValue = true;
  }

  if (receiveTimingMetadata) {
    if (Number.isFinite(receiveTimingMetadata.socketDataEventEmittedUtcMs) && receiveTimingMetadata.socketDataEventEmittedUtcMs > 0) {
      parts.push(`s=${Math.trunc(receiveTimingMetadata.socketDataEventEmittedUtcMs)}`);
      hasValue = true;
    }

    if (Number.isFinite(receiveTimingMetadata.wsReceiverWriteEnteredUtcMs) && receiveTimingMetadata.wsReceiverWriteEnteredUtcMs > 0) {
      parts.push(`r=${Math.trunc(receiveTimingMetadata.wsReceiverWriteEnteredUtcMs)}`);
      hasValue = true;
    }

    if (Number.isFinite(receiveTimingMetadata.wsMessageEmittedUtcMs) && receiveTimingMetadata.wsMessageEmittedUtcMs > 0) {
      parts.push(`w=${Math.trunc(receiveTimingMetadata.wsMessageEmittedUtcMs)}`);
      hasValue = true;
    }

    if (Number.isFinite(receiveTimingMetadata.sdkHandleMsgEnteredUtcMs) && receiveTimingMetadata.sdkHandleMsgEnteredUtcMs > 0) {
      parts.push(`h=${Math.trunc(receiveTimingMetadata.sdkHandleMsgEnteredUtcMs)}`);
      hasValue = true;
    }

    if (Number.isFinite(receiveTimingMetadata.clientMessageDispatchUtcMs) && receiveTimingMetadata.clientMessageDispatchUtcMs > 0) {
      parts.push(`c=${Math.trunc(receiveTimingMetadata.clientMessageDispatchUtcMs)}`);
      hasValue = true;
    }

    if (Number.isFinite(receiveTimingMetadata.multiClientMessageDispatchUtcMs) && receiveTimingMetadata.multiClientMessageDispatchUtcMs > 0) {
      parts.push(`m=${Math.trunc(receiveTimingMetadata.multiClientMessageDispatchUtcMs)}`);
      hasValue = true;
    }
  }

  return hasValue ? parts.join(';') : null;
}

function emitBinaryMessage(channel, source, payload, isTopic, topic, bridgeMessageObservedUtcMs, receiveTimingMetadata) {
  const secondaryText = isTopic && topic
    ? topic
    : channel === 'media' && !isTopic
      ? buildMediaReceiveTimingMetadataString(bridgeMessageObservedUtcMs, receiveTimingMetadata)
      : null;
  const frame = buildBinaryFrame(
    BINARY_FRAME_KIND_MESSAGE,
    channel,
    isTopic ? BINARY_FLAG_IS_TOPIC : 0,
    source,
    secondaryText,
    payload);
  process.stdout.write(frame);
}

function installNknReceiveTimingHooks() {
  if (!nkn || !nkn.Client || !nkn.Client.prototype) {
    return;
  }

  const clientProto = nkn.Client.prototype;
  const wsWebSocketCtor = wsRuntime && typeof wsRuntime === 'function'
    ? wsRuntime
    : wsRuntime && wsRuntime.WebSocket && wsRuntime.WebSocket.prototype
      ? wsRuntime.WebSocket
      : null;
  const wsWebSocketProto = wsWebSocketCtor && wsWebSocketCtor.prototype
    ? wsWebSocketCtor.prototype
    : null;
  const wsReceiverProto = wsRuntime && wsRuntime.Receiver && wsRuntime.Receiver.prototype
    ? wsRuntime.Receiver.prototype
    : null;
  const netSocketProto = netRuntime && netRuntime.Socket && netRuntime.Socket.prototype
    ? netRuntime.Socket.prototype
    : null;
  const clientHooksInstalled = Boolean(clientProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL]);
  const wsHooksInstalled = (!wsWebSocketProto || wsWebSocketProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL]) &&
    (!wsReceiverProto || wsReceiverProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL]) &&
    (!netSocketProto || netSocketProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL]);
  if (clientHooksInstalled && wsHooksInstalled) {
    return;
  }

  const multiClientProto = nkn.MultiClient && nkn.MultiClient.prototype
    ? nkn.MultiClient.prototype
    : null;

  if (wsWebSocketProto && typeof wsWebSocketProto.setSocket === 'function') {
    const originalSetSocket = wsWebSocketProto.setSocket;
    if (!originalSetSocket[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedSetSocket = function patchedSetSocket(...args) {
        const result = originalSetSocket.apply(this, args);
        if (this && this._socket) {
          this._socket[RECEIVE_TIMING_NKN_SOCKET_SYMBOL] = true;
        }
        if (this && this._receiver) {
          this._receiver[RECEIVE_TIMING_NKN_SOCKET_SYMBOL] = true;
        }
        return result;
      };
      wrappedSetSocket[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      wsWebSocketProto.setSocket = wrappedSetSocket;
    }
  }

  if (netSocketProto && typeof netSocketProto.emit === 'function') {
    const originalNetSocketEmit = netSocketProto.emit;
    if (!originalNetSocketEmit[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedNetSocketEmit = function patchedNetSocketEmit(eventName, ...args) {
        if (eventName !== 'data' || !this || !this[RECEIVE_TIMING_NKN_SOCKET_SYMBOL] || !inboundReceiveContextStorage) {
          return originalNetSocketEmit.call(this, eventName, ...args);
        }

        const existingContext = inboundReceiveContextStorage.getStore();
        const nextContext = existingContext && typeof existingContext === 'object'
          ? { ...existingContext }
          : {};
        nextContext.socketDataEventEmittedUtcMs = Date.now();

        return inboundReceiveContextStorage.run(nextContext, () => originalNetSocketEmit.call(this, eventName, ...args));
      };
      wrappedNetSocketEmit[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      netSocketProto.emit = wrappedNetSocketEmit;
    }
  }

  if (wsReceiverProto && typeof wsReceiverProto._write === 'function') {
    const originalReceiverWrite = wsReceiverProto._write;
    if (!originalReceiverWrite[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedReceiverWrite = function patchedReceiverWrite(...args) {
        if (!inboundReceiveContextStorage) {
          return originalReceiverWrite.apply(this, args);
        }

        const existingContext = inboundReceiveContextStorage.getStore();
        const nextContext = existingContext && typeof existingContext === 'object'
          ? { ...existingContext }
          : {};
        nextContext.wsReceiverWriteEnteredUtcMs = Date.now();

        return inboundReceiveContextStorage.run(nextContext, () => originalReceiverWrite.apply(this, args));
      };
      wrappedReceiverWrite[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      wsReceiverProto._write = wrappedReceiverWrite;
    }
  }

  if (wsWebSocketProto && typeof wsWebSocketProto.emit === 'function') {
    const originalWebSocketEmit = wsWebSocketProto.emit;
    if (!originalWebSocketEmit[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedWebSocketEmit = function patchedWebSocketEmit(eventName, ...args) {
        if (eventName !== 'message' || !inboundReceiveContextStorage) {
          return originalWebSocketEmit.call(this, eventName, ...args);
        }

        const existingContext = inboundReceiveContextStorage.getStore();
        const nextContext = existingContext && typeof existingContext === 'object'
          ? { ...existingContext }
          : {};
        nextContext.wsMessageEmittedUtcMs = Date.now();

        return inboundReceiveContextStorage.run(nextContext, () => originalWebSocketEmit.call(this, eventName, ...args));
      };
      wrappedWebSocketEmit[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      wsWebSocketProto.emit = wrappedWebSocketEmit;
    }
  }

  if (typeof clientProto._handleMsg === 'function') {
    const originalHandleMsg = clientProto._handleMsg;
    if (!originalHandleMsg[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedHandleMsg = function patchedHandleMsg(...args) {
        if (!inboundReceiveContextStorage) {
          return originalHandleMsg.apply(this, args);
        }

        const existingContext = inboundReceiveContextStorage.getStore();
        const nextContext = existingContext && typeof existingContext === 'object'
          ? { ...existingContext }
          : {};
        if (!Number.isFinite(nextContext.sdkHandleMsgEnteredUtcMs) || nextContext.sdkHandleMsgEnteredUtcMs <= 0) {
          nextContext.sdkHandleMsgEnteredUtcMs = Date.now();
        }

        return inboundReceiveContextStorage.run(
          nextContext,
          () => originalHandleMsg.apply(this, args));
      };
      wrappedHandleMsg[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      clientProto._handleMsg = wrappedHandleMsg;
    }
  }

  if (typeof clientProto.onMessage === 'function') {
    const originalClientOnMessage = clientProto.onMessage;
    if (!originalClientOnMessage[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedClientOnMessage = function patchedClientOnMessage(func) {
        if (typeof func !== 'function') {
          return originalClientOnMessage.call(this, func);
        }

        const wrappedListener = async (...args) => {
          const message = args[0];
          const payload = message && typeof message === 'object'
            ? (message.payload != null ? message.payload : message.data)
            : null;
          const receiveContext = inboundReceiveContextStorage
            ? inboundReceiveContextStorage.getStore()
            : null;
          recordReceiveTimingMetadata(payload, {
            socketDataEventEmittedUtcMs: receiveContext && Number.isFinite(receiveContext.socketDataEventEmittedUtcMs)
              ? receiveContext.socketDataEventEmittedUtcMs
              : 0,
            wsReceiverWriteEnteredUtcMs: receiveContext && Number.isFinite(receiveContext.wsReceiverWriteEnteredUtcMs)
              ? receiveContext.wsReceiverWriteEnteredUtcMs
              : 0,
            wsMessageEmittedUtcMs: receiveContext && Number.isFinite(receiveContext.wsMessageEmittedUtcMs)
              ? receiveContext.wsMessageEmittedUtcMs
              : 0,
            sdkHandleMsgEnteredUtcMs: receiveContext && Number.isFinite(receiveContext.sdkHandleMsgEnteredUtcMs)
              ? receiveContext.sdkHandleMsgEnteredUtcMs
              : 0,
            clientMessageDispatchUtcMs: Date.now()
          });
          return await func(...args);
        };

        return originalClientOnMessage.call(this, wrappedListener);
      };
      wrappedClientOnMessage[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      clientProto.onMessage = wrappedClientOnMessage;
    }
  }

  if (multiClientProto && typeof multiClientProto.onMessage === 'function') {
    const originalMultiClientOnMessage = multiClientProto.onMessage;
    if (!originalMultiClientOnMessage[RECEIVE_TIMING_WRAPPER_SYMBOL]) {
      const wrappedMultiClientOnMessage = function patchedMultiClientOnMessage(func) {
        if (typeof func !== 'function') {
          return originalMultiClientOnMessage.call(this, func);
        }

        const wrappedListener = async (...args) => {
          const message = args[0];
          const payload = message && typeof message === 'object'
            ? (message.payload != null ? message.payload : message.data)
            : null;
          recordReceiveTimingMetadata(payload, {
            multiClientMessageDispatchUtcMs: Date.now()
          });
          return await func(...args);
        };

        return originalMultiClientOnMessage.call(this, wrappedListener);
      };
      wrappedMultiClientOnMessage[RECEIVE_TIMING_WRAPPER_SYMBOL] = true;
      multiClientProto.onMessage = wrappedMultiClientOnMessage;
    }
  }

  clientProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL] = true;
  if (multiClientProto) {
    multiClientProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL] = true;
  }
  if (wsReceiverProto) {
    wsReceiverProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL] = true;
  }
  if (wsWebSocketProto) {
    wsWebSocketProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL] = true;
  }
  if (netSocketProto) {
    netSocketProto[RECEIVE_TIMING_HOOKS_INSTALLED_SYMBOL] = true;
  }
}

function startBridgeEventLoopMonitor() {
  if (typeof monitorEventLoopDelay !== 'function') {
    return;
  }

  const histogram = monitorEventLoopDelay({
    resolution: BRIDGE_EVENT_LOOP_RESOLUTION_MS
  });
  histogram.enable();

  const timer = setInterval(() => {
    try {
      const p95 = Math.max(0, Math.round(histogram.percentile(95) / 1e6));
      const max = Math.max(0, Math.round(histogram.max / 1e6));
      const mean = Number.isFinite(histogram.mean)
        ? Math.max(0, Math.round(histogram.mean / 1e6))
        : 0;

      emitJson({
        event: 'bridge_event_loop_summary',
        event_loop_p95_ms: p95,
        event_loop_max_ms: max,
        event_loop_mean_ms: mean,
        sample_window_ms: BRIDGE_EVENT_LOOP_SAMPLE_WINDOW_MS
      });
    } catch (error) {
      logStderr(`Event loop summary failed: ${safeErrorMessage(error)}`);
    } finally {
      histogram.reset();
    }
  }, BRIDGE_EVENT_LOOP_SAMPLE_WINDOW_MS);

  if (typeof timer.unref === 'function') {
    timer.unref();
  }
}

function createBridgeMediaSendSummaryWindow() {
  return {
    binarySendFrameObservedToQueueEnqueueMs: [],
    queueEnqueueToQueueDequeueMs: [],
    queueDequeueToMediaSendStartedMs: [],
    mediaSendStartedToMediaSendResolvedMs: [],
    framesSent: 0,
    sendFailures: 0,
    queueDrops: 0
  };
}

function createBridgeControlSendSummaryWindow() {
  return {
    binarySendFrameObservedToQueueEnqueueMs: [],
    queueEnqueueToQueueDequeueMs: [],
    queueDequeueToControlSendStartedMs: [],
    controlSendStartedToControlSendResolvedMs: [],
    framesSent: 0,
    sendFailures: 0,
    queueClears: 0,
    payloadBytesSent: 0
  };
}

function createBridgeBulkSendSummaryWindow() {
  return {
    binarySendFrameObservedToQueueEnqueueMs: [],
    queueEnqueueToQueueDequeueMs: [],
    queueDequeueToBulkSendStartedMs: [],
    bulkSendStartedToBulkSendResolvedMs: [],
    framesSent: 0,
    framesEnqueued: 0,
    sendFailures: 0,
    queueClears: 0,
    payloadBytesSent: 0,
    payloadBytesEnqueued: 0,
    interEnqueueGapMs: [],
    lastEnqueueUtcMs: 0,
    inFlightMax: 0,
    inFlightBytesMax: 0,
    inFlightSampleSum: 0,
    inFlightSampleCount: 0,
    workerIdleSlotSamples: 0,
    workerSaturatedSampleCount: 0,
    drainWakeCount: 0,
    sendModeFanoutFrames: 0,
    sendModeRoundRobinFrames: 0,
    sendModeSingleFrames: 0,
    sendModeRedundant2Frames: 0,
    sendModeFallbackFrames: 0
  };
}

function createBridgeTransportHealthSummaryWindow() {
  return {
    disconnectCountSinceLast: 0,
    connectFailedCountSinceLast: 0,
    wsErrorCountSinceLast: 0,
    rpcFallbackAttemptCountSinceLast: 0,
    framesSentSinceLast: 0,
    controlMessagesReceivedSinceLast: 0,
    mediaMessagesReceivedSinceLast: 0,
    bulkMessagesReceivedSinceLast: 0,
    controlBytesReceivedSinceLast: 0,
    mediaBytesReceivedSinceLast: 0,
    bulkBytesReceivedSinceLast: 0
  };
}

function resetBridgeControlSendSummaryWindow() {
  state.bridgeControlSendSummaryWindow = createBridgeControlSendSummaryWindow();
}

function resetBridgeMediaSendSummaryWindow() {
  state.bridgeMediaSendSummaryWindow = createBridgeMediaSendSummaryWindow();
}

function resetBridgeBulkSendSummaryWindow() {
  state.bridgeBulkSendSummaryWindow = createBridgeBulkSendSummaryWindow();
}

function resetBridgeTransportHealthSummaryWindow() {
  state.bridgeTransportHealthSummaryWindow = createBridgeTransportHealthSummaryWindow();
}

function recordBridgeMediaSendDuration(samples, durationMs) {
  if (!Array.isArray(samples) || !Number.isFinite(durationMs)) {
    return;
  }

  samples.push(Math.max(0, Math.round(durationMs)));
}

function buildDurationStats(values) {
  if (!Array.isArray(values) || values.length === 0) {
    return {
      avg: -1,
      median: -1,
      p95: -1,
      max: -1
    };
  }

  const sorted = values
    .filter((value) => Number.isFinite(value))
    .map((value) => Math.max(0, Math.round(value)))
    .sort((left, right) => left - right);
  if (!sorted.length) {
    return {
      avg: -1,
      median: -1,
      p95: -1,
      max: -1
    };
  }

  const sum = sorted.reduce((total, value) => total + value, 0);
  const medianIndex = Math.floor(sorted.length / 2);
  const median = sorted.length % 2 === 1
    ? sorted[medianIndex]
    : Math.round((sorted[medianIndex - 1] + sorted[medianIndex]) / 2);
  const p95Index = Math.max(0, Math.ceil(sorted.length * 0.95) - 1);

  return {
    avg: Math.round(sum / sorted.length),
    median,
    p95: sorted[p95Index],
    max: sorted[sorted.length - 1]
  };
}

function emitBridgeMediaSendSummary() {
  const window = state.bridgeMediaSendSummaryWindow;
  const ingressStats = buildDurationStats(window.binarySendFrameObservedToQueueEnqueueMs);
  const queueStats = buildDurationStats(window.queueEnqueueToQueueDequeueMs);
  const sendStartStats = buildDurationStats(window.queueDequeueToMediaSendStartedMs);
  const sendResolveStats = buildDurationStats(window.mediaSendStartedToMediaSendResolvedMs);

  emitJson({
    event: 'bridge_media_send_summary',
    binary_send_frame_observed_to_queue_enqueue_avg_ms: ingressStats.avg,
    binary_send_frame_observed_to_queue_enqueue_median_ms: ingressStats.median,
    binary_send_frame_observed_to_queue_enqueue_p95_ms: ingressStats.p95,
    binary_send_frame_observed_to_queue_enqueue_max_ms: ingressStats.max,
    queue_enqueue_to_queue_dequeue_avg_ms: queueStats.avg,
    queue_enqueue_to_queue_dequeue_median_ms: queueStats.median,
    queue_enqueue_to_queue_dequeue_p95_ms: queueStats.p95,
    queue_enqueue_to_queue_dequeue_max_ms: queueStats.max,
    queue_dequeue_to_media_send_started_avg_ms: sendStartStats.avg,
    queue_dequeue_to_media_send_started_median_ms: sendStartStats.median,
    queue_dequeue_to_media_send_started_p95_ms: sendStartStats.p95,
    queue_dequeue_to_media_send_started_max_ms: sendStartStats.max,
    media_send_started_to_media_send_resolved_avg_ms: sendResolveStats.avg,
    media_send_started_to_media_send_resolved_median_ms: sendResolveStats.median,
    media_send_started_to_media_send_resolved_p95_ms: sendResolveStats.p95,
    media_send_started_to_media_send_resolved_max_ms: sendResolveStats.max,
    frames_sent: window.framesSent,
    send_failures: window.sendFailures,
    queue_drops: window.queueDrops,
    queue_mode: state.screenShareQueueMode,
    queue_depth: state.screenShareQueue.length,
    oldest_queued_age_ms: getScreenShareQueueOldestAgeMs(),
    sample_window_ms: BRIDGE_MEDIA_SEND_SAMPLE_WINDOW_MS
  });
}

function getControlQueueOldestAgeMs(nowMs = Date.now()) {
  if (!state.controlSendQueue.length) {
    return 0;
  }

  return Math.max(0, nowMs - state.controlSendQueue[0].queuedAtMs);
}

function emitBridgeControlSendSummary() {
  const window = state.bridgeControlSendSummaryWindow;
  const ingressStats = buildDurationStats(window.binarySendFrameObservedToQueueEnqueueMs);
  const queueStats = buildDurationStats(window.queueEnqueueToQueueDequeueMs);
  const sendStartStats = buildDurationStats(window.queueDequeueToControlSendStartedMs);
  const sendResolveStats = buildDurationStats(window.controlSendStartedToControlSendResolvedMs);

  emitJson({
    event: 'bridge_control_send_summary',
    binary_send_frame_observed_to_queue_enqueue_avg_ms: ingressStats.avg,
    binary_send_frame_observed_to_queue_enqueue_median_ms: ingressStats.median,
    binary_send_frame_observed_to_queue_enqueue_p95_ms: ingressStats.p95,
    binary_send_frame_observed_to_queue_enqueue_max_ms: ingressStats.max,
    queue_enqueue_to_queue_dequeue_avg_ms: queueStats.avg,
    queue_enqueue_to_queue_dequeue_median_ms: queueStats.median,
    queue_enqueue_to_queue_dequeue_p95_ms: queueStats.p95,
    queue_enqueue_to_queue_dequeue_max_ms: queueStats.max,
    queue_dequeue_to_control_send_started_avg_ms: sendStartStats.avg,
    queue_dequeue_to_control_send_started_median_ms: sendStartStats.median,
    queue_dequeue_to_control_send_started_p95_ms: sendStartStats.p95,
    queue_dequeue_to_control_send_started_max_ms: sendStartStats.max,
    control_send_started_to_control_send_resolved_avg_ms: sendResolveStats.avg,
    control_send_started_to_control_send_resolved_median_ms: sendResolveStats.median,
    control_send_started_to_control_send_resolved_p95_ms: sendResolveStats.p95,
    control_send_started_to_control_send_resolved_max_ms: sendResolveStats.max,
    send_p95_ms: sendResolveStats.p95,
    send_max_ms: sendResolveStats.max,
    frames_sent: window.framesSent,
    payload_bytes_sent: window.payloadBytesSent,
    payload_bytes_per_second: Math.round(window.payloadBytesSent / (BRIDGE_CONTROL_SEND_SAMPLE_WINDOW_MS / 1000)),
    send_failures: window.sendFailures,
    queue_clears: window.queueClears,
    queue_depth: state.controlSendQueue.length,
    queued_bytes: state.controlQueuedBytes,
    oldest_queued_age_ms: getControlQueueOldestAgeMs(),
    in_flight: state.controlQueueInFlight ? 1 : 0,
    send_timeout_ms: getControlSendTimeoutMs(),
    sample_window_ms: BRIDGE_CONTROL_SEND_SAMPLE_WINDOW_MS
  });
}

function emitBridgeBulkSendSummary() {
  const window = state.bridgeBulkSendSummaryWindow;
  const ingressStats = buildDurationStats(window.binarySendFrameObservedToQueueEnqueueMs);
  const queueStats = buildDurationStats(window.queueEnqueueToQueueDequeueMs);
  const sendStartStats = buildDurationStats(window.queueDequeueToBulkSendStartedMs);
  const sendResolveStats = buildDurationStats(window.bulkSendStartedToBulkSendResolvedMs);
  const interEnqueueGapStats = buildDurationStats(window.interEnqueueGapMs);
  recordBulkInFlightSnapshot();
  const configuredConcurrency = state.bulkSendConcurrency;
  const effectiveConcurrency = getEffectiveBulkSendConcurrency();
  const workerUtilizationPercent = effectiveConcurrency > 0 && window.inFlightSampleCount > 0
    ? Math.min(100, Math.round((window.inFlightSampleSum / (window.inFlightSampleCount * effectiveConcurrency)) * 100))
    : 0;
  const workerSaturationPercent = window.inFlightSampleCount > 0
    ? Math.min(100, Math.round((window.workerSaturatedSampleCount / window.inFlightSampleCount) * 100))
    : 0;

  emitJson({
    event: 'bridge_bulk_send_summary',
    binary_send_frame_observed_to_queue_enqueue_avg_ms: ingressStats.avg,
    binary_send_frame_observed_to_queue_enqueue_median_ms: ingressStats.median,
    binary_send_frame_observed_to_queue_enqueue_p95_ms: ingressStats.p95,
    binary_send_frame_observed_to_queue_enqueue_max_ms: ingressStats.max,
    queue_enqueue_to_queue_dequeue_avg_ms: queueStats.avg,
    queue_enqueue_to_queue_dequeue_median_ms: queueStats.median,
    queue_enqueue_to_queue_dequeue_p95_ms: queueStats.p95,
    queue_enqueue_to_queue_dequeue_max_ms: queueStats.max,
    queue_dequeue_to_bulk_send_started_avg_ms: sendStartStats.avg,
    queue_dequeue_to_bulk_send_started_median_ms: sendStartStats.median,
    queue_dequeue_to_bulk_send_started_p95_ms: sendStartStats.p95,
    queue_dequeue_to_bulk_send_started_max_ms: sendStartStats.max,
    bulk_send_started_to_bulk_send_resolved_avg_ms: sendResolveStats.avg,
    bulk_send_started_to_bulk_send_resolved_median_ms: sendResolveStats.median,
    bulk_send_started_to_bulk_send_resolved_p95_ms: sendResolveStats.p95,
    bulk_send_started_to_bulk_send_resolved_max_ms: sendResolveStats.max,
    send_p95_ms: sendResolveStats.p95,
    send_max_ms: sendResolveStats.max,
    frames_sent: window.framesSent,
    frames_enqueued: window.framesEnqueued,
    payload_bytes_sent: window.payloadBytesSent,
    payload_bytes_per_second: Math.round(window.payloadBytesSent / (BRIDGE_BULK_SEND_SAMPLE_WINDOW_MS / 1000)),
    payload_bytes_enqueued: window.payloadBytesEnqueued,
    payload_bytes_enqueued_per_second: Math.round(window.payloadBytesEnqueued / (BRIDGE_BULK_SEND_SAMPLE_WINDOW_MS / 1000)),
    inter_enqueue_gap_p95_ms: interEnqueueGapStats.p95,
    inter_enqueue_gap_max_ms: interEnqueueGapStats.max,
    send_failures: window.sendFailures,
    queue_clears: window.queueClears,
    queue_depth: state.bulkSendQueue.length,
    queued_bytes: state.bulkQueuedBytes,
    oldest_queued_age_ms: getBulkQueueOldestAgeMs(),
    in_flight: Math.max(0, state.bulkQueueInFlight),
    in_flight_bytes: Math.max(0, state.bulkQueueInFlightBytes),
    configured_concurrency: configuredConcurrency,
    effective_concurrency: effectiveConcurrency,
    in_flight_max: window.inFlightMax,
    in_flight_bytes_max: window.inFlightBytesMax,
    worker_utilization_percent: workerUtilizationPercent,
    worker_idle_slot_samples: window.workerIdleSlotSamples,
    worker_saturation_percent: workerSaturationPercent,
    drain_wake_count: window.drainWakeCount,
    send_mode: getBulkSendMode(),
    send_mode_fanout_frames: window.sendModeFanoutFrames,
    send_mode_round_robin_frames: window.sendModeRoundRobinFrames,
    send_mode_single_frames: window.sendModeSingleFrames,
    send_mode_redundant2_frames: window.sendModeRedundant2Frames,
    send_mode_fallback_frames: window.sendModeFallbackFrames,
    sample_window_ms: BRIDGE_BULK_SEND_SAMPLE_WINDOW_MS
  });
}

function recordBridgeMessageReceived(channel, payloadLength) {
  const bytes = Number.isFinite(payloadLength) ? Math.max(0, Math.round(payloadLength)) : 0;
  const nowMs = Date.now();
  const window = state.bridgeTransportHealthSummaryWindow;

  if (channel === 'media') {
    window.mediaMessagesReceivedSinceLast += 1;
    window.mediaBytesReceivedSinceLast += bytes;
    state.mediaLastMessageReceivedAtMs = nowMs;
    return;
  }

  if (channel === 'bulk') {
    window.bulkMessagesReceivedSinceLast += 1;
    window.bulkBytesReceivedSinceLast += bytes;
    state.bulkLastMessageReceivedAtMs = nowMs;
    return;
  }

  window.controlMessagesReceivedSinceLast += 1;
  window.controlBytesReceivedSinceLast += bytes;
  state.controlLastMessageReceivedAtMs = nowMs;
}

function getBridgeChannelLastReceivedAgeMs(channel) {
  const lastReceivedAtMs = channel === 'media'
    ? state.mediaLastMessageReceivedAtMs
    : channel === 'bulk'
      ? state.bulkLastMessageReceivedAtMs
      : state.controlLastMessageReceivedAtMs;

  if (!lastReceivedAtMs) {
    return -1;
  }

  return Math.max(0, Date.now() - lastReceivedAtMs);
}

function emitBridgeTransportHealthSummary() {
  const window = state.bridgeTransportHealthSummaryWindow;
  const readyEmitted = Boolean(state.readyEmitted && state.controlReady && state.mediaReady && state.bulkReady);
  const clientReadyAgeMs = readyEmitted && state.clientReadyAtMs > 0
    ? Math.max(0, Date.now() - state.clientReadyAtMs)
    : -1;
  const selectedRpc = state.selectedRpc || '(none)';
  const selectedRpcKey = state.selectedRpcKey || '(none)';
  const connectId = state.connectId || '(none)';
  const connectKey = computeStableKey(connectId) || '(none)';

  emitJson({
    event: 'bridge_transport_health_summary',
    selected_rpc: selectedRpc,
    selected_rpc_key: selectedRpcKey,
    selected_rpc_stage: state.selectedRpcStage || 'none',
    connect_id: connectId,
    connect_key: connectKey,
    ready_emitted: readyEmitted ? 1 : 0,
    client_ready_age_ms: clientReadyAgeMs,
    disconnect_count_since_last: window.disconnectCountSinceLast,
    connect_failed_count_since_last: window.connectFailedCountSinceLast,
    ws_error_count_since_last: window.wsErrorCountSinceLast,
    rpc_fallback_attempt_count_since_last: window.rpcFallbackAttemptCountSinceLast,
    control_ready: state.controlReady ? 1 : 0,
    media_ready: state.mediaReady ? 1 : 0,
    bulk_ready: state.bulkReady ? 1 : 0,
    frames_sent_since_last: window.framesSentSinceLast,
    latest_disconnect_reason: state.lastDisconnectReason || '(none)',
    control_subclients: state.controlNumSubClients,
    media_subclients: state.mediaNumSubClients,
    bulk_subclients: state.bulkNumSubClients,
    bulk_send_concurrency: state.bulkSendConcurrency,
    control_messages_received_since_last: window.controlMessagesReceivedSinceLast,
    media_messages_received_since_last: window.mediaMessagesReceivedSinceLast,
    bulk_messages_received_since_last: window.bulkMessagesReceivedSinceLast,
    total_messages_received_since_last:
      window.controlMessagesReceivedSinceLast +
      window.mediaMessagesReceivedSinceLast +
      window.bulkMessagesReceivedSinceLast,
    control_bytes_received_since_last: window.controlBytesReceivedSinceLast,
    media_bytes_received_since_last: window.mediaBytesReceivedSinceLast,
    bulk_bytes_received_since_last: window.bulkBytesReceivedSinceLast,
    total_bytes_received_since_last:
      window.controlBytesReceivedSinceLast +
      window.mediaBytesReceivedSinceLast +
      window.bulkBytesReceivedSinceLast,
    control_last_received_age_ms: getBridgeChannelLastReceivedAgeMs('control'),
    media_last_received_age_ms: getBridgeChannelLastReceivedAgeMs('media'),
    bulk_last_received_age_ms: getBridgeChannelLastReceivedAgeMs('bulk'),
    sample_window_ms: BRIDGE_TRANSPORT_HEALTH_SAMPLE_WINDOW_MS
  });
}

function startBridgeMediaSendSummaryMonitor() {
  const timer = setInterval(() => {
    try {
      emitBridgeMediaSendSummary();
    } catch (error) {
      logStderr(`Bridge media send summary failed: ${safeErrorMessage(error)}`);
    } finally {
      resetBridgeMediaSendSummaryWindow();
    }
  }, BRIDGE_MEDIA_SEND_SAMPLE_WINDOW_MS);

  if (typeof timer.unref === 'function') {
    timer.unref();
  }
}

function startBridgeControlSendSummaryMonitor() {
  const timer = setInterval(() => {
    try {
      emitBridgeControlSendSummary();
    } catch (error) {
      logStderr(`Bridge control send summary failed: ${safeErrorMessage(error)}`);
    } finally {
      resetBridgeControlSendSummaryWindow();
    }
  }, BRIDGE_CONTROL_SEND_SAMPLE_WINDOW_MS);

  if (typeof timer.unref === 'function') {
    timer.unref();
  }
}

function startBridgeBulkSendSummaryMonitor() {
  const timer = setInterval(() => {
    try {
      emitBulkQueueState(false);
      emitBridgeBulkSendSummary();
    } catch (error) {
      logStderr(`Bridge bulk send summary failed: ${safeErrorMessage(error)}`);
    } finally {
      resetBridgeBulkSendSummaryWindow();
    }
  }, BRIDGE_BULK_SEND_SAMPLE_WINDOW_MS);

  if (typeof timer.unref === 'function') {
    timer.unref();
  }
}

function startBridgeTransportHealthSummaryMonitor() {
  const timer = setInterval(() => {
    try {
      emitBridgeTransportHealthSummary();
    } catch (error) {
      logStderr(`Bridge transport health summary failed: ${safeErrorMessage(error)}`);
    } finally {
      resetBridgeTransportHealthSummaryWindow();
    }
  }, BRIDGE_TRANSPORT_HEALTH_SAMPLE_WINDOW_MS);

  if (typeof timer.unref === 'function') {
    timer.unref();
  }
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

function isOwnerProcessAlive() {
  if (!Number.isFinite(ownerPid) || ownerPid <= 0 || ownerPid === process.pid) {
    return true;
  }

  try {
    process.kill(ownerPid, 0);
    return true;
  } catch (error) {
    if (error && error.code === 'EPERM') {
      return true;
    }

    return false;
  }
}

function startOwnerPidMonitor() {
  if (!Number.isFinite(ownerPid) || ownerPid <= 0 || ownerPid === process.pid) {
    return;
  }

  const checkOwner = async () => {
    if (state.shuttingDown || isOwnerProcessAlive()) {
      return;
    }

    logStderr(`Owner process exited (owner_pid=${ownerPid})`);
    try {
      await handleShutdown();
    } catch {
      process.exit(0);
    }
  };

  ownerPidMonitor = setInterval(() => {
    void checkOwner();
  }, OWNER_PID_CHECK_INTERVAL_MS);

  if (ownerPidMonitor && typeof ownerPidMonitor.unref === 'function') {
    ownerPidMonitor.unref();
  }

  void checkOwner();
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

function computeStableKey(value) {
  const normalized = typeof value === 'string' ? value.trim() : '';
  if (!normalized) {
    return '';
  }

  if (cryptoRuntime && typeof cryptoRuntime.createHash === 'function') {
    try {
      return cryptoRuntime.createHash('sha1').update(normalized, 'utf8').digest('hex').slice(0, 8);
    } catch {
      // Fall back to the simple hash below.
    }
  }

  let hash = 2166136261;
  for (let i = 0; i < normalized.length; i += 1) {
    hash ^= normalized.charCodeAt(i);
    hash = Math.imul(hash, 16777619);
  }

  return (hash >>> 0).toString(16).padStart(8, '0');
}

function trackSelectedRpc(rpc, stage) {
  const normalizedRpc = typeof rpc === 'string' ? rpc.trim() : '';
  if (!normalizedRpc) {
    return;
  }

  state.selectedRpc = normalizedRpc;
  state.selectedRpcKey = computeStableKey(normalizedRpc);
  state.selectedRpcStage = stage === 'fallback' ? 'fallback' : 'initial';
}

function normalizeSubClientCount(value, fallback = DEFAULT_NUM_SUBCLIENTS) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }

  return Math.min(MAX_NUM_SUBCLIENTS, Math.max(MIN_NUM_SUBCLIENTS, parsed));
}

function normalizeBulkSendConcurrency(value, fallback = DEFAULT_BULK_SEND_CONCURRENCY) {
  const parsed = Number.parseInt(value, 10);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }

  return Math.min(MAX_BULK_SEND_CONCURRENCY, Math.max(MIN_BULK_SEND_CONCURRENCY, parsed));
}

function normalizeBulkSendMode(value) {
  const normalized = String(value || '').trim().toLowerCase().replace(/-/g, '_');
  if (normalized === BULK_SEND_MODE_ROUND_ROBIN || normalized === 'roundrobin') {
    return BULK_SEND_MODE_ROUND_ROBIN;
  }

  if (normalized === BULK_SEND_MODE_SINGLE || normalized === 'single_client' || normalized === 'singleclient') {
    return BULK_SEND_MODE_SINGLE;
  }

  if (normalized === BULK_SEND_MODE_REDUNDANT2 || normalized === 'redundant_2' || normalized === 'dual' || normalized === 'dual_round_robin') {
    return BULK_SEND_MODE_REDUNDANT2;
  }

  return BULK_SEND_MODE_FANOUT;
}

function getConfiguredBulkSendMode() {
  return normalizeBulkSendMode(process.env.NLINK_NKN_BULK_SEND_MODE || DEFAULT_BULK_SEND_MODE);
}

function getBulkSendMode() {
  return normalizeBulkSendMode(state.bulkSendMode || getConfiguredBulkSendMode());
}

function clearControlSendQueue(reason) {
  if (state.controlSendQueue.length > 0) {
    state.controlQueueClearedSinceLast += state.controlSendQueue.length;
    state.bridgeControlSendSummaryWindow.queueClears += state.controlSendQueue.length;
  }

  state.controlSendQueue = [];
  state.controlQueuedBytes = 0;
  logStderr(`Control queue cleared (${reason})`);
}

function enqueueControlSend(destination, payload, binarySendFrameObservedUtcMs = 0) {
  const normalizedPayload = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
  const queueEnqueuedUtcMs = Date.now();
  state.controlSendQueue.push({
    destination,
    payload: normalizedPayload,
    queuedAtMs: queueEnqueuedUtcMs,
    queueEnqueuedUtcMs,
    transientRetryAttempt: 0,
    binarySendFrameObservedUtcMs: Number.isFinite(binarySendFrameObservedUtcMs)
      ? Math.max(0, Math.round(binarySendFrameObservedUtcMs))
      : 0
  });
  state.controlQueuedBytes += normalizedPayload.length;
  if (binarySendFrameObservedUtcMs > 0) {
    recordBridgeMediaSendDuration(
      state.bridgeControlSendSummaryWindow.binarySendFrameObservedToQueueEnqueueMs,
      queueEnqueuedUtcMs - binarySendFrameObservedUtcMs);
  }
  scheduleControlQueueDrain();
}

function scheduleControlQueueDrain() {
  if (state.controlQueueDrainActive || state.shuttingDown) {
    return;
  }

  state.controlQueueDrainActive = true;
  void drainControlQueue().finally(() => {
    state.controlQueueDrainActive = false;
    if (state.controlSendQueue.length > 0 && !state.shuttingDown) {
      scheduleControlQueueDrain();
    }
  });
}

async function drainControlQueue() {
  while (state.controlSendQueue.length > 0 && !state.shuttingDown) {
    const item = state.controlSendQueue.shift();
    if (!item) {
      break;
    }

    state.controlQueuedBytes = Math.max(0, state.controlQueuedBytes - item.payload.length);
    state.controlQueueInFlight = true;
    const queueDequeuedUtcMs = Date.now();
    if (item.queueEnqueuedUtcMs > 0) {
      recordBridgeMediaSendDuration(
        state.bridgeControlSendSummaryWindow.queueEnqueueToQueueDequeueMs,
        queueDequeuedUtcMs - item.queueEnqueuedUtcMs);
    }
    const controlSendStartedUtcMs = Date.now();
    recordBridgeMediaSendDuration(
      state.bridgeControlSendSummaryWindow.queueDequeueToControlSendStartedMs,
      controlSendStartedUtcMs - queueDequeuedUtcMs);
    try {
      await callClientMethodWithTimeout(
        'send',
        [item.destination, item.payload, { noReply: true }],
        'control',
        getControlSendTimeoutMs());
      state.bridgeControlSendSummaryWindow.framesSent += 1;
      state.bridgeControlSendSummaryWindow.payloadBytesSent += item.payload.length;
      state.bridgeTransportHealthSummaryWindow.framesSentSinceLast += 1;
    } catch (error) {
      state.bridgeControlSendSummaryWindow.sendFailures += 1;
      logStderr(`Control queue send failed: ${safeErrorMessage(error)}`);
    } finally {
      const controlSendResolvedUtcMs = Date.now();
      recordBridgeMediaSendDuration(
        state.bridgeControlSendSummaryWindow.controlSendStartedToControlSendResolvedMs,
        controlSendResolvedUtcMs - controlSendStartedUtcMs);
      state.controlQueueInFlight = false;
    }
  }
}

function hasCommandValue(command, key) {
  return command[key] !== undefined && command[key] !== null && String(command[key]).trim() !== '';
}

function resolveSubClientTopology(command) {
  const hasControlOverride = hasCommandValue(command, 'numSubClients');
  const controlCount = normalizeSubClientCount(command.numSubClients, DEFAULT_NUM_SUBCLIENTS);
  const mediaFallback = hasControlOverride ? controlCount : DEFAULT_MEDIA_NUM_SUBCLIENTS;
  const mediaCount = normalizeSubClientCount(command.mediaNumSubClients, mediaFallback);
  const bulkCount = normalizeSubClientCount(command.bulkNumSubClients, controlCount);
  const bulkSendConcurrency = normalizeBulkSendConcurrency(command.bulkSendConcurrency, DEFAULT_BULK_SEND_CONCURRENCY);
  return {
    control: controlCount,
    media: mediaCount,
    bulk: bulkCount,
    bulkSendConcurrency
  };
}

function getScreenShareQueueLimits() {
  if (state.screenShareQueueMode === 'catch_up_only') {
    return {
      maxMessages: SCREEN_SHARE_CATCH_UP_QUEUE_MAX_MESSAGES,
      maxBytes: SCREEN_SHARE_CATCH_UP_QUEUE_MAX_BYTES
    };
  }

  return {
    maxMessages: SCREEN_SHARE_QUEUE_MAX_MESSAGES,
    maxBytes: SCREEN_SHARE_QUEUE_MAX_BYTES
  };
}

function getScreenShareQueueOldestAgeMs(nowMs = Date.now()) {
  if (!state.screenShareQueue.length) {
    return 0;
  }

  return Math.max(0, nowMs - state.screenShareQueue[0].queuedAtMs);
}

function buildScreenShareQueueState() {
  const queueDepth = state.screenShareQueue.length;
  const queuedBytes = state.screenShareQueuedBytes;
  const oldestQueuedAgeMs = getScreenShareQueueOldestAgeMs();
  const congested =
    queueDepth >= SCREEN_SHARE_QUEUE_CONGESTED_MESSAGES ||
    queuedBytes >= SCREEN_SHARE_QUEUE_CONGESTED_BYTES ||
    oldestQueuedAgeMs >= SCREEN_SHARE_QUEUE_CONGESTED_AGE_MS;
  const severe =
    queueDepth >= SCREEN_SHARE_QUEUE_SEVERE_MESSAGES ||
    queuedBytes >= SCREEN_SHARE_QUEUE_SEVERE_BYTES ||
    oldestQueuedAgeMs >= SCREEN_SHARE_QUEUE_SEVERE_AGE_MS;
  return {
    queueDepth,
    queuedBytes,
    oldestQueuedAgeMs,
    inFlight: Boolean(state.screenShareQueueInFlight),
    droppedSinceLast: state.screenShareQueueDroppedSinceLast,
    congested,
    severe,
    mode: state.screenShareQueueMode
  };
}

function emitScreenShareQueueState(force = false) {
  const snapshot = buildScreenShareQueueState();
  const key = JSON.stringify([
    snapshot.queueDepth,
    snapshot.queuedBytes,
    snapshot.oldestQueuedAgeMs > 0 ? 1 : 0,
    snapshot.inFlight ? 1 : 0,
    snapshot.droppedSinceLast,
    snapshot.congested ? 1 : 0,
    snapshot.severe ? 1 : 0,
    snapshot.mode
  ]);
  if (!force &&
      key === state.lastEmittedScreenShareQueueStateKey &&
      Date.now() - state.lastEmittedScreenShareQueueStateAt < 250) {
    return;
  }

  state.lastEmittedScreenShareQueueStateKey = key;
  state.lastEmittedScreenShareQueueStateAt = Date.now();
  emitJson({
    event: 'screen_share_queue_state',
    ...snapshot
  });
  state.screenShareQueueDroppedSinceLast = 0;
}

function dropOldestScreenShareQueuedItem(reason) {
  if (!state.screenShareQueue.length) {
    return false;
  }

  const dropped = state.screenShareQueue.shift();
  state.screenShareQueuedBytes = Math.max(0, state.screenShareQueuedBytes - dropped.payload.length);
  state.screenShareQueueDroppedSinceLast += 1;
  state.bridgeMediaSendSummaryWindow.queueDrops += 1;
  logStderr(`ScreenShare queue drop (${reason}, queue_depth=${state.screenShareQueue.length}, queued_bytes=${state.screenShareQueuedBytes})`);
  emitScreenShareQueueState(true);
  return true;
}

function clearScreenShareQueue(reason) {
  if (state.screenShareQueue.length > 0) {
    state.screenShareQueueDroppedSinceLast += state.screenShareQueue.length;
    state.bridgeMediaSendSummaryWindow.queueDrops += state.screenShareQueue.length;
  }

  state.screenShareQueue = [];
  state.screenShareQueuedBytes = 0;
  logStderr(`ScreenShare queue cleared (${reason})`);
  emitScreenShareQueueState(true);
}

function enqueueScreenShareSend(destination, payload, binarySendFrameObservedUtcMs = 0) {
  const limits = getScreenShareQueueLimits();
  const normalizedPayload = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
  while (state.screenShareQueue.length >= limits.maxMessages ||
         state.screenShareQueuedBytes + normalizedPayload.length > limits.maxBytes) {
    if (!dropOldestScreenShareQueuedItem('overflow_oldest')) {
      break;
    }
  }

  const queueEnqueuedUtcMs = Date.now();
  state.screenShareQueue.push({
    destination,
    payload: normalizedPayload,
    queuedAtMs: queueEnqueuedUtcMs,
    queueEnqueuedUtcMs,
    binarySendFrameObservedUtcMs: Number.isFinite(binarySendFrameObservedUtcMs)
      ? Math.max(0, Math.round(binarySendFrameObservedUtcMs))
      : 0,
    generation: state.screenShareQueueGeneration
  });
  state.screenShareQueuedBytes += normalizedPayload.length;
  if (binarySendFrameObservedUtcMs > 0) {
    recordBridgeMediaSendDuration(
      state.bridgeMediaSendSummaryWindow.binarySendFrameObservedToQueueEnqueueMs,
      queueEnqueuedUtcMs - binarySendFrameObservedUtcMs);
  }
  emitScreenShareQueueState(true);
  scheduleScreenShareQueueDrain();
}

function scheduleScreenShareQueueDrain() {
  if (state.screenShareQueueDrainActive || state.shuttingDown) {
    return;
  }

  state.screenShareQueueDrainActive = true;
  void drainScreenShareQueue().finally(() => {
    state.screenShareQueueDrainActive = false;
    if (state.screenShareQueue.length > 0 && !state.shuttingDown) {
      scheduleScreenShareQueueDrain();
    }
  });
}

async function drainScreenShareQueue() {
  while (state.screenShareQueue.length > 0 && !state.shuttingDown) {
    const item = state.screenShareQueue[0];
    if (!item) {
      break;
    }

    state.screenShareQueue.shift();
    state.screenShareQueuedBytes = Math.max(0, state.screenShareQueuedBytes - item.payload.length);
    if (item.generation !== state.screenShareQueueGeneration) {
      emitScreenShareQueueState(true);
      continue;
    }

    state.screenShareQueueInFlight = true;
    emitScreenShareQueueState(true);
    const queueDequeuedUtcMs = Date.now();
    if (item.queueEnqueuedUtcMs > 0) {
      recordBridgeMediaSendDuration(
        state.bridgeMediaSendSummaryWindow.queueEnqueueToQueueDequeueMs,
        queueDequeuedUtcMs - item.queueEnqueuedUtcMs);
    }
    const mediaSendStartedUtcMs = Date.now();
    recordBridgeMediaSendDuration(
      state.bridgeMediaSendSummaryWindow.queueDequeueToMediaSendStartedMs,
      mediaSendStartedUtcMs - queueDequeuedUtcMs);
    try {
      await callClientMethod('send', [item.destination, item.payload, { noReply: true }], 'media');
      state.bridgeMediaSendSummaryWindow.framesSent += 1;
      state.bridgeTransportHealthSummaryWindow.framesSentSinceLast += 1;
    } catch (error) {
      state.bridgeMediaSendSummaryWindow.sendFailures += 1;
      logStderr(`ScreenShare queue send failed: ${safeErrorMessage(error)}`);
    } finally {
      const mediaSendResolvedUtcMs = Date.now();
      recordBridgeMediaSendDuration(
        state.bridgeMediaSendSummaryWindow.mediaSendStartedToMediaSendResolvedMs,
        mediaSendResolvedUtcMs - mediaSendStartedUtcMs);
      state.screenShareQueueInFlight = false;
      emitScreenShareQueueState(true);
    }
  }
}

function getBulkQueueOldestAgeMs(nowMs = Date.now()) {
  if (!state.bulkSendQueue.length) {
    return 0;
  }

  return Math.max(0, nowMs - state.bulkSendQueue[0].queuedAtMs);
}

function getEffectiveBulkSendConcurrency() {
  return normalizeBulkSendConcurrency(state.bulkSendConcurrency, DEFAULT_BULK_SEND_CONCURRENCY);
}

function getReadyBulkClientIds() {
  const client = state.bulkClient;
  if (!client) {
    return [];
  }

  if (typeof client.readyClientIDs === 'function') {
    try {
      const ids = client.readyClientIDs();
      if (Array.isArray(ids)) {
        return ids
          .map((id) => String(id))
          .filter((id) => id.length > 0 || id === '');
      }
    } catch (error) {
      logStderr(`Bulk readyClientIDs failed: ${safeErrorMessage(error)}`);
    }
  }

  if (client.clients && typeof client.clients === 'object') {
    return Object.keys(client.clients).filter((id) => {
      const subClient = client.clients[id];
      return subClient && subClient.isReady !== false;
    });
  }

  return [];
}

function getBulkClientIdSequence() {
  const ids = getReadyBulkClientIds().sort();
  if (ids.length === 0) {
    return [];
  }

  const start = Math.max(0, state.bulkRoundRobinCursor % ids.length);
  const ordered = ids.slice(start).concat(ids.slice(0, start));
  state.bulkRoundRobinCursor = (start + 1) % ids.length;
  return ordered;
}

function createNknMessageId() {
  if (cryptoRuntime && typeof cryptoRuntime.randomBytes === 'function') {
    return cryptoRuntime.randomBytes(16);
  }

  return undefined;
}

async function sendBulkWithSingleClient(destination, payload, mode) {
  const client = state.bulkClient;
  if (!client || typeof client.sendWithClient !== 'function') {
    throw new Error('bulk sendWithClient is not available');
  }

  const ids = mode === BULK_SEND_MODE_SINGLE
    ? getReadyBulkClientIds().sort().slice(0, 1)
    : getBulkClientIdSequence();
  if (ids.length === 0) {
    throw new Error('no ready bulk subclients');
  }

  const failures = [];
  for (const id of ids) {
    try {
      await client.sendWithClient(id, destination, payload, { noReply: true });
      if (mode === BULK_SEND_MODE_SINGLE) {
        state.bridgeBulkSendSummaryWindow.sendModeSingleFrames += 1;
      } else {
        state.bridgeBulkSendSummaryWindow.sendModeRoundRobinFrames += 1;
      }
      return;
    } catch (error) {
      failures.push(`${id || '(original)'}:${safeErrorMessage(error)}`);
    }
  }

  throw new Error(`bulk single-client send failed: ${failures.join(', ')}`);
}

async function sendBulkWithRedundant2(destination, payload) {
  const client = state.bulkClient;
  if (!client || typeof client.sendWithClient !== 'function') {
    throw new Error('bulk sendWithClient is not available');
  }

  const ids = getBulkClientIdSequence();
  if (ids.length === 0) {
    throw new Error('no ready bulk subclients');
  }

  const selected = ids.length === 1 ? ids : ids.slice(0, 2);
  const options = { noReply: true };
  const messageId = createNknMessageId();
  if (messageId) {
    options.messageId = messageId;
  }

  const results = await Promise.allSettled(
    selected.map((id) => client.sendWithClient(id, destination, payload, options)));
  if (results.some((result) => result.status === 'fulfilled')) {
    state.bridgeBulkSendSummaryWindow.sendModeRedundant2Frames += 1;
    return;
  }

  const failures = results.map((result, index) => {
    const id = selected[index] || '(original)';
    return result.status === 'rejected'
      ? `${id}:${safeErrorMessage(result.reason)}`
      : `${id}:unknown`;
  });
  throw new Error(`bulk redundant2 send failed: ${failures.join(', ')}`);
}

async function sendBulkPayload(destination, payload) {
  const mode = getBulkSendMode();
  if (mode === BULK_SEND_MODE_REDUNDANT2) {
    try {
      await sendBulkWithRedundant2(destination, payload);
      return;
    } catch (error) {
      state.bridgeBulkSendSummaryWindow.sendModeFallbackFrames += 1;
      logStderr(`Bulk ${mode} send fell back to fanout: ${safeErrorMessage(error)}`);
    }
  }

  if (mode === BULK_SEND_MODE_ROUND_ROBIN || mode === BULK_SEND_MODE_SINGLE) {
    try {
      await sendBulkWithSingleClient(destination, payload, mode);
      return;
    } catch (error) {
      state.bridgeBulkSendSummaryWindow.sendModeFallbackFrames += 1;
      logStderr(`Bulk ${mode} send fell back to fanout: ${safeErrorMessage(error)}`);
    }
  }

  await callClientMethod('send', [destination, payload, { noReply: true }], 'bulk');
  state.bridgeBulkSendSummaryWindow.sendModeFanoutFrames += 1;
}

function isTransientBulkSendError(error) {
  const message = safeErrorMessage(error).toLowerCase();
  return message.includes('client not ready') ||
    message.includes('no ready bulk subclients') ||
    message.includes('not connected') ||
    message.includes('not ready');
}

function scheduleBulkSendRetry(item, error) {
  const nextAttempt = Math.max(0, Number(item.transientRetryAttempt) || 0) + 1;
  if (nextAttempt > BULK_QUEUE_TRANSIENT_RETRY_MAX_ATTEMPTS || state.shuttingDown) {
    return false;
  }

  item.transientRetryAttempt = nextAttempt;
  logStderr(
    `Bulk queue transient send retry scheduled ` +
    `(attempt=${nextAttempt}, delay_ms=${BULK_QUEUE_TRANSIENT_RETRY_DELAY_MS}, reason=${safeErrorMessage(error)})`);
  setTimeout(() => {
    if (state.shuttingDown) {
      return;
    }

    state.bulkSendQueue.unshift(item);
    state.bulkQueuedBytes += item.payload.length;
    emitBulkQueueState(true);
    scheduleBulkQueueDrain();
  }, BULK_QUEUE_TRANSIENT_RETRY_DELAY_MS);
  return true;
}

function recordBulkInFlightSnapshot() {
  const window = state.bridgeBulkSendSummaryWindow;
  const inFlight = Math.max(0, state.bulkQueueInFlight);
  const inFlightBytes = Math.max(0, state.bulkQueueInFlightBytes);
  window.inFlightMax = Math.max(window.inFlightMax, inFlight);
  window.inFlightBytesMax = Math.max(window.inFlightBytesMax, inFlightBytes);
  window.inFlightSampleSum += inFlight;
  window.inFlightSampleCount += 1;
  const effectiveConcurrency = getEffectiveBulkSendConcurrency();
  window.workerIdleSlotSamples += Math.max(0, effectiveConcurrency - inFlight);
  if (effectiveConcurrency > 0 && inFlight >= effectiveConcurrency) {
    window.workerSaturatedSampleCount += 1;
  }
}

function buildBulkQueueState() {
  const queueDepth = state.bulkSendQueue.length;
  const queuedBytes = state.bulkQueuedBytes;
  const oldestQueuedAgeMs = getBulkQueueOldestAgeMs();
  const inFlight = Math.max(0, state.bulkQueueInFlight);
  const congested =
    queueDepth >= BULK_QUEUE_CONGESTED_MESSAGES ||
    queuedBytes >= BULK_QUEUE_CONGESTED_BYTES ||
    oldestQueuedAgeMs >= BULK_QUEUE_CONGESTED_AGE_MS;
  const severe =
    queueDepth >= BULK_QUEUE_SEVERE_MESSAGES ||
    queuedBytes >= BULK_QUEUE_SEVERE_BYTES ||
    oldestQueuedAgeMs >= BULK_QUEUE_SEVERE_AGE_MS;

  return {
    queueDepth,
    queuedBytes,
    oldestQueuedAgeMs,
    inFlight,
    inFlightBytes: Math.max(0, state.bulkQueueInFlightBytes),
    configuredConcurrency: state.bulkSendConcurrency,
    effectiveConcurrency: getEffectiveBulkSendConcurrency(),
    congested,
    severe,
    clearedSinceLast: state.bulkQueueClearedSinceLast
  };
}

function emitBulkQueueState(force = false) {
  const snapshot = buildBulkQueueState();
  const key = JSON.stringify([
    snapshot.queueDepth,
    snapshot.queuedBytes,
    snapshot.oldestQueuedAgeMs > 0 ? 1 : 0,
    snapshot.inFlight,
    snapshot.inFlightBytes,
    snapshot.configuredConcurrency,
    snapshot.effectiveConcurrency,
    snapshot.congested ? 1 : 0,
    snapshot.severe ? 1 : 0,
    snapshot.clearedSinceLast
  ]);
  if (!force &&
      key === state.lastEmittedBulkQueueStateKey &&
      Date.now() - state.lastEmittedBulkQueueStateAt < 250) {
    return;
  }

  state.lastEmittedBulkQueueStateKey = key;
  state.lastEmittedBulkQueueStateAt = Date.now();
  emitJson({
    event: 'bulk_queue_state',
    ...snapshot
  });
  state.bulkQueueClearedSinceLast = 0;
}

function clearBulkSendQueue(reason) {
  if (state.bulkSendQueue.length > 0) {
    state.bulkQueueClearedSinceLast += state.bulkSendQueue.length;
    state.bridgeBulkSendSummaryWindow.queueClears += state.bulkSendQueue.length;
  }

  state.bulkSendQueue = [];
  state.bulkQueuedBytes = 0;
  logStderr(`Bulk queue cleared (${reason})`);
  emitBulkQueueState(true);
}

function enqueueBulkSend(destination, payload, binarySendFrameObservedUtcMs = 0) {
  const normalizedPayload = Buffer.isBuffer(payload) ? payload : Buffer.from(payload);
  const queueEnqueuedUtcMs = Date.now();
  state.bulkSendQueue.push({
    destination,
    payload: normalizedPayload,
    queuedAtMs: queueEnqueuedUtcMs,
    queueEnqueuedUtcMs,
    binarySendFrameObservedUtcMs: Number.isFinite(binarySendFrameObservedUtcMs)
      ? Math.max(0, Math.round(binarySendFrameObservedUtcMs))
      : 0
  });
  state.bulkQueuedBytes += normalizedPayload.length;
  const window = state.bridgeBulkSendSummaryWindow;
  window.framesEnqueued += 1;
  window.payloadBytesEnqueued += normalizedPayload.length;
  if (window.lastEnqueueUtcMs > 0) {
    recordBridgeMediaSendDuration(window.interEnqueueGapMs, queueEnqueuedUtcMs - window.lastEnqueueUtcMs);
  }
  window.lastEnqueueUtcMs = queueEnqueuedUtcMs;
  if (binarySendFrameObservedUtcMs > 0) {
    recordBridgeMediaSendDuration(
      state.bridgeBulkSendSummaryWindow.binarySendFrameObservedToQueueEnqueueMs,
      queueEnqueuedUtcMs - binarySendFrameObservedUtcMs);
  }
  emitBulkQueueState(true);
  scheduleBulkQueueDrain();
}

function scheduleBulkQueueDrain() {
  if (state.shuttingDown) {
    return;
  }

  state.bridgeBulkSendSummaryWindow.drainWakeCount += 1;
  while (state.bulkSendQueue.length > 0 &&
      state.bulkQueueInFlight < getEffectiveBulkSendConcurrency() &&
      !state.shuttingDown) {
    const item = state.bulkSendQueue.shift();
    if (!item) {
      break;
    }

    state.bulkQueuedBytes = Math.max(0, state.bulkQueuedBytes - item.payload.length);
    state.bulkQueueInFlight += 1;
    state.bulkQueueInFlightBytes += item.payload.length;
    recordBulkInFlightSnapshot();
    emitBulkQueueState(true);
    void sendBulkQueueItem(item).finally(() => {
      state.bulkQueueInFlight = Math.max(0, state.bulkQueueInFlight - 1);
      state.bulkQueueInFlightBytes = Math.max(0, state.bulkQueueInFlightBytes - item.payload.length);
      recordBulkInFlightSnapshot();
      emitBulkQueueState(true);
      scheduleBulkQueueDrain();
    });
  }
}

async function sendBulkQueueItem(item) {
  const queueDequeuedUtcMs = Date.now();
  if (item.queueEnqueuedUtcMs > 0) {
    recordBridgeMediaSendDuration(
      state.bridgeBulkSendSummaryWindow.queueEnqueueToQueueDequeueMs,
      queueDequeuedUtcMs - item.queueEnqueuedUtcMs);
  }
  const bulkSendStartedUtcMs = Date.now();
  recordBridgeMediaSendDuration(
    state.bridgeBulkSendSummaryWindow.queueDequeueToBulkSendStartedMs,
    bulkSendStartedUtcMs - queueDequeuedUtcMs);
  try {
    await sendBulkPayload(item.destination, item.payload);
    state.bridgeBulkSendSummaryWindow.framesSent += 1;
    state.bridgeBulkSendSummaryWindow.payloadBytesSent += item.payload.length;
    state.bridgeTransportHealthSummaryWindow.framesSentSinceLast += 1;
  } catch (error) {
    if (isTransientBulkSendError(error) && scheduleBulkSendRetry(item, error)) {
      return;
    }

    state.bridgeBulkSendSummaryWindow.sendFailures += 1;
    logStderr(`Bulk queue send failed: ${safeErrorMessage(error)}`);
  } finally {
    const bulkSendResolvedUtcMs = Date.now();
    recordBridgeMediaSendDuration(
      state.bridgeBulkSendSummaryWindow.bulkSendStartedToBulkSendResolvedMs,
      bulkSendResolvedUtcMs - bulkSendStartedUtcMs);
  }
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
  const candidates = typeof seedRpc === 'string' && seedRpc.trim().length > 0
    ? seedRpc
      .split(/[;,]/g)
      .map((x) => x.trim())
      .filter((x) => x.length > 0)
    : [...DEFAULT_RPC_SERVERS];

  const normalized = [];
  const seen = new Set();
  for (const candidate of candidates) {
    const rpc = normalizeRpcCandidate(candidate);
    const key = rpc.toLowerCase();
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    normalized.push(rpc);
  }

  return normalized;
}

function normalizeRpcCandidate(candidate) {
  const rpc = typeof candidate === 'string' ? candidate.trim() : '';
  if (/^https:\/\/seed\.nkn\.org:30003\/?$/i.test(rpc)) {
    return 'http://seed.nkn.org:30003';
  }

  return rpc;
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
    const rawPayload = msg.payload != null ? msg.payload : msg.data;
    const source = msg.src || msg.source || msg.from || '';
    const topic = typeof msg.topic === 'string' ? msg.topic : undefined;
    const isTopic = Boolean(msg.isTopic || msg.isTopicMessage || topic);
    return {
      source: String(source || ''),
      payload: toBufferPayload(rawPayload),
      receiveTimingMetadata: readReceiveTimingMetadata(rawPayload),
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
    receiveTimingMetadata: readReceiveTimingMetadata(payload),
    isTopic: false
  };
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
    state.clientReadyAtMs = Date.now();
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
    emitScreenShareQueueState(true);
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

    state.lastDisconnectReason = reason || 'Disconnected';
    state.bridgeTransportHealthSummaryWindow.disconnectCountSinceLast += 1;
    emitJson({
      event: 'disconnected',
      reason: reason || 'Disconnected'
    });
  };

  const onMessage = (...args) => {
    try {
      const msg = normalizeMessageEvent(args);
      recordBridgeMessageReceived(channel, Buffer.isBuffer(msg.payload) ? msg.payload.length : 0);
      const bridgeMessageObservedUtcMs = channel === 'media' ? Date.now() : 0;
      emitBinaryMessage(
        channel,
        msg.source,
        msg.payload,
        Boolean(msg.isTopic),
        msg.topic || null,
        bridgeMessageObservedUtcMs,
        msg.receiveTimingMetadata);
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
        state.bridgeTransportHealthSummaryWindow.connectFailedCountSinceLast += 1;
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
        state.bridgeTransportHealthSummaryWindow.wsErrorCountSinceLast += 1;
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
  state.controlNumSubClients = DEFAULT_NUM_SUBCLIENTS;
  state.mediaNumSubClients = DEFAULT_MEDIA_NUM_SUBCLIENTS;
  state.bulkNumSubClients = DEFAULT_NUM_SUBCLIENTS;
  state.bulkSendConcurrency = DEFAULT_BULK_SEND_CONCURRENCY;
  state.bulkSendMode = getConfiguredBulkSendMode();
  state.bulkRoundRobinCursor = 0;
  state.connectAttemptId = 0;
  state.clientReadyAtMs = 0;
  state.screenShareQueueMode = 'normal';
  state.screenShareQueueGeneration = 0;
  clearControlSendQueue('close_client');
  clearScreenShareQueue('close_client');
  clearBulkSendQueue('close_client');

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
  const subClientTopology = resolveSubClientTopology(command);
  state.controlNumSubClients = subClientTopology.control;
  state.mediaNumSubClients = subClientTopology.media;
  state.bulkNumSubClients = subClientTopology.bulk;
  state.bulkSendConcurrency = subClientTopology.bulkSendConcurrency;
  state.bulkSendMode = getConfiguredBulkSendMode();
  state.bulkRoundRobinCursor = 0;
  const baseOptions = {
    // MultiClient reliability defaults inspired by production NKN apps.
    numSubClients: subClientTopology.control,
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
    trackSelectedRpc(baseOptions.rpcServerAddr, 'initial');
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

  logStderr(`Creating NKN clients (rpc=${baseOptions.rpcServerAddr || 'default'}, control_subclients=${subClientTopology.control}, media_subclients=${subClientTopology.media}, bulk_subclients=${subClientTopology.bulk}, bulk_send_concurrency=${subClientTopology.bulkSendConcurrency}, bulk_send_mode=${state.bulkSendMode})`);
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
    numSubClients: subClientTopology.media,
    identifier: buildMediaIdentifier(requestedIdentifier || 'nlink')
  };
  const bulkOptions = {
    ...baseOptions,
    numSubClients: subClientTopology.bulk,
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
    const fallbackDelayMs = clampNumber(
      command.fallbackDelayMs,
      DEFAULT_CONNECT_READY_TIMEOUT_MS,
      MIN_CONNECT_FALLBACK_DELAY_MS,
      MAX_CONNECT_FALLBACK_DELAY_MS);
    tryFallbackRpcCandidates(connectAttemptId, command, rpcCandidates.slice(1), fallbackDelayMs);
  }
}

async function tryFallbackRpcCandidates(connectAttemptId, originalCommand, remainingRpcCandidates, fallbackDelayMs = DEFAULT_CONNECT_READY_TIMEOUT_MS) {
  for (const rpc of remainingRpcCandidates) {
    await delay(fallbackDelayMs);

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
      state.bridgeTransportHealthSummaryWindow.rpcFallbackAttemptCountSinceLast += 1;
      if (state.preflightProgressEnabled) {
        emitJson({
          event: 'rpc_fallback_attempt',
          connectId: state.connectId || null,
          rpc,
          fallbackDelayMs,
          ts: Date.now()
        });
      }
      await closeClient();
      state.connectAttemptId = connectAttemptId;
      state.connectId = typeof originalCommand.connectId === 'string' ? originalCommand.connectId : '';
      state.preflightProgressEnabled = Boolean(originalCommand.preflightRpcEnabled);

      const seed = decodeSeed(originalCommand.seedHex, originalCommand.seedBase64);
      const subClientTopology = resolveSubClientTopology(originalCommand);
      state.controlNumSubClients = subClientTopology.control;
      state.mediaNumSubClients = subClientTopology.media;
      state.bulkNumSubClients = subClientTopology.bulk;
      state.bulkSendConcurrency = subClientTopology.bulkSendConcurrency;
      state.bulkSendMode = getConfiguredBulkSendMode();
      state.bulkRoundRobinCursor = 0;
      const baseOptions = {
        numSubClients: subClientTopology.control,
        originalClient: true,
        reconnectIntervalMin: 1000,
        reconnectIntervalMax: 16000,
        responseTimeout: 5000,
        tls: false,
        rpcServerAddr: rpc,
        seedRPCServerAddr: rpc
      };
      trackSelectedRpc(rpc, 'fallback');

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
        numSubClients: subClientTopology.media,
        identifier: buildMediaIdentifier(requestedIdentifier || 'nlink')
      };
      const controlClient = new ClientCtor(controlOptions);
      const mediaClient = new ClientCtor(mediaOptions);
      const bulkOptions = {
        ...baseOptions,
        numSubClients: subClientTopology.bulk,
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

async function callClientMethodWithTimeout(methodName, args, channel = 'control', timeoutMs = DEFAULT_CONTROL_SEND_TIMEOUT_MS) {
  const normalizedTimeoutMs = clampNumber(
    timeoutMs,
    DEFAULT_CONTROL_SEND_TIMEOUT_MS,
    MIN_CONTROL_SEND_TIMEOUT_MS,
    MAX_CONTROL_SEND_TIMEOUT_MS);
  let timer = null;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => {
      reject(new Error(`${methodName}_timeout_after_${normalizedTimeoutMs}ms`));
    }, normalizedTimeoutMs);
    if (timer && typeof timer.unref === 'function') {
      timer.unref();
    }
  });

  try {
    return await Promise.race([
      callClientMethod(methodName, args, channel),
      timeout
    ]);
  } finally {
    if (timer) {
      clearTimeout(timer);
    }
  }
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

  if (frame.channel === 'media') {
    enqueueScreenShareSend(destination, frame.payload, Date.now());
    return;
  }

  if (frame.channel === 'bulk') {
    enqueueBulkSend(destination, frame.payload, Date.now());
    return;
  }

  enqueueControlSend(destination, frame.payload, Date.now());
}

async function handleSetScreenSharePolicy(command) {
  const nextMode = String(command.mode || '').trim().toLowerCase() === 'catch_up_only'
    ? 'catch_up_only'
    : 'normal';
  const flushQueued = Boolean(command.flushQueued);
  const nextGeneration = Number.isFinite(Number(command.generation))
    ? Math.max(0, Number(command.generation))
    : state.screenShareQueueGeneration;

  const generationChanged = nextGeneration !== state.screenShareQueueGeneration;
  state.screenShareQueueMode = nextMode;
  state.screenShareQueueGeneration = nextGeneration;
  if (generationChanged || flushQueued) {
    clearScreenShareQueue(generationChanged ? 'generation_changed' : 'policy_flush');
  } else {
    emitScreenShareQueueState(true);
  }
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
startOwnerPidMonitor();

logStderr('Bridge started');
