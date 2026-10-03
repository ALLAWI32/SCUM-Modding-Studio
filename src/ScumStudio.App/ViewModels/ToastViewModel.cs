using System.Windows.Input;
using ScumStudio.App.Services;

namespace ScumStudio.App.ViewModels;

/// <summary>A toast notification.</summary>
public sealed class ToastViewModel : ViewModelBase
{
    /// <summary>Creates a toast.</summary>
    public ToastViewModel(ToastSeverity severity, string title, string message)
    {
        Severity = severity;
        Title = title;
        Message = message;
    }

    /// <summary>Severity.</summary>
    public ToastSeverity Severity { get; }

    /// <summary>Title line.</summary>
    public string Title { get; }

    /// <summary>Detail text (may be empty).</summary>
    public string Message { get; }

    /// <summary>True when <see cref="Message"/> is not empty.</summary>
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>Icon resource key for the severity.</summary>
    public string IconKey => Severity switch
    {
        ToastSeverity.Success => "Icon.Check",
        ToastSeverity.Warning => "Icon.Warning",
        ToastSeverity.Error => "Icon.Error",
        _ => "Icon.Info",
    };

    /// <summary>True for <see cref="ToastSeverity.Success"/>.</summary>
    public bool IsSuccess => Severity == ToastSeverity.Success;

    /// <summary>True for <see cref="ToastSeverity.Warning"/>.</summary>
    public bool IsWarning => Severity == ToastSeverity.Warning;

    /// <summary>True for <see cref="ToastSeverity.Error"/>.</summary>
    public bool IsError => Severity == ToastSeverity.Error;

    /// <summary>True for <see cref="ToastSeverity.Info"/>.</summary>
    public bool IsInfo => Severity == ToastSeverity.Info;

    /// <summary>Closes the toast.</summary>
    public ICommand? CloseCommand { get; internal set; }
}
