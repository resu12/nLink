namespace NLink.SmokeTests;

// Source contracts deliberately avoid invoking Media Foundation or a capture device.
[Trait("Area", "ScreenShare")]
public sealed class MediaFoundationEncoderStartupContractTests
{
    [Fact]
    public void PersistentStartup_UsesCpuMemoryFirst_WithoutSinkWriterProbeOrSharedCache()
    {
        var startup = ReadSection(
            "private EncoderConfiguration? TryCreatePersistentTransformSession(",
            "private EncoderConfiguration CreateSinkWriterFallbackConfiguration(");

        Assert.Contains("EnumerateStrategyAttempts(RawInputBufferStrategy.CpuMemoryBufferNv12)", startup);
        Assert.Contains("TryCreateSoftwareTransform(out encoderTransform)", startup);
        Assert.Contains("ConfigureTransformCore(", startup);
        Assert.DoesNotContain("EnsureInputBufferStrategy(", startup);
        Assert.DoesNotContain("ProbeInputBufferStrategy(", startup);
        Assert.DoesNotContain("InputBufferProbeSync", startup);
        Assert.DoesNotContain("PromoteWorkingInputBufferStrategy(", startup);
        Assert.DoesNotContain("inputBufferProbeCompleted", startup);
        Assert.Contains("catch (Exception ex)", startup);
        Assert.Contains("ReleaseComObject(encoderTransform);", startup);
        Assert.Contains("return null;", startup);
    }

    [Fact]
    public void PersistentEncode_KeepsInputRejectionAndEmptyOutputChecks_AndFailureFallback()
    {
        var encode = ReadSection(
            "private EncoderEncodeResult EncodeSingleFrameToPersistentTransform(",
            "private void ResetEncoderState()");

        Assert.Contains("session.EncoderTransform.ProcessInput(0, inputSample, 0)", encode);
        Assert.Contains("Marshal.ThrowExceptionForHR(hr);", encode);
        Assert.Contains("!TryProcessSingleOutput(", encode);
        Assert.Contains("encodedBytes.Length == 0", encode);
        Assert.Contains("new EncoderEncodeResult(Array.Empty<byte>()", encode);
        Assert.Contains("catch (Exception ex) when (IsTransformNeedMoreInputException(ex))", encode);
        Assert.Contains("ReleaseComObject(inputSample);", encode);

        var core = ReadSection("private WindowsH264EncodedFrame? EncodeCore(", "private byte[] PrepareNv12Bytes(");
        Assert.Contains("catch (Exception ex)", core);
        Assert.Contains("SwitchToSinkWriterFallback(encodeProfile, options, effectiveForceKeyFrame);", core);
        Assert.Contains("EncodeSingleFrameToSinkWriter(", core);
    }

    [Fact]
    public void SinkWriterFallback_StillRequiresProbeAndFinalization()
    {
        var fallback = ReadSection(
            "private EncoderEncodeResult EncodeSingleFrameToSinkWriter(",
            "private static SinkWriterContext CreateSinkWriter(");

        Assert.Contains("EnsureInputBufferStrategy(", fallback);
        Assert.Contains("throw new RawInputBufferStrategyUnavailableException(", fallback);
        Assert.Contains("Marshal.ThrowExceptionForHR(sinkWriter.Finalize_());", fallback);
    }

    private static string ReadSection(string start, string end)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(root,
            "src", "nLink.App", "Screenshare", "Capture", "Windows", "MediaFoundationH264FrameEncoder.cs"));
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing section: {start}");
        var endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing section end: {end}");
        return source[startIndex..endIndex];
    }
}
