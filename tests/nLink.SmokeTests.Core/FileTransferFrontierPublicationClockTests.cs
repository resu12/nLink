using System.Collections.Immutable;
using NLink.Core.FileTransfer;
using Stamp = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryStamp;
using Timing = NLink.Core.FileTransfer.FileTransferCoordinator.FrontierPublicationBoundaryTiming;

namespace NLink.SmokeTests;

public sealed class FileTransferFrontierPublicationClockTests
{
    private static readonly long Epoch = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero).Ticks;
    private static readonly long ProcessCreated = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero).ToFileTime();
    private static readonly Guid Clock = Guid.Parse("c39a5e28-a2f4-4ddd-ae70-6c560511bbd9");
    private static readonly Guid Observation = Guid.Parse("d19b1313-1f9e-466c-980e-57603a5e1991");

    public static IEnumerable<object[]> TimingCases()
    {
        foreach (var frequency in new[] { 1000L, 3001L, 10_000_000L })
        {
            foreach (var (name, expected) in new[]
            {
                ("before", "PublishedBeforeOriginalDeadline"), ("late", "PublishedWithinObservationBound"),
                ("commit_at_D", "PreDeadlineCommitUnproven"), ("commit_after_D", "PreDeadlineCommitUnproven"),
                ("completion_at_P", "PublicationOutsideObservationBound"), ("frame_at_P", "PublicationOutsideObservationBound"),
                ("binding_at_P", "PublicationOutsideObservationBound"), ("later_commit_at_P", "PublicationOutsideObservationBound"),
                ("D_ambiguity", "EvidenceIncomplete"), ("UTC_before_counter_after_D", "EvidenceIncomplete"),
                ("UTC_after_counter_before_D", "EvidenceIncomplete"), ("counter_P_guard", "EvidenceIncomplete"),
                ("counter_commit_guard", "EvidenceIncomplete"), ("UTC_regression", "EvidenceIncomplete"),
                ("counter_regression", "EvidenceIncomplete"), ("foreign_clock", "EvidenceIncomplete"),
                ("frequency_change", "EvidenceIncomplete"), ("sequence_replay", "EvidenceIncomplete"),
                ("bracket_reversed", "EvidenceIncomplete"), ("wide_bracket_straddles_D", "EvidenceIncomplete"),
                ("suspend_sized_gap", "PublicationOutsideObservationBound"), ("UTC_addition_overflow", "EvidenceIncomplete"),
                ("near_int64_counter_limit", "PublishedBeforeOriginalDeadline"), ("zero_frequency", "EvidenceIncomplete"),
                ("recent_elapsed_unproven", "EvidenceIncomplete"), ("recent_UTC_unproven", "EvidenceIncomplete"),
            }) { yield return [name, frequency, expected]; }
        }
    }

    [Theory]
    [MemberData(nameof(TimingCases))]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_ImplementsReviewedPredicates(string name, long frequency, string expected)
    {
        var input = Create(frequency);
        input = name switch
        {
            "before" => input,
            "late" => input with { Completion = At(2000, frequency, 6) },
            "commit_at_D" or "commit_after_D" => input with
            {
                Commits = [At(name == "commit_at_D" ? 1850 : 1851, frequency, 3)],
                PublicationDeadline = Utc(name == "commit_at_D" ? 2850 : 2851),
                Frame = At(1900, frequency, 4), Binding = At(1950, frequency, 5), Completion = At(2000, frequency, 6),
            },
            "completion_at_P" => input with { Completion = At(2350, frequency, 6) },
            "frame_at_P" => input with { Frame = At(2350, frequency, 4), Binding = At(2400, frequency, 5), Completion = At(2450, frequency, 6) },
            "binding_at_P" => input with { Binding = At(2350, frequency, 5), Completion = At(2400, frequency, 6) },
            "later_commit_at_P" => input with
            {
                Commits = [input.Commits[0], At(2350, frequency, 4)], Frame = At(2400, frequency, 5),
                Binding = At(2450, frequency, 6), Completion = At(2500, frequency, 7),
            },
            "D_ambiguity" => input with { Completion = At(1850, frequency, 6) },
            "UTC_before_counter_after_D" => input with { Completion = At(1800, frequency, 6, 1950) },
            "UTC_after_counter_before_D" => input with { Completion = At(1950, frequency, 6, 1750) },
            "counter_P_guard" => input with { Completion = At(2200, frequency, 6, 2500) },
            "counter_commit_guard" => input with
            {
                Commits = [At(1350, frequency, 3, 1850)], Frame = At(1500, frequency, 4, 1900),
                Binding = At(1600, frequency, 5, 1950), Completion = At(1750, frequency, 6, 2000),
            },
            "UTC_regression" => input with { Completion = At(1599, frequency, 6, 1750) },
            "counter_regression" => input with { Completion = At(1750, frequency, 6, 1599) },
            "foreign_clock" => input with { Completion = input.Completion with { ClockInstanceId = Guid.NewGuid() } },
            "frequency_change" => input with { Completion = input.Completion with { Frequency = frequency + 1 } },
            "sequence_replay" => input with { Completion = input.Completion with { CaptureSequence = input.Binding.CaptureSequence } },
            "bracket_reversed" => input with { Completion = input.Completion with { CounterAfter = input.Completion.CounterBefore - 1 } },
            "wide_bracket_straddles_D" => input with { Completion = input.Completion with { CounterAfter = Ticks(1950, frequency) } },
            "suspend_sized_gap" => input with { Completion = At(61_750, frequency, 6) },
            "UTC_addition_overflow" => ShiftUtc(input, DateTimeOffset.MaxValue.Ticks - input.Completion.UtcTicks),
            "near_int64_counter_limit" => Map(input, stamp => stamp with
            {
                CounterBefore = stamp.CounterBefore + (long.MaxValue - input.Completion.CounterAfter),
                CounterAfter = stamp.CounterAfter + (long.MaxValue - input.Completion.CounterAfter),
            }),
            "zero_frequency" => Map(input, stamp => stamp with { Frequency = 0 }),
            "recent_elapsed_unproven" => input with { Stall = At(1100, frequency, 2, 999) },
            "recent_UTC_unproven" => input with { Stall = At(999, frequency, 2, 1100), StallDeadline = Utc(1749) },
            _ => throw new InvalidOperationException(name),
        };
        Assert.Equal(expected, Evaluate(input).Outcome);
        Assert.False(Evaluate(input).LiveProofEligible);
    }

    [Theory]
    [InlineData(2248, 1849, "PublishedBeforeOriginalDeadline")]
    [InlineData(2249, 1849, "EvidenceIncomplete")]
    [InlineData(2252, 1851, "EvidenceIncomplete")]
    [InlineData(2253, 1851, "PublishedWithinObservationBound")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_PreservesFractionalStallComparisons(long delta, long utc, string expected)
    {
        var input = Create(3001);
        var counter = input.Stall.CounterAfter + delta;
        input = input with { Completion = At(utc, 3001, 6) with { CounterBefore = counter, CounterAfter = counter } };
        Assert.Equal(expected, Evaluate(input).Outcome);
    }

    [Theory]
    [InlineData(2998, "PublishedWithinObservationBound")]
    [InlineData(2999, "EvidenceIncomplete")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_PreservesStrictPublicationCounterBound(long delta, string expected)
    {
        var input = Create(3001);
        var counter = input.Commits[0].CounterAfter + delta;
        input = input with { Completion = At(2349, 3001, 6) with { CounterBefore = counter, CounterAfter = counter } };
        Assert.Equal(expected, Evaluate(input).Outcome);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("creation")]
    [InlineData("observation")]
    [InlineData("empty-clock")]
    [InlineData("empty-observation")]
    [InlineData("zero-pid")]
    [InlineData("zero-creation")]
    [InlineData("invalid-creation")]
    [InlineData("zero-sequence")]
    [InlineData("negative-counter")]
    [InlineData("negative-utc")]
    [InlineData("utc-range")]
    [InlineData("negative-frequency")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_RejectsInvalidOrForeignStampsBeforeTimeout(string field)
    {
        var input = Create(1000) with { Completion = At(5000, 1000, 6) };
        var c = input.Completion;
        c = field switch
        {
            "pid" => c with { ProcessId = c.ProcessId + 1 },
            "creation" => c with { ProcessCreationFileTime = c.ProcessCreationFileTime + 1 },
            "observation" => c with { ObservationId = Guid.NewGuid() },
            "empty-clock" => c with { ClockInstanceId = Guid.Empty },
            "empty-observation" => c with { ObservationId = Guid.Empty },
            "zero-pid" => c with { ProcessId = 0 },
            "zero-creation" => c with { ProcessCreationFileTime = 0 },
            "invalid-creation" => c with { ProcessCreationFileTime = long.MaxValue },
            "zero-sequence" => c with { CaptureSequence = 0 },
            "negative-counter" => c with { CounterBefore = -1 },
            "negative-utc" => c with { UtcTicks = -1 },
            "utc-range" => c with { UtcTicks = DateTimeOffset.MaxValue.Ticks + 1 },
            "negative-frequency" => c with { Frequency = -1 },
            _ => throw new InvalidOperationException(field),
        };
        Assert.Equal("EvidenceIncomplete", Evaluate(input with { Completion = c }).Outcome);
    }

    [Theory]
    [InlineData("recent")]
    [InlineData("stall")]
    [InlineData("publication")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_CannotMoveOriginalDeadlines(string which)
    {
        var input = Create(1000);
        input = which switch
        {
            "recent" => input with { RecentDeadline = input.RecentDeadline + 1 },
            "stall" => input with { StallDeadline = input.StallDeadline + 1 },
            _ => input with { Commits = [input.Commits[0], At(1400, 1000, 4)],
                Frame = At(1500, 1000, 5), Binding = At(1600, 1000, 6), Completion = At(1750, 1000, 7), PublicationDeadline = Utc(2400) },
        };
        Assert.Equal("EvidenceIncomplete", Evaluate(input).Outcome);
        Assert.Equal("deadline_mismatch", Evaluate(input).Reason);
    }

    [Theory]
    [InlineData(0, "EvidenceIncomplete")]
    [InlineData(59, "PublishedBeforeOriginalDeadline")]
    [InlineData(60, "EvidenceIncomplete")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_BoundsInputsWithoutChangingThem(int count, string expected)
    {
        var input = Create(1000) with
        {
            Commits = Enumerable.Range(0, count).Select(i => At(1350, 1000, i + 3)).ToImmutableArray(),
            Frame = At(1500, 1000, count + 3), Binding = At(1600, 1000, count + 4), Completion = At(1750, 1000, count + 5),
        };
        var original = input.Commits.ToArray();
        var first = Evaluate(input);
        Assert.Equal(expected, first.Outcome);
        Assert.Equal(first, Evaluate(input));
        Assert.Equal(original, input.Commits.ToArray());
        Assert.False(first.LiveProofEligible);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_WideArithmeticAndIntermediateSamplesStayStrict()
    {
        Assert.Equal("PublishedBeforeOriginalDeadline", Evaluate(Create(long.MaxValue / 4)).Outcome);
        var input = Create(1000);
        var later = At(1400, 1000, 4) with { ClockInstanceId = Guid.NewGuid() };
        input = input with { Commits = [input.Commits[0], later], Frame = At(1500, 1000, 5),
            Binding = At(1600, 1000, 6), Completion = At(1750, 1000, 7) };
        Assert.Equal("EvidenceIncomplete", Evaluate(input).Outcome);
    }

    [Theory]
    [InlineData("recent")]
    [InlineData("stall")]
    [InlineData("commit")]
    [InlineData("frame")]
    [InlineData("binding")]
    [InlineData("completion")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_RequiresEveryBoundary(string boundary)
    {
        var input = Create(1000);
        input = boundary switch
        {
            "recent" => input with { Recent = default },
            "stall" => input with { Stall = default },
            "commit" => input with { Commits = [default] },
            "frame" => input with { Frame = default },
            "binding" => input with { Binding = default },
            _ => input with { Completion = default },
        };
        Assert.Equal("EvidenceIncomplete", Evaluate(input).Outcome);
    }

    [Theory]
    [InlineData(1851, "EvidenceIncomplete")]
    [InlineData(1852, "PublishedWithinObservationBound")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_ExactUtcDeadlineNeedsConservativeElapsedAgreement(long counterMs, string expected)
    {
        var input = Create(1000) with { Completion = At(1850, 1000, 6, counterMs) };
        Assert.Equal(expected, Evaluate(input).Outcome);
    }

    [Theory]
    [InlineData(1001, "EvidenceIncomplete")]
    [InlineData(1002, "PublishedBeforeOriginalDeadline")]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_RecentEqualityNeedsConservativeElapsedAgreement(long counterMs, string expected)
    {
        var input = Create(1000) with { Stall = At(1000, 1000, 2, counterMs), StallDeadline = Utc(1750),
            Completion = At(1700, 1000, 6) };
        Assert.Equal(expected, Evaluate(input).Outcome);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationClock")]
    public void BoundaryTiming_AcceptsBoundedBracketsAndSequenceGapsWithoutRoundingUtc()
    {
        var input = Map(Create(1000), stamp => stamp with
        {
            CounterAfter = stamp.CounterAfter + 1,
            CaptureSequence = stamp.CaptureSequence * 100,
        });
        Assert.Equal("PublishedBeforeOriginalDeadline", Evaluate(input).Outcome);
        input = input with { Completion = At(2347, 1000, 600) with { UtcTicks = input.PublicationDeadline - 1 } };
        Assert.Equal("PublishedWithinObservationBound", Evaluate(input).Outcome);
        input = input with { Completion = input.Completion with { UtcTicks = input.PublicationDeadline } };
        Assert.Equal("PublicationOutsideObservationBound", Evaluate(input).Outcome);
    }

    private sealed record Input(Stamp Recent, long RecentDeadline, Stamp Stall, long StallDeadline,
        ImmutableArray<Stamp> Commits, long PublicationDeadline, Stamp Frame, Stamp Binding, Stamp Completion);

    private static Input Create(long frequency) => new(At(0, frequency, 1), Utc(1000), At(1100, frequency, 2), Utc(1850),
        [At(1350, frequency, 3)], Utc(2350), At(1500, frequency, 4), At(1600, frequency, 5), At(1750, frequency, 6));
    private static long Utc(long milliseconds) => Epoch + milliseconds * TimeSpan.TicksPerMillisecond;
    private static long Ticks(long milliseconds, long frequency) => checked((long)((Int128)milliseconds * frequency / 1000));
    private static Stamp At(long milliseconds, long frequency, long sequence, long? counterMilliseconds = null)
        => new(123, ProcessCreated, Clock, Observation, sequence, frequency, Utc(milliseconds),
            Ticks(counterMilliseconds ?? milliseconds, frequency), Ticks(counterMilliseconds ?? milliseconds, frequency));
    private static Input Map(Input input, Func<Stamp, Stamp> map) => input with
    {
        Recent = map(input.Recent), Stall = map(input.Stall), Commits = input.Commits.Select(map).ToImmutableArray(),
        Frame = map(input.Frame), Binding = map(input.Binding), Completion = map(input.Completion),
    };
    private static Input ShiftUtc(Input input, long delta)
        => Map(input, stamp => stamp with { UtcTicks = stamp.UtcTicks + delta }) with
        { RecentDeadline = input.RecentDeadline + delta, StallDeadline = input.StallDeadline + delta,
            PublicationDeadline = input.PublicationDeadline + delta };
    private static FileTransferCoordinator.FrontierPublicationTimingResult Evaluate(Input input)
        => Timing.Evaluate(input.Recent, input.RecentDeadline, input.Stall, input.StallDeadline, input.Commits.AsSpan(),
            input.PublicationDeadline, input.Frame, input.Binding, input.Completion);
}
