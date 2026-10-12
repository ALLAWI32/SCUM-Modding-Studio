using System.CommandLine;
using System.CommandLine.Invocation;
using ScumStudio.Core.Sharing;

namespace ScumStudio.Cli.Commands;

/// <summary>
/// <c>scumstudio modshare ...</c>: the owner's moderator key for the mod gallery (see <see cref="ModeratorKey"/>).
/// <list type="bullet">
/// <item><c>modshare keygen [--out &lt;folder&gt;] [--force]</c> - writes a new <c>moderator.key</c> and prints its public key for the Worker.</item>
/// <item><c>modshare pubkey [--key &lt;file&gt;]</c> - prints the public key of an existing key file.</item>
/// </list>
/// The private key is never printed.
/// </summary>
internal sealed class ModShareCommands : ICommandModule
{
    /// <inheritdoc />
    public Command Build()
    {
        var modshare = new Command("modshare", "The mod gallery's moderator key (Share your mod).");
        modshare.AddCommand(BuildKeygen());
        modshare.AddCommand(BuildPubkey());
        return modshare;
    }

    private static Command BuildKeygen()
    {
        var output = new Option<string?>("--out", "Folder for moderator.key (default: Desktop\\ScumStudio-ModShare, or the folder of SCUMSTUDIO_MODSHARE_KEY).");
        var force = new Option<bool>("--force", "Replace an existing key. The Worker then needs the new public key before removals work again.");
        var cmd = new Command("keygen", "Create the moderator key (ECDSA P-256) that signs removals from the mod gallery. Keep it only on your PC.")
        {
            output, force,
        };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = PkgCommands.Run(() =>
            {
                var folder = ctx.ParseResult.GetValueForOption(output);
                var path = Path.GetFullPath(folder is { Length: > 0 } ? Path.Combine(folder, ModeratorKey.FileName) : ModeratorKey.DefaultPath);
                if (File.Exists(path) && !ctx.ParseResult.GetValueForOption(force))
                {
                    Console.Error.WriteLine($"error: {path} already exists. Pass --force to replace it (removals then need the new public key on the Worker).");
                    return 1;
                }

                var publicKey = ModeratorKey.GenerateFile(path, overwrite: true);
                Console.WriteLine($"Moderator key written: {path}");
                Console.WriteLine("Keep this file only on this PC and never share it: whoever has it can remove mods from the gallery.");
                Console.WriteLine();
                Console.WriteLine("Public key (for the Worker):");
                Console.WriteLine(publicKey);
                Console.WriteLine();
                Console.WriteLine("Set it on the Worker (paste the public key when asked):");
                Console.WriteLine("npx wrangler secret put MODERATOR_PUBLIC_KEY");
                return 0;
            });
        });
        return cmd;
    }

    private static Command BuildPubkey()
    {
        var keyFile = new Option<string?>("--key", "The key file (default: Desktop\\ScumStudio-ModShare\\moderator.key, or SCUMSTUDIO_MODSHARE_KEY).");
        var cmd = new Command("pubkey", "Print the public key of the moderator key, for the Worker's MODERATOR_PUBLIC_KEY.") { keyFile };
        cmd.SetHandler((InvocationContext ctx) =>
        {
            ctx.ExitCode = PkgCommands.Run(() =>
            {
                var path = Path.GetFullPath(ctx.ParseResult.GetValueForOption(keyFile) is { Length: > 0 } given ? given : ModeratorKey.DefaultPath);
                using var key = ModeratorKey.TryLoad(path);
                if (key is null)
                {
                    Console.Error.WriteLine($"error: no moderator key at {path} (create one with: scumstudio modshare keygen).");
                    return 1;
                }

                Console.WriteLine(key.PublicSpkiBase64);
                return 0;
            });
        });
        return cmd;
    }
}
