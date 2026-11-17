using System;
using System.Diagnostics;

namespace NLink.Tools
{
    public sealed partial class LocalGuiWriterRequest
    {
        public sealed partial class FrontierFinalizerBinding
        {
            private bool parentCompletionAttempted;
            public bool TryCompleteParentHandoff()
            {
                lock (sourceSync)
                {
                    if (!IsCurrent) return false;
                    bool success = false; LocalFrontierParentCommitChannel channel = null;
                    try
                    {
                        if (parentCompletionAttempted) return false; parentCompletionAttempted = true;
                        if (!OwnsParentSubmission(parentSubmission) || !parentSubmission.ParentAcknowledgmentConfirmed) return false;
                        var adoption = files.GetOriginalAdoption(); var origin = LedgerOrigin;
                        if (adoption == null || origin == null || !origin.IsCurrent) return false;
                        var capture = parentPreparation.Capture;
                        var outputs = new[] { capture.Retained, capture.Writer, capture.Semantic };
                        var hashes = new string[3]; var identities = new string[3]; var bytes = new long[3]; var lines = new long[3];
                        for (int i = 0; i < 3; i++) { hashes[i] = outputs[i].Sha256; identities[i] = outputs[i].FileIdentity; bytes[i] = outputs[i].ByteCount; lines[i] = outputs[i].LineCount; }
                        var writers = capture.Validation.Writers; var ids = new string[writers.Count]; var last = new long[writers.Count];
                        for (int i = 0; i < writers.Count; i++) { ids[i] = writers[i].WriterId; last[i] = writers[i].LastSequence; }
                        string ledger = origin.LedgerId, path = origin.SourcePath; long count = capture.SourceLines, bookmark = capture.Bookmark;
                        var fileChannel = parentSubmission.Channel;
                        if (!adoption.TryRunRecoveryRead(() =>
                        {
                            if (!adoption.IsSourceVerified || !parentPreparation.Files.IsCurrent) return false;
                            channel = LocalFrontierParentCommitChannel.Open(fileChannel.Endpoint, fileChannel.Facts.Acknowledgment.BindingHash);
                            return channel != null && adoption.IsSourceVerified && parentPreparation.Files.IsCurrent && channel.Arm();
                        }) || !OwnsParentSubmission(parentSubmission)) return false;
                        // Legacy completion retires original GUI resources. The
                        // armed escrow remains private until successful exit/take.
                        var clock = Stopwatch.StartNew();
                        if (!finalizer.Complete(ledger, path, count, bookmark, hashes, identities, bytes, lines, ids, last)) return false;
                        int length = finalizer.view.ReadInt32(52);
                        if (length < 1 || length > MaximumCompletionBytes || finalizer.view.ReadInt32(48) != 2) return false;
                        var body = new byte[length]; if (finalizer.view.ReadArray(HeaderBytes + RequestBytes, body, 0, length) != length) return false;
                        success = clock.ElapsedMilliseconds <= MaximumCompletionMilliseconds && channel.Publish(body) && clock.ElapsedMilliseconds <= MaximumCompletionMilliseconds;
                        return success;
                    }
                    catch { return false; }
                    finally
                    {
                        if (!success) { Dispose(); if (channel != null) channel.Dispose(); }
                        else channel.Release();
                    }
                }
            }
        }
    }
}
