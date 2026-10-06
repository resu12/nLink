using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.Configuration;
using NLink.Infra.Nkn;
using NLink.Tests;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class AttemptOfflineFrameScheduleTests
{
    private static byte[] Bytes(int index, int count = 256) => Enumerable.Repeat((byte)(index + 1), count).ToArray();
    private static AttemptOfflineFrameHint Hint(int index, byte[] bytes) => new("sender",
        new("session", "transfer", false, index, 1, 0, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static long Started(AttemptOfflineFrameSchedule schedule, int index)
    {
        var held = (IDictionary)typeof(AttemptOfflineFrameSchedule).GetField("held", PrivateInstance)!.GetValue(schedule)!;
        var frame = held[index]!;
        return (long)frame.GetType().GetProperty("Started")!.GetValue(frame)!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("RequiredConfiguration", "Debug")]
    public void OriginalFramesDropHoldAndReleaseInExactOrderWithoutBlockingLaterDelivery(bool latePublication)
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication);
        var delivered = new List<int>();
        for (var index = 0; index < 8; index++)
        {
            var bytes = Bytes(index); var original = (byte[])bytes.Clone(); var chunk = index;
            schedule.Receive(Hint(index, bytes), bytes, value => { Assert.Equal(original, value); delivered.Add(chunk); });
            Array.Fill(bytes, (byte)99); // Retained delivery owns an exact copy.
        }
        Assert.True(schedule.AllOrdinarySeen);
        Assert.Equal(new[] { 3, 5, 6, 7 }, delivered);
        Assert.InRange(schedule.PendingBytes, 3 * 256 + 1, 3 * (256 + 1024));
        schedule.Release(2); schedule.Release(1); schedule.Release(4);
        Assert.Equal(new[] { 3, 5, 6, 7, 2, 1, 4 }, delivered);
        Assert.Equal(0, schedule.PendingBytes);
        Assert.Throws<IOException>(() => schedule.Release(4));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)]
    public void DuplicateOrdinaryFramesNeverBypassTheFault(int index)
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None);
        var bytes = Bytes(index); var deliveries = 0;
        schedule.Receive(Hint(index, bytes), bytes, _ => deliveries++);
        var before = deliveries;
        Assert.Throws<IOException>(() => schedule.Receive(Hint(index, bytes), bytes, _ => deliveries++));
        Assert.Equal(before, deliveries); Assert.Equal(0, schedule.PendingBytes);
        Assert.Throws<IOException>(schedule.Check);
    }

    [Theory]
    [InlineData("sender")] [InlineData("session")] [InlineData("transfer")] [InlineData("hash")]
    [InlineData("length")] [InlineData("count")] [InlineData("epoch")] [InlineData("repair")]
    public void ForeignOrAmbiguousHintFailsWithoutDelivery(string fault)
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None);
        var bytes = Bytes(1); var hint = Hint(1, bytes);
        hint = fault switch
        {
            "sender" => hint with { Sender = "foreign" },
            "session" => hint with { Frame = hint.Frame with { SessionId = "foreign" } },
            "transfer" => hint with { Frame = hint.Frame with { TransferId = "foreign" } },
            "hash" => hint with { Frame = hint.Frame with { EnvelopeSha256 = "wrong" } },
            "length" => hint with { Frame = hint.Frame with { EnvelopeLength = 255 } },
            "count" => hint with { Frame = hint.Frame with { ChunkCount = 3 } },
            "epoch" => hint with { Frame = hint.Frame with { TransportEpoch = 1 } },
            _ => hint with { Frame = hint.Frame with { IsRepair = true } },
        };
        Assert.Throws<IOException>(() => schedule.Receive(hint, bytes, _ => Assert.Fail("Invalid delivery")));
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("RequiredConfiguration", "Debug")]
    public void DescriptorCapacityAndAggregateHeldCapacityAreEnforced(bool latePublication)
    {
        var bytes = Bytes(1);
        Assert.Throws<IOException>(() => (Hint(1, bytes) with { Sender = new string('x', 1025) }).Encode());
        Assert.Throws<IOException>(() => AttemptOfflineFrameHint.Decode(new byte[1025]));
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication);
        bytes = Bytes(1, 524288); schedule.Receive(Hint(1, bytes), bytes, _ => Assert.Fail("Held"));
        bytes = Bytes(2, 524288);
        Assert.Throws<IOException>(() => schedule.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Overflow delivered")));
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("RequiredConfiguration", "Debug")]
    public void CancellationAndRetirementDiscardFramesWithoutRelease(bool latePublication)
    {
        using var cancellation = new CancellationTokenSource();
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", cancellation.Token, latePublication);
        var bytes = Bytes(2); schedule.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Cancelled delivery"));
        cancellation.Cancel(); Assert.Equal(0, schedule.PendingBytes);
        Assert.Throws<IOException>(() => schedule.Release(2));
        using var retired = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication);
        retired.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Retired delivery"));
        retired.Dispose(); Assert.Equal(0, retired.PendingBytes);
        Assert.Throws<ObjectDisposedException>(retired.Check);
    }

    [Fact]
    public async Task OriginalThreeSecondDeadlineIsNotRenewedByLaterFrames()
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None);
        var bytes = Bytes(1); schedule.Receive(Hint(1, bytes), bytes, _ => Assert.Fail("Expired delivery"));
        await Task.Delay(2000);
        bytes = Bytes(2); schedule.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Discarded delivery"));
        await Task.Delay(1100);
        Assert.Throws<IOException>(schedule.Check); Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData(null, 3000, "frontier-fixed-3s-v1")]
    [InlineData(false, 3000, "frontier-fixed-3s-v1")]
    [InlineData(true, 4000, "late-publication-fixed-4s-v1")]
    [Trait("RequiredConfiguration", "Debug")]
    public void FixedPolicyIsImmutableAndReportedOnce(bool? latePublication, int milliseconds, string policy)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            using var schedule = latePublication is null
                ? new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None)
                : new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication.Value);
            var field = typeof(AttemptOfflineFrameSchedule).GetField("holdMilliseconds", PrivateInstance)!;
            Assert.True(field.IsInitOnly); Assert.Equal(milliseconds, field.GetValue(schedule));
            var bytes = Bytes(1); schedule.Receive(Hint(1, bytes), bytes, _ => Assert.Fail("Held delivery"));
            Assert.Equal(Started(schedule, 1) + milliseconds, schedule.ReadBudget().OldestExpiresAtTick);
            Assert.Equal("carrier-frontier-hold-policy=" + policy + "; max_hold_ms=" + milliseconds,
                Assert.Single(output.ToString().Split(Environment.NewLine)
                    .Where(line => line.StartsWith("carrier-frontier-hold-policy=", StringComparison.Ordinal))));
        }
        finally { Console.SetOut(original); }
    }

    [Theory]
    [InlineData(false, -1)] [InlineData(false, 0)] [InlineData(false, 1)]
    [InlineData(true, -1)] [InlineData(true, 0)] [InlineData(true, 1)]
    [Trait("RequiredConfiguration", "Debug")]
    public void BudgetAndEnforcedExpiryAgreeAtExactBoundary(bool latePublication, int offset)
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication);
        var type = typeof(AttemptOfflineFrameSchedule);
        lock (type.GetField("gate", PrivateInstance)!.GetValue(schedule)!)
        {
            // Sample the owned test scheduler's check at an exact tick without
            // sleeping across a boundary or changing any retained frame clock.
            ((Timer)type.GetField("expiry", PrivateInstance)!.GetValue(schedule)!).Dispose();
            var bytes = Bytes(2); schedule.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Expired delivery"));
            var deadline = Started(schedule, 2) + (latePublication ? 4000 : 3000);
            Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
            type.GetMethod("Expire", PrivateInstance)!.Invoke(schedule, [deadline + offset]);
            if (offset < 0)
            {
                schedule.Check(); Assert.True(schedule.PendingBytes > 0);
                Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
            }
            else
            {
                Assert.IsType<TimeoutException>(Assert.Throws<IOException>(schedule.Check).InnerException);
                Assert.Equal(0, schedule.PendingBytes);
                Assert.Throws<IOException>(() => schedule.Release(2));
                Assert.Throws<IOException>(() => schedule.ReadBudget());
            }
        }
    }

    [Fact]
    [Trait("RequiredConfiguration", "Debug")]
    public async Task LateFourSecondDeadlineIsNotRenewedByLaterFrames()
    {
        using var schedule = new AttemptOfflineFrameSchedule("sender", "session", "transfer", CancellationToken.None, latePublication: true);
        var bytes = Bytes(1); schedule.Receive(Hint(1, bytes), bytes, _ => Assert.Fail("Expired delivery"));
        var deadline = schedule.ReadBudget().OldestExpiresAtTick;
        await Task.Delay(2000);
        bytes = Bytes(2); schedule.Receive(Hint(2, bytes), bytes, _ => Assert.Fail("Discarded delivery"));
        await Task.Delay(1100);
        schedule.Check(); Assert.Equal(deadline, schedule.ReadBudget().OldestExpiresAtTick);
        await Task.Delay(1000);
        Assert.IsType<TimeoutException>(Assert.Throws<IOException>(schedule.Check).InnerException);
        Assert.Equal(0, schedule.PendingBytes);
    }

    [Theory]
    [InlineData(null, false, false, "late_publication_requires_small_schedule")]
    [InlineData("valid", true, false, "offline_schedule_small_only")]
    [InlineData(null, true, true, "offline_schedule_small_only")]
    [InlineData("valid", false, false, "late_publication_requires_small_schedule")]
    public async Task LatePolicyRejectsIncompatibleProfilesBeforeOpeningCarrier(string? nativeCase,
        bool frontierSchedule, bool frontierHold, string expected)
    {
        // Null process/peer inputs ensure rejection precedes connection or lease use.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NknAccelerationTransportTests.RunCrossProcessCarrierAsync(null!, null!, "", "helper", "",
                nativeCase: nativeCase, frontierHold: frontierHold, frontierSchedule: frontierSchedule, latePublication: true));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void MetadataIsBoundedOneShotAndRepairControlBytesAreUntouched()
    {
        var descriptors = new AttemptOfflineFrameDescriptors("sender", "session", "transfer");
        for (var index = 0; index < 8; index++)
        {
            var bytes = Bytes(index); descriptors.Observe(Hint(index, bytes).Frame);
            var encoded = descriptors.Consume(0, NknBridgeChannel.Bulk, bytes);
            Assert.Equal(Hint(index, bytes), AttemptOfflineFrameHint.Decode(encoded!));
        }
        descriptors.VerifyComplete();
        var repair = Bytes(9);
        descriptors.Observe(Hint(1, repair).Frame with { IsRepair = true });
        Assert.Null(descriptors.Consume(0, NknBridgeChannel.Bulk, repair));
        Assert.Null(descriptors.Consume(0, NknBridgeChannel.Control, Bytes(10)));
        Assert.Throws<IOException>(() => descriptors.Consume(0, NknBridgeChannel.Bulk, Bytes(1)));
    }

    [Fact]
    public void MetadataFailureIsRetainedWithoutThrowingIntoProductionObserver()
    {
        var descriptors = new AttemptOfflineFrameDescriptors("sender", "session", "transfer");
        var frame = Hint(1, Bytes(1)).Frame;
        descriptors.Observe(frame); descriptors.Observe(frame);
        Assert.Throws<IOException>(descriptors.Check);
        var missing = new AttemptOfflineFrameDescriptors("sender", "session", "transfer");
        Assert.Throws<IOException>(missing.VerifyComplete);
    }

    [Fact]
    [Trait("RequiredConfiguration", "Debug")]
    public void ProfileRestoresPriorEnvironmentWithoutEnablingDeveloperMode()
    {
        var previous = Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable);
        var developer = Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, "2");
            using (new AttemptSingleSegmentProfile())
            {
                Assert.Equal("1", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
                Assert.Equal(developer, Environment.GetEnvironmentVariable(ReleaseOverridePolicy.UnsafeDeveloperModeEnvVar));
            }
            Assert.Equal("2", Environment.GetEnvironmentVariable(AttemptSingleSegmentProfile.Variable));
        }
        finally { Environment.SetEnvironmentVariable(AttemptSingleSegmentProfile.Variable, previous); }
    }
}
