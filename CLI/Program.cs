using System.Diagnostics;

namespace Altruist.CLI;

public static class Program
{
    private const string Version = "0.9.5-beta";
    private const string TemplateName = "altruist";
    private const string TemplatePackage = "Altruist.Templates";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
            return ShowHelp();

        if (args[0] is "--version" or "-v")
        {
            Console.WriteLine($"Altruist CLI v{Version}");
            return 0;
        }

        return args[0] switch
        {
            "create" or "new" => await CreateProject(args.Skip(1).ToArray()),
            "run" => await RunProject(args.Skip(1).ToArray()),
            _ => ShowHelp($"Unknown command: {args[0]}")
        };
    }

    private static async Task<int> CreateProject(string[] args)
    {
        var projectName = args.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(projectName))
        {
            Console.Error.WriteLine("Usage: altruist create <ProjectName>");
            return 1;
        }

        Console.WriteLine($"Creating Altruist project: {projectName}");
        Console.WriteLine();

        // Ensure template is installed
        Console.WriteLine("Checking template...");
        var checkResult = await RunProcess("dotnet", $"new list {TemplateName}");
        if (!checkResult.output.Contains(TemplateName))
        {
            Console.WriteLine($"Installing {TemplatePackage}...");
            await RunProcess("dotnet", $"new install {TemplatePackage}");
        }

        // Create project from template
        Console.WriteLine($"Scaffolding {projectName}...");
        var createResult = await RunProcess("dotnet", $"new {TemplateName} -n {projectName} -o {projectName}");
        if (createResult.exitCode != 0)
        {
            Console.Error.WriteLine($"Failed to create project: {createResult.output}");
            return 1;
        }

        // Restore packages
        Console.WriteLine("Restoring packages...");
        await RunProcess("dotnet", $"restore {projectName}");

        Console.WriteLine();
        Console.WriteLine($"  {projectName} created successfully!");
        Console.WriteLine();
        Console.WriteLine("  Next steps:");
        Console.WriteLine($"    cd {projectName}");
        Console.WriteLine("    dotnet run");
        Console.WriteLine();
        Console.WriteLine("  Then open:");
        Console.WriteLine("    http://localhost:8080/api/health        (REST API)");
        Console.WriteLine("    http://localhost:8080/api/health/hello  (Hello World)");
        Console.WriteLine("    ws://localhost:8080/                    (WebSocket)");
        Console.WriteLine();

        return 0;
    }

    private static async Task<int> RunProject(string[] args)
    {
        var result = await RunProcess("dotnet", "run " + string.Join(" ", args), inheritOutput: true);
        return result.exitCode;
    }

    private static int ShowHelp(string? error = null)
    {
        if (error != null)
            Console.Error.WriteLine($"Error: {error}\n");

        Console.WriteLine($"Altruist CLI v{Version}");
        Console.WriteLine();
        Console.WriteLine("Usage: altruist <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  create <name>    Create a new Altruist project");
        Console.WriteLine("  run              Run the current Altruist project");
        Console.WriteLine("  --version, -v    Show version");
        Console.WriteLine("  --help, -h       Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  altruist create MyGameServer");
        Console.WriteLine("  altruist create MyApi");
        Console.WriteLine("  cd MyGameServer && altruist run");
        Console.WriteLine();

        return error != null ? 1 : 0;
    }

    private static async Task<(int exitCode, string output)> RunProcess(
        string command, string arguments, bool inheritOutput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = !inheritOutput,
            RedirectStandardError = !inheritOutput,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        string output = "";

        if (!inheritOutput)
        {
            output = await process.StandardOutput.ReadToEndAsync();
            var err = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrEmpty(err))
                output += "\n" + err;
        }

        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}
