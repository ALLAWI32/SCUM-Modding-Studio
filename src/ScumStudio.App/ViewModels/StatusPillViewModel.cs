using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;

namespace ScumStudio.App.ViewModels;

/// <summary>Visual state of a status pill.</summary>
public enum PillState
{
    /// <summary>Not configured (grey).</summary>
    Off,

    /// <summary>Working (accent).</summary>
    Busy,

    /// <summary>Good (green).</summary>
    Ok,

    /// <summary>Needs attention (amber).</summary>
    Warn,

    /// <summary>Failed (red).</summary>
    Error,
}

/// <summary>A status pill of the top bar ("Game paks: connected").</summary>
public sealed partial class StatusPillViewModel : ViewModelBase
{
    private readonly string _labelKey;

    /// <summary>Creates a pill whose label is the string <paramref name="labelKey"/> (see <see cref="Loc"/>).</summary>
    public StatusPillViewModel(string labelKey, string iconKey)
    {
        _labelKey = labelKey;
        IconKey = iconKey;
    }

    /// <summary>Label ("Game paks") in the current UI language.</summary>
    public string Label => Loc.T(_labelKey);

    /// <summary>Re-reads <see cref="Label"/> after the UI language changed.</summary>
    public void RefreshLabel() => OnPropertyChanged(nameof(Label));

    /// <summary>Icon resource key.</summary>
    public string IconKey { get; }

    /// <summary>Value ("connected").</summary>
    [ObservableProperty]
    private string _value = Loc.T("Pill.NotSet");

    /// <summary>Tooltip with details.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>State.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsWarn), nameof(IsError), nameof(IsBusy))]
    private PillState _state;

    /// <summary>True for <see cref="PillState.Ok"/>.</summary>
    public bool IsOk => State == PillState.Ok;

    /// <summary>True for <see cref="PillState.Warn"/>.</summary>
    public bool IsWarn => State == PillState.Warn;

    /// <summary>True for <see cref="PillState.Error"/>.</summary>
    public bool IsError => State == PillState.Error;

    /// <summary>True for <see cref="PillState.Busy"/>.</summary>
    public bool IsBusy => State == PillState.Busy;

    /// <summary>Sets value, state and tooltip.</summary>
    public void Set(string value, PillState state, string? detail = null)
    {
        Value = value;
        State = state;
        Detail = detail ?? string.Empty;
    }
}
