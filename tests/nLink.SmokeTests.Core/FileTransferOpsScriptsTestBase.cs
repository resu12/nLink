using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

public abstract class FileTransferOpsScriptsTestBase
{
    protected static readonly TimeSpan ScriptProcessTimeout = TimeSpan.FromMinutes(5);
    protected static readonly TimeSpan ScriptProcessOutputDrainTimeout = TimeSpan.FromSeconds(5);

    protected static readonly string[] ExpectedFileTransferOpsModes =
    [
        "AnalyzeRetained",
        "LocalFast",
        "LocalImpaired",
        "LocalMixed",
        "NknFast",
        "NknMixed",
        "SupportCapture",
        "Test"
    ];

    protected static readonly string[] ExpectedExternalTopologyProfiles =
    [
        "BulkFanout12",
        "BulkFanout4Legacy",
        "BulkFanout8",
        "BulkSingle1",
        "Default",
        "DefaultKeepAlive",
        "MediaFanout12",
        "MediaFanout8",
        "PinnedMainnetRpc",
        "PinnedSeedHttps"
    ];

    protected static readonly string[] ExpectedPayloadEfficiencyProfiles =
    [
        "Auto",
        "Current",
        "LargeSingle48KiB",
        "Packed3x20KiB",
        "Packed3x21KiB"
    ];

    protected static readonly string[] ExpectedTopLevelParameters =
    [
        "Mode",
        "ExternalTopologyProfile",
        "LogDir",
        "LogPath",
        "ArtifactDir",
        "TransferId",
        "TailMinutes",
        "IncludeRawSlices",
        "FailOnGate",
        "Configuration",
        "PayloadSizes",
        "PayloadEfficiencyProfile",
        "Cycles",
        "Seed",
        "Direction",
        "ImpairmentProfile",
        "CycleTimeoutSeconds",
        "ProgressTimeoutSeconds",
        "NoBuild",
        "ExePath",
        "Build",
        "TimeoutSeconds",
        "SafeBaselineArtifactDir",
        "StrongBaselineArtifactDir",
        "LiveRouteProofMode"
    ];

    protected static readonly string[] ImplementationFiles =
    [
        "tools/FileTransferOps/AnalyzerOrchestration.ps1",
        "tools/FileTransferSoak/LogParsing.ps1",
        "tools/FileTransferSoak/SoakSummaryExtraction.ps1",
        "tools/FileTransferSoak/StabilizationGates.ps1",
        "tools/FileTransferSoak/ArtifactWriters.ps1",
        "tools/FileTransferSoak/BaselineComparison.ps1",
        "tools/PreRelease-Check.ps1",
        "tools/Run-FileTransferNknSoak.ps1",
        "tools/Run-FileTransferRouteAcceptance.ps1",
        "tools/Run-FileTransferTunaGuiSmoke.ps1",
        "tools/Run-FileTransferReceiveStallMatrix.ps1",
        "tools/Run-NknBridgeReceiveProbe.ps1"
    ];

    protected static readonly string[] RequiredArtifactFiles =
    [
        "filetransfer-operator-verdict.txt",
        "transfer-terminal-summary.txt",
        "throughput-summary.txt",
        "throughput-decomposition-summary.txt",
        "payload-efficiency-summary.txt",
        "protocol-shape-summary.txt",
        "filetransfer-route-consistency-summary.txt",
        "repair-reorder-summary.txt",
        "transport-budget-summary.txt",
        "bridge-config-summary.txt",
        "bridge-bulk-summary.txt",
        "coexistence-summary.txt",
        "external-transport-health-summary.txt",
        "stability-gates-summary.txt",
        "phase0-invariant-ledger.txt",
        "phase0-invariant-ledger.json",
        "filetransfer-invariant-summary.txt",
        "filetransfer-invariant-summary.json",
        "filetransfer-efficiency-summary.txt",
        "filetransfer-efficiency-summary.json",
        "v4-promotion-decision.txt",
        "v4-promotion-decision.json"
    ];

    protected static readonly string[] RequiredLiveNknArtifactFiles =
    [
        "filetransfer-live-nkn-summary.txt",
        "filetransfer-live-nkn-summary.json",
        "filetransfer-live-nkn-cycles.jsonl",
        "filetransfer-retained-log-slice.log",
        "baseline-comparison.txt"
    ];

    protected static readonly string[] RequiredRouteAcceptanceSubdirectories =
    [
        "regular-nkn-64mb-quick",
        "regular-nkn-128mb-target",
        "tuna-128mb-no-fault",
        "tuna-128mb-fallback"
    ];

    protected static readonly string[] RequiredPhase4RouteAcceptanceSubdirectories =
    [
        "regular-nkn-v4-64mb",
        "active-tuna-v4-64mb",
        "live-switch-off-helpee-64mb",
        "live-switch-off-helper-64mb",
        "live-multi-toggle-off-on-off-64mb",
        "regular-v4-live-activation-off-on-off-256mb",
        "second-transfer-after-reactivation"
    ];

    protected static readonly string[] RequiredPhase5RouteAcceptanceSubdirectories =
    [
        "regular-nkn-v4-64mb",
        "active-tuna-v4-64mb",
        "live-switch-off-helpee-64mb",
        "live-switch-off-helper-64mb",
        "regular-v4-live-activation-off-on-off-256mb",
        "second-transfer-after-reactivation"
    ];


    public static IEnumerable<object[]> ThroughputLimiterFixtures()
    {
        yield return
        [
            "receiver_gap",
            "receiver_gap_stalled",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_receiver_gap; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=8000000; raw_bytes_per_second=4000000; chunk_frames_sent=0; batch_frames_sent=80; chunk_count_sent=160; chunks_accepted_for_transport=400; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=260; remote_granted_window_bytes=3932160; sent_cache_chunk_count=300; sent_cache_bytes=7372800; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_receiver_gap; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=8000000; raw_bytes_received_per_second=4000000; contiguous_bytes_committed=1000000; contiguous_bytes_committed_per_second=500000; pending_chunk_count=180; pending_bytes=4423680; next_chunk_index=100; highest_received_chunk_index=280; late_arrival_distance=180; oldest_gap_age_ms=2200; granted_until_chunk_index_exclusive=260; granted_window_bytes=3932160; write_batch_count=1; write_batch_bytes=1000000; write_duration_ms=10",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_receiver_gap; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=100; highest_received_chunk_index=280; late_arrival_distance=180; stall_duration_ms=2200; pending_bytes=4423680; granted_window_bytes=3932160",
                "event=nkn_bridge_bulk_send_summary; frames_sent=80; payload_bytes_sent=8000000; payload_bytes_per_second=4000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "bridge_bulk",
            "bridge_bulk_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_bridge_bulk; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4000000; raw_bytes_per_second=2000000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=80; chunks_accepted_for_transport=200; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=260; remote_granted_window_bytes=3932160; sent_cache_chunk_count=100; sent_cache_bytes=2457600; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_bridge_bulk; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2000000; raw_bytes_received_per_second=1000000; contiguous_bytes_committed=2000000; contiguous_bytes_committed_per_second=1000000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=200; highest_received_chunk_index=200; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=260; granted_window_bytes=1474560; write_batch_count=2; write_batch_bytes=2000000; write_duration_ms=10",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=4000000; payload_bytes_per_second=2000000; send_failures=0; queue_clears=0; queue_depth=128; queued_bytes=5242880; oldest_queued_age_ms=500; in_flight=1; send_p95_ms=400; send_max_ms=650; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sender_window",
            "sender_window_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sender_window; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=22; chunk_count_sent=44; chunks_accepted_for_transport=44; remote_next_expected_chunk_index=20; remote_granted_until_chunk_index_exclusive=52; remote_granted_window_bytes=786432; sent_cache_chunk_count=24; sent_cache_bytes=589824; send_wait_count=3; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sender_window; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1048576; raw_bytes_received_per_second=524288; contiguous_bytes_committed=1048576; contiguous_bytes_committed_per_second=524288; pending_chunk_count=0; pending_bytes=0; next_chunk_index=44; highest_received_chunk_index=44; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=52; granted_window_bytes=196608; write_batch_count=2; write_batch_bytes=1048576; write_duration_ms=5",
                "event=nkn_bridge_bulk_send_summary; frames_sent=22; payload_bytes_sent=1048576; payload_bytes_per_second=524288; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sender_serialized",
            "sender_transport_serialized",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sender_serialized; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=2400000; raw_bytes_per_second=1200000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=120; chunks_accepted_for_transport=360; remote_next_expected_chunk_index=240; remote_granted_until_chunk_index_exclusive=520; remote_granted_window_bytes=6021120; sent_cache_chunk_count=120; sent_cache_bytes=2580480; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sender_serialized; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2400000; raw_bytes_received_per_second=1200000; contiguous_bytes_committed=2400000; contiguous_bytes_committed_per_second=1200000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=360; highest_received_chunk_index=360; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=520; granted_window_bytes=3440640; write_batch_count=2; write_batch_bytes=2400000; write_duration_ms=5",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=2400000; payload_bytes_per_second=1200000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=1; configured_concurrency=4; effective_concurrency=4; send_p95_ms=3; send_max_ms=5; worker_utilization_percent=12; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "nkn_delivery",
            "nkn_delivery_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_nkn_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=8000000; raw_bytes_per_second=4000000; chunk_frames_sent=0; batch_frames_sent=80; chunk_count_sent=160; chunks_accepted_for_transport=400; remote_next_expected_chunk_index=240; remote_granted_until_chunk_index_exclusive=400; remote_granted_window_bytes=3932160; sent_cache_chunk_count=160; sent_cache_bytes=3932160; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_nkn_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2000000; raw_bytes_received_per_second=1000000; contiguous_bytes_committed=2000000; contiguous_bytes_committed_per_second=1000000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=320; highest_received_chunk_index=320; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=400; granted_window_bytes=1966080; write_batch_count=2; write_batch_bytes=2000000; write_duration_ms=10",
                "event=nkn_bridge_bulk_send_summary; frames_sent=80; payload_bytes_sent=8000000; payload_bytes_per_second=4000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "grant_feedback",
            "adaptive_window_underprovisioned",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_grant_feedback; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_grant_feedback; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=696; remote_granted_window_bytes=4214784; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_pipeline_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; configured_depth=8; effective_depth=8; in_flight_frames=0; in_flight_bytes=0; in_flight_frames_max=8; in_flight_bytes_max=516096; scheduled_frames=28; completed_frames=28; failed_frames=0; fifo_wait_ms=10; fifo_wait_max_ms=15; accepted_progress_lag_bytes_max=516096; pending_bytes_limit=2097152",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=700; highest_received_chunk_index=700; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=896; granted_window_bytes=4214784; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=0; sparse_gap_count=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_feedback; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=8388608; effective_granted_window_bytes=4214784; current_credit_chunks=196; desired_credit_chunks=392; low_watermark_credit_chunks=353; credit_remaining_chunks=196; credit_desired_chunks=392; credit_remaining_bytes=4214784; credit_desired_bytes=8429568; granted_until_chunk_index_exclusive=896; target_granted_until_chunk_index_exclusive=1092; grant_base_chunk_index=700; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=700; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=no_sparse_ahead; next_chunk_index=700; highest_received_chunk_index=700; late_arrival_distance=0; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_grant_feedback; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=bulk",
                "event=nkn_bridge_control_receive_degraded; connect_key=test; consecutive_control_zero_receive_windows=2; active_file_transfer_sessions=1; frames_sent_since_last=12; control_messages_received_since_last=0; bulk_messages_received_since_last=14; total_messages_received_since_last=14; control_last_received_age_ms=32000; bulk_last_received_age_ms=100; sample_window_ms=2000",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sticky_limited",
            "sticky_limited_without_pressure",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_sticky_limited; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_sticky_limited; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=473088; raw_bytes_per_second=236544; chunk_frames_sent=0; batch_frames_sent=8; chunk_count_sent=22; chunks_accepted_for_transport=1560; remote_next_expected_chunk_index=1529; remote_granted_until_chunk_index_exclusive=1554; remote_granted_window_bytes=537600; sent_cache_chunk_count=31; sent_cache_bytes=666624; send_wait_count=21; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=8; chunk_count_prepared=22; raw_bytes_prepared=473088; read_duration_ms=0; batch_prepare_duration_ms=0; send_async_schedule_duration_ms=5; inter_schedule_gap_p95_ms=5227; inter_schedule_gap_max_ms=5227; credit_wait_duration_ms=6918; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2472960; raw_bytes_received_per_second=1236480; contiguous_bytes_committed=2408448; contiguous_bytes_committed_per_second=1204224; pending_chunk_count=0; pending_bytes=0; next_chunk_index=1470; highest_received_chunk_index=1478; late_arrival_distance=8; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=1538; granted_window_bytes=1462272; write_batch_count=39; write_batch_bytes=2472960; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=1236480; sparse_written_ahead_bytes=64512; sparse_gap_count=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sticky_limited; session_id=sess_a; reason=ack_only; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=537600; effective_granted_window_bytes=1462272; current_credit_chunks=68; desired_credit_chunks=68; low_watermark_credit_chunks=62; credit_remaining_chunks=68; credit_desired_chunks=68; credit_remaining_bytes=1462272; credit_desired_bytes=1462272; granted_until_chunk_index_exclusive=1538; target_granted_until_chunk_index_exclusive=1538; grant_base_chunk_index=1470; grant_base_reason=sparse_ahead; sparse_ahead_bytes=129024; credit_base_chunk_index=1470; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=2623488; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); proactive_repair_pressure_state=(none); proactive_repair_age_ms=4598; same_frontier_unfilled_ms=0; limited_recovery_clean_ms=2200; limited_recovery_block_reason=(none); fixed_file_only_window_active=0; fixed_file_only_window_bytes=0; next_chunk_index=1464; highest_received_chunk_index=1469; late_arrival_distance=5; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=8; frames_enqueued=8; payload_bytes_sent=520000; payload_bytes_per_second=260000; payload_bytes_enqueued=520000; payload_bytes_enqueued_per_second=260000; inter_enqueue_gap_p95_ms=5227; inter_enqueue_gap_max_ms=5227; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=16; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=8; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sparse_reorder_credit",
            "sparse_reorder_credit_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=4214784; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_pipeline_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; configured_depth=8; effective_depth=8; in_flight_frames=0; in_flight_bytes=0; in_flight_frames_max=8; in_flight_bytes_max=516096; scheduled_frames=28; completed_frames=28; failed_frames=0; fifo_wait_ms=10; fifo_wait_max_ms=15; accepted_progress_lag_bytes_max=516096; pending_bytes_limit=2097152",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=1308; granted_until_chunk_index_exclusive=896; granted_window_bytes=4214784; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; policy=SparseTolerant; decision=soft_limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=379; soft_reorder_threshold=192; soft_gap_stall_ms=1000; sparse_ahead_gap_stall_limit_ms=750; gap_stall_age_ms=1308; current_profile=healthy_file_only_soft_limited; target_window_bytes=1048576; soft_limit_target_bytes=1048576; granted_window_bytes=4214784; next_chunk_index=500; highest_received_chunk_index=879; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_file_only_soft_limited; target_window_bytes=1048576; effective_granted_window_bytes=4214784; current_credit_chunks=196; desired_credit_chunks=48; low_watermark_credit_chunks=44; credit_remaining_chunks=196; credit_desired_chunks=48; credit_remaining_bytes=4214784; credit_desired_bytes=1032192; granted_until_chunk_index_exclusive=896; target_granted_until_chunk_index_exclusive=548; target_base_chunk_index=500; target_base_reason=gap_stall; grant_base_chunk_index=500; grant_base_reason=gap_stall; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=0; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=gap_stall; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_sparse_reorder_credit; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=bulk",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "dominant_sparse_credit",
            "sender_feedback_loop_blocked",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1272; remote_granted_window_bytes=16602112; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_sender_grant_apply_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; async_sender_pump=1; previous_granted_until_chunk_index_exclusive=1050; new_granted_until_chunk_index_exclusive=1272; previous_accepted_chunk_index=900; accepted_chunk_index=900; remote_next_expected_chunk_index=500; available_credit_chunks_before=150; available_credit_chunks_after=372; available_credit_bytes_after=7999488; credit_wait_active_ms=1300; send_pump_signaled=1; chunks_schedulable=372; in_flight_frames=0; in_flight_bytes=0",
                "event=filetransfer_v4_sender_credit_stall_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; wait_reason=no_credit; credit_wait_active_ms=1300; accepted_chunk_index=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=900; available_credit_chunks=0; available_credit_bytes=0; last_grant_age_ms=900; in_flight_frames=0; in_flight_bytes=0; pending_repair_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1272; granted_window_bytes=8429568; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; policy=SparseTolerant; decision=tolerated; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=379; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=700; current_profile=healthy_expanded; target_window_bytes=16777216; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=879; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; target_base_chunk_index=880; target_base_reason=sparse_ahead; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_data_frame_dispatched; transfer_id=transfer_dominant_sparse_credit; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=bulk",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "grant_generation",
            "grant_generation_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_grant_generation; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_grant_generation; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_grant_generation; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1280; remote_granted_window_bytes=16773120; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_grant_generation; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_grant_generation; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1280; granted_window_bytes=16773120; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_grant_generation; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1280; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "grant_delivery",
            "grant_delivery_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_grant_delivery; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_grant_delivery; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1280; remote_granted_window_bytes=16773120; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_sender_grant_apply_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; async_sender_pump=1; previous_granted_until_chunk_index_exclusive=1050; new_granted_until_chunk_index_exclusive=1280; previous_accepted_chunk_index=900; accepted_chunk_index=900; remote_next_expected_chunk_index=500; available_credit_chunks_before=150; available_credit_chunks_after=380; available_credit_bytes_after=8171520; credit_wait_active_ms=100; send_pump_signaled=1; chunks_schedulable=380; in_flight_frames=0; in_flight_bytes=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1280; granted_window_bytes=16773120; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1",
                "event=filetransfer_v4_receiver_grant_decision_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; should_grant=1; should_ack_only=0; force_grant=0; clamp_grant=0; target_window_changed=0; sparse_credit_topup=1; low_watermark_reached=1; ack_coalesce_blocked=0; same_grant_target=0; target_window_bytes=16777216; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1280; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); ack_debt_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_grant_delivery; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=8429568; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1660; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=8171520; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "file_only_sparse_window_capacity",
            "file_only_sparse_window_capacity_proven",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4800000; raw_bytes_per_second=2400000; chunk_frames_sent=0; batch_frames_sent=75; chunk_count_sent=225; chunks_accepted_for_transport=1200; remote_next_expected_chunk_index=760; remote_granted_until_chunk_index_exclusive=1532; remote_granted_window_bytes=16602112; sent_cache_chunk_count=440; sent_cache_bytes=9461760; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=75; chunk_count_prepared=225; raw_bytes_prepared=4800000; read_duration_ms=4; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=5; inter_schedule_gap_p95_ms=10; inter_schedule_gap_max_ms=20; credit_wait_duration_ms=100; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=4800000; raw_bytes_received_per_second=2400000; contiguous_bytes_committed=4800000; contiguous_bytes_committed_per_second=2400000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=1200; highest_received_chunk_index=1200; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=1980; granted_window_bytes=16773120; write_batch_count=4; write_batch_bytes=4800000; write_duration_ms=8; sparse_mode=1; sparse_write_bytes_per_second=2400000; sparse_written_ahead_bytes=0; sparse_gap_count=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_file_only_sparse_window_capacity; session_id=sess_a; reason=target_changed; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=16773120; current_credit_chunks=300; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=300; credit_desired_chunks=780; credit_remaining_bytes=6451200; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1980; target_granted_until_chunk_index_exclusive=1980; target_base_chunk_index=1200; target_base_reason=sparse_ahead; grant_base_chunk_index=1200; grant_base_reason=sparse_ahead; sparse_ahead_bytes=0; credit_base_chunk_index=1200; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=262144; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); limited_recovery_clean_ms=0; limited_recovery_block_reason=(none); fixed_file_only_window_active=0; fixed_file_only_window_bytes=0; next_chunk_index=1200; highest_received_chunk_index=1200; late_arrival_distance=0; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=75; frames_enqueued=75; payload_bytes_sent=4900000; payload_bytes_per_second=2450000; payload_bytes_enqueued=4900000; payload_bytes_enqueued_per_second=2450000; inter_enqueue_gap_p95_ms=10; inter_enqueue_gap_max_ms=20; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=4; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=55; worker_idle_slot_samples=20; worker_saturation_percent=20; drain_wake_count=75; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "sparse_frontier_gap_unrepaired",
            "sparse_frontier_gap_unrepaired_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=525; remote_granted_window_bytes=537600; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=0",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=0; contiguous_bytes_committed_per_second=0; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=833; late_arrival_distance=333; oldest_gap_age_ms=3571; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=0; write_batch_bytes=0; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=5698560; sparse_gap_count=6",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=500; highest_received_chunk_index=833; late_arrival_distance=333; stall_duration_ms=3571; pending_bytes=0; granted_window_bytes=537600; sparse_mode=1; sparse_written_ahead_bytes=5698560; sparse_gap_count=6",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=0; repair_pressure=0; repeated_proactive_repair=0; timeout_streak=0; late_arrival_distance=333; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=1500; gap_stall_age_ms=3571; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=537600; next_chunk_index=500; highest_received_chunk_index=833; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sparse_frontier_gap_unrepaired; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=gap_stall; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=gap_stall; next_chunk_index=500; highest_received_chunk_index=833; late_arrival_distance=333; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "nkn_bulk_underutilized",
            "nkn_bulk_underutilized",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=2763264; raw_bytes_per_second=1381632; chunk_frames_sent=0; batch_frames_sent=43; chunk_count_sent=129; chunks_accepted_for_transport=512; remote_next_expected_chunk_index=320; remote_granted_until_chunk_index_exclusive=516; remote_granted_window_bytes=4214784; sent_cache_chunk_count=180; sent_cache_bytes=3935232; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_sender_pipeline_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; configured_depth=4; effective_depth=4; in_flight_frames=0; in_flight_bytes=0; in_flight_frames_max=4; in_flight_bytes_max=258048; scheduled_frames=43; completed_frames=43; failed_frames=0; fifo_wait_ms=20; fifo_wait_max_ms=24; accepted_progress_lag_bytes_max=258048; pending_bytes_limit=1048576",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=43; chunk_count_prepared=129; raw_bytes_prepared=2763264; read_duration_ms=10; batch_prepare_duration_ms=20; send_async_schedule_duration_ms=8; inter_schedule_gap_p95_ms=60; inter_schedule_gap_max_ms=90; credit_wait_duration_ms=0; pipeline_slot_wait_duration_ms=20; effective_depth=4; pending_bytes=0; pending_bytes_limit=1048576; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_nkn_bulk_underutilized; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2763264; raw_bytes_received_per_second=1381632; contiguous_bytes_committed=2763264; contiguous_bytes_committed_per_second=1381632; pending_chunk_count=0; pending_bytes=0; next_chunk_index=512; highest_received_chunk_index=512; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=516; granted_window_bytes=86016; write_batch_count=3; write_batch_bytes=2763264; write_duration_ms=10",
                "event=nkn_bridge_bulk_send_summary; frames_sent=43; frames_enqueued=43; payload_bytes_sent=2824471; payload_bytes_per_second=1412236; payload_bytes_enqueued=2824471; payload_bytes_enqueued_per_second=1412236; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=2; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=20; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=43; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "disk_write",
            "disk_write_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_disk_write; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4000000; raw_bytes_per_second=2000000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=80; chunks_accepted_for_transport=240; remote_next_expected_chunk_index=160; remote_granted_until_chunk_index_exclusive=320; remote_granted_window_bytes=3932160; sent_cache_chunk_count=80; sent_cache_bytes=1966080; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_disk_write; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=4000000; raw_bytes_received_per_second=2000000; contiguous_bytes_committed=4000000; contiguous_bytes_committed_per_second=2000000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=240; highest_received_chunk_index=240; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=320; granted_window_bytes=1966080; write_batch_count=2; write_batch_bytes=4000000; write_duration_ms=650",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=4000000; payload_bytes_per_second=2000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "repair_timeout",
            "repair_or_timeout_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_repair_timeout; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=4000000; raw_bytes_per_second=2000000; chunk_frames_sent=0; batch_frames_sent=40; chunk_count_sent=80; chunks_accepted_for_transport=240; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=260; remote_granted_window_bytes=3932160; sent_cache_chunk_count=140; sent_cache_bytes=3440640; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_repair_timeout; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1000000; raw_bytes_received_per_second=500000; contiguous_bytes_committed=1000000; contiguous_bytes_committed_per_second=500000; pending_chunk_count=40; pending_bytes=983040; next_chunk_index=100; highest_received_chunk_index=140; late_arrival_distance=40; oldest_gap_age_ms=500; granted_until_chunk_index_exclusive=260; granted_window_bytes=3932160; write_batch_count=1; write_batch_bytes=1000000; write_duration_ms=10",
                "event=filetransfer_request_timeout_detected; transfer_id=transfer_repair_timeout; session_id=sess_a; next_chunk_index=100; outstanding_count=0; timeout_ms=6000; reason=v4_timeout",
                "event=nkn_bridge_bulk_send_summary; frames_sent=40; payload_bytes_sent=4000000; payload_bytes_per_second=2000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "frontier_gap_repair",
            "frontier_repair_request_not_served",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=16; chunk_count_sent=48; chunks_accepted_for_transport=160; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=148; remote_granted_window_bytes=1032192; sent_cache_chunk_count=60; sent_cache_bytes=1290240; send_wait_count=2; repair_send_count=1",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=900000; raw_bytes_received_per_second=450000; contiguous_bytes_committed=0; contiguous_bytes_committed_per_second=0; pending_chunk_count=0; pending_bytes=0; next_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; oldest_gap_age_ms=1500; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; write_batch_count=0; write_batch_bytes=0; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=450000; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; stall_duration_ms=1500; pending_bytes=0; granted_window_bytes=1032192; sparse_mode=1; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_frontier_gap_repair; session_id=sess_a; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; reason=proactive_frontier_gap",
                "event=nkn_bridge_bulk_send_summary; frames_sent=16; payload_bytes_sent=1048576; payload_bytes_per_second=524288; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=1; configured_concurrency=4; effective_concurrency=4; send_p95_ms=3; send_max_ms=5; worker_utilization_percent=12; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "frontier_gap_repair_sent_unfilled",
            "frontier_repair_sent_but_not_filled",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=16; chunk_count_sent=48; chunks_accepted_for_transport=160; remote_next_expected_chunk_index=100; remote_granted_until_chunk_index_exclusive=148; remote_granted_window_bytes=1032192; sent_cache_chunk_count=60; sent_cache_bytes=1290240; send_wait_count=2; repair_send_count=1",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=16; chunk_count_prepared=48; raw_bytes_prepared=1048576; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=900000; raw_bytes_received_per_second=450000; contiguous_bytes_committed=0; contiguous_bytes_committed_per_second=0; pending_chunk_count=0; pending_bytes=0; next_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; oldest_gap_age_ms=2200; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; write_batch_count=0; write_batch_bytes=0; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=450000; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_v4_gap_stall_summary; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=100; highest_received_chunk_index=148; late_arrival_distance=48; stall_duration_ms=2200; pending_bytes=0; granted_window_bytes=1032192; sparse_mode=1; sparse_written_ahead_bytes=1032192; sparse_gap_count=0",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; repair_request_key=100:1; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; reason=proactive_frontier_gap",
                "event=filetransfer_frontier_gap_repair_sender_received; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; scheduled_chunk_count=1; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=160; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0",
                "event=filetransfer_frontier_gap_repair_sender_sent; transfer_id=transfer_frontier_gap_repair_sent_unfilled; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=160; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=16; payload_bytes_sent=1048576; payload_bytes_per_second=524288; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=1; configured_concurrency=4; effective_concurrency=4; send_p95_ms=3; send_max_ms=5; worker_utilization_percent=12; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "proactive_frontier_overlimited",
            "proactive_frontier_repair_overlimited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=8429568; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=2",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; oldest_gap_age_ms=920; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=2042880; sparse_gap_count=1",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=519; late_arrival_distance=65; highest_received_chunk_index=565; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=915; late_arrival_distance=95; highest_received_chunk_index=595; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=1; repair_pressure=1; repeated_proactive_repair=1; timeout_streak=0; late_arrival_distance=95; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=919; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=595; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_proactive_frontier_overlimited; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=repair_pressure; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "external_transport",
            "external_transport_limited",
            new[]
            {
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_external_transport; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=8000000; raw_bytes_per_second=4000000; chunk_frames_sent=0; batch_frames_sent=80; chunk_count_sent=160; chunks_accepted_for_transport=400; remote_next_expected_chunk_index=240; remote_granted_until_chunk_index_exclusive=400; remote_granted_window_bytes=3932160; sent_cache_chunk_count=160; sent_cache_bytes=3932160; send_wait_count=0; repair_send_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_external_transport; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1000000; raw_bytes_received_per_second=500000; contiguous_bytes_committed=1000000; contiguous_bytes_committed_per_second=500000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=280; highest_received_chunk_index=280; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=400; granted_window_bytes=2949120; write_batch_count=1; write_batch_bytes=1000000; write_duration_ms=5",
                "event=nkn_bridge_bulk_send_summary; frames_sent=80; payload_bytes_sent=8000000; payload_bytes_per_second=4000000; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; send_p95_ms=3; send_max_ms=5; sample_window_ms=2000",
                "event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1"
            }
        ];
        yield return
        [
            "proactive_frontier_repeated",
            "proactive_frontier_repair_overlimited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=8429568; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=2",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; oldest_gap_age_ms=920; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=2042880; sparse_gap_count=1",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=915; late_arrival_distance=95; highest_received_chunk_index=595; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=500; same_frontier_unfilled_ms=500; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=1; repair_pressure=1; repeated_proactive_repair=1; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=500; same_frontier_unfilled_ms=500; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; timeout_streak=0; late_arrival_distance=95; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=919; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=595; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_proactive_frontier_repeated; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=repair_pressure; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=500; same_frontier_unfilled_ms=500; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
        yield return
        [
            "proactive_frontier_repeated_after_grace",
            "proactive_frontier_gap_repeated_limited",
            new[]
            {
                "event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default",
                "event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0",
                "event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=700; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=896; remote_granted_window_bytes=8429568; sent_cache_chunk_count=200; sent_cache_bytes=4300800; send_wait_count=8; repair_send_count=2",
                "event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0",
                "event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1200000; contiguous_bytes_committed_per_second=600000; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; oldest_gap_age_ms=2600; granted_until_chunk_index_exclusive=525; granted_window_bytes=537600; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=2042880; sparse_gap_count=1",
                "event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; start_chunk_index=500; requested_chunk_count=9; gap_stall_age_ms=2600; late_arrival_distance=95; highest_received_chunk_index=595; granted_until_chunk_index_exclusive=896; granted_window_bytes=8429568; reason=proactive_frontier_gap; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=2600; same_frontier_unfilled_ms=2600; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited",
                "event=filetransfer_v4_reorder_policy_decision; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; policy=SparseTolerant; decision=limited; sparse_mode=1; transport_profile=ConservativeNknStartup; screen_share_active=0; screen_share_degraded=0; pull_session_degraded=0; receiver_buffer_pressure=0; repair_recent=1; repair_pressure=1; repeated_proactive_repair=1; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=2600; same_frontier_unfilled_ms=2600; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; timeout_streak=0; late_arrival_distance=95; soft_reorder_threshold=512; soft_gap_stall_ms=1500; sparse_ahead_gap_stall_limit_ms=2500; gap_stall_age_ms=2600; current_profile=healthy_limited; target_window_bytes=524288; soft_limit_target_bytes=4194304; granted_window_bytes=8429568; next_chunk_index=500; highest_received_chunk_index=595; pending_chunk_count=0; pending_bytes=0",
                "event=filetransfer_v4_grant_window_summary; transfer_id=transfer_proactive_frontier_repeated_after_grace; session_id=sess_a; reason=low_watermark; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=524288; effective_granted_window_bytes=537600; current_credit_chunks=25; desired_credit_chunks=25; low_watermark_credit_chunks=23; credit_remaining_chunks=25; credit_desired_chunks=25; credit_remaining_bytes=537600; credit_desired_bytes=537600; granted_until_chunk_index_exclusive=525; target_granted_until_chunk_index_exclusive=525; grant_base_chunk_index=500; grant_base_reason=contiguous_frontier; sparse_ahead_bytes=0; credit_base_chunk_index=500; credit_base_reason=contiguous_frontier; sparse_credit_advance_bytes=0; sparse_credit_topup_bytes=262144; sparse_credit_block_reason=repair_pressure; proactive_repair_pressure_state=repeated_unfilled; proactive_repair_age_ms=2600; same_frontier_unfilled_ms=2600; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_limited; next_chunk_index=500; highest_received_chunk_index=595; late_arrival_distance=95; pending_chunk_count=0; pending_bytes=0",
                "event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"
            }
        ];
    }

    protected static string[] BuildRouteAwareCompletedFixture(
        string transferId,
        string route,
        int protocolVersion,
        string runtimeProfile,
        string bridgeRecoveryPolicy,
        string runtimeEventName,
        int? runtimeProtocolVersion = null,
        string? diagnosticMarkerOverride = null)
    {
        var effectiveRuntimeProtocolVersion = runtimeProtocolVersion ?? protocolVersion;
        var frameFamily = protocolVersion == 6 ? "v6" : "v4";
        var diagnosticMarker = diagnosticMarkerOverride ?? (route == "diagnostic_regular_nkn_v6" ? "1" : "0");
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; handoff_kind=none; bridge_recovery_policy={bridgeRecoveryPolicy}; liveness_terminal_policy={route}; selection_reason=test_route; file_tuna_active={(route == "file_tuna_v4" ? 1 : 0)}; post_tuna_fallback_active={(route == "post_tuna_fallback_v6" ? 1 : 0)}; diagnostic_regular_nkn_v6={diagnosticMarker}; transport_profile=default"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; handoff_kind=none; bridge_recovery_policy={bridgeRecoveryPolicy}; liveness_terminal_policy={route}; selection_reason=test_route; file_tuna_active={(route == "file_tuna_v4" ? 1 : 0)}; post_tuna_fallback_active={(route == "post_tuna_fallback_v6" ? 1 : 0)}; diagnostic_regular_nkn_v6={diagnosticMarker}; transport_profile=default"),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}; selection_reason=test_route"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}; reason=role=Sender"),
            LogLine($"event={runtimeEventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={effectiveRuntimeProtocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={effectiveRuntimeProtocolVersion}; runtime_profile={runtimeProfile}; frame_family={frameFamily}; bridge_recovery_policy={bridgeRecoveryPolicy}"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    protected static string[] BuildRouteAwareControlledRestartFixture(bool includeSetupTerminal)
    {
        const string transferId = "[redacted]";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; reason=role=Sender"),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_v4_receiver_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict")
        };

        if (includeSetupTerminal)
        {
            lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Canceled; error_code=canceled_remote; saved_path=(none)"));
            lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Canceled; error_code=canceled_local"));
        }

        lines.AddRange(BuildRouteAwareMeasuredFallbackFixture());
        return lines.ToArray();
    }

    protected static string[] BuildRouteAwareLiveReenableFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=sidecar_read_failed"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=sidecar_read_failed"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    protected static string[] BuildRouteAwareLiveSwitchOffFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=sidecar_read_failed"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    protected static string[] BuildRouteAwareLiveMultiCycleFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; reason=role=Sender"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=first_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=8-15; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=second_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    protected static string[] BuildRouteAwareLiveRegularActivationCycleFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; reason=role=Sender"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=regular_nkn_v4_fast; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_unlocked_during_regular_transfer"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=1"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=first_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=2"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=2; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=8-15; raw_chunk_bytes=172032; chunk_count=8"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; previous_route=post_tuna_fallback_v6; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; state=started; reason=tuna_reenabled"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=3"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=3; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=4; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=second_toggle_off"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4"),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; reason=transport_probe_ack"),
            LogLine($"event=filetransfer_live_route_epoch_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; terminal_state=completed; reason=Transfer complete."),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    protected static string[] BuildRouteAwareMeasuredFallbackFixture()
    {
        const string transferId = "[redacted]";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6; selection_reason=post_tuna_file_fallback; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6; selection_reason=post_tuna_file_fallback; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_file_fallback"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; reason=role=Sender"),
            LogLine($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_v6_sender_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_v6_receiver_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_runtime_started; direction=inbound; role=receiver; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-31; payload_bytes=67108864; serialized_payload_bytes=67108864; raw_chunk_bytes=67108864; chunk_count=32"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-31; raw_chunk_bytes=67108864; chunk_count=32"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        ];
    }

    protected static string[] BuildRuntimeUnlockRecoveryCoordinationFailureFixture()
    {
        const string transferId = "transfer_runtime_unlock_coordination";
        const string sessionId = "sess_runtime_unlock_coordination";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 2),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; reason=role=Sender", secondsOffset: 3),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 4),
            LogLine($"event=filetransfer_v4_receiver_started; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8", secondsOffset: 10),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-7; raw_chunk_bytes=172032; chunk_count=8", secondsOffset: 11),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_activation_control_send_recovery_requested; session_id={sessionId}; trigger=runtime_unlock; reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout", secondsOffset: 24),
            LogLine("event=nkn_bridge_receive_stall_recovery_requested; reason=core_filetransfer_request; requested_reason=tuna_activation_offer_send_timeout; stall_reason=regular_v4_unproven_recovery_escalation; regular_v4_runtime_unlock_unproven_escalation=1; attempt=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 26),
            LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=abc123; recovery_count=1; resume_after_recovery_ms=1750; total_messages_received_since_last=1", secondsOffset: 42),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=1", secondsOffset: 45),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)", secondsOffset: 93)
        ];
    }

    protected static string[] BuildRuntimeUnlockRecoveryContractDispatchedFixture()
    {
        const string transferId = "transfer_runtime_unlock_contract_dispatched";
        const string sessionId = "sess_runtime_unlock_contract_dispatched";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=1", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_authority_granted; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryqueued; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=1; retry_authority_pending=1; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=(none)", secondsOffset: 46),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=1", secondsOffset: 48),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=1; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=bulk_queue_fallback; authority_failure_reason=(none)", secondsOffset: 51),
            LogLine($"event=session_recovery_contract_retry_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=1", secondsOffset: 52),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Completed; error_code=(none); sha256_match=1", secondsOffset: 90),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 91)
        ];
    }

    protected static string[] BuildRuntimeUnlockRetryDispatchedButOfferObservationBlockedFixture()
    {
        const string transferId = "transfer_runtime_unlock_offer_observation_blocked";
        const string sessionId = "sess_runtime_unlock_offer_observation_blocked";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=1", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=1", secondsOffset: 48),
            LogLine($"event=tuna_activation_control_send_waiting_for_regular_v4_recovery; session_id={sessionId}; purpose=offer; blocker_reason=receive_stall_recovery_in_progress; blocker_remaining_ms=0; regular_v4_pressure_reason=receive_stall_recovery_in_progress; regular_v4_pressure_remaining_ms=0; recovery_age_ms=5276; wait_budget_ms=5000; reason=runtime_unlock_regular_v4_receive_stall", secondsOffset: 49),
            LogLine($"event=tuna_acceleration_control_bulk_queue_fallback_skipped; purpose=offer; message_type=transport_acceleration_offer; reason=runtime_unlock_active_filetransfer_requires_direct_observed_send; blocker_reason=regular_v4_receive_stall_bypass", secondsOffset: 50),
            LogLine($"event=tuna_acceleration_offer_rejected; reason=runtime_unlock; session_id={sessionId}; lanes=file; payer_decision_id=8; generation=4; observed_lane=(none); queue_local_only=0; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout", secondsOffset: 51),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildRuntimeUnlockDispatchDeferredByRegularV4ReceiveRecoveryFixture()
    {
        const string transferId = "transfer_runtime_unlock_dispatch_deferred_regular_v4";
        const string sessionId = "sess_runtime_unlock_dispatch_deferred_regular_v4";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=0", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0", secondsOffset: 48),
            LogLine($"event=session_recovery_contract_retry_queued; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=3; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryqueued; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=regular_v4_receive_recovery_pending", secondsOffset: 49),
            LogLine($"event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery; session_id={sessionId}; trigger=runtime_unlock; payer_decision_id=8; blocker_reason=receive_stall_recovery_awaiting_receive_proof; blocker_remaining_ms=0; retry_scheduled=1; recovery_requested=0", secondsOffset: 50),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildRuntimeUnlockRegularV4ActivationWindowPeerVisibleFixture()
    {
        const string transferId = "transfer_runtime_unlock_regular_v4_window_peer_visible";
        const string sessionId = "sess_runtime_unlock_regular_v4_window_peer_visible";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id=(none); transaction_generation=1; offer_generation=4; lease_generation=2; listener_run_id=listener-start-2; payer_decision_id=8; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=test_listener_ready", secondsOffset: 35),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_started; session_id={sessionId}; trigger=runtime_unlock; payer_decision_id=8; blocker_reason=receive_stall_recovery_awaiting_receive_proof; blocker_remaining_ms=0; contract_generation=1; authority_attempt=1; tuna_path_lease_generation=2; reason=bounded_peer_visible_offer_window", secondsOffset: 36),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=4; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=offer_observed_send", secondsOffset: 37),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=1; authority_attempt=1; authorized_observed_lane=control_priority; authority_failure_reason=(none); regular_v4_activation_window_started=1; regular_v4_activation_window_peer_visible=0; regular_v4_activation_window_failed=0", secondsOffset: 38),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=1; offer_generation=4; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_offer_received", secondsOffset: 39),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_peer_visible; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; reason=transport_acceleration_offer_received", secondsOffset: 40),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 90)
        ];
    }

    protected static string[] BuildRuntimeUnlockRegularV4ActivationWindowObservedOnlyFixture()
    {
        const string transferId = "transfer_runtime_unlock_regular_v4_window_observed_only";
        const string sessionId = "sess_runtime_unlock_regular_v4_window_observed_only";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id=(none); transaction_generation=1; offer_generation=4; lease_generation=2; listener_run_id=listener-start-2; payer_decision_id=8; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=test_listener_ready", secondsOffset: 35),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_started; session_id={sessionId}; trigger=runtime_unlock; payer_decision_id=8; blocker_reason=receive_stall_recovery_awaiting_receive_proof; blocker_remaining_ms=0; contract_generation=1; authority_attempt=1; tuna_path_lease_generation=2; reason=bounded_peer_visible_offer_window", secondsOffset: 36),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=4; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=offer_observed_send", secondsOffset: 37),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=1; authority_attempt=1; authorized_observed_lane=control_priority; authority_failure_reason=(none); regular_v4_activation_window_started=1; regular_v4_activation_window_peer_visible=0; regular_v4_activation_window_failed=0", secondsOffset: 38),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 90)
        ];
    }

    protected static string[] BuildRuntimeUnlockRegularV4PeerVisibleWindowDeliveryFailedFixture()
    {
        const string transferId = "transfer_runtime_unlock_regular_v4_window_delivery_failed";
        const string sessionId = "sess_runtime_unlock_regular_v4_window_delivery_failed";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id=(none); transaction_generation=1; offer_generation=4; lease_generation=2; listener_run_id=listener-start-2; payer_decision_id=8; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=test_listener_ready", secondsOffset: 35),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_started; session_id={sessionId}; trigger=runtime_unlock; payer_decision_id=8; blocker_reason=receive_stall_recovery_awaiting_receive_proof; blocker_remaining_ms=0; contract_generation=1; authority_attempt=1; tuna_path_lease_generation=2; reason=bounded_peer_visible_offer_window", secondsOffset: 36),
            LogLine($"event=runtime_unlock_regular_v4_peer_visible_window_delivery_started; session_id={sessionId}; purpose=offer; reason=atomic_peer_visible_window", secondsOffset: 37),
            LogLine($"event=runtime_unlock_control_plane_delivery_result; session_id={sessionId}; payer_decision_id=8; generation=4; trigger=runtime_unlock; control_ack=0; control_copy=0; bulk_copy=0; peer_visible_any=0; accepted_any=0; control_ack_error=TimeoutException; control_copy_error=OperationCanceledException; bulk_copy_error=OperationCanceledException", secondsOffset: 38),
            LogLine($"event=runtime_unlock_regular_v4_peer_visible_window_delivery_failed; session_id={sessionId}; purpose=offer; observed_lane=(none); reason=peer_visible_proof_missing", secondsOffset: 39),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; reason=runtime_unlock_regular_v4_peer_visible_window_failed; source_reason=offer_send_not_observed; peer_visible=0; retry_scheduled=0; retry_after_recovery_armed=0", secondsOffset: 40),
            LogLine($"event=runtime_unlock_transaction_failed; session_id={sessionId}; transaction_generation=1; offer_generation=4; state=failed; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=runtime_unlock_regular_v4_peer_visible_window_failed; retired_reason=(none); reason=runtime_unlock_regular_v4_peer_visible_window_failed", secondsOffset: 41),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 90)
        ];
    }

    protected static string[] BuildRuntimeUnlockRegularV4ActivationWindowLatePeerProofFixture()
    {
        const string transferId = "transfer_runtime_unlock_regular_v4_window_late_peer";
        const string sessionId = "sess_runtime_unlock_regular_v4_window_late_peer";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id=(none); transaction_generation=1; offer_generation=4; lease_generation=2; listener_run_id=listener-start-2; payer_decision_id=8; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=test_listener_ready", secondsOffset: 35),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_started; session_id={sessionId}; trigger=runtime_unlock; payer_decision_id=8; blocker_reason=receive_stall_recovery_awaiting_receive_proof; blocker_remaining_ms=0; contract_generation=1; authority_attempt=1; tuna_path_lease_generation=2; reason=bounded_peer_visible_offer_window", secondsOffset: 36),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=4; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=offer_observed_send", secondsOffset: 37),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=1; authority_attempt=1; authorized_observed_lane=control_to_bulk_endpoint; authority_failure_reason=(none); regular_v4_activation_window_started=1; regular_v4_activation_window_peer_visible=0; regular_v4_activation_window_failed=0", secondsOffset: 38),
            LogLine($"event=runtime_unlock_regular_v4_activation_window_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; reason=runtime_unlock_regular_v4_peer_visible_window_failed; source_reason=runtime_unlock_offer_peer_response_timeout; peer_visible=0; retry_scheduled=0; retry_after_recovery_armed=0", secondsOffset: 39),
            LogLine($"event=runtime_unlock_transaction_failed; session_id={sessionId}; transaction_generation=1; offer_generation=4; state=failed; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=runtime_unlock_regular_v4_peer_visible_window_failed; retired_reason=(none); reason=runtime_unlock_regular_v4_peer_visible_window_failed", secondsOffset: 40),
            LogLine($"event=runtime_unlock_control_plane_peer_visible; session_id={sessionId}; payer_decision_id=8; generation=4; trigger=runtime_unlock; proof=peer_copy", secondsOffset: 41),
            LogLine($"event=runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored; session_id={sessionId}; payer_decision_id=8; generation=4; proof=peer_copy; reason=stale_or_retired_generation", secondsOffset: 42),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 90)
        ];
    }

    protected static string[] BuildRuntimeUnlockListenerRearmFailureFixture()
    {
        const string transferId = "transfer_runtime_unlock_listener_rearm_failed";
        const string sessionId = "sess_runtime_unlock_listener_rearm_failed";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 4),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=session_recovery_contract_listener_rearm_required; session_id={sessionId}; trigger=runtime_unlock; reason=runtime_unlock_listener_rearm_failed; listener_ready=1; listener_unavailable=1", secondsOffset: 44),
            LogLine($"event=session_recovery_contract_listener_rearm_failed; session_id={sessionId}; reason=runtime_unlock_listener_rearm_failed; trigger=runtime_unlock", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=runtime_unlock_listener_rearm_failed", secondsOffset: 46),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=0; chunks_transferred=0; chunk_count=6242; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildRuntimeUnlockListenerRearmCompletedFixture()
    {
        const string transferId = "transfer_runtime_unlock_listener_rearm_completed";
        const string sessionId = "sess_runtime_unlock_listener_rearm_completed";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=7; generation=3; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=session_recovery_contract_listener_rearm_required; session_id={sessionId}; trigger=runtime_unlock; reason=runtime_unlock_listener_rearm_failed; listener_ready=0; listener_unavailable=1", secondsOffset: 44),
            LogLine($"event=session_recovery_contract_listener_rearm_completed; session_id={sessionId}; trigger=runtime_unlock", secondsOffset: 45),
            LogLine($"event=runtime_unlock_offer_dispatched_after_listener_rearm; session_id={sessionId}; payer_decision_id=8; generation=4; trigger=runtime_unlock", secondsOffset: 46),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=1; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=1; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=(none)", secondsOffset: 47),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; offer_generation=4; retired_offer_generation=3; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=control_priority; authority_failure_reason=(none)", secondsOffset: 50),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Completed; error_code=(none); sha256_match=1", secondsOffset: 90),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 91)
        ];
    }

    protected static string[] BuildRuntimeUnlockPeerResponseMissingUnderRegularV4RecoveryFixture()
    {
        const string transferId = "transfer_runtime_unlock_peer_response_missing";
        const string sessionId = "sess_runtime_unlock_peer_response_missing";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_sender_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast", secondsOffset: 5),
            LogLine($"event=session_recovery_contract_retry_dispatched; session_id={sessionId}; transfer_id={transferId}; contract_generation=4; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_peer_response_timeout; recovery_reason=tuna_activation_offer_peer_response_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=1; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=1; retry_authority_granted=1; observed_send_pending=0; authority_attempt=2; authorized_observed_lane=(none); authority_failure_reason=(none); cutthrough_pending=1; cutthrough_active=0; cutthrough_attempt=0", secondsOffset: 44),
            LogLine($"event=runtime_unlock_cutthrough_started; session_id={sessionId}; offer_generation=12; contract_generation=4; attempt=1; trigger=runtime_unlock; reason=peer_response_timeout_under_regular_v4_recovery", secondsOffset: 45),
            LogLine($"event=session_recovery_contract_retry_authority_observed; session_id={sessionId}; transfer_id={transferId}; contract_generation=4; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=retryobserved; retry_reason=runtime_unlock_offer_peer_response_timeout; recovery_reason=tuna_activation_offer_peer_response_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=1; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=1; observed_send_pending=1; authority_attempt=2; authorized_observed_lane=control_to_bulk_endpoint; authority_failure_reason=(none); cutthrough_pending=0; cutthrough_active=1; cutthrough_offer_sent=0; cutthrough_peer_received=0; cutthrough_completed=0; cutthrough_attempt=1", secondsOffset: 47),
            LogLine($"event=runtime_unlock_cutthrough_offer_sent; session_id={sessionId}; offer_generation=12; contract_generation=4; attempt=1; observed_lane=control_to_bulk_endpoint", secondsOffset: 48),
            LogLine($"event=tuna_acceleration_offer_queued; reason=runtime_unlock; session_id={sessionId}; lanes=file; payer_decision_id=9; generation=12; observed_lane=control_to_bulk_endpoint; queue_local_only=0; recovery_requested=0; recovery_reason=(none)", secondsOffset: 49),
            LogLine($"event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout; timeout_ms=2500; session_id={sessionId}; payer_decision_id=9; generation=12; observed_lane=control_to_bulk_endpoint", secondsOffset: 52),
            LogLine($"event=runtime_unlock_cutthrough_failed; session_id={sessionId}; payer_decision_id=9; offer_generation=12; contract_generation=4; attempt=1; reason=runtime_unlock_peer_response_not_received", secondsOffset: 53),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=9; generation=12; interruption_reason=runtime_unlock_offer_peer_response_timeout; retry_scheduled=0; retry_after_recovery_armed=0; replay_scheduled=0; answer_timeout_scheduled=0; recovery_requested=0; recovery_reason=runtime_unlock_peer_response_not_received", secondsOffset: 54),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=536870912; bytes_transferred=0; chunks_transferred=0; chunk_count=24962; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildRuntimeUnlockBrokenStateRetryChurnFixture()
    {
        const string transferId = "transfer_runtime_unlock_broken_state";
        const string sessionId = "sess_runtime_unlock_broken_state";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=4; offer_generation=11; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 3),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=9; generation=11; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
            LogLine($"event=runtime_unlock_broken_state_fresh_retry_consumed; session_id={sessionId}; transfer_id={transferId}; contract_generation=4; offer_generation=11; reason=runtime_unlock_offer_send_not_observed; family=offer_send_not_observed; fresh_retry_attempt=1; max_fresh_retries=1", secondsOffset: 40),
            LogLine($"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id={sessionId}; retired_generation=11; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; trigger=receive_resumed; queued_behind_active_negotiation=0", secondsOffset: 41),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; trigger=runtime_unlock; session_id={sessionId}; payer_decision_id=10; generation=12; retry_scheduled=0; retry_after_recovery_armed=1; recovery_requested=1; recovery_reason=tuna_activation_offer_send_timeout; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 60),
            LogLine($"event=runtime_unlock_broken_state_retry_suppressed; session_id={sessionId}; transfer_id={transferId}; contract_generation=5; offer_generation=12; reason=runtime_unlock_offer_send_not_observed; family=offer_send_not_observed; fresh_retry_attempts=1; max_fresh_retries=1; final_reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed", secondsOffset: 61),
            LogLine($"event=runtime_unlock_transaction_failed; session_id={sessionId}; transaction_generation=4; offer_generation=12; state=failed; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed; retired_reason=(none); reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed", secondsOffset: 62),
            LogLine($"event=session_recovery_contract_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=5; offer_generation=12; retired_offer_generation=12; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=tuna_activation_offer_send_timeout; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed; broken_state_fresh_retry_attempt=1; broken_state_fresh_retry_limit=1; broken_state_finalized=1; broken_state_failure_reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed", secondsOffset: 63),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=268435456; chunks_transferred=12481; chunk_count=12481; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 120)
        ];
    }

    protected static string[] BuildRuntimeUnlockCutThroughPeerReceivedRegularActivationCycleFixture()
    {
        return
        [
            LogLine("event=runtime_unlock_cutthrough_started; session_id=sess_redacted; offer_generation=12; contract_generation=4; attempt=1; trigger=runtime_unlock; reason=peer_response_timeout_under_regular_v4_recovery", secondsOffset: 20),
            LogLine("event=runtime_unlock_cutthrough_offer_sent; session_id=sess_redacted; offer_generation=12; contract_generation=4; attempt=1; observed_lane=control_to_bulk_endpoint", secondsOffset: 21),
            LogLine("event=runtime_unlock_cutthrough_peer_received; session_id=sess_redacted; payer_decision_id=9; offer_generation=12; contract_generation=4; attempt=1; source=transport_acceleration_answer", secondsOffset: 22),
            LogLine("event=runtime_unlock_cutthrough_completed; session_id=sess_redacted; offer_generation=12; contract_generation=4; attempt=1; reason=answer_acknowledged", secondsOffset: 23),
            .. BuildRouteAwareLiveRegularActivationCycleFixture()
        ];
    }

    protected static string[] BuildRuntimeUnlockTransactionLocalOnlyObservedSendFixture()
    {
        const string transferId = "transfer_runtime_unlock_transaction_local_only";
        const string sessionId = "sess_runtime_unlock_transaction_local_only";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 10),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=offer_observed_send", secondsOffset: 11),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_rejected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; reason=normal_to_tuna_activation; rejection_reason=transaction_proof_missing", secondsOffset: 12),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=0; chunks_transferred=0; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildRuntimeUnlockTransactionPeerProofAndRouteCommitFixture()
    {
        const string transferId = "[redacted]";
        const string sessionId = "sess_redacted";
        return
        [
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 1),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=offer_observed_send", secondsOffset: 2),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_offer_received", secondsOffset: 3),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_answer_received", secondsOffset: 4),
            LogLine($"event=runtime_unlock_probe_started; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; probe_id=probe-7; target_transport=tuna; transport_epoch=1; acked=0; state=started; reason=transport_probe_sent", secondsOffset: 5),
            LogLine($"event=runtime_unlock_transaction_path_probe_started; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=probe-7; path_probe_state=started; path_probe_transport=tuna; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_probe_sent", secondsOffset: 6),
            LogLine($"event=runtime_unlock_probe_acked; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; probe_id=probe-7; target_transport=tuna; transport_epoch=1; acked=1; state=acked; reason=transport_probe_ack", secondsOffset: 7),
            LogLine($"event=runtime_unlock_transaction_path_probe_acked; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=routecommitpending; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=probe-7; path_probe_state=acked; path_probe_transport=tuna; route_commit_pending=1; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_probe_ack", secondsOffset: 8),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_accepted; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; reason=normal_to_tuna_activation", secondsOffset: 9),
            LogLine($"event=runtime_unlock_transaction_route_committed; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=committed; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=probe-7; path_probe_state=acked; path_probe_transport=tuna; route_commit_pending=0; route_committed=1; failure_reason=(none); retired_reason=(none); reason=normal_to_tuna_activation", secondsOffset: 10),
            .. BuildRouteAwareLiveRegularActivationCycleFixture()
        ];
    }

    protected static string[] BuildRuntimeUnlockPreCommitRouteEpochProofFixture()
    {
        return BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line =>
            {
                if (!line.Contains("route=file_tuna_v4", StringComparison.Ordinal) ||
                    !line.Contains("handoff_kind=normal_to_tuna_activation", StringComparison.Ordinal) ||
                    !line.Contains("target_transport=tuna", StringComparison.Ordinal) ||
                    (!line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                     !line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal)))
                {
                    return line;
                }

                var updated = line
                    .Replace("reason=tuna_unlocked_during_regular_transfer", "reason=precommit_probe_ack", StringComparison.Ordinal)
                    .Replace("reason=tuna_reenabled", "reason=precommit_probe_ack", StringComparison.Ordinal)
                    .Replace("reason=transport_probe_ack", "reason=precommit_probe_ack", StringComparison.Ordinal);
                return updated;
            })
            .ToArray();
    }

    protected static string[] BuildRuntimeUnlockTunaProbePathNotEstablishedFixture()
    {
        const string transferId = "transfer_runtime_unlock_tuna_probe_path_missing";
        const string sessionId = "sess_runtime_unlock_tuna_probe_path_missing";
        const string probeId = "runtime-unlock-precommit:1:7:path-missing";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_path_lease_started; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=listenerstarting; reason=runtime_unlock", secondsOffset: 1),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 2),
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=runtime_unlock", secondsOffset: 3),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=transport_acceleration_offer_received", secondsOffset: 4),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=transport_acceleration_answer_received", secondsOffset: 5),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={probeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 6),
            LogLine($"event=runtime_unlock_probe_started; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; probe_id={probeId}; target_transport=tuna; transport_epoch=0; acked=0; state=started; reason=precommit_probe_sent", secondsOffset: 7),
            LogLine($"event=runtime_unlock_transaction_path_probe_started; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id={probeId}; path_probe_state=started; path_probe_transport=tuna; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=precommit_probe_sent", secondsOffset: 8),
            LogLine($"event=runtime_unlock_tuna_probe_sent; transport=tuna; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={probeId}; target_transport=tuna", secondsOffset: 9),
            LogLine($"event=tuna_path_lease_failed; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=failed; current=1; failure_reason=sidecar_tuna_stream_eof; retired_reason=(none); reason=sidecar_tuna_stream_eof", secondsOffset: 10),
            LogLine($"event=runtime_unlock_tuna_probe_sidecar_failure; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={probeId}; target_transport=tuna; sidecar_reason=sidecar_tuna_stream_eof; reason=runtime_unlock_tuna_probe_sidecar_closed", secondsOffset: 11),
            LogLine($"event=runtime_unlock_probe_failed; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; probe_id={probeId}; target_transport=tuna; transport_epoch=0; acked=0; state=failed; reason=runtime_unlock_tuna_probe_sidecar_closed; sidecar_reason=sidecar_tuna_stream_eof", secondsOffset: 12),
            LogLine($"event=runtime_unlock_precommit_probe_failed; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={probeId}; reason=runtime_unlock_tuna_probe_sidecar_closed", secondsOffset: 13),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=268435456; chunks_transferred=12481; chunk_count=12481; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 120)
        ];
    }

    protected static string[] BuildRuntimeUnlockTransactionPendingCommitAfterPriorCommitFixture()
    {
        const string transferId = "[redacted]";
        const string sessionId = "sess_redacted";
        return
        [
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 1),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=offer_observed_send", secondsOffset: 2),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; route_commit_pending=1; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_offer_received", secondsOffset: 3),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=1; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_answer_received", secondsOffset: 4),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_accepted; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; reason=normal_to_tuna_activation", secondsOffset: 5),
            LogLine($"event=runtime_unlock_transaction_route_committed; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=committed; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=1; failure_reason=(none); retired_reason=(none); reason=normal_to_tuna_activation", secondsOffset: 6),
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=2; offer_generation=9; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=peer_runtime_unlock_offer_accepted", secondsOffset: 20),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=2; offer_generation=9; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; route_commit_pending=1; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_offer_received", secondsOffset: 21),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=2; offer_generation=9; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=1; route_committed=0; failure_reason=(none); retired_reason=(none); reason=transport_acceleration_answer_pending", secondsOffset: 22),
            LogLine($"event=runtime_unlock_transaction_failed; session_id={sessionId}; transaction_generation=2; offer_generation=9; state=failed; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; failure_reason=runtime_unlock_answer_ack_timeout; retired_reason=(none); reason=runtime_unlock_answer_ack_timeout", secondsOffset: 60),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90)
        ];
    }

    protected static string[] BuildRuntimeUnlockOldV6ProbeRejectedByV4PeerFixture()
    {
        const string transferId = "transfer_runtime_unlock_old_v6_probe";
        const string sessionId = "sess_runtime_unlock_old_v6_probe";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=tuna_path_lease_started; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=listenerstarting; reason=runtime_unlock", secondsOffset: 2),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 3),
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=runtime_unlock", secondsOffset: 4),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=offer_observed_send", secondsOffset: 5),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=transport_acceleration_offer_received", secondsOffset: 6),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=(none); path_probe_state=none; path_probe_transport=unknown; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=transport_acceleration_answer_received", secondsOffset: 7),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_rejected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; reason=normal_to_tuna_activation; rejection_reason=runtime_unlock_probe_missing", secondsOffset: 8),
            LogLine($"event=runtime_unlock_probe_started; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id=v6-probe-7; target_transport=tuna; transport_epoch=5; acked=0; state=started; reason=transport_probe_sent", secondsOffset: 9),
            LogLine($"event=runtime_unlock_transaction_path_probe_started; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; path_probe_id=v6-probe-7; path_probe_state=started; path_probe_transport=tuna; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=transport_probe_sent", secondsOffset: 10),
            LogLine($"event=filetransfer_v6_transport_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; target_transport=tuna; transport_epoch=5; probe_id=v6-probe-7; reason=runtime_unlock_probe_missing", secondsOffset: 11),
            LogLine($"event=filetransfer_data_frame_ignored; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; frame_type=filetransfer.transport_probe.v6; reason=protocol_not_v4; transport=tuna; transport_epoch=5; probe_id=v6-probe-7", secondsOffset: 12),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=0; chunks_transferred=0; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildRuntimeUnlockTunaPathLeaseUnavailableAtAnswerFixture()
    {
        const string transferId = "transfer_runtime_unlock_tuna_path_lease_unavailable";
        const string sessionId = "sess_runtime_unlock_tuna_path_lease_unavailable";
        return
        [
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 1),
            LogLine($"event=tuna_path_lease_started; session_id={sessionId}; transfer_id=(none); transaction_generation=0; offer_generation=0; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=listenerstarting; reason=runtime_unlock", secondsOffset: 2),
            LogLine($"event=tuna_path_lease_listener_ready; session_id={sessionId}; transfer_id=(none); transaction_generation=0; offer_generation=0; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=listenerready; current=1; failure_reason=(none); retired_reason=(none); reason=runtime_unlock", secondsOffset: 3),
            LogLine($"event=runtime_unlock_transaction_offer_generation_created; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=runtime_unlock", secondsOffset: 4),
            LogLine($"event=runtime_unlock_transaction_observed_send; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=offersentobserved; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=offer_observed_send", secondsOffset: 5),
            LogLine($"event=runtime_unlock_transaction_peer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=peerreceived; peer_visible_proof=1; peer_received=1; answer_received=0; route_commit_pending=1; route_committed=0; failure_reason=(none); retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=(none); reason=transport_acceleration_answer", secondsOffset: 6),
            LogLine($"event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable; session_id={sessionId}; payer_decision_id=5; offer_generation=7; reject_reason=sidecar_unavailable", secondsOffset: 7),
            LogLine($"event=tuna_path_lease_failed; session_id={sessionId}; transfer_id=(none); transaction_generation=1; offer_generation=7; lease_generation=3; listener_run_id=listener-start-3; payer_decision_id=5; state=failed; current=0; failure_reason=runtime_unlock_answer_rejected_tuna_path_lease_unavailable; retired_reason=(none); reason=runtime_unlock_answer_rejected_tuna_path_lease_unavailable", secondsOffset: 8),
            LogLine($"event=runtime_unlock_transaction_failed; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=failed; peer_visible_proof=1; peer_received=1; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=runtime_unlock_answer_rejected_tuna_path_lease_unavailable; retired_reason=(none); tuna_path_lease_generation=3; tuna_path_lease_state=failed; tuna_path_lease_current=0; tuna_path_lease_listener_run_id=listener-start-3; tuna_path_lease_failure_reason=runtime_unlock_answer_rejected_tuna_path_lease_unavailable; reason=runtime_unlock_answer_rejected_tuna_path_lease_unavailable", secondsOffset: 9),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_rejected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; reason=normal_to_tuna_activation; rejection_reason=tuna_path_lease_failed", secondsOffset: 10),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=0; chunks_transferred=0; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 92)
        ];
    }

    protected static string[] BuildCleanCompletedTransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Sender"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; chunk_range=0-1; chunk_frame_count=2; raw_bytes=49152; lane=bulk"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; serialized_payload_bytes=49251; secure_payload_bytes=49476; bridge_payload_bytes=49553; bridge_command_bytes=49653; max_allowed_bytes=65536"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-1; payload_bytes=49251; serialized_payload_bytes=49251; raw_chunk_bytes=49152; chunk_count=2"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-1; raw_chunk_bytes=49152; chunk_count=2"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    protected static string[] BuildCleanCompletedV4TransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_v6_negotiated; transfer_id={transferId}; session_id=sess_a; direction=outbound; negotiated_version=6"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; reason=role=Receiver"),
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size=21504; pump_depth=8; pending_send_bytes_limit=2097152"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; file_only=1"),
            LogLine($"event=filetransfer_v4_manifest_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_manifest_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0; bytes_committed=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_feedback_first_success; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; lane=bulk; secondary_lane=control"),
            LogLine($"event=filetransfer_v4_feedback_secondary_completed; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; lane=control; elapsed_ms=3"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=1000; scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=1; raw_bytes_sent=64512; repair_send_count=0"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_default_21k; batch_chunk_count=3; chunk_range=0-2; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_default_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-2; payload_bytes=64700; serialized_payload_bytes=64700; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512; batch_profile=v4_default_21k"),
            LogLine($"event=filetransfer_v4_chunk_batch_received; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512"),
            LogLine($"event=filetransfer_v4_sparse_write_committed; transfer_id={transferId}; session_id=sess_a; chunk_count=3; bytes_written=64512; contiguous_committed_chunk_index=3; durable_received_highest_chunk_index=2"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; payload_bytes_sent=65024; payload_bytes_per_second=6502400; in_flight=1; in_flight_bytes=65024; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; in_flight_bytes_max=65024; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    protected static string[] BuildCleanCompletedRegularNknV4TransferFixture(string transferId)
    {
        return
        [
            LogLine($"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id=sess_a; direction=Outbound; protocol_version=4; activation=primary_regular_nkn; runtime_profile=regular_nkn_v4_fast"),
            LogLine($"event=filetransfer_v4_negotiated; transfer_id={transferId}; session_id=sess_a; direction=Inbound; protocol_version=4; activation=primary_regular_nkn; runtime_profile=regular_nkn_v4_fast"),
            LogLine($"event=filetransfer_session_opened; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; chunk_size_bytes=21504; pipeline_depth=8; reason=role=Sender"),
            LogLine($"event=filetransfer_session_opened; direction=inbound; transfer_id={transferId}; session_id=sess_a; protocol_version=4; chunk_size_bytes=21504; pipeline_depth=8; reason=role=Receiver"),
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4; chunk_size=21504; pump_depth=8; pending_send_bytes_limit=2097152"),
            LogLine($"event=filetransfer_v4_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4; session_open_chunk_size_bytes=21504; session_open_pipeline_depth=8"),
            LogLine($"event=filetransfer_v4_manifest_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.manifest.v4; payload_bytes=167; serialized_payload_bytes=167; raw_chunk_bytes=0; chunk_count=0"),
            LogLine($"event=filetransfer_v4_manifest_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; chunk_size=21504; chunk_count=3; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0; bytes_committed=0; terminal_ready=0"),
            LogLine($"event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=control; queued_frames=1; queued_bytes=1024"),
            LogLine($"event=filetransfer_v4_feedback_first_success; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=bulk; secondary_lane=control"),
            LogLine($"event=filetransfer_v4_feedback_secondary_completed; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.state.v4; lane=control; elapsed_ms=3"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=1000; scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=1; raw_bytes_sent=64512; repair_send_count=0"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; batch_chunk_count=3; chunk_range=0-2; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"),
            LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v4; batch_profile=v4_default_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64615; secure_payload_bytes=64840; bridge_payload_bytes=64917; bridge_command_bytes=65017; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.055"),
            LogLine($"event=filetransfer_binary_frame_sent; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; payload_bytes=64615; serialized_payload_bytes=64615; raw_chunk_bytes=64512; chunk_count=3; batch_chunk_count=3"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3; batch_chunk_count=3"),
            LogLine($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512; batch_profile=v4_default_21k"),
            LogLine($"event=filetransfer_v4_chunk_batch_received; transfer_id={transferId}; session_id=sess_a; start_chunk_index=0; chunk_count=3; raw_bytes=64512"),
            LogLine($"event=filetransfer_v4_sparse_write_committed; transfer_id={transferId}; session_id=sess_a; chunk_count=3; bytes_written=64512; contiguous_committed_chunk_index=3; durable_received_highest_chunk_index=2"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size=64512; sha256=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; payload_bytes_sent=64917; payload_bytes_per_second=6491700; in_flight=1; in_flight_bytes=64917; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; in_flight_bytes_max=64917; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        ];
    }

    protected static string[] BuildLiveSummaryLines(
        double averageGoodput,
        double minimumGoodput,
        int bridgeWaiting,
        string protocolVersion = "3",
        double v4BatchRatio = 0,
        double v4PayloadFillPercent = 0)
    {
        return
        [
            "artifact_kind=live-nkn",
            "mode=nkn-fast",
            "verdict=PASS",
            $"average_goodput_bytes_per_second={averageGoodput}",
            $"min_goodput_bytes_per_second={minimumGoodput}",
            $"data_protocol_version={protocolVersion}",
            "v4_batch_ratio=1.0",
            $"v4_batch_ratio={v4BatchRatio.ToString("F6", CultureInfo.InvariantCulture)}",
            $"v4_average_bridge_payload_fill_percent={v4PayloadFillPercent.ToString("F3", CultureInfo.InvariantCulture)}",
            "v4_feedback_both_failed_count=0",
            "v4_sender_failed_count=0",
            "v4_receiver_failed_count=0",
            "legacy_data_protocol_started_count=0",
            "unexpected_legacy_data_frame_during_v4_count=0",
            "reorder_event_count=0",
            "request_timeout_count=0",
            "retry_requested_count=0",
            "payload_rejected_count=0",
            "decode_failure_count=0",
            "message_rejected_count=0",
            "bridge_bulk_send_failure_count=0",
            "bridge_bulk_queue_clear_count=0",
            $"bridge_bulk_queue_waiting_count={bridgeWaiting}",
            "bridge_bulk_queue_severe_count=0",
            "media_queue_drop_count=0",
            "media_send_failure_count=0",
            "media_queue_severe_count=0"
        ];
    }

    protected static readonly DateTimeOffset FakeLogStartUtc = new(2026, 4, 26, 11, 17, 58, TimeSpan.Zero);

    protected static string LogLine(string message, int secondsOffset = 0)
    {
        message = AddFixturePublicationAuthorityFields(message);
        return $"[{FakeLogStartUtc.AddSeconds(secondsOffset).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}] [INFO] [FileTransferTest] {message}";
    }

    private static string AddFixturePublicationAuthorityFields(string message)
    {
        const string skipFieldName = "fixture_skip_publication_authority_fields";
        if (!IsFixturePublicationAuthorityEvent(message))
        {
            return message;
        }

        if (HasFixtureLogField(message, skipFieldName))
        {
            return RemoveSemicolonLogField(message, skipFieldName);
        }

        var route = GetFixtureLogField(message, "route");
        var protocolVersion = GetFixtureLogField(message, "protocol_version");
        if (string.IsNullOrWhiteSpace(protocolVersion))
        {
            protocolVersion = string.Equals(route, "post_tuna_fallback_v6", StringComparison.OrdinalIgnoreCase) ? "6" : "4";
        }

        message = AppendFixtureLogFieldIfMissing(message, "live_route_epoch", "0");
        message = AppendFixtureLogFieldIfMissing(message, "runtime_generation", "1");
        message = AppendFixtureLogFieldIfMissing(message, "terminal_generation", "0");
        message = AppendFixtureLogFieldIfMissing(message, "leg_generation", "1");
        message = AppendFixtureLogFieldIfMissing(message, "route_publication_generation", "1");
        message = AppendFixtureLogFieldIfMissing(message, "coordinator_decision_reason", "fixture_authorized_publication");

        route = GetFixtureLogField(message, "route");
        var eventName = GetFixtureLogField(message, "event");
        var targetTransport = GetFixtureLogField(message, "target_transport");
        var selectionReason = GetFixtureLogField(message, "selection_reason");
        var handoffKind = GetFixtureLogField(message, "handoff_kind");
        var requiresCommitFields =
            string.Equals(eventName, "filetransfer_route_hint_authorized_publication_accepted", StringComparison.Ordinal) ||
            string.Equals(route, "file_tuna_v4", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetTransport, "tuna", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectionReason, "file_tuna_active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(handoffKind, "normal_to_tuna_activation", StringComparison.OrdinalIgnoreCase);
        if (requiresCommitFields)
        {
            var targetRoute = string.IsNullOrWhiteSpace(GetFixtureLogField(message, "target_route"))
                ? route
                : GetFixtureLogField(message, "target_route");
            var targetProtocolVersion = string.IsNullOrWhiteSpace(GetFixtureLogField(message, "target_protocol_version"))
                ? protocolVersion
                : GetFixtureLogField(message, "target_protocol_version");
            var transport = string.IsNullOrWhiteSpace(targetTransport)
                ? (string.Equals(route, "file_tuna_v4", StringComparison.OrdinalIgnoreCase) ? "tuna" : "regular_nkn")
                : targetTransport;

            message = AppendFixtureLogFieldIfMissing(message, "route_commit_generation", "1");
            message = AppendFixtureLogFieldIfMissing(message, "target_route", targetRoute);
            message = AppendFixtureLogFieldIfMissing(message, "target_protocol_version", targetProtocolVersion);
            message = AppendFixtureLogFieldIfMissing(message, "target_transport", transport);
        }

        return message;
    }

    private static bool IsFixturePublicationAuthorityEvent(string message)
    {
        var eventName = GetFixtureLogField(message, "event");
        return eventName is
            "filetransfer_route_selected" or
            "filetransfer_live_route_epoch_started" or
            "filetransfer_live_route_epoch_recovered" or
            "filetransfer_runtime_started" or
            "filetransfer_route_transitioned" or
            "filetransfer_route_hint_authorized_publication_accepted";
    }

    private static string AppendFixtureLogFieldIfMissing(string message, string fieldName, string value)
        => HasFixtureLogField(message, fieldName)
            ? message
            : $"{message}; {fieldName}={value}";

    private static bool HasFixtureLogField(string message, string fieldName)
        => !string.IsNullOrWhiteSpace(GetFixtureLogField(message, fieldName));

    private static string GetFixtureLogField(string message, string fieldName)
    {
        foreach (var part in message.Split("; ", StringSplitOptions.None))
        {
            if (part.StartsWith(fieldName + "=", StringComparison.Ordinal))
            {
                return part[(fieldName.Length + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    protected static string RetimestampLogLine(string line, int secondsOffset)
        => Regex.Replace(
            line,
            @"^\[[^\]]+\]",
            $"[{FakeLogStartUtc.AddSeconds(secondsOffset).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}]");

    protected static string RemoveSemicolonLogField(string line, string fieldName)
        => string.Join(
            "; ",
            line.Split("; ", StringSplitOptions.None)
                .Where(part => !part.StartsWith(fieldName + "=", StringComparison.Ordinal)));

    protected static List<string> StretchTransferWindowForWarningRate(IEnumerable<string> lines, int terminalOffsetSeconds = 120)
    {
        return lines
            .Select(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) ||
                            line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal)
                ? RetimestampLogLine(line, terminalOffsetSeconds)
                : line)
            .ToList();
    }

    protected static async Task<AnalyzeFixtureResult> RunAnalyzeFixtureAsync(
        IReadOnlyList<string> logLines,
        IReadOnlyList<string>? extraArguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops", Guid.NewGuid().ToString("N"));
        var logPath = Path.Combine(tempRoot, "nlink.log");
        var artifactDir = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(tempRoot);
        await File.WriteAllLinesAsync(logPath, logLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var args = new List<string>
        {
            "-Mode",
            "AnalyzeRetained",
            "-LogPath",
            logPath,
            "-ArtifactDir",
            artifactDir
        };
        if (extraArguments is not null)
        {
            args.AddRange(extraArguments);
        }

        var script = await RunFileTransferOpsAsync(repoRoot, args, environment);
        return new AnalyzeFixtureResult(tempRoot, artifactDir, script);
    }

    protected static async Task<ScriptResult> RunFileTransferOpsAsync(string repoRoot, IReadOnlyList<string> arguments)
        => await RunFileTransferOpsAsync(repoRoot, arguments, environment: null);

    protected static async Task<ScriptResult> RunFileTransferOpsAsync(
        string repoRoot,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var scriptPath = Path.Combine(repoRoot, "tools", "FileTransfer-Ops.ps1");
        return await RunPowerShellFileAsync(repoRoot, scriptPath, arguments, environment);
    }

    protected static async Task<ScriptResult> RunPowerShellFileAsync(
        string workingDirectory,
        string scriptPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            }
        };

        ApplyProcessEnvironment(process.StartInfo, environment);

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        if (await Task.WhenAny(exitTask, Task.Delay(ScriptProcessTimeout)) != exitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup; the timeout result below carries the useful diagnostics.
            }

            var stdout = await WaitForScriptOutputAsync(stdoutTask);
            var stderr = await WaitForScriptOutputAsync(stderrTask);
            var timeoutMessage = $"PowerShell script timed out after {ScriptProcessTimeout.TotalSeconds:F0}s: {scriptPath}";
            return new ScriptResult(
                -1,
                stdout,
                string.IsNullOrWhiteSpace(stderr) ? timeoutMessage : $"{timeoutMessage}{Environment.NewLine}{stderr}");
        }

        return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    protected static async Task<string> WaitForScriptOutputAsync(Task<string> outputTask)
    {
        if (await Task.WhenAny(outputTask, Task.Delay(ScriptProcessOutputDrainTimeout)) == outputTask)
        {
            return await outputTask;
        }

        return string.Empty;
    }

    protected static Dictionary<string, string> BuildFakeLiveNknEnvironment(double? fakeGoodputBytesPerSecond = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NLINK_FILETRANSFER_NKN_SOAK_FAKE_GUI"] = "1"
        };

        if (fakeGoodputBytesPerSecond.HasValue)
        {
            environment["NLINK_FILETRANSFER_NKN_SOAK_FAKE_GOODPUT_BPS"] = fakeGoodputBytesPerSecond.Value.ToString("R", CultureInfo.InvariantCulture);
        }

        return environment;
    }

    protected static Dictionary<string, string> BuildFakeRouteAcceptanceEnvironment(string timestamp)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_GUI"] = "1",
            ["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_TIMESTAMP"] = timestamp
        };
    }

    protected static void AssertRequiredLiveArtifacts(string artifactDir)
    {
        foreach (var artifactName in RequiredLiveNknArtifactFiles.Concat(RequiredArtifactFiles))
        {
            Assert.True(
                File.Exists(Path.Combine(artifactDir, artifactName)),
                $"Expected live NKN artifact to exist: {Path.Combine(artifactDir, artifactName)}");
        }
    }

    protected static void AssertRequiredPhase4RouteAcceptanceArtifacts(string runRoot)
    {
        Assert.True(File.Exists(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt")), "Expected Phase 4 route acceptance text summary.");
        Assert.True(File.Exists(Path.Combine(runRoot, "phase4-ab-acceptance-summary.json")), "Expected Phase 4 route acceptance JSON summary.");
        foreach (var directoryName in RequiredPhase4RouteAcceptanceSubdirectories)
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(Directory.Exists(artifactDir), $"Expected Phase 4 route acceptance subdirectory: {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")), $"Expected retained log slice in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-route-consistency-summary.txt")), $"Expected route consistency summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "transfer-terminal-summary.txt")), $"Expected terminal summary in {artifactDir}");
        }

        Assert.True(File.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb", "filetransfer-live-nkn-summary.txt")), "Expected Phase 4 regular NKN summary.");
        foreach (var directoryName in RequiredPhase4RouteAcceptanceSubdirectories.Where(static name => name != "regular-nkn-v4-64mb"))
        {
            Assert.True(File.Exists(Path.Combine(runRoot, directoryName, "filetransfer-tuna-gui-summary.json")), $"Expected Phase 4 Tuna GUI summary in {directoryName}.");
        }
    }

    protected static void AssertRequiredPhase5RouteAcceptanceArtifacts(string runRoot)
    {
        Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), "Expected Phase 5 route acceptance text summary.");
        Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "Expected Phase 5 route acceptance JSON summary.");
        foreach (var directoryName in RequiredPhase5RouteAcceptanceSubdirectories)
        {
            var artifactDir = Path.Combine(runRoot, directoryName);
            Assert.True(Directory.Exists(artifactDir), $"Expected Phase 5 route acceptance subdirectory: {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log")), $"Expected retained log slice in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "filetransfer-route-consistency-summary.txt")), $"Expected route consistency summary in {artifactDir}");
            Assert.True(File.Exists(Path.Combine(artifactDir, "transfer-terminal-summary.txt")), $"Expected terminal summary in {artifactDir}");
        }

        Assert.True(File.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb", "filetransfer-live-nkn-summary.txt")), "Expected Phase 5 regular NKN summary.");
        foreach (var directoryName in RequiredPhase5RouteAcceptanceSubdirectories.Where(static name => name != "regular-nkn-v4-64mb"))
        {
            Assert.True(File.Exists(Path.Combine(runRoot, directoryName, "filetransfer-tuna-gui-summary.json")), $"Expected Phase 5 Tuna GUI summary in {directoryName}.");
        }

        Assert.False(Directory.Exists(Path.Combine(runRoot, "live-multi-toggle-off-on-off-64mb")), "Phase 5 should use the canonical 256 MiB repeated-toggle stress instead of the old 64 MiB multi-toggle row.");
    }

    protected static void ApplyProcessEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }
    }

    protected static async Task<ScriptResult> RunParserAsync(string scriptPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops-parse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var parserHarnessPath = Path.Combine(tempRoot, "parse-filetransfer-ops.ps1");
            await File.WriteAllTextAsync(parserHarnessPath, BuildParserHarness(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(parserHarnessPath);
            process.StartInfo.ArgumentList.Add(scriptPath);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(ScriptProcessTimeout)) != exitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup; the timeout result below carries the useful diagnostics.
                }

                var stdout = await WaitForScriptOutputAsync(stdoutTask);
                var stderr = await WaitForScriptOutputAsync(stderrTask);
                var timeoutMessage = $"PowerShell parser harness timed out after {ScriptProcessTimeout.TotalSeconds:F0}s: {scriptPath}";
                return new ScriptResult(
                    -1,
                    stdout,
                    string.IsNullOrWhiteSpace(stderr) ? timeoutMessage : $"{timeoutMessage}{Environment.NewLine}{stderr}");
            }

            return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    protected static async Task<ScriptResult> RunPowerShellScriptTextAsync(string scriptText, IReadOnlyList<string> arguments)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var scriptPath = Path.Combine(tempRoot, "harness.ps1");
            await File.WriteAllTextAsync(scriptPath, scriptText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(ScriptProcessTimeout)) != exitTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup; the timeout result below carries the useful diagnostics.
                }

                var stdout = await WaitForScriptOutputAsync(stdoutTask);
                var stderr = await WaitForScriptOutputAsync(stderrTask);
                var timeoutMessage = $"PowerShell harness timed out after {ScriptProcessTimeout.TotalSeconds:F0}s: {scriptPath}";
                return new ScriptResult(
                    -1,
                    stdout,
                    string.IsNullOrWhiteSpace(stderr) ? timeoutMessage : $"{timeoutMessage}{Environment.NewLine}{stderr}");
            }

            return new ScriptResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    protected static Dictionary<string, string> ReadArtifactReport(string artifactDir, string fileName)
    {
        var reportPath = Path.Combine(artifactDir, fileName);
        Assert.True(File.Exists(reportPath), $"Expected artifact report: {reportPath}");

        return File.ReadAllLines(reportPath)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);
    }

    protected static string[] ExtractTopLevelPowerShellParameterNames(string scriptText)
    {
        var match = Regex.Match(scriptText, @"(?s)^param\((?<body>.*?)\)\s*Set-StrictMode");
        Assert.True(match.Success, "Could not find top-level param block before Set-StrictMode.");

        return Regex.Matches(match.Groups["body"].Value, @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups["name"].Value)
            .Where(name => !string.Equals(name, "true", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(name, "false", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    protected static string[] ExtractPowerShellValidateSetValues(string scriptText, string parameterName)
    {
        var pattern = @"\[ValidateSet\((?<body>[^)]*)\)\]\s*\[string\]\$" + Regex.Escape(parameterName) + @"\b";
        var match = Regex.Match(scriptText, pattern);
        Assert.True(match.Success, $"Could not find ValidateSet for ${parameterName}.");

        return Regex.Matches(match.Groups["body"].Value, @"[""'](?<value>[^""']+)[""']")
            .Select(match => match.Groups["value"].Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    protected static string ExtractPowerShellFunctionBody(string scriptText, string functionName, string nextFunctionName)
    {
        var pattern = @"(?s)function\s+" + Regex.Escape(functionName) + @"\s*\{(?<body>.*?)\r?\n\}\s*\r?\n\s*function\s+" + Regex.Escape(nextFunctionName) + @"\b";
        var match = Regex.Match(scriptText, pattern);
        Assert.True(match.Success, $"Could not find PowerShell function body for {functionName}.");

        return match.Groups["body"].Value;
    }

    protected static string BuildParserHarness()
    {
        return """
param(
    [Parameter(Mandatory = $true)]
    [string]$ScriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$errors) | Out-Null

if ($errors -and $errors.Count -gt 0) {
    foreach ($error in $errors) {
        Write-Error ("{0}:{1}:{2} {3}" -f $error.Extent.File, $error.Extent.StartLineNumber, $error.Extent.StartColumnNumber, $error.Message)
    }

    exit 1
}
""";
    }

    protected static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "nLink.sln")) &&
                File.Exists(Path.Combine(current.FullName, "VERSION")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root.");
    }

    protected static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }

    protected sealed record AnalyzeFixtureResult(string TempRoot, string ArtifactDir, ScriptResult Script);

    protected sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
