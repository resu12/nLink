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
[Trait("Area", "Gui")]
public sealed class SessionScenarioAndFlowTests : SessionHeaderAndBannerTestBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ViewModelFlow_HelpeeApproves_HelperAndHelpeeReachConnectedState()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-viewmodel-flow-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-viewmodel-flow-" + Guid.NewGuid().ToString("N")));
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
        Assert.Equal("Connected", helpee.ConnectionState);
        Assert.Equal("Connected", helper.ConnectionState);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Beta5_HeaderState_RemainsAuthoritative_ForConnectedChat()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-beta5-pill-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-beta5-pill-" + Guid.NewGuid().ToString("N")));
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
        await WaitUntilAsync(() => helper.EffectivePhase == SessionUiPhase.Connected && helper.IsChatInputEnabled, TimeSpan.FromSeconds(5));
        Assert.StartsWith("Connected", helper.HeaderStatusText);
        Assert.True(helper.IsChatInputEnabled);
        await helperRuntime.DisconnectAsync();
        await WaitUntilAsync(() => (helper.EffectivePhase is SessionUiPhase.Failed or SessionUiPhase.Idle or SessionUiPhase.Waiting or SessionUiPhase.Ended) && !helper.IsChatInputEnabled, TimeSpan.FromSeconds(5));
        Assert.False(helper.IsChatInputEnabled);
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Beta5_EndSession_DisablesChat_And_Command_Helper()
    {
        var transportConfig = CreateDevLocalTestConfig();
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-beta5-end-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-beta5-end-" + Guid.NewGuid().ToString("N")));
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
        await WaitUntilAsync(() => helper.EffectivePhase == SessionUiPhase.Connected && helper.IsChatInputEnabled && helper.CanEndSession, TimeSpan.FromSeconds(5));
        helper.CodeInput = "stale-invite-should-clear";
        Assert.True(helper.IsChatInputEnabled);
        Assert.True(helper.CanEndSession);
        helper.EndSessionCommand.Execute(null);
        Assert.False(helper.IsChatInputEnabled);
        Assert.False(helper.CanEndSession);
        Assert.Equal(string.Empty, helper.CodeInput);
        Assert.False(string.IsNullOrWhiteSpace(helper.HeaderStatusText));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Alpha3ScenarioA_HappyPath_HeadlessSessionRuntime_CompletesConnectAndChat()
    {
        var network = new FakeSessionTransportNetwork();
        using var helpeeRuntime = new SessionRuntime(() => network.CreateTransport("helpee-a-" + Guid.NewGuid().ToString("N")));
        using var helperRuntime = new SessionRuntime(() => network.CreateTransport("helper-a-" + Guid.NewGuid().ToString("N")));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var helperReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var helpeeReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        helperRuntime.ChatMessageReceived += (_, e) => helperReceived.TrySetResult(e.Message.Text);
        helpeeRuntime.ChatMessageReceived += (_, e) => helpeeReceived.TrySetResult(e.Message.Text);
        await helpeeRuntime.StartHelpeeAsync(cts.Token);
        await helperRuntime.StartHelperAsync(GetHostedAddressOrThrow(helpeeRuntime), cts.Token);
        await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(1));
        await helpeeRuntime.ApproveAsync(cts.Token);
        await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected && helpeeRuntime.HasSessionKey && helperRuntime.HasSessionKey, TimeSpan.FromSeconds(1));
        Assert.NotNull(await helperRuntime.TrySendChatTextAsync("hello-a", cts.Token));
        Assert.Equal("hello-a", await helpeeReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));
        helperReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        helperRuntime.ChatMessageReceived += (_, e) => helperReceived.TrySetResult(e.Message.Text);
        Assert.NotNull(await helpeeRuntime.TrySendChatTextAsync("reply-a", cts.Token));
        Assert.Equal("reply-a", await helperReceived.Task.WaitAsync(TimeSpan.FromSeconds(1), cts.Token));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Alpha3ScenarioC_SessionEnd_HeadlessRemoteEnd_ShowsFriendlyMessage()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            using var helpeeTransport = new NknSignalingTransport(new FakeNknClient("helpee.c.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helpee-c", "helpee.c.fake"));
            using var helperTransport = new NknSignalingTransport(new FakeNknClient("helper.c.addr." + Guid.NewGuid().ToString("N")), options, new NknIdentity("helper-c", "helper.c.fake"));
            using var helpeeRuntime = new SessionRuntime(() => helpeeTransport);
            using var helperRuntime = new SessionRuntime(() => helperTransport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await helpeeRuntime.StartHelpeeAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(GetHostedAddressOrThrow(helpeeRuntime), out var rawToken);
            await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.IncomingJoinRequest, TimeSpan.FromSeconds(2));
            await helpeeRuntime.ApproveAsync(cts.Token);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2));
            await helperRuntime.DisconnectAsync();
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Failed && string.Equals(helpeeRuntime.StatusText, "The helper ended the session.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Alpha3ScenarioB_WrongCodeTimeout_HeadlessHelperVm_ShowsFriendlyFailure_AndReconnect()
    {
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => throw new TimeoutException("Could not find target session")));
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, approvalTimeout: TimeSpan.FromMilliseconds(100), connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.timeout.alpha3"), out var inviteToken);
        helper.CodeInput = inviteToken;
        await helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => (string.Equals(helper.StatusText, "No response from target address.", StringComparison.Ordinal) || string.Equals(helper.TransientBannerText, "No response from target address.", StringComparison.Ordinal)) && (helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null)), TimeSpan.FromSeconds(2));
        Assert.True(string.Equals(helper.ConnectionState, "Failed", StringComparison.Ordinal) || string.Equals(helper.ConnectionState, "Waiting", StringComparison.Ordinal));
        Assert.True(helper.ConnectCommand.CanExecute(null) || helper.RetryCommand.CanExecute(null));
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task Alpha3ScenarioD_DisconnectAndRetry_HeadlessHelperVm_ReturnsToIdle()
    {
        var scripted = new ScriptedSignalingTransport(onJoinByAddressAsync: static (_, __) => Task.CompletedTask);
        var transportConfig = CreateDevLocalTestConfig();
        using var runtime = new SessionRuntime(() => scripted);
        using var helper = new HelperPageViewModel(cancelAction: static () =>
        {
        }, transportConfig, runtime, connectFailureCooldown: TimeSpan.Zero);
        CreateValidatedInviteForTarget(new PeerAddress("scripted.disconnect.alpha3"), out var inviteToken);
        helper.CodeInput = inviteToken;
        var connectTask = helper.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Connecting, TimeSpan.FromSeconds(1));
        scripted.RaiseDisconnected();
        await connectTask;
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Failed && string.Equals(helper.StatusText, "Connection lost.", StringComparison.Ordinal) && helper.ShowRetryAction, TimeSpan.FromSeconds(2));
        Assert.True(helper.RetryCommand.CanExecute(null));
        await helper.RetryCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.State == SessionRuntimeState.Waiting && helper.ConnectionState == "Waiting" && !helper.ShowRetryAction, TimeSpan.FromSeconds(2));
    }

}
