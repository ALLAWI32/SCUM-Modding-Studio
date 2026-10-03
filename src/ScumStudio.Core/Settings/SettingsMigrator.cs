using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScumStudio.Core.Settings;

/// <summary>
/// Upgrades the JSON of an older <c>settings.json</c> to <see cref="AppSettings.CurrentVersion"/> before it is
/// deserialised. Each step transforms version N to N+1 on the raw JSON object, so renamed properties survive.
/// </summary>
public static class SettingsMigrator
{
    /// <summary>Name of the schema version property in the JSON file.</summary>
    public const string VersionProperty = "version";

    // Step i upgrades version i to i+1.
    private static readonly Action<JsonObject>[] Steps =
    [
        MigrateV0ToV1,
    ];

    /// <summary>
    /// Reads the schema version of <paramref name="root"/>; a missing or non-numeric version counts as 0 (a
    /// hand-written or pre-release file).
    /// </summary>
    public static int ReadVersion(JsonObject root)
    {
        var node = Find(root, VersionProperty);
        return node is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;
    }

    /// <summary>
    /// Migrates <paramref name="root"/> in place. Returns the version the file had before migration. Files from a
    /// newer build are left unchanged (their unknown properties are preserved by <see cref="AppSettings.AdditionalProperties"/>).
    /// </summary>
    public static int Migrate(JsonObject root, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        logger ??= NullLogger.Instance;
        var original = ReadVersion(root);
        if (original > AppSettings.CurrentVersion)
        {
            logger.LogWarning(
                "settings.json has schema version {Version}, newer than this build ({Current}); unknown settings are preserved.",
                original,
                AppSettings.CurrentVersion);
            return original;
        }

        for (var version = Math.Max(original, 0); version < AppSettings.CurrentVersion; version++)
        {
            Steps[version](root);
            SetVersion(root, version + 1);
            logger.LogInformation("Migrated settings.json from version {From} to {To}.", version, version + 1);
        }

        return original;
    }

    /// <summary>
    /// v0 to v1: renames the pre-release keys <c>paksFolder</c> to <c>gamePaksFolder</c>, <c>serverPaks</c> to
    /// <c>serverPaksFolder</c> and <c>modsFolder</c> to <c>clientModsOutputFolder</c> (existing new keys win).
    /// </summary>
    private static void MigrateV0ToV1(JsonObject root)
    {
        Rename(root, "paksFolder", "gamePaksFolder");
        Rename(root, "serverPaks", "serverPaksFolder");
        Rename(root, "modsFolder", "clientModsOutputFolder");
    }

    private static void SetVersion(JsonObject root, int version)
    {
        var existing = FindKey(root, VersionProperty);
        if (existing is not null)
        {
            root.Remove(existing);
        }

        root[VersionProperty] = version;
    }

    private static void Rename(JsonObject root, string oldName, string newName)
    {
        var oldKey = FindKey(root, oldName);
        if (oldKey is null)
        {
            return;
        }

        var value = root[oldKey];
        root.Remove(oldKey);
        if (FindKey(root, newName) is null)
        {
            root[newName] = value;
        }
    }

    private static JsonNode? Find(JsonObject root, string name) => FindKey(root, name) is { } key ? root[key] : null;

    private static string? FindKey(JsonObject root, string name) =>
        root.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
