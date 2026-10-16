using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Import;

namespace ScumStudio.Level.Export;

public sealed partial class ProjectExporter
{
    /// <summary>
    /// The imported 3D models (<see cref="ModelLibrary"/>, Pheidian: "import custom models into the editor") one export
    /// draws on the map. A placement or mesh swap whose model folder is gone is left out of its level with a warning (the
    /// game would draw nothing there); every model a written level draws ships in the pak at its own
    /// <c>/Game/ScumStudio/Imports/&lt;token&gt;/</c> folder, for the client and the server alike.
    /// </summary>
    internal sealed class ModelCarry(string? projectDirectory)
    {
        private readonly Dictionary<string, (string? Folder, string Problem)> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The folder of the model <paramref name="meshPath"/> draws (read once), or null with the reason.</summary>
        public string? FolderOf(string meshPath, out string problem)
        {
            var token = ModelLibrary.TokenOf(meshPath) ?? string.Empty;
            if (!_folders.TryGetValue(token, out var known))
            {
                var folder = ModelLibrary.FolderOf(projectDirectory, meshPath, out var reason);
                _folders[token] = known = (folder, reason);
            }

            problem = known.Problem;
            return known.Folder;
        }

        /// <summary>
        /// The meshes of the models <paramref name="meshPaths"/> names, read from their own folders for the export's bounds,
        /// collision and grass checks (the game's catalog has none of them); each catalog is closed once its mesh is read.
        /// </summary>
        public Dictionary<string, BendMesh?> Describe(IEnumerable<string> meshPaths)
        {
            var described = new Dictionary<string, BendMesh?>(StringComparer.OrdinalIgnoreCase);
            foreach (var mesh in meshPaths.Where(ModelLibrary.IsModelMesh))
            {
                if (FolderOf(mesh, out _) is not { } folder)
                {
                    continue;
                }

                try
                {
                    using var own = AssetCatalog.OpenLoose(folder);
                    var read = new BendSupport(own).Describe(mesh);
                    described[mesh] = read;
                    described[AssetPaths.SplitObjectPath(mesh).PackagePath] = read;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
                {
                    described[mesh] = null; // read as a point (its place), as an unreadable game mesh is
                }
            }

            return described;
        }

        /// <summary>
        /// <paramref name="request"/> without the new actors and mesh swaps of <paramref name="level"/> that draw a model
        /// whose folder is not usable (each one a warning); the models of the rest are remembered for <see cref="CopyInto"/>.
        /// </summary>
        public LevelEditRequest Keep(string level, LevelEditRequest request, List<string> warnings)
        {
            var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Usable(string what, string mesh)
            {
                if (!ModelLibrary.IsModelMesh(mesh))
                {
                    return true;
                }

                if (FolderOf(mesh, out var problem) is null)
                {
                    warnings.Add($"{level}: {what} draws the imported 3D model {ModelLibrary.TokenOf(mesh)}, which {problem} (removed from the project's imports folder?); it was left out.");
                    return false;
                }

                _written.Add(ModelLibrary.TokenOf(mesh)!);
                return true;
            }

            var adds = new List<StaticMeshActorAdd>();
            foreach (var add in request.StaticMeshAdds)
            {
                if (Usable(add.NewName, add.StaticMesh))
                {
                    adds.Add(add);
                }
                else
                {
                    dropped.Add(add.NewName);
                }
            }

            var meshes = request.Meshes.Where(m => Usable(m.Component is null ? m.Actor : m.Actor + "." + m.Component, m.StaticMesh)).ToList();
            if (adds.Count == request.StaticMeshAdds.Count && meshes.Count == request.Meshes.Count)
            {
                return request;
            }

            return request with
            {
                StaticMeshAdds = adds,
                Meshes = meshes,
                Loot = request.Loot.Where(l => !dropped.Contains(l.Actor)).ToList(),
                Transforms = request.Transforms.Where(t => !dropped.Contains(t.Actor)).ToList(),
            };
        }

        /// <summary>
        /// Copies the files of every model a written level draws into <paramref name="staging"/> (<c>SCUM/Content/...</c>, as
        /// the Craftables pak carries them) and returns their packages.
        /// </summary>
        public IReadOnlyList<ExportedAsset> CopyInto(string staging, string projectName)
        {
            var assets = new List<ExportedAsset>();
            foreach (var token in _written.Order(StringComparer.OrdinalIgnoreCase))
            {
                var folder = _folders[token].Folder!;
                UeCook.CopyTree(folder, staging);
                assets.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(AssetPaths.IsPackageFile)
                    .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
                    .Select(virtualPath => new ExportedAsset(AssetPaths.ToPackagePath(virtualPath, projectName), virtualPath, IsClone: false)));
            }

            return assets;
        }
    }
}
