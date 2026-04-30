using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using NLink.App;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.Services.RemoteControl;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.Diagnostics;
using NLink.Core.FileTransfer;
using NLink.Core.Metrics;
using NLink.Core.RemoteControl;
using NLink.Core.Resources;
using NLink.Core.Retry;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Core.Logging;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

public abstract class CoreSmokeTestsBase
{
internal static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "nLink.App", "Views", "HelperPageView.axaml.cs");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

internal static async Task VerifyHandshakeAsync(bool approve)
    {
        var hostAddress = CreateTestPeerAddress();
        using var host = new DevLocalTransport(hostAddress);
        using var joiner = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var joinRequestRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IncomingJoinRequestEventArgs? pendingJoinRequest = null;

        host.IncomingJoinRequest += (_, e) =>
        {
            pendingJoinRequest = e;
            joinRequestRaised.TrySetResult();
        };

        joiner.Approved += (_, _) => approvedRaised.TrySetResult();
        joiner.Rejected += (_, _) => rejectedRaised.TrySetResult();
        joiner.Disconnected += (_, _) => disconnectedRaised.TrySetResult();

        _ = host.HostByAddressAsync(cts.Token);
        await Task.Delay(75, cts.Token);

        var invite = CreateValidatedInviteForTarget(
            new PeerAddress(hostAddress),
            out var rawToken,
            InviteCapabilities.Chat);
        await WaitStepAsync("joiner join", joiner.JoinByInviteAsync(rawToken, invite, cts.Token), TimeSpan.FromSeconds(3));
        await WaitStepAsync("join request raised", joinRequestRaised.Task, TimeSpan.FromSeconds(3));
        Assert.NotNull(pendingJoinRequest);

        if (approve)
        {
            await WaitStepAsync(
                "approve request",
                pendingJoinRequest!.ApproveAsync(pendingJoinRequest.CreateApprovalDecision(), CancellationToken.None),
                TimeSpan.FromSeconds(3));
        }
        else
        {
            await WaitStepAsync("reject request", pendingJoinRequest!.RejectAsync(CancellationToken.None), TimeSpan.FromSeconds(3));
        }

        if (approve)
        {
            await WaitStepAsync("approved event", approvedRaised.Task, TimeSpan.FromSeconds(3));
            Assert.False(rejectedRaised.Task.IsCompleted);
        }
        else
        {
            await WaitStepAsync("rejected event", rejectedRaised.Task, TimeSpan.FromSeconds(3));
            Assert.False(approvedRaised.Task.IsCompleted);
        }

        // Reject path may close immediately. Approve path should keep the session alive.
        if (approve)
        {
            Assert.False(disconnectedRaised.Task.IsCompleted);
        }

        joiner.Dispose();
        host.Dispose();
        cts.Cancel();
        await Task.Delay(50, CancellationToken.None);
    }

internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

internal static async Task ApprovePendingJoinIfNeededAsync(
        SessionRuntime helpeeRuntime,
        CancellationToken ct,
        TimeSpan timeout)
    {
        await WaitUntilAsync(
            () => helpeeRuntime.PendingApprovalRequest is not null ||
                  helpeeRuntime.State == SessionRuntimeState.Connected,
            timeout);

        if (helpeeRuntime.PendingApprovalRequest is not null)
        {
            await helpeeRuntime.ApproveAsync(ct);
        }
    }

internal static async Task WaitStepAsync(string stepName, Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out while waiting for step: {stepName}", ex);
        }
    }

internal static PeerAddress GetHostedAddressOrThrow(SessionRuntime runtime)
    {
        if (runtime.CurrentLocalPeerAddress is PeerAddress address)
        {
            return address;
        }

        throw new InvalidOperationException("Active helpee transport did not expose a local peer address.");
    }

internal static async Task<string> WaitForShareInviteAsync(HelpeePageViewModel helpee, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        await WaitUntilAsync(() => !string.IsNullOrWhiteSpace(helpee.ShareInvite), effectiveTimeout);
        return helpee.ShareInvite;
    }

internal static SessionSecurityState CreateApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities = CapabilityGrant.Chat | CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionId = new SessionId($"scripted_session_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, nowUtc.Add(SessionSecurityDefaults.GrantLifetime)));
    }

internal static SessionSecurityState CreateVerifiedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        SessionId? sessionId = null,
        bool inviteValidated = true)
    {
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId ?? new SessionId($"scripted_verified_{Guid.NewGuid():N}"),
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = inviteValidated,
        }).WithHandshakeVerified(helperAddress);
    }

internal static SessionSecurityState CreateExpiredApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessionId = new SessionId($"scripted_expired_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, nowUtc.AddSeconds(-5)));
    }

internal static ValidatedInviteV1 CreateValidatedInviteForTarget(
        PeerAddress targetAddress,
        out string rawToken,
        InviteCapabilities capabilities = InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.RemoteControl,
        SessionId? sessionId = null,
        PeerAddress? boundHelperAddress = null)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var factory = InviteTokenServiceFactory.CreateInviteTokenFactory();
        var create = factory.Create(
            new InviteTokenCreateRequest(
                IssuerAddress: targetAddress,
                TargetAddress: targetAddress,
                SessionId: sessionId ?? new SessionId($"sess_smoke_{Guid.NewGuid():N}"),
                Capabilities: capabilities,
                Lifetime: TimeSpan.FromMinutes(5),
                BoundHelperAddress: boundHelperAddress),
            nowUtc);
        Assert.True(create.IsSuccess, create.Message);
        Assert.NotNull(create.Token);

        rawToken = create.Token!;
        var validator = InviteTokenServiceFactory.CreateInviteTokenValidator();
        var validation = validator.Validate(rawToken, nowUtc.AddSeconds(1));
        Assert.True(validation.IsSuccess, validation.Message);
        Assert.NotNull(validation.Invite);
        return validation.Invite!;
    }

internal static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

internal static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        while (normalized.Length % 4 != 0)
        {
            normalized += "=";
        }

        return Convert.FromBase64String(normalized);
    }

internal static string CreateTestPeerAddress()
    {
        return $"devlocal.test.{Guid.NewGuid().ToString("N")[..12]}";
    }

internal static string? FindFileUpwards(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

internal static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

internal static string GetCurrentBridgeRidForTests()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new NotSupportedException("Unsupported macOS architecture for bridge RID test.")
            };
        }

        throw new NotSupportedException("Unsupported platform for bridge RID test.");
    }

internal static void PrepareFakeBridgeBundle(string bridgeRoot)
    {
        CleanupDirectoryIfExists(bridgeRoot);
        Directory.CreateDirectory(bridgeRoot);

        var nodeFileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        File.WriteAllText(Path.Combine(bridgeRoot, "index.js"), "// fake");
        File.WriteAllText(Path.Combine(bridgeRoot, nodeFileName), "fake");
        File.WriteAllText(Path.Combine(bridgeRoot, "package.json"), "{\"name\":\"fake-bridge\",\"version\":\"1.0.0\"}");
    }

internal static string BuildMockBridgeScript(int delayPongMs, bool respondToPing, bool respondToShutdown = true)
    {
        var delay = Math.Max(0, delayPongMs);
        var respond = respondToPing ? "true" : "false";
        var shutdownRespond = respondToShutdown ? "true" : "false";
        return
$@"'use strict';
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    if ({respond}) {{
      setTimeout(() => emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }}), {delay});
    }}
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    if ({shutdownRespond}) {{
      emit({{ event:'ok', id: msg.id ?? null, cmd: 'shutdown' }});
      emit({{ event:'disconnected', reason:'shutdown' }});
      setTimeout(() => process.exit(0), 10);
    }}
    return;
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

internal static string BuildMockBridgeScriptWithCustomConnect(string connectBehaviorJs, int delayPongMs = 0, bool respondToPing = true, bool respondToShutdown = true)
    {
        var delay = Math.Max(0, delayPongMs);
        var respond = respondToPing ? "true" : "false";
        var shutdownRespond = respondToShutdown ? "true" : "false";
        return
$@"'use strict';
const fs = require('fs');
const readline = require('readline');
const rl = readline.createInterface({{ input: process.stdin, crlfDelay: Infinity, terminal: false }});
let connectCount = 0;
const connectIds = [];
function emit(obj) {{ process.stdout.write(JSON.stringify(obj) + '\n'); }}
rl.on('line', (line) => {{
  if (!line || !line.trim()) return;
  let msg;
  try {{ msg = JSON.parse(line); }} catch (e) {{ emit({{ event:'error', id:null, cmd:null, reason:'Invalid JSON' }}); return; }}
  if (msg.cmd === 'hello') {{
    emit({{ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }});
    return;
  }}
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) {{
    if ({respond}) {{
      setTimeout(() => emit({{ type:'pong', id: msg.id ?? null, ts: Date.now() }}), {delay});
    }}
    return;
  }}
  if (msg.cmd === 'shutdown') {{
    if ({shutdownRespond}) {{
      emit({{ event:'ok', id: msg.id ?? null, cmd: 'shutdown' }});
      emit({{ event:'disconnected', reason:'shutdown' }});
      setTimeout(() => process.exit(0), 10);
    }}
    return;
  }}
  if (msg.cmd === 'connect') {{
    {connectBehaviorJs}
  }}
  emit({{ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null }});
}});
";
    }

internal static string BuildMockBridgeScriptWithStderrSpam()
    {
        return
@"'use strict';
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity, terminal: false });
function emit(obj) { process.stdout.write(JSON.stringify(obj) + '\n'); }
let spamTimer = null;
function startSpam() {
  if (spamTimer) return;
  let n = 0;
  spamTimer = setInterval(() => {
    for (let i = 0; i < 50; i++) {
      process.stderr.write('spam-line-' + (n++) + ' xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\\n');
    }
  }, 5);
}
function stopSpam() {
  if (spamTimer) {
    clearInterval(spamTimer);
    spamTimer = null;
  }
}
rl.on('line', (line) => {
  if (!line || !line.trim()) return;
  let msg;
  try { msg = JSON.parse(line); } catch { emit({ event:'error', id:null, cmd:null, reason:'Invalid JSON' }); return; }
  if (msg.cmd === 'hello') { emit({ event:'hello_ok', id: msg.id ?? null, protocol: 2, sdk: 'mock-sdk@1.0.0' }); startSpam(); return; }
  if ((msg.type === 'ping') || (msg.cmd === 'ping')) { emit({ type:'pong', id: msg.id ?? null, ts: Date.now() }); return; }
  if (msg.cmd === 'shutdown') {
    emit({ event:'ok', id: msg.id ?? null, cmd: 'shutdown' });
    emit({ event:'disconnected', reason:'shutdown' });
    stopSpam();
    setTimeout(() => process.exit(0), 10);
    return;
  }
  emit({ event:'ok', id: msg.id ?? null, cmd: msg.cmd ?? msg.type ?? null });
});";
    }

internal static void CleanupDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, recursive: true);
    }

internal static void SetPrivateField<TTarget>(TTarget target, string fieldName, object? value)
    {
        var aliasHandled = TryHandleLegacyPrivateFieldAlias(target!, fieldName, value, setOperation: true, out _);
        if (aliasHandled)
        {
            return;
        }

        var field = FindPrivateField(typeof(TTarget), fieldName);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

internal static object? GetPrivateField<TTarget>(TTarget target, string fieldName)
    {
        var aliasHandled = TryHandleLegacyPrivateFieldAlias(target!, fieldName, value: null, setOperation: false, out var aliasValue);
        if (aliasHandled)
        {
            return aliasValue;
        }

        var field = FindPrivateField(typeof(TTarget), fieldName);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

internal static void SetPrivateFieldDynamic(object target, string fieldName, object? value)
    {
        var aliasHandled = TryHandleLegacyPrivateFieldAlias(target, fieldName, value, setOperation: true, out _);
        if (aliasHandled)
        {
            return;
        }

        var field = FindPrivateField(target.GetType(), fieldName);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static FieldInfo? FindPrivateField(Type type, string fieldName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static bool TryHandleLegacyPrivateFieldAlias(
        object target,
        string fieldName,
        object? value,
        bool setOperation,
        out object? aliasValue)
    {
        aliasValue = null;

        var targetType = target.GetType();
        if (targetType != typeof(HelperPageViewModel) &&
            targetType != typeof(HelpeePageViewModel))
        {
            return false;
        }

        switch (fieldName)
        {
            case "fallbackUiPhase":
                var effectivePhaseField = FindPrivateField(targetType, "effectivePhase");
                Assert.NotNull(effectivePhaseField);
                if (setOperation)
                {
                    effectivePhaseField!.SetValue(target, value);
                    return true;
                }

                aliasValue = effectivePhaseField!.GetValue(target);
                return true;

            case "endSessionRequested":
                var localEndField = FindPrivateField(targetType, "localEndCommandInFlight");
                Assert.NotNull(localEndField);
                if (setOperation)
                {
                    localEndField!.SetValue(target, value);
                    return true;
                }

                aliasValue = localEndField!.GetValue(target);
                return true;

            case "wasConnected":
                if (!setOperation)
                {
                    aliasValue = false;
                }

                return true;

            case "endReason":
                if (!setOperation)
                {
                    aliasValue = null;
                }

                return true;
        }

        return false;
    }

internal static object? InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => CanBindInvocation(m, args))
            ?? methods.FirstOrDefault(m => m.GetParameters().Length == args.Length)
            ?? methods[0];
        Assert.NotNull(method);
        var invocationArgs = BuildInvocationArguments(method!, args);
        return method!.Invoke(target, invocationArgs);
    }

    private static bool CanBindInvocation(MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        if (args.Length > parameters.Length)
        {
            return false;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;
            var argument = args[i];
            if (argument is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                {
                    return false;
                }

                continue;
            }

            if (!parameterType.IsInstanceOfType(argument))
            {
                return false;
            }
        }

        for (var i = args.Length; i < parameters.Length; i++)
        {
            if (!parameters[i].IsOptional)
            {
                return false;
            }
        }

        return true;
    }

    private static object?[] BuildInvocationArguments(MethodInfo method, object?[] args)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == args.Length)
        {
            return args;
        }

        var invocationArgs = new object?[parameters.Length];
        Array.Copy(args, invocationArgs, args.Length);
        for (var i = args.Length; i < parameters.Length; i++)
        {
            invocationArgs[i] = Type.Missing;
        }

        return invocationArgs;
    }

internal static Envelope BuildSecureControlEnvelope<TMessage>(
        NknSignalingTransport senderTransport,
        MsgType msgType,
        TMessage message,
        string secureMessageType,
        string? requestId,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "controlSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var senderIdentity = new PeerAddress(senderTransport.LocalPeerAddress);
        var plaintext = message switch
        {
            ControlRequestMessageV1 controlRequest => RemoteControlPayloadCodec.Serialize(controlRequest),
            ControlResponseMessageV1 controlResponse => RemoteControlPayloadCodec.Serialize(controlResponse),
            ControlStartMessageV1 controlStart => RemoteControlPayloadCodec.Serialize(controlStart),
            ControlStopMessageV1 controlStop => RemoteControlPayloadCodec.Serialize(controlStop),
            ControlInputMessageV1 controlInput => RemoteControlPayloadCodec.Serialize(controlInput),
            ControlInputAckV1 controlAck => RemoteControlPayloadCodec.Serialize(controlAck),
            ControlStateSnapshotV1 controlSnapshot => RemoteControlPayloadCodec.Serialize(controlSnapshot),
            ControlDisplayInfoMessageV1 controlDisplayInfo => RemoteControlPayloadCodec.Serialize(controlDisplayInfo),
            _ => throw new ArgumentOutOfRangeException(nameof(message), "Unsupported control message."),
        };

        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: secureMessageType,
                SessionId: sessionId,
                SenderIdentity: senderIdentity,
                Sequence: sequence,
                RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim()),
            plaintext);
        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: msgType,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

internal static Envelope BuildSecureFileTransferEnvelope<TMessage>(
        NknSignalingTransport senderTransport,
        MsgType msgType,
        TMessage message,
        string? requestId,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var senderClient = Assert.IsAssignableFrom<INknClient>(GetPrivateField(senderTransport, "client"));
        var senderIdentity = msgType == MsgType.FileTransferChunk
            ? new PeerAddress(senderClient.BulkAddress)
            : new PeerAddress(senderTransport.LocalPeerAddress);
        var plaintext = message switch
        {
            FileTransferOfferV2 offer => FileTransferPayloadCodec.Serialize(offer),
            FileTransferAcceptV1 accept => FileTransferPayloadCodec.Serialize(accept),
            FileTransferDeclineV1 decline => FileTransferPayloadCodec.Serialize(decline),
            FileTransferCancelV1 cancel => FileTransferPayloadCodec.Serialize(cancel),
            FileTransferErrorV1 error => FileTransferPayloadCodec.Serialize(error),
            FileTransferCompleteV1 complete => FileTransferPayloadCodec.Serialize(complete),
            _ => throw new ArgumentOutOfRangeException(nameof(message), "Unsupported file-transfer message."),
        };

        try
        {
            var securePayload = SessionSecureEnvelopeCodec.Encrypt(
                key,
                new SessionSecureEnvelopeMetadata(
                    Family: SessionSecureMessageFamily.FileTransfer,
                    MessageType: msgType switch
                    {
                        MsgType.FileTransferOffer => "file_transfer_offer",
                        MsgType.FileTransferAccept => "file_transfer_accept",
                        MsgType.FileTransferDecline => "file_transfer_decline",
                        MsgType.FileTransferStart => "file_transfer_start",
                        MsgType.FileTransferChunk => "file_transfer_chunk",
                        MsgType.FileTransferWindowUpdate => "file_transfer_window_update",
                        MsgType.FileTransferMissingRange => "file_transfer_missing_range",
                        MsgType.FileTransferPressureState => "file_transfer_pressure_state",
                        MsgType.FileTransferCancel => "file_transfer_cancel",
                        MsgType.FileTransferError => "file_transfer_error",
                        MsgType.FileTransferComplete => "file_transfer_complete",
                        _ => throw new ArgumentOutOfRangeException(nameof(msgType), msgType, "Unsupported file-transfer message."),
                    },
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity,
                    Sequence: sequence,
                    RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim()),
                plaintext);

            return new Envelope(
                Version: 1,
                Code: envelopeCode,
                MessageId: Guid.NewGuid().ToString("N"),
                Type: msgType,
                Payload: securePayload,
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyTo: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

internal static void InvokeNknIncomingMessage(
        NknSignalingTransport transport,
        INknClient senderClient,
        NknIncomingMessage message)
    {
        var method = typeof(NknSignalingTransport).GetMethod(
            "OnClientMessageReceived",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method!.Invoke(transport, new object?[] { senderClient, message });
    }

internal static ScreenShareVideoStreamConfigV1 CreateScreenShareVideoStreamConfig(string sessionId, long streamEpoch = 1)
    {
        return new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
            DisplayInfoRevision = 0,
        };
    }

internal static byte[] CreateScreenShareVideoPayload(
        string sessionId,
        long frameId,
        byte[] data,
        long streamEpoch = 1,
        int width = 1,
        int height = 1,
        int fragmentIndex = 0,
        int fragmentCount = 1,
        long? capturedTsUtcMs = null,
        bool isKeyFrame = true)
    {
        return ScreenShareVideoPayloadCodec.SerializeFragment(
            new ScreenShareVideoFragmentV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                FrameId = frameId,
                CapturedTsUtcMs = capturedTsUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Width = width,
                Height = height,
                Encoding = "h264",
                IsKeyFrame = isKeyFrame,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
                Data = data,
            });
    }

internal static Envelope BuildSecureLifecycleEnvelope(
        NknSignalingTransport senderTransport,
        MsgType msgType,
        byte[] plaintext,
        string? requestId,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "controlSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var senderIdentity = new PeerAddress(senderTransport.LocalPeerAddress);

        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.Lifecycle,
                MessageType: msgType switch
                {
                    MsgType.Approve => "approve",
                    MsgType.Reject => "reject",
                    MsgType.SessionEnd => "session_end",
                    _ => throw new ArgumentOutOfRangeException(nameof(msgType), msgType, "Unsupported lifecycle message."),
                },
                SessionId: sessionId,
                SenderIdentity: senderIdentity,
                Sequence: sequence,
                RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim()),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: msgType,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

internal static Envelope BuildSecureFileTransferDataFrameEnvelope(
        NknSignalingTransport senderTransport,
        FileTransferDataFrame frame,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var plaintext = FileTransferDataFrameCodec.Serialize(frame);
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.FileTransfer,
                MessageType: "file_transfer_data_frame",
                SessionId: sessionId,
                SenderIdentity: new PeerAddress(senderTransport.LocalPeerAddress),
                Sequence: sequence,
                RequestId: string.IsNullOrWhiteSpace(frame.TransferId) ? null : frame.TransferId),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.FileTransferDataFrame,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

internal static long GetNextFileTransferSecureSequence(NknSignalingTransport senderTransport)
    {
        var current = Assert.IsType<long>(GetPrivateField(senderTransport, "nextOutboundFileTransferSecureSequence"));
        return current + 1;
    }

internal static Envelope BuildSecureScreenShareEnvelope(
        NknSignalingTransport senderTransport,
        MsgType msgType,
        byte[] plaintext,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "controlSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var senderClient = Assert.IsAssignableFrom<INknClient>(GetPrivateField(senderTransport, "client"));
        var senderIdentity = msgType == MsgType.ScreenShareStop
            ? new PeerAddress(senderTransport.LocalPeerAddress)
            : new PeerAddress(senderClient.MediaAddress);

        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.ScreenShare,
                MessageType: msgType switch
                {
                    MsgType.ScreenShareFrame => "screenshare_frame",
                    MsgType.ScreenShareStop => "screenshare_stop",
                    _ => throw new ArgumentOutOfRangeException(nameof(msgType), msgType, "Unsupported screen-share message."),
                },
                SessionId: sessionId,
                SenderIdentity: senderIdentity,
                Sequence: sequence,
                RequestId: null),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: msgType,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

internal static byte[] BuildSecureDevLocalPayload(
        DevLocalTransport senderTransport,
        SessionSecureMessageFamily family,
        string messageType,
        byte[] plaintext,
        string? requestId,
        long sequence,
        PeerAddress? senderIdentityOverride = null)
    {
        var sessionKey = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "controlSessionSharedKey")).AsSpan().ToArray();
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var senderIdentity = senderIdentityOverride ?? new PeerAddress(senderTransport.LocalPeerAddress);
        var envelopeKey = family == SessionSecureMessageFamily.FileTransfer
            ? SessionKeyDerivation.DeriveFileTransferKey(sessionKey)
            : sessionKey;

        try
        {
            return SessionSecureEnvelopeCodec.Encrypt(
                envelopeKey,
                new SessionSecureEnvelopeMetadata(
                    Family: family,
                    MessageType: messageType,
                    SessionId: sessionId,
                    SenderIdentity: senderIdentity,
                    Sequence: sequence,
                    RequestId: string.IsNullOrWhiteSpace(requestId) ? null : requestId.Trim()),
                plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelopeKey);
            if (!ReferenceEquals(envelopeKey, sessionKey))
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
        }
    }

internal static async Task SendRawDevLocalFrameAsync(
        DevLocalTransport senderTransport,
        string frameType,
        byte[] payload,
        CancellationToken ct)
    {
        var connection = GetPrivateField(senderTransport, "activeConnection");
        Assert.NotNull(connection);

        var frameTypeInfo = connection!.GetType().DeclaringType!.GetNestedType("TransportFrame", BindingFlags.NonPublic);
        Assert.NotNull(frameTypeInfo);
        var frame = Activator.CreateInstance(frameTypeInfo!);
        Assert.NotNull(frame);
        frameTypeInfo!.GetProperty("Type")!.SetValue(frame, frameType);
        frameTypeInfo.GetProperty("Data")!.SetValue(frame, Convert.ToBase64String(payload));

        var writeTask = (Task)connection.GetType().GetMethod("WriteFrameAsync")!.Invoke(connection, new[] { frame!, ct })!;
        await writeTask;
    }

internal static async Task<string> ApproveNknSessionAsync(
        NknSignalingTransport host,
        NknSignalingTransport helper,
        CancellationToken ct,
        InviteCapabilities capabilities = InviteCapabilities.Chat)
    {
        var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
        host.Approved += (_, _) => hostApproved.TrySetResult();
        helper.Approved += (_, _) => helperApproved.TrySetResult();

        await host.HostByAddressAsync(ct);
        var invite = CreateValidatedInviteForTarget(new PeerAddress(host.LocalPeerAddress), out var rawToken, capabilities);
        await helper.JoinByInviteAsync(rawToken, invite, ct);

        var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), ct);
        await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);

        return host.CurrentSessionSecurityState.SessionId!.Value.Value;
    }

internal static List<string> FindOperationalLogLines(Func<string, bool> predicate)
    {
        var logText = File.Exists(LocalOperationalLog.LogFilePath)
            ? File.ReadAllText(LocalOperationalLog.LogFilePath)
            : string.Empty;
        return logText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(predicate)
            .ToList();
    }

internal static int GetOperationalLogLength()
    {
        return ReadOperationalLogText().Length;
    }

internal static string ReadOperationalLogTail(int startIndex)
    {
        var logText = ReadOperationalLogText();
        if (startIndex <= 0 || startIndex >= logText.Length)
        {
            return startIndex >= logText.Length ? string.Empty : logText;
        }

        return logText[startIndex..];
    }

internal static string ReadOperationalLogText()
    {
        if (!File.Exists(LocalOperationalLog.LogFilePath))
        {
            return string.Empty;
        }

        using var stream = new FileStream(
            LocalOperationalLog.LogFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

internal static void SetPrivateProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

internal static bool InvokeCanEndForPhase(Type viewModelType, SessionUiPhase phase)
    {
        var method = viewModelType.GetMethod("CanEndForPhase", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object[] { phase })!;
    }

internal static TransportRuntimeConfig CreateDevLocalTestConfig()
    {
        var previous = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "DEVLOCAL");
            return TransportRuntimeConfig.Select();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previous);
        }
    }

internal static byte[] SHA256LikeDeterministicBytes(string input, int length)
    {
        var source = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        if (length == source.Length)
        {
            return source;
        }

        var buffer = new byte[length];
        Array.Copy(source, buffer, length);
        return buffer;
    }

internal static Bitmap CreateTestBitmap(int width, int height)
    {
        _ = width;
        _ = height;
        return (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
    }

internal static byte[] CreateChatPayloadBytes(
        string messageId,
        string text,
        long timestampUnixMs)
    {
        var payload = new ChatMessagePayload
        {
            MessageId = messageId,
            Text = text,
            TimestampUnixMilliseconds = timestampUnixMs,
        };

        return ChatEnvelopeCodec.SerializePayload(payload);
    }

internal static string? TryFindBridgeBundleDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "bridge", "win-x64");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

internal static string? ResolveBridgeRuntimeDirectoryForHealthCheck(out string attemptedPath, out string source)
    {
        var envValue = Environment.GetEnvironmentVariable("NLINK_BRIDGE_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            source = "env:NLINK_BRIDGE_RUNTIME_DIR";
            attemptedPath = ResolvePathFromRepoRoot(envValue);
            return Directory.Exists(attemptedPath) ? attemptedPath : null;
        }

        source = "default:artifacts/bridge/win-x64";
        attemptedPath = ResolvePathFromRepoRoot(Path.Combine("artifacts", "bridge", "win-x64"));
        if (Directory.Exists(attemptedPath))
        {
            return attemptedPath;
        }

        return TryFindBridgeBundleDirectory();
    }

internal static string ResolvePathFromRepoRoot(string pathValue)
    {
        if (Path.IsPathRooted(pathValue))
        {
            return Path.GetFullPath(pathValue);
        }

        var versionPath = FindFileUpwards("VERSION");
        if (!string.IsNullOrWhiteSpace(versionPath))
        {
            var repoRoot = Path.GetDirectoryName(versionPath)!;
            return Path.GetFullPath(Path.Combine(repoRoot, pathValue));
        }

        return Path.GetFullPath(pathValue);
    }

internal static NknTransportOptions LoadNknOptionsWithOverrides(string keyPath, string identifier)
    {
        var prevKeyPath = Environment.GetEnvironmentVariable("NLINK_NKN_KEY_PATH");
        var prevIdentifier = Environment.GetEnvironmentVariable("NLINK_NKN_IDENTIFIER");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", keyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", identifier);
            return NknTransportOptions.Load();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_NKN_KEY_PATH", prevKeyPath);
            Environment.SetEnvironmentVariable("NLINK_NKN_IDENTIFIER", prevIdentifier);
        }
    }

internal static void WriteIdentityFile(string keyPath, string identifier)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllText(
            keyPath,
            JsonSerializer.Serialize(
                new
                {
                    Version = 3,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Identifier = identifier,
                    SeedBase64 = (string?)null,
                    Address = $"{identifier}.{Guid.NewGuid():N}"[..Math.Min(identifier.Length + 1 + 20, identifier.Length + 1 + 32)],
                },
                new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(NknSecretStore.GetSecretPath(keyPath), "seed-placeholder");
    }

#pragma warning disable CS0067

internal static TransportRuntimeConfig CreateNknTestConfig()
    {
        var previous = Environment.GetEnvironmentVariable("NLINK_TRANSPORT");

        try
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", "NKN");
            var selected = TransportRuntimeConfig.Select();
            if (!selected.HasStartupWarning)
            {
                return selected;
            }

            // Most smoke tests that call this helper inject scripted/fake transports.
            // Keep those tests independent of whether a Release bridge bundle is staged
            // in the test output; CreateStartupBlockedNknTestConfig covers that path.
            NknRuntimeDiagnostics.SetLastError(string.Empty);
            return CreateTransportRuntimeConfigForTests(
                selected,
                bridgeBundled: true,
                bridgeBundleProbeReason: "test:scripted-nkn-bridge-available",
                startupWarningText: null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_TRANSPORT", previous);
        }
    }

internal static TransportRuntimeConfig CreateStartupBlockedNknTestConfig(string startupWarningText = "Couldn't start the connection. Please reinstall.")
    {
        var baseline = CreateNknTestConfig();
        return CreateTransportRuntimeConfigForTests(
            baseline,
            bridgeBundled: false,
            bridgeBundleProbeReason: "test:forced-missing-bridge",
            startupWarningText: startupWarningText);
    }

    private static TransportRuntimeConfig CreateTransportRuntimeConfigForTests(
        TransportRuntimeConfig baseline,
        bool bridgeBundled,
        string bridgeBundleProbeReason,
        string? startupWarningText)
    {
        var ctor = typeof(TransportRuntimeConfig).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(BridgeReusePolicy),
                typeof(Func<ISignalingTransport>),
            },
            modifiers: null);

        Assert.NotNull(ctor);
        return (TransportRuntimeConfig)ctor!.Invoke(
            new object?[]
            {
                baseline.Key,
                baseline.DisplayName,
                baseline.BuildMode,
                baseline.EnvironmentVariableValue,
                baseline.SelectionReason,
                baseline.ForcedByEnvironment,
                baseline.AutoSelected,
                baseline.IsDevLocal,
                bridgeBundled,
                bridgeBundleProbeReason,
                startupWarningText,
                baseline.ConfigurationErrorText,
                baseline.BridgeReusePolicy,
                baseline.CreateTransport,
            });
    }

internal sealed class CountingTransportFactory
    {
        private readonly Func<ISignalingTransport> factory;

        public CountingTransportFactory(Func<ISignalingTransport> factory)
        {
            this.factory = factory;
        }

        public int CreateCount { get; private set; }

        public ISignalingTransport Create()
        {
            CreateCount++;
            return factory();
        }
    }

internal sealed class NoOpQrCodeService : IQrCodeService
    {
        public bool TryCreatePng(string text, out byte[] pngBytes, out string? errorMessage)
        {
            pngBytes = [];
            errorMessage = "not_used_in_test";
            return false;
        }

        public bool TryDecode(Stream imageStream, out string? decodedText, out string? errorMessage)
        {
            decodedText = null;
            errorMessage = "not_used_in_test";
            return false;
        }
    }

internal sealed class FixedCaptureSourceFactory : IScreenCaptureSourceFactory
    {
        private readonly IScreenCaptureSource source;

        public FixedCaptureSourceFactory(IScreenCaptureSource source)
        {
            this.source = source;
        }

        public IScreenCaptureSource Create() => source;
    }

internal static ScreenShareFrameSendPipeline CreateControlledScreenShareFrameSendPipeline(
        Func<ScreenShareEncodedFramePacket, CancellationToken, Task<int>> sendFrameAsync,
        IScreenShareClock clock,
        ControlledDelayScheduler delayScheduler,
        int capacity = ScreenShareFrameSendPipeline.MaxBufferedFrames,
        int maxFramesPerSecond = 5)
    {
        var constructor = typeof(ScreenShareFrameSendPipeline).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(Func<ScreenShareEncodedFramePacket, CancellationToken, Task<int>>),
                typeof(int),
                typeof(IScreenShareClock),
                typeof(int),
                typeof(Func<TimeSpan, CancellationToken, Task>),
            },
            modifiers: null);

        Assert.NotNull(constructor);

        return (ScreenShareFrameSendPipeline)constructor!.Invoke(
            new object[]
            {
                sendFrameAsync,
                capacity,
                clock,
                maxFramesPerSecond,
                new Func<TimeSpan, CancellationToken, Task>(delayScheduler.DelayAsync),
            });
    }

internal sealed class ControlledDelayScheduler
    {
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> pending = new();

        public int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return pending.Count(t => !t.Task.IsCompleted);
                }
            }
        }

        public Task DelayAsync(TimeSpan _, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            ctr = ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                ctr.Dispose();
            });

            lock (gate)
            {
                pending.Add(tcs);
            }

            return tcs.Task;
        }

        public void CompleteLatest()
        {
            lock (gate)
            {
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].TrySetResult())
                    {
                        return;
                    }
                }
            }

            throw new InvalidOperationException("No pending delay task to complete.");
        }
    }

internal sealed class FakeScreenShareClock : IScreenShareClock
    {
        private DateTimeOffset utcNow;

        public FakeScreenShareClock(DateTimeOffset initialUtcNow)
        {
            utcNow = initialUtcNow;
        }

        public DateTimeOffset UtcNow => utcNow;

        public void Advance(TimeSpan by)
        {
            utcNow = utcNow.Add(by);
        }
    }

internal sealed class FakeBridgeProcessRunner : IBridgeProcessRunner
    {
        public bool WasForcedKillRequested { get; set; }
    }

}


