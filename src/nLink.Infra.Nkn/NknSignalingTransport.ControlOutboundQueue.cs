using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private sealed class ControlOutboundQueue
    {
        private readonly NknSignalingTransport owner;

        public ControlOutboundQueue(NknSignalingTransport owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public static ControlOutboundLane ResolveLane(MsgType messageType, bool isLowPriorityMouseMove = false)
        {
            return messageType switch
            {
                MsgType.ControlInput when isLowPriorityMouseMove => ControlOutboundLane.Low,
                MsgType.ControlDisplayInfo => ControlOutboundLane.High,
                MsgType.ControlRequest or
                    MsgType.ControlResponse or
                    MsgType.ControlStart or
                    MsgType.ControlStop or
                    MsgType.ControlAck or
                    MsgType.ControlStateSnapshot or
                    MsgType.ScreenSharePressureState => ControlOutboundLane.High,
                _ => ControlOutboundLane.High,
            };
        }

        public Task<bool> QueueEnvelopeAsync(
            string destination,
            Envelope envelope,
            ControlOutboundLane lane,
            CancellationToken ct,
            bool isLowPriorityMouseMove = false)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queued = new QueuedControlEnvelope(destination, envelope, completion, ct, isLowPriorityMouseMove);
            var logBootstrapScreenShareControl = TryGetStartupScreenShareControlMetadata(envelope, out var startupScreenShareMetadata);
            List<TaskCompletionSource<bool>>? droppedCompletions = null;
            var shouldStartDrainer = false;
            var lowLaneDroppedCount = 0;
            var highLaneRejected = false;
            var highLaneCoalescedCount = 0;
            var highLaneDroppedCount = 0;

            lock (owner.controlOutboundQueueGate)
            {
                if (lane == ControlOutboundLane.Low)
                {
                    if (isLowPriorityMouseMove)
                    {
                        Interlocked.Increment(ref owner.lowLaneEnqueuedMoves);
                    }

                    if (isLowPriorityMouseMove && owner.queuedLowPriorityMouseMoveNode is not null)
                    {
                        droppedCompletions ??= [];
                        droppedCompletions.Add(owner.queuedLowPriorityMouseMoveNode.Value.Completion);
                        Interlocked.Increment(ref owner.lowLaneDroppedMoves);
                        lowLaneDroppedCount++;
                        owner.queuedLowPriorityMouseMoveNode.Value = queued;
                    }
                    else
                    {
                        while (owner.lowPriorityControlOutboundQueue.Count >= LowPriorityControlLaneCapacity)
                        {
                            var droppedNode = owner.lowPriorityControlOutboundQueue.First;
                            if (droppedNode is null)
                            {
                                break;
                            }

                            owner.lowPriorityControlOutboundQueue.RemoveFirst();
                            if (ReferenceEquals(droppedNode, owner.queuedLowPriorityMouseMoveNode))
                            {
                                owner.queuedLowPriorityMouseMoveNode = null;
                            }

                            droppedCompletions ??= [];
                            droppedCompletions.Add(droppedNode.Value.Completion);
                            if (droppedNode.Value.IsLowPriorityMouseMove)
                            {
                                Interlocked.Increment(ref owner.lowLaneDroppedMoves);
                            }

                            lowLaneDroppedCount++;
                        }

                        var inserted = owner.lowPriorityControlOutboundQueue.AddLast(queued);
                        if (isLowPriorityMouseMove)
                        {
                            owner.queuedLowPriorityMouseMoveNode = inserted;
                        }
                    }

                    if (owner.lowPriorityControlOutboundQueue.Count > owner.lowLaneMaxDepthSeen)
                    {
                        owner.lowLaneMaxDepthSeen = owner.lowPriorityControlOutboundQueue.Count;
                    }
                }
                else
                {
                    if (TryCoalesceHighPriorityEnvelopeWhenFull(envelope, queued, ref droppedCompletions, out highLaneCoalescedCount))
                    {
                        Interlocked.Increment(ref owner.highPriorityControlQueueOverflowCount);
                        NknRuntimeDiagnostics.IncrementHighPriorityControlQueueOverflows();
                        Interlocked.Add(ref owner.highPriorityControlCoalescedCount, highLaneCoalescedCount);
                        NknRuntimeDiagnostics.AddHighPriorityControlCoalesced(highLaneCoalescedCount);
                    }
                    else if (owner.highPriorityControlOutboundQueue.Count >= HighPriorityControlLaneCapacity)
                    {
                        Interlocked.Increment(ref owner.highPriorityControlQueueOverflowCount);
                        NknRuntimeDiagnostics.IncrementHighPriorityControlQueueOverflows();
                        if (envelope.Type == MsgType.ControlStop)
                        {
                            var droppedNode = FindDroppableHighPriorityNodeForStop();
                            if (droppedNode is not null)
                            {
                                owner.highPriorityControlOutboundQueue.Remove(droppedNode);
                                droppedCompletions ??= [];
                                droppedCompletions.Add(droppedNode.Value.Completion);
                                highLaneDroppedCount++;
                                Interlocked.Increment(ref owner.highPriorityControlDroppedForStopCount);
                                NknRuntimeDiagnostics.AddHighPriorityControlDroppedForStop(1);
                                owner.highPriorityControlOutboundQueue.AddFirst(queued);
                            }
                            else
                            {
                                highLaneRejected = true;
                                droppedCompletions ??= [];
                                droppedCompletions.Add(queued.Completion);
                            }
                        }
                        else
                        {
                            highLaneRejected = true;
                            droppedCompletions ??= [];
                            droppedCompletions.Add(queued.Completion);
                        }
                    }
                    else
                    {
                        owner.highPriorityControlOutboundQueue.AddLast(queued);
                    }
                }

                if (!owner.controlOutboundDrainerActive &&
                    (owner.highPriorityControlOutboundQueue.Count > 0 || owner.lowPriorityControlOutboundQueue.Count > 0))
                {
                    owner.controlOutboundDrainerActive = true;
                    shouldStartDrainer = true;
                }
            }

            if (logBootstrapScreenShareControl)
            {
                LogBootstrapScreenShareControlStage("enqueued", envelope, startupScreenShareMetadata!, queuedResult: null);
            }

            if (droppedCompletions is not null)
            {
                foreach (var dropped in droppedCompletions)
                {
                    dropped.TrySetResult(false);
                }

                if (lowLaneDroppedCount > 0)
                {
                    Log($"Control outbound low lane dropped stale message(s) (count={lowLaneDroppedCount})");
                }
            }

            if (highLaneRejected)
            {
                Interlocked.Increment(ref owner.highPriorityControlRejectedCount);
                NknRuntimeDiagnostics.IncrementHighPriorityControlRejected();
                Log($"Control outbound high lane rejected message at capacity (type={envelope.Type}, cap={HighPriorityControlLaneCapacity})");
            }
            else if (highLaneCoalescedCount > 0)
            {
                Log($"Control outbound high lane coalesced supersedable message(s) (type={envelope.Type}, count={highLaneCoalescedCount}, cap={HighPriorityControlLaneCapacity})");
            }
            else if (highLaneDroppedCount > 0)
            {
                Log($"Control outbound high lane dropped queued message(s) to prioritize stop (count={highLaneDroppedCount}, cap={HighPriorityControlLaneCapacity})");
            }

            if (shouldStartDrainer)
            {
                _ = Task.Run(DrainAsync);
            }

            return completion.Task;
        }

        public void FlushLowPriority(string reason)
        {
            List<TaskCompletionSource<bool>>? droppedCompletions = null;

            lock (owner.controlOutboundQueueGate)
            {
                while (owner.lowPriorityControlOutboundQueue.First is not null)
                {
                    var droppedNode = owner.lowPriorityControlOutboundQueue.First;
                    owner.lowPriorityControlOutboundQueue.RemoveFirst();
                    droppedCompletions ??= [];
                    droppedCompletions.Add(droppedNode.Value.Completion);
                }

                owner.queuedLowPriorityMouseMoveNode = null;
            }

            if (droppedCompletions is null)
            {
                return;
            }

            foreach (var dropped in droppedCompletions)
            {
                dropped.TrySetResult(false);
            }

            Log($"Control outbound low lane flushed (reason={reason}, dropped={droppedCompletions.Count})");
        }

        public void FlushAll(string reason)
        {
            List<TaskCompletionSource<bool>>? droppedCompletions = null;

            lock (owner.controlOutboundQueueGate)
            {
                while (owner.highPriorityControlOutboundQueue.First is not null)
                {
                    var droppedNode = owner.highPriorityControlOutboundQueue.First;
                    owner.highPriorityControlOutboundQueue.RemoveFirst();
                    droppedCompletions ??= [];
                    droppedCompletions.Add(droppedNode.Value.Completion);
                }

                while (owner.lowPriorityControlOutboundQueue.First is not null)
                {
                    var droppedNode = owner.lowPriorityControlOutboundQueue.First;
                    owner.lowPriorityControlOutboundQueue.RemoveFirst();
                    droppedCompletions ??= [];
                    droppedCompletions.Add(droppedNode.Value.Completion);
                }

                owner.queuedLowPriorityMouseMoveNode = null;
            }

            if (droppedCompletions is null)
            {
                return;
            }

            foreach (var dropped in droppedCompletions)
            {
                dropped.TrySetCanceled();
            }

            Log($"Control outbound lanes flushed (reason={reason}, dropped={droppedCompletions.Count})");
        }

        private bool TryCoalesceHighPriorityEnvelopeWhenFull(
            Envelope envelope,
            QueuedControlEnvelope queued,
            ref List<TaskCompletionSource<bool>>? droppedCompletions,
            out int replacedCount)
        {
            replacedCount = 0;
            if (!IsSupersedableHighPriorityType(envelope.Type) ||
                owner.highPriorityControlOutboundQueue.Count < HighPriorityControlLaneCapacity)
            {
                return false;
            }

            var existingNode = owner.highPriorityControlOutboundQueue.Last;
            while (existingNode is not null)
            {
                if (existingNode.Value.Envelope.Type == envelope.Type)
                {
                    droppedCompletions ??= [];
                    droppedCompletions.Add(existingNode.Value.Completion);
                    existingNode.Value = queued;
                    replacedCount = 1;
                    return true;
                }

                existingNode = existingNode.Previous;
            }

            return false;
        }

        private LinkedListNode<QueuedControlEnvelope>? FindDroppableHighPriorityNodeForStop()
        {
            var droppedNode = owner.highPriorityControlOutboundQueue.First;
            while (droppedNode is not null && droppedNode.Value.Envelope.Type == MsgType.ControlStop)
            {
                droppedNode = droppedNode.Next;
            }

            return droppedNode ?? owner.highPriorityControlOutboundQueue.First;
        }

        private static bool IsSupersedableHighPriorityType(MsgType type)
        {
            return type is MsgType.ControlDisplayInfo or MsgType.ControlStateSnapshot;
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                QueuedControlEnvelope queued;
                lock (owner.controlOutboundQueueGate)
                {
                    LinkedListNode<QueuedControlEnvelope>? nextNode = null;
                    if (owner.highPriorityControlOutboundQueue.First is not null)
                    {
                        nextNode = owner.highPriorityControlOutboundQueue.First;
                        owner.highPriorityControlOutboundQueue.RemoveFirst();
                    }
                    else if (owner.lowPriorityControlOutboundQueue.First is not null)
                    {
                        nextNode = owner.lowPriorityControlOutboundQueue.First;
                        owner.lowPriorityControlOutboundQueue.RemoveFirst();
                        if (ReferenceEquals(nextNode, owner.queuedLowPriorityMouseMoveNode))
                        {
                            owner.queuedLowPriorityMouseMoveNode = null;
                        }
                    }
                    else
                    {
                        owner.controlOutboundDrainerActive = false;
                        return;
                    }

                    queued = nextNode.Value;
                }

                if (queued.CancellationToken.IsCancellationRequested)
                {
                    if (TryGetStartupScreenShareControlMetadata(queued.Envelope, out var canceledMetadata))
                    {
                        LogBootstrapScreenShareControlStage("canceled_before_send", queued.Envelope, canceledMetadata!, queuedResult: null);
                    }

                    queued.Completion.TrySetCanceled(queued.CancellationToken);
                    continue;
                }

                var logBootstrapScreenShareControl = TryGetStartupScreenShareControlMetadata(queued.Envelope, out var startupScreenShareMetadata);
                if (logBootstrapScreenShareControl)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_control_bootstrap_lane_stage; stage=dequeued; stream_epoch={startupScreenShareMetadata!.StreamEpoch}; frame_id={startupScreenShareMetadata.FrameId}; is_keyframe={(startupScreenShareMetadata.IsKeyFrame ? 1 : 0)}; msg_id={queued.Envelope.MessageId}; queued_result=-1; gate_count={owner.outboundSendGate.CurrentCount}; gate_holder={owner.outboundSendGateOwnerForDiagnostics ?? "(none)"}");
                }

                try
                {
                    await owner.SendEnvelopeAsync(queued.Destination, queued.Envelope, queued.CancellationToken).ConfigureAwait(false);
                    if (logBootstrapScreenShareControl)
                    {
                        LogBootstrapScreenShareControlStage("sent", queued.Envelope, startupScreenShareMetadata!, queuedResult: true);
                    }

                    queued.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException) when (queued.CancellationToken.IsCancellationRequested)
                {
                    if (logBootstrapScreenShareControl)
                    {
                        LogBootstrapScreenShareControlStage("canceled_during_send", queued.Envelope, startupScreenShareMetadata!, queuedResult: null);
                    }

                    queued.Completion.TrySetCanceled(queued.CancellationToken);
                }
                catch (Exception ex)
                {
                    if (logBootstrapScreenShareControl)
                    {
                        LocalOperationalLog.Info(
                            "ScreenShareTransport",
                            $"event=screenshare_control_bootstrap_lane_stage; stage=send_failed; stream_epoch={startupScreenShareMetadata!.StreamEpoch}; frame_id={startupScreenShareMetadata.FrameId}; is_keyframe={(startupScreenShareMetadata.IsKeyFrame ? 1 : 0)}; msg_id={queued.Envelope.MessageId}; ex={ex.GetType().Name}");
                    }

                    queued.Completion.TrySetException(ex);
                }
            }
        }

        private bool TryGetStartupScreenShareControlMetadata(
            Envelope envelope,
            out QueuedScreenShareEnvelopeMetadata? metadata)
        {
            metadata = null;
            if (envelope.Type != MsgType.ScreenShareFrame)
            {
                return false;
            }

            metadata = owner.TryCreateQueuedScreenShareEnvelopeMetadata(EnvelopeCodec.Serialize(envelope), recoverySendRole: null, recoveryBurstToken: 0);
            return metadata is not null && metadata.FrameId <= ScreenShareControlBootstrapMaxFrameId;
        }

        private static void LogBootstrapScreenShareControlStage(
            string stage,
            Envelope envelope,
            QueuedScreenShareEnvelopeMetadata metadata,
            bool? queuedResult)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_control_bootstrap_lane_stage; stage={stage}; stream_epoch={metadata.StreamEpoch}; frame_id={metadata.FrameId}; is_keyframe={(metadata.IsKeyFrame ? 1 : 0)}; msg_id={envelope.MessageId}; queued_result={(queuedResult.HasValue ? (queuedResult.Value ? 1 : 0) : -1)}");
        }

        private static void Log(string message) => NknSignalingTransport.Log(message);
    }
}
