namespace Altruist;

public static class TransportCode
{
    // ============================
    // 2xx — Success
    // ============================
    public const int Ok = 200;
    public const int Created = 201;
    public const int Accepted = 202;
    public const int PartialSuccess = 203;
    public const int NoContent = 204;

    // ============================
    // 3xx — Redirection / Flow Control
    // ============================
    public const int MultipleChoices = 300;
    public const int MovedPermanently = 301;
    public const int MovedTemporarily = 302;
    public const int TemporaryReconnectionRequired = 307;

    // ============================
    // 4xx — Client Errors
    // ============================
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Conflict = 409;
    public const int Gone = 410;
    public const int TooManyRequests = 429;

    // ============================
    // 5xx — Server Errors
    // ============================
    public const int InternalServerError = 500;
    public const int NotImplemented = 501;
    public const int ServiceUnavailable = 503;
    public const int Timeout = 504;
}
