using LegacyWorker;
using Microsoft.EntityFrameworkCore;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            Host.CreateDefaultBuilder(args)
                .ConfigureServices(services =>
                {
                    services.AddDbContext<ShopContext>(o => o.UseInMemoryDatabase("shop"));
                    services.AddHttpClient<IWeatherClient, WeatherClient>();
                    services.AddHostedService<SyncWorker>();

                    // DI001: the worker is a singleton (hosted service) but takes the scoped DbContext through ISyncService.
                    services.AddTransient<ISyncService, SyncService>();
                })
                .Build()
                .Run();
            return 0;
        }
        catch (Exception ex)
        {
            // Typical "log and exit" pattern: DiScope must still get its result out.
            Console.Error.WriteLine("Host terminated unexpectedly: " + ex.Message);
            Environment.Exit(42);
            return 1;
        }
    }
}
