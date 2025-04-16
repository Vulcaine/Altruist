using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Altruist.Security;

public class JwtAuth : IShieldAuth
{
    private readonly JwtTokenValidator _tokenValidator;
    private readonly TokenSessionSyncService? _syncService;

    public JwtAuth(JwtTokenValidator tokenValidator, IServiceProvider serviceProvider)
    {
        _tokenValidator = tokenValidator;
        _syncService = serviceProvider.GetService<TokenSessionSyncService>();
    }

    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public async Task<AuthResult> HandleAuthAsync(IAuthContext context)
    {
        var token = GetTokenFromRequest(context);
        var authDetails = ExtractAuthDetails(token);

        if (_syncService != null)
        {
            var cached = await _syncService.FindCachedByIdAsync(authDetails.Token);

            if (cached == null)
            {
                return new AuthResult(AuthorizationResult.Failed(), null!);
            }

            token = cached.AccessToken;
        }

        if (string.IsNullOrEmpty(token) || _tokenValidator.ValidateToken(token) == null)
        {
            return new AuthResult(AuthorizationResult.Failed(), null!);
        }


        return new AuthResult(AuthorizationResult.Success(), authDetails);
    }

    private string GetTokenFromRequest(IAuthContext context)
    {
        if (context is HttpAuthContext httpAuthContext)
        {
            var request = httpAuthContext.HttpContext.Request;
            return request.Headers["Authorization"].ToString().Replace("Bearer ", "").Split(";")[0];
        }
        else
        {
            throw new NotSupportedException($"Unsupported authentication context type {context.GetType().Name}.");
        }
    }

    private AuthDetails ExtractAuthDetails(string token)
    {
        var jwt = _tokenHandler.ReadJwtToken(token);
        var expClaim = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp);

        if (expClaim == null || !long.TryParse(expClaim.Value, out long expUnix))
        {
            throw new Exception("Invalid JWT: Missing or malformed expiration claim.");
        }

        var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expUnix);
        var remainingTime = expirationTime - DateTimeOffset.UtcNow;
        var principalId = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value ?? "Unknown";
        var ip = jwt.Claims.FirstOrDefault(c => c.Type == "Ip")?.Value ?? "Unknown";
        var groupKey = jwt.Claims.FirstOrDefault(c => c.Type == "GroupKey")?.Value ?? "Unknown";

        return new AuthDetails(token, principalId, ip, groupKey, remainingTime);
    }
}

public class JwtTokenValidator : ITokenValidator
{
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _validationParams;

    public JwtTokenValidator(TokenValidationParameters parameters)
    {
        _tokenHandler = new JwtSecurityTokenHandler();
        _validationParams = parameters;
    }

    public ClaimsPrincipal GetClaimsPrincipal(string token) => _tokenHandler.ValidateToken(token, _validationParams, out _);

    public ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            return _tokenHandler.ValidateToken(token, _validationParams, out _);
        }
        catch
        {
            return null;
        }
    }
}


[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public class JwtShieldAttribute : ShieldAttribute
{
    public JwtShieldAttribute() : base(typeof(JwtAuth)) { }
}
