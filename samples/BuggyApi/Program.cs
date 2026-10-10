using BuggyApi.Middleware;
using BuggyApi.Services;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddControllers();

// Healthy registrations: open generics, keyed services, IEnumerable<T>, options, logging.
services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
services.AddKeyedSingleton<IPriceCache, FastPriceCache>("fast");
services.AddScoped<PricingService>();
services.AddScoped<IOrderRepository, OrderRepository>();
services.AddScoped<IOrderService, OrderService>();
services.AddScoped<IUserContext, HttpUserContext>();
services.AddHttpContextAccessor();
services.AddSingleton<IHealthCheck, DatabaseHealthCheck>();
services.AddSingleton<IHealthCheck, DiskHealthCheck>();
services.AddSingleton<HealthReporter>();          // takes IEnumerable<IHealthCheck>: fine

// DI001 captive dependency: singleton MemoryCache -> scoped IOrderRepository.
services.AddSingleton<ICache, MemoryCache>();

// DI001 (transitive): singleton ReportService -> transient IFormatter -> scoped IUserContext.
services.AddSingleton<IReportService, ReportService>();
services.AddTransient<IFormatter, ReportFormatter>();

// DI002 missing registration: OrdersController needs IEmailSender, which is never registered.
//   (see Controllers/OrdersController.cs)

// DI003 cycle: AService -> IBService -> IAService.
services.AddScoped<IAService, AService>();
services.AddScoped<IBService, BService>();

// DI004 disposable transient captured by a singleton.
services.AddSingleton<IClock, SystemClock>();
services.AddTransient<IGuidGenerator, GuidGenerator>();

// DI005 shadowed registration: two INotifier registrations, OrderService injects a single one.
services.AddScoped<INotifier, EmailNotifier>();
services.AddScoped<INotifier, SmsNotifier>();

// DI006 duplicate registration.
services.AddScoped<IAuditLog, AuditLog>();
services.AddScoped<IAuditLog, AuditLog>();

// DI007 possibly unused.
services.AddTransient<ILegacyService, LegacyService>();

// DI008 factory with dependencies DiScope cannot see.
services.AddScoped<IPaymentGateway>(_ => PaymentGatewayFactory.Create("sk_test"));

// Factory with dependencies DiScope infers from IL (no diagnostic, edges shown in the graph).
services.AddSingleton<IExchangeRates>(sp =>
    new ExchangeRates(sp.GetRequiredService<ILogger<ExchangeRates>>(), sp.GetRequiredService<IClock>()));

var app = builder.Build();

// DI001 in middleware: conventional middleware is a singleton, but it takes scoped IUserContext.
app.UseMiddleware<RequestAuditMiddleware>();
app.MapControllers();
app.MapGet("/", () => "BuggyApi: run `discope` on me.");

app.Run();
