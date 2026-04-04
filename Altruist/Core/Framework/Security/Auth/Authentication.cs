
/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/
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

