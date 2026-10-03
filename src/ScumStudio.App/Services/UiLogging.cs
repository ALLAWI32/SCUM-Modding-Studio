using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ScumStudio.App.Services;

/// <summary>One formatted log line.</summary>
/// <param name="Time">Local time of the event.</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">Logger category (shortened type name).</param>
/// <param name="Message">Formatted message (plus the exception message when there is one).</param>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Category, string Message)
{
    /// <summary>Short level label (<c>INF</c>, <c>WRN</c>, ...).</summary>
    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    /// <summary><c>HH:mm:ss</c>.</summary>
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>One-line rendering used by the log file and console.</summary>
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss.fff} {LevelText} [{Category}] {Message}";
}

/// <summary>
/// <see cref="ILoggerProvider"/> feeding the log panel, the console and an optional rolling log file
/// (<c>logs/scumstudio.log</c> in the data folder, 2 MB, one backup). Messages are formatted by the caller's
/// structured-logging template; ScumStudio never passes key material to a logger.
/// </summary>
public sealed class UiLoggerProvider : ILoggerProvider
{
    /// <summary>Maximum log file size before it is rotated to <c>.1</c>.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    private readonly Action<LogEntry> _sink;
    private readonly string? _filePath;
    private readonly bool _console;
    private readonly LogLevel _minimum;
    private readonly object _fileGate = new();

    /// <summary>Creates the provider.</summary>
    /// <param name="sink">Receives every entry (from any thread).</param>
    /// <param name="filePath">Log file, or null for none.</param>
    /// <param name="console">Also write to standard output.</param>
    /// <param name="minimum">Minimum level.</param>
    public UiLoggerProvider(Action<LogEntry> sink, string? filePath, bool console, LogLevel minimum = LogLevel.Information)
    {
        _sink = sink;
        _filePath = filePath;
        _console = console;
        _minimum = minimum;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new UiLogger(this, ShortCategory(categoryName));

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary><c>ScumStudio.App.Services.GameWorkspace</c> becomes <c>GameWorkspace</c>.</summary>
    public static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private void Write(LogEntry entry)
    {
        try
        {
            _sink(entry);
        }
        catch (Exception)
        {
            // The log sink must never break the caller.
        }

        if (_console)
        {
            try
            {
                Console.Out.WriteLine(entry.ToString());
            }
            catch (IOException)
            {
            }
        }

        if (_filePath is null)
        {
            return;
        }

        lock (_fileGate)
        {
            try
            {
                var info = new FileInfo(_filePath);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    File.Move(_filePath, _filePath + ".1", overwrite: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.AppendAllText(_filePath, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging to disk is best effort.
            }
        }
    }

    private sealed class UiLogger(UiLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null && !message.Contains(exception.Message, StringComparison.Ordinal))
            {
                message += " (" + exception.GetType().Name + ": " + exception.Message + ")";
            }

            owner.Write(new LogEntry(DateTime.Now, logLevel, category, message));
        }
    }
}

/// <summary>Minimal <see cref="ILoggerFactory"/> over one <see cref="ILoggerProvider"/>.</summary>
public sealed class SingleProviderLoggerFactory(ILoggerProvider provider) : ILoggerFactory
{
    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();
}
