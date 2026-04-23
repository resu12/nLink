using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

public abstract class SessionRuntimeConnectionTestBase : CoreSmokeTestsBase
{
    private sealed class FakeSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IAddressHostSignalingTransport
    {
        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => Task.CompletedTask;
        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => Task.CompletedTask;
        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
        public void RaiseSessionKeyReady(byte[] sharedKey)
        {
            SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
        }

        public void RaiseChatMessage(byte[] payload)
        {
            ChatMessageReceived?.Invoke(this, new TransportChatMessageEventArgs(payload));
        }
    }

    #pragma warning restore CS0067
    protected sealed class FakeProtectedSeedBackend : NknSecretStore.IProtectedSeedBackend
    {
        public Dictionary<string, byte[]> StoredSeeds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[]? TryLoadSeed(string keyPath)
        {
            return StoredSeeds.TryGetValue(Path.GetFullPath(keyPath), out var seed) ? seed.ToArray() : null;
        }

        public void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
        {
            StoredSeeds[Path.GetFullPath(keyPath)] = seedBytes.ToArray();
        }

        public void DeleteSeed(string keyPath)
        {
            StoredSeeds.Remove(Path.GetFullPath(keyPath));
        }
    }

    protected sealed class CorruptedProtectedSeedBackend : NknSecretStore.IProtectedSeedBackend
    {
        public Dictionary<string, byte[]> StoredSeeds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ThrowOnLoad { get; set; }

        public byte[]? TryLoadSeed(string keyPath)
        {
            var normalized = Path.GetFullPath(keyPath);
            if (ThrowOnLoad && StoredSeeds.ContainsKey(normalized))
            {
                throw new CryptographicException("The data is invalid.");
            }

            return StoredSeeds.TryGetValue(normalized, out var seed) ? seed.ToArray() : null;
        }

        public void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
        {
            var normalized = Path.GetFullPath(keyPath);
            StoredSeeds[normalized] = seedBytes.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(normalized)!);
            File.WriteAllText(NknSecretStore.GetSecretPath(normalized), "corrupted-backend-stub");
        }

        public void DeleteSeed(string keyPath)
        {
            var normalized = Path.GetFullPath(keyPath);
            StoredSeeds.Remove(normalized);
            var secretPath = NknSecretStore.GetSecretPath(normalized);
            if (File.Exists(secretPath))
            {
                File.Delete(secretPath);
            }
        }
    }

    protected sealed class UnavailableProtectedSeedBackend : NknSecretStore.IProtectedSeedBackend
    {
        public byte[]? TryLoadSeed(string keyPath)
        {
            throw new InvalidOperationException($"Protected NKN seed storage is unavailable for '{keyPath}'.");
        }

        public void SaveSeed(string keyPath, ReadOnlySpan<byte> seedBytes)
        {
            throw new InvalidOperationException($"Protected NKN seed storage is unavailable for '{keyPath}'.");
        }

        public void DeleteSeed(string keyPath)
        {
            throw new InvalidOperationException($"Protected NKN seed storage is unavailable for '{keyPath}'.");
        }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public string LastText { get; private set; } = string.Empty;

        public Task SetTextAsync(string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingRemoteInputInjector : IRemoteInputInjector
    {
        public bool IsSupported => true;
        public int MouseMoveCount { get; private set; }
        public int KeyInjectionCount { get; private set; }

        public void InjectMouseMoveAbsolute(int xPx, int yPx)
        {
            _ = xPx;
            _ = yPx;
            MouseMoveCount++;
        }

        public void InjectMouseButton(RemoteMouseButton button, RemoteButtonAction action)
        {
            _ = button;
            _ = action;
        }

        public void InjectMouseWheel(int deltaX, int deltaY)
        {
            _ = deltaX;
            _ = deltaY;
        }

        public void InjectKey(RemoteKey key, RemoteKeyAction action, RemoteKeyModifiers mods)
        {
            _ = key;
            _ = action;
            _ = mods;
            KeyInjectionCount++;
        }
    }

    private sealed class ScriptedSignalingTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport, IRemoteControlCapabilityProvider, IRemoteControlSignalingTransport, IHelpRequestSignalingTransport
    {
        private readonly Func<string, CancellationToken, Task> onJoinByAddressAsync;
        private readonly Func<string, ValidatedInviteV1, CancellationToken, Task> onJoinByInviteAsync;
        private readonly Func<CancellationToken, Task> onHostByAddressAsync;
        private readonly Func<HelpRequestMessage, CancellationToken, Task> onSendHelpRequestAsync;
        private readonly Func<HelpRequestDecisionMessage, CancellationToken, Task> onSendHelpRequestDecisionAsync;
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> onSendChatAsync;
        private readonly Func<ControlRequestMessageV1, CancellationToken, Task> onSendControlRequestAsync;
        private readonly Func<ControlResponseMessageV1, CancellationToken, Task> onSendControlResponseAsync;
        private readonly Func<ControlStartMessageV1, CancellationToken, Task> onSendControlStartAsync;
        private readonly Func<ControlStopMessageV1, CancellationToken, Task> onSendControlStopAsync;
        private readonly Func<ControlInputMessageV1, CancellationToken, Task> onSendControlInputAsync;
        private readonly Func<ControlInputAckV1, CancellationToken, Task> onSendControlAckAsync;
        private readonly Func<ControlStateSnapshotV1, CancellationToken, Task> onSendControlStateSnapshotAsync;
        private readonly Func<ControlDisplayInfoMessageV1, CancellationToken, Task> onSendControlDisplayInfoAsync;
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        public ScriptedSignalingTransport(Func<string, CancellationToken, Task>? onJoinByAddressAsync = null, Func<string, ValidatedInviteV1, CancellationToken, Task>? onJoinByInviteAsync = null, Func<CancellationToken, Task>? onHostByAddressAsync = null, string? localPeerAddress = null, Func<HelpRequestMessage, CancellationToken, Task>? onSendHelpRequestAsync = null, Func<HelpRequestDecisionMessage, CancellationToken, Task>? onSendHelpRequestDecisionAsync = null, Func<ReadOnlyMemory<byte>, CancellationToken, Task>? onSendChatAsync = null, Func<ControlRequestMessageV1, CancellationToken, Task>? onSendControlRequestAsync = null, Func<ControlResponseMessageV1, CancellationToken, Task>? onSendControlResponseAsync = null, Func<ControlStartMessageV1, CancellationToken, Task>? onSendControlStartAsync = null, Func<ControlStopMessageV1, CancellationToken, Task>? onSendControlStopAsync = null, Func<ControlInputMessageV1, CancellationToken, Task>? onSendControlInputAsync = null, Func<ControlInputAckV1, CancellationToken, Task>? onSendControlAckAsync = null, Func<ControlStateSnapshotV1, CancellationToken, Task>? onSendControlStateSnapshotAsync = null, Func<ControlDisplayInfoMessageV1, CancellationToken, Task>? onSendControlDisplayInfoAsync = null, bool localSupportsRemoteControl = true, bool remoteSupportsRemoteControl = true)
        {
            this.onJoinByAddressAsync = onJoinByAddressAsync ?? ((_, ct) => Task.Delay(Timeout.Infinite, ct));
            this.onJoinByInviteAsync = onJoinByInviteAsync ?? ((_, invite, ct) => this.onJoinByAddressAsync(invite.TargetAddress.Value, ct));
            this.onHostByAddressAsync = onHostByAddressAsync ?? (ct => Task.Delay(Timeout.Infinite, ct));
            LocalPeerAddress = string.IsNullOrWhiteSpace(localPeerAddress) ? "scripted.local.peer" : localPeerAddress.Trim();
            this.onSendHelpRequestAsync = onSendHelpRequestAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendHelpRequestDecisionAsync = onSendHelpRequestDecisionAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendChatAsync = onSendChatAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlRequestAsync = onSendControlRequestAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlResponseAsync = onSendControlResponseAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlStartAsync = onSendControlStartAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlStopAsync = onSendControlStopAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlInputAsync = onSendControlInputAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlAckAsync = onSendControlAckAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlStateSnapshotAsync = onSendControlStateSnapshotAsync ?? ((_, _) => Task.CompletedTask);
            this.onSendControlDisplayInfoAsync = onSendControlDisplayInfoAsync ?? ((_, _) => Task.CompletedTask);
            LocalSupportsRemoteControl = localSupportsRemoteControl;
            RemoteSupportsRemoteControl = remoteSupportsRemoteControl;
        }

        public string LocalPeerAddress { get; }
        public bool LocalSupportsRemoteControl { get; }
        public bool RemoteSupportsRemoteControl { get; }
        public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<IncomingHelpRequestEventArgs>? IncomingHelpRequest;
        public event EventHandler<HelpRequestDecisionEventArgs>? HelpRequestDecisionReceived;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
        public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
        public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
        public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
        public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
        public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
        public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
        public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => onHostByAddressAsync(ct);
        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => onJoinByAddressAsync(peerAddress, ct);
        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct) => onJoinByInviteAsync(inviteToken, invite, ct);
        public Task SendHelpRequestAsync(HelpRequestMessage request, CancellationToken ct) => onSendHelpRequestAsync(request, ct);
        public Task SendHelpRequestDecisionAsync(HelpRequestDecisionMessage decision, CancellationToken ct) => onSendHelpRequestDecisionAsync(decision, ct);
        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => onSendChatAsync(payload, ct);
        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct) => onSendControlRequestAsync(message, ct);
        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct) => onSendControlResponseAsync(message, ct);
        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct) => onSendControlStartAsync(message, ct);
        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct) => onSendControlStopAsync(message, ct);
        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct) => onSendControlInputAsync(message, ct);
        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct) => onSendControlAckAsync(message, ct);
        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => onSendControlStateSnapshotAsync(message, ct);
        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct) => onSendControlDisplayInfoAsync(message, ct);
        public void RaiseDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        public void InjectIncomingControlRequest(ControlRequestMessageV1 message, string? peerId)
        {
            RemoteControlRequestReceived?.Invoke(this, new RemoteControlRequestReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlResponse(ControlResponseMessageV1 message, string? peerId)
        {
            RemoteControlResponseReceived?.Invoke(this, new RemoteControlResponseReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlStart(ControlStartMessageV1 message, string? peerId)
        {
            RemoteControlStartReceived?.Invoke(this, new RemoteControlStartReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlStop(ControlStopMessageV1 message, string? peerId)
        {
            RemoteControlStopReceived?.Invoke(this, new RemoteControlStopReceivedEventArgs(message, peerId));
        }
    }

    private sealed class ControlledDelayScheduler
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> pending = new();
        public int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return pending.Count(t => !t.Task.IsCompleted);
                }
            }
        }

        public Task DelayAsync(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            ctr = ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                ctr.Dispose();
            });
            lock (gate)
            {
                pending.Add(tcs);
            }

            return tcs.Task;
        }

        public void CompleteLatest()
        {
            lock (gate)
            {
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].TrySetResult())
                    {
                        return;
                    }
                }
            }

            throw new InvalidOperationException("No pending delay task to complete.");
        }
    }

    private sealed class FakeScreenShareClock : IScreenShareClock
    {
        private DateTimeOffset utcNow;
        public FakeScreenShareClock(DateTimeOffset initialUtcNow)
        {
            utcNow = initialUtcNow;
        }

        public DateTimeOffset UtcNow => utcNow;

        public void Advance(TimeSpan by)
        {
            utcNow = utcNow.Add(by);
        }
    }

    private sealed class FakeSessionTransportNetwork
    {
        private readonly object gate = new();
        private readonly Dictionary<string, FakeSessionTransport> hostsByAddress = new(StringComparer.Ordinal);
        public FakeSessionTransport CreateTransport(string address)
        {
            return new FakeSessionTransport(this, address);
        }

        public void RegisterHost(string address, FakeSessionTransport host)
        {
            lock (gate)
            {
                hostsByAddress[address] = host;
            }
        }

        public void UnregisterHost(FakeSessionTransport transport)
        {
            lock (gate)
            {
                foreach (var pair in hostsByAddress.ToArray())
                {
                    if (ReferenceEquals(pair.Value, transport))
                    {
                        hostsByAddress.Remove(pair.Key);
                    }
                }
            }
        }

        public FakeSessionTransport? TryFindHost(string address)
        {
            lock (gate)
            {
                return hostsByAddress.TryGetValue(address, out var host) ? host : null;
            }
        }
    }

    private sealed class FakeSessionTransport : ISignalingTransport, IAddressTargetSignalingTransport, IInviteTargetSignalingTransport, IAddressHostSignalingTransport, IHostReadySignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = CoreSmokeTestsBase.SHA256LikeDeterministicBytes("session-runtime-repeat-key", 32);
        private readonly TaskCompletionSource<bool> hostReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;
        private FakeSessionTransport? peer;
        private bool disposed;
        public FakeSessionTransport(FakeSessionTransportNetwork network, string address)
        {
            this.network = network;
            Address = address;
        }

        public string Address { get; }
        public string LocalPeerAddress => Address;

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public Task WaitUntilHostReadyAsync(CancellationToken ct) => hostReadyTcs.Task.WaitAsync(ct);
        public Task HostByAddressAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            network.RegisterHost(Address, this);
            UpdateSessionSecurityState(SessionSecurityState.CreateHelpeeWaiting(new PeerAddress(Address)));
            hostReadyTcs.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct)
        {
            ThrowIfDisposed();
            var host = network.TryFindHost(peerAddress) ?? throw new TimeoutException("Host not found.");
            return JoinCoreAsync(host, new SessionId($"fake_session_{Guid.NewGuid():N}"), SessionSecurityDefaults.AllCapabilityGrants, inviteValidated: true);
        }

        public Task JoinByInviteAsync(string inviteToken, ValidatedInviteV1 invite, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(inviteToken))
            {
                throw new ArgumentException("Invite token is required.", nameof(inviteToken));
            }

            ArgumentNullException.ThrowIfNull(invite);
            var host = network.TryFindHost(invite.TargetAddress.Value) ?? throw new TimeoutException("Host not found.");
            var helperAddress = new PeerAddress(Address);
            if (invite.BoundHelperAddress is not null && invite.BoundHelperAddress != helperAddress)
            {
                throw new InvalidOperationException("Invite token is bound to a different helper identity.");
            }

            return JoinCoreAsync(host, invite.SessionId, invite.Payload.Capabilities.ToCapabilityGrant(), inviteValidated: true);
        }

        private Task JoinCoreAsync(FakeSessionTransport host, SessionId sessionId, CapabilityGrant requestedCapabilities, bool inviteValidated)
        {
            peer = host;
            host.peer = this;
            var helpeeAddress = new PeerAddress(host.Address);
            var helperAddress = new PeerAddress(Address);
            var approvalRequest = new ApprovalRequest(helperAddress, requestedCapabilities, sessionId);
            var verifiedState = CreateVerifiedSecurityState(sessionId, helpeeAddress, helperAddress, inviteValidated);
            UpdateSessionSecurityState(verifiedState);
            host.UpdateSessionSecurityState(verifiedState);
            var joinRequest = new IncomingJoinRequestEventArgs(approveAsync: (decision, _) =>
            {
                if (decision is null)
                {
                    throw new InvalidOperationException("Explicit approval decision is required.");
                }

                ValidateApprovalDecision(approvalRequest, decision);
                var grant = decision.ToGrant();
                host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.WithApproval(grant));
                UpdateSessionSecurityState(CurrentSessionSecurityState.WithApproval(grant));
                host.SessionKeyReady?.Invoke(host, new TransportSessionKeyReadyEventArgs(host.sharedKey));
                SessionKeyReady?.Invoke(this, new TransportSessionKeyReadyEventArgs(sharedKey));
                host.Approved?.Invoke(host, EventArgs.Empty);
                Approved?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }, rejectAsync: (reason, _) =>
            {
                var rejectionReason = string.IsNullOrWhiteSpace(reason) ? "local_reject" : reason.Trim();
                host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.Invalidate(rejectionReason));
                UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate(rejectionReason));
                Rejected?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }, approvalRequest: approvalRequest);
            host.IncomingJoinRequest?.Invoke(host, joinRequest);
            return Task.CompletedTask;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            var target = peer ?? throw new InvalidOperationException("No peer connected.");
            target.ChatMessageReceived?.Invoke(target, new TransportChatMessageEventArgs(payload.ToArray()));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            network.UnregisterHost(this);
            if (peer is { } target)
            {
                peer = null;
                target.peer = null;
                UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.UpdateSessionSecurityState(target.CurrentSessionSecurityState.Invalidate("transport_disposed"));
                target.Disconnected?.Invoke(target, EventArgs.Empty);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FakeSessionTransport));
            }
        }

        private void UpdateSessionSecurityState(SessionSecurityState nextState)
        {
            if (Equals(currentSessionSecurityState, nextState))
            {
                return;
            }

            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }

        protected static SessionSecurityState CreateVerifiedSecurityState(SessionId sessionId, PeerAddress helpeeAddress, PeerAddress helperAddress, bool inviteValidated)
        {
            return (SessionSecurityState.Empty with
            {
                SessionId = sessionId,
                HelpeeAddress = helpeeAddress,
                HelperAddress = helperAddress,
                InviteValidated = inviteValidated,
            }

            ).WithHandshakeVerified(helperAddress);
        }

        protected static void ValidateApprovalDecision(ApprovalRequest approvalRequest, ApprovalDecision decision)
        {
            if (decision.SessionId != approvalRequest.SessionId || decision.HelperIdentity != approvalRequest.HelperIdentity || decision.ExpiresAtUtc <= DateTimeOffset.UtcNow || (decision.ApprovedCapabilities & ~approvalRequest.RequestedCapabilities) != 0)
            {
                throw new InvalidOperationException("Approval decision does not match the pending approval request.");
            }
        }
    }

    private sealed class FakeStatusPresenterSource : IStatusPresenterSource
    {
        private SessionRuntimeState uiState = SessionRuntimeState.Idle;
        private TransportState transportState = TransportState.Idle;
        private string statusText = string.Empty;
        private TransportFailure? failure;
        private long attempt;
        public event EventHandler<SessionRuntimeStateChangedEventArgs>? StateChanged;
        public event EventHandler<SessionRuntimeTransientStatusChangedEventArgs>? TransientStatusChanged;
        public SessionRuntimeState State => uiState;
        public TransportState TransportLifecycleState => transportState;
        public string StatusText => statusText;
        public TransportFailure? LastTransportFailure => failure;

        public DiagnosticsSnapshot GetDiagnosticsSnapshot() => new(CurrentState: transportState.ToString(), SessionUiState: uiState.ToString(), AttemptNumber: attempt, LastFailureCategory: failure?.Category.ToString() ?? string.Empty, LastFailureMessage: failure?.Message ?? string.Empty, LastConnectDurationMs: null, LastHandshakeDurationMs: null, LastBridgeStartDurationMs: null);
        public void SetAttempt(long value) => attempt = value;
        public void SetTransportState(TransportState state) => transportState = state;
        public void SetSessionUiState(SessionRuntimeState state) => uiState = state;
        public void SetStatusText(string text) => statusText = text ?? string.Empty;
        public void SetFailure(TransportFailure? transportFailure) => failure = transportFailure;
        public void RaiseStateChanged() => StateChanged?.Invoke(this, new SessionRuntimeStateChangedEventArgs(uiState, SessionRuntimeRole.Helper, statusText));
        public void RaiseTransient(bool isVisible, string text, bool canCancel)
        {
            statusText = text ?? string.Empty;
            TransientStatusChanged?.Invoke(this, new SessionRuntimeTransientStatusChangedEventArgs(isVisible, statusText, canCancel));
        }
    }

    private sealed class FakeManualTimer : NLink.App.Services.ITimer
    {
        private Action? callback;
        private bool disposed;
        public bool IsRunning { get; private set; }

        public void Start(TimeSpan dueTime, TimeSpan period, Action callback)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
            IsRunning = true;
        }

        public void Stop()
        {
            IsRunning = false;
            callback = null;
        }

        public void Tick()
        {
            if (disposed || !IsRunning || callback is null)
            {
                return;
            }

            callback();
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }
    }

}


