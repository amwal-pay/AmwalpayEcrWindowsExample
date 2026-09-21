using System.Windows;
using System.Windows.Threading;
using AmwalECR.Example.Core.Logging;
using AmwalECR.Example.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// The Debug output panel: the SDK's diagnostics, last 500 lines — this sample's
/// Logcat. It is how a tester sees the wire traffic.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private readonly LogBuffer _buffer;
    private readonly IClipboardService _clipboard;
    private readonly Dispatcher _dispatcher;
    private int _refreshQueued;

    public LogViewModel(LogBuffer buffer, IClipboardService clipboard)
    {
        _buffer = buffer;
        _clipboard = clipboard;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _buffer.Changed += QueueRefresh;
        Refresh();
    }

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _summary = "Debug output";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isExpanded;

    public int Capacity => _buffer.Capacity;

    /// <summary>The SDK logs from whichever thread its I/O lands on; the text box may only be touched from the UI thread.</summary>
    private void QueueRefresh()
    {
        // Coalesced: a signing exchange logs a dozen lines at once.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        }));
    }

    private void Refresh()
    {
        Text = _buffer.Snapshot();
        Summary = $"Debug output ({_buffer.Count} lines)";
    }

    [RelayCommand]
    private void Copy()
    {
        Status = _clipboard.TryCopy(Text) ? "Copied to clipboard" : "Could not copy: the clipboard is in use";
    }

    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        Status = "";
    }
}
