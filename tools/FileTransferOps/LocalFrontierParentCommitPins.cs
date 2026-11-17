using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierParentFilesPending
        {
            internal CommitPins DuplicateForCommit(Func<bool> current)
            {
                lock (sync)
                {
                    CommitPins result = null; bool success = false;
                    try
                    {
                        if (!NativeCurrent || !current()) return null;
                        result = new CommitPins(packet);
                        for (int i = 0; i < handles.Count; i++)
                        {
                            if (!current()) return null;
                            SafeFileHandle duplicate = null;
                            try
                            {
                                if (!DuplicateCommitHandle(GetCurrentProcess(), handles[i], GetCurrentProcess(), out duplicate, 0x80000000, false, 0) ||
                                    !Matches(duplicate, packet.Entries[i].File)) return null;
                                result.Handles.Add(duplicate); duplicate = null;
                            }
                            finally { if (duplicate != null) duplicate.Dispose(); }
                        }
                        success = NativeCurrent && current() && result.IsCurrent; return success ? result : null;
                    }
                    finally { if (!success && result != null) result.Dispose(); }
                }
            }
            internal sealed partial class CommitPins : IDisposable
            {
                private readonly object sync = new object();
                private readonly LocalFrontierParentFilePacket packet;
                internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
                private bool closed;
                internal CommitPins(LocalFrontierParentFilePacket packet) { this.packet = packet; }
                internal int Count { get { return packet.Entries.Length; } }
                internal LocalFrontierParentFilePacket.Entry EntryAt(int index) { return packet.Entries[index]; }
                internal bool IsCurrent
                {
                    get
                    {
                        lock (sync)
                        {
                            if (closed || Handles.Count != packet.Entries.Length) return false;
                            for (int i = 0; i < Handles.Count; i++) if (!Matches(Handles[i], packet.Entries[i].File)) return false;
                            return true;
                        }
                    }
                }
                public void Dispose() { lock (sync) { if (closed) return; closed = true; foreach (var handle in Handles) handle.Dispose(); } }
            }
            [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle")] private static extern bool DuplicateCommitHandle(IntPtr source,
                SafeFileHandle handle, IntPtr target, out SafeFileHandle duplicate, uint access, bool inherit, uint options);
        }
    }
}
