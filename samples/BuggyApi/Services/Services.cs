namespace BuggyApi.Services;

// ---------- healthy services
public interface IRepository<T> { IReadOnlyList<T> All(); }
public sealed class Repository<T> : IRepository<T>
{
    public Repository(ILogger<Repository<T>> logger) { }
    public IReadOnlyList<T> All() => Array.Empty<T>();
}

public interface IPriceCache { decimal? Get(string sku); }
public sealed class FastPriceCache : IPriceCache { public decimal? Get(string sku) => null; }

public sealed class PricingService
{
    public PricingService([FromKeyedServices("fast")] IPriceCache cache, IRepository<Product> products) { }
}

public sealed record Product(string Sku);
public sealed record Order(int Id);

public interface IOrderRepository { Order? Find(int id); }
public sealed class OrderRepository : IOrderRepository
{
    public OrderRepository(IRepository<Order> orders) { }
    public Order? Find(int id) => null;
}

public interface IOrderService { void Place(Order order); }
public sealed class OrderService : IOrderService
{
    public OrderService(IOrderRepository orders, INotifier notifier, IAuditLog audit, PricingService pricing) { }
    public void Place(Order order) { }
}

public interface IUserContext { string? UserName { get; } }
public sealed class HttpUserContext : IUserContext
{
    private readonly IHttpContextAccessor _accessor;
    public HttpUserContext(IHttpContextAccessor accessor) => _accessor = accessor;
    public string? UserName => _accessor.HttpContext?.User.Identity?.Name;
}

public interface IHealthCheck { bool IsHealthy(); }
public sealed class DatabaseHealthCheck : IHealthCheck { public bool IsHealthy() => true; }
public sealed class DiskHealthCheck : IHealthCheck { public bool IsHealthy() => true; }
public sealed class HealthReporter
{
    public HealthReporter(IEnumerable<IHealthCheck> checks) { }
}

// ---------- DI001 captive dependency
public interface ICache { }
public sealed class MemoryCache : ICache
{
    public MemoryCache(IOrderRepository orders) { }
}

// ---------- DI001 transitive
public interface IReportService { }
public sealed class ReportService : IReportService
{
    public ReportService(IFormatter formatter) { }
}
public interface IFormatter { }
public sealed class ReportFormatter : IFormatter
{
    public ReportFormatter(IUserContext user) { }
}

// ---------- DI002 missing registration
public interface IEmailSender { Task SendAsync(string to, string body); }

// ---------- DI003 cycle
public interface IAService { }
public interface IBService { }
public sealed class AService : IAService { public AService(IBService b) { } }
public sealed class BService : IBService { public BService(IAService a) { } }

// ---------- DI004 disposable transient captured by singleton
public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock
{
    public SystemClock(IGuidGenerator ids) { }
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}
public interface IGuidGenerator { Guid Next(); }
public sealed class GuidGenerator : IGuidGenerator, IDisposable
{
    public Guid Next() => Guid.NewGuid();
    public void Dispose() { }
}

// ---------- DI005 shadowed registration
public interface INotifier { void Notify(string message); }
public sealed class EmailNotifier : INotifier { public void Notify(string message) { } }
public sealed class SmsNotifier : INotifier { public void Notify(string message) { } }

// ---------- DI006 duplicate registration
public interface IAuditLog { void Write(string entry); }
public sealed class AuditLog : IAuditLog { public void Write(string entry) { } }

// ---------- DI007 possibly unused
public interface ILegacyService { }
public sealed class LegacyService : ILegacyService { }

// ---------- DI008 factory with unknown dependencies
public interface IPaymentGateway { }
public sealed class StripeGateway : IPaymentGateway { public StripeGateway(string apiKey) { } }
public static class PaymentGatewayFactory
{
    public static IPaymentGateway Create(string apiKey) => new StripeGateway(apiKey);
}

// ---------- factory with inferred dependencies
public interface IExchangeRates { }
public sealed class ExchangeRates : IExchangeRates
{
    public ExchangeRates(ILogger<ExchangeRates> logger, IClock clock) { }
}
