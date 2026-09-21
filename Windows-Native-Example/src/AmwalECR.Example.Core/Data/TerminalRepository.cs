using System.Text.Json;

namespace AmwalECR.Example.Core.Data;

/// <summary>The outcome of a write that can fail on a duplicate serial number.</summary>
public abstract record SaveResult
{
    private SaveResult()
    {
    }

    public sealed record Saved : SaveResult;

    public sealed record Failed(string Message) : SaveResult;
}

/// <summary>
/// All terminal persistence: a JSON file keyed by serial number
/// (<c>%APPDATA%\AmwalECR-Example\terminals.json</c>), the counterpart of the
/// Android sample's Room table.
/// <para>
/// The file is tiny, so every call reads and writes it whole under one lock. Writes
/// go to a temp file that then replaces the original, so a crash mid-save leaves
/// the previous list rather than half of a new one.
/// </para>
/// </summary>
public sealed class TerminalRepository
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<Terminal> _terminals = new();

    public TerminalRepository(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Raised after any change, on whichever thread made it.</summary>
    public event Action? Changed;

    /// <summary>Set when the file existed but could not be read; it was set aside, not deleted.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Every terminal, ordered by name (case-insensitive) like the Android query.</summary>
    public IReadOnlyList<Terminal> GetAll()
    {
        lock (_gate)
        {
            return Sorted(_terminals);
        }
    }

    public Terminal? FindBySerial(string serialNumber)
    {
        lock (_gate)
        {
            return _terminals.FirstOrDefault(t => t.SerialNumber == serialNumber);
        }
    }

    /// <summary>
    /// Inserts a new terminal, or updates the one identified by
    /// <paramref name="originalSerial"/>. A null <paramref name="originalSerial"/>
    /// means insert. Changing the serial of an existing terminal replaces the
    /// entry, because the serial is the key.
    /// </summary>
    public SaveResult Save(Terminal terminal, string? originalSerial)
    {
        try
        {
            lock (_gate)
            {
                var serialChanged = originalSerial != terminal.SerialNumber;

                if (serialChanged && _terminals.Any(t => t.SerialNumber == terminal.SerialNumber))
                {
                    return new SaveResult.Failed("A terminal with this serial number already exists");
                }

                var next = _terminals.ToList();
                if (originalSerial != null)
                {
                    next.RemoveAll(t => t.SerialNumber == originalSerial);
                }
                next.Add(terminal);
                Write(next);
                _terminals = next;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SaveResult.Failed(string.IsNullOrEmpty(e.Message) ? "Could not save terminal" : e.Message);
        }

        Changed?.Invoke();
        return new SaveResult.Saved();
    }

    /// <summary>Removes a terminal. Returns false (and a reason) when the file could not be written.</summary>
    public SaveResult Delete(string serialNumber)
    {
        try
        {
            lock (_gate)
            {
                var next = _terminals.Where(t => t.SerialNumber != serialNumber).ToList();
                Write(next);
                _terminals = next;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SaveResult.Failed(string.IsNullOrEmpty(e.Message) ? "Could not delete terminal" : e.Message);
        }

        Changed?.Invoke();
        return new SaveResult.Saved();
    }

    private static List<Terminal> Sorted(IEnumerable<Terminal> terminals) =>
        terminals.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.SerialNumber, StringComparer.Ordinal)
            .ToList();

    private void Load()
    {
        if (!File.Exists(_path)) return;

        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize<StoreFile>(stream, Json);
            // Keyed by serial: a hand-edited duplicate keeps the first.
            _terminals = (file?.Terminals ?? new List<Terminal>())
                .Where(t => !string.IsNullOrEmpty(t.SerialNumber))
                .GroupBy(t => t.SerialNumber)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Never silently overwrite what could not be read: keep it beside the
            // new file so the operator can recover the entries by hand.
            var aside = _path + ".unreadable";
            try
            {
                File.Copy(_path, aside, overwrite: true);
            }
            catch (Exception)
            {
                aside = "(could not be copied)";
            }
            LoadWarning = $"The saved terminal list could not be read ({e.Message}). A copy was kept at {aside}.";
            _terminals = new List<Terminal>();
        }
    }

    private void Write(List<Terminal> terminals)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile { Terminals = Sorted(terminals) }, Json));
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }

    private sealed class StoreFile
    {
        public List<Terminal> Terminals { get; set; } = new();
    }
}
