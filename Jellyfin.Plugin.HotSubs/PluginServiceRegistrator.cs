using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.HotSubs;

/// <summary>
/// Registers <see cref="HotSubsActionFilter"/> as a global MVC filter.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Filters.Add<T>() wraps T in a TypeFilterAttribute: a fresh instance per request,
        // constructor args resolved from DI. No DI registration of the filter itself is needed.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<HotSubsActionFilter>());
    }
}
