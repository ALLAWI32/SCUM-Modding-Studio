using System.CommandLine;

namespace ScumStudio.Cli;

/// <summary>
/// A self-registering CLI command. Every non-abstract class in the scumstudio assembly that implements
/// this interface and has a parameterless constructor is discovered by reflection at startup and its
/// <see cref="Build"/> result is added to the root command. To add a command, drop a new class into
/// <c>Commands/</c>; there is no central registration file.
/// </summary>
/// <remarks>
/// Command names must be unique (kebab-case, e.g. <c>list-levels</c>); duplicates fail at startup.
/// Handlers should return a non-zero exit code on failure, log through <see cref="CliHost.LoggerFactory"/>,
/// and never print AES key material.
/// </remarks>
public interface ICommandModule
{
    /// <summary>Creates the command (with its options, arguments and handler).</summary>
    Command Build();
}
