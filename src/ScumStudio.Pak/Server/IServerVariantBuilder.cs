namespace ScumStudio.Pak.Server;

/// <summary>
/// Turns a client mod build tree into its dedicated-server variant (the server cook differs for some asset types).
/// </summary>
/// <remarks>
/// Current rule set (tools/server_variant.py): every AkAudioEvent package is replaced by a renamed copy of a server-cooked
/// 12-byte stub; everything else is shipped as-is. Future rules (e.g. server-cooked umaps) plug in as further implementations.
/// </remarks>
public interface IServerVariantBuilder
{
    /// <summary>
    /// Applies the server rules in place to a staging directory laid out as a project root (<c>&lt;dir&gt;/SCUM/Content/...</c>).
    /// </summary>
    /// <returns>Game paths (<c>/Game/...</c>) of the packages that were replaced, sorted.</returns>
    Task<IReadOnlyList<string>> ApplyInPlaceAsync(string stagingDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the <c>SCUM/</c> tree of <paramref name="buildDirectory"/> to <paramref name="outputDirectory"/> (replacing it)
    /// and applies the server rules to the copy (port of server_variant.py <c>make_server_tree</c>).
    /// </summary>
    /// <returns>Game paths of the packages that were replaced, sorted.</returns>
    Task<IReadOnlyList<string>> BuildAsync(string buildDirectory, string outputDirectory, CancellationToken cancellationToken = default);
}
