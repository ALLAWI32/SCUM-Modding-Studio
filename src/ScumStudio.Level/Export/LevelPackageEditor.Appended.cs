using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// Lookups through the export table as the request grows it. Copies, added and imported actors are appended before any
/// patch runs, so a patch finds a copy's components (named as its source's) as it finds a stored actor's, and reads every
/// payload as it is now, against the grown name table (igor8802's duplicated sentry: its patrol route was never written).
/// </summary>
public static partial class LevelPackageEditor
{
    /// <summary>
    /// The export index of <paramref name="component"/> of <paramref name="actor"/> (null = the root the actor stores), the
    /// actor stored or created by this request, or -1 with a warning.
    /// </summary>
    private static int FindComponent(
        CookedPackage package, int levelIndex, List<ExportEntry> exports, IList<ReadOnlyMemory<byte>> data, List<string> names,
        string actor, string? component, string what, List<string> warnings)
    {
        var actorIndex = FindLevelActor(exports, names, levelIndex + 1, actor) - 1;
        if (actorIndex < 0)
        {
            warnings.Add($"Actor '{actor}' was not found in the level; its {what} was not written.");
            return -1;
        }

        if (component is null)
        {
            if (RootOf(package, exports, data, names, actorIndex) is var root && root >= 0)
            {
                return root;
            }

            warnings.Add($"Actor '{actor}' stores no RootComponent; give the component name explicitly.");
            return -1;
        }

        var componentIndex = FindChild(exports, names, actorIndex + 1, component) - 1;
        if (componentIndex < 0)
        {
            warnings.Add($"Component '{actor}.{component}' is not stored in the level package; its {what} was not written.");
        }

        return componentIndex;
    }

    /// <summary>The export index of the component the actor's <c>RootComponent</c> points at, or -1.</summary>
    private static int RootOf(CookedPackage package, List<ExportEntry> exports, IList<ReadOnlyMemory<byte>> data, List<string> names, int actorIndex)
    {
        try
        {
            return PropertyReader.ReadPayload(package, data[actorIndex].ToArray(), names).Find(RootComponentProperty)?.Value is ObjectValue { Index: > 0 } root
                && root.Index <= exports.Count
                    ? root.Index - 1
                    : -1;
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentOutOfRangeException or NotSupportedException or InvalidDataException or IndexOutOfRangeException)
        {
            return -1;
        }
    }

    /// <summary>The short class name of an export (stored or appended), or <c>None</c>.</summary>
    private static string ClassNameOf(List<ExportEntry> exports, List<ImportEntry> imports, List<string> names, int exportIndex) =>
        exports[exportIndex].ClassIndex switch
        {
            < 0 and var c when -c - 1 < imports.Count => imports[-c - 1].ObjectName.Format(names),
            > 0 and var c when c - 1 < exports.Count => exports[c - 1].ObjectName.Format(names),
            _ => "None",
        };
}
