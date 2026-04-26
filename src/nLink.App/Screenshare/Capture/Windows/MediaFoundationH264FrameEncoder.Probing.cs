using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed partial class MediaFoundationH264FrameEncoder
{
    private static TransformConfigurationResult ConfigureTransformForProbe(
        IMFTransform encoderTransform,
        int width,
        int height,
        WindowsH264EncodeOptions options,
        bool allIntra,
        uint bitrate,
        IMFDXGIDeviceManager? deviceManager = null,
        string strategyLabel = "unknown")
    {
        return ConfigureTransformCore(
            encoderTransform,
            width,
            height,
            options,
            allIntra,
            bitrate,
            deviceManager,
            strategyLabel,
            probeMode: true);
    }


    private RawInputBufferStrategy EnsureInputBufferStrategy(
        int width,
        int height,
        int targetFramesPerSecond,
        int bufferLength,
        long sampleDurationHns)
    {
        lock (InputBufferProbeSync)
        {
            if (inputBufferProbeCompleted)
            {
                if (!inputBufferProbeSucceeded)
                {
                    terminalInputBufferFailureSummary = lastInputBufferProbeSummary;
                    throw new RawInputBufferStrategyUnavailableException(lastInputBufferProbeSummary);
                }

                return selectedInputBufferStrategy;
            }
        }

        RunInputBufferProbe(width, height, targetFramesPerSecond, bufferLength, sampleDurationHns);

        lock (InputBufferProbeSync)
        {
            if (!inputBufferProbeSucceeded)
            {
                terminalInputBufferFailureSummary = lastInputBufferProbeSummary;
                throw new RawInputBufferStrategyUnavailableException(lastInputBufferProbeSummary);
            }

            return selectedInputBufferStrategy;
        }
    }


    private void RunInputBufferProbe(
        int width,
        int height,
        int targetFramesPerSecond,
        int bufferLength,
        long sampleDurationHns)
    {
        lock (InputBufferProbeSync)
        {
            if (inputBufferProbeCompleted)
            {
                return;
            }

            inputBufferProbeExecutionCount++;
            var summaries = new List<string>();
            var probeBytes = new byte[bufferLength];
            var outcomes = new List<BufferStrategyProbeOutcome>();
            foreach (var strategy in new[]
                     {
                         RawInputBufferStrategy.CpuMemoryBufferNv12,
                         RawInputBufferStrategy.Cpu2DVideoBuffer,
                         RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate,
                         RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture,
                     })
            {
                var outcome = ProbeInputBufferStrategy(strategy, width, height, targetFramesPerSecond, probeBytes, sampleDurationHns);
                outcomes.Add(outcome);
                summaries.Add(outcome.ToSummary());
                if (outcome.Success)
                {
                    selectedInputBufferStrategy = strategy;
                    inputBufferProbeCompleted = true;
                    inputBufferProbeSucceeded = true;
                    lastInputBufferRootCause = "ok";
                    lastInputBufferProbeSummary = string.Join(" | ", summaries);
                    LogInstanceLifecycle(
                        "screenshare_h264_input_buffer_probe_selected",
                        $"selected_strategy={strategy.ToString().ToLowerInvariant()}; sample_creation=1; write_sample=1; finalize=1; summary={Sanitize(lastInputBufferProbeSummary)}");
                    LogInstanceLifecycle(
                        "screenshare_h264_input_buffer_probe_root_cause",
                        $"root_cause=ok; selected_strategy={strategy.ToString().ToLowerInvariant()}; summary={Sanitize(lastInputBufferProbeSummary)}");
                    return;
                }
            }

            selectedInputBufferStrategy = RawInputBufferStrategy.CpuMemoryBufferNv12;
            inputBufferProbeCompleted = true;
            inputBufferProbeSucceeded = false;
            lastInputBufferRootCause = ClassifyRootCause(outcomes);
            lastInputBufferProbeSummary = string.Join(" | ", summaries);
            LogInstanceLifecycle(
                "screenshare_h264_input_buffer_probe_failed",
                $"selected_strategy={selectedInputBufferStrategy.ToString().ToLowerInvariant()}; sample_creation=0; write_sample=0; finalize=0; summary={Sanitize(lastInputBufferProbeSummary)}");
            LogInstanceLifecycle(
                "screenshare_h264_input_buffer_probe_root_cause",
                $"root_cause={lastInputBufferRootCause}; selected_strategy={selectedInputBufferStrategy.ToString().ToLowerInvariant()}; summary={Sanitize(lastInputBufferProbeSummary)}");
        }
    }

    private static BufferStrategyProbeOutcome ProbeInputBufferStrategy(
        RawInputBufferStrategy strategy,
        int width,
        int height,
        int targetFramesPerSecond,
        byte[] bytes,
        long sampleDurationHns)
    {
        var stage = "create_sink_writer";
        var tempPath = Path.Combine(Path.GetTempPath(), $"nlink-h264-probe-{Guid.NewGuid():N}.mp4");
        SinkWriterContext? sinkWriterContext = null;
        IMFSinkWriter? sinkWriter = null;
        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        IMFSample? sample = null;
        var sampleCreated = false;
        var sampleUsable = false;
        var uploadSucceeded = false;
        var writeSampleReached = false;
        var writeSampleSucceeded = false;
        var directTransformProbe = DirectTransformProbeResult.NotRun;
        try
        {
            outputType = CreateOutputMediaType(width, height, Math.Max(1, targetFramesPerSecond), 1_500_000);
            inputType = CreateInputMediaType(width, height, Math.Max(1, targetFramesPerSecond));
            sinkWriterContext = CreateSinkWriter(tempPath);
            sinkWriter = sinkWriterContext.Writer;

            stage = "add_stream";
            Marshal.ThrowExceptionForHR(sinkWriter.AddStream(outputType, out var streamIndex));
            stage = "set_input_media_type";
            Marshal.ThrowExceptionForHR(sinkWriter.SetInputMediaType(streamIndex, inputType, null));
            stage = "begin_writing";
            Marshal.ThrowExceptionForHR(sinkWriter.BeginWriting());
            LogLifecycle(
                "screenshare_h264_probe_stream_configured",
                $"strategy={strategy.ToString().ToLowerInvariant()}; {DescribeMediaType("input", inputType)}; {DescribeMediaType("output", outputType)}; shared_device={(sinkWriterContext.D3DDevice != IntPtr.Zero ? 1 : 0)}; shared_context={(sinkWriterContext.D3DContext != IntPtr.Zero ? 1 : 0)}");

            stage = "create_sample";
            sample = CreateInputSampleUsingStrategy(strategy, width, height, bytes, sampleDurationHns, sinkWriterContext.D3DDevice, sinkWriterContext.D3DContext);
            sampleCreated = true;
            uploadSucceeded = true;
            sampleUsable = IsSampleUsableForStrategy(strategy, sample);
            stage = "set_sample_time";
            Marshal.ThrowExceptionForHR(sample.SetSampleTime(0));
            stage = "set_sample_duration";
            var normalizedSampleDurationHns = sampleDurationHns <= 0 ? HnsPerSecond / 5 : sampleDurationHns;
            Marshal.ThrowExceptionForHR(sample.SetSampleDuration(normalizedSampleDurationHns));
            stage = "verify_sample_clock";
            VerifySampleClockMetadata(
                sample,
                0,
                normalizedSampleDurationHns,
                strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate,
                strategy.ToString().ToLowerInvariant());
            if (strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate)
            {
                LogLifecycle(
                    "screenshare_h264_probe_sample_buffer_state",
                    $"strategy={strategy.ToString().ToLowerInvariant()}; stage=pre_write; expected_length={bytes.Length}; sample_usable={(sampleUsable ? 1 : 0)}; {DescribeSampleBuffers(sample, bytes.Length, true)}");
            }
            stage = "write_sample";
            writeSampleReached = true;
            Marshal.ThrowExceptionForHR(sinkWriter.WriteSample(streamIndex, sample));
            writeSampleSucceeded = true;
            stage = "finalize";
            Marshal.ThrowExceptionForHR(sinkWriter.NotifyEndOfSegment(streamIndex));
            Marshal.ThrowExceptionForHR(sinkWriter.Finalize_());
            return BufferStrategyProbeOutcome.Successful(strategy, sampleUsable);
        }
        catch (Exception ex)
        {
            if (ex is InputSampleCreationException inputSampleCreationException && inputSampleCreationException.InnerException is not null)
            {
                stage = inputSampleCreationException.Stage;
                ex = inputSampleCreationException.InnerException;
                sampleCreated = false;
                uploadSucceeded = stage is not "query_d3d_device" and not "create_texture" and not "query_d3d_context" and not "update_subresource";
            }

            if (strategy is RawInputBufferStrategy.CpuMemoryBufferNv12 or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate or RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture &&
                writeSampleReached &&
                !writeSampleSucceeded)
            {
                directTransformProbe = ProbeDirectTransformAcceptance(
                    strategy,
                    width,
                    height,
                    Math.Max(1, targetFramesPerSecond),
                    bytes,
                    sampleDurationHns,
                    sinkWriterContext?.DeviceManager,
                    sinkWriterContext?.D3DDevice ?? IntPtr.Zero,
                    sinkWriterContext?.D3DContext ?? IntPtr.Zero);
                LogLifecycle(
                    "screenshare_h264_transform_probe_result",
                    $"strategy={strategy.ToString().ToLowerInvariant()}; {directTransformProbe.ToSummary()}");
            }

            return BufferStrategyProbeOutcome.Failed(
                strategy,
                stage,
                ex,
                sampleCreated,
                sampleUsable,
                uploadSucceeded,
                writeSampleReached,
                writeSampleSucceeded,
                directTransformProbe);
        }
        finally
        {
            if (sample is not null)
            {
                ReleaseComObject(sample);
            }
            ReleaseComObject(outputType);
            ReleaseComObject(inputType);
            sinkWriterContext?.Dispose();
            TryDeleteFile(tempPath);
        }
    }

    private static string ClassifyRootCause(IReadOnlyList<BufferStrategyProbeOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.SoftwareProcessOutputSucceeded || outcome.HardwareProcessOutputSucceeded)
            {
                return "direct_mft_backend_accepted_sample";
            }
        }

        foreach (var outcome in outcomes)
        {
            if ((outcome.SoftwareProcessInputSucceeded || outcome.HardwareProcessInputSucceeded) &&
                outcome.WriteSampleReached &&
                !outcome.WriteSampleSucceeded)
            {
                return "sink_writer_rejected_valid_sample";
            }
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.Strategy == RawInputBufferStrategy.CpuMemoryBufferNv12 &&
                !outcome.Success &&
                outcome.Stage is "create_memory_buffer" or "create_aligned_memory_buffer" or "write_memory_buffer" or "set_current_length" or "add_buffer")
            {
                return "cpu_memory_sample_assembly_failure";
            }
        }

        BufferStrategyProbeOutcome? reusableOutcome = null;
        foreach (var outcome in outcomes)
        {
            if (outcome.Strategy == RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate)
            {
                reusableOutcome = outcome;
                break;
            }
        }

        if (reusableOutcome is { Success: false } reusableFailure)
        {
            if (reusableFailure.SoftwareProcessOutputSucceeded || reusableFailure.HardwareProcessOutputSucceeded)
            {
                return "direct_mft_backend_accepted_sample";
            }

            if ((reusableFailure.SoftwareProcessInputSucceeded || reusableFailure.HardwareProcessInputSucceeded) &&
                reusableFailure.WriteSampleReached &&
                !reusableFailure.WriteSampleSucceeded)
            {
                return "sink_writer_rejected_valid_sample";
            }

            if (reusableFailure.SoftwareProcessInputReached && !reusableFailure.SoftwareProcessInputSucceeded)
            {
                return "software_backend_rejected_dxgi_sample";
            }

            if (reusableFailure.HardwareProcessInputReached && !reusableFailure.HardwareProcessInputSucceeded)
            {
                return "hardware_backend_rejected_dxgi_sample";
            }

            if (!reusableFailure.SampleUsable)
            {
                return "dxgi_surface_sample_unusable";
            }

            if (!reusableFailure.WriteSampleReached)
            {
                return "d3d_interop_failure";
            }

            if (!reusableFailure.WriteSampleSucceeded)
            {
                return "dxgi_surface_sample_unusable";
            }
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.SoftwareProcessInputReached && !outcome.SoftwareProcessInputSucceeded)
            {
                return "software_backend_rejected_dxgi_sample";
            }
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.HardwareProcessInputReached && !outcome.HardwareProcessInputSucceeded)
            {
                return "hardware_backend_rejected_dxgi_sample";
            }
        }

        foreach (var outcome in outcomes)
        {
            if (!outcome.SampleUsable)
            {
                return "dxgi_surface_sample_unusable";
            }
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.WriteSampleReached && !outcome.WriteSampleSucceeded)
            {
                return "dxgi_surface_sample_unusable";
            }
        }

        foreach (var outcome in outcomes)
        {
            if (!outcome.UploadSucceeded)
            {
                return "d3d_interop_failure";
            }
        }

        return "d3d_interop_failure";
    }

    private static DirectTransformProbeResult ProbeDirectTransformAcceptance(
        RawInputBufferStrategy strategy,
        int width,
        int height,
        int targetFramesPerSecond,
        byte[] bytes,
        long sampleDurationHns,
        IMFDXGIDeviceManager? deviceManager,
        IntPtr sharedD3DDevice,
        IntPtr sharedD3DContext)
    {
        var backendResults = new List<DirectTransformBackendProbeResult>();
        foreach (var backend in EnumerateTransformProbeBackends())
        {
            backendResults.Add(
                ProbeDirectTransformAcceptanceForBackend(
                    backend,
                    strategy,
                    width,
                    height,
                    targetFramesPerSecond,
                    bytes,
                    sampleDurationHns,
                    deviceManager,
                    sharedD3DDevice,
                    sharedD3DContext));
        }

        return DirectTransformProbeResult.FromBackendResults(backendResults);
    }

    private static DirectTransformBackendProbeResult ProbeDirectTransformAcceptanceForBackend(
        TransformProbeBackend backend,
        RawInputBufferStrategy strategy,
        int width,
        int height,
        int targetFramesPerSecond,
        byte[] bytes,
        long sampleDurationHns,
        IMFDXGIDeviceManager? deviceManager,
        IntPtr sharedD3DDevice,
        IntPtr sharedD3DContext)
    {
        var stage = "create_transform";
        IMFTransform? encoderTransform = null;
        IMFSample? sample = null;
        var summaries = new List<string>();
        try
        {
            if (!TryCreateTransform(backend, out encoderTransform) || encoderTransform is null)
            {
                var unavailable = DirectTransformBackendProbeResult.Unavailable(backend);
                summaries.Add(unavailable.ToSummary());
                return unavailable with { Summary = string.Join(" | ", summaries) };
            }

            LogLifecycle(
                "screenshare_h264_transform_backend_selected",
                $"backend={backend.ToString().ToLowerInvariant()}; strategy={strategy.ToString().ToLowerInvariant()}; has_device_manager={(deviceManager is null ? 0 : 1)}");

            stage = "configure_transform";
            var configurationResult = ConfigureTransformForProbe(
                encoderTransform,
                width,
                height,
                new WindowsH264EncodeOptions(targetFramesPerSecond, ScreenShareTransportTuningLevel.Normal, false, 1),
                allIntra: false,
                1_500_000,
                deviceManager,
                $"{backend.ToString().ToLowerInvariant()}_{strategy.ToString().ToLowerInvariant()}");
            var configuration = configurationResult.Configuration;
            LogLifecycle(
                "screenshare_h264_transform_backend_capabilities",
                $"backend={backend.ToString().ToLowerInvariant()}; strategy={strategy.ToString().ToLowerInvariant()}; has_device_manager={(deviceManager is null ? 0 : 1)}; reported_bind_flags={(configurationResult.ReportedD3D11BindFlags.HasValue ? $"0x{configurationResult.ReportedD3D11BindFlags.Value:X8}" : "unset")}");

            var probeAttempts = BuildDirectTransformProbeAttempts(configurationResult.ReportedD3D11BindFlags);
            foreach (var probeAttempt in probeAttempts)
            {
                ReleaseComObject(sample);
                sample = null;
                var processInputReached = false;
                var processInputSucceeded = false;
                var processOutputReached = false;
                var processOutputSucceeded = false;
                try
                {
                    stage = "create_sample";
                    sample = CreateInputSampleUsingStrategy(
                        strategy,
                        width,
                        height,
                        bytes,
                        sampleDurationHns,
                        sharedD3DDevice,
                        sharedD3DContext,
                        probeAttempt.BindFlagsOverride);
                    stage = "set_sample_time";
                    Marshal.ThrowExceptionForHR(sample.SetSampleTime(0));
                    stage = "set_sample_duration";
                    var normalizedSampleDurationHns = sampleDurationHns <= 0 ? HnsPerSecond / 5 : sampleDurationHns;
                    Marshal.ThrowExceptionForHR(sample.SetSampleDuration(normalizedSampleDurationHns));
                    stage = "verify_sample_clock";
                    VerifySampleClockMetadata(
                        sample,
                        0,
                        normalizedSampleDurationHns,
                        strategy is RawInputBufferStrategy.DxgiSurfaceNv12FreshTexture or RawInputBufferStrategy.DxgiSurfaceNv12ReusableTextureUpdate,
                        $"{backend.ToString().ToLowerInvariant()}_{strategy.ToString().ToLowerInvariant()}");
                    stage = "process_input";
                    processInputReached = true;
                    Marshal.ThrowExceptionForHR(encoderTransform.ProcessInput(0, sample, 0));
                    processInputSucceeded = true;
                    stage = "process_output";
                    processOutputReached = true;
                    processOutputSucceeded = TryProcessSingleOutput(encoderTransform, configuration, out var encodedBytes) &&
                                             encodedBytes.Length > 0;
                    var successResult = DirectTransformBackendProbeResult.Successful(
                        backend,
                        processOutputSucceeded ? "process_output" : "process_input",
                        probeAttempt.Label,
                        configurationResult.ReportedD3D11BindFlags,
                        processOutputReached,
                        processOutputSucceeded);
                    summaries.Add(successResult.ToSummary());
                    return successResult with { Summary = string.Join(" | ", summaries) };
                }
                catch (Exception ex)
                {
                    if (ex is TransformConfigurationException transformConfigurationException && transformConfigurationException.InnerException is not null)
                    {
                        stage = transformConfigurationException.Stage;
                        ex = transformConfigurationException.InnerException;
                    }

                    if (ex is InputSampleCreationException inputSampleCreationException && inputSampleCreationException.InnerException is not null)
                    {
                        stage = inputSampleCreationException.Stage;
                        ex = inputSampleCreationException.InnerException;
                    }

                    var failureResult = DirectTransformBackendProbeResult.Failed(
                        backend,
                        stage,
                        ex,
                        probeAttempt.Label,
                        configurationResult.ReportedD3D11BindFlags,
                        configureTransformSucceeded: true,
                        processInputReached,
                        processInputSucceeded,
                        processOutputReached,
                        processOutputSucceeded);
                    summaries.Add(failureResult.ToSummary());
                    if (!probeAttempt.IsLast)
                    {
                        continue;
                    }

                    return failureResult with { Summary = string.Join(" | ", summaries) };
                }
            }

            var noAttemptsResult = DirectTransformBackendProbeResult.Failed(
                backend,
                stage,
                new InvalidOperationException("Direct transform probe completed without any attempts."),
                "none",
                configurationResult.ReportedD3D11BindFlags,
                configureTransformSucceeded: true,
                processInputReached: false,
                processInputSucceeded: false,
                processOutputReached: false,
                processOutputSucceeded: false);
            summaries.Add(noAttemptsResult.ToSummary());
            return noAttemptsResult with { Summary = string.Join(" | ", summaries) };
        }
        catch (Exception ex)
        {
            if (ex is TransformConfigurationException transformConfigurationException && transformConfigurationException.InnerException is not null)
            {
                stage = transformConfigurationException.Stage;
                ex = transformConfigurationException.InnerException;
            }

            if (ex is InputSampleCreationException inputSampleCreationException && inputSampleCreationException.InnerException is not null)
            {
                stage = inputSampleCreationException.Stage;
                ex = inputSampleCreationException.InnerException;
            }

            var failureResult = DirectTransformBackendProbeResult.Failed(
                backend,
                stage,
                ex,
                "configure",
                reportedD3D11BindFlags: null,
                configureTransformSucceeded: false,
                processInputReached: false,
                processInputSucceeded: false,
                processOutputReached: false,
                processOutputSucceeded: false);
            summaries.Add(failureResult.ToSummary());
            return failureResult with { Summary = string.Join(" | ", summaries) };
        }
        finally
        {
            ReleaseComObject(sample);
            ReleaseComObject(encoderTransform);
        }
    }

    private static bool TryProcessSingleOutput(IMFTransform encoderTransform, EncoderConfiguration configuration, out byte[] encodedBytes)
    {
        encodedBytes = Array.Empty<byte>();
        Marshal.ThrowExceptionForHR(encoderTransform.GetOutputStreamInfo(0, out var outputInfo));
        var outputSample = CreateOutputSample(outputInfo, configuration);
        try
        {
            var outputBuffers = new[]
            {
                new MftOutputDataBuffer
                {
                    DwStreamId = 0,
                    PSample = outputSample,
                    DwStatus = 0,
                    PEvents = IntPtr.Zero,
                },
            };

            var hr = encoderTransform.ProcessOutput(0, 1, outputBuffers, out _);
            if (hr == MfTransformNeedMoreInput)
            {
                return false;
            }

            Marshal.ThrowExceptionForHR(hr);
            encodedBytes = ReadSampleBytes(outputSample);
            return true;
        }
        finally
        {
            ReleaseComObject(outputSample);
        }
    }

    private static void DrainPendingOutput(IMFTransform encoderTransform, EncoderConfiguration configuration)
    {
        for (var i = 0; i < 2; i++)
        {
            try
            {
                if (!TryProcessSingleOutput(encoderTransform, configuration, out _))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is COMException)
            {
                LogLifecycle(
                    "screenshare_h264_encoder_output_drain_failed",
                    $"reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
                return;
            }
        }
    }


    private static IReadOnlyList<DirectTransformProbeAttempt> BuildDirectTransformProbeAttempts(uint? reportedBindFlags)
    {
        var attempts = new List<DirectTransformProbeAttempt>
        {
            new("current_desc", null, false),
        };

        if (reportedBindFlags.HasValue && reportedBindFlags.Value != 0)
        {
            attempts.Add(new($"mft_bind_flags_0x{reportedBindFlags.Value:X8}", reportedBindFlags.Value, false));
        }

        if (attempts.Count > 0)
        {
            var last = attempts[^1];
            attempts[^1] = last with { IsLast = true };
        }

        return attempts;
    }

    private static IReadOnlyList<TransformProbeBackend> EnumerateTransformProbeBackends()
    {
        return
        [
            TransformProbeBackend.Software,
            TransformProbeBackend.Hardware,
        ];
    }


    private readonly record struct BufferStrategyProbeOutcome(
        RawInputBufferStrategy Strategy,
        bool Success,
        string Stage,
        string Reason,
        int HResult,
        bool SampleCreated,
        bool SampleUsable,
        bool UploadSucceeded,
        bool WriteSampleReached,
        bool WriteSampleSucceeded,
        bool DirectConfigureTransformSucceeded,
        bool DirectProcessInputReached,
        bool DirectProcessInputSucceeded,
        bool DirectProcessOutputReached,
        bool DirectProcessOutputSucceeded,
        bool HardwareConfigureTransformSucceeded,
        bool HardwareProcessInputReached,
        bool HardwareProcessInputSucceeded,
        bool HardwareProcessOutputReached,
        bool HardwareProcessOutputSucceeded,
        bool SoftwareConfigureTransformSucceeded,
        bool SoftwareProcessInputReached,
        bool SoftwareProcessInputSucceeded,
        bool SoftwareProcessOutputReached,
        bool SoftwareProcessOutputSucceeded,
        string DirectAcceptedBackend)
    {
        public static BufferStrategyProbeOutcome Successful(RawInputBufferStrategy strategy, bool sampleUsable)
        {
            return new BufferStrategyProbeOutcome(
                strategy,
                true,
                "completed",
                "ok",
                0,
                true,
                sampleUsable,
                true,
                true,
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                "none");
        }

        public static BufferStrategyProbeOutcome Failed(
            RawInputBufferStrategy strategy,
            string stage,
            Exception exception,
            bool sampleCreated,
            bool sampleUsable,
            bool uploadSucceeded,
            bool writeSampleReached,
            bool writeSampleSucceeded,
            DirectTransformProbeResult directTransformProbe)
        {
            return new BufferStrategyProbeOutcome(
                strategy,
                false,
                stage,
                exception.GetType().Name,
                exception.HResult,
                sampleCreated,
                sampleUsable,
                uploadSucceeded,
                writeSampleReached,
                writeSampleSucceeded,
                directTransformProbe.ConfigureTransformSucceeded,
                directTransformProbe.ProcessInputReached,
                directTransformProbe.ProcessInputSucceeded,
                directTransformProbe.ProcessOutputReached,
                directTransformProbe.ProcessOutputSucceeded,
                directTransformProbe.HardwareConfigureTransformSucceeded,
                directTransformProbe.HardwareProcessInputReached,
                directTransformProbe.HardwareProcessInputSucceeded,
                directTransformProbe.HardwareProcessOutputReached,
                directTransformProbe.HardwareProcessOutputSucceeded,
                directTransformProbe.SoftwareConfigureTransformSucceeded,
                directTransformProbe.SoftwareProcessInputReached,
                directTransformProbe.SoftwareProcessInputSucceeded,
                directTransformProbe.SoftwareProcessOutputReached,
                directTransformProbe.SoftwareProcessOutputSucceeded,
                directTransformProbe.AcceptedBackend);
        }

        public string ToSummary()
        {
            return $"strategy={Strategy.ToString().ToLowerInvariant()},success={Success},stage={Stage},reason={Reason},hresult=0x{HResult:X8},sample_created={(SampleCreated ? 1 : 0)},sample_usable={(SampleUsable ? 1 : 0)},upload_succeeded={(UploadSucceeded ? 1 : 0)},write_sample_reached={(WriteSampleReached ? 1 : 0)},write_sample_succeeded={(WriteSampleSucceeded ? 1 : 0)},configure_transform_succeeded={(DirectConfigureTransformSucceeded ? 1 : 0)},process_input_reached={(DirectProcessInputReached ? 1 : 0)},process_input_succeeded={(DirectProcessInputSucceeded ? 1 : 0)},process_output_reached={(DirectProcessOutputReached ? 1 : 0)},process_output_succeeded={(DirectProcessOutputSucceeded ? 1 : 0)},hardware_configure_transform_succeeded={(HardwareConfigureTransformSucceeded ? 1 : 0)},hardware_process_input_reached={(HardwareProcessInputReached ? 1 : 0)},hardware_process_input_succeeded={(HardwareProcessInputSucceeded ? 1 : 0)},hardware_process_output_reached={(HardwareProcessOutputReached ? 1 : 0)},hardware_process_output_succeeded={(HardwareProcessOutputSucceeded ? 1 : 0)},software_configure_transform_succeeded={(SoftwareConfigureTransformSucceeded ? 1 : 0)},software_process_input_reached={(SoftwareProcessInputReached ? 1 : 0)},software_process_input_succeeded={(SoftwareProcessInputSucceeded ? 1 : 0)},software_process_output_reached={(SoftwareProcessOutputReached ? 1 : 0)},software_process_output_succeeded={(SoftwareProcessOutputSucceeded ? 1 : 0)},accepted_backend={DirectAcceptedBackend}";
        }
    }

    private readonly record struct DirectTransformProbeResult(
        bool Success,
        string Stage,
        Exception? Exception,
        string AttemptLabel,
        uint? ReportedD3D11BindFlags,
        bool ConfigureTransformSucceeded,
        bool ProcessInputReached,
        bool ProcessInputSucceeded,
        bool ProcessOutputReached,
        bool ProcessOutputSucceeded,
        bool HardwareConfigureTransformSucceeded,
        bool HardwareProcessInputReached,
        bool HardwareProcessInputSucceeded,
        bool HardwareProcessOutputReached,
        bool HardwareProcessOutputSucceeded,
        bool SoftwareConfigureTransformSucceeded,
        bool SoftwareProcessInputReached,
        bool SoftwareProcessInputSucceeded,
        bool SoftwareProcessOutputReached,
        bool SoftwareProcessOutputSucceeded,
        string AcceptedBackend,
        string Summary)
    {
        public static DirectTransformProbeResult NotRun =>
            new(
                false,
                "not_run",
                null,
                "none",
                null,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                "none",
                "status=not-run");

        public static DirectTransformProbeResult FromBackendResults(IReadOnlyList<DirectTransformBackendProbeResult> backendResults)
        {
            var hardware = FindBackendResult(backendResults, TransformProbeBackend.Hardware);
            var software = FindBackendResult(backendResults, TransformProbeBackend.Software);
            var acceptedBackend =
                software.ProcessOutputSucceeded || software.ProcessInputSucceeded ? "software" :
                hardware.ProcessOutputSucceeded || hardware.ProcessInputSucceeded ? "hardware" :
                "none";
            var winningResult = acceptedBackend == "software"
                ? software
                : acceptedBackend == "hardware"
                    ? hardware
                    : software.TransformAvailable
                        ? software
                        : hardware;

            return new DirectTransformProbeResult(
                software.ProcessOutputSucceeded || hardware.ProcessOutputSucceeded,
                winningResult.Stage,
                winningResult.Exception,
                winningResult.AttemptLabel,
                software.ReportedD3D11BindFlags ?? hardware.ReportedD3D11BindFlags,
                software.ConfigureTransformSucceeded || hardware.ConfigureTransformSucceeded,
                software.ProcessInputReached || hardware.ProcessInputReached,
                software.ProcessInputSucceeded || hardware.ProcessInputSucceeded,
                software.ProcessOutputReached || hardware.ProcessOutputReached,
                software.ProcessOutputSucceeded || hardware.ProcessOutputSucceeded,
                hardware.ConfigureTransformSucceeded,
                hardware.ProcessInputReached,
                hardware.ProcessInputSucceeded,
                hardware.ProcessOutputReached,
                hardware.ProcessOutputSucceeded,
                software.ConfigureTransformSucceeded,
                software.ProcessInputReached,
                software.ProcessInputSucceeded,
                software.ProcessOutputReached,
                software.ProcessOutputSucceeded,
                acceptedBackend,
                string.Join(" | ", backendResults.Select(static result => result.ToSummary())));
        }

        private static DirectTransformBackendProbeResult FindBackendResult(IReadOnlyList<DirectTransformBackendProbeResult> backendResults, TransformProbeBackend backend)
        {
            foreach (var result in backendResults)
            {
                if (result.Backend == backend)
                {
                    return result;
                }
            }

            return DirectTransformBackendProbeResult.Unavailable(backend);
        }

        public string ToSummary()
        {
            return $"attempt={AttemptLabel},success={(Success ? 1 : 0)},stage={Stage},reason={(Exception is null ? "ok" : Exception.GetType().Name)},hresult=0x{(Exception?.HResult ?? 0):X8},reported_bind_flags={(ReportedD3D11BindFlags.HasValue ? $"0x{ReportedD3D11BindFlags.Value:X8}" : "unset")},configure_transform_succeeded={(ConfigureTransformSucceeded ? 1 : 0)},process_input_reached={(ProcessInputReached ? 1 : 0)},process_input_succeeded={(ProcessInputSucceeded ? 1 : 0)},process_output_reached={(ProcessOutputReached ? 1 : 0)},process_output_succeeded={(ProcessOutputSucceeded ? 1 : 0)},hardware_configure_transform_succeeded={(HardwareConfigureTransformSucceeded ? 1 : 0)},hardware_process_input_reached={(HardwareProcessInputReached ? 1 : 0)},hardware_process_input_succeeded={(HardwareProcessInputSucceeded ? 1 : 0)},hardware_process_output_reached={(HardwareProcessOutputReached ? 1 : 0)},hardware_process_output_succeeded={(HardwareProcessOutputSucceeded ? 1 : 0)},software_configure_transform_succeeded={(SoftwareConfigureTransformSucceeded ? 1 : 0)},software_process_input_reached={(SoftwareProcessInputReached ? 1 : 0)},software_process_input_succeeded={(SoftwareProcessInputSucceeded ? 1 : 0)},software_process_output_reached={(SoftwareProcessOutputReached ? 1 : 0)},software_process_output_succeeded={(SoftwareProcessOutputSucceeded ? 1 : 0)},accepted_backend={AcceptedBackend}";
        }
    }

    private readonly record struct DirectTransformBackendProbeResult(
        TransformProbeBackend Backend,
        bool TransformAvailable,
        bool ConfigureTransformSucceeded,
        bool ProcessInputReached,
        bool ProcessInputSucceeded,
        bool ProcessOutputReached,
        bool ProcessOutputSucceeded,
        string AttemptLabel,
        uint? ReportedD3D11BindFlags,
        string Stage,
        Exception? Exception,
        string Summary)
    {
        public static DirectTransformBackendProbeResult Unavailable(TransformProbeBackend backend) =>
            new(
                backend,
                false,
                false,
                false,
                false,
                false,
                false,
                "none",
                null,
                "create_transform",
                new PlatformNotSupportedException($"Direct transform backend '{backend}' is unavailable."),
                string.Empty);

        public static DirectTransformBackendProbeResult Successful(
            TransformProbeBackend backend,
            string stage,
            string attemptLabel,
            uint? reportedD3D11BindFlags,
            bool processOutputReached,
            bool processOutputSucceeded) =>
            new(
                backend,
                true,
                true,
                true,
                true,
                processOutputReached,
                processOutputSucceeded,
                attemptLabel,
                reportedD3D11BindFlags,
                stage,
                null,
                string.Empty);

        public static DirectTransformBackendProbeResult Failed(
            TransformProbeBackend backend,
            string stage,
            Exception exception,
            string attemptLabel,
            uint? reportedD3D11BindFlags,
            bool configureTransformSucceeded,
            bool processInputReached,
            bool processInputSucceeded,
            bool processOutputReached,
            bool processOutputSucceeded) =>
            new(
                backend,
                true,
                configureTransformSucceeded,
                processInputReached,
                processInputSucceeded,
                processOutputReached,
                processOutputSucceeded,
                attemptLabel,
                reportedD3D11BindFlags,
                stage,
                exception,
                string.Empty);

        public string ToSummary()
        {
            return $"backend={Backend.ToString().ToLowerInvariant()},transform_available={(TransformAvailable ? 1 : 0)},attempt={AttemptLabel},stage={Stage},reason={(Exception is null ? "ok" : Exception.GetType().Name)},hresult=0x{(Exception?.HResult ?? 0):X8},reported_bind_flags={(ReportedD3D11BindFlags.HasValue ? $"0x{ReportedD3D11BindFlags.Value:X8}" : "unset")},configure_transform_succeeded={(ConfigureTransformSucceeded ? 1 : 0)},process_input_reached={(ProcessInputReached ? 1 : 0)},process_input_succeeded={(ProcessInputSucceeded ? 1 : 0)},process_output_reached={(ProcessOutputReached ? 1 : 0)},process_output_succeeded={(ProcessOutputSucceeded ? 1 : 0)}";
        }
    }

    private readonly record struct TransformConfigurationResult(
        EncoderConfiguration Configuration,
        uint? ReportedD3D11BindFlags,
        string LowDelayConfigApplied);

    private readonly record struct DirectTransformProbeAttempt(
        string Label,
        uint? BindFlagsOverride,
        bool IsLast);


    private enum TransformProbeBackend
    {
        Software,
        Hardware,
    }


}
