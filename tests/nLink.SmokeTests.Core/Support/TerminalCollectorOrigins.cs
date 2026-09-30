#if NLINK_TEST_TERMINAL_REQUEST
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using NLink.Tests;
using NLink.Tools.FrontierLedger;

namespace NLink.Tests
{
    public sealed partial class HeldFileNativeParent
    {
        // Pre-ACK corroboration only; do not publish the post-release descriptor
        // or claim request authority before the original release confirmation.
        internal bool MatchesPendingCollectorOrigins(NLink.Tools.TerminalCollectorOrigins value)
        {
            return !released && requestOwner == null && IsCurrent && value != null &&
                value.MatchesProducer(measurements[0]);
        }
    }
}

namespace NLink.Tools
{
    // Private metadata from the original processor read, transported only inside
    // the two existing owned file handoffs. Never a standalone capability.
    internal sealed class TerminalCollectorOrigins
    {
        private readonly string producerIdentity;
        private readonly long producerBytes, producerLines;
        private readonly int[][] origins;

        private TerminalCollectorOrigins(string identity, long bytes, long lines, int[][] origins)
        { producerIdentity = identity; producerBytes = bytes; producerLines = lines; this.origins = origins; }

        internal static void WriteOriginal(BinaryWriter writer, HeldFileNativeParent.CollectorRead collector,
            GuiSmokeLedgerStreamCapture capture)
        {
            if (!capture.IsCurrent || !collector.OutputIsCurrent || capture.SourceBytes != collector.CopiedBytes ||
                capture.Bookmark != 0 || capture.HasWriterValidationErrors)
                throw new InvalidDataException("collector_origins_capture");
            var maps = new int[3][];
            ReadOnlyCollection<int>[] original = { capture.RetainedOrigins, capture.WriterOrigins, capture.SemanticOrigins };
            int total = 0;
            for (int role = 0; role < 3; role++)
            {
                if (original[role].Count > 4096 - total) throw new InvalidDataException("collector_origins_limit");
                total += original[role].Count;
                maps[role] = new int[original[role].Count]; original[role].CopyTo(maps[role], 0);
            }
            var value = new TerminalCollectorOrigins(collector.SourceIdentity, collector.CopiedBytes, capture.SourceLines, maps);
            value.Validate(new[] { capture.Retained, capture.Writer, capture.Semantic });
            value.Write(writer, "");
        }

        internal void Write(BinaryWriter writer, string mutation)
        {
            writer.Write(0x544f5231);
            writer.Write(mutation == "collector-origin-producer" ? "foreign" : producerIdentity);
            writer.Write(producerBytes); writer.Write(producerLines);
            for (int role = 0; role < 3; role++)
            {
                writer.Write(mutation == "collector-origin-count" && role == 0 ? 4097 : origins[role].Length);
                for (int i = 0; i < origins[role].Length; i++)
                    writer.Write(mutation == "collector-origin-range" && role == 2 && i == 0 ? 0 : origins[role][i]);
            }
        }

        internal static TerminalCollectorOrigins Read(BinaryReader reader, IList<GuiSmokeLedgerCapturedOutput> outputs)
        {
            if (reader.ReadInt32() != 0x544f5231) throw new InvalidDataException("collector_origins_magic");
            string identity = reader.ReadString(); long bytes = reader.ReadInt64(), lines = reader.ReadInt64();
            int total = 0; var maps = new int[3][];
            for (int role = 0; role < 3; role++)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > 4096 - total) throw new InvalidDataException("collector_origins_limit");
                total += count; maps[role] = new int[count];
                for (int i = 0; i < count; i++) maps[role][i] = reader.ReadInt32();
            }
            var value = new TerminalCollectorOrigins(identity, bytes, lines, maps);
            value.Validate(outputs);
            if (outputs.Count != 4 || outputs[3].LineCount != outputs[2].LineCount ||
                outputs[3].ByteCount != outputs[2].ByteCount || outputs[3].Sha256 != outputs[2].Sha256)
                throw new InvalidDataException("collector_origins_semantic_copy");
            return value;
        }

        private void Validate(IList<GuiSmokeLedgerCapturedOutput> outputs)
        {
            if (String.IsNullOrEmpty(producerIdentity) || producerIdentity.Length > 128 ||
                producerBytes < 1 || producerBytes > 4 * 1024 * 1024 || producerLines < 1 || producerLines > 4096 ||
                origins[0].Length != producerLines)
                throw new InvalidDataException("collector_origins_source");
            for (int role = 0; role < 3; role++)
            {
                if (origins[role].Length != outputs[role].LineCount) throw new InvalidDataException("collector_origins_count");
                int previous = 0;
                foreach (int line in origins[role])
                {
                    if (line <= previous || line > producerLines || (role == 0 && line != previous + 1))
                        throw new InvalidDataException("collector_origins_range");
                    previous = line;
                }
            }
            foreach (int line in origins[2])
                if (Array.BinarySearch(origins[1], line) < 0) throw new InvalidDataException("collector_origins_writer_line");
        }

        internal bool MatchesProducer(HeldFileNativeParent.Measurement producer)
        { return producer != null && producer.Role == "log" && producer.Identity == producerIdentity && producer.Bytes == producerBytes; }
        internal bool MatchesLines(long lines) { return producerLines == lines; }

        internal LocalRetainedSourceRequest.CollectorLineOriginForTest Describe(int semanticLine)
        {
            if (semanticLine < 1 || semanticLine > origins[2].Length) return null;
            int producer = origins[2][semanticLine - 1];
            return new LocalRetainedSourceRequest.CollectorLineOriginForTest(semanticLine, producer,
                Array.BinarySearch(origins[0], producer) + 1, Array.BinarySearch(origins[1], producer) + 1);
        }
    }

    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class CollectorPrimaryFilesForTest
        {
            internal TerminalCollectorOrigins origins;
            // Coordinates only. The caller must separately prove original
            // primary custody; these diagnostic rows confer no witness authority.
            public CollectorLineOriginForTest DescribeLineOriginForTest(SourceReader source, int semanticLine)
            {
                return IsCurrent && owner.OwnsCompletedSource(source) && origins != null ?
                    origins.Describe(semanticLine) : null;
            }
        }

        public sealed class CollectorLineOriginForTest
        {
            public int SemanticLine { get; private set; }
            public int ProducerLine { get; private set; }
            public int RetainedLine { get; private set; }
            public int WriterLine { get; private set; }
            internal CollectorLineOriginForTest(int semantic, int producer, int retained, int writer)
            { SemanticLine = semantic; ProducerLine = producer; RetainedLine = retained; WriterLine = writer; }
        }
    }
}
#endif
