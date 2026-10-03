using Microsoft.Extensions.Logging;

namespace ScumStudio.Core.Logging;

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> that writes one line per message to a <see cref="TextWriter"/>
/// (typically <see cref="Console.Error"/>). Used by the CLI and tests so no logging provider package is needed;
/// the App may plug in its own provider. Library code only depends on <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// Never log secret material (AES keys). Format: <c>HH:mm:ss.fff LVL Category: message</c>.
/// </remarks>
public sealed class TextWriterLoggerFactory : ILoggerFactory
{
    private readonly TextWriter _writer;
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();

    /// <summary>Creates a factory writing to <paramref name="writer"/> at or above <paramref name="minimumLevel"/>.</summary>
    public TextWriterLoggerFactory(TextWriter writer, LogLevel minimumLevel = LogLevel.Information)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _minimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new TextWriterLogger(this, categoryName);

    /// <summary>Not supported: this factory has a single fixed sink. Calls are ignored.</summary>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public void Dispose() => _writer.Flush();

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "   ",
    };

    private sealed class TextWriterLogger(TextWriterLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss.fff} {LevelTag(logLevel)} {category}: {formatter(state, exception)}";
            lock (owner._gate)
            {
                owner._writer.WriteLine(line);
                if (exception is not null)
                {
                    owner._writer.WriteLine(exception.ToString());
                }
            }
        }
    }
}
