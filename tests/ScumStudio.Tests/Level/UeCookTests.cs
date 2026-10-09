using System.Globalization;
using System.Text;
using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Import;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// External 3D models as craftables (owner: "external files, for example 3D tables or a new weapon"): the generated Unreal
/// project, the check of the cooked files, and (real engine, <c>SCUMSTUDIO_UE427</c> + <c>SCUM_PAKS</c>) a table written
/// as OBJ imported, cooked and shipped in the Craftables pak.
/// </summary>
public sealed class UeCookTests
{
    [Fact]
    public void TheGeneratedProjectCooksLikeTheGame()
    {
        const string game = "[/Script/Engine.Engine]\nX=1\n\n[/Script/Engine.RendererSettings]\r\nr.VirtualTextures=True\r\nr.GBufferFormat=1\r\n\r\n[/Script/Other]\nY=2\n";
        var root = Path.Combine(Path.GetTempPath(), "scumstudio-ueproject-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "SCUM");
        try
        {
            UeCook.WriteProject(dir, game);

            // The game's renderer settings verbatim (the shaders must match the game's), nothing else of its ini; SM5 only, light shader compiling.
            var engine = File.ReadAllText(Path.Combine(dir, "Config", "DefaultEngine.ini")).ReplaceLineEndings("\n");
            Assert.Contains("[/Script/Engine.RendererSettings]\nr.VirtualTextures=True\nr.GBufferFormat=1", engine, StringComparison.Ordinal);
            Assert.DoesNotContain("X=1", engine, StringComparison.Ordinal);
            Assert.DoesNotContain("Y=2", engine, StringComparison.Ordinal);
            Assert.Contains("+TargetedRHIs=PCD3D_SM5", engine, StringComparison.Ordinal);
            Assert.Contains("NumUnusedShaderCompilingThreads=" + Math.Max(0, Environment.ProcessorCount - 2), engine, StringComparison.Ordinal);

            // Shaders inside the cooked materials (no shader library to ship), loose pak files, the imports always cooked.
            var packaging = File.ReadAllText(Path.Combine(dir, "Config", "DefaultGame.ini"));
            Assert.Contains("bShareMaterialShaderCode=False", packaging, StringComparison.Ordinal);
            Assert.Contains("bUseIoStore=False", packaging, StringComparison.Ordinal);
            Assert.Contains("+DirectoriesToAlwaysCook=(Path=\"/Game/ScumStudio/Imports\")", packaging, StringComparison.Ordinal);

            using (var uproject = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "SCUM.uproject"))))
            {
                Assert.Equal("4.27", uproject.RootElement.GetProperty("EngineAssociation").GetString());
                var plugins = uproject.RootElement.GetProperty("Plugins").EnumerateArray()
                    .Where(p => p.GetProperty("Enabled").GetBoolean()).Select(p => p.GetProperty("Name").GetString()).ToList();
                Assert.Equal(["PythonScriptPlugin", "EditorScriptingUtilities"], plugins);
            }

            // The script exists; writing again keeps unchanged files untouched (warm -iterate cooks).
            var script = Path.Combine(dir, "Content", "Python", "ss_import.py");
            Assert.Contains("CTF_USE_COMPLEX_AS_SIMPLE", File.ReadAllText(script), StringComparison.Ordinal);
            var written = File.GetLastWriteTimeUtc(script);
            File.SetLastWriteTimeUtc(script, written.AddHours(-1));
            UeCook.WriteProject(dir, game);
            Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(script));

            // -run=pythonscript -script= breaks on spaces: no space in the project path, ever.
            Assert.DoesNotContain(' ', UeCook.DefaultProjectDirectory);
            Assert.Throws<ArgumentException>(() => UeCook.WriteProject(Path.Combine(root, "with space", "SCUM"), game));
            Assert.Throws<InvalidDataException>(() => UeCook.RendererSection("[/Script/Engine.Engine]\nX=1\n"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A cooked import passes only when every package it needs is its own or the game's: a dangling import fails.</summary>
    [Fact]
    public void ADanglingImportFailsTheCheck()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        Assert.True(catalog.TryGetPackageFile("/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01", out var table));
        var uexp = catalog.Provider.Files[Path.ChangeExtension(table.Path, ".uexp")];
        var folder = Path.Combine(Path.GetTempPath(), "scumstudio-import-" + Guid.NewGuid().ToString("N"));
        void Put(string relative)
        {
            var stem = Path.Combine([folder, "SCUM", "Content", .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
            File.WriteAllBytes(stem + ".uasset", table.Read());
            File.WriteAllBytes(stem + ".uexp", uexp.Read());
        }

        try
        {
            // A cooked mesh that uses the game's materials: fine.
            Put("ScumStudio/Imports/SS_T/SM_SS_T");
            Assert.Empty(UeCook.Verify(folder, "SS_T", catalog.PackageExists));

            // The same mesh where those materials are not in the game: every one is reported.
            var dangling = UeCook.Verify(folder, "SS_T", _ => false);
            Assert.NotEmpty(dangling);
            Assert.All(dangling, p => Assert.Contains("neither imported nor in the game", p, StringComparison.Ordinal));
            Assert.Contains(dangling, p => p.Contains("needs /Game/ConZ_Files/", StringComparison.Ordinal));

            // A missing mesh and a package outside the import's own folder (a stand-in of a game asset) fail too.
            Assert.Contains(UeCook.Verify(folder, "SS_Other", catalog.PackageExists), p => p.Contains("SM_SS_Other is missing", StringComparison.Ordinal));
            Put("ConZ_Files/Models/SM_Standin");
            Assert.Contains(UeCook.Verify(folder, "SS_T", catalog.PackageExists), p => p.Contains("/Game/ConZ_Files/Models/SM_Standin lies outside", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// End to end with the real Unreal Engine 4.27: a 160 × 80 × 75 table written as OBJ (with a material) far from the
    /// origin is imported with its pivot at the bottom centre, cooked, checked, and the Craftables pak carries it.
    /// </summary>
    [Fact]
    public async Task AnObjTableIsCookedAndShippedInTheCraftablesPak()
    {
        if (Environment.GetEnvironmentVariable(UeCook.EngineVariable) is not { Length: > 0 } || UeCook.FindEngine() is null
            || Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for: needs the engine and the game
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var root = Path.Combine(Path.GetTempPath(), "scumstudio-ueimport-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var obj = Path.Combine(root, "model", "Oak Table.obj");
        WriteTable(obj);
        try
        {
            // Kept between runs so the engine's iterative cook stays warm.
            var ueProject = Path.Combine(Path.GetTempPath(), "scumstudio-ue-test", "SCUM");
            var model = await UeCook.ImportAsync(catalog, obj, project, ueProject: ueProject);
            Assert.Equal("SS_Oak_Table", model.Token);
            Assert.Equal("/Game/ScumStudio/Imports/SS_Oak_Table/SM_SS_Oak_Table", model.Mesh);
            Assert.Equal("imports/SS_Oak_Table", model.Folder);
            Assert.Equal(0, model.Min.Z, 0.5);
            Assert.Equal(0, (model.Min.X + model.Max.X) / 2, 0.5);
            Assert.Equal(0, (model.Min.Y + model.Max.Y) / 2, 0.5);
            Assert.Equal(1.6, model.SizeMeters, 0.01);
            Assert.Equal(75, model.Max.Z, 0.5); // upright: the OBJ's Y is up
            Assert.Equal(80, model.Max.Y - model.Min.Y, 0.5);

            // Right side up, not upside down: most vertices are in the legs, so their mean height is above the middle only with the top up.
            using (var own = AssetCatalog.OpenLoose(Path.Combine(project, model.Folder)))
            {
                var verts = own.LoadFirstExport<UStaticMesh>(model.Mesh)!.RenderData!.LODs[0].PositionVertexBuffer!.Verts;
                Assert.True(verts.Average(v => v.Z) > model.Max.Z / 2, $"mean height {verts.Average(v => v.Z)}");
            }

            // The cooked mesh reads as a StaticMesh, and every package it needs is its own or the game's.
            var stem = Path.Combine(project, "imports", "SS_Oak_Table", "SCUM", "Content", "ScumStudio", "Imports", "SS_Oak_Table", "SM_SS_Oak_Table");
            var mesh = CookedPackage.Parse(File.ReadAllBytes(stem + ".uasset"), File.ReadAllBytes(stem + ".uexp"), null, model.Mesh);
            Assert.Contains(Enumerable.Range(0, mesh.Exports.Count), i => mesh.GetExportClassName(i) == "StaticMesh");
            const string wood = "/Game/ScumStudio/Imports/SS_Oak_Table/Wood"; // the OBJ's material, made by the importer
            Assert.Contains(mesh.Imports, i => mesh.ResolveName(i.ObjectName) == wood);
            Assert.Empty(UeCook.Verify(Path.Combine(project, model.Folder), model.Token, catalog.PackageExists));

            // The Craftables pak carries the mesh, and the table's element shows it.
            var craftables = new CraftablesFile
            {
                ProjectDirectory = project,
                Items = [new Craftable { Name = "Oak Table", Source = obj, Mesh = model.Mesh, Imported = model.Folder, SizeMeters = model.SizeMeters }],
            };
            var result = await CraftablesExporter.ExportAsync(craftables, "MyMod", catalog, new ExportOptions { OutputDirectory = Path.Combine(root, "out") }, ProjectSourceRole.Client);
            Assert.NotNull(result);
            Assert.Empty(result!.Warnings);
            using (var pak = AssetCatalog.OpenPaks(result.PakPath!))
            {
                Assert.True(pak.PackageExists(model.Mesh));
                Assert.True(pak.PackageExists(wood));
                var element = ModdableAssets.ReadPackage(pak, "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_SS_Oak_Table");
                Assert.Contains(element.Imports, i => element.ResolveName(i.ObjectName) == model.Mesh);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A Y-up OBJ table (top 160 × 80 × 5 cm at 70 cm, four 5 cm legs) moved 5 m off the origin, with a wood-coloured material.</summary>
    private static void WriteTable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var obj = new StringBuilder("mtllib table.mtl\nusemtl Wood\n");
        var count = 0;
        void Box(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            (x0, x1, y0, y1) = (x0 + 500, x1 + 500, y0 + 100, y1 + 100);
            foreach (var (x, y, z) in new[] { (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1) })
            {
                obj.Append(CultureInfo.InvariantCulture, $"v {x} {y} {z}\n");
            }

            // Counter-clockwise seen from outside: -z, +z, -y, +y, -x, +x.
            foreach (var face in new[] { "1 4 3 2", "5 6 7 8", "1 2 6 5", "4 8 7 3", "1 5 8 4", "2 3 7 6" })
            {
                obj.Append("f ").AppendJoin(' ', face.Split(' ').Select(i => int.Parse(i, CultureInfo.InvariantCulture) + count)).Append('\n');
            }

            count += 8;
        }

        Box(-80, 70, -40, 80, 75, 40);
        foreach (var (x, z) in new[] { (-80f, -40f), (75f, -40f), (-80f, 35f), (75f, 35f) })
        {
            Box(x, 0, z, x + 5, 70, z + 5);
        }

        File.WriteAllText(path, obj.ToString());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "table.mtl"), "newmtl Wood\nKd 0.55 0.35 0.2\n");
    }

    /// <summary>
    /// Review of craft-import: an engine process runs in the kill-on-close job (it dies with the app, even after a crash),
    /// and a cancel kills it with the processes it started and reports a cancel.
    /// </summary>
    [Fact]
    public async Task AnEngineProcessDiesWithTheAppAndOnCancel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "ping", "-n", "60", "127.0.0.1" })
        {
            info.ArgumentList.Add(argument);
        }

        using var cancel = new CancellationTokenSource();
        var run = UeCook.RunHiddenAsync(info, ScumStudio.Core.Abstractions.ProgressSink.Null, cancel.Token);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (System.Diagnostics.Process.GetProcessesByName("PING").Where(p => UeCook.InEngineJob(p)).ToList() is { Count: 0 } && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        var pings = System.Diagnostics.Process.GetProcessesByName("PING").Where(p => UeCook.InEngineJob(p)).ToList();
        Assert.NotEmpty(pings); // the child of the started process is in the job too
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.All(pings, p => Assert.True(p.WaitForExit(10_000), "ping still runs"));
    }
}
