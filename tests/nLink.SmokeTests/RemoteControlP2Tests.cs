using System.Diagnostics;
using System.Reflection;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class RemoteControlP2Tests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_Handshake_TransitionsToActive_AndStopsToOff()
    {
        var helperTransport = new LinkedRemoteControlTransport("helper-peer");
        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        helperTransport.Peer = helpeeTransport;
        helpeeTransport.Peer = helperTransport;

        using var helperRuntime = new SessionRuntime(() => helperTransport);
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);

        AttachConnectedRuntime(helperRuntime, helperTransport, SessionRuntimeRole.Helper);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        Assert.True(helperRuntime.SessionSupportsRemoteControl);
        Assert.True(helpeeRuntime.SessionSupportsRemoteControl);
        Assert.Equal(ControlState.Off, helperRuntime.ControlState);
        Assert.Equal(ControlState.Off, helpeeRuntime.ControlState);

        var requested = await helperRuntime.RequestRemoteControlAsync();
        Assert.True(requested);

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(2));

        var allowed = await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: true);
        Assert.True(allowed);

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Active &&
                  helpeeRuntime.ControlState == ControlState.Active,
            TimeSpan.FromSeconds(2));

        var stopped = await helperRuntime.StopRemoteControlAsync("helper_stop");
        Assert.True(stopped);

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Off &&
                  helpeeRuntime.ControlState == ControlState.Off,
            TimeSpan.FromSeconds(2));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_HelpeePendingStart_WhenDisconnected_RevertsToOffQuickly()
    {
        var helperTransport = new LinkedRemoteControlTransport("helper-peer")
        {
            ForwardControlStartToPeer = false,
        };
        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        helperTransport.Peer = helpeeTransport;
        helpeeTransport.Peer = helperTransport;

        using var helperRuntime = new SessionRuntime(() => helperTransport);
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);

        AttachConnectedRuntime(helperRuntime, helperTransport, SessionRuntimeRole.Helper);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        Assert.True(await helperRuntime.RequestRemoteControlAsync());
        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(2));

        Assert.True(await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: true));

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Active &&
                  helpeeRuntime.ControlState == ControlState.Requesting,
            TimeSpan.FromSeconds(2));

        helpeeTransport.InjectDisconnected();

        await WaitUntilAsync(
            () => helpeeRuntime.ControlState == ControlState.Off &&
                  !helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(1));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_MultipleQuickRequests_UsesDistinctRequestIds_AndRecoversFromDenied()
    {
        var helperTransport = new LinkedRemoteControlTransport("helper-peer");
        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        helperTransport.Peer = helpeeTransport;
        helpeeTransport.Peer = helperTransport;

        using var helperRuntime = new SessionRuntime(() => helperTransport);
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);

        AttachConnectedRuntime(helperRuntime, helperTransport, SessionRuntimeRole.Helper);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        Assert.True(await helperRuntime.RequestRemoteControlAsync());
        Assert.False(await helperRuntime.RequestRemoteControlAsync());

        Assert.Single(helperTransport.SentControlRequests);
        var firstRequestId = helperTransport.SentControlRequests[0].RequestId;
        Assert.False(string.IsNullOrWhiteSpace(firstRequestId));

        await WaitUntilAsync(
            () => helpeeRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(2));
        Assert.True(await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: false));

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Off &&
                  helpeeRuntime.ControlState == ControlState.Off,
            TimeSpan.FromSeconds(6));

        Assert.True(await helperRuntime.RequestRemoteControlAsync());
        Assert.Equal(2, helperTransport.SentControlRequests.Count);
        var secondRequestId = helperTransport.SentControlRequests[1].RequestId;
        Assert.False(string.IsNullOrWhiteSpace(secondRequestId));
        Assert.NotEqual(firstRequestId, secondRequestId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_LateOrOutOfOrderResponse_IsIgnoredSafely()
    {
        var helperTransport = new LinkedRemoteControlTransport("helper-peer")
        {
            ControlStartSendDelay = TimeSpan.FromMilliseconds(250),
        };
        using var helperRuntime = new SessionRuntime(() => helperTransport);
        AttachConnectedRuntime(helperRuntime, helperTransport, SessionRuntimeRole.Helper);

        Assert.True(await helperRuntime.RequestRemoteControlAsync());
        var request = Assert.Single(helperTransport.SentControlRequests);

        helperTransport.InjectIncomingControlResponse(
            new ControlResponseMessageV1
            {
                RequestId = request.RequestId,
                Decision = "allow",
                ConsentToken = "token-1",
                TtlMs = 60_000,
            },
            peerId: "helpee-peer");

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Requesting &&
                  string.Equals(helperRuntime.ConsentToken, "token-1", StringComparison.Ordinal),
            TimeSpan.FromSeconds(1));

        helperTransport.InjectIncomingControlResponse(
            new ControlResponseMessageV1
            {
                RequestId = request.RequestId,
                Decision = "deny",
                Reason = "late_out_of_order",
            },
            peerId: "helpee-peer");

        await Task.Delay(80);
        Assert.NotEqual(ControlState.Denied, helperRuntime.ControlState);

        await WaitUntilAsync(
            () => helperRuntime.ControlState == ControlState.Active,
            TimeSpan.FromSeconds(2));
        Assert.Single(helperTransport.SentControlStarts);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_StartWithValidToken_TransitionsToActive()
    {
        const string requestId = "req-valid";
        const string helperPeer = "helper-peer";

        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        helpeeTransport.InjectIncomingControlRequest(
            new ControlRequestMessageV1
            {
                RequestId = requestId,
                Caps = new[] { "mouse", "keyboard" },
            },
            helperPeer);

        await WaitUntilAsync(
            () => helpeeRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(1));

        var responded = await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: true);
        Assert.True(responded);

        var allowResponse = Assert.Single(helpeeTransport.SentControlResponses);
        Assert.Equal("allow", allowResponse.Decision);
        Assert.False(string.IsNullOrWhiteSpace(allowResponse.ConsentToken));

        helpeeTransport.InjectIncomingControlStart(
            new ControlStartMessageV1
            {
                RequestId = requestId,
                ConsentToken = allowResponse.ConsentToken,
            },
            helperPeer);

        await WaitUntilAsync(
            () => helpeeRuntime.ControlState == ControlState.Active,
            TimeSpan.FromSeconds(1));

        Assert.Empty(helpeeTransport.SentControlStops);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_StartWithExpiredToken_IsRejected()
    {
        const string requestId = "req-expired";
        const string helperPeer = "helper-peer";

        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        helpeeTransport.InjectIncomingControlRequest(
            new ControlRequestMessageV1
            {
                RequestId = requestId,
                Caps = new[] { "mouse" },
            },
            helperPeer);

        await WaitUntilAsync(
            () => helpeeRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(1));

        Assert.True(await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: true));
        var allowResponse = Assert.Single(helpeeTransport.SentControlResponses);
        Assert.Equal("allow", allowResponse.Decision);
        Assert.False(string.IsNullOrWhiteSpace(allowResponse.ConsentToken));

        ForcePendingTokenExpiry(helpeeRuntime, DateTimeOffset.UtcNow.AddMilliseconds(-1));

        helpeeTransport.InjectIncomingControlStart(
            new ControlStartMessageV1
            {
                RequestId = requestId,
                ConsentToken = allowResponse.ConsentToken,
            },
            helperPeer);

        await WaitUntilAsync(() => helpeeRuntime.ControlState == ControlState.Off, TimeSpan.FromSeconds(1));

        Assert.Contains(
            helpeeTransport.SentControlStops,
            s => s.RequestId == requestId &&
                 (s.Reason?.Contains("token_expired", StringComparison.Ordinal) ?? false));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_StartWithMismatchedHelperId_IsRejected()
    {
        const string requestId = "req-peer-mismatch";
        const string helperPeer = "helper-peer";
        const string otherPeer = "intruder-peer";

        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        helpeeTransport.InjectIncomingControlRequest(
            new ControlRequestMessageV1
            {
                RequestId = requestId,
                Caps = new[] { "keyboard" },
            },
            helperPeer);

        await WaitUntilAsync(
            () => helpeeRuntime.ControlState == ControlState.Requesting &&
                  helpeeRuntime.HasPendingRemoteControlConsentPrompt,
            TimeSpan.FromSeconds(1));

        Assert.True(await helpeeRuntime.RespondToRemoteControlRequestAsync(allow: true));
        var allowResponse = Assert.Single(helpeeTransport.SentControlResponses);
        Assert.Equal("allow", allowResponse.Decision);
        Assert.False(string.IsNullOrWhiteSpace(allowResponse.ConsentToken));

        helpeeTransport.InjectIncomingControlStart(
            new ControlStartMessageV1
            {
                RequestId = requestId,
                ConsentToken = allowResponse.ConsentToken,
            },
            otherPeer);

        await WaitUntilAsync(() => helpeeRuntime.ControlState == ControlState.Off, TimeSpan.FromSeconds(1));

        Assert.Contains(
            helpeeTransport.SentControlStops,
            s => s.RequestId == requestId &&
                 (s.Reason?.Contains("peer_mismatch", StringComparison.Ordinal) ?? false));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_HelpeeConsentTimeout_AutoDenies_AndSendsDenyResponse()
    {
        const string requestId = "req-consent-timeout";
        const string helperPeer = "helper-peer";

        var helpeeTransport = new LinkedRemoteControlTransport("helpee-peer");
        using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
        AttachConnectedRuntime(helpeeRuntime, helpeeTransport, SessionRuntimeRole.Helpee);

        SetPrivateField(
            helpeeRuntime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Requesting,
                ControllerPeerId: helperPeer,
                CurrentControlRequestId: requestId,
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));
        SetPrivateField(helpeeRuntime, "hasPendingRemoteControlConsentPrompt", true);

        _ = InvokePrivateMethod(helpeeRuntime, "StartRemoteControlConsentTimeout", requestId, 25L);

        await WaitUntilAsync(() => helpeeRuntime.ControlState == ControlState.Denied, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => helpeeTransport.SentControlResponses.Count > 0, TimeSpan.FromSeconds(1));

        var denyResponse = Assert.Single(helpeeTransport.SentControlResponses);
        Assert.Equal("deny", denyResponse.Decision);
        Assert.Equal("consent_timeout", denyResponse.Reason);

        await WaitUntilAsync(() => helpeeRuntime.ControlState == ControlState.Off, TimeSpan.FromSeconds(5));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task RemoteControl_HelperTimeoutPath_AutoDenies()
    {
        const string requestId = "req-helper-timeout";

        var helperTransport = new LinkedRemoteControlTransport("helper-peer");
        using var helperRuntime = new SessionRuntime(() => helperTransport);
        AttachConnectedRuntime(helperRuntime, helperTransport, SessionRuntimeRole.Helper);

        SetPrivateField(
            helperRuntime,
            "remoteControlSessionState",
            new RemoteControlSessionState(
                ControlState.Requesting,
                ControllerPeerId: null,
                CurrentControlRequestId: requestId,
                ConsentToken: null,
                SupportsRemoteControl: true,
                PeerSupportsRemoteControl: true));

        _ = InvokePrivateMethod(helperRuntime, "StartRemoteControlConsentTimeout", requestId, 25L);

        await WaitUntilAsync(() => helperRuntime.ControlState == ControlState.Denied, TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => helperRuntime.ControlState == ControlState.Off, TimeSpan.FromSeconds(5));
    }

    private static void AttachConnectedRuntime(
        SessionRuntime runtime,
        LinkedRemoteControlTransport transport,
        SessionRuntimeRole role)
    {
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        runtime.SetRoleForTests(role);
        _ = InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                helpeeIdentity: role == SessionRuntimeRole.Helpee
                    ? transport.LocalPeerId
                    : transport.Peer?.LocalPeerId ?? "helpee-peer",
                helperIdentity: role == SessionRuntimeRole.Helper
                    ? transport.LocalPeerId
                    : transport.Peer?.LocalPeerId ?? "helper-peer"));
        _ = InvokePrivateMethod(runtime, "RefreshRemoteControlCapabilitiesFromTransport");
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        string helpeeIdentity,
        string helperIdentity,
        CapabilityGrant capabilities = CapabilityGrant.RemoteControl)
    {
        var sessionId = new SessionId(
            $"rc_p2_{NormalizeSessionToken(helpeeIdentity)}_{NormalizeSessionToken(helperIdentity)}");
        var helpeeAddress = new PeerAddress(helpeeIdentity);
        var helperAddress = new PeerAddress(helperIdentity);
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(
              helperAddress,
              capabilities,
              sessionId,
              DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private static string NormalizeSessionToken(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
        }

        return new string(buffer[..length]);
    }

    private static void ForcePendingTokenExpiry(SessionRuntime runtime, DateTimeOffset expiresAtUtc)
    {
        var pending = GetPrivateField(runtime, "pendingRemoteControlConsentToken");
        Assert.NotNull(pending);

        var pendingType = pending!.GetType();
        var requestId = Assert.IsType<string>(pendingType.GetProperty("RequestId", BindingFlags.Instance | BindingFlags.Public)!.GetValue(pending));
        var controllerPeerId = Assert.IsType<string>(pendingType.GetProperty("ControllerPeerId", BindingFlags.Instance | BindingFlags.Public)!.GetValue(pending));
        var tokenHash = Assert.IsType<byte[]>(pendingType.GetProperty("TokenHash", BindingFlags.Instance | BindingFlags.Public)!.GetValue(pending));
        var replacement = Activator.CreateInstance(
            pendingType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { requestId, controllerPeerId, (byte[])tokenHash.Clone(), expiresAtUtc },
            culture: null);

        SetPrivateField(runtime, "pendingRemoteControlConsentToken", replacement);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), $"Condition was not met within {timeout}.");
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

#pragma warning disable CS0067
    private sealed class LinkedRemoteControlTransport :
        ISignalingTransport,
        IAddressTargetSignalingTransport,
        IAddressHostSignalingTransport,
        ISessionSecuritySignalingTransport,
        IRemoteControlCapabilityProvider,
        IRemoteControlSignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public LinkedRemoteControlTransport(string localPeerId)
        {
            LocalPeerId = localPeerId;
        }

        public string LocalPeerId { get; }

        public LinkedRemoteControlTransport? Peer { get; set; }

        public bool LocalSupportsRemoteControl { get; set; } = true;

        public bool RemoteSupportsRemoteControl { get; set; } = true;

        public bool SessionSupportsRemoteControl => LocalSupportsRemoteControl && RemoteSupportsRemoteControl;
        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public bool ForwardControlStartToPeer { get; set; } = true;

        public TimeSpan ControlStartSendDelay { get; set; } = TimeSpan.Zero;

        public List<ControlRequestMessageV1> SentControlRequests { get; } = new();

        public List<ControlResponseMessageV1> SentControlResponses { get; } = new();

        public List<ControlStartMessageV1> SentControlStarts { get; } = new();

        public List<ControlStopMessageV1> SentControlStops { get; } = new();

        public List<ControlInputMessageV1> SentControlInputs { get; } = new();
        public List<ControlDisplayInfoMessageV1> SentControlDisplayInfos { get; } = new();

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;

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

        public void Dispose()
        {
        }

        public Task HostByAddressAsync(CancellationToken ct) => Task.CompletedTask;

        public Task JoinByAddressAsync(string peerAddress, CancellationToken ct) => Task.CompletedTask;

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct)
        {
            SentControlRequests.Add(message);
            Peer?.InjectIncomingControlRequest(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct)
        {
            SentControlResponses.Add(message);
            Peer?.InjectIncomingControlResponse(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public async Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct)
        {
            if (ControlStartSendDelay > TimeSpan.Zero)
            {
                await Task.Delay(ControlStartSendDelay, ct);
            }

            SentControlStarts.Add(message);
            if (ForwardControlStartToPeer)
            {
                Peer?.InjectIncomingControlStart(message, LocalPeerId);
            }
        }

        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct)
        {
            SentControlStops.Add(message);
            Peer?.InjectIncomingControlStop(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct)
        {
            SentControlInputs.Add(message);
            Peer?.InjectIncomingControlInput(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct)
        {
            Peer?.InjectIncomingControlAck(message, LocalPeerId);
            return Task.CompletedTask;
        }

        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
        {
            SentControlDisplayInfos.Add(message);
            Peer?.InjectIncomingControlDisplayInfo(message, LocalPeerId);
            return Task.CompletedTask;
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

        public void InjectIncomingControlInput(ControlInputMessageV1 message, string? peerId)
        {
            RemoteControlInputReceived?.Invoke(this, new RemoteControlInputReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlAck(ControlInputAckV1 message, string? peerId)
        {
            RemoteControlAckReceived?.Invoke(this, new RemoteControlAckReceivedEventArgs(message, peerId));
        }

        public void InjectIncomingControlDisplayInfo(ControlDisplayInfoMessageV1 message, string? peerId)
        {
            RemoteControlDisplayInfoReceived?.Invoke(this, new RemoteControlDisplayInfoReceivedEventArgs(message, peerId));
        }

        public void InjectDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }
#pragma warning restore CS0067
}
