#if NLINK_TEST_NATIVE_CONTENT_ACQUISITION
using System;
using System.IO.MemoryMappedFiles;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierLaunchOwner
        {
            // Byte-comparison fixture only: deliberately no registered owner,
            // process lease, sealed files, custody receipt or authority verdict.
            public static int CheckHeaderSnapshotParityForTest()
            {
                var owner = new FrontierLaunchOwner();
                owner.id = Guid.NewGuid(); owner.parentId = 321; owner.parentCreation = 123456789;
                owner.guiId = 654; owner.guiCreation = 987654321;
                owner.registration = new byte[17];
                for (int i = 0; i < owner.registration.Length; i++) owner.registration[i] = (byte)(i + 1);
                using (var map = MemoryMappedFile.CreateNew(null, HeaderBytes + owner.registration.Length))
                using (var view = map.CreateViewAccessor())
                {
                    owner.view = view;
                    view.Write(0, Magic); view.Write(4, owner.parentId); view.Write(8, owner.parentCreation);
                    view.Write(16, owner.guiId); view.Write(20, owner.registration.Length);
                    view.WriteArray(24, owner.id.ToByteArray(), 0, 16); view.Write(40, owner.guiCreation);
                    view.WriteArray(HeaderBytes, owner.registration, 0, owner.registration.Length);
                    RequireParity(owner, true); int checks = 1;
                    for (int offset = 0; offset < HeaderBytes + owner.registration.Length; offset++)
                    {
                        byte original = view.ReadByte(offset);
                        for (int bit = 0; bit < 8; bit++)
                        {
                            view.Write(offset, (byte)(original ^ (1 << bit)));
                            RequireParity(owner, LegacyMatchesRequest(owner)); checks++;
                            view.Write(offset, original);
                            RequireParity(owner, true); checks++;
                        }
                    }
                    foreach (int stage in new[] { 0, 1, 2, -1 })
                    { view.Write(48, stage); RequireParity(owner, stage == 0 || stage == 1); checks++; }
                    return checks;
                }
            }
            private static void RequireParity(FrontierLaunchOwner owner, bool expected)
            {
                if (owner.MatchesRequest() != expected) throw new InvalidOperationException("native_gui_header_snapshot_parity");
            }
            private static bool LegacyMatchesRequest(FrontierLaunchOwner owner)
            {
                var view = owner.view; int stage = view.ReadInt32(48);
                if (view.ReadInt32(0) != Magic || ReadId(view) != owner.id || view.ReadInt32(4) != owner.parentId ||
                    view.ReadInt64(8) != owner.parentCreation || view.ReadInt32(16) != owner.guiId || view.ReadInt64(40) != owner.guiCreation ||
                    view.ReadInt32(20) != owner.registration.Length || (stage != 0 && stage != 1) || view.ReadInt32(52) != 0 ||
                    view.ReadInt64(56) != 0) return false;
                var bytes = new byte[owner.registration.Length];
                if (view.ReadArray(HeaderBytes, bytes, 0, bytes.Length) != bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++) if (bytes[i] != owner.registration[i]) return false;
                return true;
            }
        }
    }
}
#endif
