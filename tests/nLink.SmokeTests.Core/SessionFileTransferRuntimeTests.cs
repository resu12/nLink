using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using Xunit;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class SessionFileTransferRuntimeTests : CoreSmokeTestsBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_FileTransferSend_WithoutApproval_IsRejected()
    {
        ScriptedSignalingTransport scripted = new ScriptedSignalingTransport();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => scripted);
            sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helper);
            CoreSmokeTestsBase.SetPrivateField(sessionRuntime, "transport", scripted);
            CoreSmokeTestsBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
            CoreSmokeTestsBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
            CoreSmokeTestsBase.InvokePrivateMethod(sessionRuntime, "WireTransport", scripted);
            scripted.SetSessionSecurityStateForTests(CoreSmokeTestsBase.CreateVerifiedSecurityState(new PeerAddress("filetransfer.noapproval.helpee"), new PeerAddress(scripted.LocalPeerAddress)));
            bool condition = Assert.IsType<bool>(CoreSmokeTestsBase.InvokePrivateMethod(sessionRuntime, "TryAuthorizeFileTransferSend"));
            Assert.False(sessionRuntime.CanPerform(SessionCapability.FileTransfer));
            Assert.False(condition);
        }
        finally
        {
            if (scripted != null)
            {
                ((IDisposable)scripted).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_FileTransferCommand_RequiresGrantedCapability()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        TransportRuntimeConfig transportConfig = CoreSmokeTestsBase.CreateDevLocalTestConfig();
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
                await helpeeRuntime.StartHelpeeAsync(cts.Token);
                PeerAddress targetAddress = new PeerAddress(hostAddress);
                PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                string rawToken;
                ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.Chat | InviteCapabilities.ScreenShare, null, boundHelperAddress);
                await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helpeeRuntime.PendingApprovalRequest != null, TimeSpan.FromSeconds(2.0));
                await helpeeRuntime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.ScreenShare, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2.0));
                using HelperPageViewModel helper = new HelperPageViewModel(delegate
                {
                }, transportConfig, helperRuntime);
                Assert.False(helperRuntime.CanPerform(SessionCapability.FileTransfer));
                Assert.False(helper.CanSendFiles);
                Assert.False(helper.SendFileCommand.CanExecute(null));
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ChatPanelBindings_FileTransferBindings_ProjectGrantedSessionAndPendingOffer_ForBothRoles()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        TransportRuntimeConfig transportConfig = CoreSmokeTestsBase.CreateDevLocalTestConfig();
        byte[] payload = Encoding.UTF8.GetBytes("bindings file transfer payload");
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                HelpeePageViewModel helpee = new HelpeePageViewModel(delegate
                {
                }, transportConfig, helpeeRuntime);
                try
                {
                    Action cancelAction = delegate
                    {
                    };
                    SessionRuntime sessionRuntime = helperRuntime;
                    TimeSpan? connectFailureCooldown = TimeSpan.Zero;
                    HelperPageViewModel helper = new HelperPageViewModel(cancelAction, transportConfig, sessionRuntime, null, null, null, null, null, connectFailureCooldown);
                    try
                    {
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Waiting, TimeSpan.FromSeconds(3.0));
                        PeerAddress targetAddress = new PeerAddress(hostAddress);
                        PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                        string rawToken;
                        ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer, null, boundHelperAddress);
                        Task connectTask = helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpee.HasIncomingRequest, TimeSpan.FromSeconds(5.0));
                        helpee.AllowIncomingFileTransferCapability = true;
                        helpee.AllowCommand.Execute(null);
                        await connectTask;
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(5.0));
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.CanPerform(SessionCapability.FileTransfer) && helperRuntime.CanPerform(SessionCapability.FileTransfer), TimeSpan.FromSeconds(3.0));
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helper.ShowSendFileAction && helper.CanSendFileAction && helpee.ShowSendFileAction && helpee.CanSendFileAction, TimeSpan.FromSeconds(3.0));
                        Assert.True(helper.ShowSendFileAction);
                        Assert.True(helper.CanSendFileAction);
                        Assert.True(helper.SendFileCommand.CanExecute(null));
                        Assert.True(helpee.ShowSendFileAction);
                        Assert.True(helpee.CanSendFileAction);
                        Assert.True(helpee.SendFileCommand.CanExecute(null));
                        await helperRuntime.StartSendAsync(new FileTransferSendDescriptor("bindings-note.txt", payload.Length), (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)), cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helper.OutboundFileTransfer != null && (object)helpee.InboundFileTransfer != null, TimeSpan.FromSeconds(3.0));
                        FileTransferPanelItemViewModel outbound = helper.OutboundFileTransfer;
                        FileTransferPanelItemViewModel inbound = helpee.InboundFileTransfer;
                        Assert.NotNull(outbound);
                        Assert.NotNull(inbound);
                        Assert.False(helper.CanSendFileAction);
                        Assert.False(helper.SendFileCommand.CanExecute(null));
                        Assert.Equal("bindings-note.txt", outbound.FileName);
                        Assert.Equal(outbound.TransferId, inbound.TransferId);
                        Assert.True(inbound.ShowAccept);
                        Assert.True(inbound.ShowDecline);
                        Assert.False(inbound.ShowCancel);
                    }
                    finally
                    {
                        if (helper != null)
                        {
                            ((IDisposable)helper).Dispose();
                        }
                    }
                }
                finally
                {
                    if (helpee != null)
                    {
                        ((IDisposable)helpee).Dispose();
                    }
                }
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelperViewModel_FileTransferRequest_IsBlockedByRuntimeGuard_WhenUiFlagIsStale()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        TransportRuntimeConfig transportConfig = CoreSmokeTestsBase.CreateDevLocalTestConfig();
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
                await helpeeRuntime.StartHelpeeAsync(cts.Token);
                PeerAddress targetAddress = new PeerAddress(hostAddress);
                PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                string rawToken;
                ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.Chat | InviteCapabilities.ScreenShare, null, boundHelperAddress);
                await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helpeeRuntime.PendingApprovalRequest != null, TimeSpan.FromSeconds(5.0));
                await helpeeRuntime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.ScreenShare, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(2.0));
                using HelperPageViewModel helper = new HelperPageViewModel(delegate
                {
                }, transportConfig, helperRuntime);
                bool requested = false;
                helper.SendFileRequested += delegate
                {
                    requested = true;
                };
                CoreSmokeTestsBase.SetPrivateField(helper, "canSendFiles", true);
                CoreSmokeTestsBase.InvokePrivateMethod(helper, "RequestSendFileWindow");
                await Task.Delay(150, cts.Token);
                Assert.False(requested);
                Assert.False(helperRuntime.CanPerform(SessionCapability.FileTransfer));
                Assert.False(helper.CanSendFiles);
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionRuntime_FileTransferWriteOpen_RequiresGrantedCapability()
    {
        ScriptedSignalingTransport scripted = new ScriptedSignalingTransport();
        try
        {
            using SessionRuntime sessionRuntime = new SessionRuntime(() => scripted);
            string text = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-smoke-" + Guid.NewGuid().ToString("N"));
            try
            {
                sessionRuntime.SetRoleForTests(SessionRuntimeRole.Helpee);
                CoreSmokeTestsBase.SetPrivateField(sessionRuntime, "transport", scripted);
                CoreSmokeTestsBase.SetPrivateField(sessionRuntime, "state", SessionRuntimeState.Connected);
                CoreSmokeTestsBase.SetPrivateField(sessionRuntime, "statusText", "Connected");
                CoreSmokeTestsBase.InvokePrivateMethod(sessionRuntime, "WireTransport", scripted);
                scripted.SetSessionSecurityStateForTests(CoreSmokeTestsBase.CreateApprovedSecurityState(new PeerAddress(scripted.LocalPeerAddress), new PeerAddress("scripted.helper.peer"), CapabilityGrant.Chat));
                FileTransferWriteOpenResult fileTransferWriteOpenResult = Assert.IsType<FileTransferWriteOpenResult>(CoreSmokeTestsBase.InvokePrivateMethod(sessionRuntime, "OpenAuthorizedInboundFileWriteStream", new FileTransferDescriptor(sessionRuntime.SecurityState.SessionId.Value, sessionRuntime.SecurityState.HelperAddress.Value, "safe.txt", 128L), new FileTransferStoragePolicy(text, 1073741824L)));
                Assert.False(fileTransferWriteOpenResult.IsAllowed);
                Assert.Equal(FileTransferValidationFailure.AuthorizationDenied, fileTransferWriteOpenResult.Access.Failure);
                Assert.Equal(SessionAuthorizationFailure.CapabilityMissing, fileTransferWriteOpenResult.Access.AuthorizationFailure);
            }
            finally
            {
                if (Directory.Exists(text))
                {
                    Directory.Delete(text, recursive: true);
                }
            }
        }
        finally
        {
            if (scripted != null)
            {
                ((IDisposable)scripted).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_FileTransfer_RoundTrip_Completes_ThroughRuntimeServiceSurface()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        CoreSmokeTestsBase.CreateDevLocalTestConfig();
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
                byte[] payload = Encoding.UTF8.GetBytes("runtime file transfer payload");
                int helperFileTransferChanged = 0;
                int helpeeFileTransferChanged = 0;
                helperRuntime.FileTransferChanged += delegate
                {
                    Interlocked.Increment(ref helperFileTransferChanged);
                };
                helpeeRuntime.FileTransferChanged += delegate
                {
                    Interlocked.Increment(ref helpeeFileTransferChanged);
                };
                await helpeeRuntime.StartHelpeeAsync(cts.Token);
                PeerAddress targetAddress = new PeerAddress(hostAddress);
                PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                string rawToken;
                ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer, null, boundHelperAddress);
                await helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helpeeRuntime.PendingApprovalRequest != null, TimeSpan.FromSeconds(2.0));
                await helpeeRuntime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.FileTransfer, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(3.0));
                var receivedFileName = $"runtime-note-{Guid.NewGuid():N}.txt";
                await helperRuntime.StartSendAsync(new FileTransferSendDescriptor(receivedFileName, payload.Length), (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)), cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(2.0));
                FileTransferTransferSnapshot pendingInbound = helpeeRuntime.FileTransferSnapshot.Inbound;
                Assert.NotNull(pendingInbound);
                await helpeeRuntime.AcceptIncomingAsync(pendingInbound.TransferId, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => helperRuntime.FileTransferSnapshot.OutboundState == FileTransferTransferState.Completed && helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.Completed, TimeSpan.FromSeconds(12.0));
                FileTransferTransferSnapshot inboundSnapshot = helpeeRuntime.FileTransferSnapshot.Inbound;
                Assert.NotNull(inboundSnapshot);
                Assert.True(helperFileTransferChanged > 0);
                Assert.True(helpeeFileTransferChanged > 0);
                Assert.Equal(FileTransferTransferState.Completed, helperRuntime.FileTransferSnapshot.OutboundState);
                Assert.Equal(FileTransferTransferState.Completed, inboundSnapshot.State);
                string receivedFilePath = Path.Combine(SessionRuntime.GetDefaultInboundFileTransferRootDirectory(), inboundSnapshot.FileName);
                try
                {
                    Assert.True(File.Exists(receivedFilePath));
                    Assert.Equal(actual: await File.ReadAllBytesAsync(receivedFilePath, cts.Token), expected: payload);
                }
                finally
                {
                    if (File.Exists(receivedFilePath))
                    {
                        File.Delete(receivedFilePath);
                    }
                }
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_FileTransferPauseResume_RequiresGrantedCapability()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        byte[] payload = Encoding.UTF8.GetBytes("runtime pause authorization payload");
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                await ConnectFileTransferRuntimePairAsync(helpeeRuntime, helperRuntime, hostAddress, helperAddress, cts.Token);
                await helperRuntime.StartSendAsync(
                    new FileTransferSendDescriptor("pause-auth.txt", payload.Length),
                    (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)),
                    cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helperRuntime.FileTransferSnapshot.OutboundState == FileTransferTransferState.AwaitingAcceptance &&
                          helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.PendingDecision,
                    TimeSpan.FromSeconds(3.0));
                string transferId = helperRuntime.FileTransferSnapshot.Outbound!.TransferId;

                SetRuntimeGrantForTests(helperRuntime, CapabilityGrant.Chat);
                Assert.Null(await helperRuntime.PauseTransferAsync(transferId, "blocked_pause", cts.Token));
                Assert.False(helperRuntime.FileTransferSnapshot.Outbound!.IsPaused);

                SetRuntimeGrantForTests(helperRuntime, CapabilityGrant.Chat | CapabilityGrant.FileTransfer);
                FileTransferTransferSnapshot? paused = await helperRuntime.PauseTransferAsync(transferId, "allowed_pause", cts.Token);
                Assert.NotNull(paused);
                Assert.True(paused!.IsPaused);

                SetRuntimeGrantForTests(helperRuntime, CapabilityGrant.Chat);
                Assert.Null(await helperRuntime.ResumeTransferAsync(transferId, "blocked_resume", cts.Token));
                Assert.True(helperRuntime.FileTransferSnapshot.Outbound!.IsPaused);
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_FileTransferPauseResume_AuthorizedOutbound_PreservesTransferId()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        byte[] payload = Encoding.UTF8.GetBytes("runtime outbound pause payload");
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                await ConnectFileTransferRuntimePairAsync(helpeeRuntime, helperRuntime, hostAddress, helperAddress, cts.Token);
                await helperRuntime.StartSendAsync(
                    new FileTransferSendDescriptor("runtime-outbound-pause.txt", payload.Length),
                    (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)),
                    cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helperRuntime.FileTransferSnapshot.OutboundState == FileTransferTransferState.AwaitingAcceptance,
                    TimeSpan.FromSeconds(3.0));
                string transferId = helperRuntime.FileTransferSnapshot.Outbound!.TransferId;

                FileTransferTransferSnapshot? paused = await helperRuntime.PauseTransferAsync(transferId, "runtime_pause", cts.Token);
                Assert.NotNull(paused);
                Assert.Equal(transferId, paused!.TransferId);
                Assert.True(paused.IsPaused);

                FileTransferTransferSnapshot? resumed = await helperRuntime.ResumeTransferAsync(transferId, "runtime_resume", cts.Token);
                Assert.NotNull(resumed);
                Assert.Equal(transferId, resumed!.TransferId);
                Assert.False(resumed.IsPaused);
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_FileTransferPauseResume_AuthorizedInboundAfterAccept()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        byte[] payload = new byte[262144];
        RandomNumberGenerator.Fill(payload);
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(10.0));
        TaskCompletionSource allowRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                await ConnectFileTransferRuntimePairAsync(helpeeRuntime, helperRuntime, hostAddress, helperAddress, cts.Token);
                var receivedFileName = $"runtime-inbound-pause-{Guid.NewGuid():N}.bin";
                await helperRuntime.StartSendAsync(
                    new FileTransferSendDescriptor(receivedFileName, payload.Length),
                    (CancellationToken ct) => Task.FromResult((Stream)new GateReadStream(payload, allowRead.Task)),
                    cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.PendingDecision,
                    TimeSpan.FromSeconds(3.0));
                string transferId = helpeeRuntime.FileTransferSnapshot.Inbound!.TransferId;

                await helpeeRuntime.AcceptIncomingAsync(transferId, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helpeeRuntime.FileTransferSnapshot.InboundState is FileTransferTransferState.AwaitingMetadata
                        or FileTransferTransferState.AwaitingStart
                        or FileTransferTransferState.Receiving,
                    TimeSpan.FromSeconds(3.0));

                FileTransferTransferSnapshot? paused = await helpeeRuntime.PauseTransferAsync(transferId, "runtime_inbound_pause", cts.Token);
                Assert.NotNull(paused);
                Assert.Equal(transferId, paused!.TransferId);
                Assert.True(paused.IsPaused);

                FileTransferTransferSnapshot? resumed = await helpeeRuntime.ResumeTransferAsync(transferId, "runtime_inbound_resume", cts.Token);
                Assert.NotNull(resumed);
                Assert.Equal(transferId, resumed!.TransferId);
                Assert.False(resumed.IsPaused);
                allowRead.SetResult();
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helperRuntime.FileTransferSnapshot.OutboundState == FileTransferTransferState.Completed &&
                          helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.Completed,
                    TimeSpan.FromSeconds(10.0));
                FileTransferTransferSnapshot inboundCompleted = helpeeRuntime.FileTransferSnapshot.Inbound!;
                if (!string.IsNullOrWhiteSpace(inboundCompleted.SavedFilePath) &&
                    File.Exists(inboundCompleted.SavedFilePath))
                {
                    File.Delete(inboundCompleted.SavedFilePath);
                }
            }
            finally
            {
                allowRead.TrySetResult();
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task SessionRuntime_FileTransferPauseResume_IgnoredPathsReturnNull()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        byte[] payload = Encoding.UTF8.GetBytes("runtime ignored pause payload");
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                await ConnectFileTransferRuntimePairAsync(helpeeRuntime, helperRuntime, hostAddress, helperAddress, cts.Token);
                await helperRuntime.StartSendAsync(
                    new FileTransferSendDescriptor("runtime-pause-ignored.txt", payload.Length),
                    (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)),
                    cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helperRuntime.FileTransferSnapshot.OutboundState == FileTransferTransferState.AwaitingAcceptance &&
                          helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.PendingDecision,
                    TimeSpan.FromSeconds(3.0));
                string transferId = helperRuntime.FileTransferSnapshot.Outbound!.TransferId;

                Assert.Null(await helpeeRuntime.PauseTransferAsync(transferId, "pending_decision", cts.Token));
                Assert.False(helpeeRuntime.FileTransferSnapshot.Inbound!.IsPaused);
                Assert.Null(await helperRuntime.ResumeTransferAsync(transferId, "not_paused", cts.Token));
                Assert.Null(await helperRuntime.PauseTransferAsync("missing-transfer", "missing", cts.Token));

                Assert.NotNull(await helperRuntime.PauseTransferAsync(transferId, "first_pause", cts.Token));
                Assert.Null(await helperRuntime.PauseTransferAsync(transferId, "already_paused", cts.Token));
                Assert.True(helperRuntime.FileTransferSnapshot.Outbound!.IsPaused);

                Assert.NotNull(await helperRuntime.ResumeTransferAsync(transferId, "resume", cts.Token));
                await helpeeRuntime.DeclineIncomingAsync(transferId, "test_decline", cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => helperRuntime.FileTransferSnapshot.OutboundState == FileTransferTransferState.Declined &&
                          helpeeRuntime.FileTransferSnapshot.InboundState == FileTransferTransferState.Declined,
                    TimeSpan.FromSeconds(3.0));
                Assert.Null(await helperRuntime.PauseTransferAsync(transferId, "terminal", cts.Token));
                Assert.Null(await helperRuntime.ResumeTransferAsync(transferId, "terminal", cts.Token));
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task HelpeeViewModel_FileTransferCommand_CanInitiatePendingOffer_WhenGranted()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        TransportRuntimeConfig transportConfig = CoreSmokeTestsBase.CreateDevLocalTestConfig();
        byte[] payload = Encoding.UTF8.GetBytes("helpee initiated file transfer payload");
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                HelpeePageViewModel helpee = new HelpeePageViewModel(delegate
                {
                }, transportConfig, helpeeRuntime);
                try
                {
                    Action cancelAction = delegate
                    {
                    };
                    SessionRuntime sessionRuntime = helperRuntime;
                    TimeSpan? connectFailureCooldown = TimeSpan.Zero;
                    HelperPageViewModel helper = new HelperPageViewModel(cancelAction, transportConfig, sessionRuntime, null, null, null, null, null, connectFailureCooldown);
                    try
                    {
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Waiting, TimeSpan.FromSeconds(3.0));
                        PeerAddress targetAddress = new PeerAddress(hostAddress);
                        PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                        string rawToken;
                        ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer, null, boundHelperAddress);
                        Task connectTask = helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpee.HasIncomingRequest, TimeSpan.FromSeconds(3.0));
                        helpee.AllowIncomingFileTransferCapability = true;
                        helpee.AllowCommand.Execute(null);
                        await connectTask;
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(5.0));
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.CanPerform(SessionCapability.FileTransfer) && helperRuntime.CanPerform(SessionCapability.FileTransfer) && helpee.CanSendFileAction, TimeSpan.FromSeconds(3.0));
                        Assert.True(helpee.ShowSendFileAction);
                        Assert.True(helpee.CanSendFileAction);
                        Assert.True(helpee.SendFileCommand.CanExecute(null));
                        await helpee.StartSendFileAsync(new FileTransferSendDescriptor("helpee-note.txt", payload.Length), (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)), cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(() => (object)helpee.OutboundFileTransfer != null && (object)helper.InboundFileTransfer != null, TimeSpan.FromSeconds(3.0));
                        FileTransferPanelItemViewModel outbound = helpee.OutboundFileTransfer;
                        FileTransferPanelItemViewModel inbound = helper.InboundFileTransfer;
                        Assert.NotNull(outbound);
                        Assert.NotNull(inbound);
                        Assert.False(helpee.CanSendFileAction);
                        Assert.False(helpee.SendFileCommand.CanExecute(null));
                        Assert.Equal("helpee-note.txt", outbound.FileName);
                        Assert.Equal(outbound.TransferId, inbound.TransferId);
                        Assert.True(inbound.ShowAccept);
                        Assert.True(inbound.ShowDecline);
                        Assert.False(inbound.ShowCancel);
                        await helperRuntime.DeclineIncomingAsync(inbound.TransferId, null, cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(delegate
                        {
                            FileTransferPanelItemViewModel? outboundFileTransfer = helpee.OutboundFileTransfer;
                            int result;
                            if ((object)outboundFileTransfer != null && outboundFileTransfer.State == FileTransferTransferState.Declined)
                            {
                                FileTransferPanelItemViewModel? inboundFileTransfer = helper.InboundFileTransfer;
                                result = (((object)inboundFileTransfer != null && inboundFileTransfer.State == FileTransferTransferState.Declined) ? 1 : 0);
                            }
                            else
                            {
                                result = 0;
                            }

                            return (byte)result != 0;
                        }, TimeSpan.FromSeconds(3.0));
                    }
                    finally
                    {
                        if (helper != null)
                        {
                            ((IDisposable)helper).Dispose();
                        }
                    }
                }
                finally
                {
                    if (helpee != null)
                    {
                        ((IDisposable)helpee).Dispose();
                    }
                }
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task ChatPanelBindings_FileTransferBindings_ProjectPendingAndCompletedState_FromRuntimeSnapshots()
    {
        string hostAddress = CoreSmokeTestsBase.CreateTestPeerAddress();
        string helperAddress = $"devlocal.helper.{Guid.NewGuid():N}";
        TransportRuntimeConfig transportConfig = CoreSmokeTestsBase.CreateDevLocalTestConfig();
        byte[] payload = new byte[65536];
        RandomNumberGenerator.Fill(payload);
        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
        SessionRuntime helpeeRuntime = new SessionRuntime(() => new DevLocalTransport(hostAddress));
        try
        {
            SessionRuntime helperRuntime = new SessionRuntime(() => new DevLocalTransport(helperAddress));
            try
            {
                HelpeePageViewModel helpee = new HelpeePageViewModel(delegate
                {
                }, transportConfig, helpeeRuntime);
                try
                {
                    Action cancelAction = delegate
                    {
                    };
                    SessionRuntime sessionRuntime = helperRuntime;
                    TimeSpan? connectFailureCooldown = TimeSpan.Zero;
                    HelperPageViewModel helper = new HelperPageViewModel(cancelAction, transportConfig, sessionRuntime, null, null, null, null, null, connectFailureCooldown);
                    try
                    {
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Waiting, TimeSpan.FromSeconds(3.0));
                        PeerAddress targetAddress = new PeerAddress(hostAddress);
                        PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
                        string rawToken;
                        ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(targetAddress, out rawToken, InviteCapabilities.Chat | InviteCapabilities.FileTransfer, null, boundHelperAddress);
                        Task connectTask = helperRuntime.StartHelperAsync(rawToken, invite, cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpee.HasIncomingRequest, TimeSpan.FromSeconds(3.0));
                        helpee.AllowIncomingFileTransferCapability = true;
                        helpee.AllowCommand.Execute(null);
                        await connectTask;
                        await CoreSmokeTestsBase.WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Connected && helperRuntime.State == SessionRuntimeState.Connected, TimeSpan.FromSeconds(5.0));
                        var progressFileName = $"progress-{Guid.NewGuid():N}.bin";
                        await helper.StartSendFileAsync(new FileTransferSendDescriptor(progressFileName, payload.Length), (CancellationToken ct) => Task.FromResult((Stream)new MemoryStream(payload, writable: false)), cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(delegate
                        {
                            FileTransferPanelItemViewModel inboundFileTransfer = helpee.InboundFileTransfer;
                            return (object)inboundFileTransfer != null && inboundFileTransfer.ShowAccept && inboundFileTransfer.ShowDecline;
                        }, TimeSpan.FromSeconds(3.0));
                        FileTransferPanelItemViewModel pendingInbound = helpee.InboundFileTransfer;
                        FileTransferPanelItemViewModel pendingOutbound = helper.OutboundFileTransfer;
                        Assert.NotNull(pendingInbound);
                        Assert.NotNull(pendingOutbound);
                        Assert.Equal(FileTransferTransferState.PendingDecision, pendingInbound.State);
                        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, pendingOutbound.State);
                        Assert.False(pendingInbound.ShowProgress);
                        Assert.False(pendingOutbound.ShowProgress);
                        Assert.False(helper.CanSendFileAction);
                        Assert.False(helper.SendFileCommand.CanExecute(null));
                        string inboundTransferId = helpee.InboundFileTransfer.TransferId;
                        await helpeeRuntime.AcceptIncomingAsync(inboundTransferId, cts.Token);
                        await CoreSmokeTestsBase.WaitUntilAsync(delegate
                        {
                            FileTransferPanelItemViewModel? outboundFileTransfer = helper.OutboundFileTransfer;
                            int result;
                            if ((object)outboundFileTransfer != null && outboundFileTransfer.State == FileTransferTransferState.Completed)
                            {
                                FileTransferPanelItemViewModel? inboundFileTransfer = helpee.InboundFileTransfer;
                                result = (((object)inboundFileTransfer != null && inboundFileTransfer.State == FileTransferTransferState.Completed) ? 1 : 0);
                            }
                            else
                            {
                                result = 0;
                            }

                            return (byte)result != 0;
                        }, TimeSpan.FromSeconds(12.0));
                        FileTransferPanelItemViewModel outboundCompleted = helper.OutboundFileTransfer;
                        FileTransferPanelItemViewModel inboundCompleted = helpee.InboundFileTransfer;
                        Assert.NotNull(outboundCompleted);
                        Assert.NotNull(inboundCompleted);
                        Assert.True(outboundCompleted.IsTerminal);
                        Assert.True(inboundCompleted.IsTerminal);
                        Assert.False(outboundCompleted.ShowActions);
                        Assert.False(inboundCompleted.ShowActions);
                        Assert.False(outboundCompleted.ShowProgress);
                        Assert.False(inboundCompleted.ShowProgress);
                        Assert.True(outboundCompleted.ProgressFraction >= 1.0);
                        Assert.True(inboundCompleted.ProgressFraction >= 1.0);
                        Assert.Contains("complete", outboundCompleted.StatusText, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("complete", inboundCompleted.StatusText, StringComparison.OrdinalIgnoreCase);
                        Assert.True(helper.CanSendFileAction);
                        Assert.True(helper.SendFileCommand.CanExecute(null));
                        string? savedPath = helpeeRuntime.FileTransferSnapshot.Inbound?.SavedFilePath;
                        if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
                        {
                            File.Delete(savedPath);
                        }
                    }
                    finally
                    {
                        if (helper != null)
                        {
                            ((IDisposable)helper).Dispose();
                        }
                    }
                }
                finally
                {
                    if (helpee != null)
                    {
                        ((IDisposable)helpee).Dispose();
                    }
                }
            }
            finally
            {
                if (helperRuntime != null)
                {
                    ((IDisposable)helperRuntime).Dispose();
                }
            }
        }
        finally
        {
            if (helpeeRuntime != null)
            {
                ((IDisposable)helpeeRuntime).Dispose();
            }
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public void SessionViews_FileTransferSend_UsesNativePicker_InsteadOfSendFileWindow()
    {
        string text = CoreSmokeTestsBase.FindRepoRoot();
        string path = Path.Combine(text, "src", "nLink.App", "Views", "HelperPageView.axaml.cs");
        string path2 = Path.Combine(text, "src", "nLink.App", "Views", "HelpeePageView.axaml.cs");
        string actualString = File.ReadAllText(path);
        string actualString2 = File.ReadAllText(path2);
        Assert.DoesNotContain("SendFileWindow", actualString, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSendFileWindow", actualString, StringComparison.Ordinal);
        Assert.DoesNotContain("SendFileWindow", actualString2, StringComparison.Ordinal);
        Assert.Contains("NativeFileTransferPicker.PickSingleFileAsync", actualString, StringComparison.Ordinal);
        Assert.Contains("NativeFileTransferPicker.PickSingleFileAsync", actualString2, StringComparison.Ordinal);
    }

    private static async Task ConnectFileTransferRuntimePairAsync(
        SessionRuntime helpeeRuntime,
        SessionRuntime helperRuntime,
        string hostAddress,
        string helperAddress,
        CancellationToken ct)
    {
        await helpeeRuntime.StartHelpeeAsync(ct);
        PeerAddress targetAddress = new PeerAddress(hostAddress);
        PeerAddress? boundHelperAddress = new PeerAddress(helperAddress);
        string rawToken;
        ValidatedInviteV1 invite = CoreSmokeTestsBase.CreateValidatedInviteForTarget(
            targetAddress,
            out rawToken,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            null,
            boundHelperAddress);
        await helperRuntime.StartHelperAsync(rawToken, invite, ct);
        await CoreSmokeTestsBase.WaitUntilAsync(
            () => helpeeRuntime.PendingApprovalRequest is not null,
            TimeSpan.FromSeconds(3.0));
        await helpeeRuntime.ApproveAsync(CapabilityGrant.Chat | CapabilityGrant.FileTransfer, ct);
        await CoreSmokeTestsBase.WaitUntilAsync(
            () => helpeeRuntime.State == SessionRuntimeState.Connected &&
                  helperRuntime.State == SessionRuntimeState.Connected &&
                  helpeeRuntime.CanPerform(SessionCapability.FileTransfer) &&
                  helperRuntime.CanPerform(SessionCapability.FileTransfer),
            TimeSpan.FromSeconds(5.0));
    }

    private static void SetRuntimeGrantForTests(SessionRuntime runtime, CapabilityGrant capabilities)
    {
        SessionSecurityState current = runtime.SecurityState;
        Assert.NotNull(current.SessionId);
        Assert.NotNull(current.HelperAddress);
        var grant = new SessionGrant(
            current.HelperAddress.Value,
            capabilities,
            current.SessionId.Value,
            DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.GrantLifetime));
        CoreSmokeTestsBase.SetPrivateField(runtime, "currentSessionGrant", grant);
        CoreSmokeTestsBase.SetPrivateField(runtime, "sessionSecurityState", current.WithApproval(grant));
    }

    private sealed class GateReadStream : MemoryStream
    {
        private readonly Task gate;

        public GateReadStream(byte[] buffer, Task gate)
            : base(buffer, writable: false)
        {
            this.gate = gate;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }

}
