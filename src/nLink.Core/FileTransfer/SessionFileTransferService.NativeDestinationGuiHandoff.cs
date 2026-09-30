using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NLink.Core.Logging;
using NLink.Tools;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    internal sealed partial class NativeDestinationEvidence
    {
        private bool guiHandoffAttempted;

        // Explicit evidence transport only. The original GUI duplicates this
        // still-held read-only file through its existing app process handle.
        // A commit is provisional: normal app exit and original writer/parent
        // adoption remain separate requirements. No path or handle getter.
        internal async Task<bool> TryHandoffToGuiAsync(SessionFileTransferService candidate,
            FileTransferReceiveDestination target, LocalFrontierAppRequest.AppLease candidateLease,
            NamedPipeClientStream pipe, CancellationToken cancellationToken)
        {
            SafeFileHandle? retained = null;
            bool added = false, success = false;
            byte[] offer;
            var nonce = Guid.NewGuid().ToByteArray();
            lock (sync)
            {
                if (!ReferenceEquals(original, this) || !ReferenceEquals(candidate, service) ||
                    !ReferenceEquals(target, destination) || !ReferenceEquals(candidateLease, lease) ||
                    guiHandoffAttempted || file is null || !IsCurrentLocked()) return false;
                guiHandoffAttempted = true;
                try
                {
                    if (!MatchesGuiPipe(pipe) ||
                        !OperationalLogNativeSource.TryDescribe(file, out var identity, out var bytes) || bytes != ByteCount)
                    { Dispose(); return false; }
                    retained = file;
                    retained.DangerousAddRef(ref added);
                    using var body = new MemoryStream();
                    using var writer = new BinaryWriter(body, new UTF8Encoding(false, true), leaveOpen: true);
                    writer.Write(0x4e444731); writer.Write(nonce); writer.Write(lease.RequestId.ToByteArray());
                    writer.Write(lease.AppProcessId); writer.Write(lease.AppProcessCreationFileTime);
                    writer.Write(retained.DangerousGetHandle().ToInt64()); writer.Write(identity);
                    writer.Write(ByteCount); writer.Write(SessionId!); writer.Write(TransferId!); writer.Write(Sha256Base64!);
                    writer.Flush(); offer = body.ToArray();
                    if (offer.Length > 4096) throw new InvalidDataException("destination_offer_limit");
                }
                catch
                {
                    if (added) retained!.DangerousRelease();
                    Dispose(); return false;
                }
            }
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var ct = deadline.Token;
                await pipe.WriteAsync(BitConverter.GetBytes(offer.Length), ct).ConfigureAwait(false);
                await pipe.WriteAsync(offer, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                var acknowledged = new byte[nonce.Length];
                await pipe.ReadExactlyAsync(acknowledged, ct).ConfigureAwait(false);
                lock (sync)
                {
                    if (!acknowledged.AsSpan().SequenceEqual(nonce) || !IsCurrentLocked() || !MatchesGuiPipe(pipe)) return false;
                }
                await pipe.WriteAsync(nonce, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                lock (sync) success = IsCurrentLocked() && MatchesGuiPipe(pipe) && !ct.IsCancellationRequested;
                return success;
            }
            catch (IOException) { return false; }
            catch (OperationCanceledException) { return false; }
            catch (ObjectDisposedException) { return false; }
            finally
            {
                if (!success) Dispose();
                if (added) retained!.DangerousRelease();
            }
        }

        private bool MatchesGuiPipe(NamedPipeClientStream pipe)
            => pipe.IsConnected && lease.IsCurrent &&
                GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) && pid == lease.parentId && lease.IsCurrent;

        [DllImport("kernel32.dll")]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
    }
}
