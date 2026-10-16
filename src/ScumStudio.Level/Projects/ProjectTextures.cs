using System.Security.Cryptography;
using ScumStudio.Assets.Textures;
using ScumStudio.Modding.Textures;

namespace ScumStudio.Level.Projects;

/// <summary>
/// The pictures a project's textures are made from: <c>&lt;project&gt;/textures/&lt;sha256&gt;.png</c>, named by their pixels, so
/// the same picture is stored once and an undone replace leaves only a harmless file behind. The journal keeps the relative
/// file (<see cref="TextureWrites.Value"/>); the export reads it back from the project folder.
/// </summary>
public static class ProjectTextures
{
    /// <summary>The folder inside a project.</summary>
    public const string FolderName = "textures";

    /// <summary>Saves the pixels as a PNG in the project and returns its relative file (<c>textures/&lt;hash&gt;.png</c>).</summary>
    public static async Task<string> SaveAsync(string projectDirectory, byte[] rgba, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(rgba);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(width));
        hash.AppendData(BitConverter.GetBytes(height));
        hash.AppendData(rgba, 0, width * height * 4);
        var file = $"{FolderName}/{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}.png";
        var full = Path.Combine(projectDirectory, FolderName, Path.GetFileName(file));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var temp = full + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await PngWriter.WriteAsync(stream, rgba, width, height, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, full, overwrite: true);
        }

        return file;
    }

    /// <summary>Synchronous <see cref="SaveAsync"/>.</summary>
    public static string Save(string projectDirectory, byte[] rgba, int width, int height) =>
        SaveAsync(projectDirectory, rgba, width, height).GetAwaiter().GetResult();

    /// <summary>Reads a picture of the project.</summary>
    /// <exception cref="FileNotFoundException">The file is not in the project.</exception>
    /// <exception cref="InvalidDataException">The file is not a picture.</exception>
    public static TextureImage Load(string projectDirectory, string file)
    {
        var full = TextureWrites.ResolveFile(projectDirectory, file);
        return full is not null && File.Exists(full)
            ? ImageFiles.Load(full)
            : throw new FileNotFoundException($"{file} is not in the project's {FolderName} folder.", file);
    }
}
