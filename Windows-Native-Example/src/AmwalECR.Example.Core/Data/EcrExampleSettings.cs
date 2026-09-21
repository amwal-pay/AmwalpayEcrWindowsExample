using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AmwalPay.Ecr;

namespace AmwalECR.Example.Core.Data;

/// <summary>
/// App-owned signing and Web Service settings.
/// <para>
/// Secrets are stored encrypted (<c>settings.dat</c>, protected by
/// <see cref="ISecretProtector"/>). Persists them locally and exposes
/// <see cref="SecureHashKeyFor"/> so the selected terminal's mode picks one value
/// for <see cref="EcrConfig.SecureHashKey"/>. The ECR SDK does not store or choose
/// keys.
/// </para>
/// </summary>
public sealed class EcrExampleSettings
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();
    private Values _values = new();

    public EcrExampleSettings(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        Load();
    }

    /// <summary>Raised after any setting changes, so a config shown elsewhere can be re-resolved.</summary>
    public event Action? Changed;

    /// <summary>Set when the file existed but could not be decrypted (another user or machine, or damage).</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>The last error from writing the file, or null. Cleared by the next good write.</summary>
    public string? SaveError { get; private set; }

    public EcrEnvironment Environment
    {
        get
        {
            lock (_gate) return EcrEnvironments.FromName(_values.Environment);
        }
        set => Update(v => v.Environment = value.ToString());
    }

    /// <summary>LAN signing key — Wi‑Fi (USB cable and Bluetooth share it).</summary>
    public string WifiSecureHashKey
    {
        get
        {
            lock (_gate) return _values.WifiSecureHashKey;
        }
        set => Update(v => v.WifiSecureHashKey = value.Trim());
    }

    /// <summary>Web Service REST signing key — HMAC over the request's sorted key=value string.</summary>
    public string WebServiceSecureHashKey
    {
        get
        {
            lock (_gate) return _values.WebServiceSecureHashKey;
        }
        set => Update(v => v.WebServiceSecureHashKey = value.Trim());
    }

    /// <summary>Secret to put on <see cref="EcrConfig"/> for the selected terminal mode.</summary>
    public string SecureHashKeyFor(EcrMode mode) => mode switch
    {
        EcrMode.WebService => WebServiceSecureHashKey,
        _ => WifiSecureHashKey,
    };

    private void Update(Action<Values> change)
    {
        lock (_gate)
        {
            var next = _values.Clone();
            change(next);
            _values = next;
            try
            {
                Write(next);
                SaveError = null;
                // The file that could not be read has just been replaced by a good one, so
                // "enter your keys again" no longer applies once the operator has done so.
                LoadWarning = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // The value stays in memory for this run, so the operator can carry
                // on, but say so: a key that silently fails to persist is a
                // support call on the next launch.
                SaveError = $"Could not save settings: {e.Message}";
            }
        }

        Changed?.Invoke();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(_path));
            _values = JsonSerializer.Deserialize<Values>(Encoding.UTF8.GetString(plain), Json) ?? new Values();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // "Account", not "Windows user": a server host protects them with its own key
            // ring, and this text is shown there too.
            LoadWarning = "The saved settings could not be read (they are encrypted for the account that saved " +
                          $"them): {e.Message} Enter the signing keys again.";
            _values = new Values();
        }
    }

    private void Write(Values values)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var bytes = _protector.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values, Json)));
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    private sealed class Values
    {
        public string Environment { get; set; } = EcrEnvironments.Default.ToString();

        public string WifiSecureHashKey { get; set; } = "";

        public string WebServiceSecureHashKey { get; set; } = "";

        public Values Clone() => (Values)MemberwiseClone();
    }
}
