using System;
using System.Collections.Generic;

namespace NLink.App.Services;

public sealed class AppServiceRegistry
{
    private readonly Dictionary<Type, object> services = new();

    public void AddSingleton<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        services[typeof(TService)] = instance;
    }

    public TService GetRequired<TService>()
        where TService : class
    {
        if (services.TryGetValue(typeof(TService), out var value) && value is TService typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Service not registered: {typeof(TService).FullName}");
    }

    public bool TryGet<TService>(out TService? service)
        where TService : class
    {
        if (services.TryGetValue(typeof(TService), out var value) && value is TService typed)
        {
            service = typed;
            return true;
        }

        service = null;
        return false;
    }
}
