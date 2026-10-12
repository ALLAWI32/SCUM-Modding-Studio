using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak.Writing;
using ScumStudio.Tests.Formats;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The crafting templates the planner clones, as small synthetic packages (no game files needed): the chair recipe with its
/// Caption, the chair element showing a stock mesh, the workbench family without any mesh import, a table mesh and a tag.
/// </summary>
internal static class CraftFixture
{
    public const string StockMesh = "/Game/ConZ_Files/Models/Chair/SM_Chair_Improvised_Wood";
    public const string Table = "/Game/ConZ_Files/Models/Table/SM_Table_01";
    public const string Placeables = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/";
    public const string Elements = "/Game/ConZ_Files/BaseBuilding/BaseElements/";

    /// <summary>The stock packages, by package path.</summary>
    public static Dictionary<string, PackageBytes> Stock() => new(StringComparer.Ordinal)
    {
        [CraftablesPlanner.FurnitureRecipe] = Package("CR_Chair_Improvised_Wood", null, "Caption", "Description"),
        [Elements + "BP_Chair_Improvised_Wood"] = Package("Default__BP_Chair_Improvised_Wood_C", StockMesh, "_name"),
        [CraftablesPlanner.StationRecipe] = Package("CR_Improvised_Workbench", null, "Caption"),
        [Elements + "BP_Base_ImprovisedWorkbench"] = Package("Default__BP_Base_ImprovisedWorkbench_C", null, "_name"),
        ["/Game/ConZ_Files/Items/Equipment/Active_Items/Improvised_Workbench"] = Package("Improvised_Workbench_C"),
        ["/Game/ConZ_Files/Items/Equipment/Active_Items/Improvised_Workbench_ES"] = Package("Default__Improvised_Workbench_ES_C", null, "Caption"),
        [RecipeIngredients.TagFolder + "/CI_Group_Machinery"] = Package("CI_Group_Machinery", null, "ClassRepresentativeCaption"),
        [RecipeIngredients.TagFolder + "/CI_Plank"] = Package("CI_Plank"),
        [StockMesh] = Package("SM_Chair_Improvised_Wood"),
        [Table] = Package("SM_Table_01"),
    };

    /// <summary>A furniture craftable showing the table, made of planks.</summary>
    public static Craftable Chair(string name, string? token = null, string mesh = Table) =>
        new() { Name = name, Token = token, Source = mesh, Mesh = mesh, Ingredients = [new("CI_Plank", 3)] };

    /// <summary>Writes <paramref name="packages"/> below <paramref name="root"/> (a loose game folder).</summary>
    public static string WriteLoose(string root, IReadOnlyDictionary<string, PackageBytes> packages)
    {
        foreach (var (path, bytes) in packages)
        {
            var file = Path.Combine([root, .. AssetPaths.ToFilePathWithoutExtension(path).Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file + ".uasset", bytes.UAsset);
            File.WriteAllBytes(file + ".uexp", bytes.UExp);
        }

        return root;
    }

    /// <summary>Packs <paramref name="packages"/> into <paramref name="paks"/>/<paramref name="pakName"/>.</summary>
    public static async Task WritePakAsync(string paks, string pakName, IReadOnlyDictionary<string, PackageBytes> packages)
    {
        using var staging = new TempDirectory("scumstudio-craftpak-");
        WriteLoose(staging.Path, packages);
        Directory.CreateDirectory(paks);
        await new PakWriter().WriteFromDirectoryAsync(staging.Path, Path.Combine(paks, pakName));
    }

    /// <summary>
    /// A cooked package with one export <paramref name="exportName"/> whose string properties are <paramref name="properties"/>,
    /// importing the static mesh <paramref name="mesh"/> when given.
    /// </summary>
    public static PackageBytes Package(string exportName, string? mesh = null, params string[] properties)
    {
        var names = new List<string> { "/Script/CoreUObject", "/Script/Engine", "Package", "Class", "Object", "Default__Object", "StaticMesh", "None", "StrProperty", exportName };
        if (mesh is not null)
        {
            names.AddRange([mesh, mesh[(mesh.LastIndexOf('/') + 1)..]]);
        }

        names.AddRange(properties);
        names = names.Distinct(StringComparer.Ordinal).ToList();
        FNameRef N(string name) => new(names.IndexOf(name));

        var imports = new List<ImportEntry>
        {
            new(N("/Script/CoreUObject"), N("Package"), 0, N("/Script/CoreUObject")),
            new(N("/Script/CoreUObject"), N("Class"), -1, N("Object")),
            new(N("/Script/CoreUObject"), N("Object"), -1, N("Default__Object")),
        };
        if (mesh is not null)
        {
            imports.Add(new(N("/Script/CoreUObject"), N("Package"), 0, N(mesh)));
            imports.Add(new(N("/Script/Engine"), N("StaticMesh"), -4, N(mesh[(mesh.LastIndexOf('/') + 1)..])));
        }

        var payload = new ByteWriter();
        foreach (var property in properties)
        {
            var value = new ByteWriter();
            value.FString("Stock " + property);
            payload.FName(N(property));
            payload.FName(N("StrProperty"));
            payload.I32(value.Length);
            payload.I32(0);
            payload.U8(0);
            payload.Raw(value.WrittenSpan);
        }

        payload.FName(N("None"));
        payload.I32(0);
        return PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = names,
            Imports = imports,
            Exports =
            [
                new()
                {
                    ClassIndex = -2,
                    TemplateIndex = -3,
                    ObjectName = N(exportName),
                    ObjectFlags = 0x8,
                    IsAsset = 1,
                    FirstExportDependency = 0,
                    SerializationBeforeCreateDependencies = 2,
                },
            ],
            ExportData = [payload.ToArray()],
            PreloadDependencies = [-2, -3],
        });
    }
}
