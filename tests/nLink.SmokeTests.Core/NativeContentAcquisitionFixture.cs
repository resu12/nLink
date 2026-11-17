using System.Reflection;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

// Original operational writes for native input-size/custody tests. These rows
// are deliberately NOT coordinator snapshots or transport/content evidence.
public static class NativeContentAcquisitionFixture
{
    public static void WriteScaleRows(int ordinal)
    {
        if (ordinal != 1) return;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var pump = (OrderedOperationalLogWritePump)typeof(LocalOperationalLog).GetField("WritePump", flags)!.GetValue(null)!;
        var sequence = typeof(LocalOperationalLog).GetField("writerSequence", flags)!;
        var padding = new string('x', 1700);
        for (var i = 0; i < 102215; i++)
        {
            var kind = i < 5524 ? "content_native_boundary_shape_fixture" : "content_native_size_fixture";
            LocalOperationalLog.Info("NativeInputFixture", $"event={kind}; ordinal={i}; padding={padding}");
            if ((i + 1) % 128 == 0)
                Assert.True(pump.FlushThroughSequence((long)sequence.GetValue(null)!, TimeSpan.FromSeconds(5)));
        }
        Assert.True(pump.FlushThroughSequence((long)sequence.GetValue(null)!, TimeSpan.FromSeconds(5)));
    }
}
