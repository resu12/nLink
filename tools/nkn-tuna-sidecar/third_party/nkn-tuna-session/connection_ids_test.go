package session

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"io"
	"net"
	"sync"
	"testing"
	"time"

	ncp "github.com/nknorg/ncp-go"
	ncppb "github.com/nknorg/ncp-go/pb"
	nkn "github.com/nknorg/nkn-sdk-go"
	gocache "github.com/patrickmn/go-cache"
	"google.golang.org/protobuf/proto"
)

// Use real Tuna encryption/framing and NCP sessions, with pipes in place of
// provider TCP connections. No wallet, provider service, or NKN network is used.
func TestTCPProviderIDsDeliverAuthenticatedPayload(t *testing.T) {
	for _, tc := range []struct {
		name string
		ids  []string
	}{
		{"all", []string{"0", "1", "2", "3"}},
		{"missing_first", []string{"1", "2", "3"}},
		{"missing_middle", []string{"0", "2", "3"}},
		{"missing_last", []string{"0", "1", "2"}},
		{"only_last", []string{"3"}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			listener, dialer := offlineTunaClient(t), offlineTunaClient(t)
			sessionID := []byte("offline-provider-id-regression")
			dialKey := sessionKey(listener.Address(), sessionID)
			dialer.sessionConns[dialKey] = make(map[string]*Conn)
			var listenerConns []*Conn
			var dialerConns []*Conn
			var workers sync.WaitGroup
			stop := make(chan struct{})
			errors := make(chan error, 32)
			t.Cleanup(func() {
				close(stop)
				for _, c := range append(listenerConns, dialerConns...) {
					c.Close()
				}
				workers.Wait()
			})
			for _, id := range tc.ids {
				left, right := net.Pipe()
				lc, dc := newConn(left), newConn(right)
				listenerConns = append(listenerConns, lc)
				dialerConns = append(dialerConns, dc)
				dialer.sessionConns[dialKey][id] = dc
			}
			ds, err := dialer.newSession(listener.Address(), sessionID, tc.ids, dialer.config.SessionConfig)
			if err != nil {
				t.Fatal(err)
			}
			// Cancel without network I/O so every NCP worker also has bounded cleanup.
			closePacket := marshalPacket(t, &ncppb.Packet{Close: true})
			t.Cleanup(func() { _ = ds.ReceiveWith("", "", closePacket) })
			type accepted struct {
				session *ncp.Session
				created bool
				err     error
			}
			acceptedCh := make(chan accepted, len(tc.ids))
			readLoop := func(client *TunaSessionClient, conn *Conn, sess *ncp.Session, index int) {
				for {
					if err := client.handleMsg(conn, sess, index); err != nil {
						select {
						case <-stop:
							return
						default:
						}
						errors <- err
						return
					}
				}
			}
			for n, id := range tc.ids {
				index := int(id[0] - '0')
				lc, dc := listenerConns[n], dialerConns[n]
				workers.Add(2)
				go func() {
					defer workers.Done()
					sess, created, err := listener.acceptTCPConn(lc, dialer.Address(), sessionID, index)
					acceptedCh <- accepted{sess, created, err}
					if err == nil {
						readLoop(listener, lc, sess, index)
					}
				}()
				go func() { defer workers.Done(); readLoop(dialer, dc, ds, index) }()
			}
			ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
			defer cancel()
			dialDone := make(chan error, 1)
			go func() { dialDone <- ds.Dial(ctx) }()
			var ls *ncp.Session
			createdCount := 0
			for range tc.ids {
				select {
				case result := <-acceptedCh:
					if result.err != nil {
						t.Fatal(result.err)
					}
					if ls != nil && ls != result.session {
						t.Fatal("provider created a different session")
					}
					ls = result.session
					if result.created {
						createdCount++
					}
				case <-ctx.Done():
					t.Fatal(ctx.Err())
				}
			}
			if createdCount != 1 {
				t.Fatalf("created %d sessions", createdCount)
			}
			t.Cleanup(func() { _ = ls.ReceiveWith("", "", closePacket) })
			if err := ls.Accept(); err != nil {
				t.Fatal(err)
			}
			if err := <-dialDone; err != nil {
				t.Fatal(err)
			}
			for _, direction := range []struct{ sender, receiver *ncp.Session }{{ds, ls}, {ls, ds}} {
				payload := bytes.Repeat([]byte("authenticated Tuna payload\x00\xff"), 4096)
				deadline := time.Now().Add(5 * time.Second)
				_ = direction.sender.SetWriteDeadline(deadline)
				_ = direction.receiver.SetReadDeadline(deadline)
				written := make(chan error, 1)
				go func() { _, err := direction.sender.Write(payload); written <- err }()
				got := make([]byte, len(payload))
				if _, err := io.ReadFull(direction.receiver, got); err != nil {
					t.Fatal(err)
				}
				if err := <-written; err != nil {
					t.Fatal(err)
				}
				if sha256.Sum256(got) != sha256.Sum256(payload) {
					t.Fatal("payload SHA mismatch")
				}
			}
			select {
			case err := <-errors:
				t.Fatalf("provider receive: %v", err)
			default:
			}
		})
	}
}

func TestListenerHandshakeIDsRejectInvalidSlots(t *testing.T) {
	for _, tc := range []struct {
		name  string
		ids   []string
		index int
	}{
		{"empty", nil, 0}, {"duplicate", []string{"1", "1"}, 1},
		{"out_of_range", []string{"4"}, 4}, {"negative", []string{"-1"}, -1},
		{"noncanonical", []string{"01"}, 1}, {"not_numeric", []string{"x"}, 0},
		{"missing_arrival_slot", []string{"1", "2"}, 0},
		{"too_many", []string{"0", "1", "2", "3", "4"}, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			buf := marshalPacket(t, &ncppb.Packet{Handshake: true, ClientIds: tc.ids})
			if _, err := listenerHandshakeIDs(buf, tc.index, 4); err == nil {
				t.Fatal("accepted invalid provider IDs")
			}
		})
	}
	for _, p := range []*ncppb.Packet{{ClientIds: []string{"0"}}, {Handshake: true, Close: true, ClientIds: []string{"0"}}} {
		if _, err := listenerHandshakeIDs(marshalPacket(t, p), 0, 4); err == nil {
			t.Fatal("accepted non-handshake")
		}
	}
}

func TestListenerHandshakeRejectsUnauthenticatedPacket(t *testing.T) {
	listener, dialer := offlineTunaClient(t), offlineTunaClient(t)
	left, right := net.Pipe()
	defer left.Close()
	defer right.Close()
	packet := marshalPacket(t, &ncppb.Packet{Handshake: true, ClientIds: []string{"1"}, WindowSize: 4096, Mtu: 1024})
	encoded, err := dialer.encode(packet, listener.Address())
	if err != nil {
		t.Fatal(err)
	}
	encoded[len(encoded)-1] ^= 1
	written := make(chan error, 1)
	go func() { written <- writeMessage(newConn(right), encoded, time.Second) }()
	if _, _, err := listener.acceptTCPConn(newConn(left), dialer.Address(), []byte("tampered"), 1); err == nil {
		t.Fatal("accepted unauthenticated handshake")
	}
	if err := <-written; err != nil {
		t.Fatal(err)
	}
	if len(listener.sessions) != 0 || len(listener.sessionConns) != 0 {
		t.Fatal("invalid handshake changed session state")
	}
}

func TestListenerHandshakeRejectsInvalidOrClosedSessionWithoutRegistration(t *testing.T) {
	for _, tc := range []struct {
		name   string
		packet *ncppb.Packet
		closed bool
	}{
		{"invalid_slot", &ncppb.Packet{Handshake: true, ClientIds: []string{"4"}, WindowSize: 4096, Mtu: 1024}, false},
		{"zero_window", &ncppb.Packet{Handshake: true, ClientIds: []string{"1"}, Mtu: 1024}, false},
		{"zero_mtu", &ncppb.Packet{Handshake: true, ClientIds: []string{"1"}, WindowSize: 4096}, false},
		{"not_handshake", &ncppb.Packet{SequenceId: 1, Data: []byte("payload")}, false},
		{"closed_session", &ncppb.Packet{Handshake: true, ClientIds: []string{"1"}, WindowSize: 4096, Mtu: 1024}, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			listener, dialer := offlineTunaClient(t), offlineTunaClient(t)
			sessionID := []byte("rejected-handshake")
			if tc.closed {
				listener.closedSessionKey.SetDefault(sessionKey(dialer.Address(), sessionID), nil)
			}
			left, right := net.Pipe()
			defer left.Close()
			defer right.Close()
			encoded, err := dialer.encode(marshalPacket(t, tc.packet), listener.Address())
			if err != nil {
				t.Fatal(err)
			}
			written := make(chan error, 1)
			go func() { written <- writeMessage(newConn(right), encoded, time.Second) }()
			if _, _, err := listener.acceptTCPConn(newConn(left), dialer.Address(), sessionID, 1); err == nil {
				t.Fatal("accepted invalid or closed session")
			}
			if err := <-written; err != nil {
				t.Fatal(err)
			}
			if len(listener.sessions) != 0 || len(listener.sessionConns) != 0 {
				t.Fatal("rejected handshake changed session state")
			}
		})
	}
}

func offlineTunaClient(t *testing.T) *TunaSessionClient {
	t.Helper()
	account, err := nkn.NewAccount(nil)
	if err != nil {
		t.Fatal(err)
	}
	config, err := MergedConfig(DefaultConfig())
	if err != nil {
		t.Fatal(err)
	}
	return &TunaSessionClient{
		config: config, clientAccount: account,
		addr:     nkn.NewClientAddr(hex.EncodeToString(account.PubKey())),
		sessions: make(map[string]*ncp.Session), sessionConns: make(map[string]map[string]*Conn),
		sharedKeys: make(map[string]*[sharedKeySize]byte), closedSessionKey: gocache.New(time.Minute, 0),
	}
}

func marshalPacket(t *testing.T, packet *ncppb.Packet) []byte {
	t.Helper()
	buf, err := proto.Marshal(packet)
	if err != nil {
		t.Fatal(err)
	}
	return buf
}
