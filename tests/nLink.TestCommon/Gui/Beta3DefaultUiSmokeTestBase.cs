using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Metrics;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public abstract class Beta3DefaultUiSmokeTestBase : IClassFixture<Beta3DefaultUiFixture>
{
    protected readonly Beta3DefaultUiFixture fixture;

    protected Beta3DefaultUiSmokeTestBase(Beta3DefaultUiFixture fixture)
    {
        this.fixture = fixture;
    }

    protected static AppServiceRegistry EnsureAppServices()
    {
        var app = Assert.IsType<NLink.App.App>(Application.Current);
        var services = app.Services;
        if (!services.TryGet<IClipboardService>(out _))
        {
            var clipboard = new TestClipboardService();
            services.AddSingleton<IClipboardService>(clipboard);
        }

        if (!services.TryGet<IInviteShareService>(out _))
        {
            services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        }

        if (!services.TryGet<IQrCodeService>(out _))
        {
            services.AddSingleton<IQrCodeService>(new QrCodeService());
        }

        if (!services.TryGet<NLink.App.Configuration.ShareMessageConfig>(out _))
        {
            services.AddSingleton(new NLink.App.Configuration.ShareMessageConfig(null));
        }

        if (!services.TryGet<MetricsRegistry>(out _))
        {
            services.AddSingleton(new MetricsRegistry());
        }

        if (!services.TryGet<ResourceRuntimeTracker>(out _))
        {
            services.AddSingleton(new ResourceRuntimeTracker());
        }

        return services;
    }

    protected static AppServiceRegistry CreateServicesForMainWindow()
    {
        var services = new AppServiceRegistry();
        services.AddSingleton<IClipboardService>(new TestClipboardService());
        services.AddSingleton<IInviteShareService>(new DefaultInviteShareService());
        services.AddSingleton<IQrCodeService>(new QrCodeService());
        services.AddSingleton(new NLink.App.Configuration.ShareMessageConfig(null));
        services.AddSingleton(new MetricsRegistry());
        services.AddSingleton(new ResourceRuntimeTracker());
        return services;
    }

    protected static T? FindFirstDescendant<T>(Control root)
        where T : class => root.GetVisualDescendants().OfType<T>().FirstOrDefault();

    protected static Control? FindFirstControlByAutomationId(Control root, string automationId) => root.GetVisualDescendants().OfType<Control>().Concat(root.GetLogicalDescendants().OfType<Control>()).FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    protected static Control? FindFirstVisibleControlByAutomationId(Control root, string automationId) => root.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.IsVisible && string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    protected static Button FindVisibleEnabledButton(Control root, string automationId) =>
        GuiTestAssertions.FindVisibleEnabledButton(root, automationId);

    protected static async Task FlushUiAsync()
        => await GuiTestAssertions.FlushUiAsync();

    protected static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await FlushUiAsync();
        }

        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    protected static async Task<T> WaitForLayoutConditionAsync<T>(Control root, Func<T?> probe, TimeSpan timeout, string phase)
        where T : class
    {
        var current = probe();
        if (current is not null)
        {
            return current;
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void TryComplete()
        {
            var value = probe();
            if (value is not null)
            {
                tcs.TrySetResult(value);
            }
        }

        EventHandler? handler = null;
        handler = (_, _) => TryComplete();
        root.LayoutUpdated += handler;
        try
        {
            await FlushUiAsync();
            TryComplete();
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var registration = timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out waiting for {phase} after {timeout.TotalSeconds:N1}s.")));
            return await tcs.Task;
        }
        finally
        {
            root.LayoutUpdated -= handler;
        }
    }

    protected static Image? FindVisibleScreenShareViewer(Control root) => root.GetVisualDescendants().OfType<Image>().FirstOrDefault(control => control.IsVisible && string.Equals(AutomationProperties.GetAutomationId(control), "ScreenShare.Viewer", StringComparison.Ordinal) && control.Bounds.Width > 0 && control.Bounds.Height > 0);

    protected static void InvokePrivate(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, null);
    }

    protected static void InvokePrivate(object target, string methodName, params object? []? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    protected static byte[] CreateTinyImageBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }

    protected static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = writeable.Lock())
        {
            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        }

        return writeable;
    }

    protected static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyImageBytes(), writable: false);
        return new Bitmap(stream);
    }

    protected static void SetPrivateProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    protected static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    protected static async Task<ConnectedSessionContext> CreateConnectedSessionContextAsync(Action<HelpeePageViewModel>? configureIncomingApproval = null)
    {
        var transportConfig = NLink.App.Configuration.TransportRuntimeConfig.Select();
        var network = new FakeSessionTransportNetwork();
        var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-ui-smoke-" + Guid.NewGuid().ToString("N")));
        var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-ui-smoke-" + Guid.NewGuid().ToString("N")));
        var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helperRuntime, openDiagnosticsAction: static () =>
        {
        }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
        var helpee = new HelpeePageViewModel(cancelAction: static () =>
        {
        }, transportConfig, helpeeRuntime, openDiagnosticsAction: static () =>
        {
        }, clipboardService: new TestClipboardService(), shareMessageConfig: new NLink.App.Configuration.ShareMessageConfig(null));
        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), TimeSpan.FromSeconds(3));
        var connectTask = helperRuntime.StartHelperAsync(new NLink.Core.SessionConnect.PeerAddress(helpeeRuntime.CurrentLocalPeerAddress!.Value.Value), CancellationToken.None);
        await WaitUntilAsync(() => helpee.HasIncomingRequest && helpee.ConnectionState == "IncomingRequest", TimeSpan.FromSeconds(5));
        configureIncomingApproval?.Invoke(helpee);
        helpee.AllowCommand.Execute(null);
        await connectTask;
        await WaitUntilAsync(() => helpee.ConnectionState == "Connected" && helper.ConnectionState == "Connected", TimeSpan.FromSeconds(5));
        return new ConnectedSessionContext(helper, helpee, helperRuntime, helpeeRuntime);
    }

    protected static NLink.App.Configuration.TransportRuntimeConfig CreateNknUiTestConfig()
    {
        var constructor = typeof(NLink.App.Configuration.TransportRuntimeConfig).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, binder: null, new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(string), typeof(BridgeReusePolicy), typeof(Func<NLink.Core.ISignalingTransport>), }, modifiers: null);
        Assert.NotNull(constructor);
        return (NLink.App.Configuration.TransportRuntimeConfig)constructor!.Invoke(new object? [] { "NKN", "NKN internet transport", "Release", "NKN", "ui-test", true, false, false, true, "ui-test", string.Empty, string.Empty, BridgeReusePolicy.Default, (Func<NLink.Core.ISignalingTransport>)(() => new NLink.Infra.DevLocal.DevLocalTransport()), });
    }

    protected sealed class TestClipboardService : IClipboardService
    {
        public string? LastText { get; private set; }

        public Task SetTextAsync(string text)
        {
            LastText = text;
            return Task.CompletedTask;
        }
    }

    protected sealed class TestInviteShareService : IInviteShareService
    {
        public string? LastInviteText { get; private set; }

        public Task<InviteShareResult> ShareInviteAsync(string inviteText, CancellationToken ct)
        {
            LastInviteText = inviteText;
            return Task.FromResult(new InviteShareResult(true));
        }
    }

    protected sealed class FixedLocalPeerAddressTransport : ISignalingTransport, ILocalPeerAddressSignalingTransport, ISessionSecuritySignalingTransport
    {
        public FixedLocalPeerAddressTransport(string localPeerAddress)
        {
            LocalPeerAddress = localPeerAddress;
        }

        public string LocalPeerAddress { get; }
        public SessionSecurityState CurrentSessionSecurityState => SessionSecurityState.Empty;

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
    }

    protected sealed class ConnectedShellContext
    {
        public string HeaderStatusText => "Connected";
    }

    protected sealed class ConnectedChatShellContext
    {
        public string HeaderStatusText => "Connected";
        public bool ShowConnectedPanel => true;
    }

    protected sealed class MutableConnectedChatShellContext : INotifyPropertyChanged
    {
        private string headerStatusText = "Ready";
        private bool showConnectedPanel;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string HeaderStatusText
        {
            get => headerStatusText;
            set
            {
                if (string.Equals(headerStatusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                headerStatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStatusText)));
            }
        }

        public bool ShowConnectedPanel
        {
            get => showConnectedPanel;
            set
            {
                if (showConnectedPanel == value)
                {
                    return;
                }

                showConnectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConnectedPanel)));
            }
        }
    }

    protected sealed class MutableConnectedScreenShareShellContext : INotifyPropertyChanged
    {
        private string headerStatusText = "Ready";
        private bool showConnectedPanel;
        private bool showScreenSharePreviewFrame;
        private Bitmap? screenSharePreviewFrame;
        private bool isScreenSharingPreviewActive;
        private bool canShowScreenShareAction;
        private bool canToggleScreenSharePreview = true;
        private readonly RelayCommand toggleScreenSharePreviewCommand;
        public MutableConnectedScreenShareShellContext()
        {
            toggleScreenSharePreviewCommand = new RelayCommand(() =>
            {
                IsScreenSharingPreviewActive = !IsScreenSharingPreviewActive;
                ShowScreenSharePreviewFrame = IsScreenSharingPreviewActive;
            }, () => CanToggleScreenSharePreview);
            ToggleScreenSharePreviewCommand = toggleScreenSharePreviewCommand;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public string HeaderStatusText
        {
            get => headerStatusText;
            set
            {
                if (string.Equals(headerStatusText, value, StringComparison.Ordinal))
                {
                    return;
                }

                headerStatusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStatusText)));
            }
        }

        public bool ShowConnectedPanel
        {
            get => showConnectedPanel;
            set
            {
                if (showConnectedPanel == value)
                {
                    return;
                }

                showConnectedPanel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowConnectedPanel)));
            }
        }

        public bool CanShowScreenShareAction
        {
            get => canShowScreenShareAction;
            set
            {
                if (canShowScreenShareAction == value)
                {
                    return;
                }

                canShowScreenShareAction = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanShowScreenShareAction)));
            }
        }

        public bool IsScreenSharingPreviewActive
        {
            get => isScreenSharingPreviewActive;
            set
            {
                if (isScreenSharingPreviewActive == value)
                {
                    return;
                }

                isScreenSharingPreviewActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScreenSharingPreviewActive)));
            }
        }

        public bool ShowScreenSharePreviewFrame
        {
            get => showScreenSharePreviewFrame;
            set
            {
                if (showScreenSharePreviewFrame == value)
                {
                    return;
                }

                showScreenSharePreviewFrame = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowScreenSharePreviewFrame)));
            }
        }

        public Bitmap? ScreenSharePreviewFrame
        {
            get => screenSharePreviewFrame;
            set
            {
                if (ReferenceEquals(screenSharePreviewFrame, value))
                {
                    return;
                }

                screenSharePreviewFrame = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenSharePreviewFrame)));
            }
        }

        public bool CanToggleScreenSharePreview
        {
            get => canToggleScreenSharePreview;
            set
            {
                if (canToggleScreenSharePreview == value)
                {
                    return;
                }

                canToggleScreenSharePreview = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleScreenSharePreview)));
                toggleScreenSharePreviewCommand.NotifyCanExecuteChanged();
            }
        }

        public ICommand ToggleScreenSharePreviewCommand { get; }
    }

    protected sealed class StaleDisconnectedShellContext
    {
        public string HeaderStatusText => "Request rejected";
        public bool ShowConnectedPanel => true;
    }

    protected sealed class ConnectedSessionContext : IDisposable
    {
        public ConnectedSessionContext(HelperPageViewModel helper, HelpeePageViewModel helpee, SessionRuntime helperRuntime, SessionRuntime helpeeRuntime)
        {
            Helper = helper;
            Helpee = helpee;
            HelperRuntime = helperRuntime;
            HelpeeRuntime = helpeeRuntime;
        }

        public HelperPageViewModel Helper { get; }
        public HelpeePageViewModel Helpee { get; }
        public SessionRuntime HelperRuntime { get; }
        public SessionRuntime HelpeeRuntime { get; }

        public void Dispose()
        {
            Helper.Dispose();
            Helpee.Dispose();
            HelperRuntime.Dispose();
            HelpeeRuntime.Dispose();
        }
    }

    protected sealed class FakeSessionTransportNetwork
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

    protected sealed class FakeSessionTransport : NLink.Core.ISignalingTransport, NLink.Core.IAddressTargetSignalingTransport, NLink.Core.IInviteTargetSignalingTransport, NLink.Core.IAddressHostSignalingTransport, NLink.Core.IHostReadySignalingTransport, NLink.Core.ILocalPeerAddressSignalingTransport, NLink.Core.ISessionSecuritySignalingTransport
    {
        private readonly FakeSessionTransportNetwork network;
        private readonly byte[] sharedKey = SHA256LikeDeterministicBytes("beta3-ui-smoke-key", 32);
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

        public event EventHandler<NLink.Core.IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<NLink.Core.TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<NLink.Core.TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<NLink.Core.TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
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
            var joinRequest = new NLink.Core.IncomingJoinRequestEventArgs(approveAsync: (decision, _) =>
            {
                if (decision is null)
                {
                    throw new InvalidOperationException("Explicit approval decision is required.");
                }

                ValidateApprovalDecision(approvalRequest, decision);
                var grant = decision.ToGrant();
                host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.WithApproval(grant));
                UpdateSessionSecurityState(CurrentSessionSecurityState.WithApproval(grant));
                host.SessionKeyReady?.Invoke(host, new NLink.Core.TransportSessionKeyReadyEventArgs(host.sharedKey));
                SessionKeyReady?.Invoke(this, new NLink.Core.TransportSessionKeyReadyEventArgs(sharedKey));
                host.Approved?.Invoke(host, EventArgs.Empty);
                Approved?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }, rejectAsync: _ =>
            {
                host.UpdateSessionSecurityState(host.CurrentSessionSecurityState.Invalidate("local_reject"));
                UpdateSessionSecurityState(CurrentSessionSecurityState.Invalidate("local_reject"));
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
            target.ChatMessageReceived?.Invoke(target, new NLink.Core.TransportChatMessageEventArgs(payload.ToArray()));
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
            SessionSecurityStateChanged?.Invoke(this, new NLink.Core.TransportSessionSecurityStateChangedEventArgs(nextState));
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

            ).WithHandshakeVerified(helperAddress)
             .WithVerificationCode(CreateFakeVerificationCode(sessionId, helpeeAddress, helperAddress));
        }

        private static SessionVerificationCode CreateFakeVerificationCode(SessionId sessionId, PeerAddress helpeeAddress, PeerAddress helperAddress)
        {
            return SessionVerificationCodeDerivation.Derive(new SessionVerificationMaterial(
                sessionId,
                helperAddress,
                helpeeAddress,
                SHA256LikeDeterministicBytes("fake-verification-root|" + sessionId.Value, 32),
                SHA256LikeDeterministicBytes("fake-verification-helper-key|" + helperAddress.Value, 32),
                SHA256LikeDeterministicBytes("fake-verification-helpee-key|" + helpeeAddress.Value, 32),
                "fake-challenge-" + sessionId.Value,
                "fake-session-context"));
        }

        protected static void ValidateApprovalDecision(ApprovalRequest approvalRequest, ApprovalDecision decision)
        {
            if (decision.SessionId != approvalRequest.SessionId || decision.HelperIdentity != approvalRequest.HelperIdentity || decision.ExpiresAtUtc <= DateTimeOffset.UtcNow || (decision.ApprovedCapabilities & ~approvalRequest.RequestedCapabilities) != 0)
            {
                throw new InvalidOperationException("Approval decision does not match the pending approval request.");
            }
        }
    }

    protected static byte[] SHA256LikeDeterministicBytes(string text, int length)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        if (hash.Length == length)
        {
            return hash;
        }

        return hash[..length];
    }

}

