using System.Buffers.Binary;
using System.Text;
using ScumStudio.Formats.IO;

namespace ScumStudio.Formats.AssetRegistry;

/// <summary>
/// Cooked UE 4.27 <c>AssetRegistry.bin</c> (FAssetRegistryVersion 8 = FixedTags), port of <c>tools/assetreg.py</c>
/// class <c>Registry</c>. The name batch, fixed tag store and asset list are decoded; the dependency and package
/// data sections after the asset list are kept verbatim, so an unmodified file saves byte-identical.
/// </summary>
/// <remarks>
/// Layout: FGuid version guid, int32 version; name batch (u32 Num, u32 NumStringBytes, u64 HashAlgo, u64 Hash[Num]
/// = CityHash64 of the lower-cased name, u16 big-endian headers (bit 15 = UTF-16, 15-bit length), string bytes);
/// fixed tag store (0x12345679, 11 counts, texts, numberless names, names, numberless export paths, export paths,
/// ANSI/wide offsets and chars, numberless pairs, pairs, 0x87654321); int32 NumAssets + FAssetData records; tail.
/// </remarks>
public sealed class AssetRegistryFile
{
    /// <summary>Tag store begin magic.</summary>
    public const uint StoreBeginMagic = 0x12345679;

    /// <summary>Tag store end magic.</summary>
    public const uint StoreEndMagic = 0x87654321;

    /// <summary>Expected name batch hash algorithm id.</summary>
    public const ulong ExpectedHashAlgorithm = 0xC1640000;

    // store counts (indices into _n)
    private const int NNumberlessNames = 0, NNames = 1, NNumberlessExportPaths = 2, NExportPaths = 3, NTexts = 4,
        NAnsiOffsets = 5, NWideOffsets = 6, NAnsiBytes = 7, NWideChars = 8, NNumberlessPairs = 9, NPairs = 10;

    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private byte[] _head = [];
    private readonly List<string> _names = [];
    private readonly List<bool> _nameWide = [];
    private readonly List<ulong> _hashes = [];
    private int[] _n = new int[11];
    private readonly List<byte[]> _texts = [];
    private readonly ByteWriter _nlNames = new();
    private byte[] _namesV = [];
    private readonly List<int> _namesOff = [];
    private readonly ByteWriter _nlExports = new();
    private byte[] _exports = [];
    private readonly List<int> _exportsOff = [];
    private readonly ByteWriter _ansiOff = new();
    private byte[] _wideOff = [];
    private readonly ByteWriter _ansi = new();
    private byte[] _wide = [];
    private readonly ByteWriter _nlPairs = new();
    private byte[] _pairs = [];
    private readonly List<RawAsset> _assets = [];
    private byte[] _tail = [];

    private AssetRegistryFile()
    {
    }

    private sealed record RawAsset(uint[] Fields, int[] Numbers, ulong Handle, byte[] Rest, byte[] Raw);

    /// <summary>Hash algorithm id stored in the name batch.</summary>
    public ulong HashAlgorithm { get; private set; }

    /// <summary>FAssetRegistryVersion (int32 after the version guid).</summary>
    public int Version => BinaryPrimitives.ReadInt32LittleEndian(_head.AsSpan(16));

    /// <summary>Name batch strings.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>Stored name hashes, aligned with <see cref="Names"/>.</summary>
    public IReadOnlyList<ulong> NameHashes => _hashes;

    /// <summary>The 11 fixed tag store counts.</summary>
    public IReadOnlyList<int> StoreCounts => _n;

    /// <summary>Number of asset records.</summary>
    public int AssetCount => _assets.Count;

    /// <summary>Size of the verbatim tail (dependency and package data sections).</summary>
    public int TailLength => _tail.Length;

    /// <summary>Loads and parses a file.</summary>
    public static AssetRegistryFile Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Loads and parses a file asynchronously.</summary>
    public static async Task<AssetRegistryFile> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>Parses registry bytes (port of <c>Registry.__init__</c>).</summary>
    /// <exception cref="FormatException">Unsupported or corrupt layout.</exception>
    public static AssetRegistryFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var reg = new AssetRegistryFile();
        try
        {
            reg.ParseCore(data);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new FormatException("Corrupt or unsupported AssetRegistry.bin: " + ex.Message, ex);
        }

        return reg;
    }

    private static (uint Index, int Number) ReadFName(ByteReader r)
    {
        var i = r.U32();
        return (i & 0x80000000) != 0 ? (i & 0x7FFFFFFF, r.I32()) : (i, 0);
    }

    private void ParseCore(byte[] d)
    {
        var r = new ByteReader(d);
        _head = r.Raw(20);
        var num = checked((int)r.U32());
        var nbytes = r.U32();
        HashAlgorithm = r.U64();
        if (HashAlgorithm != ExpectedHashAlgorithm)
        {
            throw new FormatException($"Unexpected name hash algorithm 0x{HashAlgorithm:X}.");
        }

        if ((long)num * 10 > d.Length)
        {
            throw new FormatException($"Implausible name count {num}.");
        }

        _hashes.Capacity = num;
        for (var i = 0; i < num; i++)
        {
            _hashes.Add(r.U64());
        }

        var hdr = r.Position;
        r.Skip(2 * num);
        var s0 = r.Position;
        _names.Capacity = num;
        for (var i = 0; i < num; i++)
        {
            var b0 = d[hdr + (2 * i)];
            var b1 = d[hdr + (2 * i) + 1];
            var wide = (b0 & 0x80) != 0;
            var len = ((b0 & 0x7F) << 8) | b1;
            string name;
            if (wide)
            {
                name = Encoding.Unicode.GetString(d, r.Position, 2 * len);
                r.Skip(2 * len);
            }
            else
            {
                name = Encoding.Latin1.GetString(d, r.Position, len);
                r.Skip(len);
            }

            _names.Add(name);
            _nameWide.Add(wide);
            _index.TryAdd(name, i);
        }

        if (r.Position - s0 != nbytes)
        {
            throw new FormatException($"Name batch string bytes {r.Position - s0} != {nbytes}.");
        }

        if (r.U32() != StoreBeginMagic)
        {
            throw new FormatException("Fixed tag store begin magic not found.");
        }

        for (var i = 0; i < 11; i++)
        {
            _n[i] = r.I32();
        }

        var tdb = r.U32();
        var t0 = r.Position;
        for (var i = 0; i < _n[NTexts]; i++)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(r.Position));
            _texts.Add(r.Raw(4 + (len >= 0 ? len : -2 * len)));
        }

        if (r.Position - t0 != tdb)
        {
            throw new FormatException("Text data size mismatch.");
        }

        _nlNames.Raw(r.Raw(4 * _n[NNumberlessNames]));
        var a = r.Position;
        for (var i = 0; i < _n[NNames]; i++)
        {
            _namesOff.Add(r.Position - a);
            ReadFName(r);
        }

        _namesV = d.AsSpan(a, r.Position - a).ToArray();
        _nlExports.Raw(r.Raw(12 * _n[NNumberlessExportPaths]));
        a = r.Position;
        for (var i = 0; i < _n[NExportPaths]; i++)
        {
            _exportsOff.Add(r.Position - a);
            ReadFName(r);
            ReadFName(r);
            ReadFName(r);
        }

        _exports = d.AsSpan(a, r.Position - a).ToArray();
        _ansiOff.Raw(r.Raw(4 * _n[NAnsiOffsets]));
        _wideOff = r.Raw(4 * _n[NWideOffsets]);
        _ansi.Raw(r.Raw(_n[NAnsiBytes]));
        _wide = r.Raw(2 * _n[NWideChars]);
        _nlPairs.Raw(r.Raw(8 * _n[NNumberlessPairs]));
        a = r.Position;
        for (var i = 0; i < _n[NPairs]; i++)
        {
            ReadFName(r);
            r.U32();
        }

        _pairs = d.AsSpan(a, r.Position - a).ToArray();
        if (_n[NPairs] != 0)
        {
            throw new NotSupportedException("Numbered tag pairs are not supported (assetreg.py limitation).");
        }

        if (r.U32() != StoreEndMagic)
        {
            throw new FormatException("Fixed tag store end magic not found.");
        }

        var na = r.I32();
        if (na < 0 || (long)na * 32 > d.Length)
        {
            throw new FormatException($"Implausible asset count {na}.");
        }

        _assets.Capacity = na;
        for (var i = 0; i < na; i++)
        {
            var a0 = r.Position;
            var fields = new uint[5];
            var numbers = new int[5];
            for (var k = 0; k < 5; k++)
            {
                (fields[k], numbers[k]) = ReadFName(r);
            }

            var h = r.U64();
            var b0 = r.Position;
            var bundles = r.I32();
            for (var b = 0; b < bundles; b++)
            {
                // asset bundles: FName name, TArray<(FName path, FString sub)>
                ReadFName(r);
                var count = r.I32();
                for (var c = 0; c < count; c++)
                {
                    ReadFName(r);
                    var len = r.I32();
                    r.Skip(len >= 0 ? len : -2 * len);
                }
            }

            var nc = r.I32();
            r.Skip((4 * nc) + 4); // ChunkIDs, PackageFlags
            _assets.Add(new RawAsset(fields, numbers, h, d.AsSpan(b0, r.Position - b0).ToArray(), d.AsSpan(a0, r.Position - a0).ToArray()));
        }

        _tail = d.AsSpan(r.Position).ToArray();
    }

    // ---- names ----

    /// <summary>
    /// Hash stored for a name: CityHash64 of the lower-cased name (ASCII lower-casing, like UE's TChar::ToLower),
    /// over Latin-1 bytes or UTF-16 bytes for wide names.
    /// </summary>
    public static ulong ComputeNameHash(string name, bool wide = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lower = string.Create(name.Length, name, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
        return CityHash.CityHash64(wide ? Encoding.Unicode.GetBytes(lower) : Encoding.Latin1.GetBytes(lower));
    }

    /// <summary>Indices of names whose stored hash differs from <see cref="ComputeNameHash"/>.</summary>
    public IReadOnlyList<int> FindNameHashMismatches()
    {
        var bad = new List<int>();
        for (var i = 0; i < _names.Count; i++)
        {
            if (ComputeNameHash(_names[i], _nameWide[i]) != _hashes[i])
            {
                bad.Add(i);
            }
        }

        return bad;
    }

    /// <summary>Index of a name, appending it (with its hash) when missing (port of <c>name_id</c>).</summary>
    public int GetOrAddName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_index.TryGetValue(text, out var i))
        {
            return i;
        }

        var wide = !ByteWriter.IsLatin1(text);
        i = _names.Count;
        _names.Add(text);
        _nameWide.Add(wide);
        _index[text] = i;
        _hashes.Add(ComputeNameHash(text, wide));
        return i;
    }

    private string FormatName(uint index, int number) =>
        number == 0 ? _names[(int)index] : $"{_names[(int)index]}_{number - 1}";

    // ---- values ----

    /// <summary>Decodes a value id (port of <c>value_str</c>).</summary>
    public AssetRegistryValue GetValue(uint valueId)
    {
        var kind = (AssetRegistryValueKind)(valueId & 7);
        var i = (int)(valueId >> 3);
        switch (kind)
        {
            case AssetRegistryValueKind.AnsiString:
            {
                var ansi = _ansi.WrittenSpan;
                var o = (int)BinaryPrimitives.ReadUInt32LittleEndian(_ansiOff.WrittenSpan[(4 * i)..]);
                var e = ansi[o..].IndexOf((byte)0);
                return AssetRegistryValue.Ansi(Encoding.Latin1.GetString(ansi.Slice(o, e < 0 ? ansi.Length - o : e)));
            }

            case AssetRegistryValueKind.WideString:
            {
                var o = (int)BinaryPrimitives.ReadUInt32LittleEndian(_wideOff.AsSpan(4 * i));
                var sb = new StringBuilder();
                for (var p = 2 * o; p + 1 < _wide.Length; p += 2)
                {
                    var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(_wide.AsSpan(p));
                    if (c == 0)
                    {
                        break;
                    }

                    sb.Append(c);
                }

                return new AssetRegistryValue(kind, sb.ToString());
            }

            case AssetRegistryValueKind.NumberlessName:
                return AssetRegistryValue.NumberlessName(_names[(int)BinaryPrimitives.ReadUInt32LittleEndian(_nlNames.WrittenSpan[(4 * i)..])]);
            case AssetRegistryValueKind.Name:
            {
                var r = new ByteReader(_namesV, _namesOff[i], _namesV.Length - _namesOff[i]);
                var (a, b) = ReadFName(r);
                return new AssetRegistryValue(kind, FormatName(a, b)) { NameNumber = b };
            }

            case AssetRegistryValueKind.NumberlessExportPath:
            {
                var span = _nlExports.WrittenSpan[(12 * i)..];
                var cls = _names[(int)BinaryPrimitives.ReadUInt32LittleEndian(span)];
                var obj = _names[(int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..])];
                var pkg = _names[(int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..])];
                return AssetRegistryValue.NumberlessExport(cls, obj, pkg);
            }

            case AssetRegistryValueKind.ExportPath:
            {
                var r = new ByteReader(_exports, _exportsOff[i], _exports.Length - _exportsOff[i]);
                var (c1, c2) = ReadFName(r);
                var (o1, o2) = ReadFName(r);
                var (p1, p2) = ReadFName(r);
                var cls = FormatName(c1, c2);
                var obj = FormatName(o1, o2);
                var pkg = FormatName(p1, p2);
                return new AssetRegistryValue(kind, $"{cls}'{pkg}.{obj}'") { ExportPath = [cls, obj, pkg] };
            }

            case AssetRegistryValueKind.LocalizedText:
            {
                var raw = _texts[i];
                var r = new ByteReader(raw);
                string text;
                try
                {
                    text = r.FString();
                }
                catch (Exception ex) when (ex is FormatException or EndOfStreamException)
                {
                    text = string.Empty;
                }

                return new AssetRegistryValue(kind, text) { RawText = raw };
            }

            default:
                throw new FormatException($"Unknown tag value type {(int)kind}.");
        }
    }

    /// <summary>
    /// Appends a value to the store and returns its id (port of <c>add_value</c>; supports ANSI strings,
    /// numberless names, numberless export paths and texts).
    /// </summary>
    public uint AddValue(AssetRegistryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value.Kind)
        {
            case AssetRegistryValueKind.AnsiString:
                var o = _ansi.Length;
                _ansi.Raw(Encoding.Latin1.GetBytes(value.Text));
                _ansi.U8(0);
                _ansiOff.U32((uint)o);
                _n[NAnsiOffsets]++;
                _n[NAnsiBytes] = _ansi.Length;
                return ((uint)(_n[NAnsiOffsets] - 1) << 3) | 0;
            case AssetRegistryValueKind.NumberlessName:
                _nlNames.U32((uint)GetOrAddName(value.Text));
                _n[NNumberlessNames]++;
                return ((uint)(_n[NNumberlessNames] - 1) << 3) | 2;
            case AssetRegistryValueKind.NumberlessExportPath:
                var path = value.ExportPath ?? throw new ArgumentException("Export path value needs ExportPath (class, object, package).", nameof(value));
                foreach (var part in path)
                {
                    _nlExports.U32((uint)GetOrAddName(part));
                }

                _n[NNumberlessExportPaths]++;
                return ((uint)(_n[NNumberlessExportPaths] - 1) << 3) | 4;
            case AssetRegistryValueKind.LocalizedText:
                if (value.RawText is null)
                {
                    var w = new ByteWriter();
                    w.FString(value.Text);
                    _texts.Add(w.ToArray());
                }
                else
                {
                    _texts.Add(value.RawText);
                }

                _n[NTexts]++;
                return ((uint)(_n[NTexts] - 1) << 3) | 6;
            default:
                throw new NotSupportedException($"Adding {value.Kind} values is not supported (assetreg.py add_value).");
        }
    }

    // ---- assets ----

    /// <summary>The asset record at <paramref name="index"/> (port of <c>asset(i)</c>).</summary>
    public AssetData GetAsset(int index)
    {
        var a = _assets[index];
        var nm = new string[5];
        for (var k = 0; k < 5; k++)
        {
            nm[k] = FormatName(a.Fields[k], a.Numbers[k]);
        }

        return new AssetData(index, nm[0], nm[1], nm[2], nm[3], nm[4], a.Handle);
    }

    /// <summary>All asset records.</summary>
    public IEnumerable<AssetData> Assets
    {
        get
        {
            for (var i = 0; i < _assets.Count; i++)
            {
                yield return GetAsset(i);
            }
        }
    }

    /// <summary>The raw serialized bytes of one asset record.</summary>
    public ReadOnlyMemory<byte> GetAssetRawBytes(int index) => _assets[index].Raw;

    /// <summary>Tags of a tag map handle (port of <c>tags(h)</c>).</summary>
    public IReadOnlyList<(string Key, AssetRegistryValue Value)> GetTags(ulong handle)
    {
        var numberless = (handle >> 63) != 0;
        var count = (int)((handle >> 32) & 0xFFFF);
        var first = (int)(handle & 0xFFFFFFFF);
        var output = new List<(string, AssetRegistryValue)>(count);
        for (var k = 0; k < count; k++)
        {
            if (numberless)
            {
                var span = _nlPairs.WrittenSpan[(8 * (first + k))..];
                var key = BinaryPrimitives.ReadUInt32LittleEndian(span);
                var vid = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                output.Add((_names[(int)key], GetValue(vid)));
            }
            else
            {
                var span = _pairs.AsSpan(12 * (first + k));
                var key = BinaryPrimitives.ReadUInt32LittleEndian(span);
                var vid = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                output.Add((_names[(int)(key & 0x7FFFFFFF)], GetValue(vid)));
            }
        }

        return output;
    }

    /// <summary>Tags of an asset.</summary>
    public IReadOnlyList<(string Key, AssetRegistryValue Value)> GetTags(AssetData asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return GetTags(asset.TagMapHandle);
    }

    /// <summary>
    /// Appends an asset record cloned from <paramref name="template"/> (its class, bundles, chunk ids and package
    /// flags) with new names and the given tags (numberless keys), port of <c>add_asset</c>.
    /// </summary>
    /// <returns>The new record.</returns>
    public AssetData AddAsset(AssetData template, string objectPath, string packagePath, string packageName, string assetName,
        IEnumerable<(string Key, AssetRegistryValue Value)> tags)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(tags);
        var tpl = _assets[template.Index];
        if (tpl.Numbers[2] != 0)
        {
            // Only the class name is taken over from the template (the other names are the new ones, numberless): a
            // template in a numbered folder (a trader personality under .../Outpost_B_4) is fine.
            throw new NotSupportedException("Template asset uses a numbered class name.");
        }

        var first = _n[NNumberlessPairs];
        var count = 0;
        foreach (var (key, value) in tags)
        {
            var keyId = (uint)GetOrAddName(key);
            var vid = AddValue(value);
            _nlPairs.U32(keyId);
            _nlPairs.U32(vid);
            _n[NNumberlessPairs]++;
            count++;
        }

        var h = (1UL << 63) | ((ulong)count << 32) | (uint)first;
        var fields = new uint[]
        {
            (uint)GetOrAddName(objectPath), (uint)GetOrAddName(packagePath), tpl.Fields[2],
            (uint)GetOrAddName(packageName), (uint)GetOrAddName(assetName),
        };
        var w = new ByteWriter(64 + tpl.Rest.Length);
        foreach (var f in fields)
        {
            w.U32(f);
        }

        w.U64(h);
        w.Raw(tpl.Rest);
        _assets.Add(new RawAsset(fields, new int[5], h, tpl.Rest, w.ToArray()));
        return GetAsset(_assets.Count - 1);
    }

    /// <summary>
    /// Clones <paramref name="template"/> under a new package path: copies its tags, replacing every occurrence of the
    /// template's package name / asset name in string values (ANSI strings, names and export paths).
    /// </summary>
    public AssetData CloneAsset(AssetData template, string newPackageName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrEmpty(newPackageName);
        var slash = newPackageName.LastIndexOf('/');
        var assetName = newPackageName[(slash + 1)..];
        var packagePath = slash > 0 ? newPackageName[..slash] : "/";
        var suffix = template.ObjectPath.Length > template.PackageName.Length ? template.ObjectPath[template.PackageName.Length..] : "." + template.AssetName;
        var objectPath = newPackageName + suffix.Replace(template.AssetName, assetName, StringComparison.Ordinal);
        string Map(string s) => s.Replace(template.PackageName, newPackageName, StringComparison.Ordinal)
            .Replace(template.AssetName, assetName, StringComparison.Ordinal);
        var tags = GetTags(template).Select(t => (t.Key, t.Value.Kind switch
        {
            AssetRegistryValueKind.AnsiString => AssetRegistryValue.Ansi(Map(t.Value.Text)),
            AssetRegistryValueKind.NumberlessName => AssetRegistryValue.NumberlessName(Map(t.Value.Text)),
            AssetRegistryValueKind.NumberlessExportPath => AssetRegistryValue.NumberlessExport(
                t.Value.ExportPath![0], Map(t.Value.ExportPath[1]), Map(t.Value.ExportPath[2])),
            AssetRegistryValueKind.LocalizedText => t.Value,
            _ => throw new NotSupportedException($"Cannot clone tag '{t.Key}' of kind {t.Value.Kind}."),
        })).ToList();
        return AddAsset(template, objectPath, packagePath, newPackageName, assetName, tags);
    }

    /// <summary>Removes every asset matching <paramref name="predicate"/> (tag pairs stay, unreferenced). Returns the count.</summary>
    public int Remove(Func<AssetData, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var keep = new List<RawAsset>(_assets.Count);
        for (var i = 0; i < _assets.Count; i++)
        {
            if (!predicate(GetAsset(i)))
            {
                keep.Add(_assets[i]);
            }
        }

        var removed = _assets.Count - keep.Count;
        _assets.Clear();
        _assets.AddRange(keep);
        return removed;
    }

    /// <summary>Serializes the registry (port of <c>save</c>).</summary>
    public byte[] Save()
    {
        var o = new ByteWriter(_tail.Length + (_assets.Count * 64) + (_names.Count * 40) + 1024);
        o.Raw(_head);
        var bodyLength = 0;
        for (var i = 0; i < _names.Count; i++)
        {
            bodyLength += _nameWide[i] ? 2 * _names[i].Length : _names[i].Length;
        }

        o.U32((uint)_names.Count);
        o.U32((uint)bodyLength);
        o.U64(HashAlgorithm);
        foreach (var h in _hashes)
        {
            o.U64(h);
        }

        Span<byte> hdr = stackalloc byte[2];
        for (var i = 0; i < _names.Count; i++)
        {
            var len = _names[i].Length;
            if (len > 0x7FFF)
            {
                throw new InvalidOperationException($"Name too long for the name batch: {len} chars.");
            }

            BinaryPrimitives.WriteUInt16BigEndian(hdr, (ushort)(len | (_nameWide[i] ? 0x8000 : 0)));
            o.Raw(hdr);
        }

        for (var i = 0; i < _names.Count; i++)
        {
            o.Raw(_nameWide[i] ? Encoding.Unicode.GetBytes(_names[i]) : Encoding.Latin1.GetBytes(_names[i]));
        }

        o.U32(StoreBeginMagic);
        foreach (var n in _n)
        {
            o.I32(n);
        }

        o.U32((uint)_texts.Sum(t => t.Length));
        foreach (var t in _texts)
        {
            o.Raw(t);
        }

        o.Raw(_nlNames.WrittenSpan);
        o.Raw(_namesV);
        o.Raw(_nlExports.WrittenSpan);
        o.Raw(_exports);
        o.Raw(_ansiOff.WrittenSpan);
        o.Raw(_wideOff);
        o.Raw(_ansi.WrittenSpan);
        o.Raw(_wide);
        o.Raw(_nlPairs.WrittenSpan);
        o.Raw(_pairs);
        o.U32(StoreEndMagic);
        o.I32(_assets.Count);
        foreach (var a in _assets)
        {
            o.Raw(a.Raw);
        }

        o.Raw(_tail);
        return o.ToArray();
    }

    /// <summary>Serializes and writes the registry.</summary>
    public Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
        File.WriteAllBytesAsync(path, Save(), cancellationToken);
}
