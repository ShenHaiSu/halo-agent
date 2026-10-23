using Host.Desktop.Bridge;
using Host.Desktop.Infrastructure.Config;
using Host.Desktop.Infrastructure.Network;
using Host.Desktop.Infrastructure.Storage;
using Host.Desktop.LlmGateway;
using Host.Desktop.Services;
using Host.Desktop.Tools;

namespace Host.Desktop.Startup;

/// <summary>
/// need21-B01: 轻量 CompositionRoot（不引入新 NuGet，避免 Cef/Win7 还原风险）。
/// 实现 <see cref="IServiceProvider"/> 的单例容器；B02~B04 在此基础上继续收敛。
/// 依赖方向：Desktop → Services → Tools/LlmGateway → Infrastructure → Core；Contracts 零依赖。
/// </summary>
public sealed class HaloServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, Func<object>> _factories = new();
    private readonly Dictionary<Type, object> _singletons = new();
    private readonly object _lock = new();

    public void AddSingleton<T>(T instance)
        where T : class
    {
        lock (_lock)
            _singletons[typeof(T)] = instance;
    }

    public void AddSingleton<T>(Func<HaloServiceProvider, T> factory)
        where T : class
    {
        lock (_lock)
            _factories[typeof(T)] = () => factory(this)!;
    }

    public object? GetService(Type serviceType)
    {
        lock (_lock)
        {
            if (_singletons.TryGetValue(serviceType, out var inst))
                return inst;
            if (_factories.TryGetValue(serviceType, out var f))
            {
                var created = f();
                _singletons[serviceType] = created;
                _factories.Remove(serviceType);
                return created;
            }
            return null;
        }
    }
}

/// <summary>HaloServiceProvider 取服务扩展（与 M.E.DI 同名，便于 B05 迁移）。</summary>
public static class HaloServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider provider)
        where T : class
    {
        var svc = provider.GetService(typeof(T)) as T;
        if (svc is null)
            throw new InvalidOperationException(
                $"need21-B01: service not registered: {typeof(T).FullName}"
            );
        return svc;
    }
}
