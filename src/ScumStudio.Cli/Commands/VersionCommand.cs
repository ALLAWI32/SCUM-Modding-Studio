using System.CommandLine;
using System.Runtime.InteropServices;
using ScumStudio.Core;

namespace ScumStudio.Cli.Commands;

/// <summary><c>scumstudio version</c>: prints the tool version and runtime information.</summary>
internal sealed class VersionCommand : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var command = new Command("version", "Print the ScumStudio version and runtime information.");
        command.SetHandler(() =>
        {
            Console.WriteLine($"{CoreInfo.ProductName} {CoreInfo.Version}");
            Console.WriteLine($"Target engine: Unreal Engine {CoreInfo.TargetEngineVersion} (SCUM cooked content)");
            Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        });
        return command;
    }
}
