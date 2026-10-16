using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Readers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The strict read-back of an actor copied by the exporter (D15, D04): what the game's loader needs from every member of
/// the copy, checked against the same member of the source.
/// <list type="bullet">
/// <item>CUE4Parse reads each copied export and consumes exactly its <c>SerialSize</c> (no overrun, nothing left); the
/// native data of a game class CUE4Parse does not know (it stops after the tags) must be the source's, byte for byte.</item>
/// <item>What CUE4Parse decodes equals the source's: every object reference names the same import, or the copy's own member,
/// every name the same text (Niagara's user parameters included); a copy differs only in its place (the root's relative
/// transform), a parent outside the copy (cleared) and its keys (<c>LightGuid</c>, <c>MapBuildDataId</c>), which must be
/// new.</item>
/// <item>The studio's reader decodes every value (no raw bytes) and finds the same native data length after the tags.</item>
/// <item>Every event-driven-loader dependency is the copy of the source's, the actor's level included (its outer).</item>
/// <item>The package follows the FName rules (<see cref="PackageLint"/>) and every import of the copy exists in the game files.</item>
/// </list>
/// </summary>
internal static class CopyReadBack
{
    /// <summary>Keys a copy changes on purpose (its place) or must change (its build data keys).</summary>
    private static readonly HashSet<string> PlaceKeys = new(StringComparer.Ordinal) { "RelativeLocation", "RelativeRotation", "RelativeScale3D" };

    private static readonly HashSet<string> KeyGuids = new(StringComparer.Ordinal) { "LightGuid", "MapBuildDataId" };

    /// <summary>What is wrong with the copy <paramref name="copyActor"/> of <paramref name="sourceActor"/> (empty when nothing is).</summary>
    /// <param name="catalog">The game files (imports are looked up there).</param>
    /// <param name="source">The pristine source level.</param>
    /// <param name="sourceActor">The source actor's name.</param>
    /// <param name="target">The written level's package path.</param>
    /// <param name="uasset">The written level's header.</param>
    /// <param name="uexp">The written level's export data.</param>
    /// <param name="copyActor">The copy's name.</param>
    /// <param name="notes">Receives what is fine but worth saying (native data CUE4Parse does not read, found the same as the source's).</param>
    public static List<string> Check(AssetCatalog catalog, CookedPackage source, string sourceActor, string target, byte[] uasset, byte[] uexp, string copyActor, List<string>? notes = null)
    {
        var problems = new List<string>();
        var written = CookedPackage.Parse(uasset, uexp, null, target);
        problems.AddRange(PackageLint.Check(written));

        var sourceIndex = LevelActor(source, sourceActor);
        var copyIndex = LevelActor(written, copyActor);
        if (sourceIndex < 0 || copyIndex < 0)
        {
            problems.Add(sourceIndex < 0 ? $"{sourceActor} is not in {source.BasePath}" : $"{copyActor} was not written");
            return problems;
        }

        var sourceMembers = Members(source, sourceIndex).ToDictionary(m => RelativePath(source, m, sourceIndex));
        var copyMembers = Members(written, copyIndex).ToDictionary(m => RelativePath(written, m, copyIndex));
        problems.AddRange(sourceMembers.Keys.Except(copyMembers.Keys).Select(k => $"{k}: missing in the copy"));
        problems.AddRange(copyMembers.Keys.Except(sourceMembers.Keys).Select(k => $"{k}: not in the source"));

        var sourceSet = sourceMembers.Values.ToHashSet();
        var sourceCue = new Package(source.BasePath + ".umap", source.UAsset, source.UExp, null, null, catalog.Provider, false);
        var copyCue = new Package(target + ".umap", uasset, uexp, null, null, catalog.Provider, false);
        var imports = new SortedSet<int>();
        foreach (var (path, c) in copyMembers)
        {
            if (!sourceMembers.TryGetValue(path, out var s))
            {
                continue;
            }

            var label = $"{path} ({written.GetExportClassName(c)})";
            var se = source.Exports[s];
            var ce = written.Exports[c];
            foreach (var (what, si, ci) in new[] { ("class", se.ClassIndex, ce.ClassIndex), ("template", se.TemplateIndex, ce.TemplateIndex), ("super", se.SuperIndex, ce.SuperIndex) })
            {
                if (source.GetFullPath(si) != written.GetFullPath(ci))
                {
                    problems.Add($"{label}: {what} {written.GetFullPath(ci)}, the source's is {source.GetFullPath(si)}");
                }

                imports.Add(ci);
            }

            if (se.ObjectFlags != ce.ObjectFlags || se.NotForClient != ce.NotForClient || se.NotForServer != ce.NotForServer)
            {
                problems.Add($"{label}: object flags {ce.ObjectFlags:X} (not for client {ce.NotForClient}, not for server {ce.NotForServer}), the source's {se.ObjectFlags:X}");
            }

            // The studio's reader: every value decoded, the same native data length after the tags.
            var sb = source.ReadProperties(s);
            var cb = written.ReadProperties(c);
            if (Values(cb.Properties).OfType<RawValue>().Any())
            {
                problems.Add($"{label}: the studio's reader cannot decode every value");
            }

            if (se.SerialSize - sb.EndOffset != ce.SerialSize - cb.EndOffset)
            {
                problems.Add($"{label}: {ce.SerialSize - cb.EndOffset} bytes of native data, the source has {se.SerialSize - sb.EndOffset}");
            }

            foreach (var o in Values(cb.Properties).OfType<ObjectValue>().Where(o => o.Index < 0))
            {
                imports.Add(o.Index);
            }

            // The loader's dependencies: the source's, mapped (an export outside the copy is dropped, the actor's level kept).
            var sameLevel = string.Equals(source.BasePath, target, StringComparison.OrdinalIgnoreCase); // a duplicate keeps what it points at in its level
            // Another actor of the level (a stored child actor) travels with the copy under its own name; anything else outside is dropped.
            var expected = Dependencies(source, s, i => sourceSet.Contains(i - 1) ? "member " + RelativePath(source, i - 1, sourceIndex) : i == se.OuterIndex ? "outer"
                : sameLevel ? "other " + source.GetFullPath(i) : LevelActor(source, source.GetObjectName(i)!) == i - 1 ? "actor " + source.GetObjectName(i) : null);
            var actual = Dependencies(written, c, i => copyMembers.ContainsValue(i - 1) ? "member " + RelativePath(written, i - 1, copyIndex) : i == ce.OuterIndex ? "outer"
                : !sameLevel && LevelActor(written, written.GetObjectName(i)!) == i - 1 ? "actor " + written.GetObjectName(i) : "other " + written.GetFullPath(i));
            for (var g = 0; g < 4; g++)
            {
                if (!expected[g].SequenceEqual(actual[g]))
                {
                    problems.Add($"{label}: dependency group {g} is [{string.Join(", ", actual[g])}], expected [{string.Join(", ", expected[g])}]");
                }
            }

            imports.UnionWith(DependencyIndices(written, c).Where(i => i < 0));

            // CUE4Parse: exactly SerialSize, and the same decoded values.
            var (sourceJson, sourceLeft, sourceError) = Read(catalog, sourceCue, source.UExp, s);
            var (copyJson, copyLeft, copyError) = Read(catalog, copyCue, uexp, c);
            if (copyError is null && sourceError is null && copyLeft > 0 && copyLeft == sourceLeft
                && written.GetExportBytes(c).AsSpan((int)(ce.SerialSize - copyLeft)).SequenceEqual(source.GetExportBytes(s).AsSpan((int)(se.SerialSize - sourceLeft))))
            {
                // A class CUE4Parse does not know (a game class with native data): its native bytes are the source's, as they are.
                notes?.Add($"{label}: CUE4Parse does not read the last {copyLeft} bytes (native data of a class it does not know); they are the source's, byte for byte");
            }
            else if (copyError is not null || copyLeft != 0)
            {
                problems.Add($"{label}: CUE4Parse {(copyError ?? $"leaves {copyLeft} of {ce.SerialSize} bytes")} (the source: {sourceError ?? $"{sourceLeft} left"})");
                continue;
            }
            else if (sourceError is not null || sourceLeft != 0)
            {
                problems.Add($"{label}: CUE4Parse cannot read the source member ({sourceError ?? $"{sourceLeft} bytes left"})");
                continue;
            }

            var sourceRoot = Normalize(sourceJson, sourceActor, source.BasePath!);
            var copyRoot = Normalize(copyJson, copyActor, target);
            foreach (var key in KeyGuids)
            {
                // A key of its own where the source has one; none (zero) where the source has none.
                var before = Guids(sourceRoot, key);
                var after = Guids(copyRoot, key);
                if (before.Count != after.Count || before.Zip(after).Any(p => IsZero(p.First) ? !IsZero(p.Second) : IsZero(p.Second) || before.Contains(p.Second)))
                {
                    problems.Add($"{label}: {key} [{string.Join(", ", after)}] is not a key of its own (the source's: [{string.Join(", ", before)}])");
                }
            }

            var isRoot = sb.Find("AttachParent")?.Value is not ObjectValue { Index: > 0 } parent || !sourceSet.Contains(parent.Index - 1);
            Compare(label, sourceRoot, copyRoot, isRoot, problems);
        }

        problems.AddRange(UnresolvedImports(catalog, written, imports));

        // Nothing of the copy reaches back into the level it came from (its objects, its baked lighting package).
        if (!string.Equals(source.BasePath, target, StringComparison.OrdinalIgnoreCase))
        {
            problems.AddRange(imports.Where(i => i < 0 && -i - 1 < written.Imports.Count)
                .Select(i => written.GetFullPath(i))
                .Where(path => path.StartsWith(source.BasePath + ".", StringComparison.OrdinalIgnoreCase) || path.StartsWith(source.BasePath + "_BuiltData", StringComparison.OrdinalIgnoreCase))
                .Select(path => $"import {path} points into the source level"));
        }

        return problems;
    }

    /// <summary>Reads export <paramref name="index"/> with CUE4Parse into a fresh object: its JSON, the bytes it left (negative: overrun), or the error.</summary>
    private static (string Json, long Left, string? Error) Read(AssetCatalog catalog, Package package, byte[] uexp, int index)
    {
        var export = package.ExportMap[index];
        try
        {
            var loaded = package.ExportsLazy[index].Value; // created through the package's loader (its outer, class and template)
            var fresh = (UObject)Activator.CreateInstance(loaded.GetType())!;
            fresh.Name = loaded.Name;
            fresh.Outer = loaded.Outer;
            fresh.Class = loaded.Class;
            fresh.Super = loaded.Super;
            fresh.Template = loaded.Template;
            fresh.Flags = loaded.Flags;
            var ar = new FAssetArchive(new FByteArchive(package.Name, uexp, catalog.Provider.Versions), package, (int)package.Summary.TotalHeaderSize, null!);
            ar.SeekAbsolute(export.SerialOffset, SeekOrigin.Begin);
            var start = ar.Position;
            fresh.Deserialize(ar, start + export.SerialSize);
            return (JsonConvert.SerializeObject(fresh), start + export.SerialSize - ar.Position, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (string.Empty, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The JSON with the actor's and the level's names made neutral and an export's index left out of its path.</summary>
    private static JToken Normalize(string json, string actor, string level)
    {
        var levelName = level[(level.LastIndexOf('/') + 1)..];
        json = json.Replace(level, "<LEVEL>", StringComparison.Ordinal);
        json = Regex.Replace(json, $"(?<![A-Za-z0-9_]){Regex.Escape(actor)}(?![A-Za-z0-9_])", "<ACTOR>"); // the actor's own name, not a class it starts
        json = Regex.Replace(json, $"(?<![A-Za-z0-9_]){Regex.Escape(levelName)}(?![A-Za-z0-9_])", "<LEVEL>");
        return JToken.Parse(Regex.Replace(json, "\"ObjectPath\":\"[^\"]*?\\.\\d+\"", "\"ObjectPath\":\"<EXPORT>\""));
    }

    /// <summary>
    /// Compares two decoded members: equal except a root's place, its keys, and a parent outside the copy (the copy's is
    /// null where the source's points at another actor).
    /// </summary>
    private static void Compare(string label, JToken source, JToken copy, bool isRoot, List<string> problems)
    {
        Strip(source, isRoot);
        Strip(copy, isRoot);
        foreach (var name in new[] { "AttachParent", "ParentComponent" })
        {
            if (copy["Properties"]?[name] is { Type: JTokenType.Null } && source["Properties"]?[name] is JObject other && !other.ToString().Contains("<ACTOR>", StringComparison.Ordinal))
            {
                ((JObject)source["Properties"]!).Remove(name);
                ((JObject)copy["Properties"]!).Remove(name);
            }
        }

        if (!JToken.DeepEquals(source, copy))
        {
            problems.Add($"{label}: CUE4Parse reads {FirstDifference(source, copy)}");
        }
    }

    private static void Strip(JToken token, bool isRoot)
    {
        if (token is JObject o)
        {
            foreach (var p in o.Properties().ToList())
            {
                if (KeyGuids.Contains(p.Name) || (isRoot && PlaceKeys.Contains(p.Name)))
                {
                    p.Remove();
                }
                else
                {
                    Strip(p.Value, isRoot);
                }
            }
        }
        else if (token is JArray a)
        {
            foreach (var item in a)
            {
                Strip(item, isRoot);
            }
        }
    }

    private static List<string> Guids(JToken token, string key) =>
        token.SelectTokens("$.." + key).Select(t => t.ToString(Formatting.None)).ToList();

    private static bool IsZero(string guid) => guid.Trim('"', '0', '-').Length == 0;

    private static string FirstDifference(JToken source, JToken copy, string path = "$")
    {
        if (source is JObject so && copy is JObject co)
        {
            foreach (var name in so.Properties().Select(p => p.Name).Union(co.Properties().Select(p => p.Name)))
            {
                if (!JToken.DeepEquals(so[name], co[name]))
                {
                    return so[name] is null || co[name] is null
                        ? $"{path}.{name} = {Short(co[name])}, the source has {Short(so[name])}"
                        : FirstDifference(so[name]!, co[name]!, $"{path}.{name}");
                }
            }
        }
        else if (source is JArray sa && copy is JArray ca && sa.Count == ca.Count)
        {
            for (var i = 0; i < sa.Count; i++)
            {
                if (!JToken.DeepEquals(sa[i], ca[i]))
                {
                    return FirstDifference(sa[i], ca[i], $"{path}[{i}]");
                }
            }
        }

        return $"{path} = {Short(copy)}, the source has {Short(source)}";
    }

    private static string Short(JToken? token)
    {
        var text = token?.ToString(Formatting.None) ?? "(nothing)";
        return text.Length > 240 ? text[..240] + "…" : text;
    }

    /// <summary>The four dependency groups of an export, each entry described by <paramref name="describe"/> (null: dropped) or as an import path.</summary>
    private static List<string>[] Dependencies(CookedPackage package, int export, Func<int, string?> describe)
    {
        var groups = new[] { new List<string>(), new List<string>(), new List<string>(), new List<string>() };
        var e = package.Exports[export];
        if (e.FirstExportDependency < 0)
        {
            return groups;
        }

        var preload = package.ReadPreloadDependencies();
        int[] sizes = [e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies, e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies];
        var pos = e.FirstExportDependency;
        for (var g = 0; g < 4; g++)
        {
            for (var k = 0; k < sizes[g]; k++, pos++)
            {
                var value = pos < preload.Length ? preload[pos] : 0;
                var text = value < 0 ? "import " + package.GetFullPath(value) : value > 0 ? describe(value) : "null";
                if (text is not null)
                {
                    groups[g].Add(text);
                }
            }
        }

        return groups;
    }

    private static IEnumerable<int> DependencyIndices(CookedPackage package, int export)
    {
        var e = package.Exports[export];
        if (e.FirstExportDependency < 0)
        {
            return [];
        }

        var count = e.SerializationBeforeSerializationDependencies + e.CreateBeforeSerializationDependencies + e.SerializationBeforeCreateDependencies + e.CreateBeforeCreateDependencies;
        return package.ReadPreloadDependencies().Skip(e.FirstExportDependency).Take(count);
    }

    /// <summary>Imports of the copy that name nothing in the game files (a script class is taken as it is).</summary>
    private static IEnumerable<string> UnresolvedImports(AssetCatalog catalog, CookedPackage written, IEnumerable<int> indices)
    {
        var loaded = new Dictionary<string, IPackage?>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in indices.Where(i => i < 0).Distinct())
        {
            if (-index - 1 >= written.Imports.Count)
            {
                yield return $"import {index} is out of range ({written.Imports.Count} imports)";
                continue;
            }

            var chain = new List<int>();
            for (var i = index; i < 0 && chain.Count < 16; i = written.Imports[-i - 1].OuterIndex)
            {
                chain.Add(i);
            }

            var packageName = written.ResolveName(written.Imports[-chain[^1] - 1].ObjectName);
            if (packageName.StartsWith("/Script/", StringComparison.Ordinal) || chain.Count == 1)
            {
                continue;
            }

            if (!loaded.TryGetValue(packageName, out var package))
            {
                package = catalog.TryLoadPackage(packageName, out var p) ? p : null;
                loaded[packageName] = package;
            }

            var leaf = written.ResolveName(written.Imports[-index - 1].ObjectName);
            if (package is null || package.GetExportIndex(leaf, StringComparison.OrdinalIgnoreCase) < 0)
            {
                yield return $"import {written.GetFullPath(index)} is not in the game files";
            }
        }
    }

    private static IEnumerable<PropertyValue> Values(IEnumerable<PropertyTag> tags) => tags.SelectMany(t => Values(t.Value));

    private static IEnumerable<PropertyValue> Values(PropertyValue value)
    {
        yield return value;
        IEnumerable<PropertyValue> inner = value switch
        {
            StructValue s => Values(s.Properties),
            ArrayValue a => a.Items.SelectMany(Values),
            SetValue set => set.Items.SelectMany(Values),
            MapValue map => map.Entries.SelectMany(e => Values(e.Key).Concat(Values(e.Value))),
            NativeStructValue n => n.Fields.SelectMany(f => Values(f.Value)),
            PerPlatformValue p => Values(p.Default),
            _ => [],
        };
        foreach (var v in inner)
        {
            yield return v;
        }
    }

    /// <summary>The export named <paramref name="name"/> directly under the level, or -1.</summary>
    private static int LevelActor(CookedPackage package, string name)
    {
        for (var i = package.Exports.Count - 1; i >= 0; i--)
        {
            var outer = package.Exports[i].OuterIndex;
            if (outer > 0 && package.ResolveName(package.Exports[i].ObjectName) == name && package.GetExportClassName(outer - 1) == "Level")
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The export and every export whose outer chain leads to it.</summary>
    internal static List<int> Members(CookedPackage package, int actor)
    {
        var members = new List<int> { actor };
        var set = new HashSet<int> { actor };
        for (var grew = true; grew;)
        {
            grew = false;
            for (var i = 0; i < package.Exports.Count; i++)
            {
                if (!set.Contains(i) && package.Exports[i].OuterIndex > 0 && set.Contains(package.Exports[i].OuterIndex - 1))
                {
                    members.Add(i);
                    set.Add(i);
                    grew = true;
                }
            }
        }

        return members;
    }

    private static string RelativePath(CookedPackage package, int index, int actor)
    {
        var parts = new List<string>();
        for (var i = index; i != actor && i >= 0; i = package.Exports[i].OuterIndex - 1)
        {
            parts.Add(package.ResolveName(package.Exports[i].ObjectName));
        }

        parts.Reverse();
        return parts.Count == 0 ? "(actor)" : string.Join('.', parts);
    }
}
