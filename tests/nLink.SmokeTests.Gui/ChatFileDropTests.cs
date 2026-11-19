using System.Buffers.Binary;
using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
public sealed class ChatFileDropTests(ChatViewScrollFixture fixture) : CoreSmokeTestsBase, IClassFixture<ChatViewScrollFixture>
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ChatDrop_UsesOrdinaryOfferOnlyWhileAuthorized(bool helperSends, bool revokeAfterHover)
    {
        await fixture.Session.Dispatch(async () =>
        {
            var offers = new ConcurrentQueue<FileTransferOfferV2>();
            var accepts = 0;
            DevLocalTransport Transport(string address)
            {
                var transport = new DevLocalTransport(address);
                transport.FileTransferOfferReceived += (_, e) => offers.Enqueue(e.Message);
                transport.FileTransferAcceptReceived += (_, _) => Interlocked.Increment(ref accepts);
                return transport;
            }

            var host = CreateTestPeerAddress();
            var joiner = $"devlocal.helper.{Guid.NewGuid():N}";
            using var helpeeRuntime = new SessionRuntime(() => Transport(host));
            using var helperRuntime = new SessionRuntime(() => Transport(joiner));
            var config = CreateDevLocalTestConfig();
            using var helpee = new HelpeePageViewModel(() => { }, config, helpeeRuntime);
            using var helper = new HelperPageViewModel(() => { }, config, helperRuntime, connectFailureCooldown: TimeSpan.Zero);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var wait = TimeSpan.FromSeconds(5);
            await WaitUntilAsync(() => helpeeRuntime.State == SessionRuntimeState.Waiting, wait);
            var invite = CreateValidatedInviteForTarget(new PeerAddress(host), out var raw,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, boundHelperAddress: new PeerAddress(joiner));
            var connect = helperRuntime.StartHelperAsync(raw, invite, cts.Token);
            await WaitUntilAsync(() => helpee.HasIncomingRequest, wait);
            helpee.AllowIncomingFileTransferCapability = true;
            helpee.AllowCommand.Execute(null);
            await connect;
            await WaitUntilAsync(() => helpee.CanSendFileAction && helper.CanSendFileAction, wait);

            IChatPanelBindings sender = helperSends ? helper : helpee;
            IChatPanelBindings receiver = helperSends ? helpee : helper;
            var pickerRequests = 0;
            helper.SendFileRequested += (_, _) => pickerRequests++;
            helpee.SendFileRequested += (_, _) => pickerRequests++;
            UserControl page = helperSends ? new HelperPageView() : new HelpeePageView();
            page.DataContext = sender;
            var window = new Window { Content = page, Width = 1200, Height = 800 };
            var directory = Directory.CreateTempSubdirectory("nlink-drop-");
            var path = Path.Combine(directory.FullName, "note.txt");
            try
            {
                window.Show();
                var shell = page.GetVisualDescendants().OfType<SessionShellView>().Single();
                var chat = page.GetVisualDescendants().OfType<ChatView>().Single();
                Assert.True(DragDrop.GetAllowDrop(chat));
                Assert.False(DragDrop.GetAllowDrop(shell.MainContent!));
                var target = chat.FindControl<ScrollViewer>("MessagesScrollViewer")!;
                var hint = chat.FindControl<TextBlock>("FileDropHint")!;
                File.WriteAllBytes(path, [3, 1, 4]);
                using var file = Assert.IsAssignableFrom<IStorageFile>(await window.StorageProvider.TryGetFileFromPathAsync(path));
                using var folder = Assert.IsAssignableFrom<IStorageFolder>(await window.StorageProvider.TryGetFolderFromPathAsync(directory.FullName));
                var item = DataTransferItem.CreateFile(file);
                var valid = Data(item, ShellSelectionCount(1));
                var missingCount = Data(item); // One resolved file does not prove only one was selected.
                Assert.Equal(DragDropEffects.Copy, Raise(target, DragDrop.DragOverEvent, valid));
                Assert.Equal(DragDropEffects.None, Raise(target, DragDrop.DropEvent, missingCount));
                Assert.True(hint.IsVisible);
                Assert.Empty(offers);
                Assert.Null(sender.OutboundFileTransfer);
                Assert.Null(receiver.InboundFileTransfer);
                Assert.Equal(DragDropEffects.None, Raise(target, DragDrop.DragOverEvent, missingCount));
                foreach (var invalid in new[] {
                    Data(), Data(DataTransferItem.CreateText(path)), Data(DataTransferItem.CreateFile(folder)),
                    Data(item, item), Data(item, DataTransferItem.CreateFile(folder)), Data(item, DataTransferItem.CreateText("extra")),
                    Data(item, ShellSelectionCount(0)),
                    Data(item, ShellSelectionCount(2)), // one file survives path resolution from a larger Explorer selection
                })
                {
                    Assert.Equal(DragDropEffects.None, Raise(target, DragDrop.DragOverEvent, invalid));
                    Assert.True(hint.IsVisible);
                    Assert.Equal(DragDropEffects.None, Raise(target, DragDrop.DropEvent, invalid));
                }
                Assert.Empty(offers);
                Assert.Null(sender.OutboundFileTransfer);
                Assert.Equal(DragDropEffects.Copy, Raise(target, DragDrop.DragOverEvent, valid));
                Assert.False(hint.IsVisible);
                if (revokeAfterHover)
                {
                    var runtime = helperSends ? helperRuntime : helpeeRuntime;
                    InvokePrivateMethod(runtime, "InvalidateSessionSecurity", "test_revoked");
                    Assert.False(runtime.CanPerform(NLink.Core.SessionSecurity.SessionCapability.FileTransfer));
                    Assert.Equal(DragDropEffects.None, Raise(target, DragDrop.DropEvent, valid));
                    Assert.True(hint.IsVisible);
                    Assert.Empty(offers);
                    Assert.Null(sender.OutboundFileTransfer);
                    Assert.Null(receiver.InboundFileTransfer);
                }
                else
                {
                    Assert.Equal(DragDropEffects.Copy, Raise(target, DragDrop.DropEvent, valid));
                    await WaitUntilAsync(() => receiver.InboundFileTransfer?.ShowAccept == true && sender.OutboundFileTransfer != null, wait);
                    var offer = Assert.Single(offers);
                    Assert.Equal("note.txt", offer.FileName);
                    Assert.Equal(3L, offer.FileSizeBytes);
                    Assert.Equal(offer.TransferId, sender.OutboundFileTransfer!.TransferId);
                    Assert.Equal(offer.TransferId, receiver.InboundFileTransfer!.TransferId);
                    Assert.True(receiver.InboundFileTransfer.ShowDecline);
                    Assert.Equal(DragDropEffects.None, Raise(target, DragDrop.DropEvent, valid)); // outbound already pending
                    Assert.Single(offers);
                }
                Assert.Equal(0, pickerRequests);
                Assert.Equal(0, Volatile.Read(ref accepts));
            }
            finally
            {
                window.Close();
                File.Delete(path);
                directory.Delete();
            }
            return true;
        }, CancellationToken.None);
    }

    private static DataTransfer Data(params DataTransferItem[] items)
    {
        var data = new DataTransfer();
        foreach (var item in items) data.Add(item);
        return data;
    }

    private static DataTransferItem ShellSelectionCount(uint count)
    {
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, count);
        var item = DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("Shell IDList Array"), header);
        // Win32's formats item advertises File too, but does not return a single IStorageFile.
        item.Set(DataFormat.File, () => null);
        return item;
    }

    private static DragDropEffects Raise(Control target, RoutedEvent<DragEventArgs> routedEvent, IDataTransfer data)
    {
        var args = new DragEventArgs(routedEvent, data, target, default, KeyModifiers.None) { DragEffects = DragDropEffects.Copy };
        target.RaiseEvent(args);
        Assert.True(args.Handled);
        return args.DragEffects;
    }
}
