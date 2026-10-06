using System.Collections;

namespace MMW.App.Services;

/// <summary>A tiny service provider for queue actions: services are looked up by their exact type.</summary>
public sealed class ServiceMap : IServiceProvider, IEnumerable<object>
{
    private readonly Dictionary<Type, object> _services = [];

    public void Add(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _services[service.GetType()] = service;
    }

    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);

    public IEnumerator<object> GetEnumerator() => _services.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
