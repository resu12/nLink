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

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class SessionRuntimeConnectionLifecycleTests : SessionRuntimeConnectionTestBase
{
    [Fact]
    public async Task RecoveryStateContract_LivenessDefersForRuntimeUnlockRetryUntilCoordinatorDecision()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var heartbeatCount = 0;
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: (_, _) =>
            {
                Interlocked.Increment(ref heartbeatCount);
                return Task.CompletedTask;
            });
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(2),
            SessionLivenessTimeout = TimeSpan.FromSeconds(5),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(
            new PeerAddress(scripted.LocalPeerAddress),
            new PeerAddress("scripted.helpee.recovery-contract"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        scripted.SetSessionRecoveryContractForTests(new SessionRecoveryContractSnapshot(
            sessionId,
            "ft_recovery_contract_liveness",
            ContractGeneration: 11,
            OfferGeneration: 7,
            Kind: SessionRecoveryContractKind.RuntimeUnlockActivation,
            State: SessionRecoveryContractState.RetryQueued,
            RetryReason: "runtime_unlock_offer_send_not_observed",
            RecoveryReason: "tuna_activation_offer_send_timeout",
            CreatedUtc: now,
            RetryDeadlineUtc: now.AddSeconds(20),
            LivenessDeferralDeadlineUtc: now.AddSeconds(20),
            RecoveryPending: false,
            RecoverySettled: true,
            RetryRequired: true,
            RetryDispatching: false,
            RetryDispatched: false,
            RetryObserved: false,
            QueuedBehindActiveNegotiation: true));
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        now = now.AddSeconds(6);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal(0, disconnected);
        Assert.True(Volatile.Read(ref heartbeatCount) > 0);

        scripted.SetSessionRecoveryContractForTests(new SessionRecoveryContractSnapshot(
            sessionId,
            "ft_recovery_contract_liveness",
            ContractGeneration: 11,
            OfferGeneration: 7,
            Kind: SessionRecoveryContractKind.RuntimeUnlockActivation,
            State: SessionRecoveryContractState.Failed,
            RetryReason: "runtime_unlock_offer_send_not_observed",
            RecoveryReason: "tuna_activation_offer_send_timeout",
            CreatedUtc: now.AddSeconds(-10),
            RetryDeadlineUtc: now.AddSeconds(-1),
            LivenessDeferralDeadlineUtc: now.AddSeconds(-1),
            RecoveryPending: false,
            RecoverySettled: true,
            RetryRequired: false,
            RetryDispatching: false,
            RetryDispatched: false,
            RetryObserved: false,
            QueuedBehindActiveNegotiation: true));

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(6);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public void SessionRuntime_FileTransferPeerDisconnected_DoesNotEndConnectedSession()
    {
        var helperAddress = new PeerAddress("helper.file.peer.disconnect");
        var helpeeAddress = new PeerAddress("helpee.file.peer.disconnect");
        var sessionId = new SessionId("sess_file_peer_disconnect");
        var grant = new SessionGrant(
            helperAddress,
            CapabilityGrant.Chat | CapabilityGrant.FileTransfer,
            sessionId,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var securityState = CreateVerifiedSecurityState(helpeeAddress, helperAddress, sessionId)
            .WithApproval(grant);
        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(localPeerAddress: helperAddress.Value),
            SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "sessionSecurityState", securityState);
        SetPrivateField(runtime, "currentSessionGrant", grant);
        SetPrivateField(
            runtime,
            "sessionFlowState",
            new SessionFlowState(
                Phase: SessionFlowPhase.ActiveSession,
                LastEndOrigin: SessionFlowEndOrigin.None,
                LocalEndInProgress: false,
                HadActiveSession: true,
                FailureReason: string.Empty));
        var remoteEnded = 0;
        var fileTransferChanged = 0;
        runtime.RemoteSessionEnded += (_, _) => remoteEnded++;
        runtime.FileTransferChanged += (_, _) => fileTransferChanged++;

        var transfer = new FileTransferTransferSnapshot(
            sessionId.Value,
            "ft_peer_disconnect",
            FileTransferDirection.Inbound,
            FileTransferTransferState.Failed,
            "peer-left.bin",
            1024,
            Sha256Base64: null,
            BytesTransferred: 128,
            ChunksTransferred: 1,
            ChunkCount: 8,
            ChunkSizeBytes: 128,
            ErrorCode: FileTransferResultCodes.PeerDisconnected,
            StatusMessage: "Peer disconnected.");
        var snapshot = new SessionFileTransferSnapshot(Outbound: null, Inbound: transfer);

        InvokePrivateMethod(runtime, "OnFileTransferChanged", runtime, new SessionFileTransferSnapshotChangedEventArgs(snapshot));

        Assert.Equal(0, remoteEnded);
        Assert.Equal(1, fileTransferChanged);
        Assert.False(runtime.LastDisconnectWasRemoteEnd);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal(SessionFlowEndOrigin.None, runtime.FlowSnapshot.LastEndOrigin);
    }

    [Fact]
    public void SessionRuntime_FileTransferSessionEndCancel_EndsConnectedSession()
    {
        var helperAddress = new PeerAddress("helper.file.session.end");
        var helpeeAddress = new PeerAddress("helpee.file.session.end");
        var sessionId = new SessionId("sess_file_session_end");
        var grant = new SessionGrant(
            helperAddress,
            CapabilityGrant.Chat | CapabilityGrant.FileTransfer,
            sessionId,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var securityState = CreateVerifiedSecurityState(helpeeAddress, helperAddress, sessionId)
            .WithApproval(grant);
        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(localPeerAddress: helperAddress.Value),
            SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        SetPrivateField(runtime, "sessionSecurityState", securityState);
        SetPrivateField(runtime, "currentSessionGrant", grant);
        SetPrivateField(
            runtime,
            "sessionFlowState",
            new SessionFlowState(
                Phase: SessionFlowPhase.ActiveSession,
                LastEndOrigin: SessionFlowEndOrigin.None,
                LocalEndInProgress: false,
                HadActiveSession: true,
                FailureReason: string.Empty));
        var remoteEnded = 0;
        runtime.RemoteSessionEnded += (_, _) => remoteEnded++;

        var transfer = new FileTransferTransferSnapshot(
            sessionId.Value,
            "ft_peer_session_end",
            FileTransferDirection.Inbound,
            FileTransferTransferState.Canceled,
            "peer-ended.bin",
            1024,
            Sha256Base64: null,
            BytesTransferred: 128,
            ChunksTransferred: 1,
            ChunkCount: 8,
            ChunkSizeBytes: 128,
            ErrorCode: FileTransferResultCodes.CanceledRemote,
            StatusMessage: "session_end");
        var snapshot = new SessionFileTransferSnapshot(Outbound: null, Inbound: transfer);

        InvokePrivateMethod(runtime, "OnFileTransferChanged", runtime, new SessionFileTransferSnapshotChangedEventArgs(snapshot));

        Assert.Equal(1, remoteEnded);
        Assert.True(runtime.LastDisconnectWasRemoteEnd);
        Assert.Equal(SessionFlowEndOrigin.Remote, runtime.FlowSnapshot.LastEndOrigin);
    }

    [Fact]
    public void SessionRuntime_DefaultHumanApprovalTimers_AreAligned()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
        var outboundDecisionTimeoutField = typeof(SessionRuntime).GetField(
            "outboundHelpRequestDecisionTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(outboundDecisionTimeoutField);
        Assert.Equal(TimeSpan.FromSeconds(45), SessionApprovalTimeouts.DefaultHumanDecisionTimeout);
        Assert.Equal(
            SessionApprovalTimeouts.DefaultHumanDecisionTimeout,
            SessionRuntimeWatchdogOptions.Default.HandshakeTimeout);
        Assert.Equal(
            SessionApprovalTimeouts.DefaultHumanDecisionTimeout,
            Assert.IsType<TimeSpan>(outboundDecisionTimeoutField.GetValue(runtime)));
        Assert.Equal(TimeSpan.FromSeconds(2), SessionRuntimeWatchdogOptions.Default.SessionLivenessHeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(6), SessionRuntimeWatchdogOptions.Default.SessionLivenessSuspectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(18), SessionRuntimeWatchdogOptions.Default.SessionLivenessTimeout);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessWatchdog_StartsOnlyAfterApprovedConnected()
    {
        var delay = new ControlledDelayScheduler();
        var heartbeatCount = 0;
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: (message, _) =>
            {
                Interlocked.Increment(ref heartbeatCount);
                Assert.False(string.IsNullOrWhiteSpace(message.SessionId));
                return Task.CompletedTask;
            });
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.liveness")));

        Assert.Equal(0, delay.PendingCount);
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();
        await WaitUntilAsync(() => Volatile.Read(ref heartbeatCount) > 0, TimeSpan.FromSeconds(1));

        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessProof_PreventsTimeout()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress("scripted.helpee.liveness"), new PeerAddress(scripted.LocalPeerAddress));
        scripted.SetSessionSecurityStateForTests(securityState);
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        now = now.AddSeconds(8);
        scripted.InjectSessionLivenessProof(securityState.SessionId!.Value.Value, sequence: 7);
        delay.CompleteLatest();
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(8);
        delay.CompleteLatest();
        await Task.Delay(100);

        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_TransitionsToConnectionLost()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress("scripted.helpee.timeout"), new PeerAddress(scripted.LocalPeerAddress)));
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        now = now.AddSeconds(10);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(2));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.False(runtime.LastDisconnectWasRemoteEnd);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_DoesNotDeferDuringBridgeReceiveRecovery()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress("scripted.helpee.recovery"), new PeerAddress(scripted.LocalPeerAddress)));
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "tuna_activation_offer_send_timeout"));
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryCompleted,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "tuna_activation_offer_send_timeout"));
        now = now.AddSeconds(10);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_DoesNotDeferDuringActiveFileTransferRecoveryCooldown()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.recovery.cooldown")));
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        now = now.AddSeconds(8);
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "reason=active_filetransfer_unproven_cooldown:stall=bulk_receive_stalled:connect=bulk"));
        now = now.AddSeconds(2);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_RepeatedBridgeRecoveryDoesNotExtendTimeout()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress("scripted.helpee.recovery.loop"), new PeerAddress(scripted.LocalPeerAddress)));
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        now = now.AddSeconds(10);
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "reason=active_filetransfer_unproven_cooldown:stall=bulk_receive_stalled:connect=first"));
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_ActiveFileTransferRequestsRecoveryBeforeDisconnect()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var recoveryRequests = new ConcurrentQueue<FileTransferReceiveRecoveryRequest>();
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask,
            onRequestFileTransferReceiveRecovery: recoveryRequests.Enqueue);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.filetransfer.progress"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        void PublishProgress(long bytesAccepted)
        {
            var transfer = new FileTransferTransferSnapshot(
                sessionId,
                "ft_liveness_active_progress",
                FileTransferDirection.Outbound,
                FileTransferTransferState.Sending,
                "active-progress.bin",
                64 * 1024,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: (int)(bytesAccepted / 1024),
                ChunkCount: 64,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: null,
                BytesAcceptedForTransport: bytesAccepted,
                BytesAcknowledgedByReceiver: 0);
            var snapshot = new SessionFileTransferSnapshot(Outbound: transfer, Inbound: null);
            InvokePrivateMethod(runtime, "OnFileTransferChanged", runtime, new SessionFileTransferSnapshotChangedEventArgs(snapshot));
        }

        now = now.AddSeconds(8);
        PublishProgress(1024);
        now = now.AddSeconds(2);
        delay.CompleteLatest();
        await WaitUntilAsync(() => recoveryRequests.Count == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);

        var request = Assert.Single(recoveryRequests);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal("ft_liveness_active_progress", request.TransferId);
        Assert.Equal(FileTransferDirection.Outbound, request.Direction);
        Assert.Equal("session_liveness_timeout_pending", request.Reason);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(11);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_ActiveFileTransferBridgeRecoveryExtendsProofWindow()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var recoveryRequests = new ConcurrentQueue<FileTransferReceiveRecoveryRequest>();
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask,
            onRequestFileTransferReceiveRecovery: recoveryRequests.Enqueue);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.filetransfer.bridge-recovery"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        void PublishProgress()
        {
            var transfer = new FileTransferTransferSnapshot(
                sessionId,
                "ft_liveness_bridge_recovery",
                FileTransferDirection.Outbound,
                FileTransferTransferState.Sending,
                "bridge-recovery.bin",
                64 * 1024,
                Sha256Base64: null,
                BytesTransferred: 0,
                ChunksTransferred: 1,
                ChunkCount: 64,
                ChunkSizeBytes: 1024,
                ErrorCode: null,
                StatusMessage: null,
                BytesAcceptedForTransport: 1024,
                BytesAcknowledgedByReceiver: 0);
            var snapshot = new SessionFileTransferSnapshot(Outbound: transfer, Inbound: null);
            InvokePrivateMethod(runtime, "OnFileTransferChanged", runtime, new SessionFileTransferSnapshotChangedEventArgs(snapshot));
        }

        now = now.AddSeconds(10);
        PublishProgress();
        delay.CompleteLatest();
        await WaitUntilAsync(() => recoveryRequests.Count == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(8);
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "regular_v4_unproven_recovery_escalation"));

        now = now.AddSeconds(3);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal(0, disconnected);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(13);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_ActiveBridgeRecoveryStartOverLimitStillExtendsProofWindow()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helpee.filetransfer.bridge-recovery-limit"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        var transfer = new FileTransferTransferSnapshot(
            sessionId,
            "ft_liveness_bridge_recovery_limit",
            FileTransferDirection.Inbound,
            FileTransferTransferState.Receiving,
            "bridge-recovery-limit.bin",
            64 * 1024,
            Sha256Base64: null,
            BytesTransferred: 1024,
            ChunksTransferred: 1,
            ChunkCount: 64,
            ChunkSizeBytes: 1024,
            ErrorCode: null,
            StatusMessage: null,
            BytesAcceptedForTransport: 1024,
            BytesAcknowledgedByReceiver: 0);
        InvokePrivateMethod(
            runtime,
            "OnFileTransferChanged",
            runtime,
            new SessionFileTransferSnapshotChangedEventArgs(new SessionFileTransferSnapshot(Outbound: null, Inbound: transfer)));

        for (var i = 0; i < 4; i++)
        {
            InvokePrivateMethod(
                runtime,
                "OnBridgeLifecycle",
                null,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: $"reason=filetransfer_protocol_repair_only:stall=bulk_receive_stalled:attempt={i}"));
        }

        now = now.AddSeconds(20);
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "regular_v4_unproven_recovery_escalation"));

        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal(0, disconnected);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(13);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(11);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_ActiveBridgeRecoveryCannotDeferPastPeerSilenceCap()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helpee.filetransfer.silence-cap"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        var transfer = new FileTransferTransferSnapshot(
            sessionId,
            "ft_liveness_bridge_recovery_silence_cap",
            FileTransferDirection.Inbound,
            FileTransferTransferState.Receiving,
            "bridge-recovery-silence-cap.bin",
            64 * 1024,
            Sha256Base64: null,
            BytesTransferred: 1024,
            ChunksTransferred: 1,
            ChunkCount: 64,
            ChunkSizeBytes: 1024,
            ErrorCode: null,
            StatusMessage: null,
            BytesAcceptedForTransport: 1024,
            BytesAcknowledgedByReceiver: 0);
        InvokePrivateMethod(
            runtime,
            "OnFileTransferChanged",
            runtime,
            new SessionFileTransferSnapshotChangedEventArgs(new SessionFileTransferSnapshot(Outbound: null, Inbound: transfer)));

        now = now.AddSeconds(95);
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "regular_v4_unproven_recovery_escalation"));

        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_RuntimeUnlockRecoveryInProgressGetsLongProofWindow()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.runtime-unlock-recovery"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        var transfer = new FileTransferTransferSnapshot(
            sessionId,
            "ft_liveness_runtime_unlock_recovery",
            FileTransferDirection.Outbound,
            FileTransferTransferState.Sending,
            "runtime-unlock-recovery.bin",
            64 * 1024,
            Sha256Base64: null,
            BytesTransferred: 0,
            ChunksTransferred: 1,
            ChunkCount: 64,
            ChunkSizeBytes: 1024,
            ErrorCode: null,
            StatusMessage: null,
            BytesAcceptedForTransport: 1024,
            BytesAcknowledgedByReceiver: 0);
        InvokePrivateMethod(
            runtime,
            "OnFileTransferChanged",
            runtime,
            new SessionFileTransferSnapshotChangedEventArgs(new SessionFileTransferSnapshot(Outbound: transfer, Inbound: null)));

        now = now.AddSeconds(10);
        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "reason=recovery_already_in_progress:stall=tuna_activation_offer_send_timeout:connect=core_filetransfer_request"));

        now = now.AddSeconds(20);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal(0, disconnected);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(16);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(11);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_RuntimeUnlockStartupDefersActiveFileTransferTimeout()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var recoveryRequests = new ConcurrentQueue<FileTransferReceiveRecoveryRequest>();
        var scripted = new ScriptedSignalingTransport(
            onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask,
            onRequestFileTransferReceiveRecovery: recoveryRequests.Enqueue);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        var securityState = CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.runtime-unlock-startup"));
        var sessionId = securityState.SessionId!.Value.Value;
        scripted.SetSessionSecurityStateForTests(securityState);
        scripted.SetTransportAccelerationForTests(isActive: false, reason: "listener_starting");
        var disconnected = 0;
        runtime.Disconnected += (_, _) => disconnected++;
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        var transfer = new FileTransferTransferSnapshot(
            sessionId,
            "ft_liveness_runtime_unlock_startup",
            FileTransferDirection.Outbound,
            FileTransferTransferState.Sending,
            "runtime-unlock-startup.bin",
            64 * 1024,
            Sha256Base64: null,
            BytesTransferred: 0,
            ChunksTransferred: 1,
            ChunkCount: 64,
            ChunkSizeBytes: 1024,
            ErrorCode: null,
            StatusMessage: null,
            BytesAcceptedForTransport: 1024,
            BytesAcknowledgedByReceiver: 0);
        InvokePrivateMethod(
            runtime,
            "OnFileTransferChanged",
            runtime,
            new SessionFileTransferSnapshotChangedEventArgs(new SessionFileTransferSnapshot(Outbound: transfer, Inbound: null)));

        now = now.AddSeconds(10);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Empty(recoveryRequests);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        scripted.SetTransportAccelerationForTests(isActive: false, reason: "activation_offer_not_observed");
        now = now.AddSeconds(11);
        delay.CompleteLatest();
        await Task.Delay(50);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal(0, disconnected);
        Assert.Empty(recoveryRequests);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(61);
        delay.CompleteLatest();
        await WaitUntilAsync(() => recoveryRequests.Count == 1, TimeSpan.FromSeconds(1));
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);

        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(11);
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed, TimeSpan.FromSeconds(1));

        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.Equal(1, disconnected);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_SessionLivenessTimeout_SendsPeerVisibleEndNoticeBeforeTeardown()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var watchdog = SessionRuntimeWatchdogOptions.Default with
            {
                SessionLivenessHeartbeatInterval = TimeSpan.FromMilliseconds(100),
                SessionLivenessSuspectTimeout = TimeSpan.FromMilliseconds(250),
                SessionLivenessTimeout = TimeSpan.FromMilliseconds(700),
            };
            var helpeeClient = new FakeNknClient("helpee.liveness-timeout.notice." + Guid.NewGuid().ToString("N"));
            var helperClient = new FakeNknClient("helper.liveness-timeout.notice." + Guid.NewGuid().ToString("N"));
            using var helpeeTransport = new NknSignalingTransport(helpeeClient, options, new NknIdentity("helpee-liveness-timeout-notice", helpeeClient.Address));
            using var helperTransport = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-liveness-timeout-notice", helperClient.Address));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport, watchdog);
            using var helperRuntime = new SessionRuntime(() => helperTransport, watchdog);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var helperRemoteEnded = 0;
            helperRuntime.RemoteSessionEnded += (_, _) => Interlocked.Increment(ref helperRemoteEnded);

            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                GetHostedAddressOrThrow(helpeeRuntime),
                out var rawToken,
                boundHelperAddress: new PeerAddress(helperTransport.LocalPeerAddress));
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                      helperRuntime.State == SessionRuntimeState.Connected,
                TimeSpan.FromSeconds(2));

            helperClient.ShouldDeliverSendAsync = static (_, _, _) => Task.FromResult(false);

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpeeRuntime.StatusText, "Connection lost.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => Volatile.Read(ref helperRemoteEnded) > 0,
                TimeSpan.FromSeconds(3));

            Assert.True(Volatile.Read(ref helperRemoteEnded) > 0);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    public async Task SessionRuntime_RemoteSessionEnd_CancelsLivenessTimeoutAndPreservesRemoteEndedCopy()
    {
        var delay = new ControlledDelayScheduler();
        var now = DateTimeOffset.UtcNow;
        var scripted = new ScriptedSignalingTransport(onSendSessionHeartbeatAsync: static (_, _) => Task.CompletedTask);
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            SessionLivenessHeartbeatInterval = TimeSpan.FromSeconds(1),
            SessionLivenessSuspectTimeout = TimeSpan.FromSeconds(3),
            SessionLivenessTimeout = TimeSpan.FromSeconds(9),
        };
        using var runtime = new SessionRuntime(() => scripted, options, delay.DelayAsync, nowProvider: () => now);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        InvokePrivateMethod(runtime, "WireTransport", scripted);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.remote.end")));
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));

        InvokePrivateMethod(runtime, "OnRemoteSessionEnded", scripted, EventArgs.Empty);
        await WaitUntilAsync(() => runtime.LastDisconnectWasRemoteEnd, TimeSpan.FromSeconds(1));
        now = now.AddSeconds(10);
        await Task.Delay(100);

        Assert.True(runtime.LastDisconnectWasRemoteEnd);
        Assert.NotEqual("Connection lost.", runtime.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknSignalingTransport_HelpRequest_DuplicateRequestId_IsAckedButNotSurfacedTwice()
    {
        using var fixture = await CreateDirectHelpRequestFixtureAsync("duplicate");
        var events = new List<HelpRequestMessage>();
        fixture.HelperTransport.IncomingHelpRequest += (_, e) => events.Add(e.Request);

        var request = fixture.CreateRequest("help_req_duplicate");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, request, cts.Token);
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, request, cts.Token);

        Assert.Single(events);
        Assert.Equal("help_req_duplicate", events[0].RequestId);
        Assert.Equal("help_request_duplicate_recent", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknSignalingTransport_HelpRequest_SourceBurst_IsThrottledAfterFour()
    {
        using var fixture = await CreateDirectHelpRequestFixtureAsync("source-burst");
        var events = new List<HelpRequestMessage>();
        fixture.HelperTransport.IncomingHelpRequest += (_, e) => events.Add(e.Request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 5; i++)
        {
            await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest($"help_req_burst_{i}", newInviteToken: true), cts.Token);
        }

        Assert.Equal(4, events.Count);
        Assert.Equal("help_request_source_throttled", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknSignalingTransport_HelpRequest_RequestIdChurnForSameInvite_IsThrottledAfterTwo()
    {
        using var fixture = await CreateDirectHelpRequestFixtureAsync("request-churn");
        var events = new List<HelpRequestMessage>();
        fixture.HelperTransport.IncomingHelpRequest += (_, e) => events.Add(e.Request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest("help_req_churn_1"), cts.Token);
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest("help_req_churn_2"), cts.Token);
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest("help_req_churn_3"), cts.Token);

        Assert.Equal(2, events.Count);
        Assert.Equal("help_request_request_churn_throttled", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknSignalingTransport_HelpRequest_ThrottledPreApprovalRequest_DoesNotConsumeInviteToken()
    {
        using var fixture = await CreateDirectHelpRequestFixtureAsync("invite-not-consumed");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest("help_req_consume_1"), cts.Token);
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest("help_req_consume_2"), cts.Token);
        await SendHelpRequestOrFailAsync(fixture.HelpeeTransport, fixture.CreateRequest("help_req_consume_3"), cts.Token);

        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validation = validator.Validate(fixture.InviteToken, DateTimeOffset.UtcNow, InviteValidationMode.ConsumeIfValid);
        Assert.True(validation.IsSuccess, validation.Message);
        Assert.NotNull(validation.Invite);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_IncomingHelpRequest_DuplicatePendingRequest_IsIgnored()
    {
        var scripted = new ScriptedSignalingTransport(localPeerAddress: "helper.runtime.pending.duplicate");
        using var runtime = new SessionRuntime(() => scripted);
        PrepareHelperRuntimeForIncomingHelpRequest(runtime, scripted);
        var events = 0;
        runtime.IncomingHelpRequestAvailable += (_, _) => events++;

        var request = CreateRuntimeHelpRequest("runtime_pending_duplicate", "helper.runtime.pending.duplicate");
        InvokePrivateMethod(runtime, "OnIncomingHelpRequest", scripted, new IncomingHelpRequestEventArgs(request));
        InvokePrivateMethod(runtime, "OnIncomingHelpRequest", scripted, new IncomingHelpRequestEventArgs(request));

        Assert.Equal(1, events);
        Assert.True(runtime.HasPendingHelpRequest);
        Assert.Equal("runtime_pending_duplicate", runtime.PendingHelpRequest?.RequestId);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_IncomingHelpRequest_DifferentPendingRequest_DoesNotReplacePrompt()
    {
        var scripted = new ScriptedSignalingTransport(localPeerAddress: "helper.runtime.pending.replace");
        using var runtime = new SessionRuntime(() => scripted);
        PrepareHelperRuntimeForIncomingHelpRequest(runtime, scripted);
        var events = 0;
        runtime.IncomingHelpRequestAvailable += (_, _) => events++;

        var first = CreateRuntimeHelpRequest("runtime_pending_first", "helper.runtime.pending.replace");
        var second = CreateRuntimeHelpRequest("runtime_pending_second", "helper.runtime.pending.replace");
        InvokePrivateMethod(runtime, "OnIncomingHelpRequest", scripted, new IncomingHelpRequestEventArgs(first));
        InvokePrivateMethod(runtime, "OnIncomingHelpRequest", scripted, new IncomingHelpRequestEventArgs(second));

        Assert.Equal(1, events);
        Assert.True(runtime.HasPendingHelpRequest);
        Assert.Equal("runtime_pending_first", runtime.PendingHelpRequest?.RequestId);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_IncomingHelpRequest_WhenHelperNotWaiting_IsIgnored()
    {
        var scripted = new ScriptedSignalingTransport(localPeerAddress: "helper.runtime.notwaiting");
        using var runtime = new SessionRuntime(() => scripted);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.DirectInvite);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        var events = 0;
        runtime.IncomingHelpRequestAvailable += (_, _) => events++;

        var request = CreateRuntimeHelpRequest("runtime_not_waiting", "helper.runtime.notwaiting");
        InvokePrivateMethod(runtime, "OnIncomingHelpRequest", scripted, new IncomingHelpRequestEventArgs(request));

        Assert.Equal(0, events);
        Assert.False(runtime.HasPendingHelpRequest);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_KeepAliveBridge_IdleTimeout_DisposesCachedBridge_AndRecordsKilledMetric()
    {
        FakeNknClient.ResetNetwork();
        var idleDelayTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new MetricsRegistry();
        var sink = new MetricsTelemetrySink(registry);
        var fakeClient = new FakeNknClient("keepalive.host.addr");
        var transport = new NknSignalingTransport(fakeClient, LoadNknOptionsWithOverrides(Path.Combine(Path.GetTempPath(), "nlink-test-keepalive-" + Guid.NewGuid().ToString("N") + ".json"), "keepalive-host"), new NknIdentity("keepalive-host", "keepalive.host.addr"));
        using var runtime = new SessionRuntime(() => transport, SessionRuntimeWatchdogOptions.Default with { Enabled = false }, telemetrySink: sink, bridgeReusePolicy: new BridgeReusePolicy(BridgeReuseMode.KeepAlive, TimeSpan.FromSeconds(1)), bridgeIdleDelayAsync: (_, _) => idleDelayTcs.Task);
        await runtime.StartHelpeeAsync(CancellationToken.None);
        await runtime.ResetAsync();
        Assert.True(runtime.HasCachedBridgeTransportForTests());
        idleDelayTcs.TrySetResult();
        await WaitUntilAsync(() => !runtime.HasCachedBridgeTransportForTests(), TimeSpan.FromSeconds(2));
        var snapshot = registry.Snapshot();
        Assert.Contains(snapshot.Counters, c => c.Name == "bridge_exit_total" && c.Tags.Result == "killed" && c.Value >= 1);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_WatchdogTimeout_Handshake_TransitionsToFailed_AndClassifiesFailure()
    {
        var delay = new ControlledDelayScheduler();
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            HandshakeTimeout = TimeSpan.FromSeconds(30),
        };
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), options, delay.DelayAsync);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "connect_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "handshake_start"));
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.TransportLifecycleState == TransportState.Failed, TimeSpan.FromSeconds(2));
        Assert.Equal(TransportFailureCategory.HandshakeTimeout, runtime.GetLastFailureCategoryForTests());
        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("No response yet.", runtime.StatusText);
        Assert.NotNull(runtime.GetLastDurationMetricMilliseconds("handshake_duration_ms"));
        Assert.True(runtime.GetLastDurationMetricMilliseconds("handshake_duration_ms")!.Value >= 0);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_WatchdogTimeout_BridgeStarting_TransitionsToFailed_AndClassifiesFailure()
    {
        var delay = new ControlledDelayScheduler();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), SessionRuntimeWatchdogOptions.Default, delay.DelayAsync);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "test_start"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "bridge_start"));
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.TransportLifecycleState == TransportState.Failed, TimeSpan.FromSeconds(2));
        Assert.Equal(TransportFailureCategory.BridgeStartFailure, runtime.GetLastFailureCategoryForTests());
        Assert.Equal("Please reinstall.", runtime.StatusText);
        Assert.NotNull(runtime.GetLastDurationMetricMilliseconds("bridge_start_duration_ms"));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_WatchdogTimeout_AutoRetryEnabled_ResetsToIdle()
    {
        var delay = new ControlledDelayScheduler();
        var options = SessionRuntimeWatchdogOptions.Default with
        {
            AutoRetryEnabled = true,
            ConnectingTimeout = TimeSpan.FromSeconds(30),
        };
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), options, delay.DelayAsync);
        await runtime.StartHelpeeAsync(CancellationToken.None);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Handshake, "test_handshake"));
        await WaitUntilAsync(() => delay.PendingCount > 0, TimeSpan.FromSeconds(1));
        delay.CompleteLatest();
        await WaitUntilAsync(() => runtime.TransportLifecycleState == TransportState.Idle && runtime.State == SessionRuntimeState.Idle, TimeSpan.FromSeconds(3));
        Assert.Equal(TransportFailureCategory.HandshakeTimeout, runtime.GetLastFailureCategoryForTests());
        Assert.NotNull(runtime.GetLastDurationMetricMilliseconds("connect_duration_ms"));
        Assert.True(runtime.GetLastDurationMetricMilliseconds("connect_duration_ms")!.Value >= 0);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_HelpeeConnectingFromBridgeReady_DoesNotWatchdogTimeoutWhileIdle()
    {
        var delay = new ControlledDelayScheduler();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(), SessionRuntimeWatchdogOptions.Default, delay.DelayAsync);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.TransportInitializing, "start_helpee"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeStarting, "nkn_bridge_starting"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.BridgeReady, "bridge_ready"));
        Assert.True(runtime.TryTransitionTransportStateForTests(TransportState.Connecting, "bridge_ready"));
        // The helpee hosting path should not arm a connecting watchdog while idle.
        await Task.Delay(100);
        Assert.Equal(0, delay.PendingCount);
        Assert.Equal(TransportState.Connecting, runtime.TransportLifecycleState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_HelpeeIdleDisconnect_DuplicateEvents_DoNotStartMultipleRehosts()
    {
        var created = new List<ScriptedSignalingTransport>();
        var factory = new CountingTransportFactory(() =>
        {
            var transport = new ScriptedSignalingTransport();
            lock (created)
            {
                created.Add(transport);
            }

            return transport;
        });
        using var runtime = new SessionRuntime(factory.Create);
        await runtime.StartHelpeeAsync(CancellationToken.None);
        ScriptedSignalingTransport first;
        lock (created)
        {
            first = Assert.IsType<ScriptedSignalingTransport>(created.Single());
        }

        // Duplicate disconnected notifications can happen around bridge/process teardown.
        first.RaiseDisconnected();
        first.RaiseDisconnected();
        await WaitUntilAsync(() => factory.CreateCount >= 2, TimeSpan.FromSeconds(2));
        await Task.Delay(200);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_IgnoresStaleTransportDisconnectedEvent_AfterResetAndRehost()
    {
        var first = new ScriptedSignalingTransport();
        var second = new ScriptedSignalingTransport();
        var queue = new Queue<ISignalingTransport>(new ISignalingTransport[] { first, second });
        using var runtime = new SessionRuntime(() => queue.Dequeue());
        await runtime.StartHelpeeAsync(CancellationToken.None);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        await runtime.ResetAsync();
        await runtime.StartHelpeeAsync(CancellationToken.None);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        var onDisconnected = typeof(SessionRuntime).GetMethod("OnTransportDisconnected", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onDisconnected);
        onDisconnected!.Invoke(runtime, new object? [] { first, EventArgs.Empty });
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for helper…", runtime.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_HelperListenerTransportDisconnect_RestartsQuietly()
    {
        var created = new List<ScriptedSignalingTransport>();
        var factory = new CountingTransportFactory(() =>
        {
            var transport = new ScriptedSignalingTransport();
            lock (created)
            {
                created.Add(transport);
            }

            return transport;
        });
        using var runtime = new SessionRuntime(factory.Create);
        await runtime.StartHelperListeningAsync(CancellationToken.None);
        ScriptedSignalingTransport first;
        lock (created)
        {
            Assert.NotEmpty(created);
            first = Assert.IsType<ScriptedSignalingTransport>(created[0]);
        }

        first.RaiseDisconnected();
        await WaitUntilAsync(() => factory.CreateCount >= 2, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && string.Equals(runtime.StatusText, "Waiting for help requests…", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
        Assert.Equal("Waiting for help requests…", runtime.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_StartHelpee_SynchronousTransportFailure_DoesNotRemainTransportInitializing()
    {
        PersistenceDiagnostics.ClearForTests();
        try
        {
            PersistenceDiagnostics.Record(domain: "nkn_secret_store", operation: "load_seed", severity: PersistenceDiagnosticSeverity.Error, outcome: PersistenceDiagnosticOutcome.FailedClosed, reason: "CryptographicException", userWarning: "Protected seed storage could not be read.");
            using var runtime = new SessionRuntime(() => throw new InvalidOperationException("Protected NKN seed storage is unavailable for 'identity.json'."));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartHelpeeAsync(CancellationToken.None));
            Assert.Contains("Protected NKN seed storage is unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TransportState.Failed, runtime.TransportLifecycleState);
            Assert.Equal(SessionRuntimeState.Disconnected, runtime.State);
            Assert.Equal("Protected seed storage could not be read.", runtime.StatusText);
            Assert.NotNull(runtime.LastTransportFailure);
        }
        finally
        {
            PersistenceDiagnostics.ClearForTests();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_ConnectAttempt_IncrementsOnRetry_SameSession()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, _) => Task.CompletedTask));
        var targetAddress = new PeerAddress("scripted.connect.retry");
        await runtime.StartHelperAsync(targetAddress, CancellationToken.None);
        var firstAttempt = runtime.GetConnectAttemptForTests();
        var firstSessionId = runtime.GetSessionIdForTests();
        Assert.Equal(1, firstAttempt);
        Assert.False(string.IsNullOrWhiteSpace(firstSessionId));
        await runtime.ResetAsync();
        await runtime.StartHelperAsync(targetAddress, CancellationToken.None);
        Assert.Equal(2, runtime.GetConnectAttemptForTests());
        Assert.Equal(firstSessionId, runtime.GetSessionIdForTests());
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_ConnectAttempt_ResetsForNewSession()
    {
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, _) => Task.CompletedTask));
        await runtime.StartHelperAsync(new PeerAddress("scripted.connect.first"), CancellationToken.None);
        var firstSessionId = runtime.GetSessionIdForTests();
        Assert.Equal(1, runtime.GetConnectAttemptForTests());
        await runtime.ResetAsync();
        await runtime.StartHelperAsync(new PeerAddress("scripted.connect.second"), CancellationToken.None);
        Assert.Equal(1, runtime.GetConnectAttemptForTests());
        Assert.NotEqual(firstSessionId, runtime.GetSessionIdForTests());
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_HostJoin_RaisesJoinRequestApproveAndRejectEvents()
    {
        await VerifyHandshakeAsync(approve: true);
        await VerifyHandshakeAsync(approve: false);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task DevLocalTransport_ChatSubscriberThrow_DoesNotDisconnectSession()
    {
        var hostAddress = CreateTestPeerAddress();
        using var hostTransport = new DevLocalTransport(hostAddress);
        using var helperTransport = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostDisconnected = 0;
        var helperDisconnected = 0;
        var receiveAttempts = 0;
        hostTransport.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        hostTransport.Approved += (_, _) => hostApproved.TrySetResult();
        helperTransport.Approved += (_, _) => helperApproved.TrySetResult();
        hostTransport.Disconnected += (_, _) => Interlocked.Increment(ref hostDisconnected);
        helperTransport.Disconnected += (_, _) => Interlocked.Increment(ref helperDisconnected);
        hostTransport.ChatMessageReceived += (_, e) =>
        {
            if (Interlocked.Increment(ref receiveAttempts) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            hostReceived.TrySetResult(Encoding.UTF8.GetString(e.Payload));
        };
        _ = hostTransport.HostByAddressAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat,
            boundHelperAddress: new PeerAddress(helperTransport.LocalPeerAddress));
        await helperTransport.JoinByInviteAsync(rawToken, invite, cts.Token).WaitAsync(TimeSpan.FromSeconds(3));
        await joinRaised.Task.WaitAsync(cts.Token);
        await pendingJoin!.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await helperApproved.Task.WaitAsync(cts.Token);
        await hostApproved.Task.WaitAsync(cts.Token);
        await helperTransport.SendChatMessageAsync(Encoding.UTF8.GetBytes("first"), cts.Token);
        await helperTransport.SendChatMessageAsync(Encoding.UTF8.GetBytes("second"), cts.Token);
        var received = await hostReceived.Task.WaitAsync(cts.Token);
        Assert.Equal("second", received);
        Assert.Equal(2, Volatile.Read(ref receiveAttempts));
        Assert.Equal(0, Volatile.Read(ref hostDisconnected));
        Assert.Equal(0, Volatile.Read(ref helperDisconnected));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ChatHardening_WhenEnabled_PreservesExactChatMessageInsertionOrder()
    {
        if (!FeatureFlags.EnableChatHardening)
        {
            return;
        }

        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        using var unboundInviteOptIn = new EnvironmentOverride(AppFeatureFlags.AllowInsecureUnboundPublicInvitesEnvVar, "1");
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-chat-hardening-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-chat-hardening-" + Guid.NewGuid().ToString("N")));
        using var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime);
        _ = await WaitForShareInviteAsync(helpee);
        var connectTask = helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), CancellationToken.None);
        await WaitUntilAsync(() => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest", TimeSpan.FromSeconds(5));
        helpee.AllowCommand.Execute(null);
        await connectTask;
        await WaitUntilAsync(() => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected", TimeSpan.FromSeconds(5));
        var helperTexts = new[]
        {
            "helper-1",
            "helper-2",
            "helper-3"
        };
        var helpeeTexts = new[]
        {
            "helpee-1",
            "helpee-2",
            "helpee-3"
        };
        for (var i = 0; i < helperTexts.Length; i++)
        {
            helper.ChatDraft = helperTexts[i];
            await helper.SendChatCommand.ExecuteAsync(null);
            var expectedAfterHelperSend = (i * 2) + 1;
            await WaitUntilAsync(() => helper.ChatMessages.Count == expectedAfterHelperSend && helpee.ChatMessages.Count == expectedAfterHelperSend, TimeSpan.FromSeconds(2));
            helpee.ChatDraft = helpeeTexts[i];
            await helpee.SendChatCommand.ExecuteAsync(null);
            var expectedAfterHelpeeSend = (i * 2) + 2;
            await WaitUntilAsync(() => helper.ChatMessages.Count == expectedAfterHelpeeSend && helpee.ChatMessages.Count == expectedAfterHelpeeSend, TimeSpan.FromSeconds(2));
        }

        Assert.Equal(new[] { (true, "helper-1"), (false, "helpee-1"), (true, "helper-2"), (false, "helpee-2"), (true, "helper-3"), (false, "helpee-3"), }, helper.ChatMessages.Select(line => (line.IsLocal, line.Text)).ToArray());
        Assert.Equal(new[] { (false, "helper-1"), (true, "helpee-1"), (false, "helper-2"), (true, "helpee-2"), (false, "helper-3"), (true, "helpee-3"), }, helpee.ChatMessages.Select(line => (line.IsLocal, line.Text)).ToArray());
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_CurrentLocalPeerAddress_IgnoresNonAuthoritativeNknFallbackAddress()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var fallbackAddress = "nlink-bb427ded.a65f0bc0394645f125e4";
            var authoritativeAddress = "helper.authoritative.connected.address";
            var fakeClient = new FakeNknClient(fallbackAddress, authoritativeAddress);
            var identity = new NknIdentity("helper-id", fallbackAddress);
            using var transport = new NknSignalingTransport(fakeClient, options, identity);
            using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport());
            SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
            SetPrivateField(runtime, "transport", transport);
            Assert.Null(runtime.CurrentLocalPeerAddress);
            await transport.HostByAddressAsync(CancellationToken.None);
            Assert.Equal(authoritativeAddress, runtime.CurrentLocalPeerAddress?.Value);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_HelperListenerOnNkn_DoesNotEnterOutboundConnectingTimeout()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("nlink-helper-listener-fallback.1234567890", "helper.listener.authoritative.connected.address"), options, new NknIdentity("helper-listener-test", "nlink-helper-listener-fallback.1234567890"));
            using var runtime = new SessionRuntime(() => helperTransport, SessionRuntimeWatchdogOptions.Default with { ConnectingTimeout = TimeSpan.FromMilliseconds(250) });
            await runtime.StartHelperListeningAsync(CancellationToken.None);
            await Task.Delay(500);
            Assert.Equal(SessionRuntimeState.Waiting, runtime.State);
            Assert.NotEqual(TransportState.Connecting, runtime.TransportLifecycleState);
            Assert.NotEqual(TransportState.Failed, runtime.TransportLifecycleState);
            Assert.Equal("Waiting for help requests…", runtime.StatusText);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_HelperReconnect_DoesNotReuseOldApproval()
    {
        var hostAddress = CreateTestPeerAddress();
        var helperAddress = CreateTestPeerAddress();
        var reconnectAddress = CreateTestPeerAddress();
        using var helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        using var helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
        using var reconnectRuntime = new SessionRuntime(() => new DevLocalTransport(reconnectAddress));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat | InviteCapabilities.RemoteControl,
            boundHelperAddress: new PeerAddress(helperAddress));
        await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helperRuntime.CurrentSessionGrant is not null && helpeeRuntime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));
        var oldSessionId = helperRuntime.CurrentSessionGrant!.SessionId;
        await helperRuntime.DisconnectAsync();
        await WaitUntilAsync(() => helpeeRuntime.CurrentSessionGrant is null && !helpeeRuntime.SecurityState.ApprovalGranted, TimeSpan.FromSeconds(3));
        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        var reconnectInvite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var reconnectToken,
            InviteCapabilities.Chat | InviteCapabilities.RemoteControl,
            boundHelperAddress: new PeerAddress(reconnectAddress));
        var reconnectTask = reconnectRuntime.StartHelperAsync(reconnectToken, reconnectInvite, cts.Token);
        await WaitUntilAsync(() => helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
        Assert.Null(reconnectRuntime.CurrentSessionGrant);
        Assert.False(reconnectRuntime.SecurityState.ApprovalGranted);
        Assert.False(await reconnectRuntime.RequestRemoteControlAsync(cts.Token));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await reconnectTask;
        await WaitUntilAsync(() => reconnectRuntime.CurrentSessionGrant is not null, TimeSpan.FromSeconds(2));
        Assert.NotEqual(oldSessionId, reconnectRuntime.CurrentSessionGrant!.SessionId);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_RepeatCycle_ResetAndRetry_FiveIterations_ReturnsToIdle()
    {
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-" + Guid.NewGuid().ToString("N")));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var helperChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < 5; i++)
        {
            helperChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHelperChat(object? _, ChatMessageEventArgs e) => helperChatReceived.TrySetResult(e.Message.Text);
            void OnHelpeeChat(object? _, ChatMessageEventArgs e) => helpeeChatReceived.TrySetResult(e.Message.Text);
            helperRuntime.ChatMessageReceived += OnHelperChat;
            helpeeRuntime.ChatMessageReceived += OnHelpeeChat;
            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(1));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected && helpeeRuntime.HasSessionKey && helperRuntime.HasSessionKey, TimeSpan.FromSeconds(1));
            var helperText = $"hello-{i}";
            var helpeeText = $"reply-{i}";
            var helperSent = await helperRuntime.TrySendChatTextAsync(helperText, cts.Token);
            Assert.NotNull(helperSent);
            Assert.Equal(helperText, await helpeeChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));
            helpeeChatReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            helperRuntime.ChatMessageReceived -= OnHelperChat;
            helpeeRuntime.ChatMessageReceived -= OnHelpeeChat;
            helperRuntime.ChatMessageReceived += OnHelperChat;
            helpeeRuntime.ChatMessageReceived += OnHelpeeChat;
            var helpeeSent = await helpeeRuntime.TrySendChatTextAsync(helpeeText, cts.Token);
            Assert.NotNull(helpeeSent);
            Assert.Equal(helpeeText, await helperChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));
            helperRuntime.ChatMessageReceived -= OnHelperChat;
            helpeeRuntime.ChatMessageReceived -= OnHelpeeChat;
            await helperRuntime.ResetAsync();
            await helpeeRuntime.ResetAsync();
            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.Equal(SessionRuntimeState.Idle, helpeeRuntime.State);
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_NknRemoteSessionEnd_ShowsFriendlyMessage_AndCanReset()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(new FakeNknClient("helpee.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helpee-test", "helpee.test.fake"));
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("helper.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helper-test", "helper.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                GetHostedAddressOrThrow(helpeeRuntime),
                out var rawToken,
                boundHelperAddress: new PeerAddress(helperTransport.LocalPeerAddress));
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));
            await helperRuntime.DisconnectAsync();
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Failed && string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.Equal("The helper ended the session.", helpeeRuntime.StatusText);
            await helpeeRuntime.ResetAsync();
            Assert.Equal(SessionRuntimeState.Idle, helpeeRuntime.State);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_NknRemoteSessionEnd_BulkCopySurvivesControlFailure()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var helpeeClient = new FakeNknClient("helpee.session-end.bulk-copy.addr." + Guid.NewGuid().ToString("N"));
            var helperClient = new FakeNknClient("helper.session-end.bulk-copy.addr." + Guid.NewGuid().ToString("N"));
            using var helpeeTransport = new NknSignalingTransport(helpeeClient, options, new NknIdentity("helpee-bulk-session-end-test", helpeeClient.Address));
            using var helperTransport = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-bulk-session-end-test", helperClient.Address));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                GetHostedAddressOrThrow(helpeeRuntime),
                out var rawToken,
                boundHelperAddress: new PeerAddress(helperTransport.LocalPeerAddress));
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));

            helperClient.BeforeSendCoreAsync = static (_, _, channel, _) =>
                channel == NknBridgeChannel.Control
                    ? Task.FromException(new InvalidOperationException("control_lane_unavailable"))
                    : Task.CompletedTask;

            await helperRuntime.DisconnectAsync();

            await WaitUntilAsync(
                () => helpeeRuntime.State == SessionRuntimeState.Failed &&
                      string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            Assert.Equal("The helper ended the session.", helpeeRuntime.StatusText);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_NknHelperEndWhileApprovalPending_PreventsStaleHelpeeApproval()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(new FakeNknClient("helpee.approvalpending.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helpee-approvalpending-test", "helpee.approvalpending.test.fake"));
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("helper.approvalpending.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helper-approvalpending-test", "helper.approvalpending.test.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                GetHostedAddressOrThrow(helpeeRuntime),
                out var rawToken,
                boundHelperAddress: new PeerAddress(helperTransport.LocalPeerAddress));
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest && helpeeRuntime.PendingApprovalRequest is not null, TimeSpan.FromSeconds(2));
            await helperRuntime.DisconnectAsync();
            await helpeeRuntime.ApproveAsync(cts.Token);
            await Task.Delay(1000, cts.Token);
            Assert.NotEqual(SessionRuntimeState.Connected, helpeeRuntime.State);
            Assert.Equal(SessionRuntimeState.Idle, helperRuntime.State);
            Assert.False(helpeeRuntime.HasPendingJoinRequest);
            Assert.Null(helpeeRuntime.PendingApprovalRequest);
            Assert.Null(helpeeRuntime.CurrentSessionGrant);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_TransportDisconnect_TransitionsToFailed_WithConnectionLost()
    {
        var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        using var runtime = new SessionRuntime(() => scripted);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await runtime.StartHelperAsync(new PeerAddress("scripted.transport.disconnect"), cts.Token);
        scripted.RaiseDisconnected();
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed && string.Equals(runtime.StatusText, "Connection lost.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        Assert.Equal("Connection lost.", runtime.StatusText);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_BridgeReceiveStallRecoveryExhausted_ConnectedSessionShowsConnectionLost()
    {
        using var runtime = new SessionRuntime(
            () => new ScriptedSignalingTransport(),
            SessionRuntimeWatchdogOptions.Default with { Enabled = false });
        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(runtime, "transportState", TransportState.Connected);
        var disconnectedCount = 0;
        runtime.Disconnected += (_, _) => disconnectedCount++;

        InvokePrivateMethod(
            runtime,
            "OnBridgeLifecycle",
            null,
            new BridgeLifecycleEvent(
                BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                StartMode: null,
                Pid: null,
                ReadyTimeMs: null,
                PingRttMs: null,
                UptimeMs: null,
                ExitCode: null,
                ExitReasonKind: null,
                ExitReasonText: "post_tuna_fallback_receive_unproven"));

        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
        Assert.Equal("Connection lost.", runtime.StatusText);
        Assert.False(runtime.LastDisconnectWasRemoteEnd);
        Assert.Equal(1, disconnectedCount);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_DisconnectAfterMappedFail_KeepsMappedStatusText()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Failed);
        SetPrivateField(runtime, "statusText", "No response yet.");
        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);
        Assert.Equal("No response yet.", runtime.StatusText);
        Assert.Equal(SessionRuntimeState.Failed, runtime.State);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_TransportApproved_DoesNotAutoStartTransportScreenShare()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connecting);
        SetPrivateField(runtime, "statusText", "Connecting…");
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.peer")));
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
        Assert.False(runtime.IsTransportScreenShareActiveForTests);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_TransportDisconnect_DisablesScreenShareAutoStart_ForLaterApproval()
    {
        using var scripted = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => scripted);
        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", scripted);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.peer")));
        Assert.True(runtime.CanAutoStartTransportScreenShareForTests);
        InvokePrivateMethod(runtime, "OnTransportDisconnected", scripted, EventArgs.Empty);
        Assert.False(runtime.CanAutoStartTransportScreenShareForTests);
        scripted.SetSessionSecurityStateForTests(CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.peer")));
        InvokePrivateMethod(runtime, "OnTransportApproved", scripted, EventArgs.Empty);
        Assert.False(runtime.CanAutoStartTransportScreenShareForTests);
        Assert.Equal(SessionRuntimeState.Connected, runtime.State);
        Assert.Equal("Connected", runtime.StatusText);
    }

    private static async Task<DirectHelpRequestFixture> CreateDirectHelpRequestFixtureAsync(string scenario)
    {
        FakeNknClient.ResetNetwork();
        NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);

        var suffix = Guid.NewGuid().ToString("N");
        var helperAddress = $"helper.direct.{scenario}.{suffix}";
        var helpeeAddress = $"helpee.direct.{scenario}.{Guid.NewGuid():N}";
        var options = LoadNknOptionsWithOverrides(
            Path.Combine(Path.GetTempPath(), $"nlink-direct-help-{scenario}-{suffix}.json"),
            $"direct-help-{scenario}");
        var helperClient = new FakeNknClient(helperAddress);
        var helpeeClient = new FakeNknClient(helpeeAddress);
        var helperTransport = new NknSignalingTransport(
            helperClient,
            options,
            new NknIdentity($"helper-{scenario}", helperAddress));
        var helpeeTransport = new NknSignalingTransport(
            helpeeClient,
            options,
            new NknIdentity($"helpee-{scenario}", helpeeAddress));
        var helperPeerAddress = new PeerAddress(helperAddress);
        var helpeePeerAddress = new PeerAddress(helpeeAddress);
        CreateValidatedInviteForTarget(helpeePeerAddress, out var inviteToken, boundHelperAddress: helperPeerAddress);
        await helperTransport.HostByAddressAsync(CancellationToken.None);

        return new DirectHelpRequestFixture(
            helperTransport,
            helpeeTransport,
            helperPeerAddress,
            helpeePeerAddress,
            inviteToken);
    }

    private static HelpRequestMessage CreateRuntimeHelpRequest(string requestId, string helperAddress) =>
        new(
            requestId,
            new PeerAddress($"helpee.{requestId}"),
            new PeerAddress(helperAddress),
            "runtime-test-invite-token");

    private static async Task SendHelpRequestOrFailAsync(
        NknSignalingTransport transport,
        HelpRequestMessage request,
        CancellationToken ct)
    {
        var error = await Record.ExceptionAsync(() => transport.SendHelpRequestAsync(request, ct));
        if (error is not null)
        {
            var diagnostics = NknRuntimeDiagnostics.Snapshot();
            Assert.True(
                error is null,
                $"HelpRequest send failed with {error!.GetType().Name}: {error.Message}; last_drop={diagnostics.LastEnvelopeDropReason}; last_error={diagnostics.LastError}");
        }
    }

    private static void PrepareHelperRuntimeForIncomingHelpRequest(SessionRuntime runtime, ScriptedSignalingTransport transport)
    {
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "role", SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "helperConnectOrigin", HelperConnectOrigin.Listener);
        SetPrivateField(runtime, "state", SessionRuntimeState.Waiting);
    }

    private sealed class DirectHelpRequestFixture : IDisposable
    {
        public DirectHelpRequestFixture(
            NknSignalingTransport helperTransport,
            NknSignalingTransport helpeeTransport,
            PeerAddress helperAddress,
            PeerAddress helpeeAddress,
            string inviteToken)
        {
            HelperTransport = helperTransport;
            HelpeeTransport = helpeeTransport;
            HelperAddress = helperAddress;
            HelpeeAddress = helpeeAddress;
            InviteToken = inviteToken;
        }

        public NknSignalingTransport HelperTransport { get; }
        public NknSignalingTransport HelpeeTransport { get; }
        public PeerAddress HelperAddress { get; }
        public PeerAddress HelpeeAddress { get; }
        public string InviteToken { get; }

        public HelpRequestMessage CreateRequest(string requestId, bool newInviteToken = false)
        {
            var token = InviteToken;
            if (newInviteToken)
            {
                CreateValidatedInviteForTarget(HelpeeAddress, out token, boundHelperAddress: HelperAddress);
            }

            return new HelpRequestMessage(requestId, HelpeeAddress, HelperAddress, token);
        }

        public void Dispose()
        {
            HelperTransport.Dispose();
            HelpeeTransport.Dispose();
            FakeNknClient.ResetNetwork();
        }
    }

}
