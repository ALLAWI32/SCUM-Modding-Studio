using System.Globalization;
using System.Text;
using ScumStudio.Level.Projects;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Level.Export;

/// <summary>
/// What a refused export leaves in its folder (Discord, O Piqueno, 3.4.0: "the .pak file is not updating"). The package check
/// stopped his export, and the folder kept the earlier export's pak, .sig and export-report.md as if nothing had happened.
/// A refusal now rewrites the report as NOT EXPORTED with the reasons and says that the pak in the folder is the EARLIER
/// export (its name and time) and was NOT updated. The earlier pak and .sig are never renamed, moved or deleted: someone who
/// exports straight into a server's mods folder keeps the mod that works (owner, r38).
/// </summary>
public sealed partial class ProjectExporter
{
    /// <summary>The first line of a refused export's report ends with this; the refusal's message starts with it.</summary>
    public const string NotExported = "NOT EXPORTED";

    /// <summary>Problems a refusal report lists itself (all of them are in <see cref="ModPackageValidator.ListFileName"/>).</summary>
    public const int RefusalProblemsShown = 25;

    /// <summary>
    /// Writes what <paramref name="refusal"/> (the package check stopped the export of <paramref name="modName"/>) means for the
    /// folder: the NOT EXPORTED report at <paramref name="reportPath"/> (where the export's report goes) with the problems, the
    /// warnings so far and the earlier <paramref name="pakPath"/> (its name and time: it was NOT updated). The earlier pak and
    /// its .sig stay exactly as they are. Returns the exception to throw.
    /// </summary>
    internal static ModValidationException WriteRefusal(ModValidationException refusal, string modName, ProjectSourceRole role, string reportPath, string? pakPath,
        IReadOnlyList<string> warnings, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var at = now ?? DateTime.Now;
        var lines = new List<string>();
        if (pakPath is not null)
        {
            var name = Path.GetFileName(pakPath);
            if (File.Exists(pakPath))
            {
                var written = File.GetLastWriteTime(pakPath).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                lines.Add($"The pak in this folder ({name}, written {written}) is from an EARLIER export and was NOT updated: it does not have these edits.");
                var sig = SigCopier.GetSigPath(pakPath);
                if (File.Exists(sig))
                {
                    lines.Add($"Its .sig ({Path.GetFileName(sig)}) is the earlier export's too and was NOT updated.");
                }
            }
            else
            {
                lines.Add($"No {name} is in this folder: no earlier export is here.");
            }
        }

        var problems = refusal.Result.Problems;
        var sb = new StringBuilder();
        sb.Append("# ").Append(modName).Append(" — ").Append(role).Append(" export: ").AppendLine(NotExported);
        sb.AppendLine();
        sb.Append("- Stopped by the package check at ").Append(at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(": ")
            .Append(problems.Count.ToString(CultureInfo.InvariantCulture)).Append(" problem(s) that could crash the game or the server, so ")
            .Append(pakPath is null ? "no pak was written" : Path.GetFileName(pakPath) + " was NOT written").AppendLine(".");
        if (refusal.ListPath is { } list)
        {
            sb.Append("- All of them: ").AppendLine(list);
        }

        foreach (var line in lines)
        {
            sb.Append("- ").AppendLine(line);
        }

        sb.Append("- Fix the edit (or send ").Append(ModPackageValidator.ListFileName).AppendLine(" to the ScumStudio team) and export again.");
        sb.AppendLine();
        sb.Append("## Problems").AppendLine(problems.Count > RefusalProblemsShown ? $" (the first {RefusalProblemsShown} of {problems.Count})" : string.Empty);
        sb.AppendLine();
        foreach (var problem in problems.Take(RefusalProblemsShown))
        {
            sb.Append("- ").AppendLine(problem);
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var warning in warnings)
            {
                sb.Append("- ").AppendLine(warning);
            }
        }

        string? report = reportPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report = null; // the refusal itself still reaches the user
        }

        return new ModValidationException(refusal.Result, refusal.ListPath, pakPath, report, refusal);
    }

    /// <summary>
    /// Deletes <paramref name="reportPath"/> when it is the NOT EXPORTED report of an earlier refusal (a pak with no report of its
    /// own passed this time); any other file stays.
    /// </summary>
    internal static void ClearRefusal(string reportPath)
    {
        try
        {
            if (IsRefusalReport(reportPath))
            {
                File.Delete(reportPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an old refusal report left behind says NOT EXPORTED with its time: harmless
        }
    }

    /// <summary>True when <paramref name="reportPath"/> is a refused export's NOT EXPORTED report.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static bool IsRefusalReport(string reportPath) =>
        File.Exists(reportPath) && File.ReadLines(reportPath).FirstOrDefault() is { } first && first.EndsWith(NotExported, StringComparison.Ordinal);
}
