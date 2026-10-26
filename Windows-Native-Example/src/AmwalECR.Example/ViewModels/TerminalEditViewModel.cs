using System.Collections.ObjectModel;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Terminals;
using AmwalPay.Ecr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>One choice in the ECR mode radio group.</summary>
public sealed class ModeOptionViewModel : ObservableObject
{
    private readonly TerminalEditViewModel _owner;

    public ModeOptionViewModel(TerminalEditViewModel owner, EcrMode mode)
    {
        _owner = owner;
        Mode = mode;
    }

    public EcrMode Mode { get; }

    public string Label => Mode.Label();

    /// <summary>Why the mode cannot run transactions in this sample, or empty.</summary>
    public string Hint => Mode.IsSupportedInExample()
        ? ""
        : "Not available in this sample — Bluetooth has no channel.";

    public bool HasHint => Hint.Length != 0;

    public bool IsSelected
    {
        get => _owner.Mode == Mode;
        set
        {
            if (value) _owner.Mode = Mode;
        }
    }

    internal void NotifySelectionChanged() => OnPropertyChanged(nameof(IsSelected));
}

/// <summary>
/// Add or edit one terminal — the port of <c>TerminalEditScreen</c>. Validation is
/// <see cref="TerminalValidator"/>; the serial is the terminal's identity, so
/// changing it replaces the entry and a duplicate is refused.
/// </summary>
public sealed partial class TerminalEditViewModel : ObservableObject
{
    private readonly TerminalRepository _repository;
    private readonly string? _originalSerial;

    public TerminalEditViewModel(TerminalRepository repository, Terminal? existing)
    {
        _repository = repository;
        _originalSerial = existing?.SerialNumber;

        _name = existing?.Name ?? "";
        _serial = existing?.SerialNumber ?? "";
        _mode = existing?.Mode ?? EcrModes.Default;
        _ip = existing?.IpAddress ?? "";
        _port = existing is { Port: > 0 } ? existing.Port.ToString() : existing == null ? TerminalValidator.DefaultPort : "";
        _terminalId = existing?.TerminalId ?? "";
        _merchantId = existing?.MerchantId ?? "";
        _comPort = existing?.ComPort ?? "";

        ModeOptions = new ObservableCollection<ModeOptionViewModel>(
            EcrModes.All.Select(m => new ModeOptionViewModel(this, m)));
        ComPorts = new ObservableCollection<SerialPortChoice>();
        ReloadComPorts();
    }

    /// <summary>Raised with true once saved, false when cancelled.</summary>
    public event Action<bool>? CloseRequested;

    public bool IsNew => _originalSerial == null;

    public string Title => IsNew ? "Add terminal" : "Terminal";

    public ObservableCollection<ModeOptionViewModel> ModeOptions { get; }

    public ObservableCollection<SerialPortChoice> ComPorts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameError), nameof(HasNameError))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SerialError), nameof(HasSerialError))]
    private string _serial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWifi), nameof(IsWebService), nameof(IsUsbCable), nameof(IsBluetooth))]
    private EcrMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IpError), nameof(HasIpError))]
    private string _ip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortError), nameof(HasPortError))]
    private string _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TerminalIdError), nameof(HasTerminalIdError))]
    private string _terminalId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MerchantIdError), nameof(HasMerchantIdError))]
    private string _merchantId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComPortError), nameof(HasComPortError))]
    private string _comPort;

    private TerminalErrors _errors = new();
    private string? _saveError;

    public bool IsWifi => Mode == EcrMode.Wifi;

    public bool IsWebService => Mode == EcrMode.WebService;

    public bool IsUsbCable => Mode == EcrMode.UsbCable;

    public bool IsBluetooth => Mode == EcrMode.Bluetooth;

    public string? NameError => _errors.Name;

    public string? SerialError => _errors.Serial ?? _saveError;

    public string? IpError => _errors.Ip;

    public string? PortError => _errors.Port;

    public string? TerminalIdError => _errors.TerminalId;

    public string? MerchantIdError => _errors.MerchantId;

    public string? ComPortError => _errors.ComPort;

    public bool HasNameError => NameError != null;

    public bool HasSerialError => SerialError != null;

    public bool HasIpError => IpError != null;

    public bool HasPortError => PortError != null;

    public bool HasTerminalIdError => TerminalIdError != null;

    public bool HasMerchantIdError => MerchantIdError != null;

    public bool HasComPortError => ComPortError != null;

    partial void OnModeChanged(EcrMode value)
    {
        foreach (var option in ModeOptions) option.NotifySelectionChanged();
        if (value == EcrMode.UsbCable) ReloadComPorts();
    }

    [RelayCommand]
    private void Save()
    {
        _saveError = null;
        _errors = TerminalValidator.Validate(Name, Serial, Mode, Ip, Port, TerminalId, MerchantId, ComPort);
        NotifyErrors();
        if (_errors.Any) return;

        var terminal = TerminalValidator.ToTerminal(Name, Serial, Mode, Ip, Port, TerminalId, MerchantId, ComPort);
        switch (_repository.Save(terminal, _originalSerial))
        {
            case SaveResult.Saved:
                CloseRequested?.Invoke(true);
                break;
            case SaveResult.Failed failed:
                // A duplicate serial is shown against the serial field, where it can be fixed.
                _saveError = failed.Message;
                NotifyErrors();
                break;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    partial void OnSerialChanged(string value)
    {
        if (Mode != EcrMode.UsbCable) return;
        var serial = (value ?? "").Trim();
        if (serial.Length == 0) return;
        foreach (var port in ComPorts)
        {
            if (port.DeviceSerial.Length != 0 && string.Equals(port.DeviceSerial, serial, StringComparison.OrdinalIgnoreCase))
            {
                ComPort = port.Selection;
                return;
            }
        }
    }

    [RelayCommand]
    private void ReloadComPorts()
    {
        var listed = SerialPorts.ListFor(ComPort, Serial);
        ComPorts.Clear();
        foreach (var port in listed.Ports) ComPorts.Add(port);
        ComPort = listed.Selected;
    }

    private void NotifyErrors()
    {
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(HasNameError));
        OnPropertyChanged(nameof(SerialError));
        OnPropertyChanged(nameof(HasSerialError));
        OnPropertyChanged(nameof(IpError));
        OnPropertyChanged(nameof(HasIpError));
        OnPropertyChanged(nameof(PortError));
        OnPropertyChanged(nameof(HasPortError));
        OnPropertyChanged(nameof(TerminalIdError));
        OnPropertyChanged(nameof(HasTerminalIdError));
        OnPropertyChanged(nameof(MerchantIdError));
        OnPropertyChanged(nameof(HasMerchantIdError));
        OnPropertyChanged(nameof(ComPortError));
        OnPropertyChanged(nameof(HasComPortError));
    }
}
