using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;

namespace ScumStudio.Level.Import;

/// <summary>One 3D model of a project's library (<see cref="ModelLibrary"/>).</summary>
/// <param name="Token">Its asset token, <c>SS_</c> + the file name (<see cref="UeCook.NewToken"/>).</param>
/// <param name="Folder">Its cooked files, relative to the project folder: <c>imports/&lt;Token&gt;</c> (<see cref="ImportedModel.Folder"/>).</param>
public sealed record LibraryModel(string Token, string Folder)
{
    /// <summary>Package path of its cooked static mesh, <c>/Game/ScumStudio/Imports/&lt;Token&gt;/SM_&lt;Token&gt;</c> (<see cref="ImportedModel.Mesh"/>).</summary>
    public string MeshPackage => $"{UeCook.ImportsRoot}/{Token}/SM_{Token}";

    /// <summary>Object path of the mesh, what a placed actor draws (<see cref="AddStaticMeshActorOp.StaticMesh"/>).</summary>
    public string MeshObjectPath => $"{MeshPackage}.SM_{Token}";

    /// <summary>"Wooden Table" for <c>SS_Wooden_Table</c>; a second import keeps its <c>v2</c>.</summary>
    public string Name => string.Join(' ', (Token.StartsWith("SS_", StringComparison.OrdinalIgnoreCase) ? Token[3..] : Token).Split('_', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// The project's 3D models (Pheidian, 2026-10-13: "import custom models into the editor"): every FBX or OBJ file
/// <see cref="UeCook.ImportAsync"/> imported, from the Craftables page or the Map's Add object › My models, is one folder
/// <c>imports/&lt;token&gt;</c> of the project. Both pages list the same folders, so a model is imported once and used in
/// both: a craftable carries it in the Craftables pak, a placement on the map in the project's pak.
/// </summary>
public static class ModelLibrary
{
    /// <summary>True when <paramref name="meshPath"/> (package or object path) is an imported model's, below <see cref="UeCook.ImportsRoot"/>.</summary>
    public static bool IsModelMesh(string? meshPath) =>
        meshPath is not null && meshPath.StartsWith(UeCook.ImportsRoot + "/", StringComparison.OrdinalIgnoreCase) && TokenOf(meshPath) is not null;

    /// <summary>The token of an imported model's mesh path (<c>/Game/ScumStudio/Imports/SS_Table/SM_SS_Table.SM_SS_Table</c> gives <c>SS_Table</c>), else null.</summary>
    public static string? TokenOf(string meshPath)
    {
        ArgumentNullException.ThrowIfNull(meshPath);
        if (!meshPath.StartsWith(UeCook.ImportsRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = meshPath[(UeCook.ImportsRoot.Length + 1)..].Split('/');
        return parts is [{ Length: > 0 } token, { Length: > 0 }] ? token : null;
    }

    /// <summary>
    /// The models of the project in <paramref name="projectDirectory"/>, by name: every <c>imports/&lt;token&gt;</c> folder
    /// that holds its cooked mesh and nothing outside its own game folder (<see cref="CraftablesExporter.ImportedFolder"/>).
    /// </summary>
    public static IReadOnlyList<LibraryModel> List(string? projectDirectory)
    {
        var imports = projectDirectory is null ? null : new DirectoryInfo(Path.Combine(projectDirectory, UeCook.ImportsFolder));
        if (imports is not { Exists: true })
        {
            return [];
        }

        return imports.EnumerateDirectories()
            .Select(d => new LibraryModel(d.Name, UeCook.ImportsFolder + "/" + d.Name))
            .Where(m => FolderOf(projectDirectory, m.MeshPackage, out _) is not null)
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The full path of the folder that holds the model <paramref name="meshPath"/> draws, in the project in
    /// <paramref name="projectDirectory"/>; null, with <paramref name="problem"/> saying why, when it is not a model's mesh,
    /// the folder is gone or its cooked mesh is missing, or a file of it lies outside the model's own game folder.
    /// </summary>
    public static string? FolderOf(string? projectDirectory, string meshPath, out string problem)
    {
        ArgumentNullException.ThrowIfNull(meshPath);
        if (TokenOf(meshPath) is not { } token)
        {
            problem = "is not an imported 3D model";
            return null;
        }

        if (projectDirectory is null)
        {
            problem = "has no project folder to be read from";
            return null;
        }

        var folder = CraftablesExporter.ImportedFolder(projectDirectory, UeCook.ImportsFolder + "/" + token, out problem);
        if (folder is null)
        {
            return null;
        }

        var mesh = Path.Combine(folder, "SCUM", "Content", "ScumStudio", "Imports", token, "SM_" + token + ".uasset");
        if (!File.Exists(mesh) || !File.Exists(Path.ChangeExtension(mesh, ".uexp")))
        {
            problem = $"has no cooked mesh SM_{token} in {UeCook.ImportsFolder}/{token}";
            return null;
        }

        return folder;
    }

    /// <summary>
    /// The imported models' meshes that <paramref name="state"/> draws on the map: placed actors that are not deleted (the
    /// mesh they draw now) and parts whose mesh was replaced by a model. Distinct, as written in the journal.
    /// </summary>
    public static IReadOnlyList<string> UsedOnMap(EditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var placed = state.AddedActors
            .Where(a => !state.IsDeleted(a.Key) && a.Value is AddStaticMeshActorOp)
            .Select(a => state.GetMeshOverride(a.Key) ?? ((AddStaticMeshActorOp)a.Value).StaticMesh);
        var replaced = state.MeshOverrides.Where(m => !state.IsDeleted(m.Actor)).Select(m => m.Mesh);
        return placed.Concat(replaced).Where(IsModelMesh).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
