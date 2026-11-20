package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type writeEnteredConn struct {
	net.Conn
	entered chan struct{}
	once    sync.Once
}

func (c *writeEnteredConn) Write(p []byte) (int, error) {
	c.once.Do(func() { close(c.entered) })
	return c.Conn.Write(p)
}

func TestBridgeSharedByteCapIncludesOppositeInFlightWrite(t *testing.T) {
	appConn, app := net.Pipe()
	tunaConn, peer := net.Pipe()
	defer app.Close()
	defer appConn.Close()
	defer peer.Close()
	defer tunaConn.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	defer cancel()
	deadline, _ := ctx.Deadline()
	app.SetDeadline(deadline)
	peer.SetDeadline(deadline)
	tuna := &writeEnteredConn{Conn: tunaConn, entered: make(chan struct{})}
	emit := &emitter{jsonl: true, out: io.Discard}
	budget := bridgeByteBudget{limit: 8}
	var handoff atomic.Bool
	results := make(chan bridgeDirectionResult, 2)
	go bridgeDirection(ctx, "listener", "app_to_tuna", appConn, tuna, &budget, 0, &handoff, emit, results, nil)
	go bridgeDirection(ctx, "listener", "tuna_to_app", tuna, appConn, &budget, 0, &handoff, emit, results, nil)
	first := []byte("first")
	if err := writeFrame(app, frameTypeData, 2, 1, 0, first); err != nil {
		t.Fatal(err)
	}
	select {
	case <-tuna.entered: // Five bytes admitted, none delivered; peer is not reading.
	case <-ctx.Done():
		t.Fatal("first direction did not enter write")
	}
	if err := writeFrame(peer, frameTypeData, 2, 1, 0, []byte("four")); err != nil {
		t.Fatal(err)
	}
	select {
	case result := <-results:
		if result.direction != "tuna_to_app" || result.stage != "cap" || result.framesForwarded != 0 || budget.forwarded.Load() != 0 {
			t.Fatalf("expected rejected reverse frame and no forwarded payload: %+v", result)
		}
	case <-ctx.Done():
		t.Fatal("reverse frame was not rejected before writing")
	}
	if frame, err := readFrame(peer); err != nil || !bytes.Equal(frame.payload, first) {
		t.Fatalf("first frame changed: %v", err)
	}
	if err := writeFrame(app, frameTypeData, 2, 2, 0, []byte("end")); err != nil {
		t.Fatal(err)
	}
	if frame, err := readFrame(peer); err != nil || string(frame.payload) != "end" {
		t.Fatalf("exact cap should be allowed: %v", err)
	}
	app.Close()
	select {
	case result := <-results:
		if result.direction != "app_to_tuna" || result.framesForwarded != 2 || budget.forwarded.Load() != 8 {
			t.Fatalf("incorrect final accounting: %+v, forwarded %d", result, budget.forwarded.Load())
		}
	case <-ctx.Done():
		t.Fatal("first direction did not finish")
	}
}

func TestBridgeDurationCapClosesBlockedAppAndEmitsSummary(t *testing.T) {
	appConn, app := net.Pipe()
	tunaConn, peer := net.Pipe()
	defer app.Close()
	defer appConn.Close()
	defer peer.Close()
	defer tunaConn.Close()
	blocked := &writeEnteredConn{Conn: appConn, entered: make(chan struct{})}
	var output bytes.Buffer
	done := make(chan error, 1)
	go func() {
		done <- bridgeConns(context.Background(), &config{maxDurationSec: 1}, blocked, tunaConn,
			&emitter{jsonl: true, out: &output}, "listener", nil, nil)
	}()
	peer.SetWriteDeadline(time.Now().Add(4 * time.Second))
	if err := writeFrame(peer, frameTypeData, 2, 1, 0, []byte("blocked")); err != nil {
		t.Fatal(err)
	}
	select {
	case <-blocked.entered:
	case <-time.After(2 * time.Second):
		t.Fatal("forwarding did not enter the unread app connection")
	}
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(4 * time.Second):
		t.Fatal("unread app connection prevented bounded shutdown")
	}
	for _, endpoint := range []net.Conn{app, peer} {
		endpoint.SetReadDeadline(time.Now().Add(time.Second))
		if _, err := endpoint.Read(make([]byte, 1)); err != io.EOF {
			t.Fatalf("endpoint not closed: %v", err)
		}
	}
	dec := json.NewDecoder(&output)
	summaries := 0
	for {
		var record event
		if err := dec.Decode(&record); err == io.EOF {
			break
		} else if err != nil {
			t.Fatal(err)
		}
		if record["event"] == "summary" {
			summaries++
			if record["capReached"] != true || record["capReason"] != "duration_cap_reached" || record["bytesMoved"] != float64(0) {
				t.Fatalf("incorrect capped summary: %+v", record)
			}
		}
	}
	if summaries != 1 {
		t.Fatalf("got %d summaries; want one", summaries)
	}
}
