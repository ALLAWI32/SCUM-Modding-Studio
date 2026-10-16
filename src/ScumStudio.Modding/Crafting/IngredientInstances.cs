using System.Buffers.Binary;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// Adds ingredient tags to an item's entity setup: its class default's <c>CraftingIngredientInstances</c>
/// (<c>TArray&lt;FCraftingIngredientInstance { UCraftingIngredientTag* Type; float Amount; }&gt;</c>, SDK
/// <c>SCUM_structs.hpp</c>). Each new element names the tag (an import of <c>/Script/SCUM.CraftingIngredientTag</c>, added
/// with a create-before-serialize dependency as <see cref="RecipeIngredients"/> does) and is worth one of it.
/// </summary>
public static class IngredientInstances
{
    /// <summary>The property of <c>UItemEntitySetup</c>.</summary>
    public const string Property = "CraftingIngredientInstances";

    private const string ElementStruct = "CraftingIngredientInstance";

    /// <summary>
    /// <paramref name="setup"/> with <paramref name="tagPaths"/> (object paths) appended to its list, each worth 1; a tag it
    /// lists already is not added twice. A setup that stores no list gets a whole one: the list it inherits
    /// (<paramref name="inherited"/>, its parent setup's) first, since a list written on the child replaces the parent's.
    /// Every other export keeps its bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">The package has no class default object, or its list cannot be read.</exception>
    public static PackageBytes Append(CookedPackage setup, IReadOnlyList<string> tagPaths, IReadOnlyList<IngredientInstance>? inherited = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(tagPaths);
        var exportIndex = Enumerable.Range(0, setup.Exports.Count)
            .FirstOrDefault(i => setup.ResolveName(setup.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal), -1);
        if (exportIndex < 0)
        {
            throw new InvalidOperationException($"{setup.BasePath}: no class default object.");
        }

        var payload = setup.GetExportData(exportIndex).ToArray();
        var block = PropertyReader.ReadPayload(setup, payload, exportIndex);
        var tables = PackageTables.Of(setup, exportIndex);
        byte[] result;
        if (block.Find(Property) is { } tag)
        {
            if (tag.Value is not ArrayValue { InnerTag: { } inner } list || inner.StructName != ElementStruct)
            {
                throw new InvalidOperationException($"{setup.BasePath}: {Property} cannot be read.");
            }

            var listed = IngredientIndex.StoredInstances(setup, list).Select(i => i.TagPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = tagPaths.Where(listed.Add).Select(p => new IngredientInstance(p, 1)).ToList();
            if (added.Count == 0)
            {
                return PackageWriter.Rebuild(setup);
            }

            var elements = Elements(tables, added);
            result = [.. payload.AsSpan(0, tag.EndOffset), .. elements, .. payload.AsSpan(tag.EndOffset)];
            Add(result, tag.SizeFieldOffset, elements.Length);
            Add(result, tag.ValueOffset, added.Count);
            Add(result, inner.SizeFieldOffset, elements.Length);
        }
        else
        {
            var all = (inherited ?? []).Concat(tagPaths.Select(p => new IngredientInstance(p, 1))).DistinctBy(i => i.TagPath, StringComparer.OrdinalIgnoreCase).ToList();
            var none = block.EndOffset - 8; // the class default's own None: the new list goes before it
            result = [.. payload.AsSpan(0, none), .. WholeList(tables, all), .. payload.AsSpan(none)];
        }

        var data = Enumerable.Range(0, setup.Exports.Count).Select(i => i == exportIndex ? (ReadOnlyMemory<byte>)result : setup.GetExportData(i)).ToArray();
        return PackageWriter.Build(PackageWriter.ToBuildInput(setup) with
        {
            Names = tables.Names,
            NameIsWide = tables.Wide,
            Imports = tables.Imports,
            Exports = tables.Exports,
            PreloadDependencies = tables.Preload,
            ExportData = data,
        });
    }

    // The tagged ArrayProperty of structs: tag, count, the inner struct tag (native struct: no guid), the elements.
    private static byte[] WholeList(PackageTables tables, IReadOnlyList<IngredientInstance> instances)
    {
        var elements = Elements(tables, instances);
        var value = new ByteWriter();
        value.I32(instances.Count);
        Tag(value, tables, Property, "StructProperty", elements.Length, w =>
        {
            w.FName(tables.Name(ElementStruct));
            w.Guid(default);
        });
        value.Raw(elements);

        var property = new ByteWriter();
        Tag(property, tables, Property, "ArrayProperty", value.Length, w => w.FName(tables.Name("StructProperty")));
        property.Raw(value.WrittenSpan);
        return property.ToArray();
    }

    // Each element: Type (ObjectProperty, the tag's import), Amount (FloatProperty), None.
    private static byte[] Elements(PackageTables tables, IReadOnlyList<IngredientInstance> instances)
    {
        var w = new ByteWriter();
        foreach (var instance in instances)
        {
            Tag(w, tables, "Type", "ObjectProperty", 4);
            w.I32(tables.Import("/Script/SCUM", "CraftingIngredientTag", instance.TagPath));
            Tag(w, tables, "Amount", "FloatProperty", 4);
            w.F32(instance.Amount);
            w.FName(tables.Name("None"));
        }

        return w.ToArray();
    }

    private static void Tag(ByteWriter w, PackageTables tables, string name, string type, int size, Action<ByteWriter>? extra = null)
    {
        w.FName(tables.Name(name));
        w.FName(tables.Name(type));
        w.I32(size);
        w.I32(0); // array index
        extra?.Invoke(w);
        w.U8(0); // no property guid
    }

    private static void Add(byte[] buffer, int offset, int delta) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)) + delta);
}
