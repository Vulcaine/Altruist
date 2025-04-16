namespace Altruist.Security;

public class AuthDetails
{
    public string Token { get; set; }
    public string PrincipalId { get; set; }
    public string Ip { get; set; }
    public string GroupKey { get; set; }
    public DateTimeOffset Expiry { get; set; }

    public AuthDetails(string token, string PrincipalId, string Ip, string GroupKey, TimeSpan validityPeriod)
    {
        Token = token;
        Expiry = DateTimeOffset.UtcNow.Add(validityPeriod);
        this.PrincipalId = PrincipalId;
        this.Ip = Ip;
        this.GroupKey = GroupKey;
    }

    public bool IsAlive() => DateTimeOffset.UtcNow < Expiry && !(Ip == "Unknown" || PrincipalId == "Unknown" || GroupKey == "Unknown");

    public double TimeLeftSeconds() => (Expiry - DateTimeOffset.UtcNow).TotalSeconds;
}

