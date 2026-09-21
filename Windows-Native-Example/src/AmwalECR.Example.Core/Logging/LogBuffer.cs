using System.Text;

namespace AmwalECR.Example.Core.Logging;

/// <summary>
/// The last <see cref="Capacity"/> lines of SDK diagnostics — the Windows
/// equivalent of the Android sample's Logcat output. Safe to write to from any
/// thread: the SDK logs from whichever thread its I/O continuation lands on.
/// </summary>
public sealed class LogBuffer
{
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly LinkedList<string> _lines = new();
    private readonly Action<string>? _mirror;

    /// <param name="mirror">Also receives each line, e.g. <c>Debug.WriteLine</c>.</param>
    public LogBuffer(int capacity = DefaultCapacity, Action<string>? mirror = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _mirror = mirror;
    }

    public int Capacity { get; }

    /// <summary>Raised after a line is added or the buffer is cleared, on the writing thread.</summary>
    public event Action? Changed;

    public int Count
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }

    public void Append(string tag, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {tag} {message}";
        lock (_gate)
        {
            _lines.AddLast(line);
            while (_lines.Count > Capacity) _lines.RemoveFirst();
        }

        try
        {
            _mirror?.Invoke(line);
        }
        catch (Exception)
        {
            // A broken mirror must never break a payment flow.
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
        Changed?.Invoke();
    }

    public string Snapshot()
    {
        lock (_gate)
        {
            var builder = new StringBuilder();
            foreach (var line in _lines) builder.AppendLine(line);
            return builder.ToString();
        }
    }
}
