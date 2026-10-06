package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// Observe the production read deadline; do not shorten it or depend on a sleep.
type timeoutObservedConn struct {
	net.Conn
	timedOut chan struct{}
}

func (c *timeoutObservedConn) Read(p []byte) (int, error) {
	n, err := c.Conn.Read(p)
	if isTimeout(err) {
		select {
		case c.timedOut <- struct{}{}:
		default:
		}
	}
	return n, err
}

func startFrameReadBridge(t *testing.T, direction string) (net.Conn, net.Conn, <-chan struct{}, <-chan bridgeDirectionResult, context.CancelFunc) {
	t.Helper()
	source, app := net.Pipe()
	destination, peer := net.Pipe()
	ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
	observed := &timeoutObservedConn{Conn: source, timedOut: make(chan struct{}, 1)}
	results := make(chan bridgeDirectionResult, 1)
	done := make(chan struct{})
	var total atomic.Int64
	var handoff atomic.Bool
	go func() {
		defer close(done)
		bridgeDirection(ctx, "dialer", direction, observed, destination, &total, 1<<30, 0, &handoff,
			&emitter{jsonl: true, out: io.Discard}, results, nil)
	}()
	_ = app.SetDeadline(time.Now().Add(3 * time.Second))
	_ = peer.SetDeadline(time.Now().Add(3 * time.Second))
	t.Cleanup(func() {
		cancel()
		_ = app.Close()
		_ = peer.Close()
		_ = source.Close()
		_ = destination.Close()
		select {
		case <-done:
		case <-time.After(time.Second):
			t.Error("bridge did not stop after cleanup")
		}
	})
	return app, peer, observed.timedOut, results, cancel
}

func TestBridgeFrameReadPreservesPartialFrameAcrossDeadline(t *testing.T) {
	for _, direction := range []string{"app_to_tuna", "tuna_to_app"} {
		for _, split := range []int{0, 2, 16, frameHeaderSize + 17} {
			t.Run(fmt.Sprintf("%s/split_%d", direction, split), func(t *testing.T) {
				app, peer, timedOut, results, _ := startFrameReadBridge(t, direction)
				payload := bytes.Repeat([]byte{0xa5}, 545)
				var encoded bytes.Buffer
				if err := writeFrame(&encoded, frameTypeData, 2, 1, 123, payload); err != nil {
					t.Fatal(err)
				}
				written := make(chan error, 1)
				go func() {
					err := writeFrame(app, frameTypePing, 0, 0, 0, nil)
					if err == nil && split > 0 {
						_, err = app.Write(encoded.Bytes()[:split])
					}
					if err == nil {
						select {
						case <-timedOut:
						case <-time.After(2 * time.Second):
							err = fmt.Errorf("production read deadline was not observed")
						}
					}
					if err == nil {
						_, err = app.Write(encoded.Bytes()[split:])
					}
					if err == nil {
						err = writeFrame(app, frameTypePong, 0, 2, 456, nil)
					}
					_ = app.Close()
					written <- err
				}()
				warmup, err := readFrame(peer)
				if err != nil || warmup.typ != frameTypePing {
					t.Fatalf("warmup: frame=%+v, err=%v", warmup, err)
				}
				probe, err := readFrame(peer)
				if err != nil || probe.typ != frameTypeData || probe.lane != 2 || probe.seq != 1 ||
					probe.timestamp != 123 || !bytes.Equal(probe.payload, payload) {
					t.Fatalf("545-byte probe lost or changed across deadline: type=%d seq=%d bytes=%d err=%v",
						probe.typ, probe.seq, len(probe.payload), err)
				}
				next, err := readFrame(peer)
				if err != nil || next.typ != frameTypePong || next.seq != 2 || next.timestamp != 456 || len(next.payload) != 0 {
					t.Fatalf("next frame changed or duplicate probe: frame=%+v, err=%v", next, err)
				}
				if err := <-written; err != nil {
					t.Fatal(err)
				}
				select {
				case result := <-results:
					if result.framesForwarded != 3 || result.payloadBytes != 545 || result.err != io.EOF {
						t.Fatalf("unexpected forwarding/terminal result: %+v", result)
					}
				case <-time.After(time.Second):
					t.Fatal("bridge did not stop at EOF")
				}
			})
		}
	}
}

func TestBridgeFrameReadCancellationAndTruncation(t *testing.T) {
	var encoded bytes.Buffer
	if err := writeFrame(&encoded, frameTypeData, 2, 1, 123, bytes.Repeat([]byte{0xa5}, 545)); err != nil {
		t.Fatal(err)
	}
	for _, direction := range []string{"app_to_tuna", "tuna_to_app"} {
		for _, split := range []int{0, 2, 16, frameHeaderSize + 17} {
			for _, cancelled := range []bool{false, true} {
				t.Run(fmt.Sprintf("%s/split_%d/cancel_%t", direction, split, cancelled), func(t *testing.T) {
					app, _, timedOut, results, cancel := startFrameReadBridge(t, direction)
					if split > 0 {
						if _, err := app.Write(encoded.Bytes()[:split]); err != nil {
							t.Fatal(err)
						}
					}
					select {
					case <-timedOut:
					case <-time.After(2 * time.Second):
						t.Fatal("production read deadline was not observed")
					}
					wantStage, wantErr := "read", io.ErrUnexpectedEOF
					if cancelled {
						cancel()
						wantStage, wantErr = "context", context.Canceled
					} else {
						_ = app.Close()
						if split == 0 {
							wantErr = io.EOF
						}
					}
					select {
					case result := <-results:
						if result.stage != wantStage || !errors.Is(result.err, wantErr) || result.framesForwarded != 0 || result.payloadBytes != 0 {
							t.Fatalf("partial frame must not escape: %+v, want stage=%s err=%v", result, wantStage, wantErr)
						}
					case <-time.After(2 * time.Second):
						t.Fatal("bridge did not stop within one polling interval plus scheduling allowance")
					}
				})
			}
		}
	}
}

func TestBridgeFrameReadRejectsInvalidHeader(t *testing.T) {
	for _, invalid := range []string{"version", "payload_size"} {
		t.Run(invalid, func(t *testing.T) {
			app, _, _, results, _ := startFrameReadBridge(t, "tuna_to_app")
			var encoded bytes.Buffer
			if err := writeFrame(&encoded, frameTypeData, 2, 1, 123, nil); err != nil {
				t.Fatal(err)
			}
			want := "unsupported frame version"
			if invalid == "version" {
				encoded.Bytes()[4] = frameVersion + 1
			} else {
				binary.BigEndian.PutUint32(encoded.Bytes()[24:28], frameMaxPayloadSize+1)
				want = "frame payload too large"
			}
			if _, err := app.Write(encoded.Bytes()); err != nil {
				t.Fatal(err)
			}
			select {
			case result := <-results:
				if result.stage != "read" || result.err == nil || !strings.Contains(result.err.Error(), want) || result.framesForwarded != 0 {
					t.Fatalf("invalid header accepted or misclassified: %+v", result)
				}
			case <-time.After(time.Second):
				t.Fatal("invalid header was not rejected")
			}
		})
	}
}

// io.Reader may return both bytes and an error. Keep those bytes on timeout too.
type bytesAndTimeoutConn struct{ net.Conn }

func (c bytesAndTimeoutConn) Read(p []byte) (int, error) {
	n, err := c.Conn.Read(p[:min(len(p), 2)])
	if n > 0 {
		return n, os.ErrDeadlineExceeded
	}
	return n, err
}

func TestBridgeFrameReaderKeepsBytesReturnedWithTimeout(t *testing.T) {
	source, app := net.Pipe()
	defer source.Close()
	defer app.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	_ = app.SetWriteDeadline(time.Now().Add(time.Second))
	payload := bytes.Repeat([]byte{0xa5}, 545)
	written := make(chan error, 1)
	go func() { written <- writeFrame(app, frameTypeData, 2, 1, 123, payload) }()
	frame, err := readFrame(bridgeFrameReader{ctx: ctx, conn: bytesAndTimeoutConn{source}})
	if err != nil || frame.seq != 1 || !bytes.Equal(frame.payload, payload) {
		t.Fatalf("bytes returned with timeout lost: seq=%d bytes=%d err=%v", frame.seq, len(frame.payload), err)
	}
	if err := <-written; err != nil {
		t.Fatal(err)
	}
}
