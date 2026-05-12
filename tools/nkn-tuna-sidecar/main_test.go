package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"strings"
	"testing"
	"time"
)

func TestBridgeTerminalReasonMapsCloseClasses(t *testing.T) {
	tests := []struct {
		name      string
		direction string
		stage     string
		err       error
		want      string
	}{
		{name: "local ipc eof", direction: "app_to_tuna", stage: "read", err: io.EOF, want: "local_ipc_eof"},
		{name: "tuna stream eof", direction: "tuna_to_app", stage: "read", err: io.EOF, want: "tuna_stream_eof"},
		{name: "tuna write failed", direction: "app_to_tuna", stage: "write", err: errors.New("write failed"), want: "tuna_write_failed"},
		{name: "local write failed", direction: "tuna_to_app", stage: "write", err: errors.New("write failed"), want: "local_write_failed"},
		{name: "closed tuna write", direction: "app_to_tuna", stage: "write", err: net.ErrClosed, want: "tuna_write_failed"},
		{name: "closed local write", direction: "tuna_to_app", stage: "write", err: net.ErrClosed, want: "local_write_failed"},
		{name: "byte cap", direction: "app_to_tuna", stage: "cap", err: errors.New("byte cap reached"), want: "byte_cap_reached"},
		{name: "duration cap", direction: "app_to_tuna", stage: "context", err: context.DeadlineExceeded, want: "duration_cap_reached"},
		{name: "context cancel", direction: "app_to_tuna", stage: "context", err: context.Canceled, want: "context_cancelled"},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := bridgeTerminalReason(tt.direction, tt.stage, tt.err); got != tt.want {
				t.Fatalf("bridgeTerminalReason() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestVersionModeEmitsCompatibilityMetadataWithoutNetworkOrWallet(t *testing.T) {
	var output bytes.Buffer
	emit := &emitter{jsonl: true}
	emit.out = &output

	if err := run([]string{modeVersion, "--jsonl"}, emit); err != nil {
		t.Fatalf("run(version) error = %v", err)
	}

	var evt map[string]any
	if err := json.Unmarshal(bytes.TrimSpace(output.Bytes()), &evt); err != nil {
		t.Fatalf("decode version event: %v; output=%q", err, output.String())
	}

	if evt["event"] != "sidecar_version" {
		t.Fatalf("event = %#v, want sidecar_version", evt["event"])
	}
	if evt["sidecarVersion"] != sidecarVersion {
		t.Fatalf("sidecarVersion = %#v, want %q", evt["sidecarVersion"], sidecarVersion)
	}
	if evt["appProtocolVersion"] != float64(appProtocolVersion) {
		t.Fatalf("appProtocolVersion = %#v, want %d", evt["appProtocolVersion"], appProtocolVersion)
	}
	if evt["frameProtocolVersion"] != float64(frameVersion) {
		t.Fatalf("frameProtocolVersion = %#v, want %d", evt["frameProtocolVersion"], frameVersion)
	}
	if evt["runtime"] == "" {
		t.Fatalf("runtime missing from event: %#v", evt)
	}
}

func TestBridgeLaneSequenceSummaries(t *testing.T) {
	stats := map[byte]*bridgeLaneSequenceStats{}
	media := bridgeLaneStatsFor(stats, 1)
	media.gapCount = 2
	media.gapMissing = 5
	media.lastPreviousSeq = 10
	media.lastCurrentSeq = 16
	bulk := bridgeLaneStatsFor(stats, 2)
	bulk.reorderCount = 1
	bulk.lastPreviousSeq = 8
	bulk.lastCurrentSeq = 7

	summaries := bridgeLaneSequenceSummaries(stats)
	if len(summaries) != 2 {
		t.Fatalf("len(summaries) = %d, want 2", len(summaries))
	}

	seen := map[string]event{}
	for _, summary := range summaries {
		lane, _ := summary["frameLane"].(string)
		seen[lane] = summary
	}
	if seen["media"]["seqGapCount"] != int64(2) || seen["media"]["seqGapMissing"] != int64(5) {
		t.Fatalf("media summary = %#v", seen["media"])
	}
	if seen["bulk"]["seqReorderCount"] != int64(1) {
		t.Fatalf("bulk summary = %#v", seen["bulk"])
	}
}

func TestProviderPathReadinessStateEmitsAcceptedRecoveredAndStillDegraded(t *testing.T) {
	degradedPaths := event{
		"usableCount": int64(3),
		"pathsHash":   "degraded-hash",
		"paths": []event{
			{"index": 0, "usable": true, "ipHash": "a"},
			{"index": 1, "usable": true, "ipHash": "b"},
			{"index": 2, "usable": true, "ipHash": "c"},
		},
	}
	recoveredPaths := event{
		"usableCount": int64(4),
		"pathsHash":   "recovered-hash",
	}
	var output bytes.Buffer
	emit := &emitter{jsonl: true, out: &output}
	state := newProviderPathReadinessState()

	state.markDegradedAccepted(emit, "listener", degradedPaths, 1, 1, time.Now().Add(-time.Second))
	state.observePaths(emit, "listener", recoveredPaths, "changed")

	text := output.String()
	if !strings.Contains(text, `"event":"provider_paths_degraded_accepted"`) {
		t.Fatalf("missing degraded accepted event: %s", text)
	}
	if !strings.Contains(text, `"event":"provider_paths_recovered"`) {
		t.Fatalf("missing recovered event: %s", text)
	}
	if !strings.Contains(text, `"pathsHash":"degraded-hash"`) || !strings.Contains(text, `"pathsHash":"recovered-hash"`) {
		t.Fatalf("missing path hashes: %s", text)
	}

	summary := providerPathReadinessSummary(state)
	if summary["degradedAccepted"] != true || summary["recovered"] != true || summary["stillDegraded"] != false {
		t.Fatalf("summary = %#v", summary)
	}
}

func TestProviderPathReadinessStateEmitsStillDegradedOnce(t *testing.T) {
	paths := event{
		"usableCount": int64(3),
		"pathsHash":   "degraded-hash",
	}
	var output bytes.Buffer
	emit := &emitter{jsonl: true, out: &output}
	state := newProviderPathReadinessState()

	state.markDegradedAccepted(emit, "listener", paths, 1, 1, time.Now().Add(-time.Second))
	state.emitStillDegradedIfNeeded(emit, "listener", "bridge_summary")
	state.emitStillDegradedIfNeeded(emit, "listener", "bridge_summary")

	text := output.String()
	if count := strings.Count(text, `"event":"provider_paths_still_degraded"`); count != 1 {
		t.Fatalf("still degraded event count = %d; output=%s", count, text)
	}
	summary := providerPathReadinessSummary(state)
	if summary["degradedAccepted"] != true || summary["recovered"] != false || summary["stillDegraded"] != true {
		t.Fatalf("summary = %#v", summary)
	}
}

func TestCapHandoffSoftLimitBytesKeepsReserveBeforeHardCap(t *testing.T) {
	tests := []struct {
		name     string
		limit    int64
		hasCap   bool
		want     int64
		wantZero bool
	}{
		{name: "disabled without explicit cap", limit: 1 << 62, hasCap: false, wantZero: true},
		{name: "small cap keeps quarter reserve", limit: 16 * 1024 * 1024, hasCap: true, want: 12 * 1024 * 1024},
		{name: "normal cap uses five percent reserve", limit: 256 * 1024 * 1024, hasCap: true, want: 256*1024*1024 - 12*1024*1024 - 838860}, // 5% integer floor
		{name: "large cap clamps reserve", limit: 2048 * 1024 * 1024, hasCap: true, want: 2048*1024*1024 - 64*1024*1024},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := capHandoffSoftLimitBytes(tt.limit, tt.hasCap)
			if tt.wantZero {
				if got != 0 {
					t.Fatalf("capHandoffSoftLimitBytes() = %d, want 0", got)
				}
				return
			}
			if got != tt.want {
				t.Fatalf("capHandoffSoftLimitBytes() = %d, want %d", got, tt.want)
			}
			if got <= 0 || got >= tt.limit {
				t.Fatalf("soft limit %d should be inside hard cap %d", got, tt.limit)
			}
		})
	}
}

func TestBridgeSequenceTrackerTreatsInterleavedLanesAsContiguousGlobalSequence(t *testing.T) {
	tracker := &bridgeSequenceTracker{}
	frames := []sidecarFrame{
		{typ: frameTypeData, lane: 1, seq: 1},
		{typ: frameTypeData, lane: 2, seq: 2},
		{typ: frameTypeData, lane: 1, seq: 3},
		{typ: frameTypeData, lane: 2, seq: 4},
	}

	for _, frame := range frames {
		if observation := tracker.observe(frame); observation.kind != bridgeSequenceInOrder {
			t.Fatalf("observe(%+v) kind = %q, want in-order", frame, observation.kind)
		}
	}

	if tracker.seqGapCount != 0 || tracker.seqGapMissing != 0 || tracker.seqReorderCount != 0 {
		t.Fatalf(
			"tracker gaps = count %d missing %d reordered %d, want all zero",
			tracker.seqGapCount,
			tracker.seqGapMissing,
			tracker.seqReorderCount)
	}
	if summaries := bridgeLaneSequenceSummaries(tracker.laneStats); len(summaries) != 0 {
		t.Fatalf("lane summaries = %#v, want none", summaries)
	}
}

func TestBridgeSequenceTrackerRecordsActualGlobalGapOnCurrentLane(t *testing.T) {
	tracker := &bridgeSequenceTracker{}
	tracker.observe(sidecarFrame{typ: frameTypeData, lane: 1, seq: 1})
	tracker.observe(sidecarFrame{typ: frameTypeData, lane: 2, seq: 2})
	observation := tracker.observe(sidecarFrame{typ: frameTypeData, lane: 1, seq: 5})

	if observation.kind != bridgeSequenceGap {
		t.Fatalf("observation kind = %q, want gap", observation.kind)
	}
	if observation.previousSeq != 2 || observation.currentSeq != 5 || observation.missingCount != 2 {
		t.Fatalf("observation = %#v", observation)
	}
	if tracker.seqGapCount != 1 || tracker.seqGapMissing != 2 {
		t.Fatalf("tracker gaps = count %d missing %d, want 1 and 2", tracker.seqGapCount, tracker.seqGapMissing)
	}

	summaries := bridgeLaneSequenceSummaries(tracker.laneStats)
	if len(summaries) != 1 {
		t.Fatalf("len(summaries) = %d, want 1", len(summaries))
	}
	if summaries[0]["frameLane"] != "media" || summaries[0]["seqGapCount"] != int64(1) || summaries[0]["seqGapMissing"] != int64(2) {
		t.Fatalf("summary = %#v", summaries[0])
	}
}

func TestBridgeSequenceTrackerRecordsReorderedFrameWithoutAdvancingSequence(t *testing.T) {
	tracker := &bridgeSequenceTracker{}
	tracker.observe(sidecarFrame{typ: frameTypeData, lane: 1, seq: 1})
	tracker.observe(sidecarFrame{typ: frameTypeData, lane: 2, seq: 2})
	observation := tracker.observe(sidecarFrame{typ: frameTypeData, lane: 1, seq: 1})

	if observation.kind != bridgeSequenceReordered {
		t.Fatalf("observation kind = %q, want reordered", observation.kind)
	}
	if tracker.seqReorderCount != 1 {
		t.Fatalf("tracker seqReorderCount = %d, want 1", tracker.seqReorderCount)
	}

	next := tracker.observe(sidecarFrame{typ: frameTypeData, lane: 2, seq: 3})
	if next.kind != bridgeSequenceInOrder {
		t.Fatalf("next observation kind = %q, want in-order", next.kind)
	}
}

func TestDialDefaultsDoNotApplyLocalBridgeCaps(t *testing.T) {
	cfg, err := parseArgs([]string{modeDial, "--to", "remote-address"})
	if err != nil {
		t.Fatalf("parseArgs(dial) error = %v", err)
	}

	if cfg.maxTotalMiB != 0 {
		t.Fatalf("dial maxTotalMiB = %d, want 0", cfg.maxTotalMiB)
	}
	if cfg.maxDurationSec != 0 {
		t.Fatalf("dial maxDurationSec = %d, want 0", cfg.maxDurationSec)
	}
}

func TestDialOptionalLocalBridgeCaps(t *testing.T) {
	cfg, err := parseArgs([]string{
		modeDial,
		"--to", "remote-address",
		"--max-total-mib", "123",
		"--max-duration-sec", "456",
	})
	if err != nil {
		t.Fatalf("parseArgs(dial with caps) error = %v", err)
	}

	if cfg.maxTotalMiB != 123 {
		t.Fatalf("dial maxTotalMiB = %d, want 123", cfg.maxTotalMiB)
	}
	if cfg.maxDurationSec != 456 {
		t.Fatalf("dial maxDurationSec = %d, want 456", cfg.maxDurationSec)
	}
}

func TestPaidListenStillRequiresExplicitCaps(t *testing.T) {
	_, err := parseArgs([]string{
		modeListen,
		"--wallet", "wallet.json",
		"--password-stdin",
		"--allow-remote", "remote-address",
		"--max-price-nkn-per-mb", "0.0002",
	})
	if err == nil {
		t.Fatal("parseArgs(listen without caps) error = nil, want caps error")
	}

	cfg, err := parseArgs([]string{
		modeListen,
		"--wallet", "wallet.json",
		"--password-stdin",
		"--allow-remote", "remote-address",
		"--max-price-nkn-per-mb", "0.0002",
		"--max-total-mib", "512",
		"--max-duration-sec", "900",
	})
	if err != nil {
		t.Fatalf("parseArgs(listen with caps) error = %v", err)
	}

	if cfg.maxTotalMiB != 512 {
		t.Fatalf("listen maxTotalMiB = %d, want 512", cfg.maxTotalMiB)
	}
	if cfg.maxDurationSec != 900 {
		t.Fatalf("listen maxDurationSec = %d, want 900", cfg.maxDurationSec)
	}
	if cfg.providerReadyAttempts != 1 {
		t.Fatalf("listen providerReadyAttempts = %d, want 1", cfg.providerReadyAttempts)
	}
	if cfg.listenStartTimeoutSec != int(defaultListenStartTimeout.Seconds()) {
		t.Fatalf("listen listenStartTimeoutSec = %d, want %d", cfg.listenStartTimeoutSec, int(defaultListenStartTimeout.Seconds()))
	}
}

func TestPaidListenProviderReadyAttempts(t *testing.T) {
	cfg, err := parseArgs([]string{
		modeListen,
		"--wallet", "wallet.json",
		"--password-stdin",
		"--allow-remote", "remote-address",
		"--max-price-nkn-per-mb", "0.0002",
		"--max-total-mib", "512",
		"--max-duration-sec", "900",
		"--listen-start-timeout-sec", "30",
		"--require-provider-ready",
		"--provider-ready-attempts", "2",
	})
	if err != nil {
		t.Fatalf("parseArgs(listen with provider attempts) error = %v", err)
	}

	if !cfg.requireProviderReady {
		t.Fatal("requireProviderReady = false, want true")
	}
	if cfg.providerReadyAttempts != 2 {
		t.Fatalf("providerReadyAttempts = %d, want 2", cfg.providerReadyAttempts)
	}
	if cfg.listenStartTimeoutSec != 30 {
		t.Fatalf("listenStartTimeoutSec = %d, want 30", cfg.listenStartTimeoutSec)
	}
}
