using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;
using ScumStudio.Pak.Reading;

namespace ScumStudio.Level.Import;

/// <summary>A model brought in by <see cref="UeCook.ImportAsync"/>.</summary>
/// <param name="Token">Its asset token (<c>SS_</c> + the file name).</param>
/// <param name="Mesh">Package path of its cooked static mesh, <c>/Game/ScumStudio/Imports/&lt;Token&gt;/SM_&lt;Token&gt;</c>.</param>
/// <param name="Folder">Its cooked files in the project, relative to the project folder (<see cref="Craftable.Imported"/>).</param>
/// <param name="Min">Bounds minimum in cm (the pivot is the bottom centre: Z is 0, X and Y are minus half the size).</param>
/// <param name="Max">Bounds maximum in cm.</param>
public sealed record ImportedModel(string Token, string Mesh, string Folder, Vector3 Min, Vector3 Max)
{
    /// <summary>Largest extent in metres.</summary>
    public float SizeMeters => MathF.Max(Max.X - Min.X, MathF.Max(Max.Y - Min.Y, Max.Z - Min.Z)) / 100f;
}

/// <summary>
/// External 3D models (FBX, OBJ) as SCUM content (owner, 2026-10-09: "a list where I can add external files, for example 3D
/// tables or a new weapon"): the locally installed Unreal Engine 4.27 imports the file into a generated content-only project
/// named like the game, <c>SCUM</c>, and cooks it for WindowsNoEditor with <c>-unversioned</c> (how the owner's tree mod of
/// 2026-10-04 got stock 4.27.2 shaders to load in SCUM). The cooked mesh, materials and textures are checked here
/// (<see cref="Verify"/>) and kept in the studio project's <c>imports</c> folder; the Craftables pak carries them.
/// </summary>
public static class UeCook
{
    /// <summary>Environment variable naming the engine folder (also turns on the real-engine test).</summary>
    public const string EngineVariable = "SCUMSTUDIO_UE427";

    /// <summary>Where the Epic Games Launcher installs 4.27.</summary>
    public const string DefaultEngineDirectory = @"C:\Program Files\Epic Games\UE_4.27";

    /// <summary>Game folder of every imported model.</summary>
    public const string ImportsRoot = "/Game/ScumStudio/Imports";

    /// <summary>Folder of the cooked imports in the studio project.</summary>
    public const string ImportsFolder = "imports";

    /// <summary>File types the engine's FBX importer reads (it also reads OBJ).</summary>
    public static IReadOnlyList<string> Extensions { get; } = ["fbx", "obj"];

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    // Every engine process is put in this job, and the shader workers it starts inherit it: when the app ends, even by a
    // crash, Windows closes the job's handle and kills them all (they would keep compiling for minutes and keep the
    // generated project's files locked). Zero where there is none.
    private static readonly Lazy<IntPtr> EngineJob = new(CreateKillOnCloseJob);

    /// <summary>The generated project: <c>%LOCALAPPDATA%\ScumStudio\ue\SCUM</c>, or <c>C:\ScumStudioUE\SCUM</c> when that path has a space.</summary>
    public static string DefaultProjectDirectory
    {
        get
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScumStudio", "ue", "SCUM");
            return local.Contains(' ', StringComparison.Ordinal) ? @"C:\ScumStudioUE\SCUM" : local;
        }
    }

    /// <summary>The first 4.27 engine folder of <paramref name="directory"/>, <see cref="EngineVariable"/> and <see cref="DefaultEngineDirectory"/>; null when none.</summary>
    public static string? FindEngine(string? directory = null) =>
        new[] { directory, Environment.GetEnvironmentVariable(EngineVariable), DefaultEngineDirectory }.FirstOrDefault(d => d is { Length: > 0 } && Is427(d));

    private static bool Is427(string directory)
    {
        var version = Path.Combine(directory, "Engine", "Build", "Build.version");
        if (!File.Exists(EditorCmd(directory)) || !File.Exists(version))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(version));
            return json.RootElement.GetProperty("MajorVersion").GetInt32() == 4 && json.RootElement.GetProperty("MinorVersion").GetInt32() == 27;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static string EditorCmd(string engine) => Path.Combine(engine, "Engine", "Binaries", "Win64", "UE4Editor-Cmd.exe");

    /// <summary>
    /// Writes the content-only project <c>SCUM</c> into <paramref name="directory"/> (files whose text is unchanged are not
    /// touched, so <c>-iterate</c> cooks stay warm): the uproject with the Python and editor scripting plugins, the
    /// game's renderer settings (<paramref name="gameEngineIni"/>: SCUM's <c>DefaultEngine.ini</c>) so the shaders match,
    /// SM5 only, two shader workers, shaders inside the cooked materials, and the import script.
    /// </summary>
    /// <exception cref="ArgumentException">The path has a space (<c>-script=</c> breaks on spaces).</exception>
    /// <exception cref="InvalidDataException">The game's ini has no renderer settings.</exception>
    public static void WriteProject(string directory, string gameEngineIni)
    {
        if (directory.Contains(' ', StringComparison.Ordinal))
        {
            throw new ArgumentException($"The Unreal project path must not contain spaces: {directory}", nameof(directory));
        }

        void Write(string relative, string text)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                File.WriteAllText(path, text);
            }
        }

        Write("SCUM.uproject", """
            {
              "FileVersion": 3,
              "EngineAssociation": "4.27",
              "Description": "ScumStudio: imports 3D models and cooks them for SCUM.",
              "Plugins": [
                { "Name": "PythonScriptPlugin", "Enabled": true },
                { "Name": "EditorScriptingUtilities", "Enabled": true }
              ]
            }
            """);
        Write(Path.Combine("Config", "DefaultEngine.ini"), $"""
            [/Script/WindowsTargetPlatform.WindowsTargetSettings]
            -TargetedRHIs=PCD3D_SM5
            +TargetedRHIs=PCD3D_SM5

            [DevOptions.Shaders]
            NumUnusedShaderCompilingThreads={Math.Max(0, Environment.ProcessorCount - 2)}
            bPromptToRetryFailedShaderCompiles=False

            {RendererSection(gameEngineIni)}
            """);
        Write(Path.Combine("Config", "DefaultGame.ini"), $"""
            [/Script/UnrealEd.ProjectPackagingSettings]
            bShareMaterialShaderCode=False
            bSharedMaterialNativeLibraries=False
            bUseIoStore=False
            +DirectoriesToAlwaysCook=(Path="{ImportsRoot}")
            """);
        Write(Path.Combine("Content", "Python", "ss_import.py"), ImportScript);
    }

    /// <summary>The <c>[/Script/Engine.RendererSettings]</c> section of <paramref name="ini"/>, verbatim.</summary>
    /// <exception cref="InvalidDataException">There is none.</exception>
    public static string RendererSection(string ini)
    {
        var section = ini.Split('\n').Select(l => l.TrimEnd('\r'))
            .SkipWhile(l => l.Trim() != "[/Script/Engine.RendererSettings]")
            .TakeWhile((l, i) => i == 0 || !l.StartsWith('['))
            .ToList();
        return section.Count > 0 ? string.Join('\n', section).TrimEnd() : throw new InvalidDataException("The game's DefaultEngine.ini has no renderer settings.");
    }

    /// <summary>
    /// Imports <paramref name="sourceFile"/> (FBX or OBJ) as one static mesh with its materials and textures, cooks it, checks
    /// it and stores it in <paramref name="projectDirectory"/>/<c>imports/&lt;Token&gt;</c>. One engine job runs at a
    /// time, hidden, at below-normal priority; cancelling closes the engine, and so does the app's end. The first cook
    /// compiles shaders (minutes). <paramref name="aesKey"/> is the app's key (the key store's or the environment's when null).
    /// </summary>
    /// <exception cref="InvalidOperationException">No engine, or the engine failed (the message has its last errors).</exception>
    /// <exception cref="InvalidDataException">The cooked files failed <see cref="Verify"/>.</exception>
    public static async Task<ImportedModel> ImportAsync(
        AssetCatalog catalog, string sourceFile, string projectDirectory, IProgressSink? progress = null,
        string? engine = null, string? ueProject = null, string? aesKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        engine = FindEngine(engine) ?? throw new InvalidOperationException(
            $"Unreal Engine 4.27 was not found ({DefaultEngineDirectory}); install it from the Epic Games Launcher or set {EngineVariable} to its folder.");
        if (!File.Exists(sourceFile) || !Extensions.Contains(Path.GetExtension(sourceFile).TrimStart('.').ToLowerInvariant()))
        {
            throw new InvalidOperationException($"{sourceFile} is not an FBX or OBJ file.");
        }

        // Read from pakchunk0 itself: SCUM encrypts its ini files and the catalog drops the key once its paks are mounted.
        var pak = catalog.SourcePath is { } paks && !File.Exists(paks) ? Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak") : catalog.SourcePath;
        using var source = File.Exists(pak) ? PakFileSource.OpenFile(pak, new PakFileSourceOptions { AesKey = aesKey ?? AesKeyText.FromEnvironmentOrStore() }) : null;
        var ini = source is null ? null : await source.ReadBytesAsync(catalog.ProjectName + "/Config/DefaultEngine.ini", cancellationToken).ConfigureAwait(false);
        if (ini is null)
        {
            throw new InvalidDataException("The game's DefaultEngine.ini could not be read from pakchunk0-WindowsNoEditor.pak.");
        }

        ueProject ??= DefaultProjectDirectory;
        await OneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = NewToken(projectDirectory, sourceFile);
            WriteProject(ueProject, Encoding.UTF8.GetString(ini));
            var tail = Path.Combine("ScumStudio", "Imports", token);
            var cooked = Path.Combine(ueProject, "Saved", "Cooked", "WindowsNoEditor", "SCUM", "Content", tail);
            DeleteTree(Path.Combine(ueProject, "Content", tail)); // a token used before keeps no stale materials
            DeleteTree(cooked);
            var python = Path.Combine(ueProject, "Content", "Python");
            var resultFile = Path.Combine(python, "ss_result.json");
            File.Delete(resultFile);
            var folder = ImportsRoot + "/" + token;
            await File.WriteAllTextAsync(Path.Combine(python, "ss_job.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["source"] = Path.GetFullPath(sourceFile),
                ["folder"] = folder,
                ["name"] = "SM_" + token,
                ["max_texture_size"] = 2048,
            }), cancellationToken).ConfigureAwait(false);

            var uproject = Path.Combine(ueProject, "SCUM.uproject");
            progress?.Report($"Importing {Path.GetFileName(sourceFile)} (Unreal Engine 4.27)", 0, 0);
            await RunAsync(engine, [uproject, "-run=pythonscript", "-script=" + Path.Combine(python, "ss_import.py")], progress, cancellationToken).ConfigureAwait(false);
            using var result = File.Exists(resultFile)
                ? JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, cancellationToken).ConfigureAwait(false))
                : throw new InvalidOperationException($"Unreal Engine imported nothing (see {Path.Combine(ueProject, "Saved", "Logs")}).");
            if (result.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException("Unreal Engine could not import the model: " + error.GetString()?.Trim().Split('\n')[^1]);
            }

            Vector3 Read(string name) => result.RootElement.GetProperty(name).EnumerateArray().Select(e => (float)e.GetDouble()).ToArray() is [var x, var y, var z]
                ? new Vector3(x, y, z)
                : throw new InvalidDataException("The import result has no bounds.");

            progress?.Report("Cooking for SCUM (the first time compiles shaders: several minutes)", 0, 0);
            await RunAsync(engine, [uproject, "-run=cook", "-targetplatform=WindowsNoEditor", "-unversioned", "-iterate"], progress, cancellationToken).ConfigureAwait(false);

            progress?.Report("Checking the cooked files", 0, 0);
            var target = Path.Combine(projectDirectory, ImportsFolder, token);
            CopyTree(cooked, Path.Combine(target, "SCUM", "Content", tail));
            var problems = Verify(target, token, catalog.PackageExists);
            if (problems.Count > 0)
            {
                DeleteTree(target);
                throw new InvalidDataException(string.Join(" ", problems));
            }

            return new ImportedModel(token, folder + "/SM_" + token, ImportsFolder + "/" + token, Read("min"), Read("max"));
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>
    /// What is wrong with the cooked import in <paramref name="folder"/> (it holds <c>SCUM/Content/...</c>): every package
    /// must lie in <see cref="ImportsRoot"/>/<paramref name="token"/>, the mesh <c>SM_&lt;token&gt;</c> must be a
    /// StaticMesh, and every package a file imports must be native (<c>/Script</c>), one of these files, or in the game
    /// (<paramref name="inGame"/>). Empty when all is well.
    /// </summary>
    public static IReadOnlyList<string> Verify(string folder, string token, Func<string, bool> inGame)
    {
        ArgumentNullException.ThrowIfNull(inGame);
        var problems = new List<string>();
        var root = Path.Combine(folder, "SCUM", "Content");
        var own = ImportsRoot + "/" + token + "/";
        var mesh = Path.Combine(root, "ScumStudio", "Imports", token, "SM_" + token + ".uasset");
        if (!File.Exists(mesh))
        {
            problems.Add($"The cooked mesh SM_{token} is missing.");
        }

        foreach (var file in Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.uasset", SearchOption.AllDirectories) : [])
        {
            var stem = file[..^".uasset".Length];
            var package = "/Game/" + Path.GetRelativePath(root, stem).Replace('\\', '/');
            if (!package.StartsWith(own, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{package} lies outside {own}.");
                continue;
            }

            var cookedPackage = CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(stem + ".uexp"), null, package);
            foreach (var import in cookedPackage.Imports.Where(i => i.OuterIndex == 0))
            {
                var name = cookedPackage.ResolveName(import.ObjectName);
                var found = name.StartsWith("/Script/", StringComparison.Ordinal)
                    || (name.StartsWith(own, StringComparison.OrdinalIgnoreCase)
                        ? File.Exists(Path.Combine([root, .. name["/Game/".Length..].Split('/')]) + ".uasset")
                        : inGame(name));
                if (!found)
                {
                    problems.Add($"{package} needs {name}, which is neither imported nor in the game.");
                }
            }

            if (string.Equals(file, mesh, StringComparison.OrdinalIgnoreCase)
                && !Enumerable.Range(0, cookedPackage.Exports.Count).Any(i => cookedPackage.GetExportClassName(i) == "StaticMesh"))
            {
                problems.Add($"SM_{token} is not a static mesh.");
            }
        }

        return problems;
    }

    /// <summary>Copies every file below <paramref name="from"/> to the same place below <paramref name="to"/>.</summary>
    public static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void DeleteTree(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary><c>SS_</c> + the file name, numbered when the project already has an import of that name.</summary>
    private static string NewToken(string projectDirectory, string sourceFile)
    {
        var stem = CraftablesFile.TokenOf(Path.GetFileNameWithoutExtension(sourceFile));
        stem = stem == "SS_" ? "SS_Model" : stem;
        var token = stem;
        for (var n = 2; Directory.Exists(Path.Combine(projectDirectory, ImportsFolder, token)); n++)
        {
            token = $"{stem}_{n}";
        }

        return token;
    }

    /// <summary>Runs the editor commandlet hidden at below-normal priority; its log lines go to <paramref name="progress"/>.</summary>
    private static async Task RunAsync(string engine, IReadOnlyList<string> arguments, IProgressSink? progress, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(EditorCmd(engine))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments.Concat(["-unattended", "-nopause", "-nosplash", "-NullRHI", "-stdout", "-FullStdOutLogOutput", "-UTF8Output"]))
        {
            info.ArgumentList.Add(argument);
        }

        await RunHiddenAsync(info, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="info"/> (output redirected) in the engine job at below-normal priority, its lines to
    /// <paramref name="progress"/>; cancelling kills it with every process it started.
    /// </summary>
    /// <exception cref="InvalidOperationException">It stopped with an exit code other than 0.</exception>
    internal static async Task RunHiddenAsync(ProcessStartInfo info, IProgressSink? progress, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        void Line(object sender, DataReceivedEventArgs e)
        {
            if (e.Data?.Trim() is { Length: > 0 } line)
            {
                progress?.Report(line, 0, 0);
                if (line.Contains("Error:", StringComparison.Ordinal))
                {
                    lock (errors)
                    {
                        errors.Add(line);
                    }
                }
            }
        }

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += Line;
        process.ErrorDataReceived += Line;
        process.Start();
        if (EngineJob.Value != IntPtr.Zero)
        {
            AssignProcessToJobObject(EngineJob.Value, process.Handle);
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // it already ended
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true); // the shader workers too
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or Win32Exception)
            {
                // it already ended, or a worker could not be stopped (the job ends it with the app): still cancelled
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            // WaitForExitAsync has read the output to its end: no more lines arrive.
            throw new InvalidOperationException($"Unreal Engine stopped with code {process.ExitCode}. {string.Join(" ", errors.TakeLast(3))}");
        }
    }

    /// <summary>True when <paramref name="process"/> is in the engine job (tests).</summary>
    internal static bool InEngineJob(Process process) =>
        EngineJob.Value != IntPtr.Zero && IsProcessInJob(process.Handle, EngineJob.Value, out var inJob) && inJob;

    private static IntPtr CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        return job != IntPtr.Zero && SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ? job : IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>
    /// The import script (run by <c>-run=pythonscript</c>): reads <c>ss_job.json</c>, imports the file with the FBX importer as
    /// one combined static mesh (an OBJ turned from Y-up to Z-up), measures it and imports it again shifted so the pivot is
    /// the bottom centre (4.27 Python has no pivot call), turns on complex-as-simple collision (players can walk in and
    /// under it), caps the textures, and writes <c>ss_result.json</c> (bounds, or the error).
    /// </summary>
    private const string ImportScript = """
        # ScumStudio import script, written by UeCook.WriteProject (do not edit: it is rewritten).
        import json
        import os
        import traceback
        import unreal

        HERE = os.path.join(unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_content_dir()), 'Python')


        def import_mesh(job, offset):
            ui = unreal.FbxImportUI()
            ui.set_editor_property('import_mesh', True)
            ui.set_editor_property('import_as_skeletal', False)
            ui.set_editor_property('import_animations', False)
            ui.set_editor_property('import_materials', True)
            ui.set_editor_property('import_textures', True)
            ui.set_editor_property('automated_import_should_detect_type', False)
            ui.set_editor_property('mesh_type_to_import', unreal.FBXImportType.FBXIT_STATIC_MESH)
            data = ui.get_editor_property('static_mesh_import_data')
            data.set_editor_property('combine_meshes', True)
            data.set_editor_property('auto_generate_collision', True)
            data.set_editor_property('import_translation', offset)
            if job['source'].lower().endswith('.obj'):
                # The FBX importer reads OBJ files Z-up; they are Y-up (Blender, 3ds Max and most tools write them so).
                data.set_editor_property('import_rotation', unreal.Rotator(roll=90.0, pitch=0.0, yaw=0.0))
            task = unreal.AssetImportTask()
            task.set_editor_property('filename', job['source'])
            task.set_editor_property('destination_path', job['folder'])
            task.set_editor_property('destination_name', job['name'])
            task.set_editor_property('replace_existing', True)
            task.set_editor_property('automated', True)
            task.set_editor_property('save', True)
            task.set_editor_property('options', ui)
            unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
            return unreal.EditorAssetLibrary.load_asset(job['folder'] + '/' + job['name'])


        def run(job):
            mesh = import_mesh(job, unreal.Vector(0, 0, 0))
            if not isinstance(mesh, unreal.StaticMesh):
                raise RuntimeError('The file holds no static mesh.')
            box = mesh.get_bounding_box()
            offset = unreal.Vector(-(box.min.x + box.max.x) / 2, -(box.min.y + box.max.y) / 2, -box.min.z)
            unreal.EditorAssetLibrary.delete_asset(job['folder'] + '/' + job['name'])
            mesh = import_mesh(job, offset)
            mesh.get_editor_property('body_setup').set_editor_property('collision_trace_flag', unreal.CollisionTraceFlag.CTF_USE_COMPLEX_AS_SIMPLE)
            for path in unreal.EditorAssetLibrary.list_assets(job['folder'], True, False):
                asset = unreal.EditorAssetLibrary.load_asset(path)
                if isinstance(asset, unreal.Texture2D):
                    asset.set_editor_property('max_texture_size', job['max_texture_size'])
            # The task saves only the mesh: the materials and textures it made are saved here, or the cook loses them.
            unreal.EditorAssetLibrary.save_directory(job['folder'], False, True)
            box = mesh.get_bounding_box()
            return {'mesh': mesh.get_path_name(), 'min': [box.min.x, box.min.y, box.min.z], 'max': [box.max.x, box.max.y, box.max.z]}


        try:
            with open(os.path.join(HERE, 'ss_job.json'), encoding='utf-8') as f:
                result = run(json.load(f))
        except Exception:
            result = {'error': traceback.format_exc()}
        with open(os.path.join(HERE, 'ss_result.json'), 'w', encoding='utf-8') as f:
            json.dump(result, f)
        """;
}
