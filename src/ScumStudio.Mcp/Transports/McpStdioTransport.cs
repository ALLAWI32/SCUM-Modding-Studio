using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Mcp.Protocol;

namespace ScumStudio.Mcp.Transports;

/// <summary>
/// MCP stdio transport: newline-delimited JSON-RPC messages on standard input, responses on standard output (UTF-8,
/// no BOM, one message per line). Everything else (logs) must go to standard error. Messages are handled concurrently so
/// <c>ping</c> and cancellation are answered while a tool runs; the server itself runs tools one at a time.
/// </summary>
public static class McpStdioTransport
{
    /// <summary>Serves <paramref name="server"/> until the input ends or <paramref name="cancellationToken"/> fires.</summary>
    public static async Task RunAsync(McpServer server, Stream input, Stream output, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        logger ??= NullLogger.Instance;
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(input, utf8, detectEncodingFromByteOrderMarks: false);
        await using var writer = new StreamWriter(output, utf8) { AutoFlush = false, NewLine = "\n" };
        using var writeLock = new SemaphoreSlim(1, 1);
        var session = new McpSession();
        var pending = new List<Task>();

        async Task WriteAsync(string line)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await writer.WriteAsync("\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var message = line;
            var isInitialize = message.Contains("\"initialize\"", StringComparison.Ordinal);
            var task = Task.Run(async () =>
            {
                try
                {
                    if (await server.HandleMessageAsync(message, session, cancellationToken).ConfigureAwait(false) is { } response)
                    {
                        await WriteAsync(response).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    logger.LogError(ex, "MCP stdio message failed.");
                }
            }, CancellationToken.None);
            if (isInitialize)
            {
                // The handshake completes before anything else is read, so later requests see the negotiated session.
                await task.ConfigureAwait(false);
                continue;
            }

            pending.Add(task);
            pending.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }
}
