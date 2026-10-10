using BuggyApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BuggyApi.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;

    // DI002: IEmailSender is never registered.
    public OrdersController(IOrderService orders, IEmailSender email, IReportService reports, ICache cache)
    {
        _orders = orders;
    }

    [HttpPost]
    public IActionResult Place(Order order)
    {
        _orders.Place(order);
        return Accepted();
    }
}

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    public HealthController(HealthReporter reporter, IAService a, IExchangeRates rates, IClock clock) { }

    [HttpGet]
    public IActionResult Get() => Ok();
}
