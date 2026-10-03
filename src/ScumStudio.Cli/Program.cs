using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Reflection;
using ScumStudio.Core;

namespace ScumStudio.Cli;

/// <summary>Entry point of the <c>scumstudio</c> command line tool.</summary>
public static class Program
{
    /// <summary>Builds the root command from all discovered <see cref="ICommandModule"/>s and runs it.</summary>
    public static async Task<int> Main(string[] args)
    {
        var root = BuildRootCommand(typeof(Program).Assembly);
        var parser = new CommandLineBuilder(root)
            .UseDefaults()
            .Build();
        return await parser.InvokeAsync(args).ConfigureAwait(false);
    }

    /// <summary>Creates the root command containing every command module found in <paramref name="assembly"/>.</summary>
    public static RootCommand BuildRootCommand(Assembly assembly)
    {
        var root = new RootCommand($"{CoreInfo.ProductName} - SCUM (UE {CoreInfo.TargetEngineVersion}) modding studio command line.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in DiscoverModules(assembly))
        {
            var command = module.Build();
            if (!names.Add(command.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate CLI command name '{command.Name}' (from {module.GetType().FullName}).");
            }

            root.AddCommand(command);
        }

        return root;
    }

    /// <summary>
    /// Finds and instantiates all concrete <see cref="ICommandModule"/> types with a parameterless
    /// constructor (public or not), ordered by type name for a stable help listing.
    /// </summary>
    public static IReadOnlyList<ICommandModule> DiscoverModules(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                        && typeof(ICommandModule).IsAssignableFrom(t)
                        && t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t => (ICommandModule)Activator.CreateInstance(t, nonPublic: true)!)
            .ToList();
    }
}
