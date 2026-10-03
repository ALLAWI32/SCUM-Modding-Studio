using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>A line of the log panel.</summary>
public sealed class LogLineViewModel
{
    /// <summary>Wraps <paramref name="entry"/>.</summary>
    public LogLineViewModel(LogEntry entry) => Entry = entry;

    /// <summary>The entry.</summary>
    public LogEntry Entry { get; }

    /// <summary>Time text.</summary>
    public string Time => Entry.TimeText;

    /// <summary>Level text.</summary>
    public string Level => Entry.LevelText;

    /// <summary>Category.</summary>
    public string Category => Entry.Category;

    /// <summary>Message.</summary>
    public string Message => Entry.Message;

    /// <summary>True for warnings.</summary>
    public bool IsWarning => Entry.Level == LogLevel.Warning;

    /// <summary>True for errors and critical entries.</summary>
    public bool IsError => Entry.Level >= LogLevel.Error;
}

/// <summary>The collapsible log panel: the most recent <see cref="Capacity"/> entries of the app log.</summary>
public sealed partial class LogPanelViewModel : ViewModelBase
{
    /// <summary>Entries kept in memory.</summary>
    public const int Capacity = 2000;

    private readonly IUiDispatcher _dispatcher;

    /// <summary>Creates the panel.</summary>
    public LogPanelViewModel(IUiDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Entries, oldest first.</summary>
    public ObservableCollection<LogLineViewModel> Entries { get; } = [];

    /// <summary>Warnings since the last clear.</summary>
    [ObservableProperty]
    private int _warningCount;

    /// <summary>Errors since the last clear.</summary>
    [ObservableProperty]
    private int _errorCount;

    /// <summary>Most recent entry, for the status bar.</summary>
    [ObservableProperty]
    private LogLineViewModel? _last;

    /// <summary>Adds an entry (thread-safe).</summary>
    public void Add(LogEntry entry)
    {
        var line = new LogLineViewModel(entry);
        _dispatcher.Invoke(() =>
        {
            Entries.Add(line);
            while (Entries.Count > Capacity)
            {
                Entries.RemoveAt(0);
            }

            if (line.IsError)
            {
                ErrorCount++;
            }
            else if (line.IsWarning)
            {
                WarningCount++;
            }

            Last = line;
        });
    }

    /// <summary>Removes every entry.</summary>
    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        WarningCount = 0;
        ErrorCount = 0;
        Last = null;
    }
}
