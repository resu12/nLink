package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	nkn "github.com/nknorg/nkn-sdk-go"
	ts "github.com/nknorg/nkn-tuna-session"
	"github.com/nknorg/nkngomobile"
	"golang.org/x/term"
)

const (
	appName               = "nlink-tuna-sidecar"
	modeAddress           = "address"
	modeVersion           = "version"
	modeWalletStatus      = "wallet-status"
	modeListen            = "listen"
	modeDial              = "dial"
	defaultIdentifier     = "nlink-tuna-sidecar"
	defaultNumSubClients  = 4
	defaultConnectTimeout = 30 * time.Second
	defaultMinBalanceNKN  = "0.00000001"
	defaultLocalIPC       = "127.0.0.1:0"
	defaultMaxTotalMiB    = 0
	defaultMaxDurationSec = 0

	frameMagic            uint32 = 0x4e4c5453 // NLTS
	frameVersion          byte   = 1
	appProtocolVersion           = 1
	frameHeaderSize              = 32
	frameMaxPayloadSize          = 4 * 1024 * 1024
	frameMaxSyncSkipBytes        = 64 * 1024
	frameTypeStatus       byte   = 1
	frameTypeData         byte   = 2
	frameTypePing         byte   = 3
	frameTypePong         byte   = 4
	frameTypeClose        byte   = 5

	bridgeTraceFirstFrames    = 16
	bridgeTraceEveryFrames    = 128
	bridgeSeqGapWarnMissing   = 16
	streamHelloTimeout        = 10 * time.Second
	providerWatchInterval     = 5 * time.Second
	providerReadyPollInterval = 500 * time.Millisecond
	bridgeIdleGapThreshold    = 2 * time.Second
	bridgeSlowOpThreshold     = 2 * time.Second
	providerReadyMinPaths     = 4
	providerDegradedMinPaths  = 3
	providerReadyTimeout      = 45 * time.Second
	defaultListenStartTimeout = 45 * time.Second
)

var sidecarVersion = "dev"

type config struct {
	mode              string
	jsonl             bool
	identifier        string
	seedHex           string
	seedStdin         bool
	numSubClients     int
	connectTimeoutSec int
	localIPC          string

	walletPath            string
	passwordPrompt        bool
	passwordStdin         bool
	allowRemote           string
	allowRemoteRegex      bool
	unsafeAllowAny        bool
	maxPriceNKNPerMB      string
	minBalanceNKN         string
	maxTotalMiB           int
	maxDurationSec        int
	acceptTimeoutSec      int
	listenStartTimeoutSec int
	requireProviderReady  bool
	providerReadyAttempts int

	to                string
	tunaDialTimeoutMs int
}

type emitter struct {
	jsonl bool
	out   io.Writer
	mu    sync.Mutex
}

type event map[string]any

type paymentLogObserver struct {
	emit       *emitter
	stderr     io.Writer
	mu         sync.Mutex
	buffer     string
	cumulative *big.Rat
	eventCount int64
	bytesMoved *atomic.Int64
}

var activePaymentObserver *paymentLogObserver

func newPaymentLogObserver(emit *emitter, stderr io.Writer) *paymentLogObserver {
	observer := &paymentLogObserver{
		emit:       emit,
		stderr:     stderr,
		cumulative: new(big.Rat),
	}
	activePaymentObserver = observer
	return observer
}

func (o *paymentLogObserver) Write(p []byte) (int, error) {
	if o.stderr != nil {
		_, _ = o.stderr.Write(p)
	}

	o.mu.Lock()
	defer o.mu.Unlock()
	o.buffer += string(p)
	for {
		idx := strings.IndexByte(o.buffer, '\n')
		if idx < 0 {
			break
		}

		line := strings.TrimSpace(o.buffer[:idx])
		o.buffer = o.buffer[idx+1:]
		o.observeLineLocked(line)
	}

	return len(p), nil
}

func (o *paymentLogObserver) observeLineLocked(line string) {
	const marker = "send nanopay success:"
	idx := strings.Index(line, marker)
	if idx < 0 {
		return
	}

	amountText := strings.TrimSpace(line[idx+len(marker):])
	if fields := strings.Fields(amountText); len(fields) > 0 {
		amountText = fields[0]
	}

	amount, ok := new(big.Rat).SetString(amountText)
	if !ok || amount.Sign() < 0 {
		return
	}

	o.cumulative.Add(o.cumulative, amount)
	o.eventCount++
	var bytesMoved int64
	if o.bytesMoved != nil {
		bytesMoved = o.bytesMoved.Load()
	}

	payment := event{
		"event":              "tuna_payment",
		"amountNkn":          ratDecimalString(amount, 8),
		"cumulativeSpendNkn": ratDecimalString(o.cumulative, 8),
		"bytesMoved":         bytesMoved,
	}
	if bytesMoved > 0 {
		mb := new(big.Rat).SetFrac(big.NewInt(bytesMoved), big.NewInt(1_000_000))
		nknPerMb := new(big.Rat).Quo(o.cumulative, mb)
		payment["nknPerMb"] = ratDecimalString(nknPerMb, 9)
	}

	o.emit.emit(payment)
}

func (o *paymentLogObserver) observeBytes(bytesMoved *atomic.Int64) func() {
	if o == nil {
		return func() {}
	}

	o.mu.Lock()
	previous := o.bytesMoved
	o.bytesMoved = bytesMoved
	o.mu.Unlock()
	return func() {
		o.mu.Lock()
		if o.bytesMoved == bytesMoved {
			o.bytesMoved = previous
		}
		o.mu.Unlock()
	}
}

func (o *paymentLogObserver) snapshot() (int64, *big.Rat) {
	if o == nil {
		return 0, new(big.Rat)
	}

	o.mu.Lock()
	defer o.mu.Unlock()
	return o.eventCount, new(big.Rat).Set(o.cumulative)
}

type walletInfo struct {
	wallet  *nkn.Wallet
	address string
	balance string
}

type sidecarFrame struct {
	typ       byte
	lane      byte
	seq       uint64
	timestamp int64
	payload   []byte
}

type bridgeDirectionResult struct {
	direction           string
	stage               string
	err                 error
	terminalReason      string
	durationMs          int64
	framesForwarded     int64
	payloadBytes        int64
	firstFrameElapsedMs int64
	lastFrameElapsedMs  int64
	maxReadMs           int64
	maxWriteMs          int64
	idleGapCount        int64
	maxIdleGapMs        int64
	seqGapCount         int64
	seqGapMissing       int64
	seqReorderCount     int64
	laneGapSummaries    []event
	lastFrameType       byte
	lastFrameLane       byte
	lastFrameSeq        uint64
	lastFramePayload    int
}

type bridgeLaneSequenceStats struct {
	lane            byte
	gapCount        int64
	gapMissing      int64
	reorderCount    int64
	lastPreviousSeq uint64
	lastCurrentSeq  uint64
}

type bridgeSequenceObservationKind string

const (
	bridgeSequenceInOrder   bridgeSequenceObservationKind = ""
	bridgeSequenceGap       bridgeSequenceObservationKind = "gap"
	bridgeSequenceReordered bridgeSequenceObservationKind = "reordered"
)

type bridgeSequenceObservation struct {
	kind         bridgeSequenceObservationKind
	previousSeq  uint64
	currentSeq   uint64
	missingCount int64
}

type bridgeSequenceTracker struct {
	seen            bool
	lastSeq         uint64
	seqGapCount     int64
	seqGapMissing   int64
	seqReorderCount int64
	laneStats       map[byte]*bridgeLaneSequenceStats
}

func main() {
	emit := &emitter{jsonl: hasArg(os.Args[1:], "--jsonl")}
	paymentObserver := newPaymentLogObserver(emit, os.Stderr)
	log.SetOutput(paymentObserver)
	if err := run(os.Args[1:], emit); err != nil {
		emit.emit(event{"event": "error", "reason": safeReason(err)})
		os.Exit(1)
	}
}

func run(args []string, emit *emitter) error {
	if len(args) == 0 {
		return usageError("missing mode: use address, version, wallet-status, listen, or dial")
	}
	cfg, err := parseArgs(args)
	if err != nil {
		return err
	}
	emit.jsonl = cfg.jsonl

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	switch cfg.mode {
	case modeAddress:
		return runAddress(ctx, cfg, emit)
	case modeVersion:
		return runVersion(emit)
	case modeWalletStatus:
		return runWalletStatus(cfg, emit)
	case modeListen:
		return runListen(ctx, cfg, emit)
	case modeDial:
		return runDial(ctx, cfg, emit)
	default:
		return usageError("unsupported mode %q", cfg.mode)
	}
}

func parseArgs(args []string) (*config, error) {
	cfg := &config{
		mode:              args[0],
		identifier:        defaultIdentifier,
		numSubClients:     defaultNumSubClients,
		connectTimeoutSec: int(defaultConnectTimeout.Seconds()),
		localIPC:          defaultLocalIPC,
		minBalanceNKN:     defaultMinBalanceNKN,
		maxTotalMiB:       defaultMaxTotalMiB,
		maxDurationSec:    defaultMaxDurationSec,
	}

	fs := flag.NewFlagSet(args[0], flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	fs.BoolVar(&cfg.jsonl, "jsonl", false, "emit machine-readable JSONL events")
	fs.StringVar(&cfg.identifier, "identifier", defaultIdentifier, "NKN identifier")
	fs.StringVar(&cfg.seedHex, "seed-hex", "", "optional 32-byte hex seed for stable dialer identity; never logged")
	fs.BoolVar(&cfg.seedStdin, "seed-stdin", false, "read 32-byte hex seed from stdin for stable dialer identity; never logged")
	fs.IntVar(&cfg.numSubClients, "num-subclients", defaultNumSubClients, "NKN multiclient subclient count")
	fs.IntVar(&cfg.connectTimeoutSec, "connect-timeout-sec", int(defaultConnectTimeout.Seconds()), "NKN connect timeout in seconds")
	fs.StringVar(&cfg.localIPC, "local-ipc", defaultLocalIPC, "local app IPC endpoint host:port")

	switch args[0] {
	case modeAddress, modeVersion:
	case modeWalletStatus:
		fs.StringVar(&cfg.walletPath, "wallet", "", "linked NKN wallet.json path")
		fs.BoolVar(&cfg.passwordStdin, "password-stdin", false, "read wallet password from stdin")
	case modeListen:
		fs.StringVar(&cfg.walletPath, "wallet", "", "linked NKN wallet.json path")
		fs.BoolVar(&cfg.passwordPrompt, "password-prompt", false, "prompt for wallet password without echo")
		fs.BoolVar(&cfg.passwordStdin, "password-stdin", false, "test automation only: read wallet password from stdin")
		fs.StringVar(&cfg.allowRemote, "allow-remote", "", "allowed remote NKN address; exact by default")
		fs.BoolVar(&cfg.allowRemoteRegex, "allow-remote-regex", false, "treat --allow-remote as a regex")
		fs.BoolVar(&cfg.unsafeAllowAny, "unsafe-allow-any", false, "unsafe: accept any remote")
		fs.StringVar(&cfg.maxPriceNKNPerMB, "max-price-nkn-per-mb", "", "maximum Tuna price to pay in NKN per MB")
		fs.StringVar(&cfg.minBalanceNKN, "min-balance-nkn", defaultMinBalanceNKN, "minimum listener wallet balance")
		fs.IntVar(&cfg.maxTotalMiB, "max-total-mib", defaultMaxTotalMiB, "local byte cap in MiB")
		fs.IntVar(&cfg.maxDurationSec, "max-duration-sec", defaultMaxDurationSec, "local duration cap in seconds")
		fs.IntVar(&cfg.acceptTimeoutSec, "accept-timeout-sec", 0, "optional timeout while waiting for app/Tuna connections")
		fs.IntVar(&cfg.listenStartTimeoutSec, "listen-start-timeout-sec", int(defaultListenStartTimeout.Seconds()), "timeout while starting Tuna listener paths before retry/fallback")
		fs.BoolVar(&cfg.requireProviderReady, "require-provider-ready", false, "fail listener startup unless full Tuna provider path readiness is reached")
		fs.IntVar(&cfg.providerReadyAttempts, "provider-ready-attempts", 1, "provider readiness wait attempts before failing listener startup")
	case modeDial:
		fs.StringVar(&cfg.to, "to", "", "remote Tuna/NKN listener address")
		fs.IntVar(&cfg.maxTotalMiB, "max-total-mib", defaultMaxTotalMiB, "optional local byte cap in MiB; 0 disables")
		fs.IntVar(&cfg.maxDurationSec, "max-duration-sec", defaultMaxDurationSec, "optional local duration cap in seconds; 0 disables")
		fs.IntVar(&cfg.tunaDialTimeoutMs, "tuna-dial-timeout-ms", 0, "Tuna dial timeout override in milliseconds")
	default:
		return nil, usageError("unsupported mode %q", args[0])
	}

	if err := fs.Parse(args[1:]); err != nil {
		return nil, err
	}
	return cfg, validateConfig(cfg)
}

func validateConfig(cfg *config) error {
	if cfg.numSubClients < 1 || cfg.numSubClients > 16 {
		return usageError("--num-subclients must be between 1 and 16")
	}
	if cfg.connectTimeoutSec < 1 {
		return usageError("--connect-timeout-sec must be positive")
	}
	if cfg.localIPC == "" {
		return usageError("--local-ipc is required")
	}
	if cfg.seedHex != "" && cfg.seedStdin {
		return usageError("--seed-hex and --seed-stdin are mutually exclusive")
	}
	if cfg.seedHex != "" {
		seed, err := decodeSeed(cfg.seedHex)
		if err != nil {
			return err
		}
		zeroBytes(seed)
	}

	switch cfg.mode {
	case modeAddress, modeVersion:
	case modeWalletStatus:
		if cfg.seedHex != "" || cfg.seedStdin {
			return usageError("wallet-status identity comes from --wallet; seed flags are only supported for dial")
		}
		if strings.TrimSpace(cfg.walletPath) == "" {
			return usageError("wallet-status requires --wallet")
		}
		if !cfg.passwordStdin || cfg.passwordPrompt {
			return usageError("wallet-status requires --password-stdin")
		}
	case modeListen:
		if cfg.seedHex != "" || cfg.seedStdin {
			return usageError("listen identity comes from --wallet; seed flags are only supported for dial")
		}
		if strings.TrimSpace(cfg.walletPath) == "" {
			return usageError("listen requires --wallet")
		}
		if cfg.passwordPrompt == cfg.passwordStdin {
			return usageError("listen requires exactly one of --password-prompt or --password-stdin")
		}
		if strings.TrimSpace(cfg.maxPriceNKNPerMB) == "" {
			return usageError("listen requires --max-price-nkn-per-mb")
		}
		if _, err := parsePositiveDecimal(cfg.maxPriceNKNPerMB, "--max-price-nkn-per-mb"); err != nil {
			return err
		}
		if _, err := parsePositiveDecimal(cfg.minBalanceNKN, "--min-balance-nkn"); err != nil {
			return err
		}
		if cfg.maxTotalMiB < 1 {
			return usageError("--max-total-mib must be positive")
		}
		if cfg.maxDurationSec < 1 {
			return usageError("--max-duration-sec must be positive")
		}
		if cfg.providerReadyAttempts < 1 || cfg.providerReadyAttempts > 5 {
			return usageError("--provider-ready-attempts must be between 1 and 5")
		}
		if cfg.listenStartTimeoutSec < 5 || cfg.listenStartTimeoutSec > 300 {
			return usageError("--listen-start-timeout-sec must be between 5 and 300")
		}
		if err := validateAllowRemote(cfg); err != nil {
			return err
		}
	case modeDial:
		if strings.TrimSpace(cfg.to) == "" {
			return usageError("dial requires --to")
		}
		if cfg.maxTotalMiB < 0 {
			return usageError("--max-total-mib must not be negative")
		}
		if cfg.maxDurationSec < 0 {
			return usageError("--max-duration-sec must not be negative")
		}
		if cfg.tunaDialTimeoutMs < 0 {
			return usageError("--tuna-dial-timeout-ms must not be negative")
		}
	}
	return nil
}

func runVersion(emit *emitter) error {
	metadata := sidecarMetadata()
	metadata["event"] = "sidecar_version"
	emit.emit(metadata)
	return nil
}

func runAddress(ctx context.Context, cfg *config, emit *emitter) error {
	account, err := accountFromOptionalSeed(cfg)
	if err != nil {
		return err
	}
	client, err := newReadyMultiClient(ctx, account, cfg)
	if err != nil {
		return err
	}
	defer client.Close()
	emit.emit(event{
		"event":      "ready",
		"role":       "address",
		"transport":  "nkn",
		"identifier": cfg.identifier,
		"address":    client.Address(),
		"addressLen": len(client.Address()),
		"sidecar":    sidecarMetadata(),
	})
	return nil
}

func runWalletStatus(cfg *config, emit *emitter) error {
	_, err := openLinkedWallet(cfg, emit)
	return err
}

func runListen(ctx context.Context, cfg *config, emit *emitter) error {
	wallet, err := openLinkedWallet(cfg, emit)
	if err != nil {
		return err
	}
	if err := verifyMinimumBalance(wallet.balance, cfg.minBalanceNKN); err != nil {
		return err
	}

	client, err := newReadyMultiClient(ctx, wallet.wallet.Account(), cfg)
	if err != nil {
		return err
	}
	cleanupBeforeListen := true
	defer func() {
		if cleanupBeforeListen {
			_ = client.Close()
		}
	}()

	tunaConfig := ts.DefaultConfig()
	tunaConfig.TunaMaxPrice = cfg.maxPriceNKNPerMB
	tunaConfig.TunaMinBalance = cfg.minBalanceNKN
	tunaConfig.Verbose = true
	tunaClient, err := ts.NewTunaSessionClient(wallet.wallet.Account(), client, wallet.wallet, tunaConfig)
	if err != nil {
		return fmt.Errorf("create Tuna session client failed: %w", err)
	}
	defer func() {
		if cleanupBeforeListen {
			_ = tunaClient.Close()
		}
	}()

	allow, allowCheck, err := allowList(cfg)
	if err != nil {
		return err
	}
	listenStarted := time.Now()
	emit.emit(event{
		"event":          "tuna_listen_start",
		"role":           "listener",
		"maxPriceNknMb":  cfg.maxPriceNKNPerMB,
		"minBalanceNkn":  cfg.minBalanceNKN,
		"maxTotalMiB":    cfg.maxTotalMiB,
		"maxDurationSec": cfg.maxDurationSec,
	})

	listenStartTimeout := time.Duration(cfg.listenStartTimeoutSec) * time.Second
	listenDone := make(chan error, 1)
	go func() {
		listenDone <- tunaClient.Listen(allow)
	}()

	select {
	case err := <-listenDone:
		if err != nil {
			return fmt.Errorf("Tuna listen failed: %w", err)
		}
		emit.emit(event{
			"event":      "tuna_listen_call_completed",
			"role":       "listener",
			"durationMs": time.Since(listenStarted).Milliseconds(),
		})
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(listenStartTimeout):
		emit.emit(event{
			"event":      "tuna_listen_start_timeout",
			"role":       "listener",
			"durationMs": time.Since(listenStarted).Milliseconds(),
			"timeoutSec": cfg.listenStartTimeoutSec,
			"paths":      tunaProviderPathDiagnostics(tunaClient.GetPubAddrs()),
		})
		return fmt.Errorf("Tuna listen start timeout after %s", listenStartTimeout)
	}

	providerReady, providerReadyErr := waitForProviderPathReadiness(
		ctx,
		tunaClient,
		providerReadyMinPaths,
		providerReadyTimeout,
		emit,
		"listener",
		!cfg.requireProviderReady,
		cfg.providerReadyAttempts)
	if providerReadyErr != nil && (ctx.Err() != nil || cfg.requireProviderReady) {
		return providerReadyErr
	}
	emit.emit(event{
		"event":          "tuna_listen_started",
		"role":           "listener",
		"durationMs":     time.Since(listenStarted).Milliseconds(),
		"providerReady":  providerReady,
		"providerReason": safeReason(providerReadyErr),
		"minProviderCnt": providerReadyMinPaths,
		"paths":          tunaProviderPathDiagnostics(tunaClient.GetPubAddrs()),
	})
	cleanupBeforeListen = false
	go watchProviderPaths(ctx, tunaClient, emit, "listener")

	localListener, err := net.Listen("tcp", cfg.localIPC)
	if err != nil {
		return fmt.Errorf("local IPC listen failed: %w", err)
	}
	defer localListener.Close()

	emit.emit(event{
		"event":                "ready",
		"role":                 "listener",
		"transport":            "tuna",
		"address":              tunaClient.Address(),
		"localIpc":             localListener.Addr().String(),
		"appProtocolVersion":   appProtocolVersion,
		"frameProtocolVersion": int(frameVersion),
		"protocolVersion":      int(frameVersion),
		"sidecarVersion":       sidecarVersion,
		"walletAddress":        wallet.address,
		"walletFile":           filepath.Base(cfg.walletPath),
		"balanceNkn":           wallet.balance,
		"maxTotalMiB":          cfg.maxTotalMiB,
		"maxDurationSec":       cfg.maxDurationSec,
		"providerPaths":        tunaProviderPathDiagnostics(tunaClient.GetPubAddrs()),
	})

	go exitOnCancel(ctx, 130)
	acceptTimeout := time.Duration(cfg.acceptTimeoutSec) * time.Second
	localAcceptStarted := time.Now()
	appConn, err := acceptLocalApp(ctx, acceptTimeout, localListener)
	if err != nil {
		return fmt.Errorf("local app accept failed: %w", err)
	}
	defer appConn.Close()
	emit.emit(event{
		"event":      "local_ipc_connected",
		"role":       "listener",
		"durationMs": time.Since(localAcceptStarted).Milliseconds(),
		"localAddr":  connAddrDiagnostics(appConn.LocalAddr()),
		"remoteAddr": connAddrDiagnostics(appConn.RemoteAddr()),
	})
	if err := writeStatus(appConn, tunaClient.Address()); err != nil {
		return err
	}

	tunaAcceptStarted := time.Now()
	tunaConn, err := acceptValidatedTunaConn(ctx, acceptTimeout, tunaClient.Accept, allowCheck, emit, "listener")
	if err != nil {
		return fmt.Errorf("Tuna accept failed: %w", err)
	}
	defer tunaConn.Close()
	emit.emit(event{
		"event":      "tuna_accept_connected",
		"role":       "listener",
		"durationMs": time.Since(tunaAcceptStarted).Milliseconds(),
		"localAddr":  connAddrDiagnostics(tunaConn.LocalAddr()),
		"remoteAddr": connAddrDiagnostics(tunaConn.RemoteAddr()),
		"paths":      tunaProviderPathDiagnostics(tunaClient.GetPubAddrs()),
	})

	return bridgeConns(ctx, cfg, appConn, tunaConn, emit, "listener", tunaProviderPathDiagnostics(tunaClient.GetPubAddrs()))
}

func runDial(ctx context.Context, cfg *config, emit *emitter) error {
	account, err := accountFromOptionalSeed(cfg)
	if err != nil {
		return err
	}
	emit.emit(event{
		"event":      "dialer_account_ready",
		"role":       "dialer",
		"identifier": cfg.identifier,
	})
	client, err := newReadyMultiClient(ctx, account, cfg)
	if err != nil {
		return err
	}
	defer client.Close()
	emit.emit(event{
		"event":      "dialer_nkn_ready",
		"role":       "dialer",
		"address":    client.Address(),
		"addressLen": len(client.Address()),
	})

	tunaConfig := ts.DefaultConfig()
	tunaConfig.Verbose = true
	if cfg.tunaDialTimeoutMs > 0 {
		tunaConfig.TunaDialTimeout = cfg.tunaDialTimeoutMs
	}
	tunaClient, err := ts.NewTunaSessionClient(account, client, nil, tunaConfig)
	if err != nil {
		return fmt.Errorf("create Tuna session client failed: %w", err)
	}
	defer tunaClient.Close()
	emit.emit(event{
		"event":      "dialer_tuna_client_ready",
		"role":       "dialer",
		"address":    tunaClient.Address(),
		"addressLen": len(tunaClient.Address()),
	})

	localListener, err := net.Listen("tcp", cfg.localIPC)
	if err != nil {
		return fmt.Errorf("local IPC listen failed: %w", err)
	}
	defer localListener.Close()
	emit.emit(event{
		"event":    "dialer_local_ipc_ready",
		"role":     "dialer",
		"localIpc": localListener.Addr().String(),
	})

	emit.emit(event{
		"event":             "tuna_dial_start",
		"role":              "dialer",
		"toLen":             len(cfg.to),
		"tunaDialTimeoutMs": cfg.tunaDialTimeoutMs,
	})
	dialStarted := time.Now()
	conn, err := dialTuna(ctx, tunaClient, cfg)
	if err != nil {
		emit.emit(event{
			"event":      "tuna_dial_failed",
			"role":       "dialer",
			"durationMs": time.Since(dialStarted).Milliseconds(),
			"reason":     safeReason(err),
		})
		return fmt.Errorf("Tuna dial failed: %w", err)
	}
	defer conn.Close()
	emit.emit(event{
		"event":      "tuna_dial_connected",
		"role":       "dialer",
		"durationMs": time.Since(dialStarted).Milliseconds(),
		"localAddr":  connAddrDiagnostics(conn.LocalAddr()),
		"remoteAddr": connAddrDiagnostics(conn.RemoteAddr()),
	})
	if err := writeSidecarHello(conn, "dialer", tunaClient.Address()); err != nil {
		return fmt.Errorf("write Tuna sidecar hello failed: %w", err)
	}
	emit.emit(event{
		"event": "tuna_sidecar_hello_sent",
		"role":  "dialer",
	})

	emit.emit(event{
		"event":                "ready",
		"role":                 "dialer",
		"transport":            "tuna",
		"address":              tunaClient.Address(),
		"to":                   cfg.to,
		"localIpc":             localListener.Addr().String(),
		"appProtocolVersion":   appProtocolVersion,
		"frameProtocolVersion": int(frameVersion),
		"protocolVersion":      int(frameVersion),
		"sidecarVersion":       sidecarVersion,
	})

	localAcceptStarted := time.Now()
	appConn, err := acceptLocalApp(ctx, time.Duration(cfg.acceptTimeoutSec)*time.Second, localListener)
	if err != nil {
		return err
	}
	defer appConn.Close()
	emit.emit(event{
		"event":      "local_ipc_connected",
		"role":       "dialer",
		"durationMs": time.Since(localAcceptStarted).Milliseconds(),
		"localAddr":  connAddrDiagnostics(appConn.LocalAddr()),
		"remoteAddr": connAddrDiagnostics(appConn.RemoteAddr()),
	})
	if err := writeStatus(appConn, tunaClient.Address()); err != nil {
		return err
	}
	return bridgeConns(ctx, cfg, appConn, conn, emit, "dialer", nil)
}

func dialTuna(ctx context.Context, tunaClient *ts.TunaSessionClient, cfg *config) (net.Conn, error) {
	if cfg.tunaDialTimeoutMs <= 0 {
		return tunaClient.Dial(cfg.to)
	}
	type result struct {
		conn net.Conn
		err  error
	}
	ch := make(chan result, 1)
	var abandoned atomic.Bool
	go func() {
		conn, err := tunaClient.DialWithConfig(cfg.to, &nkn.DialConfig{DialTimeout: int32(cfg.tunaDialTimeoutMs)})
		if abandoned.Load() {
			if conn != nil {
				_ = conn.Close()
			}
			return
		}
		ch <- result{conn: conn, err: err}
	}()
	timer := time.NewTimer(time.Duration(cfg.tunaDialTimeoutMs) * time.Millisecond)
	defer timer.Stop()
	select {
	case result := <-ch:
		return result.conn, result.err
	case <-timer.C:
		abandoned.Store(true)
		return nil, fmt.Errorf("Tuna dial timed out after %dms", cfg.tunaDialTimeoutMs)
	case <-ctx.Done():
		abandoned.Store(true)
		return nil, ctx.Err()
	}
}

func acceptBoth(ctx context.Context, cfg *config, localListener net.Listener, acceptTuna func() (net.Conn, error)) (net.Conn, net.Conn, error) {
	timeout := time.Duration(cfg.acceptTimeoutSec) * time.Second
	appCh := make(chan acceptResult, 1)
	tunaCh := make(chan acceptResult, 1)
	go func() {
		conn, err := acceptLocalApp(ctx, timeout, localListener)
		appCh <- acceptResult{conn: conn, err: err}
	}()
	go func() {
		conn, err := acceptOne(ctx, timeout, acceptTuna)
		tunaCh <- acceptResult{conn: conn, err: err}
	}()

	var appConn net.Conn
	var tunaConn net.Conn
	for i := 0; i < 2; i++ {
		select {
		case result := <-appCh:
			if result.err != nil {
				return nil, nil, fmt.Errorf("local app accept failed: %w", result.err)
			}
			appConn = result.conn
		case result := <-tunaCh:
			if result.err != nil {
				return nil, nil, fmt.Errorf("Tuna accept failed: %w", result.err)
			}
			tunaConn = result.conn
		case <-ctx.Done():
			return nil, nil, ctx.Err()
		}
	}
	return appConn, tunaConn, nil
}

type acceptResult struct {
	conn net.Conn
	err  error
}

func acceptLocalApp(ctx context.Context, timeout time.Duration, listener net.Listener) (net.Conn, error) {
	return acceptOne(ctx, timeout, listener.Accept)
}

func acceptValidatedTunaConn(
	ctx context.Context,
	timeout time.Duration,
	accept func() (net.Conn, error),
	allowCheck func(string) bool,
	emit *emitter,
	role string) (net.Conn, error) {
	var deadline time.Time
	if timeout > 0 {
		deadline = time.Now().Add(timeout)
	}
	var attempts int
	for {
		remaining := timeout
		if !deadline.IsZero() {
			remaining = time.Until(deadline)
			if remaining <= 0 {
				return nil, fmt.Errorf("accept timed out after %s", timeout)
			}
		}
		attemptStarted := time.Now()
		conn, err := acceptOne(ctx, remaining, accept)
		if err != nil {
			return nil, err
		}
		attempts++

		remote := ""
		if conn.RemoteAddr() != nil {
			remote = conn.RemoteAddr().String()
			if allowCheck != nil && !allowCheck(remote) {
				emit.emit(event{
					"event":      "tuna_stream_rejected",
					"role":       role,
					"attempt":    attempts,
					"durationMs": time.Since(attemptStarted).Milliseconds(),
					"reason":     "remote_address_rejected",
					"localAddr":  connAddrDiagnostics(conn.LocalAddr()),
					"remoteAddr": connAddrDiagnostics(conn.RemoteAddr()),
				})
				_ = conn.Close()
				continue
			}
		}

		if err := readSidecarHello(conn); err != nil {
			emit.emit(event{
				"event":      "tuna_stream_rejected",
				"role":       role,
				"attempt":    attempts,
				"durationMs": time.Since(attemptStarted).Milliseconds(),
				"reason":     safeReason(err),
				"localAddr":  connAddrDiagnostics(conn.LocalAddr()),
				"remoteAddr": connAddrDiagnostics(conn.RemoteAddr()),
			})
			_ = conn.Close()
			continue
		}

		emit.emit(event{
			"event":          "tuna_stream_validated",
			"role":           role,
			"attempt":        attempts,
			"durationMs":     time.Since(attemptStarted).Milliseconds(),
			"remoteAddrLen":  len(remote),
			"remoteAddrHash": shortHash(remote),
			"localAddr":      connAddrDiagnostics(conn.LocalAddr()),
			"remoteAddr":     connAddrDiagnostics(conn.RemoteAddr()),
		})
		return conn, nil
	}
}

func acceptOne(ctx context.Context, timeout time.Duration, accept func() (net.Conn, error)) (net.Conn, error) {
	ch := make(chan acceptResult, 1)
	go func() {
		conn, err := accept()
		ch <- acceptResult{conn: conn, err: err}
	}()
	var timer <-chan time.Time
	if timeout > 0 {
		t := time.NewTimer(timeout)
		defer t.Stop()
		timer = t.C
	}
	select {
	case result := <-ch:
		return result.conn, result.err
	case <-timer:
		return nil, fmt.Errorf("accept timed out after %s", timeout)
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func writeSidecarHello(conn net.Conn, role string, address string) error {
	payload, _ := json.Marshal(event{
		"event":                "sidecar_hello",
		"role":                 role,
		"appProtocolVersion":   appProtocolVersion,
		"frameProtocolVersion": int(frameVersion),
		"protocolVersion":      int(frameVersion),
		"sidecarVersion":       sidecarVersion,
		"addressLen":           len(address),
	})
	return writeFrame(conn, frameTypeStatus, 0, 0, time.Now().UnixMilli(), payload)
}

func readSidecarHello(conn net.Conn) error {
	_ = conn.SetReadDeadline(time.Now().Add(streamHelloTimeout))
	frame, err := readFrame(conn)
	_ = conn.SetReadDeadline(time.Time{})
	if err != nil {
		return err
	}
	if frame.typ != frameTypeStatus || frame.lane != 0 {
		return fmt.Errorf("unexpected sidecar hello frame type=%s lane=%s", frameTypeName(frame.typ), frameLaneName(frame.lane))
	}
	var hello struct {
		Event                string `json:"event"`
		AppProtocolVersion   int    `json:"appProtocolVersion"`
		FrameProtocolVersion int    `json:"frameProtocolVersion"`
		ProtocolVersion      int    `json:"protocolVersion"`
		SidecarVersion       string `json:"sidecarVersion"`
	}
	if err := json.Unmarshal(frame.payload, &hello); err != nil {
		return fmt.Errorf("decode sidecar hello failed: %w", err)
	}
	if hello.Event != "sidecar_hello" {
		return fmt.Errorf("unexpected sidecar hello event")
	}
	if hello.AppProtocolVersion != appProtocolVersion {
		return fmt.Errorf("unsupported sidecar hello app protocol version")
	}
	if hello.FrameProtocolVersion != int(frameVersion) || hello.ProtocolVersion != int(frameVersion) {
		return fmt.Errorf("unsupported sidecar hello frame protocol version")
	}
	if !compatibleSidecarVersion(hello.SidecarVersion) {
		return fmt.Errorf("unsupported sidecar hello version")
	}
	return nil
}

func writeStatus(conn net.Conn, address string) error {
	payload, _ := json.Marshal(event{
		"event":                "status",
		"role":                 "sidecar",
		"transport":            "tuna",
		"address":              address,
		"appProtocolVersion":   appProtocolVersion,
		"frameProtocolVersion": int(frameVersion),
		"protocolVersion":      int(frameVersion),
		"sidecarVersion":       sidecarVersion,
		"lanes":                []string{"file", "screen"},
	})
	return writeFrame(conn, frameTypeStatus, 0, 0, time.Now().UnixMilli(), payload)
}

func sidecarMetadata() event {
	return event{
		"appProtocolVersion":   appProtocolVersion,
		"frameProtocolVersion": int(frameVersion),
		"protocolVersion":      int(frameVersion),
		"sidecarVersion":       sidecarVersion,
		"runtime":              sidecarRuntime(),
	}
}

func sidecarRuntime() string {
	if runtime.GOOS == "windows" && runtime.GOARCH == "amd64" {
		return "win-x64"
	}

	return runtime.GOOS + "-" + runtime.GOARCH
}

func compatibleSidecarVersion(peerVersion string) bool {
	peerVersion = strings.TrimSpace(peerVersion)
	if peerVersion == "" {
		return false
	}
	return peerVersion == sidecarVersion || peerVersion == "dev" || sidecarVersion == "dev"
}

func bridgeConns(ctx context.Context, cfg *config, appConn net.Conn, tunaConn net.Conn, emit *emitter, role string, providerInfo event) error {
	limitBytes := int64(cfg.maxTotalMiB) * 1024 * 1024
	duration := time.Duration(cfg.maxDurationSec) * time.Second
	hasByteCap := limitBytes > 0
	hasDurationCap := duration > 0
	if limitBytes <= 0 {
		limitBytes = 1 << 62
	}
	if duration <= 0 {
		duration = 24 * time.Hour
	}
	ctx, cancel := context.WithTimeout(ctx, duration)
	defer cancel()
	var totalBytes atomic.Int64
	var capHandoffRequested atomic.Bool
	if activePaymentObserver != nil {
		defer activePaymentObserver.observeBytes(&totalBytes)()
	}
	errCh := make(chan bridgeDirectionResult, 2)
	bridgeStarted := time.Now()
	emit.emit(event{
		"event":      "bridge_started",
		"transport":  "tuna",
		"role":       role,
		"limitBytes": limitBytes,
		"durationMs": duration.Milliseconds(),
		"appLocal":   connAddrDiagnostics(appConn.LocalAddr()),
		"appRemote":  connAddrDiagnostics(appConn.RemoteAddr()),
		"tunaLocal":  connAddrDiagnostics(tunaConn.LocalAddr()),
		"tunaRemote": connAddrDiagnostics(tunaConn.RemoteAddr()),
	})
	softLimitBytes := capHandoffSoftLimitBytes(limitBytes, hasByteCap)
	if softLimitBytes > 0 {
		emit.emit(event{
			"event":          "tuna_cap_handoff_configured",
			"transport":      "tuna",
			"role":           role,
			"capReason":      "byte_cap_reached",
			"limitBytes":     limitBytes,
			"softLimitBytes": softLimitBytes,
			"reserveBytes":   limitBytes - softLimitBytes,
		})
	}
	if hasDurationCap {
		scheduleDurationCapHandoff(ctx, emit, role, duration, &capHandoffRequested)
	}
	go bridgeDirection(ctx, role, "app_to_tuna", appConn, tunaConn, &totalBytes, limitBytes, softLimitBytes, &capHandoffRequested, emit, errCh)
	go bridgeDirection(ctx, role, "tuna_to_app", tunaConn, appConn, &totalBytes, limitBytes, softLimitBytes, &capHandoffRequested, emit, errCh)

	var err error
	var result bridgeDirectionResult
	results := make([]bridgeDirectionResult, 0, 2)
	select {
	case result = <-errCh:
		err = result.err
		results = append(results, result)
	case <-ctx.Done():
		err = ctx.Err()
	}
	_ = writeFrame(appConn, frameTypeClose, 0, 0, time.Now().UnixMilli(), nil)
	_ = appConn.Close()
	_ = tunaConn.Close()
	waitForBridgeResults := time.NewTimer(1500 * time.Millisecond)
	for len(results) < 2 {
		select {
		case extra := <-errCh:
			results = append(results, extra)
		case <-waitForBridgeResults.C:
			goto doneCollectingBridgeResults
		}
	}
doneCollectingBridgeResults:
	waitForBridgeResults.Stop()
	directionSummaries := make([]event, 0, len(results))
	for _, stopped := range results {
		if stopped.direction == "" {
			continue
		}
		stoppedEvent := bridgeDirectionEvent(stopped)
		stoppedEvent["event"] = "bridge_direction_stopped"
		stoppedEvent["transport"] = "tuna"
		stoppedEvent["role"] = role
		emit.emit(stoppedEvent)
		directionSummaries = append(directionSummaries, bridgeDirectionEvent(stopped))
	}
	paymentEventCount := int64(0)
	cumulativeSpend := new(big.Rat)
	if activePaymentObserver != nil {
		paymentEventCount, cumulativeSpend = activePaymentObserver.snapshot()
	}
	bytesMoved := totalBytes.Load()
	paymentStatus := "none"
	if paymentEventCount > 0 {
		paymentStatus = "reported"
	} else if bytesMoved > 0 {
		paymentStatus = "no_payment_telemetry_reported"
	}
	capReached := false
	capReason := ""
	if result.stage == "cap" {
		capReached = true
		capReason = "byte_cap_reached"
	} else if errors.Is(err, context.DeadlineExceeded) {
		capReached = true
		capReason = "duration_cap_reached"
	}
	fallbackReason := ""
	if !capReached && err != nil {
		fallbackReason = safeReason(err)
	}
	providerUsableCount := -1
	if providerInfo != nil {
		providerUsableCount = eventInt(providerInfo, "usableCount")
	}
	terminalReason := result.terminalReason
	if terminalReason == "" {
		terminalReason = bridgeTerminalReason(result.direction, result.stage, err)
	}
	terminal := event{
		"event":               "tuna_bridge_terminal",
		"transport":           "tuna",
		"role":                role,
		"direction":           result.direction,
		"stage":               result.stage,
		"terminalReason":      terminalReason,
		"rawReason":           safeReason(err),
		"durationMs":          time.Since(bridgeStarted).Milliseconds(),
		"framesForwarded":     result.framesForwarded,
		"bytesMoved":          bytesMoved,
		"payloadBytes":        result.payloadBytes,
		"trafficFlowed":       bytesMoved > 0 || result.framesForwarded > 0,
		"lastFrameType":       frameTypeName(result.lastFrameType),
		"lastFrameLane":       frameLaneName(result.lastFrameLane),
		"lastFrameSeq":        result.lastFrameSeq,
		"lastFramePayload":    result.lastFramePayload,
		"maxReadMs":           result.maxReadMs,
		"maxWriteMs":          result.maxWriteMs,
		"idleGapCount":        result.idleGapCount,
		"maxIdleGapMs":        result.maxIdleGapMs,
		"seqGapCount":         result.seqGapCount,
		"seqGapMissing":       result.seqGapMissing,
		"seqReorderCount":     result.seqReorderCount,
		"providerUsableCount": providerUsableCount,
		"minProviderCnt":      providerReadyMinPaths,
		"paymentStatus":       paymentStatus,
		"paymentEventCount":   paymentEventCount,
	}
	if len(result.laneGapSummaries) > 0 {
		terminal["sequenceGapsByLane"] = result.laneGapSummaries
	}
	emit.emit(terminal)
	summary := event{
		"event":               "summary",
		"transport":           "tuna",
		"role":                role,
		"bytesMoved":          bytesMoved,
		"durationMs":          time.Since(bridgeStarted).Milliseconds(),
		"reason":              safeReason(err),
		"terminalReason":      terminalReason,
		"stopDirection":       result.direction,
		"stopStage":           result.stage,
		"paymentObserved":     paymentEventCount > 0,
		"paymentStatus":       paymentStatus,
		"paymentEventCount":   paymentEventCount,
		"cumulativeSpendNkn":  ratDecimalString(cumulativeSpend, 8),
		"capReached":          capReached,
		"capReason":           capReason,
		"fallbackReason":      fallbackReason,
		"providerUsableCount": providerUsableCount,
		"minProviderCnt":      providerReadyMinPaths,
		"directions":          directionSummaries,
	}
	if bytesMoved > 0 && cumulativeSpend.Sign() > 0 {
		mb := new(big.Rat).SetFrac(big.NewInt(bytesMoved), big.NewInt(1_000_000))
		nknPerMb := new(big.Rat).Quo(cumulativeSpend, mb)
		summary["nknPerMb"] = ratDecimalString(nknPerMb, 9)
	}
	emit.emit(summary)
	if errors.Is(err, context.DeadlineExceeded) || errors.Is(err, io.EOF) || errors.Is(err, net.ErrClosed) {
		return nil
	}
	return err
}

func bridgeDirection(
	ctx context.Context,
	role string,
	direction string,
	src net.Conn,
	dst net.Conn,
	totalBytes *atomic.Int64,
	limitBytes int64,
	softLimitBytes int64,
	capHandoffRequested *atomic.Bool,
	emit *emitter,
	errCh chan<- bridgeDirectionResult) {
	startedAt := time.Now()
	var framesForwarded int64
	var payloadBytes int64
	var lastFrame sidecarFrame
	var firstFrameElapsedMs int64 = -1
	var lastFrameElapsedMs int64 = -1
	var maxReadMs int64
	var maxWriteMs int64
	var idleGapCount int64
	var maxIdleGapMs int64
	var lastReadAt time.Time
	sequenceTracker := &bridgeSequenceTracker{}
	finish := func(stage string, err error, frame sidecarFrame) {
		laneSummaries := bridgeLaneSequenceSummaries(sequenceTracker.laneStats)
		for _, summary := range laneSummaries {
			summary["event"] = "bridge_sequence_gap_summary"
			summary["transport"] = "tuna"
			summary["role"] = role
			summary["direction"] = direction
			emit.emit(summary)
		}
		errCh <- bridgeDirectionResult{
			direction:           direction,
			stage:               stage,
			err:                 err,
			terminalReason:      bridgeTerminalReason(direction, stage, err),
			durationMs:          time.Since(startedAt).Milliseconds(),
			framesForwarded:     framesForwarded,
			payloadBytes:        payloadBytes,
			firstFrameElapsedMs: firstFrameElapsedMs,
			lastFrameElapsedMs:  lastFrameElapsedMs,
			maxReadMs:           maxReadMs,
			maxWriteMs:          maxWriteMs,
			idleGapCount:        idleGapCount,
			maxIdleGapMs:        maxIdleGapMs,
			seqGapCount:         sequenceTracker.seqGapCount,
			seqGapMissing:       sequenceTracker.seqGapMissing,
			seqReorderCount:     sequenceTracker.seqReorderCount,
			laneGapSummaries:    laneSummaries,
			lastFrameType:       frame.typ,
			lastFrameLane:       frame.lane,
			lastFrameSeq:        frame.seq,
			lastFramePayload:    len(frame.payload),
		}
	}
	for {
		if ctx.Err() != nil {
			finish("context", ctx.Err(), lastFrame)
			return
		}
		_ = src.SetReadDeadline(time.Now().Add(time.Second))
		readStarted := time.Now()
		frame, err := readFrame(src)
		readMs := time.Since(readStarted).Milliseconds()
		if readMs > maxReadMs {
			maxReadMs = readMs
		}
		if err != nil {
			if isTimeout(err) {
				continue
			}
			finish("read", err, lastFrame)
			return
		}
		now := time.Now()
		if !lastReadAt.IsZero() {
			idleMs := now.Sub(lastReadAt).Milliseconds()
			if idleMs > bridgeIdleGapThreshold.Milliseconds() {
				idleGapCount++
				if idleMs > maxIdleGapMs {
					maxIdleGapMs = idleMs
				}
				emit.emit(event{
					"event":           "bridge_read_idle_gap",
					"transport":       "tuna",
					"role":            role,
					"direction":       direction,
					"idleMs":          idleMs,
					"frameType":       frameTypeName(frame.typ),
					"frameLane":       frameLaneName(frame.lane),
					"frameSeq":        frame.seq,
					"framesForwarded": framesForwarded,
					"payloadBytes":    payloadBytes,
				})
			}
		}
		lastReadAt = now
		if readMs > bridgeSlowOpThreshold.Milliseconds() {
			emit.emit(event{
				"event":           "bridge_slow_read",
				"transport":       "tuna",
				"role":            role,
				"direction":       direction,
				"readMs":          readMs,
				"frameType":       frameTypeName(frame.typ),
				"frameLane":       frameLaneName(frame.lane),
				"frameSeq":        frame.seq,
				"framePayload":    len(frame.payload),
				"framesForwarded": framesForwarded,
				"payloadBytes":    payloadBytes,
			})
		}
		sequenceObservation := sequenceTracker.observe(frame)
		if sequenceObservation.kind == bridgeSequenceReordered {
			if shouldTraceBridgeFrame(sequenceTracker.seqReorderCount) {
				emit.emit(event{
					"event":           "bridge_sequence_reordered",
					"transport":       "tuna",
					"role":            role,
					"direction":       direction,
					"frameLane":       frameLaneName(frame.lane),
					"previousSeq":     sequenceObservation.previousSeq,
					"currentSeq":      sequenceObservation.currentSeq,
					"missingCount":    0,
					"framesForwarded": framesForwarded,
					"payloadBytes":    payloadBytes,
					"seqReorderCount": sequenceTracker.seqReorderCount,
					"seqGapCount":     sequenceTracker.seqGapCount,
					"seqGapMissing":   sequenceTracker.seqGapMissing,
				})
			}
		} else if sequenceObservation.kind == bridgeSequenceGap && sequenceObservation.missingCount >= bridgeSeqGapWarnMissing {
			emit.emit(event{
				"event":           "bridge_sequence_gap",
				"transport":       "tuna",
				"role":            role,
				"direction":       direction,
				"frameLane":       frameLaneName(frame.lane),
				"previousSeq":     sequenceObservation.previousSeq,
				"currentSeq":      sequenceObservation.currentSeq,
				"missingCount":    sequenceObservation.missingCount,
				"framesForwarded": framesForwarded,
				"payloadBytes":    payloadBytes,
				"seqGapCount":     sequenceTracker.seqGapCount,
				"seqGapMissing":   sequenceTracker.seqGapMissing,
			})
		}
		moved := int64(len(frame.payload))
		projectedBytesMoved := totalBytes.Load() + moved
		if projectedBytesMoved > limitBytes {
			finish("cap", fmt.Errorf("byte cap reached"), lastFrame)
			return
		}
		if softLimitBytes > 0 && projectedBytesMoved >= softLimitBytes && capHandoffRequested.CompareAndSwap(false, true) {
			emit.emit(event{
				"event":          "tuna_cap_handoff_requested",
				"transport":      "tuna",
				"role":           role,
				"direction":      direction,
				"capReason":      "byte_cap_reached",
				"bytesMoved":     totalBytes.Load(),
				"projectedBytes": projectedBytesMoved,
				"limitBytes":     limitBytes,
				"softLimitBytes": softLimitBytes,
				"remainingBytes": maxInt64(0, limitBytes-totalBytes.Load()),
				"frameType":      frameTypeName(frame.typ),
				"frameLane":      frameLaneName(frame.lane),
				"frameSeq":       frame.seq,
			})
		}
		writeStarted := time.Now()
		if err := writeFrame(dst, frame.typ, frame.lane, frame.seq, frame.timestamp, frame.payload); err != nil {
			finish("write", err, frame)
			return
		}
		totalBytes.Add(moved)
		writeMs := time.Since(writeStarted).Milliseconds()
		if writeMs > maxWriteMs {
			maxWriteMs = writeMs
		}
		if writeMs > bridgeSlowOpThreshold.Milliseconds() {
			emit.emit(event{
				"event":           "bridge_slow_write",
				"transport":       "tuna",
				"role":            role,
				"direction":       direction,
				"writeMs":         writeMs,
				"frameType":       frameTypeName(frame.typ),
				"frameLane":       frameLaneName(frame.lane),
				"frameSeq":        frame.seq,
				"framePayload":    len(frame.payload),
				"framesForwarded": framesForwarded,
				"payloadBytes":    payloadBytes,
			})
		}
		framesForwarded++
		payloadBytes += moved
		lastFrame = frame
		if firstFrameElapsedMs < 0 {
			firstFrameElapsedMs = time.Since(startedAt).Milliseconds()
		}
		lastFrameElapsedMs = time.Since(startedAt).Milliseconds()
		if shouldTraceBridgeFrame(framesForwarded) {
			emit.emit(event{
				"event":           "bridge_frame_forwarded",
				"transport":       "tuna",
				"role":            role,
				"direction":       direction,
				"frameType":       frameTypeName(frame.typ),
				"frameLane":       frameLaneName(frame.lane),
				"frameSeq":        frame.seq,
				"framePayload":    len(frame.payload),
				"readMs":          readMs,
				"writeMs":         writeMs,
				"framesForwarded": framesForwarded,
				"payloadBytes":    payloadBytes,
			})
		}
	}
}

func capHandoffSoftLimitBytes(limitBytes int64, hasByteCap bool) int64 {
	if !hasByteCap || limitBytes <= 0 {
		return 0
	}
	reserve := limitBytes / 20
	const minReserve = int64(8 * 1024 * 1024)
	const maxReserve = int64(64 * 1024 * 1024)
	if reserve < minReserve {
		reserve = minReserve
	}
	if reserve > maxReserve {
		reserve = maxReserve
	}
	if reserve >= limitBytes/2 {
		reserve = maxInt64(1, limitBytes/4)
	}
	softLimit := limitBytes - reserve
	if softLimit <= 0 || softLimit >= limitBytes {
		return 0
	}
	return softLimit
}

func scheduleDurationCapHandoff(ctx context.Context, emit *emitter, role string, duration time.Duration, capHandoffRequested *atomic.Bool) {
	if duration <= 0 {
		return
	}
	reserve := duration / 20
	if reserve < 10*time.Second {
		reserve = 10 * time.Second
	}
	if reserve > 60*time.Second {
		reserve = 60 * time.Second
	}
	if reserve >= duration/2 {
		reserve = duration / 4
	}
	delay := duration - reserve
	if delay <= 0 {
		return
	}
	go func() {
		timer := time.NewTimer(delay)
		defer timer.Stop()
		select {
		case <-timer.C:
			if capHandoffRequested.CompareAndSwap(false, true) {
				emit.emit(event{
					"event":           "tuna_cap_handoff_requested",
					"transport":       "tuna",
					"role":            role,
					"direction":       "timer",
					"capReason":       "duration_cap_reached",
					"durationMs":      duration.Milliseconds(),
					"softDurationMs":  delay.Milliseconds(),
					"remainingTimeMs": reserve.Milliseconds(),
				})
			}
		case <-ctx.Done():
		}
	}()
}

func maxInt64(a, b int64) int64 {
	if a >= b {
		return a
	}
	return b
}

func bridgeDirectionEvent(result bridgeDirectionResult) event {
	return event{
		"direction":           result.direction,
		"stage":               result.stage,
		"reason":              safeReason(result.err),
		"terminalReason":      result.terminalReason,
		"durationMs":          result.durationMs,
		"framesForwarded":     result.framesForwarded,
		"payloadBytes":        result.payloadBytes,
		"firstFrameElapsedMs": result.firstFrameElapsedMs,
		"lastFrameElapsedMs":  result.lastFrameElapsedMs,
		"maxReadMs":           result.maxReadMs,
		"maxWriteMs":          result.maxWriteMs,
		"idleGapCount":        result.idleGapCount,
		"maxIdleGapMs":        result.maxIdleGapMs,
		"seqGapCount":         result.seqGapCount,
		"seqGapMissing":       result.seqGapMissing,
		"seqReorderCount":     result.seqReorderCount,
		"sequenceGapsByLane":  result.laneGapSummaries,
		"lastFrameType":       frameTypeName(result.lastFrameType),
		"lastFrameLane":       frameLaneName(result.lastFrameLane),
		"lastFrameSeq":        result.lastFrameSeq,
		"lastFramePayload":    result.lastFramePayload,
	}
}

func bridgeTerminalReason(direction, stage string, err error) string {
	if stage == "cap" {
		return "byte_cap_reached"
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return "duration_cap_reached"
	}
	if errors.Is(err, context.Canceled) {
		return "context_cancelled"
	}
	if stage == "write" {
		if direction == "app_to_tuna" {
			return "tuna_write_failed"
		}
		return "local_write_failed"
	}
	if stage == "read" {
		if direction == "app_to_tuna" {
			return "local_ipc_eof"
		}
		return "tuna_stream_eof"
	}
	if errors.Is(err, net.ErrClosed) {
		return "context_cancelled"
	}
	if stage == "context" {
		return "context_cancelled"
	}
	if err == nil {
		return "normal_close"
	}
	return "unknown_close"
}

func bridgeLaneStatsFor(stats map[byte]*bridgeLaneSequenceStats, lane byte) *bridgeLaneSequenceStats {
	if current, ok := stats[lane]; ok {
		return current
	}
	current := &bridgeLaneSequenceStats{lane: lane}
	stats[lane] = current
	return current
}

func (tracker *bridgeSequenceTracker) observe(frame sidecarFrame) bridgeSequenceObservation {
	if frame.typ != frameTypeData || frame.seq == 0 {
		return bridgeSequenceObservation{}
	}

	if !tracker.seen {
		tracker.seen = true
		tracker.lastSeq = frame.seq
		return bridgeSequenceObservation{}
	}

	previous := tracker.lastSeq
	if frame.seq == previous+1 {
		tracker.lastSeq = frame.seq
		return bridgeSequenceObservation{}
	}

	if tracker.laneStats == nil {
		tracker.laneStats = map[byte]*bridgeLaneSequenceStats{}
	}
	stats := bridgeLaneStatsFor(tracker.laneStats, frame.lane)
	stats.lastPreviousSeq = previous
	stats.lastCurrentSeq = frame.seq

	if frame.seq <= previous {
		tracker.seqReorderCount++
		stats.reorderCount++
		return bridgeSequenceObservation{
			kind:        bridgeSequenceReordered,
			previousSeq: previous,
			currentSeq:  frame.seq,
		}
	}

	missing := int64(frame.seq - previous - 1)
	tracker.seqGapCount++
	tracker.seqGapMissing += missing
	stats.gapCount++
	stats.gapMissing += missing
	tracker.lastSeq = frame.seq

	return bridgeSequenceObservation{
		kind:         bridgeSequenceGap,
		previousSeq:  previous,
		currentSeq:   frame.seq,
		missingCount: missing,
	}
}

func bridgeLaneSequenceSummaries(stats map[byte]*bridgeLaneSequenceStats) []event {
	if len(stats) == 0 {
		return nil
	}
	summaries := make([]event, 0, len(stats))
	for _, stat := range stats {
		if stat.gapCount == 0 && stat.reorderCount == 0 {
			continue
		}
		summaries = append(summaries, event{
			"frameLane":       frameLaneName(stat.lane),
			"seqGapCount":     stat.gapCount,
			"seqGapMissing":   stat.gapMissing,
			"seqReorderCount": stat.reorderCount,
			"previousSeq":     stat.lastPreviousSeq,
			"currentSeq":      stat.lastCurrentSeq,
		})
	}
	return summaries
}

func shouldTraceBridgeFrame(framesForwarded int64) bool {
	return framesForwarded <= bridgeTraceFirstFrames ||
		bridgeTraceEveryFrames > 0 && framesForwarded%bridgeTraceEveryFrames == 0
}

func frameTypeName(typ byte) string {
	switch typ {
	case frameTypeStatus:
		return "status"
	case frameTypeData:
		return "data"
	case frameTypePing:
		return "ping"
	case frameTypePong:
		return "pong"
	case frameTypeClose:
		return "close"
	case 0:
		return "(none)"
	default:
		return fmt.Sprintf("unknown_%d", typ)
	}
}

func frameLaneName(lane byte) string {
	switch lane {
	case 0:
		return "control"
	case 1:
		return "media"
	case 2:
		return "bulk"
	default:
		return fmt.Sprintf("unknown_%d", lane)
	}
}

func watchProviderPaths(ctx context.Context, tunaClient *ts.TunaSessionClient, emit *emitter, role string) {
	ticker := time.NewTicker(providerWatchInterval)
	defer ticker.Stop()
	lastSignature := ""
	connectedCh := tunaClient.OnConnect()
	for {
		select {
		case <-ctx.Done():
			return
		case <-connectedCh:
			connectedCh = nil
			paths := tunaProviderPathDiagnostics(tunaClient.GetPubAddrs())
			lastSignature, _ = paths["pathsHash"].(string)
			emit.emit(event{
				"event":        "tuna_provider_paths",
				"role":         role,
				"stage":        "connected",
				"providerInfo": paths,
			})
		case <-ticker.C:
			paths := tunaProviderPathDiagnostics(tunaClient.GetPubAddrs())
			signature, _ := paths["pathsHash"].(string)
			if signature == lastSignature {
				continue
			}
			lastSignature = signature
			emit.emit(event{
				"event":        "tuna_provider_paths",
				"role":         role,
				"stage":        "changed",
				"providerInfo": paths,
			})
		}
	}
}

func waitForProviderPathReadiness(
	ctx context.Context,
	tunaClient *ts.TunaSessionClient,
	minUsablePaths int,
	timeout time.Duration,
	emit *emitter,
	role string,
	allowDegradedReady bool,
	attempts int) (bool, error) {
	if minUsablePaths <= 0 {
		return true, nil
	}
	if attempts < 1 {
		attempts = 1
	}
	for attempt := 1; attempt <= attempts; attempt++ {
		started := time.Now()
		deadline := started.Add(timeout)
		ticker := time.NewTicker(providerReadyPollInterval)
		for {
			paths := tunaProviderPathDiagnostics(tunaClient.GetPubAddrs())
			usableCount := eventInt(paths, "usableCount")
			if usableCount >= minUsablePaths {
				ticker.Stop()
				emit.emit(event{
					"event":          "tuna_provider_paths_ready",
					"role":           role,
					"usableCount":    usableCount,
					"minProviderCnt": minUsablePaths,
					"attempt":        attempt,
					"maxAttempts":    attempts,
					"paths":          paths,
				})
				return true, nil
			}
			if allowDegradedReady && usableCount >= providerDegradedMinPaths {
				ticker.Stop()
				emit.emit(event{
					"event":               "provider_paths_degraded",
					"role":                role,
					"usableCount":         usableCount,
					"minProviderCnt":      minUsablePaths,
					"degradedProviderCnt": providerDegradedMinPaths,
					"elapsedMs":           time.Since(started).Milliseconds(),
					"attempt":             attempt,
					"maxAttempts":         attempts,
					"paths":               paths,
				})
				return false, fmt.Errorf("Tuna provider path readiness degraded: usable=%d min=%d", usableCount, minUsablePaths)
			}
			if time.Now().After(deadline) {
				eventName := "tuna_provider_paths_ready_timeout"
				if usableCount >= providerDegradedMinPaths {
					eventName = "provider_paths_degraded"
				}
				emit.emit(event{
					"event":          eventName,
					"role":           role,
					"usableCount":    usableCount,
					"minProviderCnt": minUsablePaths,
					"timeoutMs":      timeout.Milliseconds(),
					"attempt":        attempt,
					"maxAttempts":    attempts,
					"willRetry":      attempt < attempts,
					"paths":          paths,
				})
				ticker.Stop()
				if attempt < attempts {
					break
				}
				return false, fmt.Errorf("Tuna provider path readiness timed out: usable=%d min=%d attempts=%d", usableCount, minUsablePaths, attempts)
			}
			select {
			case <-ctx.Done():
				ticker.Stop()
				return false, ctx.Err()
			case <-ticker.C:
			}
		}
	}
	return false, fmt.Errorf("Tuna provider path readiness failed")
}

func tunaProviderPathDiagnostics(pubAddrs *ts.PubAddrs) event {
	info := event{
		"present":       pubAddrs != nil,
		"pathCount":     0,
		"usableCount":   0,
		"sessionClosed": false,
		"pathsHash":     "",
		"paths":         []event{},
	}
	if pubAddrs == nil {
		return info
	}
	paths := make([]event, 0, len(pubAddrs.Addrs))
	signatureParts := make([]string, 0, len(pubAddrs.Addrs))
	usableCount := 0
	for i, addr := range pubAddrs.Addrs {
		path := event{
			"index":  i,
			"usable": false,
		}
		if addr != nil {
			endpoint := fmt.Sprintf("%s:%d", addr.IP, addr.Port)
			path["usable"] = addr.IP != "" && addr.Port != 0
			path["ipClass"] = classifyHost(addr.IP)
			path["ipHash"] = shortHash(addr.IP)
			path["port"] = addr.Port
			path["endpointHash"] = shortHash(endpoint)
			if addr.IP != "" && addr.Port != 0 {
				usableCount++
				signatureParts = append(signatureParts, endpoint)
			}
			if classifyHost(addr.IP) == "public" {
				path["ip"] = addr.IP
			}
			if addr.InPrice != "" {
				path["inPriceNknPerMb"] = addr.InPrice
			}
			if addr.OutPrice != "" {
				path["outPriceNknPerMb"] = addr.OutPrice
			}
		}
		paths = append(paths, path)
	}
	info["pathCount"] = len(paths)
	info["usableCount"] = usableCount
	info["sessionClosed"] = pubAddrs.SessionClosed
	info["pathsHash"] = shortHash(strings.Join(signatureParts, "|"))
	info["paths"] = paths
	return info
}

func eventInt(values event, key string) int {
	if values == nil {
		return 0
	}
	switch value := values[key].(type) {
	case int:
		return value
	case int64:
		return int(value)
	case float64:
		return int(value)
	default:
		return 0
	}
}

func connAddrDiagnostics(addr net.Addr) event {
	info := event{"present": addr != nil}
	if addr == nil {
		return info
	}
	raw := addr.String()
	info["network"] = addr.Network()
	info["addrLen"] = len(raw)
	info["addrHash"] = shortHash(raw)
	host, port, err := net.SplitHostPort(raw)
	if err != nil {
		info["hostClass"] = "opaque"
		return info
	}
	hostClass := classifyHost(host)
	info["hostClass"] = hostClass
	info["hostHash"] = shortHash(host)
	info["port"] = port
	if hostClass == "public" {
		info["host"] = host
	}
	return info
}

func classifyHost(host string) string {
	if host == "" {
		return "empty"
	}
	ip := net.ParseIP(strings.Trim(host, "[]"))
	if ip == nil {
		return "hostname"
	}
	if ip.IsLoopback() {
		return "loopback"
	}
	if ip.IsPrivate() {
		return "private"
	}
	if ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() {
		return "link_local"
	}
	if ip.IsUnspecified() {
		return "unspecified"
	}
	return "public"
}

func shortHash(value string) string {
	if value == "" {
		return ""
	}
	sum := sha256.Sum256([]byte(value))
	return hex.EncodeToString(sum[:8])
}

func writeFrame(w io.Writer, typ byte, lane byte, seq uint64, ts int64, payload []byte) error {
	if len(payload) > frameMaxPayloadSize {
		return fmt.Errorf("frame payload too large: %d", len(payload))
	}
	buf := make([]byte, frameHeaderSize+len(payload))
	binary.BigEndian.PutUint32(buf[0:4], frameMagic)
	buf[4] = frameVersion
	buf[5] = typ
	buf[6] = lane
	buf[7] = 0
	binary.BigEndian.PutUint64(buf[8:16], seq)
	binary.BigEndian.PutUint64(buf[16:24], uint64(ts))
	binary.BigEndian.PutUint32(buf[24:28], uint32(len(payload)))
	binary.BigEndian.PutUint32(buf[28:32], 0)
	copy(buf[frameHeaderSize:], payload)
	for len(buf) > 0 {
		n, err := w.Write(buf)
		if err != nil {
			return err
		}
		if n <= 0 {
			return io.ErrUnexpectedEOF
		}
		buf = buf[n:]
	}
	return nil
}

func readFrame(r io.Reader) (sidecarFrame, error) {
	header, err := readSyncedFrameHeader(r)
	if err != nil {
		return sidecarFrame{}, err
	}
	if header[4] != frameVersion {
		return sidecarFrame{}, fmt.Errorf("unsupported frame version: got %d", header[4])
	}
	payloadLen := binary.BigEndian.Uint32(header[24:28])
	if payloadLen > frameMaxPayloadSize {
		return sidecarFrame{}, fmt.Errorf("frame payload too large")
	}
	payload := make([]byte, payloadLen)
	if payloadLen > 0 {
		if _, err := io.ReadFull(r, payload); err != nil {
			return sidecarFrame{}, err
		}
	}
	return sidecarFrame{
		typ:       header[5],
		lane:      header[6],
		seq:       binary.BigEndian.Uint64(header[8:16]),
		timestamp: int64(binary.BigEndian.Uint64(header[16:24])),
		payload:   payload,
	}, nil
}

func readSyncedFrameHeader(r io.Reader) ([]byte, error) {
	header := make([]byte, frameHeaderSize)
	if _, err := io.ReadFull(r, header[:4]); err != nil {
		return nil, err
	}
	var skipped int
	for binary.BigEndian.Uint32(header[:4]) != frameMagic {
		if skipped >= frameMaxSyncSkipBytes {
			return nil, fmt.Errorf("frame magic not found within sync window")
		}
		copy(header[:3], header[1:4])
		if _, err := io.ReadFull(r, header[3:4]); err != nil {
			return nil, err
		}
		skipped++
	}
	if _, err := io.ReadFull(r, header[4:]); err != nil {
		return nil, err
	}
	return header, nil
}

func openLinkedWallet(cfg *config, emit *emitter) (*walletInfo, error) {
	passwordBytes, err := readWalletPassword(cfg)
	if err != nil {
		return nil, err
	}
	defer zeroBytes(passwordBytes)
	password := string(passwordBytes)
	raw, err := os.ReadFile(cfg.walletPath)
	if err != nil {
		return nil, fmt.Errorf("read wallet file %q failed: %s", filepath.Base(cfg.walletPath), scrubError(err, cfg.walletPath))
	}
	wallet, err := nkn.WalletFromJSON(string(raw), &nkn.WalletConfig{Password: password})
	if err != nil {
		return nil, fmt.Errorf("unlock wallet %q failed: %s", filepath.Base(cfg.walletPath), scrubError(err, cfg.walletPath))
	}
	if err := wallet.VerifyPassword(password); err != nil {
		return nil, fmt.Errorf("wallet password verification failed")
	}
	balance, err := wallet.Balance()
	if err != nil {
		return nil, fmt.Errorf("wallet balance lookup failed: %w", err)
	}
	info := &walletInfo{wallet: wallet, address: wallet.Address(), balance: balance.String()}
	emit.emit(event{
		"event":                "wallet_ready",
		"walletFile":           filepath.Base(cfg.walletPath),
		"walletAddress":        info.address,
		"balanceNkn":           info.balance,
		"appProtocolVersion":   appProtocolVersion,
		"frameProtocolVersion": int(frameVersion),
		"protocolVersion":      int(frameVersion),
		"sidecarVersion":       sidecarVersion,
	})
	return info, nil
}

func readWalletPassword(cfg *config) ([]byte, error) {
	if cfg.passwordStdin {
		raw, err := io.ReadAll(io.LimitReader(os.Stdin, 4097))
		if err != nil {
			return nil, err
		}
		if len(raw) > 4096 {
			zeroBytes(raw)
			return nil, fmt.Errorf("wallet password from stdin is too long")
		}
		out := append([]byte(nil), bytes.TrimRight(raw, "\r\n")...)
		zeroBytes(raw)
		if len(out) == 0 {
			return nil, fmt.Errorf("wallet password cannot be empty")
		}
		return out, nil
	}
	fmt.Fprint(os.Stderr, "Wallet password: ")
	password, err := term.ReadPassword(int(os.Stdin.Fd()))
	fmt.Fprintln(os.Stderr)
	if err != nil {
		return nil, fmt.Errorf("read wallet password failed: %w", err)
	}
	if len(password) == 0 {
		return nil, fmt.Errorf("wallet password cannot be empty")
	}
	return password, nil
}

func verifyMinimumBalance(balanceText, minText string) error {
	balance, err := parseDecimal(balanceText, "wallet balance")
	if err != nil {
		return err
	}
	minimum, err := parsePositiveDecimal(minText, "--min-balance-nkn")
	if err != nil {
		return err
	}
	if balance.Cmp(minimum) < 0 {
		return fmt.Errorf("wallet balance is below configured minimum")
	}
	return nil
}

func accountFromOptionalSeed(cfg *config) (*nkn.Account, error) {
	seedHex := cfg.seedHex
	if cfg.seedStdin {
		stdinSeedHex, err := readSeedHexFromStdin()
		if err != nil {
			return nil, err
		}
		defer zeroBytes(stdinSeedHex)
		seedHex = string(stdinSeedHex)
	}
	seed, err := decodeSeed(seedHex)
	if err != nil {
		return nil, err
	}
	defer zeroBytes(seed)
	return nkn.NewAccount(seed)
}

func readSeedHexFromStdin() ([]byte, error) {
	raw, err := io.ReadAll(io.LimitReader(os.Stdin, 4097))
	if err != nil {
		return nil, fmt.Errorf("read seed from stdin failed: %w", err)
	}
	if len(raw) > 4096 {
		zeroBytes(raw)
		return nil, fmt.Errorf("seed from stdin is too long")
	}
	trimmed := append([]byte(nil), bytes.TrimSpace(raw)...)
	zeroBytes(raw)
	if len(trimmed) == 0 {
		return nil, fmt.Errorf("seed from stdin cannot be empty")
	}
	return trimmed, nil
}

func decodeSeed(seedHex string) ([]byte, error) {
	if strings.TrimSpace(seedHex) == "" {
		return nil, nil
	}
	seed, err := hex.DecodeString(strings.TrimSpace(seedHex))
	if err != nil {
		return nil, usageError("--seed-hex must be valid hex")
	}
	if len(seed) != 32 {
		return nil, usageError("--seed-hex must decode to exactly 32 bytes")
	}
	return seed, nil
}

func newReadyMultiClient(ctx context.Context, account *nkn.Account, cfg *config) (*nkn.MultiClient, error) {
	client, err := nkn.NewMultiClient(account, cfg.identifier, cfg.numSubClients, false, nil)
	if err != nil {
		return nil, fmt.Errorf("create NKN multiclient failed: %w", err)
	}
	if err := waitMultiClientReady(ctx, client, time.Duration(cfg.connectTimeoutSec)*time.Second); err != nil {
		client.Close()
		return nil, err
	}
	return client, nil
}

func waitMultiClientReady(ctx context.Context, client *nkn.MultiClient, timeout time.Duration) error {
	if client.OnConnect == nil {
		return nil
	}
	ready := make(chan struct{})
	go func() {
		client.OnConnect.Next()
		close(ready)
	}()
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	select {
	case <-ready:
		return nil
	case <-timer.C:
		return fmt.Errorf("NKN multiclient connect timed out")
	case <-ctx.Done():
		return ctx.Err()
	}
}

func allowList(cfg *config) (*nkngomobile.StringArray, func(string) bool, error) {
	pattern, err := compileAllowPattern(cfg)
	if err != nil {
		return nil, nil, err
	}
	if cfg.unsafeAllowAny {
		return nil, func(string) bool { return true }, nil
	}
	return nkn.NewStringArray(pattern.String()), pattern.MatchString, nil
}

func validateAllowRemote(cfg *config) error {
	if cfg.unsafeAllowAny {
		return nil
	}
	if strings.TrimSpace(cfg.allowRemote) == "" {
		return usageError("listener requires --allow-remote unless --unsafe-allow-any is set")
	}
	_, err := compileAllowPattern(cfg)
	return err
}

func compileAllowPattern(cfg *config) (*regexp.Regexp, error) {
	if cfg.unsafeAllowAny {
		return regexp.Compile(".*")
	}
	value := strings.TrimSpace(cfg.allowRemote)
	if value == "" {
		return nil, usageError("--allow-remote is required")
	}
	if !cfg.allowRemoteRegex {
		value = "^" + regexp.QuoteMeta(value) + "$"
	}
	re, err := regexp.Compile(value)
	if err != nil {
		return nil, usageError("invalid --allow-remote regex: %v", err)
	}
	return re, nil
}

func parsePositiveDecimal(raw, name string) (*big.Rat, error) {
	value, err := parseDecimal(raw, name)
	if err != nil {
		return nil, err
	}
	if value.Sign() <= 0 {
		return nil, usageError("%s must be positive", name)
	}
	return value, nil
}

func parseDecimal(raw, name string) (*big.Rat, error) {
	value := strings.TrimSpace(raw)
	if value == "" {
		return nil, usageError("%s must not be empty", name)
	}
	r := new(big.Rat)
	if _, ok := r.SetString(value); !ok {
		return nil, usageError("%s must be a decimal number", name)
	}
	return r, nil
}

func ratDecimalString(value *big.Rat, precision int) string {
	if value == nil {
		return "0"
	}

	text := value.FloatString(precision)
	text = strings.TrimRight(text, "0")
	text = strings.TrimRight(text, ".")
	if text == "" || text == "-0" {
		return "0"
	}

	return text
}

func (e *emitter) emit(v any) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.jsonl {
		out := e.out
		if out == nil {
			out = os.Stdout
		}
		_ = json.NewEncoder(out).Encode(v)
		return
	}
	fmt.Fprintf(os.Stderr, "%v\n", v)
}

func hasArg(args []string, wanted string) bool {
	for _, arg := range args {
		if arg == wanted {
			return true
		}
	}
	return false
}

func exitOnCancel(ctx context.Context, code int) {
	<-ctx.Done()
	os.Exit(code)
}

func isTimeout(err error) bool {
	var netErr net.Error
	return errors.As(err, &netErr) && netErr.Timeout()
}

func zeroBytes(b []byte) {
	for i := range b {
		b[i] = 0
	}
}

func scrubError(err error, sensitive string) string {
	if err == nil {
		return ""
	}
	text := err.Error()
	if sensitive != "" {
		text = strings.ReplaceAll(text, sensitive, filepath.Base(sensitive))
	}
	return text
}

func safeReason(err error) string {
	if err == nil {
		return ""
	}
	return scrubError(err, "")
}

func usageError(format string, args ...any) error {
	return fmt.Errorf("%s\n\n%s", fmt.Sprintf(format, args...), usage())
}

func usage() string {
	return strings.TrimSpace(`Usage:
  nlink-tuna-sidecar.exe address --seed-stdin --jsonl
  nlink-tuna-sidecar.exe version --jsonl
  nlink-tuna-sidecar.exe wallet-status --wallet wallet.json --password-stdin --jsonl
  nlink-tuna-sidecar.exe listen --wallet wallet.json --password-prompt --allow-remote <addr> --max-price-nkn-per-mb <nkn> --max-total-mib 256 --max-duration-sec 600 --local-ipc 127.0.0.1:0 --jsonl
  nlink-tuna-sidecar.exe dial --to <listener-tuna-address> --local-ipc 127.0.0.1:0 --seed-stdin --jsonl

Notes:
  version emits sidecar compatibility metadata and exits without NKN, wallet, listen, dial, or payment.
  wallet-status unlocks the linked wallet, emits public address/balance, and exits without Tuna listen/dial/payment.
  listen uses a linked low-balance wallet and never logs the full wallet path.
  dial does not need a wallet; --seed-stdin is for deterministic developer test identity only.
  local IPC carries opaque nLink EnvelopeCodec frames only; app payload security stays above Tuna.`)
}
