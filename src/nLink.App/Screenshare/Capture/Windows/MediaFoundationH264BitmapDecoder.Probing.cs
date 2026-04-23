using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Media.Imaging;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed partial class MediaFoundationH264BitmapDecoder
{
    private OutputMatrixCombinationResult DrainBufferedFramesWithCombination(
        DecoderTransformState decoderTransformState,
        long streamEpoch,
        int framesReplayed,
        OutputContractCombination combination)
    {
        drainAttemptedThisEpoch = true;
        try
        {
            decoderTransformState.Transform.ProcessMessage(MftMessageNotifyEndOfStream, IntPtr.Zero);
            decoderTransformState.Transform.ProcessMessage(MftMessageCommandDrain, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            return ApplyTransformStartupState(new OutputMatrixCombinationResult(
                combination,
                decoderTransformState.TransformId,
                decoderTransformState.OutputTypeConfigured,
                decoderTransformState.OutputTypeVerified,
                decoderTransformState.SetOutputTypeHresult,
                decoderTransformState.GetOutputCurrentTypeHresult,
                decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                Bitmap: null,
                Failure: ex,
                FailureStage: "send_drain_message",
                FailureHresult: ex.HResult,
                ProviderContractFailure: false,
                RetrievalContractFailure: true,
                NeedMoreInputObserved: false,
                SuccessWithoutSample: false,
                OutputReadyWithoutSample: false,
                FramesReplayed: framesReplayed,
                ProcessOutputAttempts: 0,
                StreamChangeObserved: false,
                CallerProvidedSample: combination.Shape != OutputSampleShapeKind.NullSampleDiagnostic,
                SampleOrigin: OutputSampleOrigin.None,
                OutputFlags: 0,
                OutputDataBufferStatus: 0,
                InputStatusFlags: 0,
                InputStatusHresult: 0,
                OutputStatusFlags: 0,
                OutputStatusHresult: 0,
                ProcessOutputHresult: 0), decoderTransformState);
        }

        var outputResult = ProcessOutputWithCombination(
            decoderTransformState,
            streamEpoch,
            combination,
            logLifecycleEvents: false);

        return ApplyTransformStartupState(new OutputMatrixCombinationResult(
            combination,
            outputResult.TransformId,
            outputResult.OutputTypeConfiguredOnTransform,
            outputResult.OutputTypeVerifiedOnTransform,
            outputResult.SetOutputTypeHresult,
            outputResult.GetOutputCurrentTypeHresult,
            outputResult.OutputStatusAfterConfigurationHresult,
            outputResult.OutputStatusAfterConfigurationFlags,
            outputResult.Bitmap,
            outputResult.Failure,
            outputResult.FailureStage,
            outputResult.Failure?.HResult ?? outputResult.ProcessOutputHresult,
            outputResult.ProviderContractFailure,
            outputResult.RetrievalContractFailure || outputResult.NeedMoreInput,
            outputResult.NeedMoreInput,
            outputResult.SuccessWithoutSample,
            outputResult.OutputReadyWithoutSample,
            framesReplayed,
            outputResult.ProcessOutputAttempts,
            outputResult.StreamChangeObserved,
            outputResult.CallerProvidedSample,
            outputResult.SampleOrigin,
            outputResult.OutputFlags,
            outputResult.OutputDataBufferStatus,
            outputResult.InputStatusFlags,
            outputResult.InputStatusHresult,
            outputResult.OutputStatusFlags,
            outputResult.OutputStatusHresult,
            outputResult.ProcessOutputHresult), decoderTransformState);
    }

    private OutputMatrixProbeSuccess? ProbeOutputMatrix(
        DecoderTransformState initialTransformState,
        long streamEpoch,
        InputSampleStrategyKind firstFrameStrategy,
        bool outputTypeChanged)
    {
        var summaries = new List<string>();
        Exception? lastFailure = null;
        var shapes = EnumerateCallerProvidedOutputShapes();
        EnsureHardwareDecoderAvailabilityDiagnostic(streamEpoch);
        var backendProfiles = EnumerateBackendProfiles();
        var outputSubtypeCandidates = EnumerateOutputSubtypeCandidates(initialTransformState, streamEpoch);

        LogLifecycle(
            "screenshare_h264_decoder_output_matrix_started",
            streamEpoch,
            $"strategy={FormatInputSampleStrategy(firstFrameStrategy)}; buffered_frames={outputProbeFrames.Count}; shapes={shapes.Count}; backend_profiles={backendProfiles.Count}; output_subtype_candidates={outputSubtypeCandidates.Count}");

        for (var subtypeIndex = 0; subtypeIndex < outputSubtypeCandidates.Count; subtypeIndex++)
        {
            var outputSubtypeCandidate = outputSubtypeCandidates[subtypeIndex];
            for (var backendIndex = 0; backendIndex < backendProfiles.Count; backendIndex++)
            {
                var backendProfile = backendProfiles[backendIndex];
                for (var shapeIndex = 0; shapeIndex < shapes.Count; shapeIndex++)
                {
                    var shape = shapes[shapeIndex];
                    var normalCombination = new OutputContractCombination(shape, OutputRetrievalMode.NormalProcessOutput);
                    var useInitialTransform =
                        subtypeIndex == 0 &&
                        backendIndex == 0 &&
                        shapeIndex == 0 &&
                        initialTransformState.BackendKind == backendProfile.Backend &&
                        initialTransformState.AttributeProfile == backendProfile.AttributeProfile;
                    DecoderTransformState? probeTransformState = useInitialTransform ? initialTransformState : null;
                    var ownsTransform = !useInitialTransform;
                    try
                    {
                        if (ownsTransform)
                        {
                            probeTransformState = CreateConfiguredTransform(
                                configuration!,
                                backendProfile.Backend,
                                backendProfile.AttributeProfile,
                                outputSubtypeCandidate.ProbeKind);
                        }
                        else
                        {
                            probeTransformState!.OutputSubtypeProbeKind = outputSubtypeCandidate.ProbeKind;
                        }

                        var combinationResult = ReplayBufferedFramesWithCombination(
                            probeTransformState!,
                            streamEpoch,
                            firstFrameStrategy,
                            normalCombination,
                            TransformStartupSequenceKind.TypesBeforeStart,
                            emitFirstFrameLogs: useInitialTransform);
                        summaries.Add(FormatOutputMatrixSummary(combinationResult));
                        RememberOutputMatrixResult(combinationResult, probeTransformState!);
                        if (combinationResult.Failure is not null)
                        {
                            lastFailure = combinationResult.Failure;
                        }

                        if (combinationResult.Bitmap is not null)
                        {
                            PromoteWinningOutputCombination(combinationResult.Combination);
                            PromoteWinningDecoderBackendProfile(backendProfile);
                            PromoteWinningOutputSubtype(probeTransformState!);
                            LogLifecycle(
                                "screenshare_h264_decoder_output_combination_selected",
                                streamEpoch,
                                $"backend={FormatDecoderBackend(backendProfile.Backend)}; attribute_profile={FormatDecoderAttributeProfile(backendProfile.AttributeProfile)}; startup_sequence={FormatStartupSequence(TransformStartupSequenceKind.TypesBeforeStart)}; combination={FormatOutputCombination(combinationResult.Combination)}; output_subtype_probe={FormatOutputSubtypeProbeKind(probeTransformState!.OutputSubtypeProbeKind)}; output_subtype={FormatVideoSubtype(probeTransformState.OutputSubtype)}; native_advertised={(probeTransformState.OutputSubtypeCandidateWasNativeAdvertised ? 1 : 0)}; output_type_changed={(outputTypeChanged ? 1 : 0)}; frames_replayed={combinationResult.FramesReplayed}; output_attempts={combinationResult.ProcessOutputAttempts}; output_flags={FormatOutputStreamFlags(combinationResult.OutputFlags)}");
                            outputProbeFrames.Clear();
                            return new OutputMatrixProbeSuccess(
                                combinationResult.Bitmap,
                                probeTransformState!,
                                combinationResult.Combination,
                                backendProfile,
                                UsedDrain: false);
                        }

                        if (ShouldAttemptDrain(combinationResult))
                        {
                            diagnosticDrainAttemptedThisEpoch = true;
                            var drainCombination = new OutputContractCombination(shape, OutputRetrievalMode.EndOfStreamDrain);
                            var drainResult = DrainBufferedFramesWithCombination(
                                probeTransformState!,
                                streamEpoch,
                                combinationResult.FramesReplayed,
                                drainCombination);
                            summaries.Add(FormatOutputMatrixSummary(drainResult));
                            RememberOutputMatrixResult(drainResult, probeTransformState!);
                            if (drainResult.Failure is not null)
                            {
                                lastFailure = drainResult.Failure;
                            }

                            if (drainResult.Bitmap is not null)
                            {
                                diagnosticDrainProducedOutputThisEpoch = true;
                                debugLastOutputMatrixSummary = string.Join(" | ", summaries);
                                LogLifecycle(
                                    "screenshare_h264_decoder_diagnostic_output_observed",
                                    streamEpoch,
                                    $"backend={FormatDecoderBackend(backendProfile.Backend)}; attribute_profile={FormatDecoderAttributeProfile(backendProfile.AttributeProfile)}; startup_sequence={FormatStartupSequence(TransformStartupSequenceKind.TypesBeforeStart)}; combination={FormatOutputCombination(drainResult.Combination)}; output_subtype_probe={FormatOutputSubtypeProbeKind(probeTransformState!.OutputSubtypeProbeKind)}; output_subtype={FormatVideoSubtype(probeTransformState.OutputSubtype)}; native_advertised={(probeTransformState.OutputSubtypeCandidateWasNativeAdvertised ? 1 : 0)}; retrieval_mode={FormatOutputRetrievalMode(drainResult.Combination.RetrievalMode)}; promotable=0");
                            }
                        }
                    }
                    catch (Exception ex) when (ownsTransform)
                    {
                        summaries.Add(
                            $"backend={FormatDecoderBackend(backendProfile.Backend)},attribute_profile={FormatDecoderAttributeProfile(backendProfile.AttributeProfile)},activation_source={(probeTransformState is null ? "unknown" : Sanitize(probeTransformState.ActivationSource))},friendly_name={(probeTransformState is null ? "unknown" : Sanitize(probeTransformState.FriendlyName))},output_subtype_probe={FormatOutputSubtypeProbeKind(outputSubtypeCandidate.ProbeKind)},output_subtype={FormatVideoSubtype(outputSubtypeCandidate.Subtype)},native_advertised={(outputSubtypeCandidate.IsNativeAdvertisedCandidate ? 1 : 0)},stage=create_transform,failure={ex.GetType().Name}:0x{ex.HResult:X8}");
                        lastFailure = ex;
                    }
                    finally
                    {
                        if (ownsTransform && probeTransformState is not null)
                        {
                            ReleaseTransformState(probeTransformState, flush: true);
                        }
                    }
                }
            }
        }

        debugLastOutputMatrixSummary = string.Join(" | ", summaries);
        LogLifecycle(
            "screenshare_h264_decoder_output_matrix_summary",
            streamEpoch,
            $"strategy={FormatInputSampleStrategy(firstFrameStrategy)}; buffered_frames={outputProbeFrames.Count}; summary={Sanitize(debugLastOutputMatrixSummary)}");

        if (outputProbeFrames.Count >= OutputProbeFrameLimit)
        {
            if (legacyStartupSequenceProducedOutputThisEpoch)
            {
                throw new InvalidOperationException("Media Foundation H.264 decoder only produced output under the legacy start-before-output-type sequence.");
            }

            if (diagnosticDrainProducedOutputThisEpoch)
            {
                throw new InvalidOperationException("Media Foundation H.264 decoder only produced output after end-of-stream drain.");
            }

            if (lastFailure is not null)
            {
                throw lastFailure;
            }

            throw new InvalidOperationException("Media Foundation H.264 decoder did not produce output for any native output subtype candidate.");
        }

        return null;
    }

    private OutputMatrixCombinationResult ReplayBufferedFramesWithCombination(
        DecoderTransformState decoderTransformState,
        long streamEpoch,
        InputSampleStrategyKind firstFrameStrategy,
        OutputContractCombination combination,
        TransformStartupSequenceKind startupSequence,
        bool emitFirstFrameLogs)
    {
        var framesReplayed = 0;
        var totalOutputAttempts = 0;
        var streamChangeObserved = false;
        var callerProvidedSample = combination.Shape != OutputSampleShapeKind.NullSampleDiagnostic;
        var sampleOrigin = OutputSampleOrigin.None;
        uint outputFlags = 0;
        uint outputDataBufferStatus = 0;
        uint inputStatusFlags = 0;
        int inputStatusHresult = 0;
        uint outputStatusFlags = 0;
        int outputStatusHresult = 0;
        int processOutputHresult = 0;
        var failureStage = "(none)";
        var needMoreInputObserved = false;
        var successWithoutSampleObserved = false;
        var outputReadyWithoutSampleObserved = false;
        var providerContractFailure = false;
        var retrievalContractFailure = false;

        try
        {
            EnsureTransformReadyForInput(decoderTransformState, streamEpoch, startupSequence);
            for (var index = 0; index < outputProbeFrames.Count; index++)
            {
                var frame = outputProbeFrames[index];
                var frameIndex = index + 1;
                var isFirstFrame = index == 0;
                var strategyKind = isFirstFrame
                    ? firstFrameStrategy
                    : GetFallbackInputSampleStrategyForFrame(frame.IsKeyFrame, isFirstFrameOfEpoch: false);
                var contract = ResolveInputSampleContract(strategyKind, frame.IsKeyFrame, isFirstFrame);
                var sampleTimeHns = checked(index * DefaultInputSampleDurationHns);
                var inputSample = CreateInputSample(frame.EncodedBytes, sampleTimeHns, contract);
                try
                {
                    if (emitFirstFrameLogs && isFirstFrame)
                    {
                        inputSampleStrategyThisEpoch = firstFrameStrategy;
                        LogInputSampleStrategySelected(streamEpoch, firstFrameStrategy, inputSample, contract, frame.IsKeyFrame);
                        processInputReachedThisEpoch = true;
                        LogLifecycle(
                            "screenshare_h264_decoder_process_input_reached",
                            streamEpoch,
                            $"strategy={FormatInputSampleStrategy(firstFrameStrategy)}; normalized_input_bytes={frame.EncodedBytes.Length}; output_combination={FormatOutputCombination(combination)}");
                    }

                    Marshal.ThrowExceptionForHR(decoderTransformState.Transform.ProcessInput(0, inputSample, 0));
                    framesReplayed++;
                    RecordReplayFrameAttempt(frameIndex);
                    if (emitFirstFrameLogs && isFirstFrame)
                    {
                        processInputSucceededThisEpoch = true;
                        RecordProcessInputSucceeded(streamEpoch, frame.EncodedBytes.Length, firstFrameStrategy, contract);
                    }
                }
                finally
                {
                    ReleaseComObject(inputSample);
                }

                if (combination.RetrievalMode == OutputRetrievalMode.NormalProcessOutput)
                {
                    var outputResult = ProcessOutputWithCombination(
                        decoderTransformState,
                        streamEpoch,
                        combination,
                        logLifecycleEvents: false);
                    totalOutputAttempts += outputResult.ProcessOutputAttempts;
                    streamChangeObserved |= outputResult.StreamChangeObserved;
                    callerProvidedSample = outputResult.CallerProvidedSample;
                    sampleOrigin = outputResult.SampleOrigin;
                    outputFlags = outputResult.OutputFlags;
                    outputDataBufferStatus = outputResult.OutputDataBufferStatus;
                    inputStatusFlags = outputResult.InputStatusFlags;
                    inputStatusHresult = outputResult.InputStatusHresult;
                    outputStatusFlags = outputResult.OutputStatusFlags;
                    outputStatusHresult = outputResult.OutputStatusHresult;
                    processOutputHresult = outputResult.ProcessOutputHresult;
                    failureStage = outputResult.FailureStage;
                    needMoreInputObserved |= outputResult.NeedMoreInput;
                    successWithoutSampleObserved |= outputResult.SuccessWithoutSample;
                    outputReadyWithoutSampleObserved |= outputResult.OutputReadyWithoutSample;
                    providerContractFailure |= outputResult.ProviderContractFailure;
                    retrievalContractFailure |= outputResult.RetrievalContractFailure;
                    RecordOutputStatusObservation(outputResult.OutputStatusHresult, outputResult.OutputStatusFlags);
                    LogReplayFrameProgress(
                        streamEpoch,
                        frameIndex,
                        frame,
                        combination,
                        decoderTransformState,
                        outputResult);
                    if (outputResult.Bitmap is not null)
                    {
                        RecordReplayFrameProducedOutput(frameIndex);
                        return ApplyTransformStartupState(new OutputMatrixCombinationResult(
                            combination,
                            outputResult.TransformId,
                            outputResult.OutputTypeConfiguredOnTransform,
                            outputResult.OutputTypeVerifiedOnTransform,
                            outputResult.SetOutputTypeHresult,
                            outputResult.GetOutputCurrentTypeHresult,
                            outputResult.OutputStatusAfterConfigurationHresult,
                            outputResult.OutputStatusAfterConfigurationFlags,
                            outputResult.Bitmap,
                            Failure: null,
                            FailureStage: outputResult.FailureStage,
                            FailureHresult: 0,
                            ProviderContractFailure: false,
                            RetrievalContractFailure: false,
                            NeedMoreInputObserved: needMoreInputObserved,
                            SuccessWithoutSample: successWithoutSampleObserved,
                            OutputReadyWithoutSample: outputReadyWithoutSampleObserved,
                            FramesReplayed: framesReplayed,
                            ProcessOutputAttempts: totalOutputAttempts,
                            StreamChangeObserved: streamChangeObserved,
                            CallerProvidedSample: callerProvidedSample,
                            SampleOrigin: sampleOrigin,
                            OutputFlags: outputFlags,
                            OutputDataBufferStatus: outputDataBufferStatus,
                            InputStatusFlags: inputStatusFlags,
                            InputStatusHresult: inputStatusHresult,
                            OutputStatusFlags: outputStatusFlags,
                            OutputStatusHresult: outputStatusHresult,
                            ProcessOutputHresult: processOutputHresult), decoderTransformState);
                    }

                    if (outputResult.Failure is not null)
                    {
                        return ApplyTransformStartupState(new OutputMatrixCombinationResult(
                            combination,
                            outputResult.TransformId,
                            outputResult.OutputTypeConfiguredOnTransform,
                            outputResult.OutputTypeVerifiedOnTransform,
                            outputResult.SetOutputTypeHresult,
                            outputResult.GetOutputCurrentTypeHresult,
                            outputResult.OutputStatusAfterConfigurationHresult,
                            outputResult.OutputStatusAfterConfigurationFlags,
                            Bitmap: null,
                            Failure: outputResult.Failure,
                            FailureStage: outputResult.FailureStage,
                            FailureHresult: outputResult.Failure.HResult,
                            ProviderContractFailure: outputResult.ProviderContractFailure,
                            RetrievalContractFailure: outputResult.RetrievalContractFailure,
                            NeedMoreInputObserved: needMoreInputObserved,
                            SuccessWithoutSample: successWithoutSampleObserved,
                            OutputReadyWithoutSample: outputReadyWithoutSampleObserved,
                            FramesReplayed: framesReplayed,
                            ProcessOutputAttempts: totalOutputAttempts,
                            StreamChangeObserved: streamChangeObserved,
                            CallerProvidedSample: callerProvidedSample,
                            SampleOrigin: sampleOrigin,
                            OutputFlags: outputFlags,
                            OutputDataBufferStatus: outputDataBufferStatus,
                            InputStatusFlags: inputStatusFlags,
                            InputStatusHresult: inputStatusHresult,
                            OutputStatusFlags: outputStatusFlags,
                            OutputStatusHresult: outputStatusHresult,
                            ProcessOutputHresult: processOutputHresult), decoderTransformState);
                    }
                }
            }

            return ApplyTransformStartupState(new OutputMatrixCombinationResult(
                combination,
                decoderTransformState.TransformId,
                decoderTransformState.OutputTypeConfigured,
                decoderTransformState.OutputTypeVerified,
                decoderTransformState.SetOutputTypeHresult,
                decoderTransformState.GetOutputCurrentTypeHresult,
                decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                Bitmap: null,
                Failure: null,
                FailureStage: needMoreInputObserved ? "process_output_need_more_input" : failureStage,
                FailureHresult: 0,
                ProviderContractFailure: providerContractFailure,
                RetrievalContractFailure: retrievalContractFailure,
                NeedMoreInputObserved: needMoreInputObserved,
                SuccessWithoutSample: successWithoutSampleObserved,
                OutputReadyWithoutSample: outputReadyWithoutSampleObserved,
                FramesReplayed: framesReplayed,
                ProcessOutputAttempts: totalOutputAttempts,
                StreamChangeObserved: streamChangeObserved,
                CallerProvidedSample: callerProvidedSample,
                SampleOrigin: sampleOrigin,
                OutputFlags: outputFlags,
                OutputDataBufferStatus: outputDataBufferStatus,
                InputStatusFlags: inputStatusFlags,
                InputStatusHresult: inputStatusHresult,
                OutputStatusFlags: outputStatusFlags,
                OutputStatusHresult: outputStatusHresult,
                ProcessOutputHresult: processOutputHresult), decoderTransformState);
        }
        catch (Exception ex)
        {
            return ApplyTransformStartupState(new OutputMatrixCombinationResult(
                combination,
                decoderTransformState.TransformId,
                decoderTransformState.OutputTypeConfigured,
                decoderTransformState.OutputTypeVerified,
                decoderTransformState.SetOutputTypeHresult,
                decoderTransformState.GetOutputCurrentTypeHresult,
                decoderTransformState.GetOutputStatusAfterConfigurationHresult,
                decoderTransformState.GetOutputStatusAfterConfigurationFlags,
                Bitmap: null,
                Failure: ex,
                FailureStage: string.IsNullOrWhiteSpace(failureStage) || failureStage == "(none)" ? "replay_frames" : failureStage,
                FailureHresult: ex.HResult,
                ProviderContractFailure: providerContractFailure,
                RetrievalContractFailure: true,
                NeedMoreInputObserved: needMoreInputObserved,
                SuccessWithoutSample: successWithoutSampleObserved,
                OutputReadyWithoutSample: outputReadyWithoutSampleObserved,
                FramesReplayed: framesReplayed,
                ProcessOutputAttempts: totalOutputAttempts,
                StreamChangeObserved: streamChangeObserved,
                CallerProvidedSample: callerProvidedSample,
                SampleOrigin: sampleOrigin,
                OutputFlags: outputFlags,
                OutputDataBufferStatus: outputDataBufferStatus,
                InputStatusFlags: inputStatusFlags,
                InputStatusHresult: inputStatusHresult,
                OutputStatusFlags: outputStatusFlags,
                OutputStatusHresult: outputStatusHresult,
                ProcessOutputHresult: processOutputHresult), decoderTransformState);
        }
    }


    private static OutputMatrixCombinationResult ApplyTransformStartupState(OutputMatrixCombinationResult result, DecoderTransformState decoderTransformState)
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

    private void RememberOutputProcessingResult(DecoderTransformState decoderTransformState, OutputContractCombination combination, OutputProcessingResult outputResult)
    {
        outputSampleShapeThisEpoch = combination.Shape;
        outputSampleProviderThisEpoch = combination.Provider;
        outputRetrievalModeThisEpoch = combination.RetrievalMode;
        lastBackendKindThisEpoch = decoderTransformState.BackendKind;
        lastAttributeProfileThisEpoch = decoderTransformState.AttributeProfile;
        lastActivationSourceThisEpoch = decoderTransformState.ActivationSource;
        lastFriendlyNameThisEpoch = decoderTransformState.FriendlyName;
        lastTransformAttributeSnapshotThisEpoch = decoderTransformState.TransformAttributesSnapshot;
        lastTransformAttributeSnapshotBeforeProfileThisEpoch = decoderTransformState.TransformAttributesSnapshotBeforeProfile;
        lastTransformAttributeSnapshotAfterProfileThisEpoch = decoderTransformState.TransformAttributesSnapshotAfterProfile;
        lastInputStreamAttributeSnapshotThisEpoch = decoderTransformState.InputStreamAttributesSnapshot;
        lastOutputStreamAttributeSnapshotThisEpoch = decoderTransformState.OutputStreamAttributesSnapshot;
        lastOutputSubtypeCandidateThisEpoch = FormatVideoSubtype(decoderTransformState.EffectiveOutputSubtypeCandidate);
        lastOutputSubtypeProbeKindThisEpoch = decoderTransformState.OutputSubtypeProbeKind;
        lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch = decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised;
        lastLowLatencyRequestedThisEpoch = decoderTransformState.LowLatencyRequested;
        lastLowLatencyAppliedThisEpoch =
            decoderTransformState.LowLatencyAppliedToTransform ||
            decoderTransformState.LowLatencyAppliedToInputMediaType ||
            decoderTransformState.LowLatencyAppliedToOutputMediaType ||
            decoderTransformState.CodecApiLowLatencyApplied;
        lastTransformLowLatencyAppliedThisEpoch = decoderTransformState.LowLatencyAppliedToTransform;
        lastTransformLowLatencyHresultThisEpoch = decoderTransformState.TransformLowLatencyHresult;
        lastInputMediaTypeLowLatencyAppliedThisEpoch = decoderTransformState.LowLatencyAppliedToInputMediaType;
        lastInputMediaTypeLowLatencyHresultThisEpoch = decoderTransformState.InputMediaTypeLowLatencyHresult;
        lastOutputMediaTypeLowLatencyAppliedThisEpoch = decoderTransformState.LowLatencyAppliedToOutputMediaType;
        lastOutputMediaTypeLowLatencyHresultThisEpoch = decoderTransformState.OutputMediaTypeLowLatencyHresult;
        lastCodecApiAvailableThisEpoch = decoderTransformState.CodecApiAvailable;
        lastCodecApiSupportedThisEpoch = decoderTransformState.CodecApiSupported;
        lastCodecApiIsSupportedHresultThisEpoch = decoderTransformState.CodecApiIsSupportedHresult;
        lastCodecApiModifiableThisEpoch = decoderTransformState.CodecApiModifiable;
        lastCodecApiIsModifiableHresultThisEpoch = decoderTransformState.CodecApiIsModifiableHresult;
        lastCodecApiLowLatencyAppliedThisEpoch = decoderTransformState.CodecApiLowLatencyApplied;
        lastCodecApiSetValueHresultThisEpoch = decoderTransformState.CodecApiSetValueHresult;
        lastTransformIdThisEpoch = outputResult.TransformId;
        lastTransformOutputTypeConfiguredThisEpoch = outputResult.OutputTypeConfiguredOnTransform;
        lastTransformOutputTypeVerifiedThisEpoch = outputResult.OutputTypeVerifiedOnTransform;
        lastTransformSetOutputTypeHresultThisEpoch = outputResult.SetOutputTypeHresult;
        lastTransformGetOutputCurrentTypeHresultThisEpoch = outputResult.GetOutputCurrentTypeHresult;
        lastTransformGetOutputStatusHresultThisEpoch = outputResult.OutputStatusAfterConfigurationHresult;
        lastTransformGetOutputStatusFlagsThisEpoch = outputResult.OutputStatusAfterConfigurationFlags;
        lastTransformInputTypeConfiguredThisEpoch = decoderTransformState.InputTypeConfigured;
        lastTransformBeginStreamingSentThisEpoch = decoderTransformState.BeginStreamingSent;
        lastTransformStartOfStreamSentThisEpoch = decoderTransformState.StartOfStreamSent;
        lastTransformBeginStreamingHresultThisEpoch = decoderTransformState.BeginStreamingHresult;
        lastTransformStartOfStreamHresultThisEpoch = decoderTransformState.StartOfStreamHresult;
        lastTransformStartupSequenceThisEpoch = decoderTransformState.StartupSequence;
        lastTransformStartupSequenceVerifiedThisEpoch = decoderTransformState.StartupSequenceVerified;
        lastTransformFullyStartedBeforeFirstInputThisEpoch = decoderTransformState.FullyStartedBeforeFirstInput;
        lastOutputFailureStageThisEpoch = string.IsNullOrWhiteSpace(outputResult.FailureStage) ? "(none)" : outputResult.FailureStage;
        lastOutputFailureHresultThisEpoch = outputResult.Failure?.HResult ?? outputResult.ProcessOutputHresult;
        lastProcessOutputHresultThisEpoch = outputResult.ProcessOutputHresult;
        lastOutputDataBufferStatusThisEpoch = outputResult.OutputDataBufferStatus;
        lastOutputStatusFlagsThisEpoch = outputResult.OutputStatusFlags;
        lastOutputStatusHresultThisEpoch = outputResult.OutputStatusHresult;
        lastInputStatusFlagsThisEpoch = outputResult.InputStatusFlags;
        lastInputStatusHresultThisEpoch = outputResult.InputStatusHresult;
        sawSampleReadyThisEpoch |= outputResult.OutputStatusHresult >= 0 &&
                                   (outputResult.OutputStatusFlags & MftOutputStatusSampleReady) != 0;
        sawSuccessWithoutSampleThisEpoch |= outputResult.SuccessWithoutSample;
        sawOutputReadyWithoutSampleThisEpoch |= outputResult.OutputReadyWithoutSample;
        nullSampleDiagnosticEligibleThisEpoch |= outputResult.SuccessWithoutSample || outputResult.OutputReadyWithoutSample;
        attemptedNullSampleDiagnosticThisEpoch |= combination.Shape == OutputSampleShapeKind.NullSampleDiagnostic;
        nullSampleDiagnosticSucceededThisEpoch |= combination.Shape == OutputSampleShapeKind.NullSampleDiagnostic && outputResult.Bitmap is not null;
        drainAttemptedThisEpoch |= combination.RetrievalMode == OutputRetrievalMode.EndOfStreamDrain;
        debugLastAttemptedOutputCombination = FormatOutputCombination(combination);
        debugLastOutputFailureStage = lastOutputFailureStageThisEpoch;
        debugLastOutputFailureHresult = $"0x{lastOutputFailureHresultThisEpoch:X8}";
        if (outputResult.ProviderContractFailure)
        {
            outputProviderContractFailureThisEpoch = true;
        }

        if (outputResult.RetrievalContractFailure || outputResult.NeedMoreInput || outputResult.SuccessWithoutSample || outputResult.OutputReadyWithoutSample)
        {
        }

        RecordBackendProfileObservation(
            decoderTransformState.BackendKind,
            decoderTransformState.AttributeProfile,
            outputResult.Bitmap is not null || outputResult.OutputReadyWithoutSample);
        RecordOutputSubtypeObservation(
            decoderTransformState.OutputSubtypeProbeKind,
            outputResult.Bitmap is not null);
    }

    private void RememberOutputMatrixResult(OutputMatrixCombinationResult result, DecoderTransformState decoderTransformState)
    {
        outputSampleShapeThisEpoch = result.Combination.Shape;
        outputSampleProviderThisEpoch = result.Combination.Provider;
        outputRetrievalModeThisEpoch = result.Combination.RetrievalMode;
        lastBackendKindThisEpoch = decoderTransformState.BackendKind;
        lastAttributeProfileThisEpoch = decoderTransformState.AttributeProfile;
        lastActivationSourceThisEpoch = decoderTransformState.ActivationSource;
        lastFriendlyNameThisEpoch = decoderTransformState.FriendlyName;
        lastTransformAttributeSnapshotThisEpoch = decoderTransformState.TransformAttributesSnapshot;
        lastTransformAttributeSnapshotBeforeProfileThisEpoch = decoderTransformState.TransformAttributesSnapshotBeforeProfile;
        lastTransformAttributeSnapshotAfterProfileThisEpoch = decoderTransformState.TransformAttributesSnapshotAfterProfile;
        lastInputStreamAttributeSnapshotThisEpoch = decoderTransformState.InputStreamAttributesSnapshot;
        lastOutputStreamAttributeSnapshotThisEpoch = decoderTransformState.OutputStreamAttributesSnapshot;
        lastOutputSubtypeCandidateThisEpoch = FormatVideoSubtype(decoderTransformState.EffectiveOutputSubtypeCandidate);
        lastOutputSubtypeProbeKindThisEpoch = decoderTransformState.OutputSubtypeProbeKind;
        lastOutputSubtypeCandidateWasNativeAdvertisedThisEpoch = decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised;
        lastLowLatencyRequestedThisEpoch = decoderTransformState.LowLatencyRequested;
        lastLowLatencyAppliedThisEpoch =
            decoderTransformState.LowLatencyAppliedToTransform ||
            decoderTransformState.LowLatencyAppliedToInputMediaType ||
            decoderTransformState.LowLatencyAppliedToOutputMediaType ||
            decoderTransformState.CodecApiLowLatencyApplied;
        lastTransformLowLatencyAppliedThisEpoch = decoderTransformState.LowLatencyAppliedToTransform;
        lastTransformLowLatencyHresultThisEpoch = decoderTransformState.TransformLowLatencyHresult;
        lastInputMediaTypeLowLatencyAppliedThisEpoch = decoderTransformState.LowLatencyAppliedToInputMediaType;
        lastInputMediaTypeLowLatencyHresultThisEpoch = decoderTransformState.InputMediaTypeLowLatencyHresult;
        lastOutputMediaTypeLowLatencyAppliedThisEpoch = decoderTransformState.LowLatencyAppliedToOutputMediaType;
        lastOutputMediaTypeLowLatencyHresultThisEpoch = decoderTransformState.OutputMediaTypeLowLatencyHresult;
        lastCodecApiAvailableThisEpoch = decoderTransformState.CodecApiAvailable;
        lastCodecApiSupportedThisEpoch = decoderTransformState.CodecApiSupported;
        lastCodecApiIsSupportedHresultThisEpoch = decoderTransformState.CodecApiIsSupportedHresult;
        lastCodecApiModifiableThisEpoch = decoderTransformState.CodecApiModifiable;
        lastCodecApiIsModifiableHresultThisEpoch = decoderTransformState.CodecApiIsModifiableHresult;
        lastCodecApiLowLatencyAppliedThisEpoch = decoderTransformState.CodecApiLowLatencyApplied;
        lastCodecApiSetValueHresultThisEpoch = decoderTransformState.CodecApiSetValueHresult;
        lastTransformIdThisEpoch = result.TransformId;
        lastTransformOutputTypeConfiguredThisEpoch = result.OutputTypeConfiguredOnTransform;
        lastTransformOutputTypeVerifiedThisEpoch = result.OutputTypeVerifiedOnTransform;
        lastTransformSetOutputTypeHresultThisEpoch = result.SetOutputTypeHresult;
        lastTransformGetOutputCurrentTypeHresultThisEpoch = result.GetOutputCurrentTypeHresult;
        lastTransformGetOutputStatusHresultThisEpoch = result.OutputStatusAfterConfigurationHresult;
        lastTransformGetOutputStatusFlagsThisEpoch = result.OutputStatusAfterConfigurationFlags;
        lastTransformInputTypeConfiguredThisEpoch = decoderTransformState.InputTypeConfigured;
        lastTransformBeginStreamingSentThisEpoch = decoderTransformState.BeginStreamingSent;
        lastTransformStartOfStreamSentThisEpoch = decoderTransformState.StartOfStreamSent;
        lastTransformBeginStreamingHresultThisEpoch = decoderTransformState.BeginStreamingHresult;
        lastTransformStartOfStreamHresultThisEpoch = decoderTransformState.StartOfStreamHresult;
        lastTransformStartupSequenceThisEpoch = decoderTransformState.StartupSequence;
        lastTransformStartupSequenceVerifiedThisEpoch = decoderTransformState.StartupSequenceVerified;
        lastTransformFullyStartedBeforeFirstInputThisEpoch = decoderTransformState.FullyStartedBeforeFirstInput;
        lastOutputFailureStageThisEpoch = string.IsNullOrWhiteSpace(result.FailureStage) ? "(none)" : result.FailureStage;
        lastOutputFailureHresultThisEpoch = result.FailureHresult;
        lastProcessOutputHresultThisEpoch = result.ProcessOutputHresult;
        lastOutputDataBufferStatusThisEpoch = result.OutputDataBufferStatus;
        lastOutputStatusFlagsThisEpoch = result.OutputStatusFlags;
        lastOutputStatusHresultThisEpoch = result.OutputStatusHresult;
        lastInputStatusFlagsThisEpoch = result.InputStatusFlags;
        lastInputStatusHresultThisEpoch = result.InputStatusHresult;
        sawSampleReadyThisEpoch |= result.OutputStatusHresult >= 0 &&
                                   (result.OutputStatusFlags & MftOutputStatusSampleReady) != 0;
        sawSuccessWithoutSampleThisEpoch |= result.SuccessWithoutSample;
        sawOutputReadyWithoutSampleThisEpoch |= result.OutputReadyWithoutSample;
        nullSampleDiagnosticEligibleThisEpoch |= result.SuccessWithoutSample || result.OutputReadyWithoutSample;
        attemptedNullSampleDiagnosticThisEpoch |= result.Combination.Shape == OutputSampleShapeKind.NullSampleDiagnostic;
        nullSampleDiagnosticSucceededThisEpoch |= result.Combination.Shape == OutputSampleShapeKind.NullSampleDiagnostic && result.Bitmap is not null;
        drainAttemptedThisEpoch |= result.Combination.RetrievalMode == OutputRetrievalMode.EndOfStreamDrain;
        debugLastAttemptedOutputCombination = FormatOutputCombination(result.Combination);
        debugLastOutputFailureStage = lastOutputFailureStageThisEpoch;
        debugLastOutputFailureHresult = $"0x{lastOutputFailureHresultThisEpoch:X8}";
        if (result.ProviderContractFailure)
        {
            outputProviderContractFailureThisEpoch = true;
        }

        if (result.RetrievalContractFailure || result.NeedMoreInputObserved || result.SuccessWithoutSample || result.OutputReadyWithoutSample)
        {
        }

        RecordBackendProfileObservation(
            decoderTransformState.BackendKind,
            decoderTransformState.AttributeProfile,
            result.Bitmap is not null || result.OutputReadyWithoutSample);
        RecordOutputSubtypeObservation(
            decoderTransformState.OutputSubtypeProbeKind,
            result.Bitmap is not null);
    }

    private void RecordBackendProfileObservation(
        DecoderBackendKind backendKind,
        DecoderAttributeProfileKind attributeProfile,
        bool producedOutputOrSampleReady)
    {
        switch (backendKind, attributeProfile)
        {
            case (DecoderBackendKind.SoftwareFixedClsid, DecoderAttributeProfileKind.Baseline):
                softwareBaselineAttemptedThisEpoch = true;
                softwareBaselineSampleReadyThisEpoch |= producedOutputOrSampleReady;
                break;
            case (DecoderBackendKind.SoftwareFixedClsid, DecoderAttributeProfileKind.LowLatency):
                softwareLowLatencyAttemptedThisEpoch = true;
                softwareLowLatencySampleReadyThisEpoch |= producedOutputOrSampleReady;
                break;
            case (DecoderBackendKind.HardwareEnumFirst, DecoderAttributeProfileKind.Baseline):
                hardwareBaselineAttemptedThisEpoch = true;
                hardwareBaselineSampleReadyThisEpoch |= producedOutputOrSampleReady;
                break;
            case (DecoderBackendKind.HardwareEnumFirst, DecoderAttributeProfileKind.LowLatency):
                hardwareLowLatencyAttemptedThisEpoch = true;
                hardwareLowLatencySampleReadyThisEpoch |= producedOutputOrSampleReady;
                break;
        }
    }

    private void RecordOutputSubtypeObservation(
        OutputSubtypeProbeKind probeKind,
        bool producedOutput)
    {
        switch (probeKind)
        {
            case OutputSubtypeProbeKind.NativeAdvertisedFirstSupported:
                nativeAdvertisedOutputSubtypeProducedOutputThisEpoch |= producedOutput;
                break;
            case OutputSubtypeProbeKind.ExplicitNv12:
                explicitNv12OutputSubtypeProducedOutputThisEpoch |= producedOutput;
                break;
            case OutputSubtypeProbeKind.ExplicitYuy2:
                explicitYuy2OutputSubtypeProducedOutputThisEpoch |= producedOutput;
                break;
        }

        if (producedOutput && string.Equals(firstSuccessfulOutputSubtypeThisEpoch, "unknown", StringComparison.Ordinal))
        {
            firstSuccessfulOutputSubtypeThisEpoch = lastOutputSubtypeCandidateThisEpoch;
        }
    }

    private void PromoteWinningOutputCombination(OutputContractCombination combination)
    {
        if (combination.Shape == OutputSampleShapeKind.Unknown || combination.Shape == OutputSampleShapeKind.NullSampleDiagnostic)
        {
            return;
        }

        preferredOutputSampleShape = combination.Shape;
        preferredOutputSampleProvider = combination.Provider;
        preferredOutputRetrievalMode = combination.RetrievalMode;
        debugPreferredOutputSampleProvider = FormatOutputSampleProvider(combination.Provider);
        debugPreferredOutputCombination = FormatOutputCombination(combination);
    }

    private static void PromoteWinningOutputSubtype(DecoderTransformState decoderTransformState)
    {
        preferredOutputSubtypeProbeKind = decoderTransformState.OutputSubtype switch
        {
            var subtype when subtype == MfVideoFormatNv12 => OutputSubtypeProbeKind.ExplicitNv12,
            var subtype when subtype == MfVideoFormatYuy2 => OutputSubtypeProbeKind.ExplicitYuy2,
            _ => decoderTransformState.OutputSubtypeProbeKind,
        };
        debugPreferredOutputSubtype = FormatVideoSubtype(decoderTransformState.OutputSubtype);
    }

    private static void PromoteWinningDecoderBackendProfile(DecoderBackendProfileCombination backendProfile)
    {
        preferredDecoderBackend = backendProfile.Backend;
        preferredDecoderAttributeProfile = backendProfile.AttributeProfile;
        debugPreferredDecoderBackendProfile = FormatDecoderBackendProfile(backendProfile);
    }

    private static bool ShouldAttemptDrain(OutputMatrixCombinationResult result)
    {
        if (result.Bitmap is not null || result.Combination.RetrievalMode == OutputRetrievalMode.EndOfStreamDrain)
        {
            return false;
        }

        if (result.ProviderContractFailure)
        {
            return false;
        }

        return result.NeedMoreInputObserved || result.SuccessWithoutSample || result.OutputReadyWithoutSample;
    }


    private static bool HasPreferredOutputCombination()
        => preferredOutputSampleShape != OutputSampleShapeKind.Unknown &&
           preferredOutputRetrievalMode != OutputRetrievalMode.Unknown;

    private void BufferOutputProbeFrame(ReadOnlyMemory<byte> normalizedBytes, bool isKeyFrame)
    {
        if (outputProbeFrames.Count >= OutputProbeFrameLimit)
        {
            return;
        }

        if (!detailedReplayTimelineEnabledThisEpoch &&
            IsHelperRemoteRole() &&
            outputProbeFrames.Count == 0 &&
            Interlocked.CompareExchange(ref helperRemoteReplayTimelineCaptured, 1, 0) == 0)
        {
            detailedReplayTimelineEnabledThisEpoch = true;
        }

        outputProbeFrames.Add(new BufferedProbeFrame(normalizedBytes.ToArray(), isKeyFrame));
    }

    private void RecordReplayFrameAttempt(int frameIndex)
    {
        if (frameIndex > maxReplayFrameIndexThisEpoch)
        {
            maxReplayFrameIndexThisEpoch = frameIndex;
        }
    }

    private void RecordReplayFrameProducedOutput(int frameIndex)
    {
        RecordReplayFrameAttempt(frameIndex);
        if (firstOutputFrameIndexThisEpoch == 0)
        {
            firstOutputFrameIndexThisEpoch = frameIndex;
        }
    }

    private void RecordOutputStatusObservation(int outputStatusHresult, uint outputStatusFlags)
    {
        if (outputStatusHresult >= 0 && (outputStatusFlags & MftOutputStatusSampleReady) != 0)
        {
            sawSampleReadyThisEpoch = true;
        }
    }

    private void LogReplayFrameProgress(
        long streamEpoch,
        int frameIndex,
        BufferedProbeFrame frame,
        OutputContractCombination combination,
        DecoderTransformState decoderTransformState,
        OutputProcessingResult outputResult)
    {
        if (!detailedReplayTimelineEnabledThisEpoch)
        {
            return;
        }

        LogLifecycle(
            "screenshare_h264_decoder_replay_frame_progress",
            streamEpoch,
            $"frame_index={frameIndex}; is_keyframe={(frame.IsKeyFrame ? 1 : 0)}; normalized_input_bytes={frame.EncodedBytes.Length}; backend={FormatDecoderBackend(decoderTransformState.BackendKind)}; attribute_profile={FormatDecoderAttributeProfile(decoderTransformState.AttributeProfile)}; startup_sequence={FormatStartupSequence(decoderTransformState.StartupSequence)}; output_subtype_probe={FormatOutputSubtypeProbeKind(decoderTransformState.OutputSubtypeProbeKind)}; output_subtype_candidate={FormatVideoSubtype(decoderTransformState.EffectiveOutputSubtypeCandidate)}; native_advertised={(decoderTransformState.OutputSubtypeCandidateWasNativeAdvertised ? 1 : 0)}; output_combination={FormatOutputCombination(combination)}; input_status_hr=0x{outputResult.InputStatusHresult:X8}; input_status={FormatInputStatusFlags(outputResult.InputStatusFlags)}; output_status_hr=0x{outputResult.OutputStatusHresult:X8}; output_status={FormatOutputStatusFlags(outputResult.OutputStatusFlags)}; process_output_hr=0x{outputResult.ProcessOutputHresult:X8}; sample_ready_seen={(outputResult.OutputStatusHresult >= 0 && (outputResult.OutputStatusFlags & MftOutputStatusSampleReady) != 0 ? 1 : 0)}; bitmap={(outputResult.Bitmap is not null ? 1 : 0)}; failure_stage={Sanitize(outputResult.FailureStage)}");
    }

    private static IReadOnlyList<OutputSampleShapeKind> EnumerateCallerProvidedOutputShapes()
        => preferredOutputSampleShape != OutputSampleShapeKind.Unknown &&
           preferredOutputSampleShape != OutputSampleShapeKind.NullSampleDiagnostic
            ? [preferredOutputSampleShape]
            : [OutputSampleShapeKind.TwoDVideoBufferLengthPreset];

    private static IReadOnlyList<DecoderBackendProfileCombination> EnumerateBackendProfiles()
        => [new DecoderBackendProfileCombination(DecoderBackendKind.SoftwareFixedClsid, DecoderAttributeProfileKind.LowLatency)];

    private IReadOnlyList<OutputSubtypeCandidate> EnumerateOutputSubtypeCandidates(
        DecoderTransformState decoderTransformState,
        long streamEpoch)
    {
        var availableSubtypes = DiscoverOutputSubtypes(
            decoderTransformState.Transform,
            out var firstSupportedSubtype,
            out var nv12Available,
            out var yuy2Available);

        if (availableSubtypes.Count > 0)
        {
            LogLifecycle(
                "screenshare_h264_decoder_output_types_seen",
                streamEpoch,
                $"transform_id={decoderTransformState.TransformId}; types={string.Join(",", availableSubtypes)}");
        }

        var candidates = new List<OutputSubtypeCandidate>();
        if (firstSupportedSubtype == MfVideoFormatNv12 || firstSupportedSubtype == MfVideoFormatYuy2)
        {
            candidates.Add(new OutputSubtypeCandidate(
                firstSupportedSubtype,
                OutputSubtypeProbeKind.NativeAdvertisedFirstSupported,
                IsNativeAdvertisedCandidate: true));
        }

        if (nv12Available)
        {
            candidates.Add(new OutputSubtypeCandidate(
                MfVideoFormatNv12,
                OutputSubtypeProbeKind.ExplicitNv12,
                IsNativeAdvertisedCandidate: false));
        }

        if (yuy2Available)
        {
            candidates.Add(new OutputSubtypeCandidate(
                MfVideoFormatYuy2,
                OutputSubtypeProbeKind.ExplicitYuy2,
                IsNativeAdvertisedCandidate: false));
        }

        LogLifecycle(
            "screenshare_h264_decoder_output_subtype_candidates",
            streamEpoch,
            $"transform_id={decoderTransformState.TransformId}; candidates={string.Join(",", candidates.Select(static candidate => $"{FormatOutputSubtypeProbeKind(candidate.ProbeKind)}:{FormatVideoSubtype(candidate.Subtype)}"))}");

        return candidates;
    }

    private static List<string> DiscoverOutputSubtypes(
        IMFTransform decoderTransform,
        out Guid firstSupportedSubtype,
        out bool nv12Available,
        out bool yuy2Available)
    {
        firstSupportedSubtype = Guid.Empty;
        nv12Available = false;
        yuy2Available = false;
        var availableSubtypes = new List<string>();
        var typeIndex = 0u;
        while (true)
        {
            IMFMediaType? candidate = null;
            try
            {
                int hr;
                try
                {
                    hr = decoderTransform.GetOutputAvailableType(0, typeIndex, out candidate);
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

                var subtypeKey = MfMtSubtype;
                if (candidate.GetGUID(ref subtypeKey, out var subtype) < 0)
                {
                    typeIndex++;
                    continue;
                }

                availableSubtypes.Add(FormatVideoSubtype(subtype));
                if (subtype == MfVideoFormatNv12)
                {
                    nv12Available = true;
                }
                else if (subtype == MfVideoFormatYuy2)
                {
                    yuy2Available = true;
                }

                if (firstSupportedSubtype == Guid.Empty &&
                    (subtype == MfVideoFormatNv12 || subtype == MfVideoFormatYuy2))
                {
                    firstSupportedSubtype = subtype;
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

        return availableSubtypes;
    }

    private static string FormatHardwareDecoderDiagnosticSummary()
        => $"available={(hardwareDecoderDiagnosticAvailable ? 1 : 0)}; activation_source={Sanitize(hardwareDecoderDiagnosticActivationSource)}; friendly_name={Sanitize(hardwareDecoderDiagnosticFriendlyName)}; failure_stage={Sanitize(hardwareDecoderDiagnosticFailureStage)}; failure_hresult=0x{hardwareDecoderDiagnosticFailureHresult:X8}";

    private void EnsureHardwareDecoderAvailabilityDiagnostic(long streamEpoch)
    {
        bool shouldLog;
        lock (hardwareDecoderDiagnosticSync)
        {
            shouldLog = !hardwareDecoderDiagnosticChecked;
            if (!hardwareDecoderDiagnosticChecked)
            {
                IMFTransform? diagnosticTransform = null;
                try
                {
                    hardwareDecoderDiagnosticAvailable = TryCreateTransform(
                        DecoderBackendKind.HardwareEnumFirst,
                        out diagnosticTransform,
                        out hardwareDecoderDiagnosticActivationSource,
                        out hardwareDecoderDiagnosticFriendlyName,
                        out hardwareDecoderDiagnosticFailureStage,
                        out hardwareDecoderDiagnosticFailureHresult) && diagnosticTransform is not null;
                    if (hardwareDecoderDiagnosticAvailable)
                    {
                        hardwareDecoderDiagnosticFailureStage = "(none)";
                        hardwareDecoderDiagnosticFailureHresult = 0;
                    }
                }
                finally
                {
                    if (diagnosticTransform is not null)
                    {
                        ReleaseComObject(diagnosticTransform);
                    }
                }

                hardwareDecoderDiagnosticChecked = true;
                debugLastHardwareDecoderAvailabilitySummary = FormatHardwareDecoderDiagnosticSummary();
            }
        }

        hardwareDecoderAvailableThisEpoch = hardwareDecoderDiagnosticAvailable;
        if (!shouldLog)
        {
            return;
        }

        LogLifecycle(
            "screenshare_h264_decoder_hardware_backend_diagnostic",
            streamEpoch,
            debugLastHardwareDecoderAvailabilitySummary);
    }

    private static string FormatOutputMatrixSummary(OutputMatrixCombinationResult result)
    {
        var failure = result.Failure is null
            ? "none"
            : $"{result.Failure.GetType().Name}:0x{result.FailureHresult:X8}";
        return $"backend={FormatDecoderBackend(result.BackendKindOnTransform)},attribute_profile={FormatDecoderAttributeProfile(result.AttributeProfileOnTransform)},activation_source={Sanitize(result.ActivationSourceOnTransform)},friendly_name={Sanitize(result.FriendlyNameOnTransform)},output_subtype_probe={FormatOutputSubtypeProbeKind(result.OutputSubtypeProbeKindOnTransform)},output_subtype_candidate={FormatVideoSubtype(result.OutputSubtypeCandidateOnTransform)},output_subtype_native_advertised={(result.OutputSubtypeCandidateWasNativeAdvertisedOnTransform ? 1 : 0)},combination={FormatOutputCombination(result.Combination)},transform_id={result.TransformId},startup_sequence={FormatStartupSequence(result.StartupSequenceOnTransform)},startup_sequence_verified={(result.StartupSequenceVerifiedOnTransform ? 1 : 0)},input_type_configured={(result.InputTypeConfiguredOnTransform ? 1 : 0)},output_type_configured={(result.OutputTypeConfiguredOnTransform ? 1 : 0)},output_type_verified={(result.OutputTypeVerifiedOnTransform ? 1 : 0)},begin_streaming_sent={(result.BeginStreamingSentOnTransform ? 1 : 0)},begin_streaming_hr=0x{result.BeginStreamingHresultOnTransform:X8},start_of_stream_sent={(result.StartOfStreamSentOnTransform ? 1 : 0)},start_of_stream_hr=0x{result.StartOfStreamHresultOnTransform:X8},fully_started_before_first_input={(result.FullyStartedBeforeFirstInputOnTransform ? 1 : 0)},set_output_type_hr=0x{result.SetOutputTypeHresult:X8},get_output_current_type_hr=0x{result.GetOutputCurrentTypeHresult:X8},post_config_output_status_hr=0x{result.OutputStatusAfterConfigurationHresult:X8},post_config_output_status={FormatOutputStatusFlags(result.OutputStatusAfterConfigurationFlags)},low_latency_requested={(result.LowLatencyRequestedOnTransform ? 1 : 0)},low_latency_applied={(result.LowLatencyAppliedOnTransform ? 1 : 0)},transform_low_latency_applied={(result.TransformLowLatencyAppliedOnTransform ? 1 : 0)},transform_low_latency_hr=0x{result.TransformLowLatencyHresultOnTransform:X8},input_media_type_low_latency_applied={(result.InputMediaTypeLowLatencyAppliedOnTransform ? 1 : 0)},input_media_type_low_latency_hr=0x{result.InputMediaTypeLowLatencyHresultOnTransform:X8},output_media_type_low_latency_applied={(result.OutputMediaTypeLowLatencyAppliedOnTransform ? 1 : 0)},output_media_type_low_latency_hr=0x{result.OutputMediaTypeLowLatencyHresultOnTransform:X8},codecapi_available={(result.CodecApiAvailableOnTransform ? 1 : 0)},codecapi_supported={(result.CodecApiSupportedOnTransform ? 1 : 0)},codecapi_is_supported_hr=0x{result.CodecApiIsSupportedHresultOnTransform:X8},codecapi_modifiable={(result.CodecApiModifiableOnTransform ? 1 : 0)},codecapi_is_modifiable_hr=0x{result.CodecApiIsModifiableHresultOnTransform:X8},codecapi_applied={(result.CodecApiLowLatencyAppliedOnTransform ? 1 : 0)},codecapi_set_value_hr=0x{result.CodecApiSetValueHresultOnTransform:X8},transform_attributes_before_profile={Sanitize(result.TransformAttributesSnapshotBeforeProfileOnTransform)},transform_attributes_after_profile={Sanitize(result.TransformAttributesSnapshotAfterProfileOnTransform)},transform_attributes={Sanitize(result.TransformAttributesSnapshotOnTransform)},input_stream_attributes={Sanitize(result.InputStreamAttributesSnapshotOnTransform)},output_stream_attributes={Sanitize(result.OutputStreamAttributesSnapshotOnTransform)},frames={result.FramesReplayed},attempts={result.ProcessOutputAttempts},output={(result.Bitmap is not null ? 1 : 0)},provider_failure={(result.ProviderContractFailure ? 1 : 0)},retrieval_failure={(result.RetrievalContractFailure ? 1 : 0)},need_more_input={(result.NeedMoreInputObserved ? 1 : 0)},success_without_sample={(result.SuccessWithoutSample ? 1 : 0)},output_ready_without_sample={(result.OutputReadyWithoutSample ? 1 : 0)},sample_ready_seen={((result.OutputStatusHresult >= 0 && (result.OutputStatusFlags & MftOutputStatusSampleReady) != 0) ? 1 : 0)},caller_sample={(result.CallerProvidedSample ? 1 : 0)},sample_origin={FormatOutputSampleOrigin(result.SampleOrigin)},stream_change={(result.StreamChangeObserved ? 1 : 0)},input_status_hr=0x{result.InputStatusHresult:X8},input_status={FormatInputStatusFlags(result.InputStatusFlags)},output_status_hr=0x{result.OutputStatusHresult:X8},output_status={FormatOutputStatusFlags(result.OutputStatusFlags)},process_output_hr=0x{result.ProcessOutputHresult:X8},output_dw_status=0x{result.OutputDataBufferStatus:X8},output_flags={FormatOutputStreamFlags(result.OutputFlags)},stage={Sanitize(result.FailureStage)},failure={failure}";
    }


    private readonly record struct BufferedProbeFrame(
        byte[] EncodedBytes,
        bool IsKeyFrame);

    private readonly record struct OutputSubtypeCandidate(
        Guid Subtype,
        OutputSubtypeProbeKind ProbeKind,
        bool IsNativeAdvertisedCandidate);


    private sealed record OutputMatrixCombinationResult(
        OutputContractCombination Combination,
        int TransformId,
        bool OutputTypeConfiguredOnTransform,
        bool OutputTypeVerifiedOnTransform,
        int SetOutputTypeHresult,
        int GetOutputCurrentTypeHresult,
        int OutputStatusAfterConfigurationHresult,
        uint OutputStatusAfterConfigurationFlags,
        Bitmap? Bitmap,
        Exception? Failure,
        string FailureStage,
        int FailureHresult,
        bool ProviderContractFailure,
        bool RetrievalContractFailure,
        bool NeedMoreInputObserved,
        bool SuccessWithoutSample,
        bool OutputReadyWithoutSample,
        int FramesReplayed,
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
        int ProcessOutputHresult)
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

    private sealed record OutputMatrixProbeSuccess(
        Bitmap Bitmap,
        DecoderTransformState TransformState,
        OutputContractCombination Combination,
        DecoderBackendProfileCombination BackendProfile,
        bool UsedDrain);


    private readonly record struct DecoderBackendProfileCombination(
        DecoderBackendKind Backend,
        DecoderAttributeProfileKind AttributeProfile);


}