using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FrontierPublicationWriter")]
    public async Task FrontierPublicationWriter_ExactEmittedDigestBindsReceiptAndClosingWriterOrder()
    {
        var fixture = await RunFrontierPublicationFixtureAsync("newer_token", true);
        var producer = fixture.Producer;
        var binding = Assert.IsType<FileTransferCoordinator.FrontierPublicationWriterBinding>(producer.WriterBinding);
        Assert.Null(producer.TakeWriterBinding()); // The actual service consumed the one emission.
        var original = fixture.Lines.ToArray();
        var marker = "event=" + FileTransferCoordinator.FrontierPublicationWriterBinding.EventName + ";";
        var index = Array.FindIndex(original, line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(index >= 0);
        Assert.Single(original.Where(line => line.Contains(marker, StringComparison.Ordinal)));
        Assert.Contains(binding.ToLogMessage() + "; log_writer_id=", original[index], StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", original[index], StringComparison.Ordinal);
        var capture = Freeze(original);
        using var full = JsonDocument.Parse(capture.ExportFull());
        using var compact = JsonDocument.Parse(capture.ExportCompact());
        foreach (var root in new[] { full.RootElement, compact.RootElement })
        {
            Assert.Equal(binding.ReceiptHash, root.GetProperty("WriterBinding").GetProperty("ReceiptHash").GetString());
            Assert.Equal(index + 1, root.GetProperty("WriterBindingLocation").GetProperty("LedgerLine").GetInt32());
            Assert.Equal(original[index], root.GetProperty("WriterBindingLocation").GetProperty("RawLine").GetString());
        }
        Reject(original.Where((_, i) => i != index).ToArray());
        foreach (var field in new[] { "frontier_receipt_version", "frontier_receipt_clock", "frontier_receipt_hash",
            "frontier_completion_hash", "frontier_observation_id", "frontier_coordinator_id", "frontier_completion_id",
            "frontier_receipt_outcome", "frontier_lifecycle_end" })
        {
            var changed = original.ToArray();
            var prior = GetDelimitedLogField(changed[index], field);
            changed[index] = changed[index].Replace(field + "=" + prior, field + "=changed", StringComparison.Ordinal);
            Reject(changed);
            changed = original.ToArray();
            changed[index] += "; " + field + "=" + prior;
            Reject(changed);
        }
        var duplicate = original.ToList();
        duplicate.Insert(index + 1, original[index]);
        Assert.NotNull(Freeze(duplicate)); // Same writer sequence and bytes is one retained copy.
        Reject(Renumber(duplicate)); // A second emission is not a retained copy.
        var moved = original.ToList();
        moved.RemoveAt(index);
        moved.Insert(0, original[index]);
        Reject(Renumber(moved));
        moved.RemoveAt(0);
        moved.Add(original[index]);
        Reject(Renumber(moved));
        var alteredReceipt = producer.Receipt! with { CompletedUtc = producer.Receipt.CompletedUtc.AddTicks(-1) };
        var alteredBinding = FileTransferCoordinator.FrontierPublicationWriterBinding.Create(alteredReceipt);
        Assert.NotEqual(binding.ReceiptHash, alteredBinding.ReceiptHash);
        foreach (var view in new[] { "full", "compact" })
        {
            var candidate = System.Text.Json.Nodes.JsonNode.Parse(view == "full" ? capture.ExportFull() : capture.ExportCompact())!;
            candidate["Receipt"]!["CompletedUtc"] = alteredReceipt.CompletedUtc.ToString("O");
            candidate["WriterBinding"]!["ReceiptHash"] = alteredBinding.ReceiptHash;
            candidate["WriterBindingLocation"]!["RawLine"] = original[index].Replace(binding.ReceiptHash, alteredBinding.ReceiptHash);
            candidate["InputSha256"] = new string('0', 64);
            Assert.Equal("EvidenceIncomplete", capture.Validate(Canonical(candidate), view).Status);
        }
        Assert.Equal("Idempotent", producer.Receipt.Completion.DecisionStatus);
        Assert.False(capture.Validate(capture.ExportCompact(), "compact").LiveProofEligible);

        FileTransferCoordinator.FrontierPublicationOfflineCapture Freeze(IReadOnlyList<string> lines)
        {
            var result = FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, lines, out var reason);
            Assert.True(result is not null, reason);
            return result!;
        }
        void Reject(IReadOnlyList<string> lines) => Assert.Null(
            FileTransferCoordinator.FrontierPublicationOfflineCapture.TryFreeze(producer, lines, out _));
        string[] Renumber(IEnumerable<string> lines)
        {
            var first = long.Parse(GetDelimitedLogField(original[0], "log_writer_sequence"));
            return lines.Select((line, i) => line.Replace("log_writer_sequence=" + GetDelimitedLogField(line, "log_writer_sequence") + ";",
                "log_writer_sequence=" + (first + i) + ";", StringComparison.Ordinal)).ToArray();
        }
    }
}
