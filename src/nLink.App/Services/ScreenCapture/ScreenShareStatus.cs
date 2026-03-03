using System;

namespace NLink.App.Services.ScreenCapture;

public sealed record ScreenShareStatus(
    ScreenShareState State,
    string? UserMessage,
    DateTimeOffset ChangedAt);
