using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Settings;
using ScumStudio.Mcp.Protocol;
using ScumStudio.Mcp.Studio;
using ScumStudio.Mcp.Transports;

namespace ScumStudio.App.Services;

/// <summary>
/// The editor's built-in MCP server ("AI control"): Streamable HTTP on 127.0.0.1 with a bearer token, serving the
/// <see cref="StudioTools"/> over <see cref="AppStudioHost"/> so an AI assistant (Claude Code, Claude Desktop through
/// <c>scumstudio mcp --connect</c>, …) can drive the open editor. Off until enabled in Settings (or with
/// <c>--mcp-port</c>). The AES key is never exposed: no tool takes or returns it.
/// </summary>
public sealed partial class AppMcpService : ObservableObject, IDisposable
{
    /// <summary>Name the server is registered under in AI clients.</summary>
    public const string ServerName = "scumstudio";

    private readonly AppServices _services;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpServer? _server;
    private McpHttpServer? _http;
    private (int Port, string Token)? _runningWith;

    /// <summary>Creates the service (stopped).</summary>
    public AppMcpService(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = services.LoggerFactory.CreateLogger<AppMcpService>();
        Host = new AppStudioHost(services, () => (Host?.Ui as AppStudioUi)?.MapIfCreated);
        Host.Activity += OnActivity;
    }

    /// <summary>Raised on the UI thread when <see cref="IsRunning"/>, <see cref="StatusText"/> or the activity changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>What the tools drive.</summary>
    public AppStudioHost Host { get; }

    /// <summary>Port from the command line (<c>--mcp-port</c>): forces the server on for this run.</summary>
    public int? PortOverride { get; set; }

    /// <summary>Token from the command line (<c>--mcp-token</c>) used instead of the stored one for this run.</summary>
    public string? TokenOverride { get; set; }

    /// <summary>True while the HTTP endpoint accepts connections.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>Endpoint URL while running (<c>http://127.0.0.1:47130/mcp</c>), else empty.</summary>
    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>"Off", "Listening on …", or the start error.</summary>
    [ObservableProperty]
    private string _statusText = Localization.Loc.T("Mcp.Off");

    /// <summary>The last start error, or null.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>Tool calls served since start-up.</summary>
    [ObservableProperty]
    private int _callCount;

    /// <summary>"14:03 delete_actors — Deleted 1 actor", or empty.</summary>
    [ObservableProperty]
    private string _lastActivity = string.Empty;

    /// <summary>Name of the last AI client that connected ("claude-code 2.1"), or empty.</summary>
    [ObservableProperty]
    private string _clientName = string.Empty;

    /// <summary>The token clients must send (stored in settings, or <see cref="TokenOverride"/>); null before the first start.</summary>
    public string? Token => TokenOverride ?? _services.Settings.Load().Mcp.Token;

    /// <summary>Port in use, or the configured one.</summary>
    public int Port => _http?.Port ?? PortOverride ?? _services.Settings.Load().Mcp.Port;

    /// <summary>Endpoint URL for <see cref="Port"/> (also when stopped).</summary>
    public string EndpointUrl => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/mcp");

    /// <summary>Lets the tools drive the main window (pages, 3D viewport, screenshots). Call before starting.</summary>
    public void AttachShell(MainWindowViewModel shell, Func<TopLevel?> window) =>
        Host.Ui = new AppStudioUi(_services, shell, window);

    /// <summary>Starts, restarts or stops the server to match the settings and command-line overrides. Never throws.</summary>
    public async Task ApplySettingsAsync()
    {
        var settings = _services.Settings.Load().Mcp;
        var wanted = PortOverride is not null || settings.Enabled;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!wanted)
            {
                await StopCoreAsync().ConfigureAwait(false);
                Publish(Localization.Loc.T("Mcp.Off"), null);
                return;
            }

            var token = TokenOverride ?? settings.Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = McpHttpServerOptions.NewToken();
                var generated = token;
                _services.UpdateSettings(s => s with { Mcp = s.Mcp with { Token = generated } });
            }

            var port = PortOverride ?? settings.Port;
            if (_http is not null && _runningWith == (port, token))
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            var server = StudioTools.CreateServer(Host, _services.LoggerFactory.CreateLogger("ScumStudio.Mcp"));
            var http = new McpHttpServer(server, new McpHttpServerOptions { Port = port, Token = token }, _services.LoggerFactory.CreateLogger<McpHttpServer>());
            try
            {
                http.Start();
            }
            catch (SocketException ex)
            {
                await http.DisposeAsync().ConfigureAwait(false);
                var message = Localization.Loc.F("Mcp.PortBusy", port.ToString(CultureInfo.InvariantCulture), ex.SocketErrorCode);
                _logger.LogWarning("MCP server not started: {Message}", message);
                Publish(Localization.Loc.F("Mcp.NotRunning", message), message);
                _services.Notifications.Warning(Localization.Loc.T("Mcp.NotStarted"), message);
                return;
            }

            _server = server;
            _http = http;
            _runningWith = (port, token);
            _logger.LogInformation("MCP server listening on {Url} ({Count} tools).", http.Url, server.Tools.Count);
            Publish(Localization.Loc.F("Mcp.Listening", http.Url, server.Tools.Count)
                    + (PortOverride is not null ? Localization.Loc.T("Mcp.StartedWithPort") : string.Empty), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the server (settings unchanged).</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            Publish(Localization.Loc.T("Mcp.Off"), null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tools served (empty while stopped).</summary>
    public IReadOnlyList<McpTool> Tools => _server?.Tools.ToList() ?? [];

    /// <summary>Shell command that registers the running editor with Claude Code.</summary>
    public string ClaudeCodeCommand() =>
        $"claude mcp add --transport http {ServerName} {EndpointUrl} --header \"Authorization: Bearer {Token ?? "<token>"}\"";

    /// <summary>
    /// <c>claude_desktop_config.json</c> entry: Claude Desktop starts <c>scumstudio mcp --connect</c>, a stdio bridge to
    /// the running editor (the token travels in the bridge's environment, not on its command line).
    /// </summary>
    public string ClaudeDesktopConfig()
    {
        var cli = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "scumstudio.exe" : "scumstudio");
        var entry = new JsonObject
        {
            ["command"] = File.Exists(cli) ? cli : "scumstudio",
            ["args"] = new JsonArray("mcp", "--connect", EndpointUrl),
            ["env"] = new JsonObject { ["SCUMSTUDIO_MCP_TOKEN"] = Token ?? "<token>" },
        };
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = entry } };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Host.Activity -= OnActivity;
        var http = _http;
        _http = null;
        _server = null;
        if (http is not null)
        {
            // Bounded: never hang the exit on a stuck client.
            Task.Run(() => http.StopAsync()).Wait(TimeSpan.FromSeconds(3));
        }

        _gate.Dispose();
    }

    private async Task StopCoreAsync()
    {
        var http = _http;
        _http = null;
        _server = null;
        _runningWith = null;
        if (http is not null)
        {
            await http.StopAsync().ConfigureAwait(false);
            _logger.LogInformation("MCP server stopped.");
        }
    }

    private void Publish(string status, string? error) => _services.Dispatcher.Invoke(() =>
    {
        IsRunning = _http?.IsRunning == true;
        Url = _http?.Url ?? string.Empty;
        StatusText = status;
        Error = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    });

    private void OnActivity(object? sender, StudioActivity activity)
    {
        var tool = _server?.Tools.FirstOrDefault(t => t.Name == activity.Tool);
        var client = _http?.Sessions.Where(s => s.ClientName is not null).OrderByDescending(s => s.LastSeen).FirstOrDefault();
        _services.Dispatcher.Invoke(() =>
        {
            CallCount++;
            LastActivity = string.Create(CultureInfo.InvariantCulture, $"{activity.At:HH:mm:ss} {activity.Tool} — {FirstLine(activity.Summary)}");
            if (client is not null)
            {
                ClientName = client.ClientName + (client.ClientVersion is { } v ? " " + v : string.Empty);
            }

            // The full stream goes to the Console; only edits raise a toast, and a burst of them is one toast.
            if (activity.IsError)
            {
                _logger.LogWarning("AI {Tool} failed: {Summary}", activity.Tool, FirstLine(activity.Summary));
            }
            else
            {
                _logger.LogInformation("AI {Tool}: {Summary}", activity.Tool, FirstLine(activity.Summary));
            }

            if (!activity.IsError && tool is { ReadOnly: false } && tool.Name is not ("navigate" or "show_levels" or "select_actor" or "set_camera" or "show_item"))
            {
                _services.Notifications.ShowCoalesced("mcp", ToastSeverity.Info, Localization.Loc.F("Mcp.Toast", tool.Title ?? tool.Name), FirstLine(activity.Summary));
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 160 ? line[..157] + "…" : line;
    }
}
