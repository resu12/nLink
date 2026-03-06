using NLink.Core.SessionConnect;

namespace NLink.App.Services.SessionConnect;

internal static class ConnectInputResolverFactory
{
    public static IConnectInputResolver CreateDefault()
    {
        return InviteTokenServiceFactory.CreateDefaultResolver();
    }

    public static IInviteTokenFactory CreateInviteTokenFactory()
    {
        return InviteTokenServiceFactory.CreateInviteTokenFactory();
    }
}
