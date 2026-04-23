using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using Avalonia.Media.Imaging;
using NLink.App.Configuration;
using NLink.Core.Logging;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed partial class MediaFoundationH264BitmapDecoder
{
    private static OutputSampleShapeKind preferredOutputSampleShape = OutputSampleShapeKind.Unknown;
    private static OutputSampleProviderKind preferredOutputSampleProvider = OutputSampleProviderKind.Unknown;
    private static OutputRetrievalMode preferredOutputRetrievalMode = OutputRetrievalMode.Unknown;
    private static OutputSubtypeProbeKind preferredOutputSubtypeProbeKind = OutputSubtypeProbeKind.Unknown;
    private static InputSampleStrategyKind preferredInputSampleStrategy = InputSampleStrategyKind.Unknown;
    private static DecoderBackendKind preferredDecoderBackend = DecoderBackendKind.Unknown;
    private static DecoderAttributeProfileKind preferredDecoderAttributeProfile = DecoderAttributeProfileKind.Unknown;
    private static int nextDecoderInstanceId;
    private static int nextTransformInstanceId;
    private static int helperRemoteArtifactBundleCaptured;
    private static int helperRemoteReplayTimelineCaptured;
    private static readonly object hardwareDecoderDiagnosticSync = new();
    private static bool hardwareDecoderDiagnosticChecked;
    private static bool hardwareDecoderDiagnosticAvailable;
    private static string hardwareDecoderDiagnosticActivationSource = "unknown";
    private static string hardwareDecoderDiagnosticFriendlyName = "unknown";
    private static string hardwareDecoderDiagnosticFailureStage = "(none)";
    private static int hardwareDecoderDiagnosticFailureHresult;
    private static string debugLastCreatedRole = string.Empty;
    private static string debugLastHelperRemoteArtifactPath = string.Empty;
    private static string debugPreferredOutputSampleProvider = OutputSampleProviderKind.Unknown.ToString();
    private static string debugPreferredOutputCombination = "unknown";
    private static string debugPreferredOutputSubtype = "unknown";
    private static string debugLastAttemptedOutputCombination = "unknown";
    private static string debugLastOutputFailureStage = "(none)";
    private static string debugLastOutputFailureHresult = "(none)";
    private static string debugPreferredInputSampleStrategy = InputSampleStrategyKind.Unknown.ToString();
    private static string debugLastOutputMatrixSummary = string.Empty;
    private static string debugLastConclusion = string.Empty;
    private static string debugPreferredDecoderBackendProfile = "unknown";
    private static string debugLastHardwareDecoderAvailabilitySummary = "unchecked";

    private int framesSubmittedThisEpoch;
    private int lastNormalizedInputBytesThisEpoch;
    private int needMoreInputCountThisEpoch;
    private bool sawAnyOutputSampleThisEpoch;
    private bool createdAnyBitmapThisEpoch;
    private bool loggedInputAcceptedThisEpoch;
    private bool loggedProcessInputSuccessThisEpoch;
    private bool loggedProcessOutputObservedThisEpoch;
    private bool loggedDecodeSuccessThisEpoch;
    private bool loggedDecoderConclusionThisEpoch;
    private bool processInputReachedThisEpoch;
    private bool processInputSucceededThisEpoch;
    private bool processOutputReachedThisEpoch;
    private bool loggedInputStrategySelectedThisEpoch;
    private bool outputProviderContractFailureThisEpoch;
    private bool diagnosticDrainProducedOutputThisEpoch;
    private bool legacyStartupSequenceProducedOutputThisEpoch;
    private bool sawSuccessWithoutSampleThisEpoch;
    private bool sawOutputReadyWithoutSampleThisEpoch;
    private bool sawSampleReadyThisEpoch;
    private bool nullSampleDiagnosticSucceededThisEpoch;
    private bool attemptedNullSampleDiagnosticThisEpoch;
    private bool nullSampleDiagnosticEligibleThisEpoch;
    private bool drainAttemptedThisEpoch;
    private bool diagnosticDrainAttemptedThisEpoch;
    private bool detailedReplayTimelineEnabledThisEpoch;
    private readonly List<byte[]> helperRemoteSubmittedFrames = new(HelperRemoteArtifactFrameLimit);
    private readonly List<BufferedProbeFrame> outputProbeFrames = new(OutputProbeFrameLimit);
    private bool outputTypeMismatchThisEpoch;
    private OutputSampleShapeKind outputSampleShapeThisEpoch;
    private OutputSampleProviderKind outputSampleProviderThisEpoch;
    private OutputRetrievalMode outputRetrievalModeThisEpoch;
    private InputSampleStrategyKind inputSampleStrategyThisEpoch;
    private uint outputSampleBufferSizeThisEpoch;
    private uint outputSampleBufferAlignmentThisEpoch;
    private string lastOutputFailureStageThisEpoch = "(none)";
    private int lastOutputFailureHresultThisEpoch;
    private int lastProcessOutputHresultThisEpoch;
    private uint lastOutputDataBufferStatusThisEpoch;
    private uint lastOutputStatusFlagsThisEpoch;
    private int lastOutputStatusHresultThisEpoch;
    private uint lastInputStatusFlagsThisEpoch;
    private int lastInputStatusHresultThisEpoch;
    private int lastTransformIdThisEpoch;
    private bool lastTransformOutputTypeConfiguredThisEpoch;
    private bool lastTransformOutputTypeVerifiedThisEpoch;
    private int lastTransformSetOutputTypeHresultThisEpoch;
    private int lastTransformGetOutputCurrentTypeHresultThisEpoch;
    private int lastTransformGetOutputStatusHresultThisEpoch;
    private uint lastTransformGetOutputStatusFlagsThisEpoch;
    private bool lastTransformInputTypeConfiguredThisEpoch;
    private bool lastTransformBeginStreamingSentThisEpoch;
    private bool lastTransformStartOfStreamSentThisEpoch;
    private int lastTransformBeginStreamingHresultThisEpoch;
    private int lastTransformStartOfStreamHresultThisEpoch;
    private TransformStartupSequenceKind lastTransformStartupSequenceThisEpoch;
    private bool lastTransformStartupSequenceVerifiedThisEpoch;
    private bool lastTransformFullyStartedBeforeFirstInputThisEpoch;
    private bool loggedOutputBufferSizeOverrideThisEpoch;
    private int maxReplayFrameIndexThisEpoch;
    private int firstOutputFrameIndexThisEpoch;
    private DecoderBackendKind lastBackendKindThisEpoch;
    private DecoderAttributeProfileKind lastAttributeProfileThisEpoch;
    private string lastActivationSourceThisEpoch = "unknown";
    private string lastFriendlyNameThisEpoch = "unknown";
    private string lastTransformAttributeSnapshotThisEpoch = "unavailable";
    private string lastTransformAttributeSnapshotBeforeProfileThisEpoch = "unavailable";
    private string lastTransformAttributeSnapshotAfterProfileThisEpoch = "unavailable";
    private string lastInputStreamAttributeSnapshotThisEpoch = "unavailable";
    private string lastOutputStreamAttributeSnapshotThisEpoch = "unavailable";
    private string lastOutputSubtypeCandidateThisEpoch = "unknown";
    private OutputSubtypeProbeKind lastOutputSubtypeProbeKindThisEpoch;
    private bool lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch;
    private string firstSuccessfulOutputSubtypeThisEpoch = "unknown";
    private bool lastLowLatencyRequestedThisEpoch;
    private bool lastLowLatencyAppliedThisEpoch;
    private bool lastTransformLowLatencyAppliedThisEpoch;
    private int lastTransformLowLatencyHresultThisEpoch;
    private bool lastInputMediaTypeLowLatencyAppliedThisEpoch;
    private int lastInputMediaTypeLowLatencyHresultThisEpoch;
    private bool lastOutputMediaTypeLowLatencyAppliedThisEpoch;
    private int lastOutputMediaTypeLowLatencyHresultThisEpoch;
    private bool lastCodecApiAvailableThisEpoch;
    private bool lastCodecApiSupportedThisEpoch;
    private int lastCodecApiIsSupportedHresultThisEpoch;
    private bool lastCodecApiModifiableThisEpoch;
    private int lastCodecApiIsModifiableHresultThisEpoch;
    private bool lastCodecApiLowLatencyAppliedThisEpoch;
    private int lastCodecApiSetValueHresultThisEpoch;
    private bool decoderBackendActivationFailureThisEpoch;
    private bool decoderAttributeProfileFailureThisEpoch;
    private bool hardwareDecoderAvailableThisEpoch;
    private bool softwareBaselineAttemptedThisEpoch;
    private bool softwareBaselineSampleReadyThisEpoch;
    private bool softwareLowLatencyAttemptedThisEpoch;
    private bool softwareLowLatencySampleReadyThisEpoch;
    private bool hardwareBaselineAttemptedThisEpoch;
    private bool hardwareBaselineSampleReadyThisEpoch;
    private bool hardwareLowLatencyAttemptedThisEpoch;
    private bool hardwareLowLatencySampleReadyThisEpoch;
    private bool nativeAdvertisedOutputSubtypeProducedOutputThisEpoch;
    private bool explicitNv12OutputSubtypeProducedOutputThisEpoch;
    private bool explicitYuy2OutputSubtypeProducedOutputThisEpoch;

    private sealed class MediaFoundationNeedMoreInputException : H264DecoderNeedsMoreInputException
    {
        public MediaFoundationNeedMoreInputException(string message)
            : base(message)
        {
            HResult = MfTransformNeedMoreInput;
        }
    }

    internal static string DebugLastCreatedRole => debugLastCreatedRole;
    internal static string DebugLastHelperRemoteArtifactPath => debugLastHelperRemoteArtifactPath;
    internal static string DebugPreferredOutputSampleProvider => debugPreferredOutputSampleProvider;
    internal static string DebugPreferredOutputCombination => debugPreferredOutputCombination;
    internal static string DebugPreferredOutputSubtype => debugPreferredOutputSubtype;
    internal static string DebugLastAttemptedOutputCombination => debugLastAttemptedOutputCombination;
    internal static string DebugLastOutputFailureStage => debugLastOutputFailureStage;
    internal static string DebugLastOutputFailureHresult => debugLastOutputFailureHresult;
    internal static string DebugPreferredInputSampleStrategy => debugPreferredInputSampleStrategy;
    internal static string DebugLastOutputMatrixSummary => debugLastOutputMatrixSummary;
    internal static string DebugLastConclusion => debugLastConclusion;
    internal static string DebugPreferredDecoderBackendProfile => debugPreferredDecoderBackendProfile;
    internal static string DebugLastHardwareDecoderAvailabilitySummary => debugLastHardwareDecoderAvailabilitySummary;

    internal static void ResetDebugInputSampleStrategyState()
    {
        preferredOutputSampleShape = OutputSampleShapeKind.Unknown;
        preferredOutputSampleProvider = OutputSampleProviderKind.Unknown;
        preferredOutputRetrievalMode = OutputRetrievalMode.Unknown;
        preferredOutputSubtypeProbeKind = OutputSubtypeProbeKind.Unknown;
        preferredInputSampleStrategy = InputSampleStrategyKind.Unknown;
        preferredDecoderBackend = DecoderBackendKind.Unknown;
        preferredDecoderAttributeProfile = DecoderAttributeProfileKind.Unknown;
        debugPreferredOutputSampleProvider = FormatOutputSampleProvider(OutputSampleProviderKind.Unknown);
        debugPreferredOutputCombination = "unknown";
        debugPreferredOutputSubtype = "unknown";
        debugLastAttemptedOutputCombination = "unknown";
        debugLastOutputFailureStage = "(none)";
        debugLastOutputFailureHresult = "(none)";
        debugPreferredInputSampleStrategy = FormatInputSampleStrategy(InputSampleStrategyKind.Unknown);
        debugPreferredDecoderBackendProfile = "unknown";
        debugLastOutputMatrixSummary = string.Empty;
        debugLastConclusion = string.Empty;
        debugLastHardwareDecoderAvailabilitySummary = hardwareDecoderDiagnosticChecked
            ? FormatHardwareDecoderDiagnosticSummary()
            : "unchecked";
        helperRemoteReplayTimelineCaptured = 0;
    }

    private static H264DecoderNeedsMoreInputException CreateNeedMoreInputException(string message)
        => new MediaFoundationNeedMoreInputException(message);

    private void RecordInputAccepted(long streamEpoch, int normalizedInputBytes, long sampleTimeHns, long sampleDurationHns)
    {
        framesSubmittedThisEpoch++;
        lastNormalizedInputBytesThisEpoch = normalizedInputBytes;
        if (!loggedInputAcceptedThisEpoch)
        {
            loggedInputAcceptedThisEpoch = true;
            LogLifecycle(
                "screenshare_h264_decoder_input_accepted",
                streamEpoch,
                $"normalized_input_bytes={normalizedInputBytes}; sample_time_hns={sampleTimeHns}; sample_duration_hns={sampleDurationHns}");
        }
    }

    private void LogInputSampleStrategySelected(
        long streamEpoch,
        InputSampleStrategyKind strategyKind,
        IMFSample inputSample,
        InputSampleContract contract,
        bool isKeyFrame)
    {
        inputSampleStrategyThisEpoch = strategyKind;
        if (loggedInputStrategySelectedThisEpoch)
        {
            return;
        }

        loggedInputStrategySelectedThisEpoch = true;
        inputSample.GetBufferCount(out var inputBufferCount);
        LogLifecycle(
            "screenshare_h264_decoder_input_strategy_selected",
            streamEpoch,
            $"strategy={FormatInputSampleStrategy(strategyKind)}; sample_buffers={inputBufferCount}; sample_time_present={(contract.SetSampleTime ? 1 : 0)}; sample_duration_present={(contract.SetSampleDuration ? 1 : 0)}; clean_point={(contract.SetCleanPoint ? 1 : 0)}; discontinuity={(contract.SetDiscontinuity ? 1 : 0)}; is_keyframe={(isKeyFrame ? 1 : 0)}");
    }

    private void RecordNormalizedInputForDiagnostics(ReadOnlyMemory<byte> normalizedBytes)
    {
        if (!IsHelperRemoteRole() || normalizedBytes.Length == 0 || helperRemoteSubmittedFrames.Count >= HelperRemoteArtifactFrameLimit)
        {
            return;
        }

        helperRemoteSubmittedFrames.Add(normalizedBytes.ToArray());
    }

    private void RecordProcessInputSucceeded(long streamEpoch, int normalizedInputBytes, InputSampleStrategyKind strategyKind, InputSampleContract contract)
    {
        if (loggedProcessInputSuccessThisEpoch)
        {
            return;
        }

        loggedProcessInputSuccessThisEpoch = true;
        LogLifecycle(
            "screenshare_h264_decoder_process_input_succeeded",
            streamEpoch,
            $"normalized_input_bytes={normalizedInputBytes}; strategy={FormatInputSampleStrategy(strategyKind)}; sample_time_present={(contract.SetSampleTime ? 1 : 0)}; sample_duration_present={(contract.SetSampleDuration ? 1 : 0)}; clean_point={(contract.SetCleanPoint ? 1 : 0)}; discontinuity={(contract.SetDiscontinuity ? 1 : 0)}");
    }

    private void RecordProcessOutputObserved(
        long streamEpoch,
        bool callerProvidesSample,
        uint outputBufferCount,
        uint outputFlags,
        OutputContractCombination combination,
        OutputSampleOrigin sampleOrigin)
    {
        sawAnyOutputSampleThisEpoch = true;
        if (firstOutputFrameIndexThisEpoch == 0)
        {
            firstOutputFrameIndexThisEpoch = Math.Max(1, framesSubmittedThisEpoch);
        }

        outputTypeMismatchThisEpoch = false;
        outputSampleShapeThisEpoch = combination.Shape;
        outputSampleProviderThisEpoch = combination.Provider;
        outputRetrievalModeThisEpoch = combination.RetrievalMode;
        if (combination.Provider is not OutputSampleProviderKind.Unknown &&
            combination.Shape is not OutputSampleShapeKind.NullSampleDiagnostic)
        {
            preferredOutputSampleShape = combination.Shape;
            preferredOutputSampleProvider = combination.Provider;
            debugPreferredOutputSampleProvider = FormatOutputSampleProvider(combination.Provider);
        }

        if (combination.RetrievalMode is not OutputRetrievalMode.Unknown &&
            combination.Shape is not OutputSampleShapeKind.NullSampleDiagnostic)
        {
            preferredOutputRetrievalMode = combination.RetrievalMode;
            debugPreferredOutputCombination = FormatOutputCombination(combination);
        }

        if (loggedProcessOutputObservedThisEpoch)
        {
            return;
        }

        loggedProcessOutputObservedThisEpoch = true;
        LogLifecycle(
            "screenshare_h264_decoder_process_output_observed",
            streamEpoch,
            $"caller_provides_sample={(callerProvidesSample ? 1 : 0)}; output_buffer_count={outputBufferCount}; output_flags={FormatOutputStreamFlags(outputFlags)}; provider={FormatOutputSampleProvider(combination.Provider)}; retrieval_mode={FormatOutputRetrievalMode(combination.RetrievalMode)}; output_combination={FormatOutputCombination(combination)}; sample_origin={FormatOutputSampleOrigin(sampleOrigin)}");
    }

    private void RecordDecodeSucceeded(long streamEpoch, bool outputTypeChanged, Bitmap bitmap)
    {
        createdAnyBitmapThisEpoch = true;
        needMoreInputCountThisEpoch = 0;
        outputProbeFrames.Clear();
        if (loggedDecodeSuccessThisEpoch)
        {
            return;
        }

        loggedDecodeSuccessThisEpoch = true;
        LogLifecycle(
            "screenshare_h264_decoder_decode_succeeded",
            streamEpoch,
            $"rendered_width={bitmap.PixelSize.Width}; rendered_height={bitmap.PixelSize.Height}; output_type_changed={(outputTypeChanged ? 1 : 0)}");
    }

    private void RecordNeedMoreInput(long streamEpoch, int normalizedInputBytes, Exception ex)
    {
        needMoreInputCountThisEpoch++;
        if (Array.IndexOf(NeedMoreInputSummaryThresholds, needMoreInputCountThisEpoch) >= 0)
        {
            LogLifecycle(
                "screenshare_h264_decoder_need_more_input",
                streamEpoch,
                $"normalized_input_bytes={normalizedInputBytes}; count={needMoreInputCountThisEpoch}; saw_output_sample={(sawAnyOutputSampleThisEpoch ? 1 : 0)}; saw_bitmap={(createdAnyBitmapThisEpoch ? 1 : 0)}; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
        }

        MaybePersistHelperRemoteArtifactBundle(streamEpoch, "need_more_input");
    }

    private void LogEpochSummary(string reason)
    {
        var activeEpoch = configuration?.StreamEpoch ?? 0;
        if (framesSubmittedThisEpoch <= 0 && needMoreInputCountThisEpoch <= 0 && !sawAnyOutputSampleThisEpoch && !createdAnyBitmapThisEpoch)
        {
            return;
        }

        if (!loggedDecoderConclusionThisEpoch)
        {
            loggedDecoderConclusionThisEpoch = true;
            var conclusion = ClassifyDecoderConclusion();
            debugLastConclusion = conclusion;
            LogLifecycle(
                "screenshare_h264_decoder_conclusion",
                activeEpoch,
                $"classification={conclusion}; expected_width={configuration?.ExpectedCodedWidth ?? 0}; expected_height={configuration?.ExpectedCodedHeight ?? 0}; chosen_width={outputWidth}; chosen_height={outputHeight}; output_subtype_candidate={Sanitize(lastOutputSubtypeCandidateThisEpoch)}; output_subtype_probe={FormatOutputSubtypeProbeKind(lastOutputSubtypeProbeKindThisEpoch)}; output_subtype_native_advertised={(lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch ? 1 : 0)}; first_successful_output_subtype={Sanitize(firstSuccessfulOutputSubtypeThisEpoch)}; preferred_output_subtype={Sanitize(debugPreferredOutputSubtype)}; provider={FormatOutputSampleProvider(outputSampleProviderThisEpoch)}; retrieval_mode={FormatOutputRetrievalMode(outputRetrievalModeThisEpoch)}; winning_output_combination={Sanitize(debugPreferredOutputCombination)}; last_attempted_output_combination={Sanitize(debugLastAttemptedOutputCombination)}; winning_backend_profile={Sanitize(debugPreferredDecoderBackendProfile)}; backend={FormatDecoderBackend(lastBackendKindThisEpoch)}; attribute_profile={FormatDecoderAttributeProfile(lastAttributeProfileThisEpoch)}; activation_source={Sanitize(lastActivationSourceThisEpoch)}; friendly_name={Sanitize(lastFriendlyNameThisEpoch)}; hardware_diagnostic={Sanitize(debugLastHardwareDecoderAvailabilitySummary)}; input_strategy={FormatInputSampleStrategy(inputSampleStrategyThisEpoch)}; process_input_reached={(processInputReachedThisEpoch ? 1 : 0)}; process_input_succeeded={(processInputSucceededThisEpoch ? 1 : 0)}; process_output_reached={(processOutputReachedThisEpoch ? 1 : 0)}; max_frame_index_attempted={maxReplayFrameIndexThisEpoch}; first_output_frame_index={firstOutputFrameIndexThisEpoch}; sample_ready_seen={(sawSampleReadyThisEpoch ? 1 : 0)}; transform_id={lastTransformIdThisEpoch}; startup_sequence={FormatStartupSequence(lastTransformStartupSequenceThisEpoch)}; startup_sequence_verified={(lastTransformStartupSequenceVerifiedThisEpoch ? 1 : 0)}; input_type_configured={(lastTransformInputTypeConfiguredThisEpoch ? 1 : 0)}; begin_streaming_sent={(lastTransformBeginStreamingSentThisEpoch ? 1 : 0)}; begin_streaming_hr=0x{lastTransformBeginStreamingHresultThisEpoch:X8}; start_of_stream_sent={(lastTransformStartOfStreamSentThisEpoch ? 1 : 0)}; start_of_stream_hr=0x{lastTransformStartOfStreamHresultThisEpoch:X8}; fully_started_before_first_input={(lastTransformFullyStartedBeforeFirstInputThisEpoch ? 1 : 0)}; transform_output_type_configured={(lastTransformOutputTypeConfiguredThisEpoch ? 1 : 0)}; transform_output_type_verified={(lastTransformOutputTypeVerifiedThisEpoch ? 1 : 0)}; set_output_type_hr=0x{lastTransformSetOutputTypeHresultThisEpoch:X8}; get_output_current_type_hr=0x{lastTransformGetOutputCurrentTypeHresultThisEpoch:X8}; post_config_output_status_hr=0x{lastTransformGetOutputStatusHresultThisEpoch:X8}; post_config_output_status={FormatOutputStatusFlags(lastTransformGetOutputStatusFlagsThisEpoch)}; input_status_hr=0x{lastInputStatusHresultThisEpoch:X8}; input_status={FormatInputStatusFlags(lastInputStatusFlagsThisEpoch)}; output_status_hr=0x{lastOutputStatusHresultThisEpoch:X8}; output_status={FormatOutputStatusFlags(lastOutputStatusFlagsThisEpoch)}; process_output_hr=0x{lastProcessOutputHresultThisEpoch:X8}; output_dw_status=0x{lastOutputDataBufferStatusThisEpoch:X8}; last_stage={Sanitize(lastOutputFailureStageThisEpoch)}; last_hresult=0x{lastOutputFailureHresultThisEpoch:X8}; low_latency_requested={(lastLowLatencyRequestedThisEpoch ? 1 : 0)}; low_latency_applied={(lastLowLatencyAppliedThisEpoch ? 1 : 0)}; transform_low_latency_applied={(lastTransformLowLatencyAppliedThisEpoch ? 1 : 0)}; transform_low_latency_hr=0x{lastTransformLowLatencyHresultThisEpoch:X8}; input_media_type_low_latency_applied={(lastInputMediaTypeLowLatencyAppliedThisEpoch ? 1 : 0)}; input_media_type_low_latency_hr=0x{lastInputMediaTypeLowLatencyHresultThisEpoch:X8}; output_media_type_low_latency_applied={(lastOutputMediaTypeLowLatencyAppliedThisEpoch ? 1 : 0)}; output_media_type_low_latency_hr=0x{lastOutputMediaTypeLowLatencyHresultThisEpoch:X8}; codecapi_available={(lastCodecApiAvailableThisEpoch ? 1 : 0)}; codecapi_supported={(lastCodecApiSupportedThisEpoch ? 1 : 0)}; codecapi_is_supported_hr=0x{lastCodecApiIsSupportedHresultThisEpoch:X8}; codecapi_modifiable={(lastCodecApiModifiableThisEpoch ? 1 : 0)}; codecapi_is_modifiable_hr=0x{lastCodecApiIsModifiableHresultThisEpoch:X8}; codecapi_applied={(lastCodecApiLowLatencyAppliedThisEpoch ? 1 : 0)}; codecapi_set_value_hr=0x{lastCodecApiSetValueHresultThisEpoch:X8}; transform_attributes_before_profile={Sanitize(lastTransformAttributeSnapshotBeforeProfileThisEpoch)}; transform_attributes_after_profile={Sanitize(lastTransformAttributeSnapshotAfterProfileThisEpoch)}; transform_attributes={Sanitize(lastTransformAttributeSnapshotThisEpoch)}; input_stream_attributes={Sanitize(lastInputStreamAttributeSnapshotThisEpoch)}; output_stream_attributes={Sanitize(lastOutputStreamAttributeSnapshotThisEpoch)}; drain_attempted={(drainAttemptedThisEpoch ? 1 : 0)}; diagnostic_drain_attempted={(diagnosticDrainAttemptedThisEpoch ? 1 : 0)}; buffer_size={outputSampleBufferSizeThisEpoch}; buffer_alignment={outputSampleBufferAlignmentThisEpoch}");
        }

        LogLifecycle(
            "screenshare_h264_decoder_epoch_summary",
            activeEpoch,
            $"reason={Sanitize(reason)}; frames_submitted={framesSubmittedThisEpoch}; need_more_input_count={needMoreInputCountThisEpoch}; saw_output_sample={(sawAnyOutputSampleThisEpoch ? 1 : 0)}; created_bitmap={(createdAnyBitmapThisEpoch ? 1 : 0)}; max_frame_index_attempted={maxReplayFrameIndexThisEpoch}; first_output_frame_index={firstOutputFrameIndexThisEpoch}; sample_ready_seen={(sawSampleReadyThisEpoch ? 1 : 0)}; output_subtype_candidate={Sanitize(lastOutputSubtypeCandidateThisEpoch)}; output_subtype_probe={FormatOutputSubtypeProbeKind(lastOutputSubtypeProbeKindThisEpoch)}; output_subtype_native_advertised={(lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch ? 1 : 0)}; first_successful_output_subtype={Sanitize(firstSuccessfulOutputSubtypeThisEpoch)}; preferred_output_subtype={Sanitize(debugPreferredOutputSubtype)}; provider={FormatOutputSampleProvider(outputSampleProviderThisEpoch)}; retrieval_mode={FormatOutputRetrievalMode(outputRetrievalModeThisEpoch)}; last_attempted_output_combination={Sanitize(debugLastAttemptedOutputCombination)}; winning_output_combination={Sanitize(debugPreferredOutputCombination)}; winning_backend_profile={Sanitize(debugPreferredDecoderBackendProfile)}; backend={FormatDecoderBackend(lastBackendKindThisEpoch)}; attribute_profile={FormatDecoderAttributeProfile(lastAttributeProfileThisEpoch)}; activation_source={Sanitize(lastActivationSourceThisEpoch)}; friendly_name={Sanitize(lastFriendlyNameThisEpoch)}; hardware_diagnostic={Sanitize(debugLastHardwareDecoderAvailabilitySummary)}; input_strategy={FormatInputSampleStrategy(inputSampleStrategyThisEpoch)}; transform_id={lastTransformIdThisEpoch}; startup_sequence={FormatStartupSequence(lastTransformStartupSequenceThisEpoch)}; startup_sequence_verified={(lastTransformStartupSequenceVerifiedThisEpoch ? 1 : 0)}; input_type_configured={(lastTransformInputTypeConfiguredThisEpoch ? 1 : 0)}; begin_streaming_sent={(lastTransformBeginStreamingSentThisEpoch ? 1 : 0)}; begin_streaming_hr=0x{lastTransformBeginStreamingHresultThisEpoch:X8}; start_of_stream_sent={(lastTransformStartOfStreamSentThisEpoch ? 1 : 0)}; start_of_stream_hr=0x{lastTransformStartOfStreamHresultThisEpoch:X8}; fully_started_before_first_input={(lastTransformFullyStartedBeforeFirstInputThisEpoch ? 1 : 0)}; transform_output_type_configured={(lastTransformOutputTypeConfiguredThisEpoch ? 1 : 0)}; transform_output_type_verified={(lastTransformOutputTypeVerifiedThisEpoch ? 1 : 0)}; set_output_type_hr=0x{lastTransformSetOutputTypeHresultThisEpoch:X8}; get_output_current_type_hr=0x{lastTransformGetOutputCurrentTypeHresultThisEpoch:X8}; post_config_output_status_hr=0x{lastTransformGetOutputStatusHresultThisEpoch:X8}; post_config_output_status={FormatOutputStatusFlags(lastTransformGetOutputStatusFlagsThisEpoch)}; input_status_hr=0x{lastInputStatusHresultThisEpoch:X8}; input_status={FormatInputStatusFlags(lastInputStatusFlagsThisEpoch)}; output_status_hr=0x{lastOutputStatusHresultThisEpoch:X8}; output_status={FormatOutputStatusFlags(lastOutputStatusFlagsThisEpoch)}; process_output_hr=0x{lastProcessOutputHresultThisEpoch:X8}; output_dw_status=0x{lastOutputDataBufferStatusThisEpoch:X8}; last_stage={Sanitize(lastOutputFailureStageThisEpoch)}; last_hresult=0x{lastOutputFailureHresultThisEpoch:X8}; low_latency_requested={(lastLowLatencyRequestedThisEpoch ? 1 : 0)}; low_latency_applied={(lastLowLatencyAppliedThisEpoch ? 1 : 0)}; transform_low_latency_applied={(lastTransformLowLatencyAppliedThisEpoch ? 1 : 0)}; transform_low_latency_hr=0x{lastTransformLowLatencyHresultThisEpoch:X8}; input_media_type_low_latency_applied={(lastInputMediaTypeLowLatencyAppliedThisEpoch ? 1 : 0)}; input_media_type_low_latency_hr=0x{lastInputMediaTypeLowLatencyHresultThisEpoch:X8}; output_media_type_low_latency_applied={(lastOutputMediaTypeLowLatencyAppliedThisEpoch ? 1 : 0)}; output_media_type_low_latency_hr=0x{lastOutputMediaTypeLowLatencyHresultThisEpoch:X8}; codecapi_available={(lastCodecApiAvailableThisEpoch ? 1 : 0)}; codecapi_supported={(lastCodecApiSupportedThisEpoch ? 1 : 0)}; codecapi_is_supported_hr=0x{lastCodecApiIsSupportedHresultThisEpoch:X8}; codecapi_modifiable={(lastCodecApiModifiableThisEpoch ? 1 : 0)}; codecapi_is_modifiable_hr=0x{lastCodecApiIsModifiableHresultThisEpoch:X8}; codecapi_applied={(lastCodecApiLowLatencyAppliedThisEpoch ? 1 : 0)}; codecapi_set_value_hr=0x{lastCodecApiSetValueHresultThisEpoch:X8}; transform_attributes_before_profile={Sanitize(lastTransformAttributeSnapshotBeforeProfileThisEpoch)}; transform_attributes_after_profile={Sanitize(lastTransformAttributeSnapshotAfterProfileThisEpoch)}; transform_attributes={Sanitize(lastTransformAttributeSnapshotThisEpoch)}; input_stream_attributes={Sanitize(lastInputStreamAttributeSnapshotThisEpoch)}; output_stream_attributes={Sanitize(lastOutputStreamAttributeSnapshotThisEpoch)}; drain_attempted={(drainAttemptedThisEpoch ? 1 : 0)}; diagnostic_drain_attempted={(diagnosticDrainAttemptedThisEpoch ? 1 : 0)}; buffer_size={outputSampleBufferSizeThisEpoch}; buffer_alignment={outputSampleBufferAlignmentThisEpoch}");
        MaybePersistHelperRemoteArtifactBundle(activeEpoch, reason);
    }

    private void MaybePersistHelperRemoteArtifactBundle(long streamEpoch, string reason)
    {
        if (!IsHelperRemoteRole() ||
            !FeatureFlags.ScreenShareDeepDiagnostics ||
            createdAnyBitmapThisEpoch ||
            helperRemoteSubmittedFrames.Count == 0 ||
            (string.Equals(reason, "need_more_input", StringComparison.Ordinal) && needMoreInputCountThisEpoch < HelperRemoteArtifactNeedMoreInputThreshold) ||
            Interlocked.CompareExchange(ref helperRemoteArtifactBundleCaptured, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "nLink",
                "decoder-debug",
                $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-helper-remote-epoch-{streamEpoch}");
            Directory.CreateDirectory(root);

            var metadata = new HelperRemoteDecoderArtifactMetadata(
                Role: logRole,
                DecoderId: decoderInstanceId,
                StreamEpoch: streamEpoch,
                Reason: reason,
                ConfigByteCount: configuration?.DecoderConfigData.Length ?? 0,
                FirstFrameByteCount: helperRemoteSubmittedFrames.Count > 0 ? helperRemoteSubmittedFrames[0].Length : 0,
                FrameCountCaptured: helperRemoteSubmittedFrames.Count,
                LiveFrameCountCaptured: helperRemoteSubmittedFrames.Count,
                ReplayFrameCountCaptured: outputProbeFrames.Count,
                ReplayFrameCountAttempted: maxReplayFrameIndexThisEpoch,
                FramesSubmitted: framesSubmittedThisEpoch,
                NeedMoreInputCount: needMoreInputCountThisEpoch,
                SawOutputSample: sawAnyOutputSampleThisEpoch,
                CreatedBitmap: createdAnyBitmapThisEpoch,
                MaxFrameIndexAttempted: maxReplayFrameIndexThisEpoch,
                FirstOutputFrameIndex: firstOutputFrameIndexThisEpoch,
                SampleReadySeen: sawSampleReadyThisEpoch,
                ExpectedWidth: configuration?.ExpectedCodedWidth ?? 0,
                ExpectedHeight: configuration?.ExpectedCodedHeight ?? 0,
                ChosenWidth: outputWidth,
                ChosenHeight: outputHeight,
                OutputSubtype: FormatVideoSubtype(outputSubtype),
                OutputSubtypeCandidate: lastOutputSubtypeCandidateThisEpoch,
                OutputSubtypeProbe: FormatOutputSubtypeProbeKind(lastOutputSubtypeProbeKindThisEpoch),
                OutputSubtypeCandidateIsNativeAdvertised: lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch,
                FirstSuccessfulOutputSubtype: firstSuccessfulOutputSubtypeThisEpoch,
                OutputSampleProvider: FormatOutputSampleProvider(outputSampleProviderThisEpoch),
                OutputRetrievalMode: FormatOutputRetrievalMode(outputRetrievalModeThisEpoch),
                WinningOutputCombination: debugPreferredOutputCombination,
                LastAttemptedOutputCombination: debugLastAttemptedOutputCombination,
                InputSampleStrategy: FormatInputSampleStrategy(inputSampleStrategyThisEpoch),
                Backend: FormatDecoderBackend(lastBackendKindThisEpoch),
                AttributeProfile: FormatDecoderAttributeProfile(lastAttributeProfileThisEpoch),
                ActivationSource: lastActivationSourceThisEpoch,
                FriendlyName: lastFriendlyNameThisEpoch,
                OutputBufferSize: outputSampleBufferSizeThisEpoch,
                OutputBufferAlignment: outputSampleBufferAlignmentThisEpoch,
                LastOutputFailureStage: lastOutputFailureStageThisEpoch,
                LastOutputFailureHresult: $"0x{lastOutputFailureHresultThisEpoch:X8}",
                TransformId: lastTransformIdThisEpoch,
                TransformOutputTypeConfigured: lastTransformOutputTypeConfiguredThisEpoch,
                TransformOutputTypeVerified: lastTransformOutputTypeVerifiedThisEpoch,
                TransformInputTypeConfigured: lastTransformInputTypeConfiguredThisEpoch,
                StartupSequence: FormatStartupSequence(lastTransformStartupSequenceThisEpoch),
                StartupSequenceVerified: lastTransformStartupSequenceVerifiedThisEpoch,
                BeginStreamingSent: lastTransformBeginStreamingSentThisEpoch,
                BeginStreamingHresult: $"0x{lastTransformBeginStreamingHresultThisEpoch:X8}",
                StartOfStreamSent: lastTransformStartOfStreamSentThisEpoch,
                StartOfStreamHresult: $"0x{lastTransformStartOfStreamHresultThisEpoch:X8}",
                FullyStartedBeforeFirstInput: lastTransformFullyStartedBeforeFirstInputThisEpoch,
                SetOutputTypeHresult: $"0x{lastTransformSetOutputTypeHresultThisEpoch:X8}",
                GetOutputCurrentTypeHresult: $"0x{lastTransformGetOutputCurrentTypeHresultThisEpoch:X8}",
                PostConfigurationOutputStatus: FormatOutputStatusFlags(lastTransformGetOutputStatusFlagsThisEpoch),
                PostConfigurationOutputStatusHresult: $"0x{lastTransformGetOutputStatusHresultThisEpoch:X8}",
                InputStatus: FormatInputStatusFlags(lastInputStatusFlagsThisEpoch),
                InputStatusHresult: $"0x{lastInputStatusHresultThisEpoch:X8}",
                OutputStatus: FormatOutputStatusFlags(lastOutputStatusFlagsThisEpoch),
                OutputStatusHresult: $"0x{lastOutputStatusHresultThisEpoch:X8}",
                ProcessOutputHresult: $"0x{lastProcessOutputHresultThisEpoch:X8}",
                OutputDataBufferStatus: $"0x{lastOutputDataBufferStatusThisEpoch:X8}",
                TransformAttributes: lastTransformAttributeSnapshotThisEpoch,
                TransformAttributesBeforeProfile: lastTransformAttributeSnapshotBeforeProfileThisEpoch,
                TransformAttributesAfterProfile: lastTransformAttributeSnapshotAfterProfileThisEpoch,
                InputStreamAttributes: lastInputStreamAttributeSnapshotThisEpoch,
                OutputStreamAttributes: lastOutputStreamAttributeSnapshotThisEpoch,
                LowLatencyRequested: lastLowLatencyRequestedThisEpoch,
                LowLatencyApplied: lastLowLatencyAppliedThisEpoch,
                TransformLowLatencyApplied: lastTransformLowLatencyAppliedThisEpoch,
                TransformLowLatencyHresult: $"0x{lastTransformLowLatencyHresultThisEpoch:X8}",
                InputMediaTypeLowLatencyApplied: lastInputMediaTypeLowLatencyAppliedThisEpoch,
                InputMediaTypeLowLatencyHresult: $"0x{lastInputMediaTypeLowLatencyHresultThisEpoch:X8}",
                OutputMediaTypeLowLatencyApplied: lastOutputMediaTypeLowLatencyAppliedThisEpoch,
                OutputMediaTypeLowLatencyHresult: $"0x{lastOutputMediaTypeLowLatencyHresultThisEpoch:X8}",
                CodecApiAvailable: lastCodecApiAvailableThisEpoch,
                CodecApiSupported: lastCodecApiSupportedThisEpoch,
                CodecApiIsSupportedHresult: $"0x{lastCodecApiIsSupportedHresultThisEpoch:X8}",
                CodecApiModifiable: lastCodecApiModifiableThisEpoch,
                CodecApiIsModifiableHresult: $"0x{lastCodecApiIsModifiableHresultThisEpoch:X8}",
                CodecApiLowLatencyApplied: lastCodecApiLowLatencyAppliedThisEpoch,
                CodecApiSetValueHresult: $"0x{lastCodecApiSetValueHresultThisEpoch:X8}",
                DrainAttempted: drainAttemptedThisEpoch,
                DiagnosticDrainAttempted: diagnosticDrainAttemptedThisEpoch,
                CapturedUtc: DateTimeOffset.UtcNow);
            File.WriteAllText(Path.Combine(root, "metadata.json"), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            var configBytes = configuration?.DecoderConfigData ?? Array.Empty<byte>();
            File.WriteAllBytes(Path.Combine(root, "decoder-config.bin"), configBytes);
            for (var i = 0; i < helperRemoteSubmittedFrames.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"frame-{i + 1:0000}.bin"), helperRemoteSubmittedFrames[i]);
            }

            for (var i = 0; i < outputProbeFrames.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(root, $"replay-frame-{i + 1:0000}.bin"), outputProbeFrames[i].EncodedBytes);
            }

            debugLastHelperRemoteArtifactPath = root;
            LogLifecycle(
                "screenshare_h264_decoder_stall_artifact_preserved",
                streamEpoch,
                $"reason={Sanitize(reason)}; path={Sanitize(root)}; live_frame_count={helperRemoteSubmittedFrames.Count}; replay_frame_count={outputProbeFrames.Count}; replay_frame_attempted={maxReplayFrameIndexThisEpoch}; config_bytes={configBytes.Length}; backend={FormatDecoderBackend(lastBackendKindThisEpoch)}; attribute_profile={FormatDecoderAttributeProfile(lastAttributeProfileThisEpoch)}");
        }
        catch (Exception ex)
        {
            LogLifecycle(
                "screenshare_h264_decoder_stall_artifact_failed",
                streamEpoch,
                $"reason={Sanitize(reason)}; error={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
        }
    }

    private void ResetEpochDiagnostics()
    {
        framesSubmittedThisEpoch = 0;
        lastNormalizedInputBytesThisEpoch = 0;
        needMoreInputCountThisEpoch = 0;
        sawAnyOutputSampleThisEpoch = false;
        createdAnyBitmapThisEpoch = false;
        loggedInputAcceptedThisEpoch = false;
        loggedInputStrategySelectedThisEpoch = false;
        loggedProcessInputSuccessThisEpoch = false;
        loggedProcessOutputObservedThisEpoch = false;
        loggedDecodeSuccessThisEpoch = false;
        loggedDecoderConclusionThisEpoch = false;
        processInputReachedThisEpoch = false;
        processInputSucceededThisEpoch = false;
        processOutputReachedThisEpoch = false;
        outputTypeMismatchThisEpoch = false;
        outputSampleShapeThisEpoch = OutputSampleShapeKind.Unknown;
        outputSampleProviderThisEpoch = OutputSampleProviderKind.Unknown;
        outputRetrievalModeThisEpoch = OutputRetrievalMode.Unknown;
        inputSampleStrategyThisEpoch = InputSampleStrategyKind.Unknown;
        outputSampleBufferSizeThisEpoch = 0;
        outputSampleBufferAlignmentThisEpoch = 0;
        loggedOutputBufferSizeOverrideThisEpoch = false;
        outputProviderContractFailureThisEpoch = false;
        diagnosticDrainProducedOutputThisEpoch = false;
        legacyStartupSequenceProducedOutputThisEpoch = false;
        sawSuccessWithoutSampleThisEpoch = false;
        sawOutputReadyWithoutSampleThisEpoch = false;
        sawSampleReadyThisEpoch = false;
        nullSampleDiagnosticSucceededThisEpoch = false;
        attemptedNullSampleDiagnosticThisEpoch = false;
        nullSampleDiagnosticEligibleThisEpoch = false;
        drainAttemptedThisEpoch = false;
        diagnosticDrainAttemptedThisEpoch = false;
        detailedReplayTimelineEnabledThisEpoch = false;
        lastOutputFailureStageThisEpoch = "(none)";
        lastOutputFailureHresultThisEpoch = 0;
        lastProcessOutputHresultThisEpoch = 0;
        lastOutputDataBufferStatusThisEpoch = 0;
        lastOutputStatusFlagsThisEpoch = 0;
        lastOutputStatusHresultThisEpoch = 0;
        lastInputStatusFlagsThisEpoch = 0;
        lastInputStatusHresultThisEpoch = 0;
        lastTransformIdThisEpoch = 0;
        lastTransformOutputTypeConfiguredThisEpoch = false;
        lastTransformOutputTypeVerifiedThisEpoch = false;
        lastTransformSetOutputTypeHresultThisEpoch = 0;
        lastTransformGetOutputCurrentTypeHresultThisEpoch = 0;
        lastTransformGetOutputStatusHresultThisEpoch = 0;
        lastTransformGetOutputStatusFlagsThisEpoch = 0;
        lastTransformInputTypeConfiguredThisEpoch = false;
        lastTransformBeginStreamingSentThisEpoch = false;
        lastTransformStartOfStreamSentThisEpoch = false;
        lastTransformBeginStreamingHresultThisEpoch = 0;
        lastTransformStartOfStreamHresultThisEpoch = 0;
        lastTransformStartupSequenceThisEpoch = TransformStartupSequenceKind.Unknown;
        lastTransformStartupSequenceVerifiedThisEpoch = false;
        lastTransformFullyStartedBeforeFirstInputThisEpoch = false;
        maxReplayFrameIndexThisEpoch = 0;
        firstOutputFrameIndexThisEpoch = 0;
        lastBackendKindThisEpoch = DecoderBackendKind.Unknown;
        lastAttributeProfileThisEpoch = DecoderAttributeProfileKind.Unknown;
        lastActivationSourceThisEpoch = "unknown";
        lastFriendlyNameThisEpoch = "unknown";
        lastTransformAttributeSnapshotThisEpoch = "unavailable";
        lastTransformAttributeSnapshotBeforeProfileThisEpoch = "unavailable";
        lastTransformAttributeSnapshotAfterProfileThisEpoch = "unavailable";
        lastInputStreamAttributeSnapshotThisEpoch = "unavailable";
        lastOutputStreamAttributeSnapshotThisEpoch = "unavailable";
        lastOutputSubtypeCandidateThisEpoch = "unknown";
        lastOutputSubtypeProbeKindThisEpoch = OutputSubtypeProbeKind.Unknown;
        lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch = false;
        firstSuccessfulOutputSubtypeThisEpoch = "unknown";
        lastLowLatencyRequestedThisEpoch = false;
        lastLowLatencyAppliedThisEpoch = false;
        lastTransformLowLatencyAppliedThisEpoch = false;
        lastTransformLowLatencyHresultThisEpoch = 0;
        lastInputMediaTypeLowLatencyAppliedThisEpoch = false;
        lastInputMediaTypeLowLatencyHresultThisEpoch = 0;
        lastOutputMediaTypeLowLatencyAppliedThisEpoch = false;
        lastOutputMediaTypeLowLatencyHresultThisEpoch = 0;
        lastCodecApiAvailableThisEpoch = false;
        lastCodecApiSupportedThisEpoch = false;
        lastCodecApiIsSupportedHresultThisEpoch = 0;
        lastCodecApiModifiableThisEpoch = false;
        lastCodecApiIsModifiableHresultThisEpoch = 0;
        lastCodecApiLowLatencyAppliedThisEpoch = false;
        lastCodecApiSetValueHresultThisEpoch = 0;
        decoderBackendActivationFailureThisEpoch = false;
        decoderAttributeProfileFailureThisEpoch = false;
        hardwareDecoderAvailableThisEpoch = false;
        softwareBaselineAttemptedThisEpoch = false;
        softwareBaselineSampleReadyThisEpoch = false;
        softwareLowLatencyAttemptedThisEpoch = false;
        softwareLowLatencySampleReadyThisEpoch = false;
        hardwareBaselineAttemptedThisEpoch = false;
        hardwareBaselineSampleReadyThisEpoch = false;
        hardwareLowLatencyAttemptedThisEpoch = false;
        hardwareLowLatencySampleReadyThisEpoch = false;
        nativeAdvertisedOutputSubtypeProducedOutputThisEpoch = false;
        explicitNv12OutputSubtypeProducedOutputThisEpoch = false;
        explicitYuy2OutputSubtypeProducedOutputThisEpoch = false;
        helperRemoteSubmittedFrames.Clear();
        outputProbeFrames.Clear();
    }

    private string ClassifyDecoderConclusion()
    {
        if (createdAnyBitmapThisEpoch || sawAnyOutputSampleThisEpoch)
        {
            if (!explicitYuy2OutputSubtypeProducedOutputThisEpoch &&
                (nativeAdvertisedOutputSubtypeProducedOutputThisEpoch || explicitNv12OutputSubtypeProducedOutputThisEpoch))
            {
                return "software_backend_requires_native_output_subtype";
            }

            return diagnosticDrainProducedOutputThisEpoch
                ? "decoder_requires_end_of_stream_to_surface_output"
                : firstOutputFrameIndexThisEpoch > InitialOutputProbeFrameWindow
                    ? "decoder_requires_more_input_than_initial_window"
                    : "decoder_progressed_to_output";
        }

        if (nullSampleDiagnosticSucceededThisEpoch)
        {
            return "mft_misreported_output_flags";
        }

        if (sawOutputReadyWithoutSampleThisEpoch || sawSampleReadyThisEpoch)
        {
            return "mft_reported_sample_ready_but_no_output";
        }

        if (sawSuccessWithoutSampleThisEpoch)
        {
            return "process_output_returned_success_without_sample";
        }

        if (decoderAttributeProfileFailureThisEpoch &&
            lastLowLatencyRequestedThisEpoch &&
            !lastLowLatencyAppliedThisEpoch &&
            !processInputSucceededThisEpoch)
        {
            return "decoder_attribute_profile_failure";
        }

        if (decoderBackendActivationFailureThisEpoch &&
            !processInputReachedThisEpoch &&
            !softwareBaselineAttemptedThisEpoch &&
            !softwareLowLatencyAttemptedThisEpoch &&
            !hardwareBaselineAttemptedThisEpoch &&
            !hardwareLowLatencyAttemptedThisEpoch)
        {
            return "decoder_backend_activation_failure";
        }

        if (!hardwareDecoderAvailableThisEpoch &&
            !softwareBaselineAttemptedThisEpoch &&
            !softwareLowLatencyAttemptedThisEpoch)
        {
            return "hardware_backend_unavailable";
        }

        if (lastTransformIdThisEpoch > 0 &&
            (!lastTransformInputTypeConfiguredThisEpoch ||
             !lastTransformOutputTypeVerifiedThisEpoch ||
             !lastTransformStartupSequenceVerifiedThisEpoch ||
             !lastTransformBeginStreamingSentThisEpoch ||
             !lastTransformStartOfStreamSentThisEpoch ||
             lastTransformGetOutputCurrentTypeHresultThisEpoch == MfTransformTypeNotSet ||
             lastTransformGetOutputStatusHresultThisEpoch == MfTransformTypeNotSet))
        {
            return "transform_start_sequence_failure";
        }

        if (outputProviderContractFailureThisEpoch &&
            lastTransformStartupSequenceVerifiedThisEpoch &&
            lastProcessOutputHresultThisEpoch != 0 &&
            lastProcessOutputHresultThisEpoch != MfTransformNeedMoreInput)
        {
            return "process_output_rejected_caller_sample";
        }

        if (maxReplayFrameIndexThisEpoch >= OutputProbeFrameLimit &&
            processInputSucceededThisEpoch &&
            !sawSampleReadyThisEpoch)
        {
            if (softwareLowLatencyAttemptedThisEpoch)
            {
                return "software_backend_never_reported_sample_ready_with_native_output_types";
            }

            if (hardwareBaselineAttemptedThisEpoch || hardwareLowLatencyAttemptedThisEpoch)
            {
                return "hardware_backend_never_reported_sample_ready";
            }

            return "decoder_never_reported_sample_ready_after_extended_replay";
        }

        if (diagnosticDrainAttemptedThisEpoch &&
            (needMoreInputCountThisEpoch > 0 || lastProcessOutputHresultThisEpoch == MfTransformNeedMoreInput))
        {
            return "process_output_needs_more_input_after_verified_drain";
        }

        if (processInputReachedThisEpoch && !processInputSucceededThisEpoch)
        {
            return "input_sample_contract_failure";
        }

        if (needMoreInputCountThisEpoch > 0)
        {
            return "process_output_needs_more_input_after_verified_drain";
        }

        return "decoder_no_output";
    }

    private bool IsHelperRemoteRole()
        => string.Equals(logRole, "helper_remote", StringComparison.Ordinal);

    private void LogLifecycle(string eventName, long streamEpoch, string details)
    {
        LogLifecycle(eventName, details, logRole, decoderInstanceId, streamEpoch);
    }

    private static void LogLifecycle(string eventName, string details, string role, int decoderId, long streamEpoch)
    {
        if (!ShouldLogLifecycleEvent(eventName))
        {
            return;
        }

        LocalOperationalLog.Info("ScreenShareTransport", $"event={eventName}; role={Sanitize(role)}; decoder_id={decoderId}; stream_epoch={streamEpoch}; {details}");
        WriteDebugTrace($"[MediaFoundationH264BitmapDecoder] {eventName}: role={role} decoder_id={decoderId} stream_epoch={streamEpoch} {details}");
    }

    private static bool ShouldLogLifecycleEvent(string eventName)
    {
        if (FeatureFlags.ScreenShareDeepDiagnostics)
        {
            return true;
        }

        return eventName is "screenshare_h264_decoder_configured"
            or "screenshare_h264_decoder_first_frame_decoded"
            or "screenshare_h264_decoder_probe_failed";
    }

    [Conditional("DEBUG")]
    private static void WriteDebugTrace(string message)
    {
        Trace.WriteLine(message);
    }

    private sealed record HelperRemoteDecoderArtifactMetadata(
        string Role,
        int DecoderId,
        long StreamEpoch,
        string Reason,
        int ConfigByteCount,
        int FirstFrameByteCount,
        int FrameCountCaptured,
        int LiveFrameCountCaptured,
        int ReplayFrameCountCaptured,
        int ReplayFrameCountAttempted,
        int FramesSubmitted,
        int NeedMoreInputCount,
        bool SawOutputSample,
        bool CreatedBitmap,
        int MaxFrameIndexAttempted,
        int FirstOutputFrameIndex,
        bool SampleReadySeen,
        int ExpectedWidth,
        int ExpectedHeight,
        int ChosenWidth,
        int ChosenHeight,
        string OutputSubtype,
        string OutputSubtypeCandidate,
        string OutputSubtypeProbe,
        bool OutputSubtypeCandidateIsNativeAdvertised,
        string FirstSuccessfulOutputSubtype,
        string OutputSampleProvider,
        string OutputRetrievalMode,
        string WinningOutputCombination,
        string LastAttemptedOutputCombination,
        string InputSampleStrategy,
        string Backend,
        string AttributeProfile,
        string ActivationSource,
        string FriendlyName,
        uint OutputBufferSize,
        uint OutputBufferAlignment,
        string LastOutputFailureStage,
        string LastOutputFailureHresult,
        int TransformId,
        bool TransformOutputTypeConfigured,
        bool TransformOutputTypeVerified,
        bool TransformInputTypeConfigured,
        string StartupSequence,
        bool StartupSequenceVerified,
        bool BeginStreamingSent,
        string BeginStreamingHresult,
        bool StartOfStreamSent,
        string StartOfStreamHresult,
        bool FullyStartedBeforeFirstInput,
        string SetOutputTypeHresult,
        string GetOutputCurrentTypeHresult,
        string PostConfigurationOutputStatus,
        string PostConfigurationOutputStatusHresult,
        string InputStatus,
        string InputStatusHresult,
        string OutputStatus,
        string OutputStatusHresult,
        string ProcessOutputHresult,
        string OutputDataBufferStatus,
        string TransformAttributes,
        string TransformAttributesBeforeProfile,
        string TransformAttributesAfterProfile,
        string InputStreamAttributes,
        string OutputStreamAttributes,
        bool LowLatencyRequested,
        bool LowLatencyApplied,
        bool TransformLowLatencyApplied,
        string TransformLowLatencyHresult,
        bool InputMediaTypeLowLatencyApplied,
        string InputMediaTypeLowLatencyHresult,
        bool OutputMediaTypeLowLatencyApplied,
        string OutputMediaTypeLowLatencyHresult,
        bool CodecApiAvailable,
        bool CodecApiSupported,
        string CodecApiIsSupportedHresult,
        bool CodecApiModifiable,
        string CodecApiIsModifiableHresult,
        bool CodecApiLowLatencyApplied,
        string CodecApiSetValueHresult,
        bool DrainAttempted,
        bool DiagnosticDrainAttempted,
        DateTimeOffset CapturedUtc);
}
