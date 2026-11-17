package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestDeliveryTraceWireVectors(t *testing.T) {
	vectors := map[string]string{
		"":                                   "f828ecd36fc7cae8effda283dae5531e671624f1df4d156ac30834e9cd3c56cc",
		`{"messageId":"same","payload":"a"}`: "a54b75e512ad68c229a6c504c685abf07d88875e9a26dd7d6ab3182aa8321919",
		`{"messageId":"same","payload":"b"}`: "b8ba54dbe5567eae4a6df25c1f9e794e1e986eeb7329a0b9a97f9298f67caa9e",
	}
	for input, expected := range vectors {
		if got := deliveryWireHash([]byte(input)); got != "ftwire1-"+expected {
			t.Fatalf("wire vector mismatch: %s", got)
		}
	}
}

func TestDeliveryTraceBridgeWriteControls(t *testing.T) {
	for _, failWrite := range []bool{false, true} {
		t.Run(map[bool]string{false: "forward", true: "write_failure"}[failWrite], func(t *testing.T) {
			var output bytes.Buffer
			emit := &emitter{jsonl: true, out: &output}
			producer := &deliveryTraceProducer{capture: "11111111-2222-3333-4444-555555555555", id: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", emit: emit}
			for _, direction := range []string{"app_to_tuna", "tuna_to_app"} {
				source, app := net.Pipe()
				destination, peer := net.Pipe()
				ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
				defer cancel()
				defer source.Close()
				defer app.Close()
				defer destination.Close()
				defer peer.Close()
				_ = app.SetDeadline(time.Now().Add(3 * time.Second))
				_ = peer.SetDeadline(time.Now().Add(3 * time.Second))
				if failWrite {
					_ = peer.Close()
				}
				var total atomic.Int64
				var handoff atomic.Bool
				results := make(chan bridgeDirectionResult, 1)
				go bridgeDirection(ctx, "dialer", direction, source, destination, &total, 1<<30, 0, &handoff, emit, results, producer.connect())
				payload := []byte(`{"messageId":"same","payload":"a"}`)
				if err := writeFrame(app, frameTypeData, 2, 1, 0, payload); err != nil {
					t.Fatal(err)
				}
				if !failWrite {
					frame, err := readFrame(peer)
					if err != nil || !bytes.Equal(frame.payload, payload) || frame.seq != 1 {
						t.Fatalf("forward: %v", err)
					}
				}
				_ = app.Close()
				select {
				case result := <-results:
					if failWrite && result.stage != "write" {
						t.Fatalf("expected write failure: %+v", result)
					}
				case <-ctx.Done():
					t.Fatal("bridge did not stop")
				}
			}
			records := deliveryRecords(t, output.Bytes())
			for _, stage := range []string{"read_completed", "write_entered", map[bool]string{false: "write_completed", true: "write_failed"}[failWrite]} {
				count := 0
				for _, record := range records {
					if record["trace_stage"] == stage {
						count++
						if record["trace_wire"] != deliveryWireHash([]byte(`{"messageId":"same","payload":"a"}`)) {
							t.Fatal("wrong wire identity")
						}
					}
					if failWrite && record["trace_stage"] == "write_completed" {
						t.Fatal("invented completed write")
					}
				}
				if count != 2 {
					t.Fatalf("%s count: %d", stage, count)
				}
			}
		})
	}
}

func TestDeliveryTraceDisabledCapsAndAbruptTail(t *testing.T) {
	t.Setenv("NLINK_TUNA_DELIVERY_TRACE", "")
	t.Setenv("NLINK_FILETRANSFER_TRACE", "1")
	if newDeliveryTraceProducer(&emitter{}) != nil {
		t.Fatal("trace must be default off")
	}
	t.Setenv("NLINK_TUNA_DELIVERY_TRACE", "1")
	t.Setenv("NLINK_TUNA_DELIVERY_CAPTURE_ID", "11111111-2222-3333-4444-555555555555")
	var output bytes.Buffer
	p := newDeliveryTraceProducer(&emitter{jsonl: true, out: &output})
	c := p.connect()
	for _, direction := range []string{"app_to_tuna", "tuna_to_app"} {
		for i := 1; i <= 65; i++ {
			f := c.selectFrame(direction, sidecarFrame{typ: frameTypeData, lane: 2, seq: 1, payload: []byte{1}})
			if (f != nil) != (i <= 64) {
				t.Fatal("frame cap")
			}
		}
	}
	for i := 0; i < 3; i++ {
		if p.connect() == nil {
			t.Fatal("early connection cap")
		}
	}
	if p.connect() != nil {
		t.Fatal("missing connection cap")
	}
	for i := 0; i < 5000; i++ {
		c.record("app_to_tuna", "write_entered", "none", nil)
	}
	records := deliveryRecords(t, output.Bytes())
	if len(records) > 4096 || output.Len() > 4*1024*1024 {
		t.Fatal("producer cap")
	}
	if !strings.Contains(output.String(), "record_or_byte_cap") {
		t.Fatal("missing incomplete marker")
	}
	if strings.Contains(output.String(), "direction_end") || strings.Contains(output.String(), `"trace_tail":"complete"`) {
		t.Fatal("invented abrupt-stop completion")
	}
}

func TestDeliveryTraceIncompletePreservesTerminalReason(t *testing.T) {
	for _, fault := range []string{"write_error", "short_write", "record_cap"} {
		t.Run(fault, func(t *testing.T) {
			output := &deliveryTraceFaultWriter{fault: fault}
			p := &deliveryTraceProducer{capture: "11111111-2222-3333-4444-555555555555",
				id: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", emit: &emitter{jsonl: true, out: output}}
			c := p.connect()
			if fault == "record_cap" {
				p.records = 4096 - 32
			}
			c.record("app_to_tuna", "read_completed", "none", &deliveryTraceFrame{1, 1, deliveryWireHash([]byte{1}), 1})
			if !p.incomplete {
				t.Fatal("fault did not mark the trace incomplete")
			}
			c.record("app_to_tuna", "direction_end", "tuna_write_failed", nil)
			records := deliveryRecords(t, output.Bytes())
			last := records[len(records)-1]
			if last["trace_stage"] != "direction_end" || last["trace_reason"] != "tuna_write_failed" {
				t.Fatalf("terminal reason lost after %s: %+v", fault, last)
			}
			if last["trace_incomplete"] != true || last["trace_tail"] != "unproved" {
				t.Fatal("terminal reason preservation must not claim a complete trace")
			}
		})
	}
}

type deliveryTraceFaultWriter struct {
	bytes.Buffer
	fault  string
	writes int
}

func (w *deliveryTraceFaultWriter) Write(p []byte) (int, error) {
	w.writes++
	if w.writes == 1 {
		if w.fault == "write_error" {
			return 0, io.ErrClosedPipe
		}
		if w.fault == "short_write" {
			return 0, nil
		}
	}
	return w.Buffer.Write(p)
}

func deliveryRecords(t *testing.T, data []byte) []map[string]any {
	t.Helper()
	decoder := json.NewDecoder(bytes.NewReader(data))
	var result []map[string]any
	for {
		var record map[string]any
		if err := decoder.Decode(&record); err == io.EOF {
			break
		} else if err != nil {
			t.Fatal(err)
		}
		if record["event"] == "tuna_delivery_trace" {
			result = append(result, record)
		}
	}
	return result
}
