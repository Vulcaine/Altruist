namespace Altruist;

public enum TransportCode
{
    // ============================
    // 2xx — Success
    // ============================
    Ok = 200,
    Created = 201,
    Accepted = 202,
    PartialSuccess = 203,
    NoContent = 204,

    // ============================
    // 3xx — Redirection / Flow Control
    // ============================
    MultipleChoices = 300,
    MovedPermanently = 301,
    MovedTemporarily = 302,
    TemporaryReconnectionRequired = 307,

    // ============================
    // 4xx — Client Errors
    // ============================
    BadRequest = 400,
    Unauthorized = 401,
    Forbidden = 403,
    NotFound = 404,
    Conflict = 409,
    Gone = 410,
    TooManyRequests = 429,

    // ============================
    // 5xx — Server Errors
    // ============================
    InternalServerError = 500,
    NotImplemented = 501,
    ServiceUnavailable = 503,
    Timeout = 504
}
