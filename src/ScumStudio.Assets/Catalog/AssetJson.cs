using Newtonsoft.Json;

namespace ScumStudio.Assets.Catalog;

/// <summary>JSON dumps of CUE4Parse objects (exports, packages) using CUE4Parse's own Newtonsoft converters.</summary>
public static class AssetJson
{
    /// <summary>Serialises <paramref name="value"/> (an export, a package's export list, ...) to indented JSON.</summary>
    public static string Serialize(object? value) =>
        JsonConvert.SerializeObject(value, Formatting.Indented, new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
        });
}
