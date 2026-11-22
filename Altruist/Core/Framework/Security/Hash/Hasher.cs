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

using System.Security.Cryptography;

namespace Altruist.Security;

/// <summary>
/// Password hashing for account stores. Inject it in your <see cref="Altruist.Security.Auth.ILoginService"/>: store
/// <see cref="Hash"/> results and check sign-ins with <see cref="Verify"/>. Default: <see cref="BcryptPasswordHasher"/>
/// (singleton). Not for tokens: opaque tokens are hashed with <see cref="OpaqueToken.Hash"/> (fast SHA-256 suffices for
/// 256-bit random secrets).
/// </summary>
/// <example>
/// <code>
/// var account = await accounts.FindByNameAsync(name);          // may be null
/// if (!hasher.Verify(password, account?.PasswordHash)) return LoginResult.RFailure("Invalid credentials");
/// </code>
/// </example>
public interface IPasswordHasher
{
    /// <summary>A salted hash of <paramref name="password"/> to store (includes algorithm and cost; CPU-heavy, call off hot paths).</summary>
    string Hash(string password);

    /// <summary>
    /// True when <paramref name="password"/> matches <paramref name="hash"/>. A null hash (unknown
    /// user) costs one verify against a dummy hash and returns false, so both paths take the same
    /// time; a malformed hash returns false.
    /// </summary>
    bool Verify(string password, string? hash);
}

/// <summary>
/// BCrypt password hashes. Config <c>altruist:security:password</c>:
/// <c>work-factor</c> (default 11) and <c>prehash</c>: <c>none</c> (default, plain BCrypt, which
/// only uses the first 72 bytes of a password) or <c>sha384</c> (BCrypt over a SHA-384 pre-hash,
/// BCrypt.Net's "enhanced" mode, so long passwords are not truncated). Hashes of one prehash mode
/// do not verify in the other: pick it before storing hashes.
/// </summary>
[Service(typeof(IPasswordHasher))]
public class BcryptPasswordHasher : IPasswordHasher
{
    /// <summary>Default BCrypt cost (2^11 rounds).</summary>
    public const int DefaultWorkFactor = 11;
    /// <summary><c>prehash</c> value for plain BCrypt.</summary>
    public const string PrehashNone = "none";
    /// <summary><c>prehash</c> value for BCrypt over SHA-384.</summary>
    public const string PrehashSha384 = "sha384";

    private readonly Lazy<string> _dummyHash;

    /// <summary>BCrypt cost (log2 rounds, 4 to 31) used for new hashes (<c>altruist:security:password:work-factor</c>). Existing hashes verify at their own cost.</summary>
    public int WorkFactor { get; }

    /// <summary>True for the <c>sha384</c> prehash (BCrypt.Net enhanced hashing).</summary>
    public bool Enhanced { get; }

    /// <summary>A hasher with work factor 11 and no prehash.</summary>
    public BcryptPasswordHasher() : this(DefaultWorkFactor, PrehashNone) { }

    /// <summary>Creates a hasher (DI reads both values from configuration).</summary>
    /// <param name="workFactor">BCrypt cost, 4 to 31 (<c>altruist:security:password:work-factor</c>, default 11).</param>
    /// <param name="prehash"><c>none</c> or <c>sha384</c> (<c>altruist:security:password:prehash</c>, default none).</param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="workFactor"/> is outside 4..31.</exception>
    /// <exception cref="ArgumentException">When <paramref name="prehash"/> is not a known mode.</exception>
    public BcryptPasswordHasher(
        [AppConfigValue("altruist:security:password:work-factor", "11")] int workFactor,
        [AppConfigValue("altruist:security:password:prehash", PrehashNone)] string prehash)
    {
        if (workFactor is < 4 or > 31)
            throw new ArgumentOutOfRangeException(nameof(workFactor), workFactor, "BCrypt work factors run from 4 to 31.");
        WorkFactor = workFactor;
        Enhanced = (prehash ?? PrehashNone).Trim().ToLowerInvariant() switch
        {
            PrehashNone or "" => false,
            PrehashSha384 => true,
            var other => throw new ArgumentException(
                $"altruist:security:password:prehash must be '{PrehashNone}' or '{PrehashSha384}', not '{other}'.", nameof(prehash)),
        };
        // Verified against when there is no hash, so a missing user costs as much as a wrong password.
        _dummyHash = new Lazy<string>(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));
    }

    /// <inheritdoc/>
    public string Hash(string password) => Enhanced
        ? BCrypt.Net.BCrypt.EnhancedHashPassword(password, WorkFactor)
        : BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);

    /// <inheritdoc/>
    public bool Verify(string password, string? hash)
    {
        try
        {
            if (hash is null)
            {
                Check(password, _dummyHash.Value);
                return false;
            }
            return Check(password, hash);
        }
        catch (Exception ex) when (ex is BCrypt.Net.SaltParseException or ArgumentException)
        {
            return false;
        }
    }

    private bool Check(string password, string hash) => Enhanced
        ? BCrypt.Net.BCrypt.EnhancedVerify(password, hash)
        : BCrypt.Net.BCrypt.Verify(password, hash);
}
