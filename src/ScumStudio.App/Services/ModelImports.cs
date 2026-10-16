using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Import;
using ScumStudio.Level.Projects;

namespace ScumStudio.App.Services;

/// <summary>
/// The one import of 3D models (FBX, OBJ) behind the Craftables page's Import 3D model and the Map's Add object › My
/// models: the engine check, the file picker and <see cref="UeCook.ImportAsync"/> under the app's operation runner. The
/// model lands in the project's library (<see cref="ModelLibrary"/>), which both pages list: imported once, used in both.
/// </summary>
public static class ModelImports
{
    /// <summary>Unreal Engine 4.27 is installed (the import needs it).</summary>
    public static bool HasEngine => UeCook.FindEngine() is not null;

    /// <summary>What the import needs when <see cref="HasEngine"/> is false.</summary>
    public static string NoEngineText => Loc.F("Craftables.Import.NoEngine", UeCook.DefaultEngineDirectory, UeCook.EngineVariable);

    /// <summary>
    /// Picks an FBX or OBJ file for the open project. Null when the game files or a project are missing
    /// (<paramref name="noProjectText"/>) or Unreal Engine 4.27 is not installed (<see cref="NoEngineText"/>), each said in a
    /// notification under <paramref name="title"/>, or when the picker was closed.
    /// </summary>
    public static async Task<ModelImportJob?> PickAsync(AppServices services, string title, string noProjectText)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Workspace.Catalog is not { } catalog || services.Projects.Current is not { } project)
        {
            services.Notifications.Warning(title, services.Projects.HasProject ? Loc.T("Craftables.ConnectFirst") : noProjectText);
            return null;
        }

        if (!HasEngine)
        {
            services.Notifications.Error(title, NoEngineText);
            return null;
        }

        var file = await services.Dialogs.OpenFileAsync(Loc.T("Craftables.Import"), string.Join(';', UeCook.Extensions), Loc.T("Craftables.Import.Filter")).ConfigureAwait(true);
        return file is null ? null : new ModelImportJob(catalog, project, file);
    }

    /// <summary>
    /// Imports <paramref name="job"/>'s file into its project's library (minutes the first time); null when it failed or
    /// was cancelled (the operation runner reports it).
    /// </summary>
    public static async Task<ImportedModel?> ImportAsync(AppServices services, ModelImportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(job);
        services.Keys.TryGet(out var key);
        var (ok, model) = await services.Operations.RunAsync(Loc.F("Craftables.Importing", Path.GetFileName(job.File)),
            (sink, ct) => UeCook.ImportAsync(job.Catalog, job.File, job.Project.DirectoryPath, sink, aesKey: key, cancellationToken: ct), cancellationToken).ConfigureAwait(true);
        return ok ? model : null;
    }
}

/// <summary>A picked 3D file and where it goes (<see cref="ModelImports.PickAsync"/>).</summary>
/// <param name="Catalog">The game files it is checked against.</param>
/// <param name="Project">The project it is kept in (the one open when it was picked).</param>
/// <param name="File">The FBX or OBJ file.</param>
public sealed record ModelImportJob(AssetCatalog Catalog, Project Project, string File);
