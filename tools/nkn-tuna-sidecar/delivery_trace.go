package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"regexp"
	"strings"
	"sync"
)

var deliveryCapturePattern = regexp.MustCompile(`^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$`)

func deliveryWireHash(payload []byte) string {
	h := sha256.New()
	_, _ = h.Write([]byte("nLink/tuna-delivery/wire/v1\x00"))
	_, _ = h.Write(payload)
	return "ftwire1-" + hex.EncodeToString(h.Sum(nil))
}

// This producer observes bridge I/O only. Its caps never affect forwarding.
type deliveryTraceProducer struct {
	mu                          sync.Mutex
	capture, id                 string
	emit                        *emitter
	connections, records, bytes int
	incomplete, capped          bool
}

func newDeliveryTraceProducer(emit *emitter) *deliveryTraceProducer {
	capture := strings.ToLower(os.Getenv("NLINK_TUNA_DELIVERY_CAPTURE_ID"))
	if os.Getenv("NLINK_TUNA_DELIVERY_TRACE") != "1" || !deliveryCapturePattern.MatchString(capture) {
		return nil
	}
	var nonce [16]byte
	if _, err := rand.Read(nonce[:]); err != nil {
		return nil
	}
	id := fmt.Sprintf("%x-%x-%x-%x-%x", nonce[:4], nonce[4:6], nonce[6:8], nonce[8:10], nonce[10:])
	return &deliveryTraceProducer{capture: capture, id: id, emit: emit}
}

func (p *deliveryTraceProducer) connect() *deliveryTraceConnection {
	if p == nil {
		return nil
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	p.connections++
	if p.connections > 4 {
		p.recordLocked(4, "app_to_tuna", "incomplete", "connection_cap", nil, true)
		return nil
	}
	return &deliveryTraceConnection{producer: p, number: p.connections}
}

func (p *deliveryTraceProducer) recordLocked(connection int, direction, stage, reason string, frame *deliveryTraceFrame, marker bool) {
	if p.capped && !marker {
		return
	}
	v := event{"event": "tuna_delivery_trace", "trace_capture": p.capture, "trace_producer": p.id,
		"trace_incomplete": p.incomplete,
		"trace_connection": connection, "trace_direction": direction, "trace_stage": stage,
		"trace_reason": reason, "trace_order": p.records + 1, "trace_seq": uint64(0),
		"trace_occurrence": 0, "trace_wire": "none", "trace_bytes": 0, "trace_tail": "unproved"}
	if frame != nil {
		v["trace_seq"], v["trace_occurrence"], v["trace_wire"], v["trace_bytes"] = frame.sequence, frame.occurrence, frame.wire, frame.bytes
	}
	b, err := json.Marshal(v)
	if err != nil {
		p.incomplete = true
		return
	}
	b = append(b, '\n')
	if (!marker && (p.records >= 4096-32 || p.bytes+len(b) > 4*1024*1024-32768)) || p.records >= 4096 || p.bytes+len(b) > 4*1024*1024 {
		p.capped = true
		if !p.incomplete {
			p.incomplete = true
			p.recordLocked(connection, direction, "incomplete", "record_or_byte_cap", nil, true)
		}
		return
	}
	p.records++
	p.bytes += len(b)
	p.emit.mu.Lock()
	out := p.emit.out
	if out == nil {
		out = os.Stdout
	}
	n, err := out.Write(b)
	p.emit.mu.Unlock()
	if err != nil || n != len(b) {
		p.incomplete = true
	}
}

type deliveryTraceConnection struct {
	producer *deliveryTraceProducer
	number   int
	mu       sync.Mutex
	counts   [2]int
}

type deliveryTraceFrame struct {
	sequence   uint64
	occurrence int
	wire       string
	bytes      int
}

func (c *deliveryTraceConnection) selectFrame(direction string, frame sidecarFrame) *deliveryTraceFrame {
	if c == nil || frame.typ != frameTypeData || frame.lane != 2 {
		return nil
	}
	index := 0
	if direction == "tuna_to_app" {
		index = 1
	}
	c.mu.Lock()
	c.counts[index]++
	occurrence := c.counts[index]
	c.mu.Unlock()
	if occurrence > 64 {
		if occurrence == 65 {
			c.record(direction, "incomplete", "frame_window_cap", nil)
		}
		return nil
	}
	return &deliveryTraceFrame{frame.seq, occurrence, deliveryWireHash(frame.payload), len(frame.payload)}
}

func (c *deliveryTraceConnection) record(direction, stage, reason string, frame *deliveryTraceFrame) {
	if c == nil {
		return
	}
	c.producer.mu.Lock()
	defer c.producer.mu.Unlock()
	c.producer.recordLocked(c.number, direction, stage, reason, frame, stage == "direction_start" || stage == "direction_end" || stage == "incomplete")
}
