namespace Altruist;

/// <summary>Application-level result codes carried in <see cref="SuccessPacket.Code"/> and
/// <see cref="FailedPacket.Code"/> (and in HTTP error bodies). Values mirror HTTP status codes so their
/// meaning is familiar, but they travel inside packets over any transport (WebSocket, TCP, UDP) and are
/// independent of the transport's own status. Build results with <see cref="ResultPacket"/>.</summary>
/// <example><code>
/// return ResultPacket.Failed(TransportCode.BadRequest, "Room is full.");
/// return ResultPacket.Success(TransportCode.Accepted, responsePacket);
/// </code></example>
public static class TransportCode
{
    // ============================
    // 2xx — Success
    // ============================
    /// <summary>200: the request succeeded.</summary>
    public const int Ok = 200;
    /// <summary>201: the request succeeded and created something.</summary>
    public const int Created = 201;
    /// <summary>202: the request was accepted (e.g. a handshake); processing may continue.</summary>
    public const int Accepted = 202;
    /// <summary>203: some parts of the request succeeded, others did not.</summary>
    public const int PartialSuccess = 203;
    /// <summary>204: the request succeeded with nothing to return.</summary>
    public const int NoContent = 204;

    // ============================
    // 3xx — Redirection / Flow Control
    // ============================
    /// <summary>300: several targets are possible; the client must choose.</summary>
    public const int MultipleChoices = 300;
    /// <summary>301: the target moved permanently (e.g. to another server/room).</summary>
    public const int MovedPermanently = 301;
    /// <summary>302: the target moved temporarily.</summary>
    public const int MovedTemporarily = 302;
    /// <summary>307: the client should reconnect (temporarily) before retrying.</summary>
    public const int TemporaryReconnectionRequired = 307;

    // ============================
    // 4xx — Client Errors
    // ============================
    /// <summary>400: the request was malformed or not allowed in the current state.</summary>
    public const int BadRequest = 400;
    /// <summary>401: the client is not authenticated.</summary>
    public const int Unauthorized = 401;
    /// <summary>403: the client is authenticated but not allowed to do this.</summary>
    public const int Forbidden = 403;
    /// <summary>404: the target does not exist.</summary>
    public const int NotFound = 404;
    /// <summary>409: the request conflicts with the current state.</summary>
    public const int Conflict = 409;
    /// <summary>410: the target existed but is gone for good.</summary>
    public const int Gone = 410;
    /// <summary>429: the client is rate-limited.</summary>
    public const int TooManyRequests = 429;

    // ============================
    // 5xx — Server Errors
    // ============================
    /// <summary>500: an unexpected server error.</summary>
    public const int InternalServerError = 500;
    /// <summary>501: the operation is not implemented.</summary>
    public const int NotImplemented = 501;
    /// <summary>503: the service is temporarily unavailable.</summary>
    public const int ServiceUnavailable = 503;
    /// <summary>504: the operation timed out.</summary>
    public const int Timeout = 504;
}
