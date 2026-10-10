using BuggyApi.Services;

namespace BuggyApi.Middleware;

/// <summary>
/// Conventional middleware is constructed once, so it behaves like a singleton:
/// taking a scoped service in the constructor is a captive dependency (DI001).
/// Scoped services belong in InvokeAsync's parameters instead.
/// </summary>
public sealed class RequestAuditMiddleware
{
    private readonly RequestDelegate _next;

    public RequestAuditMiddleware(RequestDelegate next, IUserContext user)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IAuditLog audit)
    {
        await _next(context);
    }
}
