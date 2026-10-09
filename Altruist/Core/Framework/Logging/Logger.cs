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

using Microsoft.Extensions.Logging;

/// <summary>
/// The framework's console <see cref="ILogger"/>: writes every entry as <c>[ALTRUIST-major.minor] message</c>,
/// coloured by level on an interactive console and as plain single lines when output is redirected.
/// </summary>
/// <remarks>
/// Created by <see cref="AltruistLoggerProvider"/>, which the bootstrap installs as the only logging provider
/// (other providers are cleared). Do not construct it directly; inject <c>ILogger&lt;T&gt;</c> as usual.
/// All levels are enabled (filtering is left to the logging configuration), scopes are not supported, the
/// category name is not printed, and exceptions are appended to the message. Thread-safe.
/// </remarks>
public class AltruistLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _frameworkVersion;

    /// <summary>Creates a logger for a category.</summary>
    /// <param name="categoryName">Logging category (stored, not printed).</param>
    /// <param name="frameworkVersion">Framework version; must contain at least <c>major.minor</c> (only those are printed).</param>
    public AltruistLogger(string categoryName, string frameworkVersion)
    {
        _categoryName = categoryName;
        var versionParts = frameworkVersion.Split('.');
        _frameworkVersion = $"{versionParts[0]}.{versionParts[1]}";
    }

    /// <summary>Scopes are not supported; always returns null.</summary>
    /// <typeparam name="TState">Scope state type.</typeparam>
    /// <param name="state">Ignored.</param>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    /// <summary>Always true; level filtering is done by the logging infrastructure's configured filters.</summary>
    /// <param name="logLevel">Ignored.</param>
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var logMessage = formatter(state, exception);
        // The formatter does not include the exception; without it errors are undiagnosable.
        if (exception is not null)
            logMessage = $"{logMessage}{Environment.NewLine}{exception}";

        var versionMessage = $"[ALTRUIST-{_frameworkVersion}]";
        if (Console.IsOutputRedirected)
        {
            // One WriteLine per entry: Console.Out is synchronized, so concurrent loggers never
            // interleave (Write + WriteLine pairs did, and colour codes are useless in a file).
            Console.Out.WriteLine($"{versionMessage} {logMessage}");
            return;
        }

        var logLevelColor = logLevel switch
        {
            LogLevel.Trace => ConsoleColor.Gray,
            LogLevel.Debug => ConsoleColor.Magenta,
            LogLevel.Information => ConsoleColor.Blue,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Critical => ConsoleColor.Red,
            LogLevel.None => ConsoleColor.Gray,
            _ => ConsoleColor.White
        };
        lock (ConsoleLock)
        {
            Console.ForegroundColor = logLevelColor;
            Console.Write(versionMessage);
            Console.ResetColor();
            Console.WriteLine($" {logMessage}");
        }
    }

    private static readonly object ConsoleLock = new();
}


/// <summary>
/// <see cref="ILoggerProvider"/> that hands out <see cref="AltruistLogger"/> instances. Installed by the
/// framework bootstrap via <c>loggingBuilder.AddProvider(...)</c> after clearing the default providers.
/// </summary>
public class AltruistLoggerProvider : ILoggerProvider
{
    private readonly string _frameworkVersion;

    /// <summary>Creates the provider.</summary>
    /// <param name="frameworkVersion">Version string (at least <c>major.minor</c>) shown in every log line.</param>
    public AltruistLoggerProvider(string frameworkVersion)
    {
        _frameworkVersion = frameworkVersion;
    }

    /// <summary>Returns a new <see cref="AltruistLogger"/> for <paramref name="categoryName"/> (not cached).</summary>
    /// <param name="categoryName">Logging category.</param>
    public ILogger CreateLogger(string categoryName)
    {
        // Create and return a new AltruistLogger
        return new AltruistLogger(categoryName, _frameworkVersion);
    }

    /// <summary>No-op; the provider holds no resources.</summary>
    public void Dispose()
    {
        // No resources to dispose of in this example
    }
}
