using System;

namespace NLink.App.Services;

public enum BridgeReuseMode
{
    PerSession,
    KeepAlive,
}

public readonly record struct BridgeReusePolicy(
    BridgeReuseMode Mode,
    TimeSpan KeepAliveIdleTimeout)
{
    public static BridgeReusePolicy Default => new(BridgeReuseMode.PerSession, TimeSpan.FromSeconds(60));

    public bool IsKeepAlive => Mode == BridgeReuseMode.KeepAlive;

    public string Label => Mode switch
    {
        BridgeReuseMode.KeepAlive => "KeepAlive",
        _ => "PerSession",
    };
}

