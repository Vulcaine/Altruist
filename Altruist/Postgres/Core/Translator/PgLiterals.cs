/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Globalization;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Renders client-side values as Postgres SQL literals for the translators, which inline values instead of binding
/// parameters. Every format is culture-invariant and strings cannot terminate their literal.
/// </summary>
internal static class PgLiterals
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff";

    /// <summary>
    /// Formats <paramref name="value"/> as a literal: strings and chars as escape-string literals, numbers invariantly,
    /// enums as their integer value, GUIDs quoted, <see cref="DateTime"/> as a microsecond timestamp (a
    /// <see cref="DateTimeKind.Local"/> value is converted to UTC first, because stored timestamps are UTC and the
    /// session time zone is UTC), <see cref="DateTimeOffset"/> normalized to UTC; anything else as the escaped string
    /// of its <c>ToString()</c>.
    /// </summary>
    /// <param name="value">The value; <c>null</c> renders <c>NULL</c>.</param>
    /// <returns>The literal.</returns>
    /// <exception cref="ArgumentException">A string contains a NUL character.</exception>
    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => StringLiteral(s),
        char c => StringLiteral(c.ToString()),
        bool b => b ? "TRUE" : "FALSE",
        DateTime dt => $"'{(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).ToString(TimestampFormat, CultureInfo.InvariantCulture)}'",
        DateTimeOffset dto => $"'{dto.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)}+00'",
        Guid g => $"'{g:D}'",
        Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong
            => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        _ => StringLiteral(value.ToString() ?? "")
    };

    /// <summary>
    /// Renders <paramref name="s"/> as a Postgres escape-string literal (<c>E'...'</c>) with backslashes and quotes
    /// escaped, exact whatever <c>standard_conforming_strings</c> is set to (a plain <c>'...'</c> literal would let a
    /// trailing backslash swallow the closing quote on a server running with it off).
    /// </summary>
    /// <param name="s">The text.</param>
    /// <returns>The literal.</returns>
    /// <exception cref="ArgumentException"><paramref name="s"/> contains a NUL character.</exception>
    public static string StringLiteral(string s)
    {
        if (s.Contains('\0'))
            throw new ArgumentException("String literals may not contain NUL characters.");
        return $"E'{s.Replace("\\", "\\\\").Replace("'", "''")}'";
    }

    /// <summary>Quotes an identifier with double quotes, doubling embedded quotes.</summary>
    /// <param name="ident">The identifier.</param>
    /// <returns>The quoted identifier.</returns>
    public static string Ident(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";
}
