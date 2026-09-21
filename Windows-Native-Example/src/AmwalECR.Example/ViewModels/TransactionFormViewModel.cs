using AmwalECR.Example.Core.Transaction;
using AmwalPay.Ecr;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmwalECR.Example.ViewModels;

/// <summary>
/// Bindable face of <see cref="TransactionFormState"/>. All the rules — which
/// fields exist, what is required, what is valid — stay in that immutable record;
/// this only forwards edits into it and tells the view to re-read.
/// <para>
/// Editing a field clears that field's own error, so a stale message does not sit
/// next to a value that has since been fixed.
/// </para>
/// </summary>
public sealed class TransactionFormViewModel : ObservableObject
{
    private TransactionFormState _state = new();

    public TransactionFormState State => _state;

    /// <summary>Replaces the state and refreshes every binding: a type switch changes which fields exist.</summary>
    private void Set(TransactionFormState next)
    {
        _state = next;
        OnPropertyChanged(string.Empty);
    }

    public IReadOnlyList<EcrTransactionType> TypeOptions => EcrTransactionTypes.MenuOptions;

    public EcrTransactionType Type
    {
        get => _state.Type;
        // Switching type resets the choices that no longer apply, so a terminal id
        // or code left over cannot travel with the next one.
        set
        {
            if (value != _state.Type) Set(new TransactionFormState { Type = value });
        }
    }

    // Amount.

    public bool RequiresAmount => _state.Type.RequiresAmount();

    public string AmountDigits
    {
        get => _state.AmountDigits;
        set
        {
            if (value == _state.AmountDigits) return;
            Set(_state with { AmountDigits = value, Errors = _state.Errors with { Amount = null } });
        }
    }

    public string? AmountError => _state.Errors.Amount;

    public bool HasAmountError => AmountError != null;

    /// <summary>The explanation shown under a dimmed amount field; empty when the field is live.</summary>
    public string AmountHelp => _state.Type switch
    {
        _ when _state.Type.RequiresAmount() => "",
        EcrTransactionType.Inquiry => "An inquiry only reads; nothing is charged",
        _ => "Uses the original transaction's amount",
    };

    public bool HasAmountHelp => AmountHelp.Length != 0 && !HasAmountError;

    // Merchant reference (sale).

    public bool ShowMerchantReference => _state.ShowMerchantReference;

    public string MerchantReference
    {
        get => _state.MerchantReference;
        set
        {
            var next = Truncate(value);
            if (next == _state.MerchantReference) return;
            Set(_state with { MerchantReference = next, Errors = _state.Errors with { MerchantReference = null } });
        }
    }

    public string? MerchantReferenceError => _state.Errors.MerchantReference;

    public bool HasMerchantReferenceError => MerchantReferenceError != null;

    // Inquiry: by reference.

    public bool ShowReferenceSwitch => _state.ShowReferenceSwitch;

    public bool LookUpByReference
    {
        get => _state.LookUpByReference;
        set
        {
            if (value == _state.LookUpByReference) return;
            Set(_state with
            {
                LookUpByReference = value,
                ReceiptNumber = "",
                OriginalReference = "",
                Errors = new FormErrors(),
            });
        }
    }

    public bool LooksUpByReference => _state.LooksUpByReference;

    public string OriginalReference
    {
        get => _state.OriginalReference;
        set
        {
            var next = Truncate(value);
            if (next == _state.OriginalReference) return;
            Set(_state with { OriginalReference = next, Errors = _state.Errors with { OriginalReference = null } });
        }
    }

    public string? OriginalReferenceError => _state.Errors.OriginalReference;

    public bool HasOriginalReferenceError => OriginalReferenceError != null;

    // Receipt number.

    public bool ShowReceiptNumber => _state.ShowReceiptNumber;

    public string ReceiptNumber
    {
        get => _state.ReceiptNumber;
        set
        {
            var next = new string(value.Where(c => c >= '0' && c <= '9').Take(TransactionFormState.ReceiptNumberMaxLength).ToArray());
            if (next == _state.ReceiptNumber) return;
            Set(_state with { ReceiptNumber = next, Errors = _state.Errors with { ReceiptNumber = null } });
        }
    }

    public string? ReceiptNumberError => _state.Errors.ReceiptNumber;

    public bool HasReceiptNumberError => ReceiptNumberError != null;

    // Original date.

    public bool ShowOriginalDate => _state.ShowOriginalDate;

    /// <summary>The original transaction cannot be in the future.</summary>
    public DateTime LatestDate => DateTime.Today;

    /// <summary>Nullable because the picker reports null when its text is cleared; a cleared date keeps the previous one.</summary>
    public DateTime? OriginalDate
    {
        get => _state.OriginalDate;
        set
        {
            if (value is not { } picked)
            {
                OnPropertyChanged();
                return;
            }
            if (picked.Date == _state.OriginalDate) return;
            Set(_state with { OriginalDate = picked.Date > DateTime.Today ? DateTime.Today : picked.Date });
        }
    }

    // Different terminal.

    public bool ShowOtherTerminalSwitch => _state.ShowOtherTerminalSwitch;

    public bool UseOtherTerminal
    {
        get => _state.UseOtherTerminal;
        set
        {
            if (value == _state.UseOtherTerminal) return;
            Set(_state with { UseOtherTerminal = value, OriginalTerminalId = "" });
        }
    }

    public bool ShowOriginalTerminal => _state.ShowOriginalTerminal;

    public string OriginalTerminalId
    {
        get => _state.OriginalTerminalId;
        set
        {
            var next = new string(value.Where(c => c >= '0' && c <= '9').ToArray());
            if (next == _state.OriginalTerminalId) return;
            Set(_state with { OriginalTerminalId = next, Errors = _state.Errors with { OriginalTerminalId = null } });
        }
    }

    public string? OriginalTerminalIdError => _state.Errors.OriginalTerminalId;

    public bool HasOriginalTerminalIdError => OriginalTerminalIdError != null;

    // Submit.

    public string StartButtonText => _state.Type == EcrTransactionType.Inquiry ? "Look up transaction" : "Start transaction";

    /// <summary>Validates; the state then carries the reasons, and the request is null when it cannot be sent.</summary>
    public TransactionRequest? Validate(string terminalSerial)
    {
        var (validated, request) = _state.Validated(terminalSerial);
        Set(validated);
        return request;
    }

    /// <summary>
    /// Shown once, then cleared so the next transaction starts fresh: the amount and
    /// the references are per transaction and must not linger into the next sale.
    /// </summary>
    public void ClearAfterResult() =>
        Set(_state with { AmountDigits = "", MerchantReference = "", OriginalReference = "" });

    private static string Truncate(string value) =>
        value.Length <= TransactionFormState.MerchantReferenceMaxLength
            ? value
            : value[..TransactionFormState.MerchantReferenceMaxLength];
}
