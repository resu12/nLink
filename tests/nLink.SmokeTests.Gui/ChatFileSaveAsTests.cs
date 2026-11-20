using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "Gui")]
public sealed class ChatFileSaveAsTests(ChatViewScrollFixture fixture) : CoreSmokeTestsBase, IClassFixture<ChatViewScrollFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiverCanChooseDestination_OrKeepDownloads(bool helperReceives)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var root = Directory.CreateTempSubdirectory("nlink-save-as-");
            var downloads = root.CreateSubdirectory("Downloads").FullName;
            var elsewhere = root.CreateSubdirectory("chosen folder").FullName;
            using var downloadsOverride = new EnvironmentOverride("NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT", downloads);
            var window = new Window { Width = 1050, Height = 760 };
            try
            {
                var host = CreateTestPeerAddress();
                var joiner = $"devlocal.helper.{Guid.NewGuid():N}";
                int accepts = 0;
                DevLocalTransport Transport(string address)
                {
                    var transport = new DevLocalTransport(address);
                    transport.FileTransferAcceptReceived += (_, _) => Interlocked.Increment(ref accepts);
                    return transport;
                }
                using var helpeeRuntime = new SessionRuntime(() => Transport(host));
                using var helperRuntime = new SessionRuntime(() => Transport(joiner));
                var config = CreateDevLocalTestConfig();
                using var helpee = new HelpeePageViewModel(() => { }, config, helpeeRuntime);
                using var helper = new HelperPageViewModel(() => { }, config, helperRuntime, connectFailureCooldown: TimeSpan.Zero);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var wait = TimeSpan.FromSeconds(8);
                await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Waiting, wait);
                var invite = CreateValidatedInviteForTarget(new PeerAddress(host), out var raw,
                    InviteCapabilities.Chat | InviteCapabilities.FileTransfer, boundHelperAddress: new PeerAddress(joiner));
                var connect = helperRuntime.StartHelperAsync(raw, invite, cts.Token);
                await WaitUntilAsync(() => helpee.HasIncomingRequest, wait);
                helpee.AllowIncomingFileTransferCapability = true;
                helpee.AllowCommand.Execute(null);
                await connect;
                await WaitUntilAsync(() => helpee.CanSendFileAction && helper.CanSendFileAction, wait);

                IChatPanelBindings receiver = helperReceives ? helper : helpee;
                var sender = helperReceives ? helpeeRuntime : helperRuntime;
                var chat = new ChatView { DataContext = receiver };
                window.Content = chat;
                window.Show();
                var payload = new byte[] { 0, 19, 255, 82 };
                var sentinel = new byte[] { 97, 98 };
                const string offered = "offered.txt";
                var chosen = Path.Combine(elsewhere, "renamed.txt");
                var occupied = Path.Combine(elsewhere, "existing.txt");
                File.WriteAllBytes(occupied, sentinel);

                async Task OfferAsync()
                {
                    var sent = await sender.StartSendAsync(new FileTransferSendDescriptor(offered, payload.Length),
                        _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), cts.Token);
                    Assert.NotNull(sent);
                    await WaitUntilAsync(() => receiver.InboundFileTransfer is { ShowAccept: true } card && card.TransferId == sent!.TransferId, wait);
                    window.UpdateLayout();
                }
                void Click(string id)
                {
                    var button = chat.GetVisualDescendants().OfType<Button>().Single(b =>
                        AutomationProperties.GetAutomationId(b) == "Chat.FileTransfer." + id &&
                        b.DataContext is FileTransferPanelItemViewModel { ShowAccept: true });
                    Assert.True(button.IsEffectivelyVisible);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                async Task AssertSavedAsync(string path)
                {
                    await WaitUntilAsync(() => receiver.InboundFileTransfer?.State == FileTransferTransferState.Completed &&
                        sender.FileTransferSnapshot.OutboundState == FileTransferTransferState.Completed, wait);
                    Assert.Equal(path, receiver.InboundFileTransfer!.SavedFilePath);
                    Assert.Contains(path, receiver.InboundFileTransfer.SavedLocationText);
                    Assert.Equal(payload, await File.ReadAllBytesAsync(path, cts.Token));
                    Assert.Empty(Directory.EnumerateFiles(root.FullName, "*.part", SearchOption.AllDirectories));
                }
                string? selection = null;
                var opens = 0;
                chat.SavePickerForTests = name =>
                {
                    Assert.Equal(offered, name);
                    opens++;
                    return Task.FromResult(selection);
                };

                await OfferAsync();
                Click("SaveAs"); // Cancel the picker, not the offer.
                Assert.Equal(1, opens);
                Assert.Equal(0, Volatile.Read(ref accepts));
                Assert.True(receiver.InboundFileTransfer!.ShowAccept);
                Assert.True(receiver.InboundFileTransfer.ShowDecline);
                var receivingRuntime = helperReceives ? helperRuntime : helpeeRuntime;
                // This invite grants file transfer, not screen sharing. The collision must not erase that denial.
                Assert.False(receivingRuntime.TryAuthorizePrivilegedAction(
                    new SessionPrivilegedAction(SessionPrivilegedActionKind.ScreenShareDispatch, "screen_share_stream")));
                const string priorDenial = "authorization_capability_missing";
                Assert.Equal(priorDenial, receivingRuntime.GetDiagnosticsSnapshot().LastAuthorizationDenialReason);
                selection = occupied;
                Click("SaveAs");
                await WaitUntilAsync(() => chat.FindControl<TextBlock>("FileActionHint")!.Text?.Contains("already in use") == true, wait);
                Assert.Equal(priorDenial, receivingRuntime.GetDiagnosticsSnapshot().LastAuthorizationDenialReason);
                Assert.Equal(sentinel, File.ReadAllBytes(occupied));
                Assert.True(receiver.InboundFileTransfer.ShowAccept);
                Assert.Equal(0, Volatile.Read(ref accepts));

                selection = chosen;
                Click("SaveAs");
                await AssertSavedAsync(chosen);
                Assert.Equal(1, Volatile.Read(ref accepts));
                Assert.Empty(Directory.EnumerateFiles(downloads));

                File.WriteAllBytes(Path.Combine(downloads, offered), sentinel);
                await OfferAsync();
                // DevLocal requires a new transfer ID after cancellation; the service tests also cover reused IDs.
                var delayed = new TaskCompletionSource<string?>();
                chat.SavePickerForTests = _ => { opens++; return delayed.Task; };
                Click("SaveAs");
                var canceledId = receiver.InboundFileTransfer!.TransferId;
                var canceledToken = receiver.InboundFileTransfer.InboundOfferToken;
                await sender.CancelTransferAsync(canceledId, uiCt: cts.Token);
                await WaitUntilAsync(() => receiver.InboundFileTransfer?.IsTerminal == true, wait);
                await OfferAsync();
                Assert.NotEqual(canceledId, receiver.InboundFileTransfer!.TransferId);
                Assert.NotEqual(canceledToken, receiver.InboundFileTransfer!.InboundOfferToken);
                var stalePath = Path.Combine(elsewhere, "stale.txt");
                delayed.SetResult(stalePath);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(receiver.InboundFileTransfer.ShowAccept);
                Assert.Equal(1, Volatile.Read(ref accepts));
                Assert.False(File.Exists(stalePath));
                Click("Accept"); // No picker, still number on collision, and don't reuse the earlier choice.
                await AssertSavedAsync(Path.Combine(downloads, "offered (1).txt"));
                Assert.Equal(4, opens);
                Assert.Equal(2, Volatile.Read(ref accepts));
                Assert.Equal(sentinel, File.ReadAllBytes(Path.Combine(downloads, offered)));

                // Hold the destination lookup while the UI handles a replacement or a revoked grant.
                await OfferAsync();
                var pending = receiver.InboundFileTransfer!;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim();
                var checking = receivingRuntime.AcceptIncomingAsAsync(pending.SessionId, pending.TransferId, pending.InboundOfferToken,
                    stalePath, () =>
                    {
                        entered.TrySetResult(Dispatcher.UIThread.CheckAccess());
                        if (!release.Wait(wait)) throw new TimeoutException("Destination check was not released.");
                    });
                try
                {
                    Assert.False(await entered.Task.WaitAsync(wait));
                    if (helperReceives)
                    {
                        await sender.CancelTransferAsync(pending.TransferId, uiCt: cts.Token);
                        await WaitUntilAsync(() => receiver.InboundFileTransfer?.IsTerminal == true, wait);
                        await OfferAsync();
                        Assert.NotEqual(pending.InboundOfferToken, receiver.InboundFileTransfer!.InboundOfferToken);
                    }
                    else
                    {
                        InvokePrivateMethod(receivingRuntime, "InvalidateSessionSecurity", "test_revoked");
                        Assert.False(receivingRuntime.CanPerform(NLink.Core.SessionSecurity.SessionCapability.FileTransfer));
                    }
                }
                finally { release.Set(); }
                Assert.Null(await checking.WaitAsync(wait));
                Assert.Equal(2, Volatile.Read(ref accepts));
                Assert.False(File.Exists(stalePath));
                if (helperReceives) Assert.True(receiver.InboundFileTransfer!.ShowAccept);
            }
            finally
            {
                window.Close();
                root.Delete(recursive: true);
            }
            return true;
        }, CancellationToken.None);
    }
}
