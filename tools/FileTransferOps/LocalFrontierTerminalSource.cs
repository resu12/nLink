using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        // The receipt's existing hash/count passes need the original file
        // object: reopening its path conflicts with its restrictive sharing.
        // This reader grants no diagnostic/terminal acceptance authority.
        public TerminalSourceRead TryOpenTerminalSource(string path)
        {
            lock (frontierInputSync)
            {
                if (!Object.ReferenceEquals(frontierOriginal, this)) return null;
                try
                {
                    if (frontierBody == null || frozenSource == null || Volatile.Read(ref disposed) != 0 || closed.WaitOne(0)) return null;
                    using (var body = new BinaryReader(new MemoryStream(frontierBody, false)))
                    {
                        body.ReadString(); body.ReadString(); body.ReadString();
                        if (!String.Equals(Path.GetFullPath(path), body.ReadString(), StringComparison.OrdinalIgnoreCase)) return null;
                    }
                    return new TerminalSourceRead(this);
                }
                catch { return null; }
            }
        }
        public sealed class TerminalSourceRead : Stream
        {
            private readonly TerminalSourceRead original;
            private readonly LocalRetainedSourceRequest owner;
            private readonly MemoryMappedFile map;
            private readonly LocalFrontierPrimaryScope scope;
            private readonly long length;
            private long position;
            private bool ended;
            internal TerminalSourceRead(LocalRetainedSourceRequest owner)
            {
                original = this; this.owner = owner; length = owner.frozenSource.Length;
                scope = new LocalFrontierPrimaryScope(owner.map, owner.frontierBody);
                try { if (length > 0) map = MemoryMappedFile.CreateFromFile(owner.frozenSource, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true); }
                catch { scope.Dispose(); throw; }
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                lock (owner.frontierInputSync)
                {
                    if (!Object.ReferenceEquals(original, this) || ended || Volatile.Read(ref owner.disposed) != 0 || owner.closed.WaitOne(0) || !scope.IsCurrent)
                        throw new IOException("frontier_terminal_source_retired");
                    int take = (int)Math.Min(Math.Min(count, 32768), length - position);
                    if (take > 0) using (var view = map.CreateViewAccessor(position, take, MemoryMappedFileAccess.Read))
                        if (view.ReadArray(0, buffer, offset, take) != take) throw new IOException("frontier_terminal_source_partial");
                    if (Volatile.Read(ref owner.disposed) != 0 || owner.closed.WaitOne(0) || !scope.IsCurrent) throw new IOException("frontier_terminal_source_retired");
                    position += take; return take;
                }
            }
            protected override void Dispose(bool disposing)
            { if (disposing && Object.ReferenceEquals(original, this) && !ended) { ended = true; if (map != null) map.Dispose(); scope.Dispose(); } base.Dispose(disposing); }
            public override bool CanRead { get { return !ended && Object.ReferenceEquals(original, this); } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
