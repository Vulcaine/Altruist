using Microsoft.AspNetCore.Http;

namespace Altruist;

public enum ReadyState
{
    Starting = 0,
    Failed = 1,
    Alive = 2
}

public interface IAppStatus
{
    ReadyState Status { get; set; }
    void SignalState(ReadyState state);
}

public class AppStatus : IAppStatus
{
    public ReadyState Status { get; set; } = ReadyState.Starting;
    public void SignalState(ReadyState state) => Status = state;
}

public class ReadinessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAppStatus _appStatus;

    public ReadinessMiddleware(RequestDelegate next, IAppStatus appStatus)
    {
        _next = next;
        _appStatus = appStatus;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_appStatus.Status != ReadyState.Alive)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Service is not ready.");
            return;
        }

        await _next(context);
    }
}
