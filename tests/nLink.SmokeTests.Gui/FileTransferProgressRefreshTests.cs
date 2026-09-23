using System.ComponentModel;
using System.Reflection;
using Avalonia.Threading;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "Gui")]
public sealed class FileTransferProgressRefreshTests : SessionHeaderAndBannerTestBase, IClassFixture<ChatViewScrollFixture>
{
    private readonly ChatViewScrollFixture fixture;

    public FileTransferProgressRefreshTests(ChatViewScrollFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData(false, FileTransferTransferState.Sending)]
    [InlineData(true, FileTransferTransferState.Sending)]
    [InlineData(false, FileTransferTransferState.Completed)]
    [InlineData(true, FileTransferTransferState.Completed)]
    public async Task QueuedTransferRefresh_CannotReviveAnOldTransfer(bool helpee, FileTransferTransferState state)
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var runtime = new SessionRuntime(() => new DevLocalTransport(),
                SessionRuntimeWatchdogOptions.Default with { Enabled = false });
            using var page = helpee
                ? (IDisposable)new HelpeePageViewModel(() => { }, CreateDevLocalTestConfig(), runtime)
                : new HelperPageViewModel(() => { }, CreateDevLocalTestConfig(), runtime);
            var bindings = (IChatPanelBindings)page;
            var old = new FileTransferTransferSnapshot("old-session", "old-transfer", FileTransferDirection.Outbound,
                state, "old.bin", 100, null, 60, 6, 10, 10, null, null);
            var method = page.GetType().GetMethod("OnFileTransferChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var scheduled = page.GetType().GetField("fileTransferUiRefreshScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var revived = false;
            ((INotifyPropertyChanged)page).PropertyChanged += (_, _) => revived |= bindings.OutboundFileTransfer is not null;

            // Simulate a late event queued after the service already removed the transfer.
            Assert.Null(runtime.FileTransferSnapshot.Outbound);
            await Task.Run(() => method.Invoke(page, [runtime,
                new SessionFileTransferSnapshotChangedEventArgs(new(old, null))]));
            for (var i = 0; i < 100 && (int)scheduled.GetValue(page)! != 0; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Assert.Equal(0, (int)scheduled.GetValue(page)!);
            Assert.False(revived);
            Assert.Null(bindings.OutboundFileTransfer);
            return true;
        }, CancellationToken.None);
    }
}
