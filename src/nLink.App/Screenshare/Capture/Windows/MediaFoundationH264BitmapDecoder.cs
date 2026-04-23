using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed partial class MediaFoundationH264BitmapDecoder : IWindowsH264BitmapDecoder
{
    private const int MfTransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfTransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfTransformTypeNotSet = unchecked((int)0xC00D6D60);
    private const int MfNoMoreTypes = unchecked((int)0xC00D36B9);
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;
    private const uint ClsctxInprocServer = 0x1;
    private const uint MfVideoInterlaceProgressive = 2;
    private const uint MftOutputStreamWholeSamples = 0x00000001;
    private const uint MftOutputStreamSingleSamplePerBuffer = 0x00000002;
    private const uint MftOutputStreamFixedSampleSize = 0x00000004;
    private const uint MftOutputStreamProvidesSamples = 0x00000100;
    private const uint MftOutputStreamCanProvideSamples = 0x00000200;
    private const uint MftInputStatusAcceptData = 0x00000001;
    private const uint MftOutputStatusSampleReady = 0x00000001;
    private const uint FourCcNv12 = 0x3231564E;
    private const uint FourCcYuy2 = 0x32595559;
    private const ushort VariantTypeUi4 = 19;
    private const long HnsPerSecond = 10_000_000;
    private const long DefaultInputSampleDurationHns = HnsPerSecond / 30;
    private const string H264Encoding = "h264";
    private const int InitialOutputProbeFrameWindow = 3;
    private const int OutputProbeFrameLimit = 7;
    private const int HelperRemoteArtifactFrameLimit = OutputProbeFrameLimit;
    private const int HelperRemoteArtifactNeedMoreInputThreshold = OutputProbeFrameLimit;
    private static readonly int[] NeedMoreInputSummaryThresholds = [1, 3, 10, 25];

    private static readonly Guid ClsidCmsH264DecoderMft = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    private static readonly Guid MftCategoryVideoDecoder = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidICodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatArgb32 = new("00000015-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatYuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtAllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MfMtFixedSizeSamples = new("b8ebefaf-b718-4e04-b0a9-116775e3321b");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtMpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MftFriendlyNameAttribute = new("314ffbae-5b41-4c95-9c19-4e7d586f2d9a");
    private static readonly Guid MfSampleExtensionCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid MfSampleExtensionDiscontinuity = new("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a");
    private readonly object sync = new();
    private readonly bool mediaFoundationLeaseHeld;
    private readonly string logRole;
    private readonly int decoderInstanceId;
    private bool disposed;
    private DecoderTransformState? activeTransformState;
    private DecoderConfiguration? configuration;
    private int outputWidth;
    private int outputHeight;
    private int outputStride;
    private bool outputRequiresOpaqueAlpha;
    private Guid outputSubtype;
    private long nextInputSampleTimeHns;
    private bool inputNormalizationLogged;

    private MediaFoundationH264BitmapDecoder(bool mediaFoundationLeaseHeld, string logRole, int decoderInstanceId)
    {
        this.mediaFoundationLeaseHeld = mediaFoundationLeaseHeld;
        this.logRole = string.IsNullOrWhiteSpace(logRole) ? "viewer" : logRole.Trim();
        this.decoderInstanceId = decoderInstanceId;
    }

    public bool IsSupported => !disposed;

    public static IWindowsH264BitmapDecoder? TryCreate(string logRole = "viewer")
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!MediaFoundationRuntime.TryAcquire())
        {
            return null;
        }

        IMFTransform? probeTransform = null;
        var decoderId = Interlocked.Increment(ref nextDecoderInstanceId);
        debugLastCreatedRole = string.IsNullOrWhiteSpace(logRole) ? "viewer" : logRole.Trim();
        try
        {
            var defaultBackend = ResolveDefaultDecoderBackend();
            if (!TryCreateTransform(defaultBackend, out probeTransform, out var activationSource, out var friendlyName, out _, out _)
                || probeTransform is null)
            {
                if (defaultBackend != DecoderBackendKind.SoftwareFixedClsid &&
                    !TryCreateTransform(DecoderBackendKind.SoftwareFixedClsid, out probeTransform, out activationSource, out friendlyName, out _, out _))
                {
                    throw new PlatformNotSupportedException("No Media Foundation H.264 decoder transform is available.");
                }
            }
            return new MediaFoundationH264BitmapDecoder(mediaFoundationLeaseHeld: true, debugLastCreatedRole, decoderId);
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_decoder_probe_failed",
                $"reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}",
                debugLastCreatedRole,
                decoderId,
                0);
            if (probeTransform is not null)
            {
                ReleaseComObject(probeTransform);
            }

            MediaFoundationRuntime.Release();
            return null;
        }
        finally
        {
            if (probeTransform is not null)
            {
                ReleaseComObject(probeTransform);
            }
        }
    }

    public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!string.Equals(config.Encoding?.Trim(), H264Encoding, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Media Foundation H.264 decoder cannot configure encoding '{config.Encoding}'.");
        }

        lock (sync)
        {
            var normalizedConfigData = config.DecoderConfigData ?? Array.Empty<byte>();
            var nextConfiguration = new DecoderConfiguration(
                config.StreamEpoch,
                config.CodecProfile ?? string.Empty,
                normalizedConfigData);
            if (configuration is not null &&
                configuration.StreamEpoch == config.StreamEpoch &&
                string.Equals(configuration.CodecProfile, config.CodecProfile ?? string.Empty, StringComparison.Ordinal) &&
                ByteArraysEqual(configuration.DecoderConfigData, normalizedConfigData))
            {
                return;
            }

            ResetDecoderState("reconfigure");

            var backend = ResolveDefaultDecoderBackend();
            var attributeProfile = ResolveDefaultDecoderAttributeProfile();
            activeTransformState = CreateConfiguredTransform(
                nextConfiguration,
                backend,
                attributeProfile,
                ResolveDefaultOutputSubtypeProbeKind());
            configuration = nextConfiguration;
            outputWidth = 0;
            outputHeight = 0;
            outputStride = 0;
            outputRequiresOpaqueAlpha = false;
            outputSubtype = Guid.Empty;
            nextInputSampleTimeHns = 0;
            inputNormalizationLogged = false;

            LogLifecycle(
                "screenshare_h264_decoder_configured",
                config.StreamEpoch,
                $"profile={Sanitize(config.CodecProfile)}; config_bytes={normalizedConfigData.Length}");
            LogLifecycle(
                normalizedConfigData.Length > 0
                    ? "screenshare_h264_decoder_bootstrap_config_present"
                    : "screenshare_h264_decoder_bootstrap_config_missing",
                config.StreamEpoch,
                $"profile={Sanitize(config.CodecProfile)}; config_bytes={normalizedConfigData.Length}");
        }
    }

    public void Reset()
    {
        if (disposed)
        {
            return;
        }

        lock (sync)
        {
            ResetDecoderState("manual_reset");
        }
    }

    public Bitmap Decode(EncodedFrameDecodeRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Encoding);

        if (!string.Equals(request.Encoding.Trim(), H264Encoding, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Media Foundation H.264 decoder cannot decode '{request.Encoding}'.");
        }

        lock (sync)
        {
            if (configuration is null || activeTransformState is null)
            {
                throw new InvalidOperationException("Media Foundation H.264 decoder is not configured.");
            }

            if (request.StreamEpoch > 0 && configuration.StreamEpoch > 0 && request.StreamEpoch != configuration.StreamEpoch)
            {
                ResetDecoderState("unexpected_epoch");
                throw new InvalidOperationException($"Media Foundation H.264 decoder received frame for unexpected epoch {request.StreamEpoch}.");
            }

            try
            {
                var activeTransform = activeTransformState;
                var sampleTimeHns = nextInputSampleTimeHns;
                var normalizedBytes = NormalizeInputBytesForDecoder(request.EncodedFrameBytes, configuration, request.StreamEpoch);
                RecordNormalizedInputForDiagnostics(normalizedBytes);
                RecordInputAccepted(request.StreamEpoch, normalizedBytes.Length, sampleTimeHns, DefaultInputSampleDurationHns);
                return DecodeWithStrategySelection(activeTransform, request, normalizedBytes, sampleTimeHns);
            }
            catch (H264DecoderNeedsMoreInputException ex)
            {
                RecordNeedMoreInput(request.StreamEpoch, normalizedInputBytes: lastNormalizedInputBytesThisEpoch > 0 ? lastNormalizedInputBytesThisEpoch : request.EncodedFrameBytes.Length, ex);
                throw;
            }
            catch (Exception ex)
            {
                LogLifecycle(
                    "screenshare_h264_decoder_decode_failed",
                    request.StreamEpoch,
                    $"bytes={request.EncodedFrameBytes.Length}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                ResetDecoderState("decode_failure");
                throw;
            }
        }
    }

    private Bitmap DecodeWithStrategySelection(
        DecoderTransformState activeTransformState,
        EncodedFrameDecodeRequest request,
        ReadOnlyMemory<byte> normalizedBytes,
        long sampleTimeHns)
    {
        var streamEpoch = request.StreamEpoch;
        var isFirstFrameOfEpoch = framesSubmittedThisEpoch == 1;
        var fallbackStrategy = GetFallbackInputSampleStrategyForFrame(request.IsKeyFrame, isFirstFrameOfEpoch);
        var probeOutputContract = !HasPreferredOutputCombination();
        var probeStrategies = probeOutputContract
            ? EnumerateFirstFrameInputSampleStrategies()
            : isFirstFrameOfEpoch && preferredInputSampleStrategy == InputSampleStrategyKind.Unknown
            ? EnumerateFirstFrameInputSampleStrategies()
            : [fallbackStrategy];
        var strategyList = new List<InputSampleStrategyKind>(probeStrategies);
        Exception? lastProbeFailure = null;
        if (strategyList.Count == 0)
        {
            strategyList.Add(fallbackStrategy);
        }

        for (var index = 0; index < strategyList.Count; index++)
        {
            var baseStrategy = strategyList[index];
            var effectiveContract = ResolveInputSampleContract(baseStrategy, request.IsKeyFrame, isFirstFrameOfEpoch);
            var usePrimaryTransform = !probeOutputContract &&
                preferredOutputRetrievalMode != OutputRetrievalMode.EndOfStreamDrain &&
                index == strategyList.Count - 1;
            DecoderTransformState? attemptTransformState = usePrimaryTransform ? activeTransformState : null;
            var replacePrimaryTransform = false;
            var outputTypeChanged = usePrimaryTransform
                ? !activeTransformState.OutputTypeConfigured
                : true;
            try
            {
                if (!usePrimaryTransform)
                {
                    attemptTransformState = CreateConfiguredTransform(
                        configuration!,
                        ResolveDefaultDecoderBackend(),
                        ResolveDefaultDecoderAttributeProfile(),
                        ResolveDefaultOutputSubtypeProbeKind());
                }

                var bitmap = DecodeSingleSampleWithStrategy(
                    attemptTransformState!,
                    request,
                    normalizedBytes,
                    sampleTimeHns,
                    baseStrategy,
                    effectiveContract,
                    outputTypeChanged);
                preferredInputSampleStrategy = baseStrategy;
                debugPreferredInputSampleStrategy = FormatInputSampleStrategy(baseStrategy);
                inputSampleStrategyThisEpoch = baseStrategy;
                if (!usePrimaryTransform)
                {
                    replacePrimaryTransform = true;
                }

                if (replacePrimaryTransform)
                {
                    ReplaceActiveTransform(attemptTransformState!);
                    attemptTransformState = null;
                }

                return bitmap;
            }
            catch (H264DecoderNeedsMoreInputException) when (!usePrimaryTransform)
            {
                lastProbeFailure = null;
                LogLifecycle(
                    "screenshare_h264_decoder_strategy_probe_continuing",
                    streamEpoch,
                    $"strategy={FormatInputSampleStrategy(baseStrategy)}; reason=need_more_input");
                continue;
            }
            catch (Exception ex) when (!usePrimaryTransform)
            {
                lastProbeFailure = ex;
                LogLifecycle(
                    "screenshare_h264_decoder_strategy_probe_continuing",
                    streamEpoch,
                    $"strategy={FormatInputSampleStrategy(baseStrategy)}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                continue;
            }
            finally
            {
                if (!usePrimaryTransform && attemptTransformState is not null)
                {
                    ReleaseTransformState(attemptTransformState, flush: true);
                }
            }
        }

        if (lastProbeFailure is not null)
        {
            throw lastProbeFailure;
        }

        throw CreateNeedMoreInputException("Media Foundation H.264 decoder needs more input before it can produce a frame.");
    }

    private Bitmap DecodeSingleSampleWithStrategy(
        DecoderTransformState decoderTransformState,
        EncodedFrameDecodeRequest request,
        ReadOnlyMemory<byte> normalizedBytes,
        long sampleTimeHns,
        InputSampleStrategyKind baseStrategy,
        InputSampleContract contract,
        bool outputTypeChanged)
    {
        if (!HasPreferredOutputCombination())
        {
            BufferOutputProbeFrame(normalizedBytes, request.IsKeyFrame);
            var probeResult = ProbeOutputMatrix(
                decoderTransformState,
                request.StreamEpoch,
                baseStrategy,
                outputTypeChanged);
            if (probeResult is not null)
            {
                ReplaceActiveTransform(probeResult.TransformState);
                var bitmap = probeResult.Bitmap;
                RecordDecodeSucceeded(request.StreamEpoch, outputTypeChanged: true, bitmap);
                return bitmap;
            }

            if (outputProbeFrames.Count < OutputProbeFrameLimit)
            {
                throw CreateNeedMoreInputException("Media Foundation H.264 decoder needs more buffered frames before it can determine a working output contract.");
            }

            throw new InvalidOperationException("Media Foundation H.264 decoder did not produce output for any output-sample provider or retrieval mode combination.");
        }

        var combination = new OutputContractCombination(preferredOutputSampleShape, preferredOutputRetrievalMode);
        var bitmapWithSelectedCombination = DecodeWithSelectedOutputCombination(
            decoderTransformState,
            request.StreamEpoch,
            normalizedBytes,
            sampleTimeHns,
            baseStrategy,
            contract,
            request.IsKeyFrame,
            combination,
            TransformStartupSequenceKind.TypesBeforeStart,
            outputTypeChanged);
        RecordDecodeSucceeded(request.StreamEpoch, outputTypeChanged, bitmapWithSelectedCombination);
        return bitmapWithSelectedCombination;
    }

    private Bitmap DecodeWithSelectedOutputCombination(
        DecoderTransformState decoderTransformState,
        long streamEpoch,
        ReadOnlyMemory<byte> normalizedBytes,
        long sampleTimeHns,
        InputSampleStrategyKind baseStrategy,
        InputSampleContract contract,
        bool isKeyFrame,
        OutputContractCombination combination,
        TransformStartupSequenceKind startupSequence,
        bool outputTypeChanged)
    {
        EnsureTransformReadyForInput(decoderTransformState, streamEpoch, startupSequence);
        var inputSample = CreateInputSample(normalizedBytes, sampleTimeHns, contract);
        try
        {
            inputSampleStrategyThisEpoch = baseStrategy;
            LogInputSampleStrategySelected(streamEpoch, baseStrategy, inputSample, contract, isKeyFrame);
            processInputReachedThisEpoch = true;
            LogLifecycle(
                "screenshare_h264_decoder_process_input_reached",
                streamEpoch,
                $"strategy={FormatInputSampleStrategy(baseStrategy)}; normalized_input_bytes={normalizedBytes.Length}; output_combination={FormatOutputCombination(combination)}");

            int inputHr;
            try
            {
                inputHr = decoderTransformState.Transform.ProcessInput(0, inputSample, 0);
            }
            catch (COMException ex) when (ex.HResult == MfTransformNeedMoreInput)
            {
                inputHr = MfTransformNeedMoreInput;
            }

            if (inputHr == MfTransformNeedMoreInput)
            {
                throw CreateNeedMoreInputException("Media Foundation H.264 decoder needs more input before it can produce a frame.");
            }

            Marshal.ThrowExceptionForHR(inputHr);
            processInputSucceededThisEpoch = true;
            RecordProcessInputSucceeded(streamEpoch, normalizedBytes.Length, baseStrategy, contract);
            nextInputSampleTimeHns = checked(sampleTimeHns + DefaultInputSampleDurationHns);
            var bitmap = DrainOutput(decoderTransformState, streamEpoch, combination);
            return bitmap;
        }
        finally
        {
            ReleaseComObject(inputSample);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ResetDecoderState("dispose");
        }

        if (mediaFoundationLeaseHeld)
        {
            MediaFoundationRuntime.Release();
        }
    }

    private static IMFTransform CreateTransform()
    {
        if (TryCreateTransform(ResolveDefaultDecoderBackend(), out var transform, out _, out _, out _, out _) &&
            transform is not null)
        {
            return transform;
        }

        if (TryCreateTransform(DecoderBackendKind.SoftwareFixedClsid, out transform, out _, out _, out _, out _) &&
            transform is not null)
        {
            return transform;
        }

        throw new PlatformNotSupportedException("No Media Foundation H.264 decoder transform is available.");
    }

    private DecoderTransformState CreateConfiguredTransform(
        DecoderConfiguration decoderConfiguration,
        DecoderBackendKind backendKind,
        DecoderAttributeProfileKind attributeProfile,
        OutputSubtypeProbeKind outputSubtypeProbeKind)
    {
        if (!TryCreateTransform(
                backendKind,
                out var decoderTransform,
                out var activationSource,
                out var friendlyName,
                out var activationFailureStage,
                out var activationFailureHresult) ||
            decoderTransform is null)
        {
            decoderBackendActivationFailureThisEpoch = true;
            throw new InvalidOperationException(
                $"Media Foundation H.264 decoder could not activate backend '{FormatDecoderBackend(backendKind)}' at stage '{activationFailureStage}' (0x{activationFailureHresult:X8}).");
        }

        var transformState = CreateTransformState(
            decoderTransform,
            backendKind,
            attributeProfile,
            activationSource,
            friendlyName,
            outputSubtypeProbeKind);
        if (backendKind == DecoderBackendKind.HardwareEnumFirst)
        {
            hardwareDecoderAvailableThisEpoch = true;
        }

        try
        {
            ApplyDecoderAttributeProfile(transformState, decoderConfiguration.StreamEpoch);
            ConfigureTransformInput(transformState, decoderConfiguration);
            return transformState;
        }
        catch
        {
            ReleaseTransformState(transformState, flush: false);
            throw;
        }
    }

    private void ConfigureTransformInput(
        DecoderTransformState decoderTransformState,
        DecoderConfiguration decoderConfiguration)
    {
        var inputType = CreateInputMediaType(decoderConfiguration, decoderTransformState.AttributeProfile, decoderTransformState);
        try
        {
            Marshal.ThrowExceptionForHR(decoderTransformState.Transform.SetInputType(0, inputType, 0));
            decoderTransformState.InputTypeConfigured = true;
            TryCaptureInputStreamAttributes(decoderTransformState);
            LogLifecycle(
                "screenshare_h264_decoder_input_ready",
                decoderConfiguration.StreamEpoch,
                $"config_bytes={decoderConfiguration.DecoderConfigData.Length}; expected_width={decoderConfiguration.ExpectedCodedWidth}; expected_height={decoderConfiguration.ExpectedCodedHeight}; transform_id={decoderTransformState.TransformId}");
            LogLifecycle(
                "screenshare_h264_decoder_input_stream_attributes",
                decoderConfiguration.StreamEpoch,
                $"backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; transform_id={decoderTransformState.TransformId}; snapshot={Sanitize(decoderTransformState.InputStreamAttributesSnapshot)}");
        }
        finally
        {
            ReleaseComObject(inputType);
        }
    }

    private DecoderTransformState CreateTransformState(
        IMFTransform decoderTransform,
        DecoderBackendKind backendKind,
        DecoderAttributeProfileKind attributeProfile,
        string activationSource,
        string friendlyName,
        OutputSubtypeProbeKind outputSubtypeProbeKind)
        => new(
            decoderTransform,
            Interlocked.Increment(ref nextTransformInstanceId),
            backendKind,
            attributeProfile,
            activationSource,
            friendlyName,
            outputSubtypeProbeKind);

    private void ReplaceActiveTransform(DecoderTransformState replacementTransformState)
    {
        if (ReferenceEquals(activeTransformState, replacementTransformState))
        {
            return;
        }

        var previous = activeTransformState;
        activeTransformState = replacementTransformState;
        SyncDecoderOutputMetadata(replacementTransformState);
        ReleaseTransformState(previous, flush: true);
    }

    private static void ReleaseTransformState(DecoderTransformState? transformState, bool flush)
    {
        if (transformState is null)
        {
            return;
        }

        if (flush)
        {
            try
            {
                transformState.Transform.ProcessMessage(MftMessageCommandFlush, IntPtr.Zero);
            }
            catch
            {
            }
        }

        ReleaseComObject(transformState.Transform);
    }

    private static DecoderBackendKind ResolveDefaultDecoderBackend()
        => preferredDecoderBackend != DecoderBackendKind.Unknown
            ? preferredDecoderBackend
            : DecoderBackendKind.SoftwareFixedClsid;

    private static DecoderAttributeProfileKind ResolveDefaultDecoderAttributeProfile()
        => preferredDecoderAttributeProfile != DecoderAttributeProfileKind.Unknown
            ? preferredDecoderAttributeProfile
            : DecoderAttributeProfileKind.LowLatency;

    private static OutputSubtypeProbeKind ResolveDefaultOutputSubtypeProbeKind()
        => preferredOutputSubtypeProbeKind != OutputSubtypeProbeKind.Unknown
            ? preferredOutputSubtypeProbeKind
            : OutputSubtypeProbeKind.NativeAdvertisedFirstSupported;

    private static bool TryCreateTransform(
        DecoderBackendKind backendKind,
        out IMFTransform? transform,
        out string activationSource,
        out string friendlyName,
        out string failureStage,
        out int failureHresult)
        => backendKind switch
        {
            DecoderBackendKind.HardwareEnumFirst => TryCreateHardwareTransform(
                out transform,
                out activationSource,
                out friendlyName,
                out failureStage,
                out failureHresult),
            _ => TryCreateSoftwareTransform(
                out transform,
                out activationSource,
                out friendlyName,
                out failureStage,
                out failureHresult),
        };

    private static bool TryCreateSoftwareTransform(
        out IMFTransform? transform,
        out string activationSource,
        out string friendlyName,
        out string failureStage,
        out int failureHresult)
    {
        transform = null;
        activationSource = "fixed_clsid";
        friendlyName = "unknown";
        failureStage = "(none)";
        failureHresult = 0;
        var clsid = ClsidCmsH264DecoderMft;
        var iid = IidImfTransform;
        try
        {
            failureStage = "co_create_instance";
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var transformPtr);
            failureHresult = hr;
            if (hr < 0 || transformPtr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr);
                friendlyName = "CMSH264DecoderMFT";
                failureStage = "(none)";
                failureHresult = 0;
                return true;
            }
            finally
            {
                Marshal.Release(transformPtr);
            }
        }
        catch (COMException ex)
        {
            failureStage = "co_create_instance";
            failureHresult = ex.HResult;
            return false;
        }
    }

    private static bool TryCreateHardwareTransform(
        out IMFTransform? transform,
        out string activationSource,
        out string friendlyName,
        out string failureStage,
        out int failureHresult)
    {
        transform = null;
        activationSource = "mft_enum_ex";
        friendlyName = "unknown";
        failureStage = "(none)";
        failureHresult = 0;
        IntPtr activationArray = IntPtr.Zero;
        IntPtr inputPtr = IntPtr.Zero;
        try
        {
            var inputType = new MftRegisterTypeInfo
            {
                GuidMajorType = MfMediaTypeVideo,
                GuidSubtype = MfVideoFormatH264,
            };

            inputPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MftRegisterTypeInfo>());
            Marshal.StructureToPtr(inputType, inputPtr, false);

            var category = MftCategoryVideoDecoder;
            failureStage = "mft_enum_ex";
            var hr = MFTEnumEx(
                ref category,
                MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                inputPtr,
                IntPtr.Zero,
                out activationArray,
                out var count);
            failureHresult = hr;
            if (hr < 0 || count <= 0 || activationArray == IntPtr.Zero)
            {
                return false;
            }

            var activationPtr = Marshal.ReadIntPtr(activationArray);
            if (activationPtr == IntPtr.Zero)
            {
                failureStage = "activation_ptr";
                failureHresult = unchecked((int)0x80004005);
                return false;
            }

            var activation = (IMFActivate)Marshal.GetObjectForIUnknown(activationPtr);
            Marshal.Release(activationPtr);
            try
            {
                friendlyName = TryGetFriendlyName(activation) ?? "unknown";
                var iid = IidImfTransform;
                failureStage = "activate_object";
                var activateHr = activation.ActivateObject(ref iid, out var transformPtr);
                failureHresult = activateHr;
                if (activateHr < 0 || transformPtr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr);
                    failureStage = "(none)";
                    failureHresult = 0;
                    return true;
                }
                finally
                {
                    Marshal.Release(transformPtr);
                }
            }
            finally
            {
                ReleaseComObject(activation);
            }
        }
        catch (COMException ex)
        {
            failureHresult = ex.HResult;
            return false;
        }
        finally
        {
            if (activationArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(activationArray);
            }

            if (inputPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPtr);
            }
        }
    }

    private static string? TryGetFriendlyName(IMFActivate activation)
    {
        try
        {
            var key = MftFriendlyNameAttribute;
            return activation.GetAllocatedString(ref key, out var value, out _) >= 0
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private Bitmap DrainOutput(DecoderTransformState decoderTransformState, long streamEpoch, OutputContractCombination combination)
    {
        var outputResult = ProcessOutputWithCombination(
            decoderTransformState,
            streamEpoch,
            combination,
            logLifecycleEvents: true);
        RememberOutputProcessingResult(decoderTransformState, combination, outputResult);
        if (outputResult.Bitmap is not null)
        {
            return outputResult.Bitmap;
        }

        if (outputResult.NeedMoreInput)
        {
            throw CreateNeedMoreInputException("Media Foundation H.264 decoder needs more input before it can output a frame.");
        }

        if (outputResult.ProviderContractFailure)
        {
            outputProviderContractFailureThisEpoch = true;
        }

        if (outputResult.RetrievalContractFailure)
        {
        }

        if (outputResult.Failure is not null)
        {
            throw outputResult.Failure;
        }

        throw new InvalidOperationException($"Media Foundation H.264 decoder did not produce output for combination '{FormatOutputCombination(combination)}'.");
    }

    private OutputProcessingResult ProcessOutputWithCombination(
        DecoderTransformState decoderTransformState,
        long streamEpoch,
        OutputContractCombination combination,
        bool logLifecycleEvents)
    {
        var streamChangeObserved = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            EnsureOutputTypeConfigured(decoderTransformState);
            var decoderTransform = decoderTransformState.Transform;

            var stage = "get_output_stream_info";
            Marshal.ThrowExceptionForHR(decoderTransform.GetOutputStreamInfo(0, out var outputInfo));
            var outputStatusHresult = TryGetOutputStatus(decoderTransform, out var outputStatusFlags);
            var inputStatusHresult = TryGetInputStatus(decoderTransform, out var inputStatusFlags);
            IMFSample? outputSample = null;
            IMFSample? actualSample = null;
            IntPtr outputSamplePtr = IntPtr.Zero;
            IntPtr actualSamplePtr = IntPtr.Zero;
            var callerProvidesSample = (outputInfo.DwFlags & MftOutputStreamProvidesSamples) == 0 &&
                                       combination.Shape != OutputSampleShapeKind.NullSampleDiagnostic;
            if (callerProvidesSample)
            {
                stage = "create_output_sample";
                outputSample = CreateOutputSample(decoderTransformState, outputInfo, streamEpoch, combination.Shape);
                outputSampleShapeThisEpoch = combination.Shape;
                outputSampleProviderThisEpoch = combination.Provider;
                outputRetrievalModeThisEpoch = combination.RetrievalMode;
                outputSampleBufferSizeThisEpoch = decoderTransformState.OutputBufferSize;
                outputSampleBufferAlignmentThisEpoch = decoderTransformState.OutputBufferAlignment;
                outputSamplePtr = Marshal.GetIUnknownForObject(outputSample);
            }

            var outputBuffers = new[]
            {
                new MftOutputDataBuffer
                {
                    DwStreamId = 0,
                    PSample = outputSamplePtr,
                    DwStatus = 0,
                    PEvents = IntPtr.Zero,
                },
            };

            try
            {
                stage = "process_output";
                processOutputReachedThisEpoch = true;
                if (logLifecycleEvents && !loggedProcessOutputObservedThisEpoch)
                {
                    LogLifecycle(
                        "screenshare_h264_decoder_process_output_reached",
                        streamEpoch,
                        $"attempt={attempt + 1}; output_combination={FormatOutputCombination(combination)}; caller_provides_sample={(callerProvidesSample ? 1 : 0)}; output_flags={FormatOutputStreamFlags(outputInfo.DwFlags)}; strategy={FormatInputSampleStrategy(inputSampleStrategyThisEpoch)}");
                }

                int hr;
                try
                {
                    hr = decoderTransform.ProcessOutput(0, 1, outputBuffers, out _);
                }
                catch (COMException ex) when (ex.HResult == MfTransformStreamChange || ex.HResult == MfTransformNeedMoreInput)
                {
                    hr = ex.HResult;
                }

                if (hr == MfTransformStreamChange)
                {
                    streamChangeObserved = true;
                    ResetTransformOutputTypeState(decoderTransformState);
                    if (logLifecycleEvents)
                    {
                        LogLifecycle(
                            "screenshare_h264_decoder_stream_change",
                            streamEpoch,
                            $"attempt={attempt + 1}; output_combination={FormatOutputCombination(combination)}");
                    }

                    continue;
                }

                if (hr == MfTransformNeedMoreInput)
                {
                    return ApplyTransformStartupState(new OutputProcessingResult(
                        TransformId: decoderTransformState.TransformId,
                        OutputTypeConfiguredOnTransform: decoderTransformState.OutputTypeConfigured,
                        OutputTypeVerifiedOnTransform: decoderTransformState.OutputTypeVerified,
                        SetOutputTypeHresult: decoderTransformState.SetOutputTypeHresult,
                        GetOutputCurrentTypeHresult: decoderTransformState.GetOutputCurrentTypeHresult,
                        OutputStatusAfterConfigurationHresult: decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                        OutputStatusAfterConfigurationFlags: decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                        Bitmap: null,
                        ProcessOutputAttempts: attempt + 1,
                        StreamChangeObserved: streamChangeObserved,
                        CallerProvidedSample: callerProvidesSample,
                    SampleOrigin: OutputSampleOrigin.None,
                    OutputFlags: outputInfo.DwFlags,
                    OutputDataBufferStatus: outputBuffers[0].DwStatus,
                    InputStatusFlags: inputStatusFlags,
                    InputStatusHresult: inputStatusHresult,
                    OutputStatusFlags: outputStatusFlags,
                    OutputStatusHresult: outputStatusHresult,
                    ProcessOutputHresult: hr,
                    FailureStage: "process_output",
                    Failure: null,
                    NeedMoreInput: true,
                    SuccessWithoutSample: false,
                    OutputReadyWithoutSample: (outputStatusHresult >= 0 && (outputStatusFlags & MftOutputStatusSampleReady) != 0),
                    ProviderContractFailure: false,
                    RetrievalContractFailure: false), decoderTransformState);
                }

                Marshal.ThrowExceptionForHR(hr);

                stage = "resolve_output_sample";
                var sampleOrigin = OutputSampleOrigin.None;
                if (callerProvidesSample && (outputBuffers[0].PSample == IntPtr.Zero || outputBuffers[0].PSample == outputSamplePtr))
                {
                    actualSample = outputSample;
                    outputSample = null;
                    outputBuffers[0].PSample = IntPtr.Zero;
                    sampleOrigin = OutputSampleOrigin.CallerSampleReused;
                }
                else if (outputBuffers[0].PSample != IntPtr.Zero)
                {
                    actualSamplePtr = outputBuffers[0].PSample;
                    actualSample = (IMFSample)Marshal.GetObjectForIUnknown(actualSamplePtr);
                    outputBuffers[0].PSample = IntPtr.Zero;
                    sampleOrigin = OutputSampleOrigin.MftReturnedDifferentSample;
                }

                if (actualSample is null)
                {
                    return ApplyTransformStartupState(new OutputProcessingResult(
                        TransformId: decoderTransformState.TransformId,
                        OutputTypeConfiguredOnTransform: decoderTransformState.OutputTypeConfigured,
                        OutputTypeVerifiedOnTransform: decoderTransformState.OutputTypeVerified,
                        SetOutputTypeHresult: decoderTransformState.SetOutputTypeHresult,
                        GetOutputCurrentTypeHresult: decoderTransformState.GetOutputCurrentTypeHresult,
                        OutputStatusAfterConfigurationHresult: decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                        OutputStatusAfterConfigurationFlags: decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                        Bitmap: null,
                        ProcessOutputAttempts: attempt + 1,
                        StreamChangeObserved: streamChangeObserved,
                        CallerProvidedSample: callerProvidesSample,
                        SampleOrigin: OutputSampleOrigin.None,
                        OutputFlags: outputInfo.DwFlags,
                        OutputDataBufferStatus: outputBuffers[0].DwStatus,
                        InputStatusFlags: inputStatusFlags,
                        InputStatusHresult: inputStatusHresult,
                        OutputStatusFlags: outputStatusFlags,
                        OutputStatusHresult: outputStatusHresult,
                        ProcessOutputHresult: hr,
                        FailureStage: "resolve_output_sample",
                        Failure: null,
                        NeedMoreInput: false,
                        SuccessWithoutSample: true,
                        OutputReadyWithoutSample: (outputStatusHresult >= 0 && (outputStatusFlags & MftOutputStatusSampleReady) != 0),
                        ProviderContractFailure: false,
                        RetrievalContractFailure: false), decoderTransformState);
                }

                actualSample.GetBufferCount(out var outputBufferCount);
                if (logLifecycleEvents)
                {
                    RecordProcessOutputObserved(
                        streamEpoch,
                        callerProvidesSample,
                        outputBufferCount,
                        outputInfo.DwFlags,
                        combination,
                        sampleOrigin);
                }

                stage = "create_bitmap";
                var bitmap = CreateBitmapFromSample(decoderTransformState, actualSample);
                return ApplyTransformStartupState(new OutputProcessingResult(
                    TransformId: decoderTransformState.TransformId,
                    OutputTypeConfiguredOnTransform: decoderTransformState.OutputTypeConfigured,
                    OutputTypeVerifiedOnTransform: decoderTransformState.OutputTypeVerified,
                    SetOutputTypeHresult: decoderTransformState.SetOutputTypeHresult,
                    GetOutputCurrentTypeHresult: decoderTransformState.GetOutputCurrentTypeHresult,
                    OutputStatusAfterConfigurationHresult: decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                    OutputStatusAfterConfigurationFlags: decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                    Bitmap: bitmap,
                    ProcessOutputAttempts: attempt + 1,
                    StreamChangeObserved: streamChangeObserved,
                    CallerProvidedSample: callerProvidesSample,
                    SampleOrigin: sampleOrigin,
                    OutputFlags: outputInfo.DwFlags,
                    OutputDataBufferStatus: outputBuffers[0].DwStatus,
                    InputStatusFlags: inputStatusFlags,
                    InputStatusHresult: inputStatusHresult,
                    OutputStatusFlags: outputStatusFlags,
                    OutputStatusHresult: outputStatusHresult,
                    ProcessOutputHresult: hr,
                    FailureStage: "(none)",
                    Failure: null,
                    NeedMoreInput: false,
                    SuccessWithoutSample: false,
                    OutputReadyWithoutSample: false,
                    ProviderContractFailure: false,
                    RetrievalContractFailure: false), decoderTransformState);
            }
            catch (Exception ex)
            {
                if (logLifecycleEvents)
                {
                    LogLifecycle(
                        "screenshare_h264_decoder_output_stage_failed",
                        streamEpoch,
                        $"attempt={attempt + 1}; output_combination={FormatOutputCombination(combination)}; stage={stage}; caller_provides_sample={(callerProvidesSample ? 1 : 0)}; output_flags={FormatOutputStreamFlags(outputInfo.DwFlags)}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                }

                return ApplyTransformStartupState(new OutputProcessingResult(
                    TransformId: decoderTransformState.TransformId,
                    OutputTypeConfiguredOnTransform: decoderTransformState.OutputTypeConfigured,
                    OutputTypeVerifiedOnTransform: decoderTransformState.OutputTypeVerified,
                    SetOutputTypeHresult: decoderTransformState.SetOutputTypeHresult,
                    GetOutputCurrentTypeHresult: decoderTransformState.GetOutputCurrentTypeHresult,
                    OutputStatusAfterConfigurationHresult: decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                    OutputStatusAfterConfigurationFlags: decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                    Bitmap: null,
                    ProcessOutputAttempts: attempt + 1,
                    StreamChangeObserved: streamChangeObserved,
                    CallerProvidedSample: callerProvidesSample,
                    SampleOrigin: OutputSampleOrigin.None,
                    OutputFlags: outputInfo.DwFlags,
                    OutputDataBufferStatus: outputBuffers[0].DwStatus,
                    InputStatusFlags: inputStatusFlags,
                    InputStatusHresult: inputStatusHresult,
                    OutputStatusFlags: outputStatusFlags,
                    OutputStatusHresult: outputStatusHresult,
                    ProcessOutputHresult: ex.HResult,
                    FailureStage: stage,
                    Failure: ex,
                    NeedMoreInput: false,
                    SuccessWithoutSample: false,
                    OutputReadyWithoutSample: false,
                    ProviderContractFailure: string.Equals(stage, "create_output_sample", StringComparison.Ordinal),
                    RetrievalContractFailure: !string.Equals(stage, "create_output_sample", StringComparison.Ordinal)), decoderTransformState);
            }
            finally
            {
                if (outputBuffers[0].PEvents != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(outputBuffers[0].PEvents);
                }

                if (actualSample is not null)
                {
                    ReleaseComObject(actualSample);
                }

                if (actualSamplePtr != IntPtr.Zero)
                {
                    Marshal.Release(actualSamplePtr);
                }

                if (outputSample is not null)
                {
                    ReleaseComObject(outputSample);
                }

                if (outputSamplePtr != IntPtr.Zero)
                {
                    Marshal.Release(outputSamplePtr);
                }
            }
        }

        return ApplyTransformStartupState(new OutputProcessingResult(
            TransformId: decoderTransformState.TransformId,
            OutputTypeConfiguredOnTransform: decoderTransformState.OutputTypeConfigured,
            OutputTypeVerifiedOnTransform: decoderTransformState.OutputTypeVerified,
            SetOutputTypeHresult: decoderTransformState.SetOutputTypeHresult,
            GetOutputCurrentTypeHresult: decoderTransformState.GetOutputCurrentTypeHresult,
            OutputStatusAfterConfigurationHresult: decoderTransformState.GetOutputStatusAfterConfigurationHresult,
            OutputStatusAfterConfigurationFlags: decoderTransformState.GetOutputStatusAfterConfigurationFlags,
            Bitmap: null,
            ProcessOutputAttempts: 8,
            StreamChangeObserved: streamChangeObserved,
            CallerProvidedSample: false,
            SampleOrigin: OutputSampleOrigin.None,
            OutputFlags: 0,
            OutputDataBufferStatus: 0,
            InputStatusFlags: 0,
            InputStatusHresult: 0,
            OutputStatusFlags: 0,
            OutputStatusHresult: 0,
            ProcessOutputHresult: 0,
            FailureStage: "process_output_attempts_exhausted",
            Failure: null,
            NeedMoreInput: false,
            SuccessWithoutSample: false,
            OutputReadyWithoutSample: false,
            ProviderContractFailure: false,
            RetrievalContractFailure: true), decoderTransformState);
    }

    private static OutputProcessingResult ApplyTransformStartupState(OutputProcessingResult result, DecoderTransformState decoderTransformState)
        => result with
        {
            BackendKindOnTransform = decoderTransformState.BackendKind,
            AttributeProfileOnTransform = decoderTransformState.AttributeProfile,
            ActivationSourceOnTransform = decoderTransformState.ActivationSource,
            FriendlyNameOnTransform = decoderTransformState.FriendlyName,
            TransformAttributesSnapshotOnTransform = decoderTransformState.TransformAttributesSnapshot,
            TransformAttributesSnapshotBeforeProfileOnTransform = decoderTransformState.TransformAttributesSnapshotBeforeProfile,
            TransformAttributesSnapshotAfterProfileOnTransform = decoderTransformState.TransformAttributesSnapshotAfterProfile,
            InputStreamAttributesSnapshotOnTransform = decoderTransformState.InputStreamAttributesSnapshot,
            OutputStreamAttributesSnapshotOnTransform = decoderTransformState.OutputStreamAttributesSnapshot,
            OutputSubtypeProbeKindOnTransform = decoderTransformState.OutputSubtypeProbeKind,
            OutputSubtypeCandidateOnTransform = decoderTransformState.EffectiveOutputSubtypeCandidate,
            OutputSubtypeCandidateWasNativeAdvertisedOnTransform = decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised,
            LowLatencyRequestedOnTransform = decoderTransformState.LowLatencyRequested,
            LowLatencyAppliedOnTransform =
                decoderTransformState.LowLatencyAppliedToTransform ||
                decoderTransformState.LowLatencyAppliedToInputMediaType ||
                decoderTransformState.LowLatencyAppliedToOutputMediaType ||
                decoderTransformState.CodecApiLowLatencyApplied,
            TransformLowLatencyAppliedOnTransform = decoderTransformState.LowLatencyAppliedToTransform,
            TransformLowLatencyHresultOnTransform = decoderTransformState.TransformLowLatencyHresult,
            InputMediaTypeLowLatencyAppliedOnTransform = decoderTransformState.LowLatencyAppliedToInputMediaType,
            InputMediaTypeLowLatencyHresultOnTransform = decoderTransformState.InputMediaTypeLowLatencyHresult,
            OutputMediaTypeLowLatencyAppliedOnTransform = decoderTransformState.LowLatencyAppliedToOutputMediaType,
            OutputMediaTypeLowLatencyHresultOnTransform = decoderTransformState.OutputMediaTypeLowLatencyHresult,
            CodecApiAvailableOnTransform = decoderTransformState.CodecApiAvailable,
            CodecApiSupportedOnTransform = decoderTransformState.CodecApiSupported,
            CodecApiIsSupportedHresultOnTransform = decoderTransformState.CodecApiIsSupportedHresult,
            CodecApiModifiableOnTransform = decoderTransformState.CodecApiModifiable,
            CodecApiIsModifiableHresultOnTransform = decoderTransformState.CodecApiIsModifiableHresult,
            CodecApiLowLatencyAppliedOnTransform = decoderTransformState.CodecApiLowLatencyApplied,
            CodecApiSetValueHresultOnTransform = decoderTransformState.CodecApiSetValueHresult,
            InputTypeConfiguredOnTransform = decoderTransformState.InputTypeConfigured,
            BeginStreamingSentOnTransform = decoderTransformState.BeginStreamingSent,
            StartOfStreamSentOnTransform = decoderTransformState.StartOfStreamSent,
            BeginStreamingHresultOnTransform = decoderTransformState.BeginStreamingHresult,
            StartOfStreamHresultOnTransform = decoderTransformState.StartOfStreamHresult,
            StartupSequenceOnTransform = decoderTransformState.StartupSequence,
            StartupSequenceVerifiedOnTransform = decoderTransformState.StartupSequenceVerified,
            FullyStartedBeforeFirstInputOnTransform = decoderTransformState.FullyStartedBeforeFirstInput,
        };

    private static int TryGetInputStatus(IMFTransform decoderTransform, out uint flags)
    {
        flags = 0;
        try
        {
            return decoderTransform.GetInputStatus(0, out flags);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    private static int TryGetOutputStatus(IMFTransform decoderTransform, out uint flags)
    {
        flags = 0;
        try
        {
            return decoderTransform.GetOutputStatus(out flags);
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
    }

    private void EnsureOutputTypeConfigured(DecoderTransformState decoderTransformState)
    {
        if (decoderTransformState.OutputTypeConfigured)
        {
            SyncDecoderOutputMetadata(decoderTransformState);
            return;
        }

        IMFMediaType? chosenType = null;
        IMFMediaType? firstSupportedType = null;
        IMFMediaType? nv12Type = null;
        IMFMediaType? yuy2Type = null;
        IMFMediaType? currentType = null;
        Guid chosenSubtype = Guid.Empty;
        Guid firstSupportedSubtype = Guid.Empty;
        var sawAnyOutputType = false;
        var availableSubtypes = new List<string>();
        try
        {
            var typeIndex = 0u;
            while (true)
            {
                IMFMediaType? candidate = null;
                try
                {
                    int hr;
                    try
                    {
                        hr = decoderTransformState.Transform.GetOutputAvailableType(0, typeIndex, out candidate);
                    }
                    catch (COMException ex) when (ex.HResult == MfNoMoreTypes)
                    {
                        hr = MfNoMoreTypes;
                        candidate = null;
                    }

                    if (hr == MfNoMoreTypes)
                    {
                        break;
                    }

                    if (hr < 0 || candidate is null)
                    {
                        break;
                    }

                    sawAnyOutputType = true;
                    var subtypeKey = MfMtSubtype;
                    if (candidate.GetGUID(ref subtypeKey, out var subtype) < 0)
                    {
                        continue;
                    }

                    availableSubtypes.Add(FormatVideoSubtype(subtype));
                    if (firstSupportedType is null &&
                        (subtype == MfVideoFormatNv12 || subtype == MfVideoFormatYuy2))
                    {
                        firstSupportedType = candidate;
                        firstSupportedSubtype = subtype;
                        if (subtype == MfVideoFormatNv12 && nv12Type is null)
                        {
                            nv12Type = candidate;
                        }
                        else if (subtype == MfVideoFormatYuy2 && yuy2Type is null)
                        {
                            yuy2Type = candidate;
                        }

                        candidate = null;
                        continue;
                    }

                    if (subtype == MfVideoFormatNv12 && nv12Type is null)
                    {
                        nv12Type = candidate;
                        candidate = null;
                        continue;
                    }

                    if (subtype == MfVideoFormatYuy2 && yuy2Type is null)
                    {
                        yuy2Type = candidate;
                        candidate = null;
                        continue;
                    }
                }
                finally
                {
                    if (candidate is not null)
                    {
                        ReleaseComObject(candidate);
                    }
                }

                typeIndex++;
            }

            if (availableSubtypes.Count > 0)
            {
                LogLifecycle(
                    "screenshare_h264_decoder_output_types_seen",
                    configuration?.StreamEpoch ?? 0,
                    $"types={string.Join(",", availableSubtypes)}");
            }

            (chosenType, chosenSubtype, decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised) = decoderTransformState.OutputSubtypeProbeKind switch
            {
                OutputSubtypeProbeKind.NativeAdvertisedFirstSupported when firstSupportedType is not null
                    => (firstSupportedType, firstSupportedSubtype, true),
                OutputSubtypeProbeKind.ExplicitNv12 when nv12Type is not null
                    => (nv12Type, MfVideoFormatNv12, false),
                OutputSubtypeProbeKind.ExplicitYuy2 when yuy2Type is not null
                    => (yuy2Type, MfVideoFormatYuy2, false),
                _ => (null, Guid.Empty, false),
            };

            decoderTransformState.EffectiveOutputSubtypeCandidate = chosenSubtype;
            lastOutputSubtypeProbeKindThisEpoch = decoderTransformState.OutputSubtypeProbeKind;
            lastOutputSubtypeCandidateThisEpoch = FormatVideoSubtype(chosenSubtype);
            lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch = decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised;

            if (chosenType is null)
            {
                if (!sawAnyOutputType)
                {
                    LogLifecycle("screenshare_h264_decoder_output_type_pending", configuration?.StreamEpoch ?? 0, "reason=no_output_types_yet");
                    throw CreateNeedMoreInputException("Media Foundation H.264 decoder has not exposed an output type yet.");
                }

                throw new NotSupportedException(
                    $"Media Foundation H.264 decoder does not expose requested output subtype '{FormatOutputSubtypeProbeKind(decoderTransformState.OutputSubtypeProbeKind)}'. available={string.Join(",", availableSubtypes)}");
            }

            LogLifecycle(
                "screenshare_h264_decoder_output_subtype_selected",
                configuration?.StreamEpoch ?? 0,
                $"transform_id={decoderTransformState.TransformId}; probe={FormatOutputSubtypeProbeKind(decoderTransformState.OutputSubtypeProbeKind)}; subtype={FormatVideoSubtype(chosenSubtype)}; native_advertised={(decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised ? 1 : 0)}");

            var chosenMetadata = ReadOutputMetadata(chosenType, configuration);
            var expectedWidth = configuration?.ExpectedCodedWidth ?? 0;
            var expectedHeight = configuration?.ExpectedCodedHeight ?? 0;
            var mismatch = expectedWidth > 0 &&
                           expectedHeight > 0 &&
                           (chosenMetadata.Width != expectedWidth || chosenMetadata.Height != expectedHeight);
            if (mismatch)
            {
                LogLifecycle(
                    "screenshare_h264_decoder_output_type_mismatch",
                    configuration?.StreamEpoch ?? 0,
                    $"expected_width={expectedWidth}; expected_height={expectedHeight}; chosen_width={chosenMetadata.Width}; chosen_height={chosenMetadata.Height}; subtype={FormatVideoSubtype(chosenMetadata.Subtype)}; stride={chosenMetadata.Stride}");
                TryOverrideOutputFrameSize(chosenType, chosenMetadata.Subtype, expectedWidth, expectedHeight);
                chosenMetadata = ReadOutputMetadata(chosenType, configuration, preferExpectedDimensions: true);
            }

            if (decoderTransformState.AttributeProfile == DecoderAttributeProfileKind.LowLatency)
            {
                var lowLatencyKey = MfLowLatency;
                decoderTransformState.OutputMediaTypeLowLatencyHresult = chosenType.SetUINT32(ref lowLatencyKey, 1);
                decoderTransformState.LowLatencyAppliedToOutputMediaType = decoderTransformState.OutputMediaTypeLowLatencyHresult >= 0;
                decoderTransformState.AttributeProfileFailure |= decoderTransformState.OutputMediaTypeLowLatencyHresult < 0;
                if (yuy2Type is not null)
                {
                    try
                    {
                        yuy2Type.SetUINT32(ref lowLatencyKey, 1);
                    }
                    catch
                    {
                    }
                }
            }

            decoderTransformState.SetOutputTypeHresult = decoderTransformState.Transform.SetOutputType(0, chosenType, 0);
            Marshal.ThrowExceptionForHR(decoderTransformState.SetOutputTypeHresult);
            var configuredMetadata = ReadOutputMetadata(chosenType, configuration, preferExpectedDimensions: true);
            Marshal.ThrowExceptionForHR(decoderTransformState.Transform.GetOutputStreamInfo(0, out var outputInfo));

            try
            {
                decoderTransformState.GetOutputCurrentTypeHresult = decoderTransformState.Transform.GetOutputCurrentType(0, out currentType);
            }
            catch (COMException ex)
            {
                decoderTransformState.GetOutputCurrentTypeHresult = ex.HResult;
                currentType = null;
            }

            decoderTransformState.GetOutputStatusAfterConfigurationHresult = TryGetOutputStatus(decoderTransformState.Transform, out var outputStatusFlags);
            decoderTransformState.GetOutputStatusAfterConfigurationFlags = outputStatusFlags;
            decoderTransformState.OutputTypeConfigured = true;
            decoderTransformState.OutputTypeVerified = decoderTransformState.GetOutputCurrentTypeHresult >= 0 && currentType is not null;
            ApplyOutputMetadata(decoderTransformState, configuredMetadata, outputInfo);
            TryCaptureOutputStreamAttributes(decoderTransformState);
            SyncDecoderOutputMetadata(decoderTransformState);
            UpdateStartupSequenceVerification(decoderTransformState);
            outputTypeMismatchThisEpoch = mismatch &&
                                          configuredMetadata.Width != expectedWidth &&
                                          configuredMetadata.Height != expectedHeight;
            if (!decoderTransformState.LoggedOutputTypeVerification)
            {
                decoderTransformState.LoggedOutputTypeVerification = true;
                LogLifecycle(
                    "screenshare_h264_decoder_transform_output_type_ready",
                    configuration?.StreamEpoch ?? 0,
                    $"transform_id={decoderTransformState.TransformId}; backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; output_type_configured={(decoderTransformState.OutputTypeConfigured ? 1 : 0)}; output_type_verified={(decoderTransformState.OutputTypeVerified ? 1 : 0)}; set_output_type_hr=0x{decoderTransformState.SetOutputTypeHresult:X8}; get_output_current_type_hr=0x{decoderTransformState.GetOutputCurrentTypeHresult:X8}; get_output_status_hr=0x{decoderTransformState.GetOutputStatusAfterConfigurationHresult:X8}; get_output_status={FormatOutputStatusFlags(decoderTransformState.GetOutputStatusAfterConfigurationFlags)}");
            }

            LogLifecycle(
                "screenshare_h264_decoder_output_stream_attributes",
                configuration?.StreamEpoch ?? 0,
                $"backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; transform_id={decoderTransformState.TransformId}; snapshot={Sanitize(decoderTransformState.OutputStreamAttributesSnapshot)}");

            LogTransformStartupReadyIfNeeded(decoderTransformState, configuration?.StreamEpoch ?? 0);

            LogLifecycle(
                "screenshare_h264_decoder_output_ready",
                configuration?.StreamEpoch ?? 0,
                $"width={outputWidth}; height={outputHeight}; stride={outputStride}; subtype={FormatVideoSubtype(outputSubtype)}; opaque_alpha={outputRequiresOpaqueAlpha}; expected_width={expectedWidth}; expected_height={expectedHeight}; mismatch={(outputTypeMismatchThisEpoch ? 1 : 0)}");
        }
        finally
        {
            if (chosenType is not null)
            {
                ReleaseComObject(chosenType);
            }

            if (currentType is not null)
            {
                ReleaseComObject(currentType);
            }

            if (yuy2Type is not null && !ReferenceEquals(yuy2Type, chosenType))
            {
                ReleaseComObject(yuy2Type);
            }

            if (nv12Type is not null && !ReferenceEquals(nv12Type, chosenType))
            {
                ReleaseComObject(nv12Type);
            }

            if (firstSupportedType is not null && !ReferenceEquals(firstSupportedType, chosenType))
            {
                ReleaseComObject(firstSupportedType);
            }
        }
    }

    private static bool ShouldPromoteYuy2OutputType(
        MftOutputStreamInfo outputInfo,
        OutputMediaTypeMetadata configuredMetadata,
        IMFMediaType? yuy2Type)
    {
        if (yuy2Type is null ||
            configuredMetadata.Subtype != MfVideoFormatNv12 ||
            configuredMetadata.Width <= 0 ||
            configuredMetadata.Height <= 0 ||
            outputInfo.CbSize == 0)
        {
            return false;
        }

        var nv12Size = DeriveOutputBufferSize(
            MfVideoFormatNv12,
            configuredMetadata.Width,
            configuredMetadata.Height,
            configuredMetadata.Stride);
        var yuy2Stride = GetDefaultStrideForSubtype(MfVideoFormatYuy2, configuredMetadata.Width);
        var yuy2Size = DeriveOutputBufferSize(
            MfVideoFormatYuy2,
            configuredMetadata.Width,
            configuredMetadata.Height,
            yuy2Stride);

        return nv12Size > 0 &&
               yuy2Size > 0 &&
               outputInfo.CbSize > nv12Size &&
               outputInfo.CbSize >= yuy2Size;
    }

    private static OutputMediaTypeMetadata ReadOutputMetadata(
        IMFMediaType mediaType,
        DecoderConfiguration? decoderConfiguration,
        bool preferExpectedDimensions = false)
    {
        var expectedWidth = decoderConfiguration?.ExpectedCodedWidth ?? 0;
        var expectedHeight = decoderConfiguration?.ExpectedCodedHeight ?? 0;
        var frameSizeKey = MfMtFrameSize;
        var frameSizeHr = mediaType.GetUINT64(ref frameSizeKey, out var packedFrameSize);
        var width = frameSizeHr >= 0
            ? unchecked((int)(packedFrameSize >> 32))
            : 0;
        var height = frameSizeHr >= 0
            ? unchecked((int)(packedFrameSize & 0xffffffff))
            : 0;

        if (preferExpectedDimensions && expectedWidth > 0 && expectedHeight > 0)
        {
            width = expectedWidth;
            height = expectedHeight;
        }

        var strideKey = MfMtDefaultStride;
        var strideHr = mediaType.GetUINT32(ref strideKey, out var rawStride);
        var subtypeKey = MfMtSubtype;
        Marshal.ThrowExceptionForHR(mediaType.GetGUID(ref subtypeKey, out var subtype));
        var stride = strideHr >= 0
            ? unchecked((int)rawStride)
            : GetDefaultStrideForSubtype(subtype, width);
        var requiresOpaqueAlpha = subtype == MfVideoFormatRgb32;

        return new OutputMediaTypeMetadata(width, height, stride, subtype, requiresOpaqueAlpha);
    }

    private void ApplyOutputMetadata(DecoderTransformState decoderTransformState, OutputMediaTypeMetadata metadata, MftOutputStreamInfo outputInfo)
    {
        decoderTransformState.OutputWidth = metadata.Width;
        decoderTransformState.OutputHeight = metadata.Height;
        decoderTransformState.OutputStride = metadata.Stride;
        decoderTransformState.OutputSubtype = metadata.Subtype;
        decoderTransformState.OutputRequiresOpaqueAlpha = metadata.RequiresOpaqueAlpha;
        decoderTransformState.OutputBufferSize = ResolveRequiredOutputBufferSize(outputInfo, metadata.Subtype, metadata.Width, metadata.Height, metadata.Stride);
        decoderTransformState.OutputBufferAlignment = NormalizeAlignment(outputInfo.CbAlignment);
    }

    private void SyncDecoderOutputMetadata(DecoderTransformState? decoderTransformState)
    {
        if (decoderTransformState is null)
        {
            outputWidth = 0;
            outputHeight = 0;
            outputStride = 0;
            outputSubtype = Guid.Empty;
            outputRequiresOpaqueAlpha = false;
            return;
        }

        outputWidth = decoderTransformState.OutputWidth;
        outputHeight = decoderTransformState.OutputHeight;
        outputStride = decoderTransformState.OutputStride;
        outputSubtype = decoderTransformState.OutputSubtype;
        outputRequiresOpaqueAlpha = decoderTransformState.OutputRequiresOpaqueAlpha;
    }

    private void ResetTransformOutputTypeState(DecoderTransformState decoderTransformState)
    {
        decoderTransformState.OutputTypeConfigured = false;
        decoderTransformState.OutputTypeVerified = false;
        decoderTransformState.OutputWidth = 0;
        decoderTransformState.OutputHeight = 0;
        decoderTransformState.OutputStride = 0;
        decoderTransformState.OutputSubtype = Guid.Empty;
        decoderTransformState.OutputRequiresOpaqueAlpha = false;
        decoderTransformState.OutputBufferSize = 0;
        decoderTransformState.OutputBufferAlignment = 0;
        decoderTransformState.StartupSequenceVerified = false;
        if (ReferenceEquals(activeTransformState, decoderTransformState))
        {
            SyncDecoderOutputMetadata(decoderTransformState);
        }
    }

    private void EnsureTransformReadyForInput(DecoderTransformState decoderTransformState, long streamEpoch, TransformStartupSequenceKind startupSequence)
    {
        if (decoderTransformState.StartupSequence != TransformStartupSequenceKind.Unknown &&
            decoderTransformState.StartupSequence != startupSequence)
        {
            throw new InvalidOperationException(
                $"Decoder transform {decoderTransformState.TransformId} is already bound to startup sequence '{FormatStartupSequence(decoderTransformState.StartupSequence)}'.");
        }

        decoderTransformState.StartupSequence = startupSequence;
        if (startupSequence == TransformStartupSequenceKind.TypesBeforeStart)
        {
            EnsureOutputTypeConfigured(decoderTransformState);
            StartTransformStream(decoderTransformState, streamEpoch);
            decoderTransformState.FullyStartedBeforeFirstInput = decoderTransformState.StartupSequenceVerified;
            return;
        }

        StartTransformStream(decoderTransformState, streamEpoch);
        decoderTransformState.FullyStartedBeforeFirstInput = false;
    }

    private void StartTransformStream(DecoderTransformState decoderTransformState, long streamEpoch)
    {
        if (!decoderTransformState.BeginStreamingSent)
        {
            decoderTransformState.BeginStreamingHresult = decoderTransformState.Transform.ProcessMessage(MftMessageNotifyBeginStreaming, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(decoderTransformState.BeginStreamingHresult);
            decoderTransformState.BeginStreamingSent = true;
        }

        if (!decoderTransformState.StartOfStreamSent)
        {
            decoderTransformState.StartOfStreamHresult = decoderTransformState.Transform.ProcessMessage(MftMessageNotifyStartOfStream, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(decoderTransformState.StartOfStreamHresult);
            decoderTransformState.StartOfStreamSent = true;
        }

        UpdateStartupSequenceVerification(decoderTransformState);
        LogTransformStartupReadyIfNeeded(decoderTransformState, streamEpoch);
    }

    private void UpdateStartupSequenceVerification(DecoderTransformState decoderTransformState)
    {
        decoderTransformState.StartupSequenceVerified =
            decoderTransformState.StartupSequence != TransformStartupSequenceKind.Unknown &&
            decoderTransformState.InputTypeConfigured &&
            decoderTransformState.OutputTypeVerified &&
            decoderTransformState.BeginStreamingSent &&
            decoderTransformState.StartOfStreamSent;
    }

    private void LogTransformStartupReadyIfNeeded(DecoderTransformState decoderTransformState, long streamEpoch)
    {
        if (decoderTransformState.LoggedStartupSequence || !decoderTransformState.StartupSequenceVerified)
        {
            return;
        }

        decoderTransformState.LoggedStartupSequence = true;
        LogLifecycle(
            "screenshare_h264_decoder_transform_startup_ready",
            streamEpoch,
            $"transform_id={decoderTransformState.TransformId}; startup_sequence={FormatStartupSequence(decoderTransformState.StartupSequence)}; input_type_configured={(decoderTransformState.InputTypeConfigured ? 1 : 0)}; output_type_configured={(decoderTransformState.OutputTypeConfigured ? 1 : 0)}; output_type_verified={(decoderTransformState.OutputTypeVerified ? 1 : 0)}; begin_streaming_sent={(decoderTransformState.BeginStreamingSent ? 1 : 0)}; begin_streaming_hr=0x{decoderTransformState.BeginStreamingHresult:X8}; start_of_stream_sent={(decoderTransformState.StartOfStreamSent ? 1 : 0)}; start_of_stream_hr=0x{decoderTransformState.StartOfStreamHresult:X8}; fully_started_before_first_input={(decoderTransformState.FullyStartedBeforeFirstInput ? 1 : 0)}");
    }

    private void ApplyDecoderAttributeProfile(DecoderTransformState decoderTransformState, long streamEpoch)
    {
        decoderTransformState.LowLatencyRequested = decoderTransformState.AttributeProfile == DecoderAttributeProfileKind.LowLatency;
        TryCaptureTransformAttributes(decoderTransformState);
        decoderTransformState.TransformAttributesSnapshotBeforeProfile = decoderTransformState.TransformAttributesSnapshot;

        IMFAttributes? transformAttributes = null;
        try
        {
            decoderTransformState.TransformAttributesHresult = decoderTransformState.Transform.GetAttributes(out transformAttributes);
            decoderTransformState.TransformAttributesAvailable = decoderTransformState.TransformAttributesHresult >= 0 && transformAttributes is not null;
            if (decoderTransformState.TransformAttributesAvailable && transformAttributes is not null && decoderTransformState.LowLatencyRequested)
            {
                var key = MfLowLatency;
                decoderTransformState.TransformLowLatencyHresult = transformAttributes.SetUINT32(ref key, 1);
                decoderTransformState.LowLatencyAppliedToTransform = decoderTransformState.TransformLowLatencyHresult >= 0;
                decoderTransformState.AttributeProfileFailure |= decoderTransformState.TransformLowLatencyHresult < 0;
            }
        }
        catch (Exception ex)
        {
            decoderTransformState.TransformAttributesHresult = ex.HResult;
            decoderTransformState.TransformLowLatencyHresult = ex.HResult;
            decoderTransformState.AttributeProfileFailure = decoderTransformState.LowLatencyRequested;
        }
        finally
        {
            if (transformAttributes is not null)
            {
                ReleaseComObject(transformAttributes);
            }
        }

        ApplyCodecApiLowLatencyProfile(decoderTransformState);
        TryCaptureTransformAttributes(decoderTransformState);
        decoderTransformState.TransformAttributesSnapshotAfterProfile = decoderTransformState.TransformAttributesSnapshot;
        decoderAttributeProfileFailureThisEpoch |= decoderTransformState.AttributeProfileFailure;
        LogLifecycle(
            "screenshare_h264_decoder_transform_activation",
            streamEpoch,
            $"backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; activation_source={Sanitize(decoderTransformState.ActivationSource)}; transform_id={decoderTransformState.TransformId}; friendly_name={Sanitize(decoderTransformState.FriendlyName)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; transform_attributes_available={(decoderTransformState.TransformAttributesAvailable ? 1 : 0)}; input_attributes_available={(decoderTransformState.InputStreamAttributesAvailable ? 1 : 0)}; output_attributes_available={(decoderTransformState.OutputStreamAttributesAvailable ? 1 : 0)}");
        LogLifecycle(
            "screenshare_h264_decoder_transform_attributes",
            streamEpoch,
            $"backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; transform_id={decoderTransformState.TransformId}; before_profile={Sanitize(decoderTransformState.TransformAttributesSnapshotBeforeProfile)}; after_profile={Sanitize(decoderTransformState.TransformAttributesSnapshotAfterProfile)}");
        LogLifecycle(
            "screenshare_h264_decoder_low_latency_profile",
            streamEpoch,
            $"backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; transform_id={decoderTransformState.TransformId}; requested={(decoderTransformState.LowLatencyRequested ? 1 : 0)}; transform_applied={(decoderTransformState.LowLatencyAppliedToTransform ? 1 : 0)}; transform_hr=0x{decoderTransformState.TransformLowLatencyHresult:X8}; input_media_type_applied={(decoderTransformState.LowLatencyAppliedToInputMediaType ? 1 : 0)}; input_media_type_hr=0x{decoderTransformState.InputMediaTypeLowLatencyHresult:X8}; output_media_type_applied={(decoderTransformState.LowLatencyAppliedToOutputMediaType ? 1 : 0)}; output_media_type_hr=0x{decoderTransformState.OutputMediaTypeLowLatencyHresult:X8}; codecapi_available={(decoderTransformState.CodecApiAvailable ? 1 : 0)}; codecapi_supported={(decoderTransformState.CodecApiSupported ? 1 : 0)}; codecapi_is_supported_hr=0x{decoderTransformState.CodecApiIsSupportedHresult:X8}; codecapi_modifiable={(decoderTransformState.CodecApiModifiable ? 1 : 0)}; codecapi_is_modifiable_hr=0x{decoderTransformState.CodecApiIsModifiableHresult:X8}; codecapi_applied={(decoderTransformState.CodecApiLowLatencyApplied ? 1 : 0)}; codecapi_set_value_hr=0x{decoderTransformState.CodecApiSetValueHresult:X8}");
    }

    private void ApplyCodecApiLowLatencyProfile(DecoderTransformState decoderTransformState)
    {
        if (!decoderTransformState.LowLatencyRequested)
        {
            return;
        }

        IntPtr transformUnknownPtr = IntPtr.Zero;
        IntPtr codecApiPtr = IntPtr.Zero;
        ICodecAPI? codecApi = null;
        try
        {
            transformUnknownPtr = Marshal.GetIUnknownForObject(decoderTransformState.Transform);
            var iid = IidICodecApi;
            var queryInterfaceHresult = Marshal.QueryInterface(transformUnknownPtr, ref iid, out codecApiPtr);
            decoderTransformState.CodecApiIsSupportedHresult = queryInterfaceHresult;
            decoderTransformState.CodecApiIsModifiableHresult = queryInterfaceHresult;
            decoderTransformState.CodecApiSetValueHresult = queryInterfaceHresult;
            if (queryInterfaceHresult < 0 || codecApiPtr == IntPtr.Zero)
            {
                return;
            }

            decoderTransformState.CodecApiAvailable = true;
            codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(codecApiPtr);

            var api = MfLowLatency;
            decoderTransformState.CodecApiIsSupportedHresult = codecApi.IsSupported(ref api);
            decoderTransformState.CodecApiSupported = decoderTransformState.CodecApiIsSupportedHresult == 0;
            if (!decoderTransformState.CodecApiSupported)
            {
                return;
            }

            decoderTransformState.CodecApiIsModifiableHresult = codecApi.IsModifiable(ref api);
            decoderTransformState.CodecApiModifiable = decoderTransformState.CodecApiIsModifiableHresult == 0;
            if (!decoderTransformState.CodecApiModifiable)
            {
                return;
            }

            var variant = CodecApiVariant.FromUInt32(1);
            var variantSize = Marshal.SizeOf<CodecApiVariant>();
            var variantPtr = Marshal.AllocHGlobal(variantSize);
            try
            {
                Marshal.StructureToPtr(variant, variantPtr, false);
                decoderTransformState.CodecApiSetValueHresult = codecApi.SetValue(ref api, variantPtr);
                decoderTransformState.CodecApiLowLatencyApplied = decoderTransformState.CodecApiSetValueHresult >= 0;
                decoderTransformState.AttributeProfileFailure |= decoderTransformState.CodecApiSetValueHresult < 0;
            }
            finally
            {
                Marshal.FreeHGlobal(variantPtr);
            }
        }
        catch (Exception ex)
        {
            decoderTransformState.CodecApiSetValueHresult = ex.HResult;
            decoderTransformState.AttributeProfileFailure = true;
        }
        finally
        {
            if (codecApiPtr != IntPtr.Zero)
            {
                Marshal.Release(codecApiPtr);
            }

            if (transformUnknownPtr != IntPtr.Zero)
            {
                Marshal.Release(transformUnknownPtr);
            }
        }
    }

    private void CaptureTransformAttributes(DecoderTransformState decoderTransformState)
    {
        IMFAttributes? attributes = null;
        try
        {
            decoderTransformState.TransformAttributesHresult = decoderTransformState.Transform.GetAttributes(out attributes);
            decoderTransformState.TransformAttributesAvailable = decoderTransformState.TransformAttributesHresult >= 0 && attributes is not null;
            decoderTransformState.TransformAttributesSnapshot = decoderTransformState.TransformAttributesAvailable && attributes is not null
                ? DescribeAttributes(attributes, [MfLowLatency])
                : $"unavailable; hr=0x{decoderTransformState.TransformAttributesHresult:X8}";
        }
        catch (Exception ex)
        {
            decoderTransformState.TransformAttributesHresult = ex.HResult;
            decoderTransformState.TransformAttributesAvailable = false;
            decoderTransformState.TransformAttributesSnapshot = $"unavailable; hr=0x{ex.HResult:X8}";
        }
        finally
        {
            if (attributes is not null)
            {
                ReleaseComObject(attributes);
            }
        }
    }

    private void TryCaptureTransformAttributes(DecoderTransformState decoderTransformState)
    {
        try
        {
            CaptureTransformAttributes(decoderTransformState);
        }
        catch (Exception ex)
        {
            decoderTransformState.TransformAttributesHresult = ex.HResult;
            decoderTransformState.TransformAttributesAvailable = false;
            decoderTransformState.TransformAttributesSnapshot = $"unavailable; hr=0x{ex.HResult:X8}";
        }
    }

    private void CaptureInputStreamAttributes(DecoderTransformState decoderTransformState)
    {
        IMFAttributes? attributes = null;
        try
        {
            decoderTransformState.InputStreamAttributesHresult = decoderTransformState.Transform.GetInputStreamAttributes(0, out attributes);
            decoderTransformState.InputStreamAttributesAvailable = decoderTransformState.InputStreamAttributesHresult >= 0 && attributes is not null;
            decoderTransformState.InputStreamAttributesSnapshot = decoderTransformState.InputStreamAttributesAvailable && attributes is not null
                ? DescribeAttributes(attributes, [MfLowLatency, MfMtAllSamplesIndependent, MfMtFixedSizeSamples])
                : $"unavailable; hr=0x{decoderTransformState.InputStreamAttributesHresult:X8}";
        }
        catch (Exception ex)
        {
            decoderTransformState.InputStreamAttributesHresult = ex.HResult;
            decoderTransformState.InputStreamAttributesAvailable = false;
            decoderTransformState.InputStreamAttributesSnapshot = $"unavailable; hr=0x{ex.HResult:X8}";
        }
        finally
        {
            if (attributes is not null)
            {
                ReleaseComObject(attributes);
            }
        }
    }

    private void TryCaptureInputStreamAttributes(DecoderTransformState decoderTransformState)
    {
        try
        {
            CaptureInputStreamAttributes(decoderTransformState);
        }
        catch (Exception ex)
        {
            decoderTransformState.InputStreamAttributesHresult = ex.HResult;
            decoderTransformState.InputStreamAttributesAvailable = false;
            decoderTransformState.InputStreamAttributesSnapshot = $"unavailable; hr=0x{ex.HResult:X8}";
        }
    }

    private void CaptureOutputStreamAttributes(DecoderTransformState decoderTransformState)
    {
        IMFAttributes? attributes = null;
        try
        {
            decoderTransformState.OutputStreamAttributesHresult = decoderTransformState.Transform.GetOutputStreamAttributes(0, out attributes);
            decoderTransformState.OutputStreamAttributesAvailable = decoderTransformState.OutputStreamAttributesHresult >= 0 && attributes is not null;
            decoderTransformState.OutputStreamAttributesSnapshot = decoderTransformState.OutputStreamAttributesAvailable && attributes is not null
                ? DescribeAttributes(attributes, [MfLowLatency, MfMtAllSamplesIndependent, MfMtFixedSizeSamples])
                : $"unavailable; hr=0x{decoderTransformState.OutputStreamAttributesHresult:X8}";
        }
        catch (Exception ex)
        {
            decoderTransformState.OutputStreamAttributesHresult = ex.HResult;
            decoderTransformState.OutputStreamAttributesAvailable = false;
            decoderTransformState.OutputStreamAttributesSnapshot = $"unavailable; hr=0x{ex.HResult:X8}";
        }
        finally
        {
            if (attributes is not null)
            {
                ReleaseComObject(attributes);
            }
        }
    }

    private void TryCaptureOutputStreamAttributes(DecoderTransformState decoderTransformState)
    {
        try
        {
            CaptureOutputStreamAttributes(decoderTransformState);
        }
        catch (Exception ex)
        {
            decoderTransformState.OutputStreamAttributesHresult = ex.HResult;
            decoderTransformState.OutputStreamAttributesAvailable = false;
            decoderTransformState.OutputStreamAttributesSnapshot = $"unavailable; hr=0x{ex.HResult:X8}";
        }
    }

    private static string DescribeAttributes(IMFAttributes attributes, Guid[] knownKeys)
    {
        var parts = new List<string>();
        try
        {
            if (attributes.GetCount(out var count) >= 0)
            {
                parts.Add($"count={count}");
                var unknownKeys = TryEnumerateAttributeKeys(attributes, knownKeys);
                parts.Add($"unknown_keys={(unknownKeys.Count == 0 ? "none" : string.Join(",", unknownKeys))}");
            }
        }
        catch (COMException ex)
        {
            parts.Add($"count_hr=0x{ex.HResult:X8}");
        }

        foreach (var key in knownKeys)
        {
            if (TryFormatAttributeValue(attributes, key, out var value))
            {
                parts.Add($"{FormatAttributeKey(key)}={value}");
            }
        }

        return string.Join(";", parts);
    }

    private static List<string> TryEnumerateAttributeKeys(IMFAttributes attributes, Guid[] knownKeys)
    {
        var keys = new List<string>();
        if (attributes.GetCount(out var count) < 0)
        {
            return keys;
        }

        var known = new HashSet<Guid>(knownKeys);
        for (uint index = 0; index < count; index++)
        {
            try
            {
                if (attributes.GetItemByIndex(index, out var key, IntPtr.Zero) >= 0 && !known.Contains(key))
                {
                    keys.Add(key.ToString("D"));
                }
            }
            catch
            {
                break;
            }
        }

        return keys;
    }

    private static bool TryFormatAttributeValue(IMFAttributes attributes, Guid key, out string value)
    {
        value = string.Empty;
        if (attributes.GetUINT32(ref key, out var uintValue) >= 0)
        {
            value = uintValue.ToString();
            return true;
        }

        if (attributes.GetUINT64(ref key, out var ulongValue) >= 0)
        {
            value = ulongValue.ToString();
            return true;
        }

        if (attributes.GetGUID(ref key, out var guidValue) >= 0)
        {
            value = guidValue.ToString("D");
            return true;
        }

        return false;
    }

    private static string FormatAttributeKey(Guid key)
    {
        if (key == MfLowLatency)
        {
            return "mf_low_latency";
        }

        if (key == MfMtAllSamplesIndependent)
        {
            return "all_samples_independent";
        }

        if (key == MfMtFixedSizeSamples)
        {
            return "fixed_size_samples";
        }

        return key.ToString("D");
    }

    private static IMFMediaType CreateInputMediaType(
        DecoderConfiguration decoderConfiguration,
        DecoderAttributeProfileKind attributeProfile,
        DecoderTransformState decoderTransformState)
    {
        Marshal.ThrowExceptionForHR(MFCreateMediaType(out var mediaType));
        try
        {
            var majorType = MfMediaTypeVideo;
            var subtype = MfVideoFormatH264;
            var majorTypeKey = MfMtMajorType;
            var subtypeKey = MfMtSubtype;
            var interlaceModeKey = MfMtInterlaceMode;
            Marshal.ThrowExceptionForHR(mediaType.SetGUID(ref majorTypeKey, ref majorType));
            Marshal.ThrowExceptionForHR(mediaType.SetGUID(ref subtypeKey, ref subtype));
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref interlaceModeKey, MfVideoInterlaceProgressive));
            if (decoderConfiguration.DecoderConfigData.Length > 0)
            {
                var sequenceHeaderKey = MfMtMpegSequenceHeader;
                Marshal.ThrowExceptionForHR(mediaType.SetBlob(ref sequenceHeaderKey, decoderConfiguration.DecoderConfigData, (uint)decoderConfiguration.DecoderConfigData.Length));
            }

            if (decoderConfiguration.ExpectedCodedWidth > 0 && decoderConfiguration.ExpectedCodedHeight > 0)
            {
                var frameSizeKey = MfMtFrameSize;
                Marshal.ThrowExceptionForHR(mediaType.SetUINT64(ref frameSizeKey, PackFrameSize(decoderConfiguration.ExpectedCodedWidth, decoderConfiguration.ExpectedCodedHeight)));
            }

            if (attributeProfile == DecoderAttributeProfileKind.LowLatency)
            {
                var lowLatencyKey = MfLowLatency;
                decoderTransformState.InputMediaTypeLowLatencyHresult = mediaType.SetUINT32(ref lowLatencyKey, 1);
                decoderTransformState.LowLatencyAppliedToInputMediaType = decoderTransformState.InputMediaTypeLowLatencyHresult >= 0;
                decoderTransformState.AttributeProfileFailure |= decoderTransformState.InputMediaTypeLowLatencyHresult < 0;
            }

            return mediaType;
        }
        catch
        {
            ReleaseComObject(mediaType);
            throw;
        }
    }

    private static IMFSample CreateInputSample(ReadOnlyMemory<byte> encodedBytes, long sampleTimeHns, InputSampleContract contract)
    {
        if (encodedBytes.Length == 0)
        {
            throw new ArgumentException("Encoded H.264 frame must not be empty.", nameof(encodedBytes));
        }

        Marshal.ThrowExceptionForHR(MFCreateSample(out var sample));
        IMFMediaBuffer? buffer = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateMemoryBuffer(encodedBytes.Length, out buffer));
            Marshal.ThrowExceptionForHR(buffer.Lock(out var scan0, out _, out _));
            try
            {
                if (MemoryMarshal.TryGetArray(encodedBytes, out var segment) && segment.Array is not null)
                {
                    Marshal.Copy(segment.Array, segment.Offset, scan0, segment.Count);
                }
                else
                {
                    Marshal.Copy(encodedBytes.ToArray(), 0, scan0, encodedBytes.Length);
                }
            }
            finally
            {
                buffer.Unlock();
            }

            Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(encodedBytes.Length));
            Marshal.ThrowExceptionForHR(sample.AddBuffer(buffer));
            if (contract.SetSampleTime)
            {
                Marshal.ThrowExceptionForHR(sample.SetSampleTime(sampleTimeHns));
            }

            if (contract.SetSampleDuration)
            {
                Marshal.ThrowExceptionForHR(sample.SetSampleDuration(DefaultInputSampleDurationHns));
            }

            if (contract.SetCleanPoint)
            {
                var key = MfSampleExtensionCleanPoint;
                Marshal.ThrowExceptionForHR(sample.SetUINT32(ref key, 1));
            }

            if (contract.SetDiscontinuity)
            {
                var key = MfSampleExtensionDiscontinuity;
                Marshal.ThrowExceptionForHR(sample.SetUINT32(ref key, 1));
            }

            return sample;
        }
        catch
        {
            ReleaseComObject(sample);
            throw;
        }
        finally
        {
            if (buffer is not null)
            {
                ReleaseComObject(buffer);
            }
        }
    }

    private ReadOnlyMemory<byte> NormalizeInputBytesForDecoder(
        ReadOnlyMemory<byte> encodedBytes,
        DecoderConfiguration activeConfiguration,
        long streamEpoch)
    {
        var preparationConfig = WindowsH264DecodePreparation.TryCreateDecoderConfiguration(activeConfiguration.DecoderConfigData, out var sharedConfiguration)
            ? sharedConfiguration
            : null;
        var converted = WindowsH264DecodePreparation.NormalizeForMediaFoundation(encodedBytes, preparationConfig);
        if (converted.Length == 0)
        {
            return encodedBytes;
        }

        if (!inputNormalizationLogged)
        {
            inputNormalizationLogged = true;
            LogLifecycle(
                "screenshare_h264_decoder_input_normalized",
                streamEpoch,
                $"input_format=annexb; output_format=length_prefixed; nal_length_size={activeConfiguration.NalLengthSize}; input_bytes={encodedBytes.Length}; output_bytes={converted.Length}; strip_config_nals=0");
        }

        return converted;
    }

    internal static byte[] DebugConvertAnnexBToLengthPrefixed(byte[] annexBBytes, byte[] decoderConfigData)
        => WindowsH264DecodePreparation.DebugConvertAnnexBToLengthPrefixed(annexBBytes, decoderConfigData);

    private static bool TryParseNalLengthSize(byte[] decoderConfigData, out int nalLengthSize)
        => WindowsH264DecodePreparation.TryParseNalLengthSize(decoderConfigData, out nalLengthSize);

    private static bool TryParseExpectedCodedSize(byte[] decoderConfigData, out int width, out int height)
        => WindowsH264DecodePreparation.TryParseExpectedCodedSize(decoderConfigData, out width, out height);

    private static bool TryParseAvcConfiguration(
        byte[] avcC,
        out int nalLengthSize,
        out List<byte[]> spsUnits,
        out List<byte[]> ppsUnits)
        => WindowsH264DecodePreparation.TryParseAvcConfiguration(avcC, out nalLengthSize, out spsUnits, out ppsUnits);

    private static bool TryReadLengthPrefixedBlob(byte[] bytes, ref int offset, out byte[] blob)
    {
        blob = Array.Empty<byte>();
        if (offset + 2 > bytes.Length)
        {
            return false;
        }

        var length = (bytes[offset] << 8) | bytes[offset + 1];
        offset += 2;
        if (length <= 0 || offset + length > bytes.Length)
        {
            return false;
        }

        blob = new byte[length];
        Buffer.BlockCopy(bytes, offset, blob, 0, length);
        offset += length;
        return true;
    }

    private static bool TryParseH264SpsDimensions(byte[] spsNalUnit, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (spsNalUnit.Length < 4)
        {
            return false;
        }

        try
        {
            var rbsp = RemoveEmulationPreventionBytes(spsNalUnit.AsSpan(1));
            var reader = new H264BitReader(rbsp);
            var profileIdc = reader.ReadBits(8);
            reader.SkipBits(8);
            reader.SkipBits(8);
            reader.ReadUnsignedExpGolomb();

            var chromaFormatIdc = 1;
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = reader.ReadUnsignedExpGolomb();
                if (chromaFormatIdc == 3)
                {
                    reader.SkipBits(1);
                }

                reader.ReadUnsignedExpGolomb();
                reader.ReadUnsignedExpGolomb();
                reader.SkipBits(1);
                if (reader.ReadFlag())
                {
                    var scalingCount = chromaFormatIdc == 3 ? 12 : 8;
                    for (var i = 0; i < scalingCount; i++)
                    {
                        if (reader.ReadFlag())
                        {
                            SkipScalingList(reader, i < 6 ? 16 : 64);
                        }
                    }
                }
            }

            reader.ReadUnsignedExpGolomb();
            var picOrderCntType = reader.ReadUnsignedExpGolomb();
            if (picOrderCntType == 0)
            {
                reader.ReadUnsignedExpGolomb();
            }
            else if (picOrderCntType == 1)
            {
                reader.SkipBits(1);
                reader.ReadSignedExpGolomb();
                reader.ReadSignedExpGolomb();
                var cycleCount = reader.ReadUnsignedExpGolomb();
                for (var i = 0; i < cycleCount; i++)
                {
                    reader.ReadSignedExpGolomb();
                }
            }

            reader.ReadUnsignedExpGolomb();
            reader.SkipBits(1);
            var picWidthInMbsMinus1 = reader.ReadUnsignedExpGolomb();
            var picHeightInMapUnitsMinus1 = reader.ReadUnsignedExpGolomb();
            var frameMbsOnlyFlag = reader.ReadFlag();
            if (!frameMbsOnlyFlag)
            {
                reader.SkipBits(1);
            }

            reader.SkipBits(1);
            var frameCroppingFlag = reader.ReadFlag();
            var cropLeft = 0;
            var cropRight = 0;
            var cropTop = 0;
            var cropBottom = 0;
            if (frameCroppingFlag)
            {
                cropLeft = reader.ReadUnsignedExpGolomb();
                cropRight = reader.ReadUnsignedExpGolomb();
                cropTop = reader.ReadUnsignedExpGolomb();
                cropBottom = reader.ReadUnsignedExpGolomb();
            }

            var frameWidth = (picWidthInMbsMinus1 + 1) * 16;
            var frameHeight = (2 - (frameMbsOnlyFlag ? 1 : 0)) * (picHeightInMapUnitsMinus1 + 1) * 16;
            GetCropUnits(chromaFormatIdc, frameMbsOnlyFlag, out var cropUnitX, out var cropUnitY);
            frameWidth -= (cropLeft + cropRight) * cropUnitX;
            frameHeight -= (cropTop + cropBottom) * cropUnitY;
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                return false;
            }

            width = frameWidth;
            height = frameHeight;
            return true;
        }
        catch
        {
            width = 0;
            height = 0;
            return false;
        }
    }

    private static byte[] RemoveEmulationPreventionBytes(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        using var stream = new MemoryStream(source.Length);
        var zeroCount = 0;
        foreach (var next in source)
        {
            if (zeroCount >= 2 && next == 0x03)
            {
                zeroCount = 0;
                continue;
            }

            stream.WriteByte(next);
            zeroCount = next == 0 ? zeroCount + 1 : 0;
        }

        return stream.ToArray();
    }

    private static void SkipScalingList(H264BitReader reader, int count)
    {
        var lastScale = 8;
        var nextScale = 8;
        for (var i = 0; i < count; i++)
        {
            if (nextScale != 0)
            {
                var deltaScale = reader.ReadSignedExpGolomb();
                nextScale = (lastScale + deltaScale + 256) % 256;
            }

            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
    }

    private static void GetCropUnits(int chromaFormatIdc, bool frameMbsOnlyFlag, out int cropUnitX, out int cropUnitY)
    {
        var frameMultiplier = frameMbsOnlyFlag ? 1 : 2;
        switch (chromaFormatIdc)
        {
            case 0:
                cropUnitX = 1;
                cropUnitY = frameMultiplier;
                return;
            case 1:
                cropUnitX = 2;
                cropUnitY = 2 * frameMultiplier;
                return;
            case 2:
                cropUnitX = 2;
                cropUnitY = frameMultiplier;
                return;
            default:
                cropUnitX = 1;
                cropUnitY = frameMultiplier;
                return;
        }
    }

    private static bool LooksLikeAnnexB(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i <= bytes.Length - 3; i++)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0)
            {
                if (bytes[i + 2] == 1)
                {
                    return true;
                }

                if (i + 3 < bytes.Length && bytes[i + 2] == 0 && bytes[i + 3] == 1)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] ConvertAnnexBToLengthPrefixed(
        ReadOnlySpan<byte> annexBBytes,
        int nalLengthSize,
        bool stripDecoderConfigNalUnits)
    {
        using var stream = new MemoryStream(annexBBytes.Length);
        var offset = 0;
        while (TryReadAnnexBNalUnit(annexBBytes, ref offset, out var nalUnit))
        {
            if (nalUnit.Length == 0)
            {
                continue;
            }

            if (stripDecoderConfigNalUnits && IsDecoderConfigNalUnit(nalUnit))
            {
                continue;
            }

            for (var shift = (nalLengthSize - 1) * 8; shift >= 0; shift -= 8)
            {
                stream.WriteByte((byte)((nalUnit.Length >> shift) & 0xFF));
            }

            stream.Write(nalUnit);
        }

        return stream.ToArray();
    }

    private static bool IsDecoderConfigNalUnit(ReadOnlySpan<byte> nalUnit)
    {
        if (nalUnit.IsEmpty)
        {
            return false;
        }

        var nalType = nalUnit[0] & 0x1F;
        return nalType is 7 or 8;
    }

    private static bool TryReadAnnexBNalUnit(ReadOnlySpan<byte> bytes, ref int offset, out ReadOnlySpan<byte> nalUnit)
    {
        nalUnit = default;
        var startCodeOffset = FindAnnexBStartCode(bytes, offset, out var startCodeLength);
        if (startCodeOffset < 0)
        {
            return false;
        }

        var nalStart = startCodeOffset + startCodeLength;
        var nextStart = FindAnnexBStartCode(bytes, nalStart, out _);
        var nalEnd = nextStart >= 0 ? nextStart : bytes.Length;

        while (nalEnd > nalStart && bytes[nalEnd - 1] == 0)
        {
            nalEnd--;
        }

        nalUnit = bytes.Slice(nalStart, Math.Max(0, nalEnd - nalStart));
        offset = nextStart >= 0 ? nextStart : bytes.Length;
        return true;
    }

    private static int FindAnnexBStartCode(ReadOnlySpan<byte> bytes, int offset, out int startCodeLength)
    {
        for (var i = Math.Max(0, offset); i <= bytes.Length - 3; i++)
        {
            if (bytes[i] != 0 || bytes[i + 1] != 0)
            {
                continue;
            }

            if (bytes[i + 2] == 1)
            {
                startCodeLength = 3;
                return i;
            }

            if (i <= bytes.Length - 4 && bytes[i + 2] == 0 && bytes[i + 3] == 1)
            {
                startCodeLength = 4;
                return i;
            }
        }

        startCodeLength = 0;
        return -1;
    }

    private IMFSample CreateOutputSample(DecoderTransformState decoderTransformState, MftOutputStreamInfo outputInfo, long streamEpoch, OutputSampleShapeKind shapeKind)
    {
        var providerKind = GetOutputSampleProvider(shapeKind);
        var reportedBufferSize = outputInfo.CbSize;
        var resolvedBufferSize = ResolveRequiredOutputBufferSize(outputInfo, decoderTransformState.OutputSubtype, decoderTransformState.OutputWidth, decoderTransformState.OutputHeight, decoderTransformState.OutputStride);
        if (!loggedOutputBufferSizeOverrideThisEpoch &&
            reportedBufferSize > 0 &&
            resolvedBufferSize > 0 &&
            reportedBufferSize != resolvedBufferSize)
        {
            loggedOutputBufferSizeOverrideThisEpoch = true;
            LogLifecycle(
                "screenshare_h264_decoder_output_buffer_size_override",
                streamEpoch,
                $"reported_cb_size={reportedBufferSize}; resolved_buffer_size={resolvedBufferSize}; subtype={FormatVideoSubtype(decoderTransformState.OutputSubtype)}; width={decoderTransformState.OutputWidth}; height={decoderTransformState.OutputHeight}; stride={decoderTransformState.OutputStride}; transform_id={decoderTransformState.TransformId}");
        }

        try
        {
            var sample = shapeKind switch
            {
                OutputSampleShapeKind.AlignedContiguousLengthZero => CreateAlignedOutputSample(decoderTransformState, outputInfo, setCurrentLength: false),
                OutputSampleShapeKind.AlignedContiguousLengthPreset => CreateAlignedOutputSample(decoderTransformState, outputInfo, setCurrentLength: true),
                OutputSampleShapeKind.TwoDVideoBufferLengthZero => Create2DOutputSample(decoderTransformState, outputInfo, setCurrentLength: false),
                OutputSampleShapeKind.TwoDVideoBufferLengthPreset => Create2DOutputSample(decoderTransformState, outputInfo, setCurrentLength: true),
                _ => throw new InvalidOperationException("No output sample provider is available."),
            };

            LogLifecycle(
                "screenshare_h264_decoder_output_provider_created",
                streamEpoch,
                $"provider={FormatOutputSampleProvider(providerKind)}; shape={FormatOutputSampleShape(shapeKind)}; reported_cb_size={reportedBufferSize}; buffer_size={resolvedBufferSize}; buffer_alignment={NormalizeAlignment(outputInfo.CbAlignment)}; transform_id={decoderTransformState.TransformId}");
            return sample;
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_decoder_output_provider_failed",
                streamEpoch,
                $"provider={FormatOutputSampleProvider(providerKind)}; shape={FormatOutputSampleShape(shapeKind)}; transform_id={decoderTransformState.TransformId}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            throw;
        }
    }

    private IMFSample CreateAlignedOutputSample(DecoderTransformState decoderTransformState, MftOutputStreamInfo outputInfo, bool setCurrentLength)
    {
        var stage = "create_sample";
        IMFSample? sample = null;
        IMFMediaBuffer? buffer = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateSample(out sample));
            var bufferSize = ResolveRequiredOutputBufferSize(outputInfo, decoderTransformState.OutputSubtype, decoderTransformState.OutputWidth, decoderTransformState.OutputHeight, decoderTransformState.OutputStride);
            var alignment = NormalizeAlignment(outputInfo.CbAlignment);
            stage = "create_aligned_buffer";
            Marshal.ThrowExceptionForHR(MFCreateAlignedMemoryBuffer(checked((int)bufferSize), alignment, out buffer));
            if (setCurrentLength)
            {
                stage = "set_current_length";
                Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(checked((int)bufferSize)));
            }

            stage = "add_buffer";
            Marshal.ThrowExceptionForHR(sample.AddBuffer(buffer));
            var result = sample;
            sample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Aligned output sample creation failed at {stage}.", ex);
        }
        finally
        {
            ReleaseComObject(buffer);
            ReleaseComObject(sample);
        }
    }

    private IMFSample Create2DOutputSample(DecoderTransformState decoderTransformState, MftOutputStreamInfo outputInfo, bool setCurrentLength)
    {
        if (!SupportsTwoDVideoBuffer(decoderTransformState.OutputSubtype))
        {
            throw new NotSupportedException($"2D output provider is not supported for subtype '{FormatVideoSubtype(decoderTransformState.OutputSubtype)}'.");
        }

        var stage = "create_sample";
        IMFSample? sample = null;
        IMFMediaBuffer? buffer = null;
        try
        {
            Marshal.ThrowExceptionForHR(MFCreateSample(out sample));
            stage = "create_2d_buffer";
            Marshal.ThrowExceptionForHR(MFCreate2DMediaBuffer(
                checked((uint)decoderTransformState.OutputWidth),
                checked((uint)decoderTransformState.OutputHeight),
                GetOutputSubtypeFourCc(decoderTransformState.OutputSubtype),
                false,
                out buffer));
            var bufferSize = ResolveRequiredOutputBufferSize(outputInfo, decoderTransformState.OutputSubtype, decoderTransformState.OutputWidth, decoderTransformState.OutputHeight, decoderTransformState.OutputStride);
            if (setCurrentLength)
            {
                stage = "set_current_length";
                Marshal.ThrowExceptionForHR(buffer.SetCurrentLength(checked((int)bufferSize)));
            }

            stage = "add_buffer";
            Marshal.ThrowExceptionForHR(sample.AddBuffer(buffer));
            var result = sample;
            sample = null;
            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"2D output sample creation failed at {stage}.", ex);
        }
        finally
        {
            ReleaseComObject(buffer);
            ReleaseComObject(sample);
        }
    }

    private static uint ResolveRequiredOutputBufferSize(MftOutputStreamInfo outputInfo, Guid subtype, int width, int height, int stride)
    {
        var derivedSize = DeriveOutputBufferSize(subtype, width, height, stride);
        if (outputInfo.CbSize > 0 && outputInfo.CbSize >= derivedSize)
        {
            return outputInfo.CbSize;
        }

        if (derivedSize > 0)
        {
            return derivedSize;
        }

        if (outputInfo.CbSize > 0)
        {
            return outputInfo.CbSize;
        }

        throw new InvalidOperationException("Media Foundation H.264 decoder did not report an output buffer size and the subtype dimensions are not usable.");
    }

    private static uint DeriveOutputBufferSize(Guid subtype, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var effectiveStride = Math.Abs(stride != 0 ? stride : GetDefaultStrideForSubtype(subtype, width));
        if (effectiveStride <= 0)
        {
            return 0;
        }

        return subtype switch
        {
            var candidate when candidate == MfVideoFormatNv12
                => checked((uint)(effectiveStride * height + (effectiveStride * ((height + 1) / 2)))),
            var candidate when candidate == MfVideoFormatYuy2
                => checked((uint)(effectiveStride * height)),
            var candidate when candidate == MfVideoFormatArgb32 || candidate == MfVideoFormatRgb32
                => checked((uint)(effectiveStride * height)),
            _ => 0u,
        };
    }

    private static uint NormalizeAlignment(uint alignment)
    {
        if (alignment <= 1)
        {
            return 16;
        }

        if ((alignment & (alignment - 1)) == 0)
        {
            return alignment;
        }

        uint next = 1;
        while (next < alignment && next < 4096)
        {
            next <<= 1;
        }

        return next >= alignment ? next : 16;
    }

    private static bool SupportsTwoDVideoBuffer(Guid subtype)
    {
        return subtype == MfVideoFormatNv12 || subtype == MfVideoFormatYuy2;
    }

    private static uint GetOutputSubtypeFourCc(Guid subtype)
    {
        if (subtype == MfVideoFormatNv12)
        {
            return FourCcNv12;
        }

        if (subtype == MfVideoFormatYuy2)
        {
            return FourCcYuy2;
        }

        throw new NotSupportedException($"Media Foundation H.264 decoder does not support 2D output buffer provisioning for subtype '{FormatVideoSubtype(subtype)}'.");
    }

    private static void TryOverrideOutputFrameSize(IMFMediaType mediaType, Guid subtype, int expectedWidth, int expectedHeight)
    {
        if (expectedWidth <= 0 || expectedHeight <= 0)
        {
            return;
        }

        var frameSizeKey = MfMtFrameSize;
        var strideKey = MfMtDefaultStride;
        Marshal.ThrowExceptionForHR(mediaType.SetUINT64(ref frameSizeKey, PackFrameSize(expectedWidth, expectedHeight)));
        var stride = GetDefaultStrideForSubtype(subtype, expectedWidth);
        if (stride > 0)
        {
            Marshal.ThrowExceptionForHR(mediaType.SetUINT32(ref strideKey, checked((uint)stride)));
        }
    }

    private Bitmap CreateBitmapFromSample(DecoderTransformState decoderTransformState, IMFSample sample)
    {
        IntPtr contiguousBufferPtr = IntPtr.Zero;
        IMFMediaBuffer? contiguousBuffer = null;
        var stage = "get_buffer_count";
        uint sampleBufferCount = 0;
        try
        {
            Marshal.ThrowExceptionForHR(sample.GetBufferCount(out sampleBufferCount));
            stage = "convert_to_contiguous_buffer";
            Marshal.ThrowExceptionForHR(sample.ConvertToContiguousBuffer(out contiguousBufferPtr));
            contiguousBuffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(contiguousBufferPtr);
            stage = "get_current_length";
            Marshal.ThrowExceptionForHR(contiguousBuffer.GetCurrentLength(out var currentLength));
            if (currentLength <= 0)
            {
                throw new InvalidOperationException("Media Foundation H.264 decoder returned an empty frame.");
            }

            stage = "lock_buffer";
            Marshal.ThrowExceptionForHR(contiguousBuffer.Lock(out var scan0, out _, out _));
            try
            {
                if (decoderTransformState.OutputWidth <= 0 || decoderTransformState.OutputHeight <= 0)
                {
                    throw new InvalidOperationException("Media Foundation H.264 decoder output dimensions are unknown.");
                }

                return decoderTransformState.OutputSubtype switch
                {
                    var subtype when subtype == MfVideoFormatArgb32 || subtype == MfVideoFormatRgb32 =>
                        CreateAvaloniaBitmap(scan0, currentLength, decoderTransformState.OutputWidth, decoderTransformState.OutputHeight, decoderTransformState.OutputStride, decoderTransformState.OutputRequiresOpaqueAlpha),
                    var subtype when subtype == MfVideoFormatNv12 =>
                        CreateAvaloniaBitmapFromNv12(scan0, currentLength, decoderTransformState.OutputWidth, decoderTransformState.OutputHeight, decoderTransformState.OutputStride),
                    var subtype when subtype == MfVideoFormatYuy2 =>
                        CreateAvaloniaBitmapFromYuy2(scan0, currentLength, decoderTransformState.OutputWidth, decoderTransformState.OutputHeight, decoderTransformState.OutputStride),
                    _ => throw new NotSupportedException($"Media Foundation H.264 decoder output subtype '{FormatVideoSubtype(decoderTransformState.OutputSubtype)}' is not supported.")
                };
            }
            finally
            {
                contiguousBuffer.Unlock();
            }
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_decoder_output_buffer_access_failed",
                configuration?.StreamEpoch ?? 0,
                $"stage={stage}; subtype={FormatVideoSubtype(decoderTransformState.OutputSubtype)}; sample_buffers={sampleBufferCount}; transform_id={decoderTransformState.TransformId}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
            throw;
        }
        finally
        {
            if (contiguousBuffer is not null)
            {
                ReleaseComObject(contiguousBuffer);
            }

            if (contiguousBufferPtr != IntPtr.Zero)
            {
                Marshal.Release(contiguousBufferPtr);
            }
        }
    }

    private static Bitmap CreateAvaloniaBitmap(
        IntPtr source,
        int sourceLength,
        int width,
        int height,
        int stride,
        bool forceOpaqueAlpha)
    {
        var targetStride = checked(width * 4);
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul);

        using var framebuffer = writeable.Lock();
        var targetRow = new byte[targetStride];
        var effectiveStride = stride != 0 ? stride : targetStride;
        var absoluteStride = Math.Abs(effectiveStride);

        for (var y = 0; y < height; y++)
        {
            var sourceOffset = effectiveStride >= 0
                ? checked(y * absoluteStride)
                : checked((height - 1 - y) * absoluteStride);
            if (sourceOffset + targetStride > sourceLength)
            {
                throw new InvalidOperationException("Media Foundation H.264 decoder returned a truncated frame buffer.");
            }

            Marshal.Copy(source + sourceOffset, targetRow, 0, targetStride);
            if (forceOpaqueAlpha)
            {
                for (var pixel = 3; pixel < targetStride; pixel += 4)
                {
                    targetRow[pixel] = byte.MaxValue;
                }
            }

            Marshal.Copy(targetRow, 0, framebuffer.Address + (y * framebuffer.RowBytes), targetStride);
        }

        return writeable;
    }

    private static Bitmap CreateAvaloniaBitmapFromNv12(
        IntPtr source,
        int sourceLength,
        int width,
        int height,
        int stride)
    {
        var lumaStride = Math.Abs(stride != 0 ? stride : width);
        var chromaOffset = checked(lumaStride * height);
        var chromaRows = (height + 1) / 2;
        var requiredLength = checked(chromaOffset + (lumaStride * chromaRows));
        if (requiredLength > sourceLength)
        {
            throw new InvalidOperationException("Media Foundation H.264 decoder returned a truncated NV12 frame buffer.");
        }

        var sourceBytes = new byte[requiredLength];
        Marshal.Copy(source, sourceBytes, 0, requiredLength);

        var targetStride = checked(width * 4);
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul);

        using var framebuffer = writeable.Lock();
        var targetRow = new byte[targetStride];

        for (var y = 0; y < height; y++)
        {
            var yRowOffset = checked(y * lumaStride);
            var uvRowOffset = checked(chromaOffset + ((y / 2) * lumaStride));
            for (var x = 0; x < width; x++)
            {
                var yValue = sourceBytes[yRowOffset + x];
                var uvOffset = uvRowOffset + ((x / 2) * 2);
                var uValue = sourceBytes[uvOffset];
                var vValue = sourceBytes[uvOffset + 1];
                WriteBgraFromYuv(targetRow, x * 4, yValue, uValue, vValue);
            }

            Marshal.Copy(targetRow, 0, framebuffer.Address + (y * framebuffer.RowBytes), targetStride);
        }

        return writeable;
    }

    private static Bitmap CreateAvaloniaBitmapFromYuy2(
        IntPtr source,
        int sourceLength,
        int width,
        int height,
        int stride)
    {
        var packedStride = Math.Abs(stride != 0 ? stride : checked(width * 2));
        var requiredLength = checked(packedStride * height);
        if (requiredLength > sourceLength)
        {
            throw new InvalidOperationException("Media Foundation H.264 decoder returned a truncated YUY2 frame buffer.");
        }

        var sourceBytes = new byte[requiredLength];
        Marshal.Copy(source, sourceBytes, 0, requiredLength);

        var targetStride = checked(width * 4);
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Unpremul);

        using var framebuffer = writeable.Lock();
        var targetRow = new byte[targetStride];

        for (var y = 0; y < height; y++)
        {
            var sourceRowOffset = checked(y * packedStride);
            var sourceRowLimit = checked(sourceRowOffset + packedStride);
            var targetOffset = 0;
            for (var sourceOffset = sourceRowOffset; sourceOffset + 3 < sourceRowLimit && targetOffset < targetStride; sourceOffset += 4)
            {
                var y0 = sourceBytes[sourceOffset];
                var u = sourceBytes[sourceOffset + 1];
                var y1 = sourceBytes[sourceOffset + 2];
                var v = sourceBytes[sourceOffset + 3];
                WriteBgraFromYuv(targetRow, targetOffset, y0, u, v);
                targetOffset += 4;
                if (targetOffset < targetStride)
                {
                    WriteBgraFromYuv(targetRow, targetOffset, y1, u, v);
                    targetOffset += 4;
                }
            }

            Marshal.Copy(targetRow, 0, framebuffer.Address + (y * framebuffer.RowBytes), targetStride);
        }

        return writeable;
    }

    private static void WriteBgraFromYuv(byte[] targetRow, int offset, byte yValue, byte uValue, byte vValue)
    {
        var c = Math.Max(0, yValue - 16);
        var d = uValue - 128;
        var e = vValue - 128;
        targetRow[offset] = ClampToByte((298 * c + 516 * d + 128) >> 8);
        targetRow[offset + 1] = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
        targetRow[offset + 2] = ClampToByte((298 * c + 409 * e + 128) >> 8);
        targetRow[offset + 3] = byte.MaxValue;
    }

    private static byte ClampToByte(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= byte.MaxValue)
        {
            return byte.MaxValue;
        }

        return (byte)value;
    }

    private static int GetDefaultStrideForSubtype(Guid subtype, int width)
    {
        if (subtype == MfVideoFormatArgb32 || subtype == MfVideoFormatRgb32)
        {
            return checked(width * 4);
        }

        if (subtype == MfVideoFormatYuy2)
        {
            return checked(width * 2);
        }

        if (subtype == MfVideoFormatNv12)
        {
            return width;
        }

        return checked(width * 4);
    }

    private static int GetOutputSubtypeRank(Guid subtype)
    {
        if (subtype == MfVideoFormatArgb32)
        {
            return 0;
        }

        if (subtype == MfVideoFormatRgb32)
        {
            return 1;
        }

        if (subtype == MfVideoFormatNv12)
        {
            return 2;
        }

        if (subtype == MfVideoFormatYuy2)
        {
            return 3;
        }

        return int.MaxValue;
    }

    private static string FormatVideoSubtype(Guid subtype)
    {
        if (subtype == Guid.Empty)
        {
            return "(none)";
        }

        if (subtype == MfVideoFormatArgb32)
        {
            return "ARGB32";
        }

        if (subtype == MfVideoFormatRgb32)
        {
            return "RGB32";
        }

        if (subtype == MfVideoFormatNv12)
        {
            return "NV12";
        }

        if (subtype == MfVideoFormatYuy2)
        {
            return "YUY2";
        }

        return subtype.ToString("D");
    }

    private void ResetDecoderState(string reason)
    {
        var activeEpoch = configuration?.StreamEpoch ?? 0;
        LogEpochSummary(reason);
        ReleaseTransformState(activeTransformState, flush: true);
        activeTransformState = null;

        configuration = null;
        outputWidth = 0;
        outputHeight = 0;
        outputStride = 0;
        outputRequiresOpaqueAlpha = false;
        outputSubtype = Guid.Empty;
        nextInputSampleTimeHns = 0;
        ResetEpochDiagnostics();
        LogLifecycle("screenshare_h264_decoder_reset", activeEpoch, $"reason={Sanitize(reason)}");
    }

    private static bool ByteArraysEqual(byte[] left, byte[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch
        {
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        return value.Replace(';', ',').Trim();
    }

    private static ulong PackFrameSize(int width, int height)
    {
        return ((ulong)(uint)width << 32) | (uint)height;
    }

    private static string FormatOutputSampleProvider(OutputSampleProviderKind providerKind)
    {
        return providerKind switch
        {
            OutputSampleProviderKind.AlignedContiguousBuffer => "aligned_contiguous_buffer",
            OutputSampleProviderKind.TwoDVideoBuffer => "two_d_video_buffer",
            _ => "unknown",
        };
    }

    private static OutputSampleProviderKind GetOutputSampleProvider(OutputSampleShapeKind shapeKind)
    {
        return shapeKind switch
        {
            OutputSampleShapeKind.AlignedContiguousLengthZero or OutputSampleShapeKind.AlignedContiguousLengthPreset
                => OutputSampleProviderKind.AlignedContiguousBuffer,
            OutputSampleShapeKind.TwoDVideoBufferLengthZero or OutputSampleShapeKind.TwoDVideoBufferLengthPreset
                => OutputSampleProviderKind.TwoDVideoBuffer,
            _ => OutputSampleProviderKind.Unknown,
        };
    }

    private static string FormatOutputSampleShape(OutputSampleShapeKind shapeKind)
    {
        return shapeKind switch
        {
            OutputSampleShapeKind.AlignedContiguousLengthZero => "aligned_contiguous_length_zero",
            OutputSampleShapeKind.AlignedContiguousLengthPreset => "aligned_contiguous_length_preset",
            OutputSampleShapeKind.TwoDVideoBufferLengthZero => "two_d_video_buffer_length_zero",
            OutputSampleShapeKind.TwoDVideoBufferLengthPreset => "two_d_video_buffer_length_preset",
            OutputSampleShapeKind.NullSampleDiagnostic => "null_sample_diagnostic",
            _ => "unknown",
        };
    }

    private static string FormatOutputSubtypeProbeKind(OutputSubtypeProbeKind probeKind)
    {
        return probeKind switch
        {
            OutputSubtypeProbeKind.NativeAdvertisedFirstSupported => "native_advertised_first_supported",
            OutputSubtypeProbeKind.ExplicitNv12 => "explicit_nv12",
            OutputSubtypeProbeKind.ExplicitYuy2 => "explicit_yuy2",
            _ => "unknown",
        };
    }

    private static string FormatOutputRetrievalMode(OutputRetrievalMode retrievalMode)
    {
        return retrievalMode switch
        {
            OutputRetrievalMode.NormalProcessOutput => "normal_process_output",
            OutputRetrievalMode.EndOfStreamDrain => "end_of_stream_drain",
            _ => "unknown",
        };
    }

    private static string FormatOutputCombination(OutputContractCombination combination)
        => $"{FormatOutputSampleShape(combination.Shape)}+{FormatOutputRetrievalMode(combination.RetrievalMode)}";

    private static string FormatInputStatusFlags(uint flags)
    {
        if (flags == 0)
        {
            return "none";
        }

        return (flags & MftInputStatusAcceptData) != 0
            ? "accept_data"
            : $"0x{flags:X8}";
    }

    private static string FormatOutputStreamFlags(uint flags)
    {
        if (flags == 0)
        {
            return "none";
        }

        var parts = new List<string>();
        if ((flags & MftOutputStreamWholeSamples) != 0)
        {
            parts.Add("whole_samples");
        }

        if ((flags & MftOutputStreamSingleSamplePerBuffer) != 0)
        {
            parts.Add("single_sample_per_buffer");
        }

        if ((flags & MftOutputStreamFixedSampleSize) != 0)
        {
            parts.Add("fixed_sample_size");
        }

        if ((flags & MftOutputStreamProvidesSamples) != 0)
        {
            parts.Add("provides_samples");
        }

        if ((flags & MftOutputStreamCanProvideSamples) != 0)
        {
            parts.Add("can_provide_samples");
        }

        return parts.Count > 0 ? string.Join('|', parts) : $"0x{flags:X8}";
    }

    private static string FormatOutputStatusFlags(uint flags)
    {
        if (flags == 0)
        {
            return "none";
        }

        return (flags & MftOutputStatusSampleReady) != 0
            ? "sample_ready"
            : $"0x{flags:X8}";
    }

    private static string FormatOutputSampleOrigin(OutputSampleOrigin sampleOrigin)
    {
        return sampleOrigin switch
        {
            OutputSampleOrigin.CallerSampleReused => "caller_sample_reused",
            OutputSampleOrigin.MftReturnedDifferentSample => "mft_returned_different_sample",
            _ => "none",
        };
    }

    private static string FormatInputSampleStrategy(InputSampleStrategyKind strategyKind)
    {
        return strategyKind switch
        {
            InputSampleStrategyKind.TimedCleanPointDiscontinuity => "timed_clean_point_discontinuity",
            InputSampleStrategyKind.TimedCleanPoint => "timed_clean_point",
            InputSampleStrategyKind.TimedOnly => "timed_only",
            InputSampleStrategyKind.TimeOnlyCleanPointDiscontinuity => "time_only_clean_point_discontinuity",
            _ => "unknown",
        };
    }

    private static string FormatStartupSequence(TransformStartupSequenceKind startupSequence)
    {
        return startupSequence switch
        {
            TransformStartupSequenceKind.TypesBeforeStart => "types_before_start",
            TransformStartupSequenceKind.StartBeforeOutputType => "start_before_output_type",
            _ => "unknown",
        };
    }

    private static string FormatDecoderBackend(DecoderBackendKind backendKind)
    {
        return backendKind switch
        {
            DecoderBackendKind.SoftwareFixedClsid => "software_fixed_clsid",
            DecoderBackendKind.HardwareEnumFirst => "hardware_enum_first",
            _ => "unknown",
        };
    }

    private static string FormatDecoderAttributeProfile(DecoderAttributeProfileKind attributeProfile)
    {
        return attributeProfile switch
        {
            DecoderAttributeProfileKind.Baseline => "baseline",
            DecoderAttributeProfileKind.LowLatency => "low_latency",
            _ => "unknown",
        };
    }

    private static string FormatDecoderBackendProfile(DecoderBackendProfileCombination backendProfile)
        => $"{FormatDecoderBackend(backendProfile.Backend)}+{FormatDecoderAttributeProfile(backendProfile.AttributeProfile)}";

    private static IReadOnlyList<InputSampleStrategyKind> EnumerateFirstFrameInputSampleStrategies()
        => new[]
        {
            InputSampleStrategyKind.TimedCleanPointDiscontinuity,
            InputSampleStrategyKind.TimedCleanPoint,
            InputSampleStrategyKind.TimedOnly,
            InputSampleStrategyKind.TimeOnlyCleanPointDiscontinuity,
        };

    private static InputSampleStrategyKind GetFallbackInputSampleStrategyForFrame(bool isKeyFrame, bool isFirstFrameOfEpoch)
    {
        if (preferredInputSampleStrategy != InputSampleStrategyKind.Unknown)
        {
            return preferredInputSampleStrategy;
        }

        if (isFirstFrameOfEpoch)
        {
            return InputSampleStrategyKind.TimedCleanPointDiscontinuity;
        }

        return isKeyFrame
            ? InputSampleStrategyKind.TimedCleanPoint
            : InputSampleStrategyKind.TimedOnly;
    }

    private static InputSampleContract ResolveInputSampleContract(
        InputSampleStrategyKind strategyKind,
        bool isKeyFrame,
        bool isFirstFrameOfEpoch)
    {
        if (!isFirstFrameOfEpoch)
        {
            return strategyKind switch
            {
                InputSampleStrategyKind.TimeOnlyCleanPointDiscontinuity => new InputSampleContract(
                    SetSampleTime: true,
                    SetSampleDuration: false,
                    SetCleanPoint: isKeyFrame,
                    SetDiscontinuity: false),
                _ => new InputSampleContract(
                    SetSampleTime: true,
                    SetSampleDuration: true,
                    SetCleanPoint: isKeyFrame,
                    SetDiscontinuity: false),
            };
        }

        return strategyKind switch
        {
            InputSampleStrategyKind.TimedCleanPointDiscontinuity => new InputSampleContract(true, true, true, true),
            InputSampleStrategyKind.TimedCleanPoint => new InputSampleContract(true, true, true, false),
            InputSampleStrategyKind.TimedOnly => new InputSampleContract(true, true, false, false),
            InputSampleStrategyKind.TimeOnlyCleanPointDiscontinuity => new InputSampleContract(true, false, true, true),
            _ => new InputSampleContract(true, true, isKeyFrame, false),
        };
    }

    private const int MftMessageCommandFlush = 0;
    private const int MftMessageCommandDrain = 1;
    private const int MftMessageNotifyBeginStreaming = 0x10000000;
    private const int MftMessageNotifyEndOfStream = 0x10000002;
    private const int MftMessageNotifyStartOfStream = 0x10000003;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int dwFlags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAlignedMemoryBuffer(int maxLength, uint alignment, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreate2DMediaBuffer(
        uint width,
        uint height,
        uint fourCC,
        [MarshalAs(UnmanagedType.Bool)] bool bottomUpWhenLinear,
        out IMFMediaBuffer buffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr punkOuter,
        uint clsContext,
        ref Guid iid,
        out IntPtr instance);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(
        ref Guid guidCategory,
        uint flags,
        IntPtr inputType,
        IntPtr outputType,
        out IntPtr activates,
        out int count);

    private sealed class DecoderConfiguration
    {
        public DecoderConfiguration(long streamEpoch, string codecProfile, byte[] decoderConfigData)
        {
            StreamEpoch = streamEpoch;
            CodecProfile = codecProfile;
            DecoderConfigData = decoderConfigData;
            NalLengthSize = TryParseNalLengthSize(decoderConfigData, out var nalLengthSize)
                ? nalLengthSize
                : 0;
            if (TryParseExpectedCodedSize(decoderConfigData, out var expectedWidth, out var expectedHeight))
            {
                ExpectedCodedWidth = expectedWidth;
                ExpectedCodedHeight = expectedHeight;
            }
        }

        public long StreamEpoch { get; }

        public string CodecProfile { get; }

        public byte[] DecoderConfigData { get; }

        public int NalLengthSize { get; }

        public int ExpectedCodedWidth { get; }

        public int ExpectedCodedHeight { get; }
    }

    private sealed class DecoderTransformState
    {
        public DecoderTransformState(
            IMFTransform transform,
            int transformId,
            DecoderBackendKind backendKind,
            DecoderAttributeProfileKind attributeProfile,
            string activationSource,
            string friendlyName,
            OutputSubtypeProbeKind outputSubtypeProbeKind)
        {
            Transform = transform;
            TransformId = transformId;
            BackendKind = backendKind;
            AttributeProfile = attributeProfile;
            ActivationSource = activationSource;
            FriendlyName = friendlyName;
            OutputSubtypeProbeKind = outputSubtypeProbeKind;
        }

        public IMFTransform Transform { get; }

        public int TransformId { get; }

        public DecoderBackendKind BackendKind { get; }

        public DecoderAttributeProfileKind AttributeProfile { get; }

        public string ActivationSource { get; }

        public string FriendlyName { get; }

        public OutputSubtypeProbeKind OutputSubtypeProbeKind { get; set; }

        public Guid EffectiveOutputSubtypeCandidate { get; set; }

        public bool OutputSubtypeCandidateWasNativeAdvertised { get; set; }

        public bool OutputTypeConfigured { get; set; }

        public bool OutputTypeVerified { get; set; }

        public bool LoggedOutputTypeVerification { get; set; }

        public bool InputTypeConfigured { get; set; }

        public bool BeginStreamingSent { get; set; }

        public bool StartOfStreamSent { get; set; }

        public int BeginStreamingHresult { get; set; }

        public int StartOfStreamHresult { get; set; }

        public TransformStartupSequenceKind StartupSequence { get; set; }

        public bool StartupSequenceVerified { get; set; }

        public bool FullyStartedBeforeFirstInput { get; set; }

        public bool LoggedStartupSequence { get; set; }

        public int OutputWidth { get; set; }

        public int OutputHeight { get; set; }

        public int OutputStride { get; set; }

        public bool OutputRequiresOpaqueAlpha { get; set; }

        public Guid OutputSubtype { get; set; }

        public uint OutputBufferSize { get; set; }

        public uint OutputBufferAlignment { get; set; }

        public bool TransformAttributesAvailable { get; set; }

        public int TransformAttributesHresult { get; set; }

        public string TransformAttributesSnapshot { get; set; } = "unavailable";

        public string TransformAttributesSnapshotBeforeProfile { get; set; } = "unavailable";

        public string TransformAttributesSnapshotAfterProfile { get; set; } = "unavailable";

        public bool InputStreamAttributesAvailable { get; set; }

        public int InputStreamAttributesHresult { get; set; }

        public string InputStreamAttributesSnapshot { get; set; } = "unavailable";

        public bool OutputStreamAttributesAvailable { get; set; }

        public int OutputStreamAttributesHresult { get; set; }

        public string OutputStreamAttributesSnapshot { get; set; } = "unavailable";

        public bool LowLatencyRequested { get; set; }

        public bool LowLatencyAppliedToTransform { get; set; }

        public int TransformLowLatencyHresult { get; set; }

        public bool LowLatencyAppliedToInputMediaType { get; set; }

        public int InputMediaTypeLowLatencyHresult { get; set; }

        public bool LowLatencyAppliedToOutputMediaType { get; set; }

        public int OutputMediaTypeLowLatencyHresult { get; set; }

        public bool CodecApiAvailable { get; set; }

        public bool CodecApiSupported { get; set; }

        public int CodecApiIsSupportedHresult { get; set; }

        public bool CodecApiModifiable { get; set; }

        public int CodecApiIsModifiableHresult { get; set; }

        public bool CodecApiLowLatencyApplied { get; set; }

        public int CodecApiSetValueHresult { get; set; }

        public bool AttributeProfileFailure { get; set; }

        public int SetOutputTypeHresult { get; set; }

        public int GetOutputCurrentTypeHresult { get; set; }

        public int GetOutputStatusAfterConfigurationHresult { get; set; }

        public uint GetOutputStatusAfterConfigurationFlags { get; set; }
    }

    private readonly record struct OutputMediaTypeMetadata(
        int Width,
        int Height,
        int Stride,
        Guid Subtype,
        bool RequiresOpaqueAlpha);

    private readonly record struct InputSampleContract(
        bool SetSampleTime,
        bool SetSampleDuration,
        bool SetCleanPoint,
        bool SetDiscontinuity);

    private readonly record struct OutputContractCombination(
        OutputSampleShapeKind Shape,
        OutputRetrievalMode RetrievalMode)
    {
        public OutputSampleProviderKind Provider => GetOutputSampleProvider(Shape);
    }

    private readonly record struct OutputProcessingResult(
        int TransformId,
        bool OutputTypeConfiguredOnTransform,
        bool OutputTypeVerifiedOnTransform,
        int SetOutputTypeHresult,
        int GetOutputCurrentTypeHresult,
        int OutputStatusAfterConfigurationHresult,
        uint OutputStatusAfterConfigurationFlags,
        Bitmap? Bitmap,
        int ProcessOutputAttempts,
        bool StreamChangeObserved,
        bool CallerProvidedSample,
        OutputSampleOrigin SampleOrigin,
        uint OutputFlags,
        uint OutputDataBufferStatus,
        uint InputStatusFlags,
        int InputStatusHresult,
        uint OutputStatusFlags,
        int OutputStatusHresult,
        int ProcessOutputHresult,
        string FailureStage,
        Exception? Failure,
        bool NeedMoreInput,
        bool SuccessWithoutSample,
        bool OutputReadyWithoutSample,
        bool ProviderContractFailure,
        bool RetrievalContractFailure)
    {
        public DecoderBackendKind BackendKindOnTransform { get; init; }

        public DecoderAttributeProfileKind AttributeProfileOnTransform { get; init; }

        public string ActivationSourceOnTransform { get; init; } = "unknown";

        public string FriendlyNameOnTransform { get; init; } = "unknown";

        public string TransformAttributesSnapshotOnTransform { get; init; } = "unavailable";

        public string TransformAttributesSnapshotBeforeProfileOnTransform { get; init; } = "unavailable";

        public string TransformAttributesSnapshotAfterProfileOnTransform { get; init; } = "unavailable";

        public string InputStreamAttributesSnapshotOnTransform { get; init; } = "unavailable";

        public string OutputStreamAttributesSnapshotOnTransform { get; init; } = "unavailable";

        public OutputSubtypeProbeKind OutputSubtypeProbeKindOnTransform { get; init; }

        public Guid OutputSubtypeCandidateOnTransform { get; init; }

        public bool OutputSubtypeCandidateWasNativeAdvertisedOnTransform { get; init; }

        public bool LowLatencyRequestedOnTransform { get; init; }

        public bool LowLatencyAppliedOnTransform { get; init; }

        public bool TransformLowLatencyAppliedOnTransform { get; init; }

        public int TransformLowLatencyHresultOnTransform { get; init; }

        public bool InputMediaTypeLowLatencyAppliedOnTransform { get; init; }

        public int InputMediaTypeLowLatencyHresultOnTransform { get; init; }

        public bool OutputMediaTypeLowLatencyAppliedOnTransform { get; init; }

        public int OutputMediaTypeLowLatencyHresultOnTransform { get; init; }

        public bool CodecApiAvailableOnTransform { get; init; }

        public bool CodecApiSupportedOnTransform { get; init; }

        public int CodecApiIsSupportedHresultOnTransform { get; init; }

        public bool CodecApiModifiableOnTransform { get; init; }

        public int CodecApiIsModifiableHresultOnTransform { get; init; }

        public bool CodecApiLowLatencyAppliedOnTransform { get; init; }

        public int CodecApiSetValueHresultOnTransform { get; init; }

        public bool InputTypeConfiguredOnTransform { get; init; }

        public bool BeginStreamingSentOnTransform { get; init; }

        public bool StartOfStreamSentOnTransform { get; init; }

        public int BeginStreamingHresultOnTransform { get; init; }

        public int StartOfStreamHresultOnTransform { get; init; }

        public TransformStartupSequenceKind StartupSequenceOnTransform { get; init; }

        public bool StartupSequenceVerifiedOnTransform { get; init; }

        public bool FullyStartedBeforeFirstInputOnTransform { get; init; }
    }

    private enum OutputSampleShapeKind
    {
        Unknown = 0,
        AlignedContiguousLengthZero = 1,
        AlignedContiguousLengthPreset = 2,
        TwoDVideoBufferLengthZero = 3,
        TwoDVideoBufferLengthPreset = 4,
        NullSampleDiagnostic = 5,
    }

    private enum OutputSubtypeProbeKind
    {
        Unknown = 0,
        NativeAdvertisedFirstSupported = 1,
        ExplicitNv12 = 2,
        ExplicitYuy2 = 3,
    }

    private enum OutputSampleProviderKind
    {
        Unknown = 0,
        AlignedContiguousBuffer = 1,
        TwoDVideoBuffer = 2,
    }

    private enum OutputRetrievalMode
    {
        Unknown = 0,
        NormalProcessOutput = 1,
        EndOfStreamDrain = 2,
    }

    private enum OutputSampleOrigin
    {
        None = 0,
        CallerSampleReused = 1,
        MftReturnedDifferentSample = 2,
    }

    private enum DecoderBackendKind
    {
        Unknown = 0,
        SoftwareFixedClsid = 1,
        HardwareEnumFirst = 2,
    }

    private enum DecoderAttributeProfileKind
    {
        Unknown = 0,
        Baseline = 1,
        LowLatency = 2,
    }

    private enum InputSampleStrategyKind
    {
        Unknown = 0,
        TimedCleanPointDiscontinuity = 1,
        TimedCleanPoint = 2,
        TimedOnly = 3,
        TimeOnlyCleanPointDiscontinuity = 4,
    }

    private enum TransformStartupSequenceKind
    {
        Unknown = 0,
        TypesBeforeStart = 1,
        StartBeforeOutputType = 2,
    }

    private sealed class H264BitReader
    {
        private readonly byte[] bytes;
        private int bitOffset;

        public H264BitReader(byte[] bytes)
        {
            this.bytes = bytes;
        }

        public bool ReadFlag() => ReadBits(1) != 0;

        public void SkipBits(int bitCount)
        {
            if (bitCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            }

            bitOffset += bitCount;
            if (bitOffset > bytes.Length * 8)
            {
                throw new InvalidOperationException("H.264 bit reader reached the end of the SPS.");
            }
        }

        public int ReadBits(int bitCount)
        {
            if (bitCount <= 0 || bitCount > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            }

            var value = 0;
            for (var i = 0; i < bitCount; i++)
            {
                if (bitOffset >= bytes.Length * 8)
                {
                    throw new InvalidOperationException("H.264 bit reader reached the end of the SPS.");
                }

                var byteIndex = bitOffset / 8;
                var bitIndex = 7 - (bitOffset % 8);
                value = (value << 1) | ((bytes[byteIndex] >> bitIndex) & 0x01);
                bitOffset++;
            }

            return value;
        }

        public int ReadUnsignedExpGolomb()
        {
            var leadingZeroBits = 0;
            while (!ReadFlag())
            {
                leadingZeroBits++;
                if (leadingZeroBits > 31)
                {
                    throw new InvalidOperationException("Invalid H.264 Exp-Golomb code.");
                }
            }

            if (leadingZeroBits == 0)
            {
                return 0;
            }

            return ((1 << leadingZeroBits) - 1) + ReadBits(leadingZeroBits);
        }

        public int ReadSignedExpGolomb()
        {
            var codeNum = ReadUnsignedExpGolomb();
            var magnitude = (codeNum + 1) / 2;
            return (codeNum & 1) == 0 ? -magnitude : magnitude;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid GuidMajorType;
        public Guid GuidSubtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint DwFlags;
        public uint CbSize;
        public uint CbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftInputStreamInfo
    {
        public long HnsMaxLatency;
        public uint DwFlags;
        public uint CbSize;
        public uint CbMaxLookahead;
        public uint CbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint DwStreamId;
        public IntPtr PSample;
        public uint DwStatus;
        public IntPtr PEvents;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct CodecApiVariant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(2)]
        public ushort Reserved1;

        [FieldOffset(4)]
        public ushort Reserved2;

        [FieldOffset(6)]
        public ushort Reserved3;

        [FieldOffset(8)]
        public uint UlVal;

        public static CodecApiVariant FromUInt32(uint value)
            => new()
            {
                Vt = VariantTypeUi4,
                UlVal = value,
            };
    }

    [ComImport]
    [Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out uint pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        new int Compare(IMFAttributes theirs, uint matchType, out int result);
        new int GetUINT32(ref Guid guidKey, out uint punValue);
        new int GetUINT64(ref Guid guidKey, out ulong punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new int SetItem(ref Guid guidKey, IntPtr value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, uint unValue);
        new int SetUINT64(ref Guid guidKey, ulong unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out uint pcItems);
        new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);

        int ActivateObject(ref Guid riid, out IntPtr ppv);
        int ShutdownObject();
        int DetachObject();
    }

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        int GetItem(ref Guid guidKey, IntPtr pValue);
        int GetItemType(ref Guid guidKey, out uint pType);
        int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        int Compare(IMFAttributes theirs, uint matchType, out int result);
        int GetUINT32(ref Guid guidKey, out uint punValue);
        int GetUINT64(ref Guid guidKey, out ulong punValue);
        int GetDouble(ref Guid guidKey, out double pfValue);
        int GetGUID(ref Guid guidKey, out Guid pguidValue);
        int GetStringLength(ref Guid guidKey, out uint pcchLength);
        int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        int SetItem(ref Guid guidKey, IntPtr value);
        int DeleteItem(ref Guid guidKey);
        int DeleteAllItems();
        int SetUINT32(ref Guid guidKey, uint unValue);
        int SetUINT64(ref Guid guidKey, ulong unValue);
        int SetDouble(ref Guid guidKey, double fValue);
        int SetGUID(ref Guid guidKey, ref Guid guidValue);
        int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out uint pcItems);
        int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out uint pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        new int Compare(IMFAttributes theirs, uint matchType, out int result);
        new int GetUINT32(ref Guid guidKey, out uint punValue);
        new int GetUINT64(ref Guid guidKey, out ulong punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new int SetItem(ref Guid guidKey, IntPtr value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, uint unValue);
        new int SetUINT64(ref Guid guidKey, ulong unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out uint pcItems);
        new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);

        int GetMajorType(out Guid pguidMajorType);
        int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
        int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
        int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        int Unlock();
        int GetCurrentLength(out int pcbCurrentLength);
        int SetCurrentLength(int cbCurrentLength);
        int GetMaxLength(out int pcbMaxLength);
    }

    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample : IMFAttributes
    {
        new int GetItem(ref Guid guidKey, IntPtr pValue);
        new int GetItemType(ref Guid guidKey, out uint pType);
        new int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        new int Compare(IMFAttributes theirs, uint matchType, out int result);
        new int GetUINT32(ref Guid guidKey, out uint punValue);
        new int GetUINT64(ref Guid guidKey, out ulong punValue);
        new int GetDouble(ref Guid guidKey, out double pfValue);
        new int GetGUID(ref Guid guidKey, out Guid pguidValue);
        new int GetStringLength(ref Guid guidKey, out uint pcchLength);
        new int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        new int GetAllocatedString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
        new int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
        new int GetBlob(ref Guid guidKey, [Out] byte[] ipBuf, uint cbBufSize, out uint pcbBlobSize);
        new int GetAllocatedBlob(ref Guid guidKey, out IntPtr ipBuf, out uint pcbSize);
        new int GetUnknown(ref Guid guidKey, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        new int SetItem(ref Guid guidKey, IntPtr value);
        new int DeleteItem(ref Guid guidKey);
        new int DeleteAllItems();
        new int SetUINT32(ref Guid guidKey, uint unValue);
        new int SetUINT64(ref Guid guidKey, ulong unValue);
        new int SetDouble(ref Guid guidKey, double fValue);
        new int SetGUID(ref Guid guidKey, ref Guid guidValue);
        new int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        new int SetBlob(ref Guid guidKey, [In] byte[] pBuf, uint cbBufSize);
        new int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
        new int LockStore();
        new int UnlockStore();
        new int GetCount(out uint pcItems);
        new int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
        new int CopyAllItems(IMFAttributes pDest);

        int GetSampleFlags(out uint pdwSampleFlags);
        int SetSampleFlags(uint dwSampleFlags);
        int GetSampleTime(out long phnsSampleTime);
        int SetSampleTime(long hnsSampleTime);
        int GetSampleDuration(out long phnsSampleDuration);
        int SetSampleDuration(long hnsSampleDuration);
        int GetBufferCount(out uint pdwBufferCount);
        int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        int ConvertToContiguousBuffer(out IntPtr ppBuffer);
        int AddBuffer(IMFMediaBuffer pBuffer);
        int RemoveBufferByIndex(uint dwIndex);
        int RemoveAllBuffers();
        int GetTotalLength(out uint pcbTotalLength);
        int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport]
    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFTransform
    {
        int GetStreamLimits(out uint inputMinimum, out uint inputMaximum, out uint outputMinimum, out uint outputMaximum);
        int GetStreamCount(out uint inputStreams, out uint outputStreams);
        int GetStreamIDs(uint inputIdArraySize, IntPtr inputIds, uint outputIdArraySize, IntPtr outputIds);
        int GetInputStreamInfo(uint inputStreamId, out MftInputStreamInfo streamInfo);
        int GetOutputStreamInfo(uint outputStreamId, out MftOutputStreamInfo streamInfo);
        int GetAttributes(out IMFAttributes attributes);
        int GetInputStreamAttributes(uint inputStreamId, out IMFAttributes attributes);
        int GetOutputStreamAttributes(uint outputStreamId, out IMFAttributes attributes);
        int DeleteInputStream(uint streamId);
        int AddInputStreams(uint streams, IntPtr streamIds);
        int GetInputAvailableType(uint inputStreamId, uint typeIndex, out IMFMediaType type);
        int GetOutputAvailableType(uint outputStreamId, uint typeIndex, out IMFMediaType type);
        int SetInputType(uint inputStreamId, IMFMediaType type, uint flags);
        int SetOutputType(uint outputStreamId, IMFMediaType type, uint flags);
        int GetInputCurrentType(uint inputStreamId, out IMFMediaType type);
        int GetOutputCurrentType(uint outputStreamId, out IMFMediaType type);
        int GetInputStatus(uint inputStreamId, out uint flags);
        int GetOutputStatus(out uint flags);
        int SetOutputBounds(long lowerBound, long upperBound);
        int ProcessEvent(uint inputStreamId, IntPtr mediaEvent);
        int ProcessMessage(int message, IntPtr param);
        int ProcessInput(uint inputStreamId, IMFSample sample, uint flags);
        int ProcessOutput(
            uint flags,
            uint outputBufferCount,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] MftOutputDataBuffer[] outputSamples,
            out uint status);
    }

    [ComImport]
    [Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        int IsSupported(ref Guid api);
        int IsModifiable(ref Guid api);
        int GetParameterRange(ref Guid api, IntPtr values, IntPtr modifiableValues);
        int GetParameterValues(ref Guid api, IntPtr values);
        int GetDefaultValue(ref Guid api, IntPtr value);
        int GetValue(ref Guid api, IntPtr value);
        int SetValue(ref Guid api, IntPtr value);
        int RegisterForEvent(ref Guid api, int userData);
        int UnregisterForEvent(ref Guid api);
        int SetAllDefaults();
        int SetValueWithNotify(ref Guid api, IntPtr value, IntPtr callback, int userData);
        int SetAllDefaultsWithNotify(IntPtr callback, int userData);
    }
}