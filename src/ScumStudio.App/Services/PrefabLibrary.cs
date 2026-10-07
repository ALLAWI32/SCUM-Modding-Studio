using System.Globalization;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;

namespace ScumStudio.App.Services;

/// <summary>A prefab of the library with the file it lives in.</summary>
/// <param name="Path">The <c>.ssprefab</c> file.</param>
/// <param name="Prefab">Its content.</param>
public sealed record PrefabEntry(string Path, Prefab Prefab)
{
    /// <summary>The name shown: the file's name (what Save and Import gave it).</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The user's prefabs: one plain JSON file per prefab in <c>&lt;data folder&gt;\Prefabs</c>, independent of any project.
/// Export is a file copy, Import a validated copy in; no file is ever encrypted (owner: "it must show the objects, their
/// meshes and positions").
/// </summary>
public sealed class PrefabLibrary
{
    /// <summary>Creates the library over <paramref name="folder"/> (created on first write).</summary>
    public PrefabLibrary(string folder) => Folder = Path.GetFullPath(folder);

    /// <summary>The library folder.</summary>
    public string Folder { get; }

    /// <summary>Raised after a prefab was saved, imported or deleted.</summary>
    public event EventHandler? Changed;

    /// <summary>Every readable prefab, newest first; unreadable files are skipped.</summary>
    public IReadOnlyList<PrefabEntry> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var entries = new List<PrefabEntry>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*" + Prefab.Extension))
        {
            try
            {
                entries.Add(new PrefabEntry(file, Prefab.Load(file)));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // a half-written or foreign file: not listed
            }
        }

        return entries.OrderByDescending(e => e.Prefab.Created).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Writes <paramref name="prefab"/> as <c>&lt;name&gt;.ssprefab</c> (a prefab of the same name is replaced).</summary>
    public PrefabEntry Save(Prefab prefab)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        var path = Path.Combine(Folder, Prefab.SafeFileName(prefab.Name) + Prefab.Extension);
        prefab.Save(path);
        Changed?.Invoke(this, EventArgs.Empty);
        return new PrefabEntry(path, prefab);
    }

    /// <summary>Deletes the prefab's file.</summary>
    public void Delete(PrefabEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        File.Delete(entry.Path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the prefab's file to <paramref name="destination"/> (plain JSON, for sharing).</summary>
    public void Export(PrefabEntry entry, string destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.Copy(entry.Path, destination, overwrite: true);
    }

    /// <summary>
    /// Copies a <c>.ssprefab</c> file into the library under its own name (a free name when that is taken). The file must
    /// parse, and when <paramref name="isKnown"/> is given at least one part must refer to something it knows.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a prefab file, or nothing in it is known to this game install.</exception>
    public PrefabEntry Import(string sourcePath, Func<PrefabPart, bool>? isKnown = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var prefab = Prefab.Load(sourcePath);
        if (isKnown is not null && !prefab.Parts.Any(isKnown))
        {
            throw new InvalidDataException("None of the prefab's objects exist in this game install.");
        }

        var stem = Prefab.SafeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, stem + Prefab.Extension);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(Folder, string.Create(CultureInfo.InvariantCulture, $"{stem} {i}{Prefab.Extension}"));
        }

        File.Copy(sourcePath, path);
        Changed?.Invoke(this, EventArgs.Empty);
        return new PrefabEntry(path, prefab);
    }

    /// <summary>
    /// Imports <paramref name="path"/> (see <see cref="Import"/>), refusing a file none of whose objects exist in
    /// <paramref name="catalog"/>, and tells the user how it went. Null when refused.
    /// </summary>
    public PrefabEntry? ImportFile(string path, AssetCatalog? catalog, NotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        try
        {
            var entry = Import(path, catalog is null ? null : p => Knows(catalog, p));
            notifications.Success(Loc.T("Map.Prefabs.Imported"), Loc.F("Map.Prefabs.SavedDetail", entry.Name, entry.Prefab.Parts.Count));
            return entry;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            notifications.Error(Loc.T("Map.Prefabs.ImportFailed"), ex.Message);
            return null;
        }
    }

    /// <summary>True when the game files <paramref name="catalog"/> reads still hold what the part is made from (its mesh, or its source level).</summary>
    public static bool Knows(AssetCatalog catalog, PrefabPart part)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(part);
        return part.Kind switch
        {
            PrefabPartKind.StaticMesh => Has(part.Mesh),
            PrefabPartKind.FoliageInstance => Has(part.Mesh) || Has(part.Level),
            _ => Has(part.Level),
        };

        bool Has(string? path) => path is not null && catalog.PackageExists(AssetPaths.SplitObjectPath(path).PackagePath);
    }
}
