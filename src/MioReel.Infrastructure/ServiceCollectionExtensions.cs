using Microsoft.Extensions.DependencyInjection;
using MioReel.Core.Abstractions;
using MioReel.Infrastructure.Services;
using MioReel.Infrastructure.Services.MediaProxy;

namespace MioReel.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMioReelInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(_ => new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        });
        services.AddSingleton<IDramaCatalogService, GatewayDramaCatalogService>();
        services.AddSingleton<IMediaStreamProxyService, LocalMediaProxyService>();
        services.AddSingleton<IHistoryService, FileHistoryService>();
        services.AddSingleton<IDownloadService, FileDownloadService>();
        services.AddSingleton<ISettingsService, FileSettingsService>();
        services.AddSingleton<IAuthService>(sp =>
            new ApiAuthService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<IHistorySyncService, HistorySyncService>();
        return services;
    }
}
