package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"math/big"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"regexp"
	"sort"
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
	appName = "nlink-tuna-poc"

	transportTuna     = "tuna"
	transportBaseline = "baseline"

	modeAddress      = "address"
	modeCreateWallet = "create-wallet"
	modeListen       = "listen"
	modeDial         = "dial"
	modeBaseline     = "baseline"

	roleListen = "listen"
	roleDial   = "dial"

	profileFile   = "file"
	profileScreen = "screen"
	profilePing   = "ping"
	profileMixed  = "mixed"

	wireMagic   uint32 = 0x4e545030 // "NTP0"
	wireVersion byte   = 1
	headerSize         = 28

	frameHello   byte = 1
	frameData    byte = 2
	framePing    byte = 3
	framePong    byte = 4
	frameFinish  byte = 5
	frameSummary byte = 6
	frameSample  byte = 7
	frameAck     byte = 8

	defaultIdentifier        = "nlink-tuna-poc"
	defaultNumSubClients     = 4
	defaultConnectTimeoutSec = 30
	defaultDurationSec       = 60
	defaultListenDurationSec = 120
	defaultMaxTotalMiB       = 64
	defaultMinBalanceNKN     = "0.00000001"
	defaultFileWriteSize     = 64 * 1024
	defaultFileAckEveryKiB   = 64
	defaultFileInflightKiB   = 256
	defaultWriteDeadlineMs   = 10_000
	defaultScreenFrameBytes  = 24 * 1024
	defaultScreenKeyBytes    = 128 * 1024
	defaultScreenFPS         = 10
	defaultPingBytes         = 128
	defaultWalletOutPath     = "artifacts/tuna-poc/wallet-test-nkn.json"
	maxFramePayloadBytes     = 1024 * 1024
	maxSyntheticTotalMiB     = 256
)

type cliConfig struct {
	mode              string
	role              string
	transport         string
	jsonl             bool
	identifier        string
	seedHex           string
	numSubClients     int
	connectTimeoutSec int
	outputRoot        string

	walletPath           string
	walletOutPath        string
	overwriteWallet      bool
	passwordPrompt       bool
	passwordStdin        bool
	allowRemote          string
	allowRemoteRegex     bool
	unsafeAllowAny       bool
	maxPriceNKNPerMB     string
	minBalanceNKN        string
	maxTotalMiB          int
	maxDurationSec       int
	acceptTimeoutSec     int
	tunaNumListeners     int
	tunaDialTimeoutMs    int
	tunaServiceName      string
	tunaMeasureBandwidth bool

	to              string
	profile         string
	durationSec     int
	dialTimeoutMs   int
	writeSize       int
	fileAckKiB      int
	fileWindowKiB   int
	filePaceMbps    float64
	writeDeadlineMs int
	fps             int
	pingBytes       int
}

type emitter struct {
	jsonl bool
	mu    sync.Mutex
}

type event map[string]any

type helloPayload struct {
	Profile           string  `json:"profile"`
	DurationMs        int64   `json:"durationMs"`
	MaxBytes          int64   `json:"maxBytes"`
	FPS               int     `json:"fps"`
	WriteSizeBytes    int     `json:"writeSizeBytes"`
	PingBytes         int     `json:"pingBytes"`
	FileAckEveryBytes int     `json:"fileAckEveryBytes,omitempty"`
	FileWindowBytes   int     `json:"fileWindowBytes,omitempty"`
	FilePaceMbps      float64 `json:"filePaceMbps,omitempty"`
}

type samplePayload struct {
	Event          string  `json:"event"`
	Transport      string  `json:"transport"`
	Profile        string  `json:"profile"`
	Role           string  `json:"role"`
	BytesSent      int64   `json:"bytesSent"`
	BytesReceived  int64   `json:"bytesReceived"`
	ThroughputMbps float64 `json:"throughputMbps"`
	RTTP50Ms       int64   `json:"rttP50Ms,omitempty"`
	RTTP95Ms       int64   `json:"rttP95Ms,omitempty"`
	RTTP99Ms       int64   `json:"rttP99Ms,omitempty"`
	Stalls         int64   `json:"stalls,omitempty"`
}

type summaryPayload struct {
	Event          string  `json:"event"`
	Transport      string  `json:"transport"`
	Profile        string  `json:"profile"`
	Role           string  `json:"role"`
	DurationMs     int64   `json:"durationMs"`
	BytesSent      int64   `json:"bytesSent"`
	BytesReceived  int64   `json:"bytesReceived"`
	ThroughputMbps float64 `json:"throughputMbps"`
	RTTP50Ms       int64   `json:"rttP50Ms"`
	RTTP95Ms       int64   `json:"rttP95Ms"`
	RTTP99Ms       int64   `json:"rttP99Ms"`
	PingCount      int     `json:"pingCount"`
	Stalls         int64   `json:"stalls"`
	CapReached     bool    `json:"capReached"`
	FallbackReason string  `json:"fallbackReason"`
}

type benchCounters struct {
	bytesSent     atomic.Int64
	bytesReceived atomic.Int64
	bytesAcked    atomic.Int64
	stalls        atomic.Int64
}

type rttStats struct {
	mu   sync.Mutex
	vals []time.Duration
}

type frame struct {
	typ       byte
	seq       uint64
	timestamp int64
	payload   []byte
}

type walletInfo struct {
	wallet  *nkn.Wallet
	address string
	balance string
}

func storeMaxInt64(target *atomic.Int64, value int64) {
	for {
		current := target.Load()
		if value <= current {
			return
		}
		if target.CompareAndSwap(current, value) {
			return
		}
	}
}

func main() {
	jsonl := hasArg(os.Args[1:], "--jsonl")
	emit := &emitter{jsonl: jsonl}
	if err := run(os.Args[1:], emit); err != nil {
		emit.emit(event{
			"event":  "error",
			"reason": safeReason(err),
		})
		os.Exit(1)
	}
}

func run(args []string, emit *emitter) error {
	if len(args) == 0 {
		return usageError("missing mode: use address, listen, dial, or baseline")
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
	case modeCreateWallet:
		return runCreateWallet(cfg, emit)
	case modeListen:
		return runTunaListen(ctx, cfg, emit)
	case modeDial:
		return runTunaDial(ctx, cfg, emit)
	case modeBaseline:
		if cfg.role == roleListen {
			return runBaselineListen(ctx, cfg, emit)
		}
		return runBaselineDial(ctx, cfg, emit)
	default:
		return usageError("unsupported mode %q", cfg.mode)
	}
}

func parseArgs(args []string) (*cliConfig, error) {
	cfg := &cliConfig{
		mode:              args[0],
		jsonl:             false,
		identifier:        defaultIdentifier,
		numSubClients:     defaultNumSubClients,
		connectTimeoutSec: defaultConnectTimeoutSec,
		outputRoot:        filepath.Join("artifacts", "tuna-poc"),
		minBalanceNKN:     defaultMinBalanceNKN,
		maxTotalMiB:       defaultMaxTotalMiB,
		maxDurationSec:    defaultListenDurationSec,
		durationSec:       defaultDurationSec,
		dialTimeoutMs:     60_000,
		writeSize:         defaultFileWriteSize,
		fileAckKiB:        defaultFileAckEveryKiB,
		fileWindowKiB:     defaultFileInflightKiB,
		writeDeadlineMs:   defaultWriteDeadlineMs,
		fps:               defaultScreenFPS,
		pingBytes:         defaultPingBytes,
		profile:           profileFile,
		tunaNumListeners:  0,
		tunaDialTimeoutMs: 0,
		walletOutPath:     defaultWalletOutPath,
	}

	fs := flag.NewFlagSet(args[0], flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	addCommonFlags(fs, cfg)

	switch args[0] {
	case modeAddress:
		addIdentityWalletFlags(fs, cfg)
	case modeCreateWallet:
		addCreateWalletFlags(fs, cfg)
	case modeListen:
		cfg.transport = transportTuna
		addIdentityWalletFlags(fs, cfg)
		addListenFlags(fs, cfg, true)
	case modeDial:
		cfg.transport = transportTuna
		addTunaClientFlags(fs, cfg)
		addDialFlags(fs, cfg, true)
	case modeBaseline:
		cfg.transport = transportBaseline
		fs.StringVar(&cfg.role, "role", "", "baseline role: listen or dial")
		addListenFlags(fs, cfg, false)
		addDialFlags(fs, cfg, false)
	default:
		return nil, usageError("unsupported mode %q", args[0])
	}

	if err := fs.Parse(args[1:]); err != nil {
		return nil, err
	}

	if err := validateConfig(cfg); err != nil {
		return nil, err
	}
	return cfg, nil
}

func addCommonFlags(fs *flag.FlagSet, cfg *cliConfig) {
	fs.BoolVar(&cfg.jsonl, "jsonl", false, "emit machine-readable JSONL events")
	fs.StringVar(&cfg.identifier, "identifier", defaultIdentifier, "NKN identifier")
	fs.StringVar(&cfg.seedHex, "seed-hex", "", "optional 32-byte hex seed for stable POC identity; never logged")
	fs.IntVar(&cfg.numSubClients, "num-subclients", defaultNumSubClients, "NKN multiclient subclient count")
	fs.IntVar(&cfg.connectTimeoutSec, "connect-timeout-sec", defaultConnectTimeoutSec, "NKN connect/listen timeout in seconds")
	fs.StringVar(&cfg.outputRoot, "output-root", filepath.Join("artifacts", "tuna-poc"), "artifact output root")
}

func addIdentityWalletFlags(fs *flag.FlagSet, cfg *cliConfig) {
	fs.StringVar(&cfg.walletPath, "wallet", "", "linked NKN wallet.json path")
	fs.BoolVar(&cfg.passwordPrompt, "password-prompt", false, "prompt for wallet password without echo")
	fs.BoolVar(&cfg.passwordStdin, "password-stdin", false, "test automation: read wallet password from stdin; prefer --password-prompt for manual runs")
}

func addCreateWalletFlags(fs *flag.FlagSet, cfg *cliConfig) {
	fs.StringVar(&cfg.walletOutPath, "out", defaultWalletOutPath, "encrypted wallet JSON output path")
	fs.BoolVar(&cfg.passwordPrompt, "password-prompt", false, "prompt for new wallet password without echo")
	fs.BoolVar(&cfg.overwriteWallet, "overwrite", false, "overwrite existing wallet output path")
}

func addListenFlags(fs *flag.FlagSet, cfg *cliConfig, paid bool) {
	fs.StringVar(&cfg.allowRemote, "allow-remote", "", "allowed remote NKN address; exact by default, regex when --allow-remote-regex is set")
	fs.BoolVar(&cfg.allowRemoteRegex, "allow-remote-regex", false, "treat --allow-remote as an NKN address regular expression")
	fs.BoolVar(&cfg.unsafeAllowAny, "unsafe-allow-any", false, "unsafe: accept any remote address")
	fs.IntVar(&cfg.maxTotalMiB, "max-total-mib", defaultMaxTotalMiB, "local benchmark byte cap in MiB")
	fs.IntVar(&cfg.maxDurationSec, "max-duration-sec", defaultListenDurationSec, "local benchmark duration cap in seconds")
	fs.IntVar(&cfg.acceptTimeoutSec, "accept-timeout-sec", 0, "optional listener timeout while waiting for one accepted benchmark connection; 0 disables")
	if paid {
		fs.StringVar(&cfg.maxPriceNKNPerMB, "max-price-nkn-per-mb", "", "maximum Tuna price to pay in NKN per MB")
		fs.StringVar(&cfg.minBalanceNKN, "min-balance-nkn", defaultMinBalanceNKN, "minimum wallet balance required before paid listen starts")
		fs.IntVar(&cfg.tunaNumListeners, "tuna-listeners", 0, "Tuna listener count override; 0 uses library default")
		fs.StringVar(&cfg.tunaServiceName, "tuna-service-name", "", "Tuna service name override")
		fs.BoolVar(&cfg.tunaMeasureBandwidth, "tuna-measure-bandwidth", false, "allow Tuna library bandwidth measurement")
		addTunaClientFlags(fs, cfg)
	}
}

func addTunaClientFlags(fs *flag.FlagSet, cfg *cliConfig) {
	fs.IntVar(&cfg.tunaDialTimeoutMs, "tuna-dial-timeout-ms", 0, "Tuna dial timeout override in milliseconds; 0 uses library default")
}

func addDialFlags(fs *flag.FlagSet, cfg *cliConfig, includeMaxTotal bool) {
	fs.StringVar(&cfg.to, "to", "", "remote NKN/Tuna address")
	fs.StringVar(&cfg.profile, "profile", profileFile, "benchmark profile: file, screen, ping, mixed")
	fs.IntVar(&cfg.durationSec, "duration-sec", defaultDurationSec, "dial-side benchmark duration in seconds")
	fs.IntVar(&cfg.dialTimeoutMs, "dial-timeout-ms", 60_000, "NKN session dial timeout in milliseconds")
	if includeMaxTotal {
		fs.IntVar(&cfg.maxTotalMiB, "max-total-mib", defaultMaxTotalMiB, "dial-side maximum synthetic bytes in MiB")
	}
	fs.IntVar(&cfg.writeSize, "write-size", defaultFileWriteSize, "file profile write size in bytes")
	fs.IntVar(&cfg.fileAckKiB, "file-ack-every-kib", defaultFileAckEveryKiB, "file profile ACK cadence in KiB; 0 disables data ACKs")
	fs.IntVar(&cfg.fileWindowKiB, "file-inflight-kib", defaultFileInflightKiB, "file profile maximum unacknowledged data in KiB; 0 disables ACK window pacing")
	fs.Float64Var(&cfg.filePaceMbps, "file-pace-mbps", 0, "optional file profile sender pacing target in Mbps; 0 disables rate pacing")
	fs.IntVar(&cfg.writeDeadlineMs, "write-deadline-ms", defaultWriteDeadlineMs, "per-frame write deadline in milliseconds; 0 disables")
	fs.IntVar(&cfg.fps, "fps", defaultScreenFPS, "screen/mixed profile synthetic frame rate")
	fs.IntVar(&cfg.pingBytes, "ping-bytes", defaultPingBytes, "ping payload size in bytes")
}

func validateConfig(cfg *cliConfig) error {
	if cfg.numSubClients < 1 || cfg.numSubClients > 16 {
		return usageError("--num-subclients must be between 1 and 16")
	}
	if cfg.connectTimeoutSec < 1 {
		return usageError("--connect-timeout-sec must be positive")
	}
	if cfg.acceptTimeoutSec < 0 {
		return usageError("--accept-timeout-sec must not be negative")
	}
	if cfg.tunaDialTimeoutMs < 0 {
		return usageError("--tuna-dial-timeout-ms must not be negative")
	}
	if cfg.dialTimeoutMs < 0 {
		return usageError("--dial-timeout-ms must not be negative")
	}
	if cfg.seedHex != "" {
		if _, err := decodeSeed(cfg.seedHex); err != nil {
			return err
		}
	}

	switch cfg.mode {
	case modeAddress:
		if cfg.walletPath != "" {
			if err := validateWalletPasswordInput(cfg, "address mode with --wallet"); err != nil {
				return err
			}
		}
	case modeCreateWallet:
		if !cfg.passwordPrompt {
			return usageError("create-wallet requires --password-prompt")
		}
		if strings.TrimSpace(cfg.walletOutPath) == "" {
			return usageError("create-wallet requires --out")
		}
		if cfg.seedHex != "" {
			return usageError("create-wallet always generates a new random account; do not pass --seed-hex")
		}
	case modeListen:
		if strings.TrimSpace(cfg.walletPath) == "" {
			return usageError("paid Tuna listen requires --wallet")
		}
		if err := validateWalletPasswordInput(cfg, "paid Tuna listen"); err != nil {
			return err
		}
		if strings.TrimSpace(cfg.maxPriceNKNPerMB) == "" {
			return usageError("paid Tuna listen requires --max-price-nkn-per-mb")
		}
		if _, err := parsePositiveDecimal(cfg.maxPriceNKNPerMB, "--max-price-nkn-per-mb"); err != nil {
			return err
		}
		if _, err := parsePositiveDecimal(cfg.minBalanceNKN, "--min-balance-nkn"); err != nil {
			return err
		}
		if err := validateListenerCaps(cfg); err != nil {
			return err
		}
		if err := validateAllowRemote(cfg); err != nil {
			return err
		}
	case modeDial:
		if err := validateDial(cfg); err != nil {
			return err
		}
	case modeBaseline:
		if cfg.role != roleListen && cfg.role != roleDial {
			return usageError("baseline requires --role listen or --role dial")
		}
		if cfg.role == roleListen {
			if err := validateListenerCaps(cfg); err != nil {
				return err
			}
			if err := validateAllowRemote(cfg); err != nil {
				return err
			}
		} else if err := validateDial(cfg); err != nil {
			return err
		}
	}
	return nil
}

func validateListenerCaps(cfg *cliConfig) error {
	if cfg.maxTotalMiB < 1 || cfg.maxTotalMiB > maxSyntheticTotalMiB {
		return usageError("--max-total-mib must be between 1 and %d", maxSyntheticTotalMiB)
	}
	if cfg.maxDurationSec < 1 {
		return usageError("--max-duration-sec must be positive")
	}
	return nil
}

func validateWalletPasswordInput(cfg *cliConfig, context string) error {
	if cfg.passwordPrompt && cfg.passwordStdin {
		return usageError("%s requires exactly one of --password-prompt or --password-stdin", context)
	}
	if !cfg.passwordPrompt && !cfg.passwordStdin {
		return usageError("%s requires --password-prompt or --password-stdin", context)
	}
	return nil
}

func validateDial(cfg *cliConfig) error {
	if strings.TrimSpace(cfg.to) == "" {
		return usageError("dial requires --to")
	}
	if !isValidProfile(cfg.profile) {
		return usageError("--profile must be one of file, screen, ping, mixed")
	}
	if cfg.durationSec < 1 {
		return usageError("--duration-sec must be positive")
	}
	if cfg.maxTotalMiB < 1 || cfg.maxTotalMiB > maxSyntheticTotalMiB {
		return usageError("--max-total-mib must be between 1 and %d", maxSyntheticTotalMiB)
	}
	if cfg.writeSize < 1024 || cfg.writeSize > maxFramePayloadBytes {
		return usageError("--write-size must be between 1024 and %d", maxFramePayloadBytes)
	}
	if cfg.fileAckKiB < 0 || cfg.fileAckKiB > 16*1024 {
		return usageError("--file-ack-every-kib must be between 0 and 16384")
	}
	if cfg.fileWindowKiB < 0 || cfg.fileWindowKiB > 16*1024 {
		return usageError("--file-inflight-kib must be between 0 and 16384")
	}
	if cfg.fileWindowKiB > 0 && cfg.fileAckKiB == 0 {
		return usageError("--file-inflight-kib requires --file-ack-every-kib > 0")
	}
	if cfg.fileWindowKiB > 0 && cfg.fileAckKiB > cfg.fileWindowKiB {
		return usageError("--file-ack-every-kib must be less than or equal to --file-inflight-kib when window pacing is enabled")
	}
	if cfg.filePaceMbps < 0 || cfg.filePaceMbps > 10_000 {
		return usageError("--file-pace-mbps must be between 0 and 10000")
	}
	if cfg.writeDeadlineMs < 0 {
		return usageError("--write-deadline-ms must not be negative")
	}
	if cfg.fps < 1 || cfg.fps > 30 {
		return usageError("--fps must be between 1 and 30")
	}
	if cfg.pingBytes < 8 || cfg.pingBytes > 4096 {
		return usageError("--ping-bytes must be between 8 and 4096")
	}
	return nil
}

func validateAllowRemote(cfg *cliConfig) error {
	if cfg.unsafeAllowAny {
		return nil
	}
	if strings.TrimSpace(cfg.allowRemote) == "" {
		return usageError("listener requires --allow-remote unless --unsafe-allow-any is set")
	}
	_, err := compileAllowPattern(cfg)
	return err
}

func runAddress(ctx context.Context, cfg *cliConfig, emit *emitter) error {
	account, walletAddress, balance, err := resolveAddressAccount(cfg)
	if err != nil {
		return err
	}

	client, err := newReadyMultiClient(ctx, account, cfg)
	if err != nil {
		return err
	}
	defer client.Close()

	ev := event{
		"event":     "ready",
		"role":      "address",
		"transport": "nkn",
		"address":   client.Address(),
	}
	if walletAddress != "" {
		ev["walletAddress"] = walletAddress
		ev["balanceNkn"] = balance
	}
	emit.emit(ev)
	return nil
}

func runCreateWallet(cfg *cliConfig, emit *emitter) error {
	outPath, err := filepath.Abs(strings.TrimSpace(cfg.walletOutPath))
	if err != nil {
		return fmt.Errorf("resolve wallet output path failed: %w", err)
	}

	if _, err := os.Stat(outPath); err == nil && !cfg.overwriteWallet {
		return fmt.Errorf("wallet output file already exists: %s; pass --overwrite to replace it", filepath.Base(outPath))
	} else if err != nil && !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("check wallet output path %q failed: %s", filepath.Base(outPath), scrubError(err, outPath))
	}

	passwordBytes, err := promptNewWalletPassword()
	if err != nil {
		return err
	}
	defer zeroBytes(passwordBytes)
	password := string(passwordBytes)

	account, err := nkn.NewAccount(nil)
	if err != nil {
		return fmt.Errorf("create NKN account failed: %w", err)
	}
	wallet, err := nkn.NewWallet(account, &nkn.WalletConfig{Password: password})
	if err != nil {
		return fmt.Errorf("create encrypted NKN wallet failed: %w", err)
	}
	walletJSON, err := wallet.ToJSON()
	if err != nil {
		return fmt.Errorf("serialize encrypted wallet JSON failed: %w", err)
	}

	if err := os.MkdirAll(filepath.Dir(outPath), 0o755); err != nil {
		return fmt.Errorf("create wallet output directory failed: %s", scrubError(err, filepath.Dir(outPath)))
	}
	if err := os.WriteFile(outPath, []byte(walletJSON), 0o600); err != nil {
		return fmt.Errorf("write encrypted wallet %q failed: %s", filepath.Base(outPath), scrubError(err, outPath))
	}

	loaded, err := nkn.WalletFromJSON(walletJSON, &nkn.WalletConfig{Password: password})
	if err != nil {
		return fmt.Errorf("verify newly written wallet failed: %w", err)
	}
	if err := loaded.VerifyPassword(password); err != nil {
		return fmt.Errorf("verify newly written wallet password failed")
	}
	if loaded.Address() != wallet.Address() {
		return fmt.Errorf("verify newly written wallet address mismatch")
	}

	emit.emit(event{
		"event":         "wallet_created",
		"walletAddress": wallet.Address(),
		"walletFile":    filepath.Base(outPath),
	})
	return nil
}

func runTunaListen(ctx context.Context, cfg *cliConfig, emit *emitter) error {
	wallet, err := openLinkedWallet(cfg, emit)
	if err != nil {
		return err
	}

	minBalance, _ := parsePositiveDecimal(cfg.minBalanceNKN, "--min-balance-nkn")
	balance, err := parseDecimal(wallet.balance, "wallet balance")
	if err != nil {
		return err
	}
	if balance.Cmp(minBalance) < 0 {
		return fmt.Errorf("wallet balance is below configured minimum")
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

	tunaConfig := buildTunaConfig(cfg)
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

	if err := tunaClient.Listen(allow); err != nil {
		return fmt.Errorf("Tuna listen failed: %w", err)
	}
	// nkn-tuna-session v0.2.6 can panic during listener shutdown because an
	// internal goroutine keeps reading from the NKN client after Close. Once
	// paid listening starts, this short-lived POC lets process exit clean up.
	cleanupBeforeListen = false

	waitTunaExit(ctx, tunaClient, cfg, emit)
	emit.emit(event{
		"event":          "ready",
		"role":           "listener",
		"transport":      transportTuna,
		"address":        tunaClient.Address(),
		"walletAddress":  wallet.address,
		"balanceNkn":     wallet.balance,
		"walletFile":     filepath.Base(cfg.walletPath),
		"maxTotalMiB":    cfg.maxTotalMiB,
		"maxDurationSec": cfg.maxDurationSec,
	})

	go exitOnCancel(ctx, 130)
	conn, err := acceptOne(ctx, time.Duration(cfg.acceptTimeoutSec)*time.Second, tunaClient.Accept)
	if err != nil {
		return fmt.Errorf("Tuna accept failed: %w", err)
	}
	return serveAcceptedConn(ctx, conn, cfg, emit, transportTuna, allowCheck)
}

func runTunaDial(ctx context.Context, cfg *cliConfig, emit *emitter) error {
	account, err := accountFromOptionalSeed(cfg.seedHex)
	if err != nil {
		return err
	}
	client, err := newReadyMultiClient(ctx, account, cfg)
	if err != nil {
		return err
	}
	defer client.Close()

	tunaConfig := buildTunaConfig(cfg)
	tunaClient, err := ts.NewTunaSessionClient(account, client, nil, tunaConfig)
	if err != nil {
		return fmt.Errorf("create Tuna session client failed: %w", err)
	}
	defer tunaClient.Close()

	emit.emit(event{
		"event":     "ready",
		"role":      "dialer",
		"transport": transportTuna,
		"address":   tunaClient.Address(),
		"to":        cfg.to,
	})

	go closeOnCancel(ctx, tunaClient.Close, client.Close)
	conn, err := dialTunaSession(tunaClient, cfg)
	if err != nil {
		return fmt.Errorf("Tuna dial failed: %w", err)
	}
	return runDialBenchmark(ctx, conn, cfg, emit, transportTuna)
}

func dialTunaSession(tunaClient *ts.TunaSessionClient, cfg *cliConfig) (net.Conn, error) {
	timeoutMs := cfg.tunaDialTimeoutMs
	if timeoutMs <= 0 {
		timeoutMs = cfg.dialTimeoutMs
	}
	if timeoutMs <= 0 {
		return tunaClient.Dial(cfg.to)
	}

	return tunaClient.DialWithConfig(cfg.to, &nkn.DialConfig{
		DialTimeout: int32(timeoutMs),
	})
}

func runBaselineListen(ctx context.Context, cfg *cliConfig, emit *emitter) error {
	account, err := accountFromOptionalSeed(cfg.seedHex)
	if err != nil {
		return err
	}
	client, err := newReadyMultiClient(ctx, account, cfg)
	if err != nil {
		return err
	}
	defer client.Close()

	allow, allowCheck, err := allowList(cfg)
	if err != nil {
		return err
	}
	if err := client.Listen(allow); err != nil {
		return fmt.Errorf("baseline listen failed: %w", err)
	}

	emit.emit(event{
		"event":          "ready",
		"role":           "listener",
		"transport":      transportBaseline,
		"address":        client.Address(),
		"maxTotalMiB":    cfg.maxTotalMiB,
		"maxDurationSec": cfg.maxDurationSec,
	})

	go closeOnCancel(ctx, client.Close)
	conn, err := acceptOne(ctx, time.Duration(cfg.acceptTimeoutSec)*time.Second, client.Accept)
	if err != nil {
		return fmt.Errorf("baseline accept failed: %w", err)
	}
	return serveAcceptedConn(ctx, conn, cfg, emit, transportBaseline, allowCheck)
}

func runBaselineDial(ctx context.Context, cfg *cliConfig, emit *emitter) error {
	account, err := accountFromOptionalSeed(cfg.seedHex)
	if err != nil {
		return err
	}
	client, err := newReadyMultiClient(ctx, account, cfg)
	if err != nil {
		return err
	}
	defer client.Close()

	emit.emit(event{
		"event":     "ready",
		"role":      "dialer",
		"transport": transportBaseline,
		"address":   client.Address(),
		"to":        cfg.to,
	})

	go closeOnCancel(ctx, client.Close)
	conn, err := dialBaselineSession(client, cfg)
	if err != nil {
		return fmt.Errorf("baseline dial failed: %w", err)
	}
	return runDialBenchmark(ctx, conn, cfg, emit, transportBaseline)
}

func dialBaselineSession(client *nkn.MultiClient, cfg *cliConfig) (net.Conn, error) {
	if cfg.dialTimeoutMs <= 0 {
		return client.Dial(cfg.to)
	}
	return client.DialWithConfig(cfg.to, &nkn.DialConfig{
		DialTimeout: int32(cfg.dialTimeoutMs),
	})
}

func acceptOne(ctx context.Context, timeout time.Duration, accept func() (net.Conn, error)) (net.Conn, error) {
	type acceptResult struct {
		conn net.Conn
		err  error
	}
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

func serveAcceptedConn(ctx context.Context, conn net.Conn, cfg *cliConfig, emit *emitter, transport string, allowCheck func(string) bool) error {
	defer conn.Close()

	remote := ""
	if conn.RemoteAddr() != nil {
		remote = conn.RemoteAddr().String()
	}
	if !allowCheck(remote) {
		return fmt.Errorf("remote address rejected by allow-list")
	}

	summary, err := serveBenchmarkConn(ctx, conn, cfg, emit, transport)
	if summary != nil {
		if writeErr := writeSummaryArtifact(cfg, transport, roleListen, summary); writeErr != nil {
			emit.emit(event{"event": "artifact_error", "reason": safeReason(writeErr)})
		}
	}
	return err
}

func serveBenchmarkConn(ctx context.Context, conn net.Conn, cfg *cliConfig, emit *emitter, transport string) (*summaryPayload, error) {
	first, err := readFrame(conn)
	if err != nil {
		return nil, fmt.Errorf("read benchmark hello failed: %w", err)
	}
	if first.typ != frameHello {
		return nil, fmt.Errorf("first frame was not benchmark hello")
	}

	var hello helloPayload
	if err := json.Unmarshal(first.payload, &hello); err != nil {
		return nil, fmt.Errorf("decode benchmark hello failed: %w", err)
	}
	if !isValidProfile(hello.Profile) {
		return nil, fmt.Errorf("invalid benchmark profile %q", hello.Profile)
	}

	maxBytes := int64(cfg.maxTotalMiB) * 1024 * 1024
	if hello.MaxBytes > 0 && hello.MaxBytes < maxBytes {
		maxBytes = hello.MaxBytes
	}
	maxDuration := time.Duration(cfg.maxDurationSec) * time.Second
	if hello.DurationMs > 0 {
		dialDuration := time.Duration(hello.DurationMs) * time.Millisecond
		if dialDuration < maxDuration {
			maxDuration = dialDuration
		}
	}

	start := time.Now()
	counters := &benchCounters{}
	stopSamples := make(chan struct{})
	var writeMu sync.Mutex
	go emitServerSamples(stopSamples, conn, &writeMu, counters, emit, transport, hello.Profile, start)
	defer close(stopSamples)

	capReached := false
	fallbackReason := ""
	var lastErr error
	lastAckedBytes := int64(0)
	fileAckEveryBytes := int64(hello.FileAckEveryBytes)

	for {
		if time.Since(start) >= maxDuration {
			capReached = true
			fallbackReason = "duration_cap_reached"
			break
		}
		_ = conn.SetReadDeadline(time.Now().Add(time.Second))
		fr, err := readFrame(conn)
		if err != nil {
			if isTimeout(err) {
				continue
			}
			if errors.Is(err, io.EOF) {
				lastErr = nil
				break
			}
			lastErr = err
			break
		}

		switch fr.typ {
		case frameData:
			received := counters.bytesReceived.Add(int64(len(fr.payload)))
			if hello.Profile == profileFile && fileAckEveryBytes > 0 && received-lastAckedBytes >= fileAckEveryBytes {
				lastAckedBytes = received
				err = writeFrameLocked(conn, &writeMu, writeDeadlineFromConfig(cfg), frameAck, uint64(received), time.Now().UnixNano(), nil)
				if err != nil {
					lastErr = err
					goto done
				}
			}
			if received >= maxBytes {
				capReached = true
				fallbackReason = "byte_cap_reached"
				goto done
			}
		case framePing:
			counters.bytesReceived.Add(int64(len(fr.payload)))
			err = writeFrameLocked(conn, &writeMu, writeDeadlineFromConfig(cfg), framePong, fr.seq, fr.timestamp, fr.payload)
			if err != nil {
				lastErr = err
				goto done
			}
			counters.bytesSent.Add(int64(len(fr.payload)))
		case frameFinish:
			goto done
		default:
			lastErr = fmt.Errorf("unexpected frame type %d", fr.typ)
			goto done
		}
	}

done:
	duration := time.Since(start)
	summary := &summaryPayload{
		Event:          "summary",
		Transport:      transport,
		Profile:        hello.Profile,
		Role:           "listener",
		DurationMs:     duration.Milliseconds(),
		BytesSent:      counters.bytesSent.Load(),
		BytesReceived:  counters.bytesReceived.Load(),
		ThroughputMbps: mbps(counters.bytesReceived.Load(), duration),
		CapReached:     capReached,
		FallbackReason: fallbackReason,
	}

	if hello.Profile == profileFile && fileAckEveryBytes > 0 && counters.bytesReceived.Load() > lastAckedBytes {
		if ackErr := writeFrameLocked(conn, &writeMu, writeDeadlineFromConfig(cfg), frameAck, uint64(counters.bytesReceived.Load()), time.Now().UnixNano(), nil); ackErr != nil && lastErr == nil {
			lastErr = ackErr
		}
	}

	payload, _ := json.Marshal(summary)
	writeErr := writeFrameLocked(conn, &writeMu, writeDeadlineFromConfig(cfg), frameSummary, 0, time.Now().UnixNano(), payload)
	emit.emit(summaryEvent(summary))
	if writeErr != nil && lastErr == nil {
		lastErr = writeErr
	}
	if writeErr == nil {
		// NKN/NCP session close can race the final summary frame on slow overlay
		// paths. Keep the listener alive briefly so the dialer can consume it.
		time.Sleep(2 * time.Second)
	}
	return summary, lastErr
}

func emitServerSamples(stop <-chan struct{}, conn net.Conn, writeMu *sync.Mutex, counters *benchCounters, emit *emitter, transport, profile string, start time.Time) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-stop:
			return
		case <-ticker.C:
			sample := samplePayload{
				Event:          "sample",
				Transport:      transport,
				Profile:        profile,
				Role:           "listener",
				BytesSent:      counters.bytesSent.Load(),
				BytesReceived:  counters.bytesReceived.Load(),
				ThroughputMbps: mbps(counters.bytesReceived.Load(), time.Since(start)),
			}
			payload, _ := json.Marshal(sample)
			writeMu.Lock()
			_ = writeFrame(conn, frameSample, 0, time.Now().UnixNano(), payload)
			writeMu.Unlock()
			emit.emit(sampleEvent(sample))
		}
	}
}

func writeDeadlineFromConfig(cfg *cliConfig) time.Duration {
	if cfg == nil || cfg.writeDeadlineMs <= 0 {
		return 0
	}
	return time.Duration(cfg.writeDeadlineMs) * time.Millisecond
}

func writeFrameLocked(conn net.Conn, writeMu *sync.Mutex, deadline time.Duration, typ byte, seq uint64, ts int64, payload []byte) error {
	writeMu.Lock()
	defer writeMu.Unlock()
	return writeFrameWithDeadline(conn, deadline, typ, seq, ts, payload)
}

func writeFrameWithDeadline(conn net.Conn, deadline time.Duration, typ byte, seq uint64, ts int64, payload []byte) error {
	if deadline > 0 {
		if err := conn.SetWriteDeadline(time.Now().Add(deadline)); err != nil {
			return err
		}
		defer func() {
			_ = conn.SetWriteDeadline(time.Time{})
		}()
	}
	return writeFrame(conn, typ, seq, ts, payload)
}

func runDialBenchmark(ctx context.Context, conn net.Conn, cfg *cliConfig, emit *emitter, transport string) error {
	defer conn.Close()

	maxBytes := int64(cfg.maxTotalMiB) * 1024 * 1024
	duration := time.Duration(cfg.durationSec) * time.Second
	fileAckEveryBytes := cfg.fileAckKiB * 1024
	fileWindowBytes := cfg.fileWindowKiB * 1024
	hello := helloPayload{
		Profile:           cfg.profile,
		DurationMs:        duration.Milliseconds(),
		MaxBytes:          maxBytes,
		FPS:               cfg.fps,
		WriteSizeBytes:    cfg.writeSize,
		PingBytes:         cfg.pingBytes,
		FileAckEveryBytes: fileAckEveryBytes,
		FileWindowBytes:   fileWindowBytes,
		FilePaceMbps:      cfg.filePaceMbps,
	}
	helloBytes, _ := json.Marshal(hello)
	if err := writeFrameWithDeadline(conn, writeDeadlineFromConfig(cfg), frameHello, 0, time.Now().UnixNano(), helloBytes); err != nil {
		return fmt.Errorf("send benchmark hello failed: %w", err)
	}

	start := time.Now()
	counters := &benchCounters{}
	rtts := &rttStats{}
	var writeMu sync.Mutex
	summaryCh := make(chan *summaryPayload, 1)
	readErrCh := make(chan error, 1)
	readCtx, cancelRead := context.WithCancel(ctx)
	defer cancelRead()
	go readDialFrames(readCtx, conn, emit, counters, rtts, summaryCh, readErrCh)

	var sendErr error
	switch cfg.profile {
	case profileFile:
		sendErr = runFileProfile(ctx, conn, &writeMu, cfg, counters, maxBytes, duration)
	case profileScreen:
		sendErr = runScreenLikeProfile(ctx, conn, &writeMu, cfg, counters, rtts, maxBytes, duration, time.Second, true)
	case profilePing:
		sendErr = runScreenLikeProfile(ctx, conn, &writeMu, cfg, counters, rtts, maxBytes, duration, 100*time.Millisecond, false)
	case profileMixed:
		sendErr = runScreenLikeProfile(ctx, conn, &writeMu, cfg, counters, rtts, maxBytes, duration, 250*time.Millisecond, true)
	default:
		sendErr = fmt.Errorf("unsupported profile %q", cfg.profile)
	}

	_ = writeFrameLocked(conn, &writeMu, writeDeadlineFromConfig(cfg), frameFinish, 0, time.Now().UnixNano(), nil)

	var remoteSummary *summaryPayload
	select {
	case remoteSummary = <-summaryCh:
	case err := <-readErrCh:
		if sendErr == nil && err != nil && !errors.Is(err, context.Canceled) {
			sendErr = err
		}
	case <-time.After(10 * time.Second):
		if sendErr == nil {
			sendErr = fmt.Errorf("timed out waiting for remote summary")
		}
	}
	cancelRead()

	r50, r95, r99, count := rtts.percentiles()
	localSummary := &summaryPayload{
		Event:          "summary",
		Transport:      transport,
		Profile:        cfg.profile,
		Role:           "dialer",
		DurationMs:     time.Since(start).Milliseconds(),
		BytesSent:      counters.bytesSent.Load(),
		BytesReceived:  counters.bytesReceived.Load(),
		ThroughputMbps: mbps(counters.bytesSent.Load(), time.Since(start)),
		RTTP50Ms:       r50.Milliseconds(),
		RTTP95Ms:       r95.Milliseconds(),
		RTTP99Ms:       r99.Milliseconds(),
		PingCount:      count,
		Stalls:         counters.stalls.Load(),
	}
	if remoteSummary != nil {
		localSummary.CapReached = remoteSummary.CapReached
		localSummary.FallbackReason = remoteSummary.FallbackReason
	}

	emit.emit(summaryEvent(localSummary))
	if err := writeSummaryArtifact(cfg, transport, roleDial, localSummary); err != nil {
		emit.emit(event{"event": "artifact_error", "reason": safeReason(err)})
	}
	return sendErr
}

func runFileProfile(ctx context.Context, conn net.Conn, writeMu *sync.Mutex, cfg *cliConfig, counters *benchCounters, maxBytes int64, duration time.Duration) error {
	payload := makeSyntheticPayload(cfg.writeSize)
	deadline := time.Now().Add(duration)
	writeDeadline := writeDeadlineFromConfig(cfg)
	fileWindowBytes := int64(cfg.fileWindowKiB) * 1024
	pacer := newBytePacer(cfg.filePaceMbps)
	for counters.bytesSent.Load() < maxBytes && time.Now().Before(deadline) {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if err := waitForFileWindow(ctx, counters, fileWindowBytes, deadline); err != nil {
			return err
		}
		if !time.Now().Before(deadline) {
			break
		}
		remaining := maxBytes - counters.bytesSent.Load()
		chunk := payload
		if remaining < int64(len(chunk)) {
			chunk = chunk[:remaining]
		}
		if err := pacer.wait(ctx, int64(len(chunk))); err != nil {
			return err
		}
		start := time.Now()
		err := writeFrameLocked(conn, writeMu, writeDeadline, frameData, uint64(counters.bytesSent.Load()), time.Now().UnixNano(), chunk)
		if err != nil {
			return err
		}
		counters.bytesSent.Add(int64(len(chunk)))
		if time.Since(start) > stallThreshold(duration, 750*time.Millisecond) {
			counters.stalls.Add(1)
		}
	}
	return nil
}

func waitForFileWindow(ctx context.Context, counters *benchCounters, maxOutstanding int64, deadline time.Time) error {
	if maxOutstanding <= 0 {
		return nil
	}
	for counters.bytesSent.Load()-counters.bytesAcked.Load() >= maxOutstanding {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if !time.Now().Before(deadline) {
			return nil
		}
		timer := time.NewTimer(10 * time.Millisecond)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}
	}
	return nil
}

type bytePacer struct {
	mbps  float64
	start time.Time
	sent  int64
}

func newBytePacer(mbps float64) *bytePacer {
	if mbps <= 0 {
		return nil
	}
	return &bytePacer{mbps: mbps, start: time.Now()}
}

func (p *bytePacer) wait(ctx context.Context, nextBytes int64) error {
	if p == nil || p.mbps <= 0 || nextBytes <= 0 {
		return nil
	}
	nextSent := p.sent + nextBytes
	targetSeconds := float64(nextSent*8) / (p.mbps * 1_000_000)
	target := p.start.Add(time.Duration(targetSeconds * float64(time.Second)))
	if wait := time.Until(target); wait > 0 {
		timer := time.NewTimer(wait)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}
	}
	p.sent = nextSent
	return nil
}

func stallThreshold(totalDuration time.Duration, fallback time.Duration) time.Duration {
	if totalDuration <= 0 {
		return fallback
	}
	short := totalDuration / 10
	if short > 0 && short < fallback {
		return short
	}
	return fallback
}

func runScreenLikeProfile(ctx context.Context, conn net.Conn, writeMu *sync.Mutex, cfg *cliConfig, counters *benchCounters, rtts *rttStats, maxBytes int64, duration time.Duration, pingEvery time.Duration, includeData bool) error {
	deadline := time.Now().Add(duration)
	writeDeadline := writeDeadlineFromConfig(cfg)
	frameEvery := time.Second / time.Duration(cfg.fps)
	if frameEvery <= 0 {
		frameEvery = 100 * time.Millisecond
	}
	frameTicker := time.NewTicker(frameEvery)
	defer frameTicker.Stop()
	pingTicker := time.NewTicker(pingEvery)
	defer pingTicker.Stop()

	framePayload := makeSyntheticPayload(defaultScreenFrameBytes)
	keyPayload := makeSyntheticPayload(defaultScreenKeyBytes)
	pingPayload := makeSyntheticPayload(cfg.pingBytes)
	var frameID uint64
	var pingID uint64

	for time.Now().Before(deadline) && counters.bytesSent.Load() < maxBytes {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-frameTicker.C:
			if !includeData {
				continue
			}
			frameID++
			payload := framePayload
			if frameID%30 == 1 {
				payload = keyPayload
			}
			remaining := maxBytes - counters.bytesSent.Load()
			if remaining <= 0 {
				return nil
			}
			if remaining < int64(len(payload)) {
				payload = payload[:remaining]
			}
			start := time.Now()
			err := writeFrameLocked(conn, writeMu, writeDeadline, frameData, frameID, time.Now().UnixNano(), payload)
			if err != nil {
				return err
			}
			counters.bytesSent.Add(int64(len(payload)))
			if time.Since(start) > 2*frameEvery {
				counters.stalls.Add(1)
			}
		case <-pingTicker.C:
			pingID++
			started := time.Now()
			writePingTimestamp(pingPayload, started)
			err := writeFrameLocked(conn, writeMu, writeDeadline, framePing, pingID, started.UnixNano(), pingPayload)
			if err != nil {
				return err
			}
			counters.bytesSent.Add(int64(len(pingPayload)))
			_ = rtts
		}
	}
	return nil
}

func readDialFrames(ctx context.Context, conn net.Conn, emit *emitter, counters *benchCounters, rtts *rttStats, summaryCh chan<- *summaryPayload, errCh chan<- error) {
	for {
		select {
		case <-ctx.Done():
			errCh <- ctx.Err()
			return
		default:
		}

		_ = conn.SetReadDeadline(time.Now().Add(time.Second))
		fr, err := readFrame(conn)
		if err != nil {
			if isTimeout(err) {
				continue
			}
			if errors.Is(err, io.EOF) || errors.Is(err, net.ErrClosed) {
				errCh <- nil
				return
			}
			errCh <- err
			return
		}
		switch fr.typ {
		case frameAck:
			storeMaxInt64(&counters.bytesAcked, int64(fr.seq))
		case framePong:
			counters.bytesReceived.Add(int64(len(fr.payload)))
			if len(fr.payload) >= 8 {
				sentAt := int64(binary.BigEndian.Uint64(fr.payload[:8]))
				if sentAt > 0 {
					rtts.add(time.Since(time.Unix(0, sentAt)))
				}
			}
		case frameSample:
			var sample samplePayload
			if err := json.Unmarshal(fr.payload, &sample); err == nil {
				emit.emit(sampleEvent(sample))
			}
		case frameSummary:
			var summary summaryPayload
			if err := json.Unmarshal(fr.payload, &summary); err != nil {
				errCh <- err
				return
			}
			summaryCh <- &summary
			return
		default:
			errCh <- fmt.Errorf("unexpected response frame type %d", fr.typ)
			return
		}
	}
}

func writeFrame(w io.Writer, typ byte, seq uint64, ts int64, payload []byte) error {
	if len(payload) > maxFramePayloadBytes {
		return fmt.Errorf("frame payload too large: %d", len(payload))
	}
	buf := make([]byte, headerSize+len(payload))
	binary.BigEndian.PutUint32(buf[0:4], wireMagic)
	buf[4] = wireVersion
	buf[5] = typ
	binary.BigEndian.PutUint16(buf[6:8], 0)
	binary.BigEndian.PutUint64(buf[8:16], seq)
	binary.BigEndian.PutUint64(buf[16:24], uint64(ts))
	binary.BigEndian.PutUint32(buf[24:28], uint32(len(payload)))
	copy(buf[headerSize:], payload)
	return writeAll(w, buf)
}

func writeAll(w io.Writer, buf []byte) error {
	for len(buf) > 0 {
		n, err := w.Write(buf)
		if err != nil {
			return err
		}
		if n <= 0 {
			return io.ErrShortWrite
		}
		buf = buf[n:]
	}
	return nil
}

func readFrame(r io.Reader) (frame, error) {
	header := make([]byte, headerSize)
	if _, err := io.ReadFull(r, header); err != nil {
		return frame{}, err
	}
	if binary.BigEndian.Uint32(header[0:4]) != wireMagic {
		return frame{}, fmt.Errorf("invalid wire magic")
	}
	if header[4] != wireVersion {
		return frame{}, fmt.Errorf("unsupported wire version %d", header[4])
	}
	payloadLen := binary.BigEndian.Uint32(header[24:28])
	if payloadLen > maxFramePayloadBytes {
		return frame{}, fmt.Errorf("frame payload too large: %d", payloadLen)
	}
	payload := make([]byte, int(payloadLen))
	if payloadLen > 0 {
		if _, err := io.ReadFull(r, payload); err != nil {
			return frame{}, err
		}
	}
	return frame{
		typ:       header[5],
		seq:       binary.BigEndian.Uint64(header[8:16]),
		timestamp: int64(binary.BigEndian.Uint64(header[16:24])),
		payload:   payload,
	}, nil
}

func resolveAddressAccount(cfg *cliConfig) (*nkn.Account, string, string, error) {
	if cfg.walletPath != "" {
		wallet, err := openLinkedWallet(cfg, &emitter{jsonl: cfg.jsonl})
		if err != nil {
			return nil, "", "", err
		}
		return wallet.wallet.Account(), wallet.address, wallet.balance, nil
	}
	account, err := accountFromOptionalSeed(cfg.seedHex)
	return account, "", "", err
}

func openLinkedWallet(cfg *cliConfig, emit *emitter) (*walletInfo, error) {
	if err := validateWalletPasswordInput(cfg, "wallet loading"); err != nil {
		return nil, err
	}
	walletPath := strings.TrimSpace(cfg.walletPath)
	if walletPath == "" {
		return nil, usageError("wallet path is required")
	}

	passwordBytes, err := readWalletPassword(cfg)
	if err != nil {
		return nil, err
	}
	defer zeroBytes(passwordBytes)
	password := string(passwordBytes)

	raw, err := os.ReadFile(walletPath)
	if err != nil {
		return nil, fmt.Errorf("read wallet file %q failed: %s", filepath.Base(walletPath), scrubError(err, walletPath))
	}
	wallet, err := nkn.WalletFromJSON(string(raw), &nkn.WalletConfig{Password: password})
	if err != nil {
		return nil, fmt.Errorf("unlock wallet %q failed: %s", filepath.Base(walletPath), scrubError(err, walletPath))
	}
	if err := wallet.VerifyPassword(password); err != nil {
		return nil, fmt.Errorf("wallet password verification failed")
	}
	balance, err := wallet.Balance()
	if err != nil {
		return nil, fmt.Errorf("wallet balance lookup failed: %w", err)
	}
	info := &walletInfo{
		wallet:  wallet,
		address: wallet.Address(),
		balance: balance.String(),
	}
	emit.emit(event{
		"event":         "wallet_ready",
		"walletFile":    filepath.Base(walletPath),
		"walletAddress": info.address,
		"balanceNkn":    info.balance,
	})
	return info, nil
}

func readWalletPassword(cfg *cliConfig) ([]byte, error) {
	if cfg.passwordStdin {
		return readPasswordFromStdin()
	}
	return promptPassword()
}

func promptPassword() ([]byte, error) {
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

func readPasswordFromStdin() ([]byte, error) {
	raw, err := io.ReadAll(io.LimitReader(os.Stdin, 4097))
	if err != nil {
		return nil, fmt.Errorf("read wallet password from stdin failed: %w", err)
	}
	if len(raw) > 4096 {
		zeroBytes(raw)
		return nil, fmt.Errorf("wallet password from stdin is too long")
	}
	password := bytes.TrimRight(raw, "\r\n")
	if len(password) == 0 {
		zeroBytes(raw)
		return nil, fmt.Errorf("wallet password cannot be empty")
	}
	out := append([]byte(nil), password...)
	zeroBytes(raw)
	return out, nil
}

func promptNewWalletPassword() ([]byte, error) {
	first, err := readHiddenPassword("New wallet password: ")
	if err != nil {
		return nil, err
	}
	confirmed := false
	defer func() {
		if !confirmed {
			zeroBytes(first)
		}
	}()

	second, err := readHiddenPassword("Confirm wallet password: ")
	if err != nil {
		return nil, err
	}
	defer zeroBytes(second)

	if string(first) != string(second) {
		return nil, fmt.Errorf("wallet passwords did not match")
	}
	confirmed = true
	return first, nil
}

func readHiddenPassword(prompt string) ([]byte, error) {
	fmt.Fprint(os.Stderr, prompt)
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

func accountFromOptionalSeed(seedHex string) (*nkn.Account, error) {
	seed, err := decodeSeed(seedHex)
	if err != nil {
		return nil, err
	}
	return nkn.NewAccount(seed)
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

func newReadyMultiClient(ctx context.Context, account *nkn.Account, cfg *cliConfig) (*nkn.MultiClient, error) {
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

func waitTunaExit(ctx context.Context, tunaClient *ts.TunaSessionClient, cfg *cliConfig, emit *emitter) {
	ch := tunaClient.OnConnect()
	timer := time.NewTimer(time.Duration(cfg.connectTimeoutSec) * time.Second)
	defer timer.Stop()
	select {
	case <-ch:
		emit.emit(event{"event": "tuna_exit_ready", "transport": transportTuna})
	case <-timer.C:
		emit.emit(event{"event": "warning", "transport": transportTuna, "reason": "tuna_exit_connect_timeout"})
	case <-ctx.Done():
	}
}

func buildTunaConfig(cfg *cliConfig) *ts.Config {
	tunaConfig := ts.DefaultConfig()
	if cfg.maxPriceNKNPerMB != "" {
		tunaConfig.TunaMaxPrice = cfg.maxPriceNKNPerMB
	}
	if cfg.minBalanceNKN != "" {
		tunaConfig.TunaMinBalance = cfg.minBalanceNKN
	}
	if cfg.tunaNumListeners > 0 {
		tunaConfig.NumTunaListeners = cfg.tunaNumListeners
	}
	if cfg.tunaDialTimeoutMs > 0 {
		tunaConfig.TunaDialTimeout = cfg.tunaDialTimeoutMs
	}
	if cfg.tunaServiceName != "" {
		tunaConfig.TunaServiceName = cfg.tunaServiceName
	}
	tunaConfig.TunaMeasureBandwidth = cfg.tunaMeasureBandwidth
	return tunaConfig
}

func allowList(cfg *cliConfig) (*nkngomobile.StringArray, func(string) bool, error) {
	pattern, err := compileAllowPattern(cfg)
	if err != nil {
		return nil, nil, err
	}
	if cfg.unsafeAllowAny {
		return nil, func(string) bool { return true }, nil
	}
	return nkn.NewStringArray(pattern.String()), pattern.MatchString, nil
}

func compileAllowPattern(cfg *cliConfig) (*regexp.Regexp, error) {
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

func writeSummaryArtifact(cfg *cliConfig, transport, role string, summary *summaryPayload) error {
	if summary == nil {
		return nil
	}
	dir := filepath.Join(cfg.outputRoot, time.Now().UTC().Format("20060102T150405Z"))
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	path := filepath.Join(dir, fmt.Sprintf("summary-%s-%s-%s.json", transport, role, summary.Profile))
	data, err := json.MarshalIndent(summary, "", "  ")
	if err != nil {
		return err
	}
	data = append(data, '\n')
	return os.WriteFile(path, data, 0o644)
}

func (e *emitter) emit(v any) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.jsonl {
		_ = json.NewEncoder(os.Stdout).Encode(v)
		return
	}
	switch x := v.(type) {
	case *summaryPayload:
		e.emitHumanSummary(x)
	case summaryPayload:
		e.emitHumanSummary(&x)
	case event:
		if name, _ := x["event"].(string); name != "" {
			fmt.Fprintf(os.Stderr, "%s: %v\n", name, x)
		}
	default:
		fmt.Fprintf(os.Stderr, "%v\n", v)
	}
}

func (e *emitter) emitHumanSummary(x *summaryPayload) {
	fmt.Fprintf(os.Stderr, "%s %s %s: sent=%d received=%d throughput=%.2fMbps rtt_p95=%dms cap=%v reason=%s\n",
		x.Transport, x.Role, x.Profile, x.BytesSent, x.BytesReceived, x.ThroughputMbps, x.RTTP95Ms, x.CapReached, x.FallbackReason)
}

func sampleEvent(sample samplePayload) event {
	return event{
		"event":          "sample",
		"transport":      sample.Transport,
		"profile":        sample.Profile,
		"role":           sample.Role,
		"bytesSent":      sample.BytesSent,
		"bytesReceived":  sample.BytesReceived,
		"throughputMbps": round(sample.ThroughputMbps, 3),
		"rttP50Ms":       sample.RTTP50Ms,
		"rttP95Ms":       sample.RTTP95Ms,
		"rttP99Ms":       sample.RTTP99Ms,
		"stalls":         sample.Stalls,
	}
}

func summaryEvent(summary *summaryPayload) event {
	return event{
		"event":          "summary",
		"transport":      summary.Transport,
		"profile":        summary.Profile,
		"role":           summary.Role,
		"durationMs":     summary.DurationMs,
		"bytesSent":      summary.BytesSent,
		"bytesReceived":  summary.BytesReceived,
		"throughputMbps": round(summary.ThroughputMbps, 3),
		"rttP50Ms":       summary.RTTP50Ms,
		"rttP95Ms":       summary.RTTP95Ms,
		"rttP99Ms":       summary.RTTP99Ms,
		"pingCount":      summary.PingCount,
		"stalls":         summary.Stalls,
		"capReached":     summary.CapReached,
		"fallbackReason": summary.FallbackReason,
	}
}

func (r *rttStats) add(v time.Duration) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if v >= 0 && v < time.Hour {
		r.vals = append(r.vals, v)
	}
}

func (r *rttStats) percentiles() (time.Duration, time.Duration, time.Duration, int) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if len(r.vals) == 0 {
		return 0, 0, 0, 0
	}
	cp := append([]time.Duration(nil), r.vals...)
	sort.Slice(cp, func(i, j int) bool { return cp[i] < cp[j] })
	return percentile(cp, 0.50), percentile(cp, 0.95), percentile(cp, 0.99), len(cp)
}

func percentile(vals []time.Duration, p float64) time.Duration {
	if len(vals) == 0 {
		return 0
	}
	idx := int(math.Ceil(float64(len(vals))*p)) - 1
	if idx < 0 {
		idx = 0
	}
	if idx >= len(vals) {
		idx = len(vals) - 1
	}
	return vals[idx]
}

func makeSyntheticPayload(size int) []byte {
	if size < 0 {
		size = 0
	}
	payload := make([]byte, size)
	if _, err := rand.Read(payload); err != nil {
		for i := range payload {
			payload[i] = byte(i % 251)
		}
	}
	return payload
}

func writePingTimestamp(payload []byte, t time.Time) {
	if len(payload) >= 8 {
		binary.BigEndian.PutUint64(payload[:8], uint64(t.UnixNano()))
	}
}

func mbps(bytes int64, duration time.Duration) float64 {
	if bytes <= 0 || duration <= 0 {
		return 0
	}
	return float64(bytes*8) / duration.Seconds() / 1_000_000
}

func round(v float64, places int) float64 {
	factor := math.Pow10(places)
	return math.Round(v*factor) / factor
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

func isValidProfile(profile string) bool {
	switch profile {
	case profileFile, profileScreen, profilePing, profileMixed:
		return true
	default:
		return false
	}
}

func hasArg(args []string, wanted string) bool {
	for _, arg := range args {
		if arg == wanted {
			return true
		}
	}
	return false
}

func closeOnCancel(ctx context.Context, closers ...func() error) {
	<-ctx.Done()
	for _, closeFn := range closers {
		if closeFn != nil {
			_ = closeFn()
		}
	}
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
	msg := fmt.Sprintf(format, args...)
	return fmt.Errorf("%s\n\n%s", msg, usage())
}

func usage() string {
	return strings.TrimSpace(`Usage:
  nlink-tuna-poc.exe address [--wallet wallet.json --password-prompt] [--seed-hex <hex>] [--jsonl]
  nlink-tuna-poc.exe create-wallet --out artifacts\tuna-poc\wallet-test-nkn.json --password-prompt [--jsonl]
  nlink-tuna-poc.exe listen --wallet wallet.json --password-prompt --allow-remote <addr> --max-price-nkn-per-mb <nkn> --max-total-mib 64 --max-duration-sec 120 [--jsonl]
  nlink-tuna-poc.exe dial --to <listener-address> --profile file --duration-sec 60 [--jsonl]
  nlink-tuna-poc.exe baseline --role listen --allow-remote <addr> --max-total-mib 64 --max-duration-sec 120 [--jsonl]
  nlink-tuna-poc.exe baseline --role dial --to <listener-address> --profile file --duration-sec 60 [--jsonl]

Notes:
  create-wallet generates a new encrypted wallet and prints only its public address.
  --allow-remote is exact-match by default. Add --allow-remote-regex for a pubkey/address regexp.
  --unsafe-allow-any is available for local smoke tests only.
  --seed-hex gives a stable POC identity without writing a key file; do not share it.`)
}
