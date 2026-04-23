using DrawingBitmap = System.Drawing.Bitmap;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class WindowsH264MediaFoundationDiagnosticTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;

    public WindowsH264MediaFoundationDiagnosticTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_TryCreate_ReturnsSupportedEncoder_WhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var encoder = MediaFoundationH264FrameEncoder.TryCreate();
        if (encoder is null)
        {
            return;
        }

        Assert.True(encoder.IsSupported);
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_InputBufferProbe_RunsOnceAndRecordsSelection_WhenEncodeIsAttempted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        MediaFoundationH264FrameEncoder.ResetDebugInputBufferProbeState();
        try
        {
            await using var encoder = MediaFoundationH264FrameEncoder.TryCreate("test");
            if (encoder is null)
            {
                return;
            }

            using var bitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var frame = new WindowsRawCaptureFrame(bitmap, capturedTsUtcMs: 1000);

            try
            {
                await encoder.EncodeAsync(
                    frame,
                    new WindowsH264EncodeOptions(
                        TargetFramesPerSecond: 5,
                        TuningLevel: ScreenShareTransportTuningLevel.Normal,
                        ForceKeyFrame: true,
                        StreamEpoch: 21),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
            {
            }

            Assert.Equal(1, MediaFoundationH264FrameEncoder.DebugInputBufferProbeExecutionCount);
            var summary = MediaFoundationH264FrameEncoder.DebugLastInputBufferProbeSummary;
            var rootCause = MediaFoundationH264FrameEncoder.DebugLastInputBufferRootCause;

            Assert.NotEqual("status=not-run", summary);
            Assert.False(string.IsNullOrWhiteSpace(MediaFoundationH264FrameEncoder.DebugSelectedInputBufferStrategy));
            Assert.Contains("sample_created=", summary);
            Assert.Contains("sample_usable=", summary);
            Assert.Contains("write_sample_reached=", summary);
            Assert.Contains("configure_transform_succeeded=", summary);
            Assert.Contains("process_input_reached=", summary);
            Assert.Contains("process_output_reached=", summary);
            Assert.Contains("hardware_process_input_reached=", summary);
            Assert.Contains("software_process_input_reached=", summary);
            Assert.Contains("accepted_backend=", summary);
            Assert.NotEqual("unknown", rootCause);

            if (rootCause == "sink_writer_rejected_valid_sample")
            {
                Assert.Contains("sample_usable=1", summary);
                Assert.Contains("process_input_succeeded=1", summary);
            }

            if (rootCause == "software_backend_rejected_dxgi_sample")
            {
                Assert.Contains("software_process_input_reached=1", summary);
                Assert.Contains("software_process_input_succeeded=0", summary);
            }

            if (rootCause == "hardware_backend_rejected_dxgi_sample")
            {
                Assert.Contains("hardware_process_input_reached=1", summary);
                Assert.Contains("hardware_process_input_succeeded=0", summary);
            }

            if (rootCause == "dxgi_surface_sample_unusable")
            {
                Assert.Contains("sample_usable=0", summary);
            }

            if (rootCause == "direct_mft_backend_accepted_sample")
            {
                Assert.Contains("process_input_succeeded=1", summary);
                Assert.Contains("process_output_succeeded=1", summary);
                Assert.DoesNotContain("accepted_backend=none", summary);
            }
        }
        finally
        {
            MediaFoundationH264FrameEncoder.ResetDebugInputBufferProbeState();
        }
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_InputBufferProbe_DoesNotRepeatAcrossSequentialEncodeAttempts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        MediaFoundationH264FrameEncoder.ResetDebugInputBufferProbeState();
        try
        {
            await using var encoder = MediaFoundationH264FrameEncoder.TryCreate("test");
            if (encoder is null)
            {
                return;
            }

            for (var i = 0; i < 2; i++)
            {
                using var bitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var frame = new WindowsRawCaptureFrame(bitmap, capturedTsUtcMs: 1000 + (i * 100));

                try
                {
                    await encoder.EncodeAsync(
                        frame,
                        new WindowsH264EncodeOptions(
                            TargetFramesPerSecond: 5,
                            TuningLevel: ScreenShareTransportTuningLevel.Normal,
                            ForceKeyFrame: i == 0,
                            StreamEpoch: 22),
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
                {
                }
            }

            Assert.Equal(1, MediaFoundationH264FrameEncoder.DebugInputBufferProbeExecutionCount);
        }
        finally
        {
            MediaFoundationH264FrameEncoder.ResetDebugInputBufferProbeState();
        }
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_WhenAvailable_EncodesAndEmitsStreamConfigOnFirstFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var encoder = MediaFoundationH264FrameEncoder.TryCreate();
        if (encoder is null)
        {
            return;
        }

        using var firstBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var firstFrame = new WindowsRawCaptureFrame(firstBitmap, capturedTsUtcMs: 1000);
        WindowsH264EncodedFrame? firstEncoded = null;
        try
        {
            firstEncoded = await encoder.EncodeAsync(
                firstFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: true,
                    StreamEpoch: 1),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
        {
            return;
        }

        if (firstEncoded is null)
        {
            using var warmupBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var warmupFrame = new WindowsRawCaptureFrame(warmupBitmap, capturedTsUtcMs: 1100);
            firstEncoded = await encoder.EncodeAsync(
                warmupFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: false,
                    StreamEpoch: 1),
                CancellationToken.None);
        }

        Assert.NotNull(firstEncoded);

        Assert.NotEmpty(firstEncoded!.EncodedBytes);
        Assert.NotNull(firstEncoded.StreamConfig);
        Assert.Equal(1, firstEncoded.StreamConfig!.StreamEpoch);
        Assert.Equal("h264", firstEncoded.StreamConfig.Encoding);
        var metricsSource = Assert.IsAssignableFrom<IWindowsH264FrameEncoderMetricsSource>(encoder);
        var runtimeMetrics = metricsSource.GetRuntimeMetricsSnapshot();
        Assert.False(string.IsNullOrWhiteSpace(runtimeMetrics.LowDelayConfigApplied));
        Assert.False(string.IsNullOrWhiteSpace(runtimeMetrics.LastAccessUnitKind));
        Assert.True(runtimeMetrics.EmittedDisplayableFrames + runtimeMetrics.EmittedNonDisplayableUnits >= 1);

        using var secondBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var secondFrame = new WindowsRawCaptureFrame(secondBitmap, capturedTsUtcMs: 1200);
        WindowsH264EncodedFrame? secondEncoded;
        try
        {
            secondEncoded = await encoder.EncodeAsync(
                secondFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: false,
                    StreamEpoch: 1),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
        {
            return;
        }

        Assert.NotNull(secondEncoded);
        Assert.NotEmpty(secondEncoded!.EncodedBytes);
        Assert.Null(secondEncoded.StreamConfig);
        Assert.Equal(1, secondEncoded.StreamEpoch);
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_WhenAvailable_TransportRole_ReportsIpOnlyRuntimeMetrics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var encoder = MediaFoundationH264FrameEncoder.TryCreate("transport");
        if (encoder is null)
        {
            return;
        }

        using var firstBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var firstFrame = new WindowsRawCaptureFrame(firstBitmap, capturedTsUtcMs: 1000);
        try
        {
            _ = await encoder.EncodeAsync(
                firstFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: true,
                    StreamEpoch: 31),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
        {
            return;
        }

        var metricsSource = Assert.IsAssignableFrom<IWindowsH264FrameEncoderMetricsSource>(encoder);
        var runtimeMetrics = metricsSource.GetRuntimeMetricsSnapshot();
        Assert.True(runtimeMetrics.TransportIpOnlyMode);
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_WhenAvailable_PreviewRole_RemainsNonIpOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var encoder = MediaFoundationH264FrameEncoder.TryCreate("preview");
        if (encoder is null)
        {
            return;
        }

        using var firstBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var firstFrame = new WindowsRawCaptureFrame(firstBitmap, capturedTsUtcMs: 1000);
        try
        {
            _ = await encoder.EncodeAsync(
                firstFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: true,
                    StreamEpoch: 32),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
        {
            return;
        }

        var metricsSource = Assert.IsAssignableFrom<IWindowsH264FrameEncoderMetricsSource>(encoder);
        var runtimeMetrics = metricsSource.GetRuntimeMetricsSnapshot();
        Assert.False(runtimeMetrics.TransportIpOnlyMode);
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264BitmapDecoder_TryCreate_ReturnsSupportedDecoder_WhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var decoder = MediaFoundationH264BitmapDecoder.TryCreate();
        if (decoder is null)
        {
            return;
        }

        Assert.True(decoder.IsSupported);
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264BitmapDecoder_TryCreate_RecordsDiagnosticRole_WhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var decoder = MediaFoundationH264BitmapDecoder.TryCreate("helper_remote");
        if (decoder is null)
        {
            return;
        }

        Assert.Equal("helper_remote", MediaFoundationH264BitmapDecoder.DebugLastCreatedRole);
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264BitmapDecoder_WhenAvailable_ConfigureAndDecodeRoundTripsEncoderOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var encoder = MediaFoundationH264FrameEncoder.TryCreate();
        using var decoder = MediaFoundationH264BitmapDecoder.TryCreate();
        if (encoder is null || decoder is null)
        {
            return;
        }

        using var firstBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var firstFrame = new WindowsRawCaptureFrame(firstBitmap, capturedTsUtcMs: 1000);
        WindowsH264EncodedFrame? encoded = null;
        try
        {
            encoded = await encoder.EncodeAsync(
                firstFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: true,
                    StreamEpoch: 11),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
        {
            return;
        }

        if (encoded is null)
        {
            using var warmupBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var warmupFrame = new WindowsRawCaptureFrame(warmupBitmap, capturedTsUtcMs: 1200);
            encoded = await encoder.EncodeAsync(
                warmupFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: false,
                    StreamEpoch: 11),
                CancellationToken.None);
        }

        Assert.NotNull(encoded);

        decoder.ConfigureStream(encoded!.StreamConfig ?? new ScreenShareVideoStreamConfigV1
        {
            SessionId = string.Empty,
            StreamEpoch = 11,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = Array.Empty<byte>(),
        });

        Bitmap? decoded = null;
        try
        {
            decoded = decoder.Decode(new EncodedFrameDecodeRequest("h264", encoded.EncodedBytes, encoded.IsKeyFrame, encoded.StreamEpoch));
        }
        catch (COMException)
        {
            return;
        }

        using (decoded)
        {
            Assert.NotNull(decoded);
            Assert.True(decoded.PixelSize.Width > 0);
            Assert.True(decoded.PixelSize.Height > 0);
        }
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public async Task MediaFoundationH264FrameEncoder_WhenAvailable_ConfigureAndFfmpegDecodeRoundTripsEncoderOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var encoder = MediaFoundationH264FrameEncoder.TryCreate();
        if (encoder is null)
        {
            return;
        }

        using var firstBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var firstFrame = new WindowsRawCaptureFrame(firstBitmap, capturedTsUtcMs: 1000);
        WindowsH264EncodedFrame? firstEncoded = null;
        try
        {
            firstEncoded = await encoder.EncodeAsync(
                firstFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: true,
                    StreamEpoch: 17),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
        {
            return;
        }

        if (firstEncoded is null)
        {
            using var warmupBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var warmupFrame = new WindowsRawCaptureFrame(warmupBitmap, capturedTsUtcMs: 1100);
            firstEncoded = await encoder.EncodeAsync(
                warmupFrame,
                new WindowsH264EncodeOptions(
                    TargetFramesPerSecond: 5,
                    TuningLevel: ScreenShareTransportTuningLevel.Normal,
                    ForceKeyFrame: false,
                    StreamEpoch: 17),
                CancellationToken.None);
        }

        Assert.NotNull(firstEncoded);

        var needsSecondFrame = false;
        await fixture.Session.Dispatch(() =>
        {
            using var decoder = FfmpegH264BitmapDecoder.TryCreate("helper_remote");
            if (decoder is null)
            {
                return true;
            }

            decoder.ConfigureStream(firstEncoded!.StreamConfig ?? new ScreenShareVideoStreamConfigV1
            {
                SessionId = string.Empty,
                StreamEpoch = 17,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = Array.Empty<byte>(),
            });

            Bitmap? decoded = null;
            try
            {
                decoded = decoder.Decode(new EncodedFrameDecodeRequest("h264", firstEncoded.EncodedBytes, firstEncoded.IsKeyFrame, firstEncoded.StreamEpoch));
            }
            catch (H264DecoderNeedsMoreInputException)
            {
                needsSecondFrame = true;
            }

            using (decoded)
            {
                if (decoded is null)
                {
                    return true;
                }

                Assert.NotNull(decoded);
                Assert.True(decoded!.PixelSize.Width > 0);
                Assert.True(decoded.PixelSize.Height > 0);
            }

            return true;
        }, default);

        if (needsSecondFrame)
        {
            using var secondBitmap = new DrawingBitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var secondFrame = new WindowsRawCaptureFrame(secondBitmap, capturedTsUtcMs: 1200);
            WindowsH264EncodedFrame? secondEncoded;
            try
            {
                secondEncoded = await encoder.EncodeAsync(
                    secondFrame,
                    new WindowsH264EncodeOptions(
                        TargetFramesPerSecond: 5,
                        TuningLevel: ScreenShareTransportTuningLevel.Normal,
                        ForceKeyFrame: false,
                        StreamEpoch: 17),
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is COMException || ex is InvalidOperationException)
            {
                return;
            }

            if (secondEncoded is null)
            {
                return;
            }

            await fixture.Session.Dispatch(() =>
            {
                using var decoder = FfmpegH264BitmapDecoder.TryCreate("helper_remote");
                if (decoder is null)
                {
                    return true;
                }

                decoder.ConfigureStream(firstEncoded!.StreamConfig ?? new ScreenShareVideoStreamConfigV1
                {
                    SessionId = string.Empty,
                    StreamEpoch = 17,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = Array.Empty<byte>(),
                });

                using var decoded = decoder.Decode(new EncodedFrameDecodeRequest("h264", secondEncoded.EncodedBytes, secondEncoded.IsKeyFrame, secondEncoded.StreamEpoch));
                Assert.NotNull(decoded);
                Assert.True(decoded!.PixelSize.Width > 0);
                Assert.True(decoded.PixelSize.Height > 0);
                return true;
            }, default);
        }
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264BitmapDecoder_WhenAvailable_HelperStyleReplay_EitherDecodesOrClassifiesNeedMoreInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        MediaFoundationH264BitmapDecoder.ResetDebugInputSampleStrategyState();
        using var decoder = MediaFoundationH264BitmapDecoder.TryCreate("helper_remote");
        if (decoder is null)
        {
            return;
        }

        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelperRemoteH264Decode");
        Assert.True(Directory.Exists(fixtureRoot), $"Expected helper replay fixture directory to exist at '{fixtureRoot}'.");

        var configBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, "decoder-config.bin"));
        var framePaths = Directory.GetFiles(fixtureRoot, "frame-*.bin")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.True(framePaths.Length >= 7, "Expected at least seven helper replay frames.");

        decoder.ConfigureStream(new ScreenShareVideoStreamConfigV1
        {
            SessionId = "helper-replay",
            StreamEpoch = 29,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = configBytes,
        });

        Bitmap? decodedBitmap = null;
        var needMoreInputCount = 0;
        InvalidOperationException? classifiedFailure = null;
        foreach (var framePath in framePaths)
        {
            var encodedBytes = File.ReadAllBytes(framePath);
            try
            {
                decodedBitmap = decoder.Decode(new EncodedFrameDecodeRequest("h264", encodedBytes, IsKeyFrame: true, StreamEpoch: 29));
                break;
            }
            catch (H264DecoderNeedsMoreInputException)
            {
                needMoreInputCount++;
            }
            catch (InvalidOperationException ex)
            {
                classifiedFailure = ex;
                break;
            }
            catch (COMException)
            {
                return;
            }
        }

        using (decodedBitmap)
        {
            if (decodedBitmap is not null)
            {
                Assert.True(decodedBitmap.PixelSize.Width > 0);
                Assert.True(decodedBitmap.PixelSize.Height > 0);
                Assert.NotEqual("unknown", MediaFoundationH264BitmapDecoder.DebugPreferredOutputCombination);
                Assert.Equal("software_fixed_clsid+low_latency", MediaFoundationH264BitmapDecoder.DebugPreferredDecoderBackendProfile);
                Assert.NotEqual("unknown", MediaFoundationH264BitmapDecoder.DebugPreferredOutputSubtype);
                return;
            }

            decoder.Reset();
            Assert.True(needMoreInputCount > 0 || classifiedFailure is not null);
            Assert.Contains(
                MediaFoundationH264BitmapDecoder.DebugLastConclusion,
                [
                    "transform_start_sequence_failure",
                    "process_output_rejected_caller_sample",
                    "process_output_returned_success_without_sample",
                    "mft_reported_sample_ready_but_no_output",
                    "mft_misreported_output_flags",
                    "software_backend_never_reported_sample_ready_with_native_output_types",
                    "software_backend_requires_native_output_subtype",
                    "decoder_attribute_profile_failure",
                    "process_output_needs_more_input_after_verified_drain",
                    "decoder_no_output",
                    "decoder_requires_end_of_stream_to_surface_output"
                ]);
            var outputMatrixSummary = MediaFoundationH264BitmapDecoder.DebugLastOutputMatrixSummary;
            Assert.False(string.IsNullOrWhiteSpace(outputMatrixSummary));
            Assert.Contains("backend=software_fixed_clsid", outputMatrixSummary);
            Assert.Contains("attribute_profile=low_latency", outputMatrixSummary);
            Assert.DoesNotContain("attribute_profile=baseline", outputMatrixSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("backend=hardware_enum_first", outputMatrixSummary, StringComparison.Ordinal);
            Assert.Contains("combination=two_d_video_buffer_length_preset+normal_process_output", outputMatrixSummary);
            Assert.DoesNotContain("aligned_contiguous_length_zero", outputMatrixSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("aligned_contiguous_length_preset", outputMatrixSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("two_d_video_buffer_length_zero", outputMatrixSummary, StringComparison.Ordinal);
            Assert.Contains("output_subtype_probe=", outputMatrixSummary);
            Assert.Contains("output_subtype_candidate=", outputMatrixSummary);
            Assert.Contains("output_subtype_native_advertised=", outputMatrixSummary);
            Assert.Contains("startup_sequence=types_before_start", outputMatrixSummary);
            Assert.Contains("startup_sequence_verified=", outputMatrixSummary);
            Assert.Contains("input_type_configured=", outputMatrixSummary);
            Assert.Contains("begin_streaming_sent=", outputMatrixSummary);
            Assert.Contains("start_of_stream_sent=", outputMatrixSummary);
            Assert.Contains("fully_started_before_first_input=", outputMatrixSummary);
            Assert.Contains("output_type_configured=", outputMatrixSummary);
            Assert.Contains("output_type_verified=", outputMatrixSummary);
            Assert.Contains("set_output_type_hr=", outputMatrixSummary);
            Assert.Contains("get_output_current_type_hr=", outputMatrixSummary);
            Assert.Contains("post_config_output_status_hr=", outputMatrixSummary);
            Assert.Contains("activation_source=", outputMatrixSummary);
            Assert.Contains("friendly_name=", outputMatrixSummary);
            Assert.Contains("low_latency_requested=", outputMatrixSummary);
            Assert.Contains("low_latency_applied=", outputMatrixSummary);
            Assert.Contains("transform_low_latency_applied=", outputMatrixSummary);
            Assert.Contains("transform_low_latency_hr=", outputMatrixSummary);
            Assert.Contains("input_media_type_low_latency_applied=", outputMatrixSummary);
            Assert.Contains("input_media_type_low_latency_hr=", outputMatrixSummary);
            Assert.Contains("output_media_type_low_latency_applied=", outputMatrixSummary);
            Assert.Contains("output_media_type_low_latency_hr=", outputMatrixSummary);
            Assert.Contains("codecapi_available=", outputMatrixSummary);
            Assert.Contains("codecapi_supported=", outputMatrixSummary);
            Assert.Contains("codecapi_is_supported_hr=", outputMatrixSummary);
            Assert.Contains("codecapi_modifiable=", outputMatrixSummary);
            Assert.Contains("codecapi_is_modifiable_hr=", outputMatrixSummary);
            Assert.Contains("codecapi_set_value_hr=", outputMatrixSummary);
            Assert.Contains("transform_attributes_before_profile=", outputMatrixSummary);
            Assert.Contains("transform_attributes_after_profile=", outputMatrixSummary);
            Assert.Contains("transform_attributes=", outputMatrixSummary);
            Assert.Contains("input_stream_attributes=", outputMatrixSummary);
            Assert.Contains("output_stream_attributes=", outputMatrixSummary);
            Assert.Contains("transform_id=", outputMatrixSummary);
            Assert.Contains("stage=", outputMatrixSummary);
            Assert.Contains("input_status=", outputMatrixSummary);
            Assert.Contains("output_status=", outputMatrixSummary);
            Assert.Contains("sample_ready_seen=", outputMatrixSummary);
            Assert.Contains("process_output_hr=", outputMatrixSummary);
            Assert.Contains("frames=7", outputMatrixSummary);
            Assert.True(
                Regex.Matches(outputMatrixSummary, "transform_id=").Count >= 1,
                "Expected output matrix replay to configure at least one transform instance.");
            Assert.NotEqual("unchecked", MediaFoundationH264BitmapDecoder.DebugLastHardwareDecoderAvailabilitySummary);
            Assert.Contains("available=", MediaFoundationH264BitmapDecoder.DebugLastHardwareDecoderAvailabilitySummary);
            Assert.NotEqual("unknown", MediaFoundationH264BitmapDecoder.DebugLastAttemptedOutputCombination);
            Assert.NotEqual("(none)", MediaFoundationH264BitmapDecoder.DebugLastOutputFailureStage);
            Assert.NotEqual("(none)", MediaFoundationH264BitmapDecoder.DebugLastOutputFailureHresult);
            Assert.Equal("unknown", MediaFoundationH264BitmapDecoder.DebugPreferredDecoderBackendProfile);
            Assert.Equal("unknown", MediaFoundationH264BitmapDecoder.DebugPreferredOutputCombination);
            Assert.Equal("unknown", MediaFoundationH264BitmapDecoder.DebugPreferredOutputSubtype);

            var nativeSubtypeIndex = outputMatrixSummary.IndexOf("output_subtype_probe=native_advertised_first_supported", StringComparison.Ordinal);
            Assert.True(nativeSubtypeIndex >= 0, "Expected the native advertised output subtype candidate to be exercised first.");

            var explicitNv12Index = outputMatrixSummary.IndexOf("output_subtype_probe=explicit_nv12", StringComparison.Ordinal);
            if (explicitNv12Index >= 0)
            {
                Assert.True(nativeSubtypeIndex < explicitNv12Index, "Expected explicit NV12 probing to happen after the native advertised candidate.");
            }

            var explicitYuy2Index = outputMatrixSummary.IndexOf("output_subtype_probe=explicit_yuy2", StringComparison.Ordinal);
            if (explicitYuy2Index >= 0)
            {
                Assert.True(nativeSubtypeIndex < explicitYuy2Index, "Expected explicit YUY2 probing to happen after the native advertised candidate.");
            }

            if (explicitNv12Index >= 0 && explicitYuy2Index >= 0)
            {
                Assert.True(explicitNv12Index < explicitYuy2Index, "Expected explicit NV12 probing to precede explicit YUY2 probing when both are advertised.");
            }

            if (string.Equals(MediaFoundationH264BitmapDecoder.DebugLastConclusion, "software_backend_never_reported_sample_ready_with_native_output_types", StringComparison.Ordinal))
            {
                Assert.Contains("sample_ready_seen=0", outputMatrixSummary);
            }
        }
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_Mp4Extraction_UsesTopLevelMdatInsteadOfFakePayloadSignature()
    {
        var avcC = new byte[]
        {
            0x01, 0x42, 0x00, 0x1E, 0xFF, 0xE1,
            0x00, 0x04, 0x67, 0x42, 0x00, 0x1E,
            0x01,
            0x00, 0x02, 0x68, 0xCE,
        };

        var fakeMdatPayload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44 };
        var realSamplePayload = new byte[]
        {
            0x00, 0x00, 0x00, 0x04,
            0x65, 0x88, 0x84, 0x21,
        };

        var ftypPayload = CreateBox("junk", fakeMdatPayload);
        var containerBytes = Combine(
            CreateBox("ftyp", ftypPayload),
            CreateBox("avcC", avcC),
            CreateBox("mdat", realSamplePayload));

        var encodedBytes = MediaFoundationH264FrameEncoder.DebugExtractAnnexBFromSingleSampleMp4(containerBytes, out var decoderConfigData);

        Assert.Equal(avcC, decoderConfigData);
        Assert.True(
            ContainsSubsequence(encodedBytes, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21 }),
            "Expected extracted Annex B bytes to contain the real IDR sample payload.");

        static byte[] CreateBox(string type, byte[] payload)
        {
            var size = 8 + payload.Length;
            var bytes = new byte[size];
            bytes[0] = (byte)((size >> 24) & 0xFF);
            bytes[1] = (byte)((size >> 16) & 0xFF);
            bytes[2] = (byte)((size >> 8) & 0xFF);
            bytes[3] = (byte)(size & 0xFF);
            bytes[4] = (byte)type[0];
            bytes[5] = (byte)type[1];
            bytes[6] = (byte)type[2];
            bytes[7] = (byte)type[3];
            Buffer.BlockCopy(payload, 0, bytes, 8, payload.Length);
            return bytes;
        }

        static byte[] Combine(params byte[][] segments)
        {
            var totalLength = segments.Sum(segment => segment.Length);
            var result = new byte[totalLength];
            var offset = 0;
            foreach (var segment in segments)
            {
                Buffer.BlockCopy(segment, 0, result, offset, segment.Length);
                offset += segment.Length;
            }

            return result;
        }

        static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0)
            {
                return true;
            }

            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_AccessUnitClassification_ClassifiesDisplayableIdrUnits()
    {
        var accessUnit = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x1F,
            0x00, 0x00, 0x00, 0x01, 0x68, 0xEB, 0xEF, 0x20,
            0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21,
        };

        Assert.True(MediaFoundationH264FrameEncoder.DebugIsDisplayableAccessUnit(accessUnit));
        Assert.True(MediaFoundationH264FrameEncoder.DebugIsIdrAccessUnit(accessUnit));
        Assert.Equal("idr_vcl", MediaFoundationH264FrameEncoder.DebugClassifyAccessUnitKind(accessUnit));
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_AccessUnitClassification_ClassifiesDisplayablePUnits()
    {
        var accessUnit = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x41, 0xC0,
        };

        Assert.True(MediaFoundationH264FrameEncoder.DebugIsDisplayableAccessUnit(accessUnit));
        Assert.False(MediaFoundationH264FrameEncoder.DebugIsIdrAccessUnit(accessUnit));
        Assert.Equal("p_vcl", MediaFoundationH264FrameEncoder.DebugClassifyAccessUnitKind(accessUnit));
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_AccessUnitClassification_ClassifiesDisplayableBUnits()
    {
        var accessUnit = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x41, 0xA0,
        };

        Assert.True(MediaFoundationH264FrameEncoder.DebugIsDisplayableAccessUnit(accessUnit));
        Assert.False(MediaFoundationH264FrameEncoder.DebugIsIdrAccessUnit(accessUnit));
        Assert.Equal("b_vcl", MediaFoundationH264FrameEncoder.DebugClassifyAccessUnitKind(accessUnit));
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_AccessUnitClassification_ClassifiesMultiPictureUnits()
    {
        var accessUnit = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x41, 0xC0,
            0x00, 0x00, 0x00, 0x01, 0x41, 0xC0,
        };

        Assert.True(MediaFoundationH264FrameEncoder.DebugIsDisplayableAccessUnit(accessUnit));
        Assert.Equal("multi_picture_vcl", MediaFoundationH264FrameEncoder.DebugClassifyAccessUnitKind(accessUnit));
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_AccessUnitClassification_ClassifiesSpsPpsOnlyUnitsAsNonDisplayable()
    {
        var accessUnit = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x67, 0x64, 0x00, 0x1F,
            0x00, 0x00, 0x00, 0x01, 0x68, 0xEB, 0xEF, 0x20,
        };

        Assert.False(MediaFoundationH264FrameEncoder.DebugIsDisplayableAccessUnit(accessUnit));
        Assert.False(MediaFoundationH264FrameEncoder.DebugIsIdrAccessUnit(accessUnit));
        Assert.Equal("sps_pps_only", MediaFoundationH264FrameEncoder.DebugClassifyAccessUnitKind(accessUnit));
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264FrameEncoder_AccessUnitClassification_ClassifiesSeiAudOnlyUnitsAsNonDisplayable()
    {
        var accessUnit = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x06, 0x05, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x01, 0x09, 0x10,
        };

        Assert.False(MediaFoundationH264FrameEncoder.DebugIsDisplayableAccessUnit(accessUnit));
        Assert.False(MediaFoundationH264FrameEncoder.DebugIsIdrAccessUnit(accessUnit));
        Assert.Equal("sei_aud_only", MediaFoundationH264FrameEncoder.DebugClassifyAccessUnitKind(accessUnit));
    }

    [Fact]
    [Trait("Category", "MfDiagnostic")]
    public void MediaFoundationH264BitmapDecoder_AnnexBConversion_UsesAvccNalLengthSize()
    {
        var avcC = new byte[]
        {
            0x01, 0x42, 0x00, 0x1E, 0xFF, 0xE1,
            0x00, 0x04, 0x67, 0x42, 0x00, 0x1E,
            0x01,
            0x00, 0x02, 0x68, 0xCE,
        };

        var annexB = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1E,
            0x00, 0x00, 0x00, 0x01, 0x68, 0xCE,
            0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21,
        };

        var converted = MediaFoundationH264BitmapDecoder.DebugConvertAnnexBToLengthPrefixed(annexB, avcC);

        Assert.Equal(
            new byte[]
            {
                0x00, 0x00, 0x00, 0x04, 0x67, 0x42, 0x00, 0x1E,
                0x00, 0x00, 0x00, 0x02, 0x68, 0xCE,
                0x00, 0x00, 0x00, 0x04, 0x65, 0x88, 0x84, 0x21,
            },
            converted);
    }
}
