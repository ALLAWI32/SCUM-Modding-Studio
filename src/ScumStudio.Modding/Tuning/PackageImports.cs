using ScumStudio.Formats;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Modding.Tuning;

/// <summary>A package's name, import, export and preload tables while an edit adds imports.</summary>
internal sealed record PackageTables(List<string> Names, List<bool> Wide, List<ImportEntry> Imports, List<ExportEntry> Exports, List<int> Preload, int ExportIndex)
{
    /// <summary>The tables of <paramref name="package"/> for an edit of export <paramref name="exportIndex"/>.</summary>
    public static PackageTables Of(CookedPackage package, int exportIndex) => new(
        package.Names.ToList(), package.NameEntries.Select(n => n.IsWide).ToList(), package.Imports.ToList(),
        package.Exports.ToList(), package.ReadPreloadDependencies().ToList(), exportIndex);

    /// <summary>The index of <paramref name="name"/> in the name table (added when missing).</summary>
    public FNameRef Name(string name)
    {
        var index = Names.IndexOf(name);
        if (index < 0)
        {
            index = Names.Count;
            Names.Add(name);
            Wide.Add(!name.All(char.IsAscii));
        }

        return new FNameRef(index, 0);
    }

    /// <summary>
    /// The import index of <paramref name="objectPath"/> (<c>/Game/…/X.X</c>, class <paramref name="classPackage"/>.<paramref name="className"/>),
    /// added (package and object imports, plus a create-before-serialize dependency of the edited export) when the package
    /// does not import it yet.
    /// </summary>
    public int Import(string classPackage, string className, string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        var packagePath = dot > 0 ? objectPath[..dot] : objectPath;
        var objectName = dot > 0 ? objectPath[(dot + 1)..] : objectPath[(objectPath.LastIndexOf('/') + 1)..];
        bool Is(FNameRef n, string text) => string.Equals(n.Format(Names), text, StringComparison.OrdinalIgnoreCase);
        var package = Imports.FindIndex(im => im.OuterIndex == 0 && Is(im.ClassName, "Package") && Is(im.ObjectName, packagePath));
        if (package >= 0 && Imports.FindIndex(im => im.OuterIndex == -(package + 1) && Is(im.ObjectName, objectName)) is >= 0 and var existing)
        {
            return -(existing + 1);
        }

        if (package < 0)
        {
            Imports.Add(new ImportEntry(Name("/Script/CoreUObject"), Name("Package"), 0, Name(packagePath)));
            package = Imports.Count - 1;
        }

        Imports.Add(new ImportEntry(Name(classPackage), Name(className), -(package + 1), Name(objectName)));
        var index = -Imports.Count;
        AddDependency(ExportIndex, 1, index);
        return index;
    }

    /// <summary>
    /// Adds <paramref name="dependency"/> (an FPackageIndex) to group <paramref name="group"/> of export
    /// <paramref name="exportIndex"/>'s preload dependencies. The groups are SBS, CBS, SBC, CBC (0–3) in that order: the
    /// new one goes at the end of its group, and every later group of any export moves up by one.
    /// </summary>
    public void AddDependency(int exportIndex, int group, int dependency)
    {
        var export = Exports[exportIndex];
        int[] counts = [export.SerializationBeforeSerializationDependencies, export.CreateBeforeSerializationDependencies, export.SerializationBeforeCreateDependencies, export.CreateBeforeCreateDependencies];
        int at;
        if (export.FirstExportDependency < 0)
        {
            export = export with { FirstExportDependency = Preload.Count };
            at = Preload.Count;
        }
        else
        {
            at = export.FirstExportDependency + counts.Take(group + 1).Sum();
            for (var i = 0; i < Exports.Count; i++)
            {
                if (i != exportIndex && Exports[i].FirstExportDependency >= at)
                {
                    Exports[i] = Exports[i] with { FirstExportDependency = Exports[i].FirstExportDependency + 1 };
                }
            }
        }

        Preload.Insert(at, dependency);
        counts[group]++;
        Exports[exportIndex] = export with
        {
            SerializationBeforeSerializationDependencies = counts[0],
            CreateBeforeSerializationDependencies = counts[1],
            SerializationBeforeCreateDependencies = counts[2],
            CreateBeforeCreateDependencies = counts[3],
        };
    }

    /// <summary>The export's preload dependency groups (SBS, CBS, SBC, CBC).</summary>
    public int[][] DependencyGroups(int exportIndex)
    {
        var e = Exports[exportIndex];
        int[] counts = [e.SerializationBeforeSerializationDependencies, e.CreateBeforeSerializationDependencies, e.SerializationBeforeCreateDependencies, e.CreateBeforeCreateDependencies];
        var at = e.FirstExportDependency;
        return counts.Select(c =>
        {
            var group = at < 0 ? [] : Preload.GetRange(at, c).ToArray();
            at = at < 0 ? at : at + c;
            return group;
        }).ToArray();
    }
}
