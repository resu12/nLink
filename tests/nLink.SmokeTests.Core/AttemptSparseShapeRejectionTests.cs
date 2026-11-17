using System.Collections;
using System.Reflection;
using System.Text;
using NLink.Core.FileTransfer;
using NLink.Tests;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class AttemptSparseShapeRejectionTests
{
    private const string Failure = "offline_schedule_frontier_advanced_before_sparse_shape";

    [Fact]
    public void RejectionCapturesLockedScalarsAndEmitsOnceAfterTheRead()
    {
        var context = new ContextShape(12477, 12479, new BitArray(12484));
        context.Pending.UnionWith([12477, 12478, 12479]);
        var observation = new ObservationShape(false, "Unobserved");
        string? rejection = null;
        var gate = new object();
        IOException error;
        lock (gate)
        {
            error = Assert.Throws<IOException>(() =>
                AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, observation, out rejection));
            Assert.Equal(Failure, error.Message);
            Assert.Contains(nameof(AttemptOfflineLatePublicationProgress.MatchesSparseShape), error.StackTrace!);
            Assert.Equal(12477, context.NextChunkIndex); Assert.Equal(12479, context.PullHighestReceivedChunkIndex);
            Assert.Equal(3, context.Pending.Count); Assert.False(context.ReceiverSparseChunksWritten![12477]);
            Assert.False(observation.IsClosed); Assert.Equal("Unobserved", observation.Status);
        }
        // Later mutations cannot change the scalar snapshot already captured.
        context.NextChunkIndex = 12480; context.Pending.Clear(); context.ReceiverSparseChunksWritten![12477] = true;
        using var output = new StringWriter();
        AttemptOfflineLatePublicationProgress.WriteSparseShapeRejection(ref rejection, output);
        AttemptOfflineLatePublicationProgress.WriteSparseShapeRejection(ref rejection, output);
        Assert.Null(rejection);
        var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("carrier-frontier-sparse-shape-rejected; next=12477; highest=12479; snapshot=available; " +
            "pending_count=3; next_pending=1; written_length=12484; next_written=0; observation_present=1; " +
            "observation_closed=0; observation_status=Unobserved; source_bound=0; repair_bound=0", line);
        Assert.Equal(Failure, error.Message);
    }

    [Theory]
    [InlineData(12023, 12025, 3)]
    [InlineData(12023, 12025, 1)]
    [InlineData(0, 0, 1)] [InlineData(1, 2, 2)]
    [InlineData(12475, 12475, 1)] [InlineData(12475, 12476, 2)] [InlineData(12475, 12483, 9)]
    public void PendingCurrentPrefixIsOnlyNotReadyAndRemovingItRestoresRejection(int next, int highest, int pendingCount)
    {
        var context = new ContextShape(next, highest, new BitArray(12484));
        context.Pending.UnionWith(Enumerable.Range(next, pendingCount));
        Assert.False(AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, null, out var rejection));
        Assert.Null(rejection); Assert.Equal(next, context.NextChunkIndex);
        Assert.Equal(highest, context.PullHighestReceivedChunkIndex); Assert.Equal(pendingCount, context.Pending.Count);
        Assert.False(context.ReceiverSparseChunksWritten![next]);
        using var output = new StringWriter();
        AttemptOfflineLatePublicationProgress.WriteSparseShapeRejection(ref rejection, output);
        Assert.Equal("", output.ToString());

        context.Pending.Remove(next);
        Assert.Equal(Failure, Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, null, out rejection)).Message);
        Assert.Contains("next_pending=0;", rejection!);
    }

    [Theory]
    [InlineData(true, 12023, 12025, 12484, false, false)]
    [InlineData(true, 12023, 12025, 12484, true, true)]
    [InlineData(true, 12023, 12025, -1, false, true)]
    [InlineData(true, 12023, 12025, 0, false, true)]
    [InlineData(true, 12023, 12025, 12483, false, true)]
    [InlineData(true, 12023, 12025, 12485, false, true)]
    [InlineData(true, -1, 2, 12484, false, true)]
    [InlineData(true, int.MinValue, 2, 12484, false, true)]
    [InlineData(true, 12023, 12021, 12484, false, true)]
    [InlineData(true, 0, -2, 12484, false, true)]
    [InlineData(true, 12023, 12484, 12484, false, true)]
    [InlineData(true, 12023, int.MaxValue, 12484, false, true)]
    [InlineData(true, 12477, 12479, 12484, false, true)]
    [InlineData(true, 12484, 12484, 12484, false, true)]
    [InlineData(false, 12023, 12025, 12484, false, true)]
    public void PendingEvidenceCannotBypassSparseShapeRejection(bool canonical, int next, int highest,
        int length, bool written, bool pendingNext)
    {
        var context = new ContextShape(next, highest, length < 0 ? null : new BitArray(length, written));
        context.Pending.Add(next + 1);
        if (pendingNext) context.Pending.Add(next);
        var pendingCount = context.Pending.Count;
        string? rejection = null;
        Assert.Equal(Failure, Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(canonical, context, null, out rejection)).Message);
        Assert.Equal(next, context.NextChunkIndex); Assert.Equal(pendingCount, context.Pending.Count);
        if (canonical) Assert.NotNull(rejection);
        else Assert.Null(rejection);
    }

    [Fact]
    public void UnavailablePendingEvidenceCannotRelaxOrReplaceTheOriginalRejection()
    {
        var context = new ContextShape(12023, 12025, new BitArray(12484)) { FailDiagnosticRead = true };
        string? rejection = null;
        var error = Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, null, out rejection));
        Assert.Equal(Failure, error.Message); Assert.Null(error.InnerException);
        Assert.Contains(nameof(AttemptOfflineLatePublicationProgress.MatchesSparseShape), error.StackTrace!);
        Assert.Contains("snapshot=unavailable", rejection!);
    }

    [Theory]
    [InlineData(-1, 8, -1)] [InlineData(int.MinValue, 8, -1)] [InlineData(int.MaxValue, 8, -1)]
    [InlineData(8, 8, -1)] [InlineData(0, 0, -1)] [InlineData(2, -1, -1)] [InlineData(2, 8, 1)]
    public void MissingOrOutOfBoundsWrittenBitIsUnknownNotFalse(int next, int length, int expected)
    {
        var context = new ContextShape(next, 10, length < 0 ? null : new BitArray(length, true));
        string? rejection = null;
        Assert.Equal(Failure, Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, null, out rejection)).Message);
        Assert.Contains($"written_length={length}; next_written={expected};", rejection!);
        Assert.Contains("observation_present=0; observation_closed=-1; observation_status=none; source_bound=0; repair_bound=0", rejection!);
    }

    [Theory]
    [InlineData(true, 12476, 12483, true)] [InlineData(true, 12476, 12482, false)]
    [InlineData(true, 12476, 12484, false)] [InlineData(true, 12020, 12019, false)]
    [InlineData(true, 0, -1, false)] [InlineData(false, 0, 7, true)] [InlineData(false, 0, 6, false)]
    public void NonRejectionsNeverReadDiagnosticState(bool canonical, int next, int highest, bool expected)
    {
        var context = new ContextShape(next, highest, null) { FailDiagnosticRead = true };
        Assert.Equal(expected, AttemptOfflineLatePublicationProgress.ReadSparseShape(canonical, context, null, out var rejection));
        Assert.Null(rejection);
        using var output = new StringWriter();
        AttemptOfflineLatePublicationProgress.WriteSparseShapeRejection(ref rejection, output);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void SmallProfileRejectionRemainsUninstrumented()
    {
        var context = new ContextShape(1, 0, null) { FailDiagnosticRead = true };
        string? rejection = null;
        Assert.Equal(Failure, Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(false, context, null, out rejection)).Message);
        Assert.Null(rejection);
    }

    [Fact]
    public void DiagnosticReadFailureCannotReplaceTheOriginalRejection()
    {
        var context = new ContextShape(12020, 12022, null) { FailDiagnosticRead = true };
        string? rejection = null;
        var error = Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, null, out rejection));
        Assert.Equal(Failure, error.Message); Assert.Null(error.InnerException);
        Assert.Contains(nameof(AttemptOfflineLatePublicationProgress.MatchesSparseShape), error.StackTrace!);
        Assert.Equal("carrier-frontier-sparse-shape-rejected; next=12020; highest=12022; snapshot=unavailable", rejection);
    }

    [Fact]
    public void StatusAndOutputAreBoundedSingleLineAndBindingsAreOnlyBooleans()
    {
        var context = new ContextShape(12020, int.MaxValue, new BitArray(12484));
        context.Pending.UnionWith(Enumerable.Range(0, 100000));
        var observation = new ObservationShape(true, "Bad\r\n;=" + new string('x', 100000), new object(), new object());
        string? rejection = null;
        Assert.Equal(Failure, Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, observation, out rejection)).Message);
        Assert.InRange(rejection!.Length, 1, 512);
        Assert.DoesNotContain('\r', rejection); Assert.DoesNotContain('\n', rejection);
        Assert.Contains("pending_count=100000; next_pending=1;", rejection);
        Assert.Contains("observation_closed=1; observation_status=Bad____" + new string('x', 57) + "; source_bound=1; repair_bound=1", rejection);
        Assert.Equal(100000, context.Pending.Count);
    }

    [Fact]
    public void FailedOutputIsNotRetriedAndCannotReplaceTheRejection()
    {
        var context = new ContextShape(12020, 12022, null);
        string? rejection = null;
        var original = Assert.Throws<IOException>(() =>
            AttemptOfflineLatePublicationProgress.ReadSparseShape(true, context, null, out rejection));
        using var output = new FailingWriter();
        AttemptOfflineLatePublicationProgress.WriteSparseShapeRejection(ref rejection, output);
        AttemptOfflineLatePublicationProgress.WriteSparseShapeRejection(ref rejection, output);
        Assert.Equal(1, output.Calls); Assert.Null(rejection); Assert.Equal(Failure, original.Message);
    }

    [Fact]
    public void DiagnosticMemberNamesMatchTheRealCoreTypes()
    {
        var context = typeof(SessionFileTransferService).GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        foreach (var name in new[] { "NextChunkIndex", "PullHighestReceivedChunkIndex" })
            Assert.Equal(typeof(int), context.GetProperty(name)!.PropertyType);
        Assert.Equal(typeof(HashSet<int>), context.GetProperty("ReceiverSparseChunksPendingWrite")!.PropertyType);
        Assert.Equal(typeof(BitArray), context.GetProperty("ReceiverSparseChunksWritten")!.PropertyType);
        var observation = context.GetProperty("FrontierBoundaryObservation")!.PropertyType;
        Assert.Equal(typeof(bool), observation.GetProperty("IsClosed")!.PropertyType);
        Assert.Equal(typeof(string), observation.GetProperty("Status")!.PropertyType);
        foreach (var name in new[] { "source", "capturedRepair" })
            Assert.NotNull(observation.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
    }

    // Synthetic value shapes, not native witnesses or coordinator authority.
    private sealed class ContextShape(int next, int highest, BitArray? written)
    {
        public int NextChunkIndex { get; set; } = next;
        public int PullHighestReceivedChunkIndex { get; } = highest;
        public HashSet<int> ReceiverSparseChunksPendingWrite => FailDiagnosticRead ? throw new IOException("diagnostic_read_failed") : Pending;
        public BitArray? ReceiverSparseChunksWritten { get; } = written;
        internal HashSet<int> Pending { get; } = [];
        internal bool FailDiagnosticRead { get; init; }
    }

    private sealed class ObservationShape(bool closed, string status, object? sourceValue = null, object? repairValue = null)
    {
        private readonly object? source = sourceValue, capturedRepair = repairValue;
        public bool IsClosed => closed;
        public string Status => status;
    }

    private sealed class FailingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        internal int Calls { get; private set; }
        public override void WriteLine(string? value) { Calls++; throw new IOException("diagnostic_output_failed"); }
    }
}
