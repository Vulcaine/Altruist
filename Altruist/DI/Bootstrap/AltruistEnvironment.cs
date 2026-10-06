/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist
{
    /// <summary>
    /// The hosting environment the application runs in: <c>DOTNET_ENVIRONMENT</c>, else
    /// <c>ASPNETCORE_ENVIRONMENT</c>, else <see cref="Production"/>. Decides which
    /// <c>config.{Name}.yml</c> <see cref="AppConfigLoader"/> loads and which safety checks are fatal
    /// (e.g. the JWT key guard). Read it here instead of reading the variables yourself, so every
    /// part of the application agrees on the environment.
    /// </summary>
    public static class AltruistEnvironment
    {
        public const string Development = "Development";
        public const string Production = "Production";

        /// <summary>The environment name (unset or blank variables count as unset).</summary>
        public static string Name =>
            Read("DOTNET_ENVIRONMENT") ?? Read("ASPNETCORE_ENVIRONMENT") ?? Production;

        public static bool IsDevelopment => Is(Development);

        /// <summary>True when the environment is Production, including when none is set.</summary>
        public static bool IsProduction => Is(Production);

        /// <summary>Case-insensitive comparison with the current <see cref="Name"/>.</summary>
        public static bool Is(string name) => string.Equals(Name, name?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string? Read(string variable)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
