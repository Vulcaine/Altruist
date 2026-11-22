/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Security.Cryptography;
using System.Text;

namespace Altruist.Security;

/// <summary>
/// Opaque random secrets (refresh tokens, one-time links, connection tickets) and the hashes they
/// are stored and looked up by. Only the hash is ever stored: a leaked table holds no usable token.
/// </summary>
public static class OpaqueToken
{
    /// <summary>Length of <see cref="New"/> tokens: 32 bytes as unpadded base64url.</summary>
    public const int Length = 43;

    /// <summary>256 random bits, base64url without padding (43 characters).</summary>
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Lower-case hex SHA-256 of the token's UTF-8 bytes (64 characters).</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? throw new ArgumentNullException(nameof(token))))).ToLowerInvariant();

    /// <summary>
    /// Plausibility check before hashing untrusted input: the length and alphabet of <see cref="New"/>.
    /// Cheap rejection of junk, not a validity check (only a lookup of the hash is).
    /// </summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: Length } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
