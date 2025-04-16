namespace Altruist.Security;

public class AuthDetails
{
    public string Token { get; set; }
    public DateTimeOffset Expiry { get; set; }

    public AuthDetails(string token, TimeSpan validityPeriod)
    {
        Token = token;
        Expiry = DateTimeOffset.UtcNow.Add(validityPeriod);
    }

    public bool IsAlive() => DateTimeOffset.UtcNow < Expiry;

    public double TimeLeftSeconds() => (Expiry - DateTimeOffset.UtcNow).TotalSeconds;
}

