using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// The terminals list with the environment and signing keys — the port of
/// <c>TerminalsScreen</c> and <c>TerminalsViewModel</c>. Add and edit open the
/// editor as a dialog; delete asks first.
/// </summary>
public sealed partial class TerminalsViewModel : ObservableObject
{
    private readonly TerminalRepository _repository;
    private readonly IDialogService _dialogs;

    public TerminalsViewModel(TerminalRepository repository, SettingsViewModel settings, IDialogService dialogs)
    {
        _repository = repository;
        _dialogs = dialogs;
        Settings = settings;
        _terminals = repository.GetAll();
        _notice = repository.LoadWarning ?? "";
        repository.Changed += OnRepositoryChanged;
    }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTerminals), nameof(IsEmpty))]
    private IReadOnlyList<Terminal> _terminals;

    /// <summary>A terminal list that could not be read, or a save that failed; empty when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice;

    public bool HasTerminals => Terminals.Count != 0;

    public bool IsEmpty => Terminals.Count == 0;

    public bool HasNotice => Notice.Length != 0;

    private void OnRepositoryChanged() => Terminals = _repository.GetAll();

    [RelayCommand]
    private void Add() => _dialogs.ShowTerminalEdit(new TerminalEditViewModel(_repository, existing: null));

    [RelayCommand]
    private void Edit(Terminal? terminal)
    {
        if (terminal == null) return;
        _dialogs.ShowTerminalEdit(new TerminalEditViewModel(_repository, terminal));
    }

    [RelayCommand]
    private void Delete(Terminal? terminal)
    {
        if (terminal == null) return;
        if (!_dialogs.Confirm("Delete terminal", $"Delete “{terminal.Name}”? This cannot be undone.")) return;

        Notice = _repository.Delete(terminal.SerialNumber) is SaveResult.Failed failed
            ? $"Could not delete the terminal: {failed.Message}"
            : "";
    }
}
