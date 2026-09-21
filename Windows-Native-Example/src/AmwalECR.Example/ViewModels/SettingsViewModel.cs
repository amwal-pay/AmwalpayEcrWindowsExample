using AmwalECR.Example.Core.Data;
using AmwalPay.Ecr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// The environment and both signing keys — the port of the Android
/// <c>EnvironmentSelector</c>. Every change is saved at once, trimmed, like the
/// Android sample; there is no Save button to forget.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly EcrExampleSettings _settings;

    public SettingsViewModel(EcrExampleSettings settings)
    {
        _settings = settings;
        _wifiSecureHashKey = settings.WifiSecureHashKey;
        _webServiceSecureHashKey = settings.WebServiceSecureHashKey;
        _notice = settings.LoadWarning ?? "";
    }

    public EcrEnvironment Environment
    {
        get => _settings.Environment;
        set
        {
            if (_settings.Environment == value) return;
            _settings.Environment = value;
            OnPropertyChanged();
            RefreshNotice();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiKeyError))]
    [NotifyPropertyChangedFor(nameof(HasWifiKeyError))]
    private string _wifiSecureHashKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebServiceKeyError))]
    [NotifyPropertyChangedFor(nameof(HasWebServiceKeyError))]
    private string _webServiceSecureHashKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiKeyToggleText))]
    private bool _showWifiKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebServiceKeyToggleText))]
    private bool _showWebServiceKey;

    /// <summary>A settings file that could not be read, or written; empty when all is well.</summary>
    [ObservableProperty]
    private string _notice;

    public string WifiKeyToggleText => ShowWifiKey ? "Hide" : "Show";

    public string WebServiceKeyToggleText => ShowWebServiceKey ? "Hide" : "Show";

    [RelayCommand]
    private void ToggleWifiKey() => ShowWifiKey = !ShowWifiKey;

    [RelayCommand]
    private void ToggleWebServiceKey() => ShowWebServiceKey = !ShowWebServiceKey;

    public string WifiKeyError => ErrorFor(WifiSecureHashKey);

    public bool HasWifiKeyError => WifiKeyError.Length != 0;

    public string WebServiceKeyError => ErrorFor(WebServiceSecureHashKey);

    public bool HasWebServiceKeyError => WebServiceKeyError.Length != 0;

    partial void OnWifiSecureHashKeyChanged(string value)
    {
        _settings.WifiSecureHashKey = value;
        RefreshNotice();
    }

    partial void OnWebServiceSecureHashKeyChanged(string value)
    {
        _settings.WebServiceSecureHashKey = value;
        RefreshNotice();
    }

    private void RefreshNotice() => Notice = _settings.SaveError ?? _settings.LoadWarning ?? "";

    private static string ErrorFor(string key) =>
        key.Length != 0 && !EcrConfig.IsValidSecureHashKey(key.Trim())
            ? "Must be an even-length hex string of at least 16 characters"
            : "";
}
