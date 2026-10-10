using Microsoft.EntityFrameworkCore;

namespace LegacyWorker;

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options);

public interface IWeatherClient { }
public sealed class WeatherClient(HttpClient http) : IWeatherClient;

public interface ISyncService { }
public sealed class SyncService(ShopContext db, IWeatherClient weather) : ISyncService;

public sealed class SyncWorker(ISyncService sync, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
