using AmwalECR.Example.ViewModels;

namespace AmwalECR.Example.Services;

/// <summary>Windows the view-models ask for, without knowing how they are drawn.</summary>
public interface IDialogService
{
    /// <summary>Shows the terminal editor modally. True when the operator saved.</summary>
    bool ShowTerminalEdit(TerminalEditViewModel viewModel);

    /// <summary>Shows a transaction result modally; returns when it is closed.</summary>
    void ShowResult(ResultDialogViewModel viewModel);

        /// <summary>A yes/no question, defaulting to no.</summary>
        bool Confirm(string title, string message);

        /// <summary>A message the operator only has to read.</summary>
        void Alert(string title, string message);
    }

public interface IClipboardService
{
    /// <summary>Puts text on the clipboard. False when Windows refused (another app holds it).</summary>
    bool TryCopy(string text);
}
