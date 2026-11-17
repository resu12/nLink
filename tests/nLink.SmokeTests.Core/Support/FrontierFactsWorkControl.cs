#if FRONTIER_FACTS_WORK_CONTROL
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

// Codec-free transport control. Never submitted as facts or native custody.
public static class FrontierFactsWorkControl
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Action Check = delegate { };
    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int memcmp(IntPtr first, IntPtr second, UIntPtr length);

    private static long Measure(Func<bool> operation, int count)
    {
        for (int i = 0; i < 32; i++) if (!operation()) throw new Exception("Warmup rejected");
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < count; i++) if (!operation()) throw new Exception("Comparison rejected");
        return timer.ElapsedTicks;
    }

    public static string Run(Assembly assembly, int length, int count)
    {
        var type = assembly.GetType("NLink.Tools.LocalFrontierParentFactsChannel", true);
        var body = new byte[length];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(1 + i % 251);
        var facts = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
            new object[] { "facts-work-" + Guid.NewGuid().ToString("N"), new byte[32], body, Check });
        if (facts == null) throw new Exception("Facts control missing");
        using ((IDisposable)facts)
        {
            var view = (MemoryMappedViewAccessor)type.GetField("view", Instance).GetValue(facts);
            var header = (byte[])type.GetField("header", Instance).GetValue(facts);
            var retained = (byte[])type.GetField("body", Instance).GetValue(facts);
            var current = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), facts, type.GetProperty("IsCurrent", Instance).GetGetMethod(true));
            var compare = (Func<Action, bool>)Delegate.CreateDelegate(typeof(Func<Action, bool>), facts, type.GetMethod("MatchesCurrentBody", Instance));
            long fullTicks = Measure(current, count);
            long bodyTicks = Measure(delegate { return compare(Check); }, count);
            long headerTicks = Measure(delegate {
                for (int i = 0; i < 80; i++) if ((i < 8 || i >= 12) && view.ReadByte(i) != header[i]) return false;
                return true;
            }, count);
            var handle = view.SafeMemoryMappedViewHandle;
            bool held = false; GCHandle pinned = default(GCHandle);
            long candidateTicks;
            try
            {
                handle.DangerousAddRef(ref held); pinned = GCHandle.Alloc(retained, GCHandleType.Pinned);
                var source = IntPtr.Add(handle.DangerousGetHandle(), checked((int)view.PointerOffset + 80));
                var expected = pinned.AddrOfPinnedObject();
                candidateTicks = Measure(delegate {
                    for (int i = 0; i < retained.Length; i += 262144)
                    {
                        if (i != 0) Check();
                        if (memcmp(IntPtr.Add(source, i), IntPtr.Add(expected, i), new UIntPtr((uint)Math.Min(262144, retained.Length - i))) != 0) return false;
                    }
                    return true;
                }, count);
            }
            finally { if (pinned.IsAllocated) pinned.Free(); if (held) handle.DangerousRelease(); }
            // Isolate the original fixed-size file offer without nested facts.
            var fileType = assembly.GetType("NLink.Tools.LocalFrontierParentFileChannel", true);
            byte[] packet;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x4e504631); writer.Write(1); writer.Write(10); writer.Write(0);
                for (int i = 0; i < 3; i++) writer.Write(Guid.NewGuid().ToByteArray());
                for (int i = 0; i < 10; i++)
                {
                    writer.Write(i); writer.Write(-1); writer.Write(Guid.Empty.ToByteArray());
                    writer.Write((long)i + 1); writer.Write((uint)1); writer.Write((uint)0); writer.Write((uint)i + 1); writer.Write((long)1);
                }
                packet = stream.ToArray();
            }
            var files = fileType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new object[] { "files-work-" + Guid.NewGuid().ToString("N"), new byte[32], packet, CancellationToken.None });
            if (files == null) throw new Exception("File offer control missing");
            long fileTicks;
            using ((IDisposable)files)
            {
                var fileCurrent = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), files, fileType.GetProperty("IsCurrent", Instance).GetGetMethod(true));
                fileTicks = Measure(fileCurrent, count);
            }
            return "facts-work-control; bytes=" + length + "; calls=" + count + "; frequency=" + Stopwatch.Frequency +
                "; full_ticks=" + fullTicks + "; body_ticks=" + bodyTicks + "; header_ticks=" + headerTicks +
                "; candidate_ticks=" + candidateTicks + "; file_ticks=" + fileTicks;
        }
    }
}
#endif
