using System;
using System.Linq;
using System.Text.Json;

namespace NLink.App.Services;

public enum TransportFailureCategory
{
    BridgeStartFailure,
    BridgeUnresponsive,
    BridgeCrashed,
    HandshakeTimeout,
    PeerUnreachable,
    NknSendFailure,
    JsonProtocolError,
    UnexpectedProcessExit,
    UserCancelled,
    Unknown,
}

public sealed record TransportFailure(
    TransportFailureCategory Category,
    string Message,
    string ExceptionType,
    string RawError,
    bool IsTransient,
    string CorrelationId)
{
    public static TransportFailure Create(
        TransportFailureCategory category,
        string message,
        string? exceptionType = null,
        string? rawError = null,
        bool isTransient = false,
        string? correlationId = null)
    {
        return new TransportFailure(
            category,
            string.IsNullOrWhiteSpace(message) ? "Transport failure" : message,
            string.IsNullOrWhiteSpace(exceptionType) ? "(none)" : exceptionType!,
            string.IsNullOrWhiteSpace(rawError) ? "(none)" : rawError!,
            isTransient,
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N")[..8] : correlationId!);
    }
}

public static class TransportFailureMapper
{
    public static TransportFailure FromException(
        Exception ex,
        string? rawError = null,
        string? lastDisconnectReason = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is OperationCanceledException)
        {
            return CreateFromExceptionCore(TransportFailureCategory.UserCancelled, "Cancelled", ex, rawError, lastDisconnectReason, isTransient: true);
        }

        if (ex is TimeoutException)
        {
            return CreateFromExceptionCore(TransportFailureCategory.HandshakeTimeout, "Timed out", ex, rawError, lastDisconnectReason, isTransient: true);
        }

        if (ex is JsonException)
        {
            return CreateFromExceptionCore(TransportFailureCategory.JsonProtocolError, "Protocol parse error", ex, rawError, lastDisconnectReason, isTransient: false);
        }

        return FromSignals(
            rawError ?? ex.Message,
            exceptionType: ex.GetType().Name,
            lastDisconnectReason: lastDisconnectReason,
            fallbackMessage: ex.Message);
    }

    public static TransportFailure FromSignals(
        string? rawError,
        string? exceptionType = null,
        string? lastDisconnectReason = null,
        string? fallbackMessage = null)
    {
        rawError = NormalizeSignalValue(rawError);
        lastDisconnectReason = NormalizeSignalValue(lastDisconnectReason);
        var raw = ((rawError ?? string.Empty) + " " + (lastDisconnectReason ?? string.Empty)).Trim();
        var normalized = raw.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransportFailure.Create(
                TransportFailureCategory.UserCancelled,
                string.IsNullOrWhiteSpace(fallbackMessage) ? "Session ended" : fallbackMessage!,
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("json", StringComparison.Ordinal) ||
            normalized.Contains("parse", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.JsonProtocolError,
                "Protocol parse error",
                exceptionType,
                raw,
                isTransient: false);
        }

        if (normalized.Contains("process exited", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.UnexpectedProcessExit,
                "Connection helper process exited",
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("bridge crashed", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.BridgeCrashed,
                "Connection helper process crashed",
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("bridge_ping_timeout", StringComparison.Ordinal) ||
            normalized.Contains("bridge_unresponsive", StringComparison.Ordinal) ||
            (normalized.Contains("nkn bridge hello failed", StringComparison.Ordinal) &&
             normalized.Contains("timed out", StringComparison.Ordinal)))
        {
            return TransportFailure.Create(
                TransportFailureCategory.BridgeUnresponsive,
                "Connection helper is not responding",
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("bridge_connect_ready_timeout", StringComparison.Ordinal) ||
            normalized.Contains("connecttonodetimeouterror", StringComparison.Ordinal) ||
            normalized.Contains("rpctimeouterror", StringComparison.Ordinal) ||
            normalized.Contains("nkn_start_failed: ready_timeout", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.HandshakeTimeout,
                "Timed out",
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("bridge_hello_failed", StringComparison.Ordinal) ||
            normalized.Contains("nkn bridge hello failed", StringComparison.Ordinal) ||
            normalized.Contains("nkn_start_failed: bridge_start", StringComparison.Ordinal) ||
            normalized.Contains("nkn_start_failed: bridge_missing", StringComparison.Ordinal) ||
            normalized.Contains("bridge runtime not found", StringComparison.Ordinal) ||
            normalized.Contains("could not start the local helper process", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.BridgeStartFailure,
                "Could not start the connection system",
                exceptionType,
                raw,
                isTransient: false);
        }

        if (normalized.Contains("could not find session for code", StringComparison.Ordinal) ||
            normalized.Contains("no one found with that code", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.PeerUnreachable,
                "No response from target address",
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("append tx", StringComparison.Ordinal) ||
            normalized.Contains("no destinations", StringComparison.Ordinal) ||
            normalized.Contains("send failed", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.NknSendFailure,
                "Could not send over network",
                exceptionType,
                raw,
                isTransient: true);
        }

        if (normalized.Contains("timeout", StringComparison.Ordinal))
        {
            return TransportFailure.Create(
                TransportFailureCategory.HandshakeTimeout,
                "Timed out",
                exceptionType,
                raw,
                isTransient: true);
        }

        return TransportFailure.Create(
            TransportFailureCategory.Unknown,
            string.IsNullOrWhiteSpace(fallbackMessage) ? "Transport failure" : fallbackMessage!,
            exceptionType,
            raw,
            isTransient: false);
    }

    public static TransportFailure CreateTimeout(string? rawError = null)
    {
        return TransportFailure.Create(
            TransportFailureCategory.HandshakeTimeout,
            "Timed out",
            exceptionType: nameof(TimeoutException),
            rawError: rawError,
            isTransient: true);
    }

    private static TransportFailure CreateFromExceptionCore(
        TransportFailureCategory category,
        string message,
        Exception ex,
        string? rawError,
        string? lastDisconnectReason,
        bool isTransient)
    {
        var combinedRaw = string.Join(" | ", new[] { rawError, lastDisconnectReason }.Where(static s => !string.IsNullOrWhiteSpace(s)));
        return TransportFailure.Create(
            category,
            message,
            exceptionType: ex.GetType().Name,
            rawError: combinedRaw,
            isTransient: isTransient);
    }

    private static string? NormalizeSignalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("(none)", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }
}
