using ScumStudio.Core.Abstractions;

namespace ScumStudio.Pak.Writing;

/// <summary>A file to put into a pak: its path relative to the mount point and where its bytes come from.</summary>
public sealed record PakWriterEntry
{
    private PakWriterEntry(string path, string? sourceFile, ReadOnlyMemory<byte> data)
    {
        Path = VirtualPath.Normalize(path);
        if (Path.Length == 0)
        {
            throw new ArgumentException("Entry path must not be empty.", nameof(path));
        }

        SourceFile = sourceFile;
        Data = data;
    }

    /// <summary>Entry path relative to the mount point, e.g. <c>SCUM/Content/Foo.uasset</c>.</summary>
    public string Path { get; }

    /// <summary>File on disk providing the bytes, or null for in-memory data.</summary>
    public string? SourceFile { get; }

    /// <summary>In-memory bytes (used when <see cref="SourceFile"/> is null).</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Creates an entry backed by a file on disk.</summary>
    public static PakWriterEntry FromFile(string path, string sourceFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        return new PakWriterEntry(path, System.IO.Path.GetFullPath(sourceFile), ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>Creates an entry backed by in-memory bytes.</summary>
    public static PakWriterEntry FromBytes(string path, ReadOnlyMemory<byte> data) => new(path, null, data);

    /// <summary>Opens the source for reading.</summary>
    internal Stream OpenRead() =>
        SourceFile is not null
            ? new FileStream(SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan)
            : new MemoryStream(Data.ToArray(), writable: false);
}
