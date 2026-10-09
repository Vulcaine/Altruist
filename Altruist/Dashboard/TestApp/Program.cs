using Altruist;

/// <summary>Sample host used to run the dashboard locally (not part of the package).</summary>
public static class Program
{
    /// <summary>Starts the Altruist application with <paramref name="args"/>.</summary>
    /// <param name="args">Command-line arguments.</param>
    public static async Task Main(string[] args)
        => await AltruistApplication.Run(args);
}
