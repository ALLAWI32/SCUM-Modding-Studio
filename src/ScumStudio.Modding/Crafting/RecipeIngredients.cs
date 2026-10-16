using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// The <c>Ingredients</c> of a cooked crafting recipe (<c>PlaceableCraftingRecipe</c> / <c>ItemCraftingRecipe</c>): an
/// array of <c>FCraftingIngredientSlot</c> structs (<c>AllowedTypes</c> = ingredient tags, <c>Amount</c> per skill level
/// <c>NoSkill/Basic/Medium/Advanced/AboveAdvanced</c>, <c>Purpose</c> Material or Tool, …). A new list is written by
/// copying one of the recipe's own slots per ingredient and setting its tag (an import, added when the recipe lacks it),
/// amounts and purpose.
/// </summary>
public static class RecipeIngredients
{
    /// <summary>Folder of the game's ingredient tags (<c>CI_*</c>); a craftable station's tag is cloned into it.</summary>
    public const string TagFolder = "/Game/ConZ_Files/Items/Crafting/Ingredients";

    private static readonly string[] Levels = ["NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced"];

    /// <summary>The object path of tag <paramref name="tag"/> (<c>CI_Plank</c> → <c>/Game/…/Ingredients/CI_Plank.CI_Plank</c>).</summary>
    public static string TagPath(string tag) => tag.StartsWith('/') ? tag : $"{TagFolder}/{tag}.{tag}";

    /// <summary>
    /// The amount of each skill level for <paramref name="amount"/> without skill: the game's recipes ask less of a
    /// skilled player (wall of wood 9/9/7/6/5, of cement 28/…/16), about 100/100/80/65/60 %.
    /// </summary>
    public static int[] PerSkill(int amount) =>
        [amount, amount, Math.Max(1, (int)MathF.Round(amount * 0.8f)), Math.Max(1, (int)MathF.Round(amount * 0.65f)), Math.Max(1, (int)MathF.Round(amount * 0.6f))];

    /// <summary>
    /// Writes <paramref name="ingredients"/> as the recipe's whole ingredient list and, when given, moves it to the crafting
    /// menu section <paramref name="section"/> (<see cref="SetCategory(CookedPackage, CraftCategory)"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">No recipe export, no slot to copy, an empty list, or a section but no section sub-object.</exception>
    public static PackageBytes Rewrite(CookedPackage package, IReadOnlyList<CraftIngredient> ingredients, CraftCategory? section = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(ingredients);
        if (ingredients.Count == 0)
        {
            throw new InvalidOperationException("A recipe needs at least one ingredient.");
        }

        var exportIndex = RecipeExport(package);
        var payload = DropSkins(package, exportIndex, package.GetExportData(exportIndex).ToArray());
        var block = PropertyReader.ReadPayload(package, payload, exportIndex);
        if (block.Find("Ingredients") is not { Value: ArrayValue { InnerTag: { } inner } list } tag)
        {
            throw new InvalidOperationException($"{package.BasePath}: no ingredient list.");
        }

        var slots = list.Items.OfType<StructValue>().ToList();
        var model = slots.FirstOrDefault(s => Member(s, "AllowedTypes")?.Value is ArrayValue { Items.Count: 1 } && Member(s, "Purpose") is not null && Member(s, "Amount") is not null)
            ?? throw new InvalidOperationException($"{package.BasePath}: no single-tag ingredient slot to copy.");
        var allowed = ((ArrayValue)Member(model, "AllowedTypes")!.Value).Items[0];
        var amounts = (StructValue)Member(model, "Amount")!.Value;
        var purpose = Member(model, "Purpose")!;

        var tables = PackageTables.Of(package, exportIndex);
        var tool = tables.Name("ECraftingIngredientPurpose::Tool");
        var material = tables.Name("ECraftingIngredientPurpose::Material");
        var written = new List<byte>();
        foreach (var ingredient in ingredients)
        {
            var entry = payload.AsSpan(model.Offset, model.Size).ToArray();
            var import = tables.Import("/Script/SCUM", "CraftingIngredientTag", TagPath(ingredient.Tag));
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(allowed.Offset - model.Offset), import);
            var perSkill = PerSkill(Math.Max(1, ingredient.Amount));
            for (var i = 0; i < Levels.Length; i++)
            {
                if (Member(amounts, Levels[i]) is { Value: IntValue } level)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(level.ValueOffset - model.Offset), perSkill[i]);
                }
            }

            var name = ingredient.IsTool ? tool : material;
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(purpose.ValueOffset - model.Offset), name.Index);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(purpose.ValueOffset - model.Offset + 4), name.Number);
            written.AddRange(entry);
        }

        // Swap the old elements for the new ones: the array's size, its count and the inner struct tag's size follow.
        var start = slots[0].Offset;
        var delta = written.Count - (tag.EndOffset - start);
        var result = new byte[payload.Length + delta];
        payload.AsSpan(0, start).CopyTo(result);
        written.CopyTo(result, start);
        payload.AsSpan(tag.EndOffset).CopyTo(result.AsSpan(start + written.Count));
        Add(result, tag.SizeFieldOffset, delta);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tag.ValueOffset), ingredients.Count);
        Add(result, inner.SizeFieldOffset, delta);

        var data = Enumerable.Range(0, package.Exports.Count).Select(i => i == exportIndex ? (ReadOnlyMemory<byte>)result : package.GetExportData(i)).ToArray();
        if (section is not null)
        {
            var category = CategoryIndex(package);
            data[category] = SetCategory(package, category, package.GetExportData(category).ToArray(), tables, section);
        }

        return Build(package, tables, data);
    }

    /// <summary>
    /// The recipe shown in the crafting menu section <paramref name="section"/>: the FName of its
    /// <c>CraftingMetadata_RecipeCategory.CraftingCategoryTag.TagName</c> and, when the recipe stores one, of its
    /// <c>DisplayDomain</c> are replaced (same size; the names are added to the name table when missing).
    /// </summary>
    /// <exception cref="InvalidOperationException">The recipe has no section sub-object or no tag in it.</exception>
    public static PackageBytes SetCategory(CookedPackage package, CraftCategory section)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(section);
        var index = CategoryIndex(package);
        var tables = PackageTables.Of(package, index);
        var data = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i)).ToArray();
        data[index] = SetCategory(package, index, data[index].ToArray(), tables, section);
        return Build(package, tables, data);
    }

    /// <summary>
    /// The recipe with its own ingredients and without its skin links (a copied upgrade recipe of a crafted element would
    /// otherwise offer the stock skin upgrade, which builds the stock element); null when it has none.
    /// </summary>
    public static PackageBytes? WithoutSkins(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var exportIndex = RecipeExport(package);
        var stored = package.GetExportData(exportIndex).ToArray();
        var payload = DropSkins(package, exportIndex, stored);
        if (payload.Length == stored.Length)
        {
            return null;
        }

        var data = Enumerable.Range(0, package.Exports.Count).Select(i => i == exportIndex ? (ReadOnlyMemory<byte>)payload : package.GetExportData(i)).ToArray();
        return Build(package, PackageTables.Of(package, exportIndex), data);
    }

    /// <summary>The recipe's ingredients as written (tag name, amount without skill, tool or material).</summary>
    public static IReadOnlyList<CraftIngredient> Read(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var block = package.ReadProperties(RecipeExport(package));
        if (block.Find("Ingredients")?.Value is not ArrayValue list)
        {
            return [];
        }

        return list.Items.OfType<StructValue>().Select(s =>
        {
            var tag = Member(s, "AllowedTypes")?.Value is ArrayValue { Items: [ObjectValue { Index: < 0 } o, ..] }
                ? package.ResolveName(package.Imports[-o.Index - 1].ObjectName)
                : string.Empty;
            var amount = Member(s, "Amount")?.Value is StructValue a && Member(a, "NoSkill")?.Value is IntValue n ? n.Value : 0;
            var isTool = Member(s, "Purpose")?.Value is EnumValue e && e.Value.EndsWith("Tool", StringComparison.Ordinal);
            return new CraftIngredient(tag, amount, isTool);
        }).ToList();
    }

    /// <summary>The index of the package's recipe export.</summary>
    /// <exception cref="InvalidOperationException">The package holds no crafting recipe.</exception>
    public static int RecipeExport(CookedPackage package)
    {
        var index = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i).EndsWith("CraftingRecipe", StringComparison.Ordinal), -1);
        return index >= 0 ? index : throw new InvalidOperationException($"{package.BasePath}: no crafting recipe.");
    }

    /// <summary>
    /// The recipe without its skin links (<c>CraftingMetadata_Skin_*</c> entries of <c>CraftingMetadata</c>): a copied
    /// recipe would otherwise offer the template's skins, which make the template's product.
    /// </summary>
    private static byte[] DropSkins(CookedPackage package, int exportIndex, byte[] payload)
    {
        if (PropertyReader.ReadPayload(package, payload, exportIndex).Find("CraftingMetadata") is not { Value: ArrayValue list } tag)
        {
            return payload;
        }

        var skins = list.Items.OfType<ObjectValue>()
            .Where(o => o.Index > 0 && package.GetExportClassName(o.Index - 1).StartsWith("CraftingMetadata_Skin", StringComparison.Ordinal))
            .OrderByDescending(o => o.Offset)
            .ToList();
        foreach (var skin in skins)
        {
            payload = [.. payload.AsSpan(0, skin.Offset), .. payload.AsSpan(skin.Offset + skin.Size)];
        }

        if (skins.Count > 0)
        {
            Add(payload, tag.SizeFieldOffset, -4 * skins.Count);
            Add(payload, tag.ValueOffset, -skins.Count);
        }

        return payload;
    }

    private static byte[] SetCategory(CookedPackage package, int exportIndex, byte[] payload, PackageTables tables, CraftCategory section)
    {
        var block = PropertyReader.ReadPayload(package, payload, exportIndex);
        if (block.Find("CraftingCategoryTag")?.Value is not StructValue tag || Member(tag, "TagName") is not { Value: NameValue } tagName)
        {
            throw new InvalidOperationException($"{package.BasePath}: no crafting section tag.");
        }

        WriteName(payload, tagName.ValueOffset, NameOf(tables, section.Tag));
        if (section.Domain is { } domain && block.Find("DisplayDomain") is { Value: NameValue } stored)
        {
            WriteName(payload, stored.ValueOffset, NameOf(tables, domain));
        }

        return payload;
    }

    private static int CategoryIndex(CookedPackage package) =>
        CraftCategories.CategoryExport(package) is >= 0 and var index ? index : throw new InvalidOperationException($"{package.BasePath}: no crafting section.");

    // A name as the engine splits it: "Tag_2" is "Tag" with number 3, the way the game's own packages store it.
    private static FNameRef NameOf(PackageTables tables, string text)
    {
        var underscore = text.LastIndexOf('_');
        var tail = underscore >= 0 ? text[(underscore + 1)..] : string.Empty;
        return tail.Length is > 0 and < 10 && tail.All(char.IsAsciiDigit) && (tail == "0" || tail[0] != '0')
            ? tables.Name(text[..underscore]) with { Number = int.Parse(tail, System.Globalization.CultureInfo.InvariantCulture) + 1 }
            : tables.Name(text);
    }

    private static void WriteName(byte[] payload, int offset, FNameRef name)
    {
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), name.Index);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4), name.Number);
    }

    private static PackageBytes Build(CookedPackage package, PackageTables tables, ReadOnlyMemory<byte>[] data) =>
        PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = tables.Names,
            NameIsWide = tables.Wide,
            Imports = tables.Imports,
            Exports = tables.Exports,
            PreloadDependencies = tables.Preload,
            ExportData = data,
        });

    private static PropertyTag? Member(StructValue value, string name) => value.Properties.FirstOrDefault(p => p.Name == name);

    private static void Add(byte[] buffer, int offset, int delta) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)) + delta);
}
