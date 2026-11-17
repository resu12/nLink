using System;
using System.IO;
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Tools
{
    public sealed partial class LocalRetainedSourceRequest
    {
        public sealed partial class FrontierPrimaryOutput
        {
            private readonly Encoding[] outputEncodings;

            // Reproduce ReadToEnd + Set-Content -Encoding UTF8, not the raw
            // capture hash. The host determines BOM behavior, never a caller flag.
            private string ReportHash(int index, Action check)
            {
                var output = capture.RecoveryOutputs[index];
                using (var input = new TerminalOutputStream(output, check))
                using (var reader = new StreamReader(input, outputEncodings[index], true, 32768))
                using (var sha = SHA256.Create())
                using (var hashStream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
                {
                    using (var writer = new StreamWriter(hashStream, new UTF8Encoding(typeof(PSObject).Assembly.GetName().Version.Major < 6), 32768, true))
                    {
                        var chars = new char[32768]; int count;
                        while ((count = reader.Read(chars, 0, chars.Length)) != 0) { check(); writer.Write(chars, 0, count); }
                        if (!output.ReachedEnd(input)) throw new InvalidOperationException("frontier_terminal_output_eof");
                        writer.Write(Environment.NewLine);
                    }
                    hashStream.FlushFinalBlock(); check();
                    return LocalFrontierDiagnosticPacket.Hex(sha.Hash);
                }
            }

            private sealed class TerminalOutputStream : Stream
            {
                private readonly LocalGuiWriterRequest.FrontierRecoveryOutput output;
                private readonly Action check;
                internal TerminalOutputStream(LocalGuiWriterRequest.FrontierRecoveryOutput output, Action check)
                { this.output = output; this.check = check; check(); if (!output.TryOpen(this)) throw new InvalidOperationException("frontier_terminal_output_open"); }
                public override int Read(byte[] buffer, int offset, int count)
                { check(); int read; if (!output.TryRead(this, buffer, offset, count, out read)) throw new IOException("frontier_terminal_output_read"); check(); return read; }
                public override bool CanRead { get { return true; } }
                public override bool CanSeek { get { return false; } }
                public override bool CanWrite { get { return false; } }
                public override long Length { get { return output.Length; } }
                public override long Position { get { return output.Position(this); } set { throw new NotSupportedException(); } }
                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
                public override void SetLength(long value) { throw new NotSupportedException(); }
                public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            }
        }
    }
}
