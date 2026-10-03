using Microsoft.Extensions.Logging;
using ScumStudio.Core.Logging;

namespace ScumStudio.Cli;

/// <summary>
/// Process-wide services shared by all command modules.
/// </summary>
public static class CliHost
{
    /// <summary>
    /// Environment variable that sets the minimum log level
    /// (Trace, Debug, Information, Warning, Error, Critical, None). Default: Information.
    /// </summary>
    public const string LogLevelVariable = "SCUMSTUDIO_LOG_LEVEL";

    private static readonly Lazy<ILoggerFactory> DefaultFactory = new(() =>
        new TextWriterLoggerFactory(Console.Error, ReadLogLevel()));

    /// <summary>Logger factory writing to standard error. Standard output is reserved for command results.</summary>
    public static ILoggerFactory LoggerFactory => DefaultFactory.Value;

    /// <summary>Creates a logger for <typeparamref name="T"/>.</summary>
    public static ILogger<T> CreateLogger<T>() => new Logger<T>(LoggerFactory);

    private static LogLevel ReadLogLevel()
    {
        var value = Environment.GetEnvironmentVariable(LogLevelVariable);
        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Information;
    }
}
