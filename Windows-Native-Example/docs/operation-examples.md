# Full examples — Wi‑Fi and Web Service

Copy-paste guide for integrators reading these samples. Each section is a complete flow for
**one transport**. The SDK's own cookbook (same operations, no sample types) is its
[integration guide §5a](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/integration-guide.md#5a-copy-paste-examples-wi-fi-and-web-service).

The Close receipt call (section 14) exists only in SDK 1.0.2; build the samples with
`-p:EcrSdkDependency=project` until it is published.

**Keys live in settings** (two different Amwal secrets). **Terminal records hold addresses
only** — no secure-hash field on Add/Edit terminal. Both the WPF and web samples share
this Core path (`SelectedTerminalConfig` → `EcrSessions.Plan` → `EcrSessions.Open`).

| | Wi‑Fi (`ecrMode: 2`) | Web Service (`ecrMode: 4`) |
|---|---|---|
| Settings | *Wi‑Fi secure hash key* | Environment (SIT/UAT/PROD) + *Web Service secure hash key* |
| Terminal form | name, serial, IP, port | name, serial, merchant ID, terminal ID |
| Client | `EcrTerminal` / Wi-Fi session | `EcrWebServiceTerminal` / Hub session |
| Probe / e-receipt | Yes | No |
| Default response timeout | 120 s | 130 s (the plan raises the 120 s default) |

```csharp
using System.Diagnostics;
using System.Globalization;
using AmwalECR.Example.Core.Data;
using AmwalECR.Example.Core.Services;
using AmwalPay.Ecr;

// From EcrExampleSettings (encrypted) — never hard-code in production
string wifiKey = settings.WifiSecureHashKey;
string webServiceKey = settings.WebServiceSecureHashKey;
EcrEnvironment environment = settings.Environment;   // SIT / UAT / PROD
```

---

## How the samples open a session

Both apps resolve a registered `Terminal` through Core, then open one session for every mode:

```csharp
SelectedTerminalConfig config = SelectedTerminalConfig.Resolve(
    terminal,
    environment: settings.Environment,
    secureHashKey: settings.SecureHashKeyFor(terminal.Mode));   // one key by mode

if (!config.IsReady)
{
    screen.Show(string.Join("\n", config.Issues));
    return;
}

EcrOpenedSession session = EcrSessions.Open(
    terminalSerial: terminal.SerialNumber,
    plan: config.Plan,
    usbChannel: config.Plan.UsesUsbCable ? () => new SerialEcrChannel(SerialPorts.Resolve(terminal.ComPort)) : null,
    logger: EcrLogger.From(Debug.WriteLine));

// TransactionEngine.OpenSession does the same, then SaleAsync / VoidAsync / …
```

The typed clients below (`EcrSessions.LanTerminal` / `WebServiceTerminal`) are equivalent when
you are not using `Open` — useful when porting into your own till without the sample's engine.
A USB cable has to go through `Open` and pass `usbChannel`, because that is the only place the
till supplies the host side of the cable.

---

## USB cable — setup

TMS: `terminalMode: 1`, `ecrMode: 1`. No IP address. The signing key is the Wi‑Fi key.
The terminal's USB virtual serial port is on, and Windows shows it as MediaTek USB VCOM
at 115200 8N1. Plug and Play assigns the COM port from the device serial. The same
terminal on another USB socket can get a different COM number. The sample stores that
serial and calls `SerialPorts.Resolve` before it opens the port. Sale, void, refund,
inquiry, e-receipt and close receipt are the same framed messages as Wi‑Fi.

**In the sample UI:** Terminals → paste *Wi‑Fi secure hash key* → Add terminal
(mode USB cable, USB device) → plug the terminal into this computer → Transaction → select it.

```csharp
EcrOpenedSession OpenUsbTerminal(string serial, string wifiSecureHashKey)
{
    var plan = EcrSessions.Plan(
        link: new EcrLink.UsbCable("COM8"),
        config: new EcrConfig { SecureHashKey = wifiSecureHashKey });

    return EcrSessions.Open(
        terminalSerial: serial,
        plan: plan,
        usbChannel: () => new SerialEcrChannel("COM8"));
}
```

---

## 1. Wi‑Fi — setup

TMS: `terminalMode: 1`, `ecrMode: 2`. Same Wi-Fi as the till. Terminal shows IP:port under
the card logos.

**In the sample UI:** Terminals → paste *Wi‑Fi secure hash key* → Add terminal
(mode Wi‑Fi, IP, port) → Transaction → select it (config card must be ready).

```csharp
EcrTerminal OpenWifiTerminal(Terminal registered, string wifiSecureHashKey)
{
    // Same mapping SelectedTerminalConfig uses for EcrMode.Wifi
    var plan = EcrSessions.Plan(
        link: new EcrLink.Lan(Host: registered.IpAddress, Port: registered.Port is >= 1 and <= 65535
            ? registered.Port
            : EcrConfig.DefaultPort),
        config: new EcrConfig
        {
            SecureHashKey = wifiSecureHashKey,
            EcrId = "TILL-04",
            CurrencyCode = "512",
            MinorUnitDigits = 3,
            ResponseTimeout = TimeSpan.FromSeconds(120),
        });

    if (!plan.IsReady)
        throw new InvalidOperationException(string.Join("\n", plan.Issues));

    return EcrSessions.LanTerminal(
        registered.SerialNumber,
        plan,
        EcrLogger.From(msg => Debug.WriteLine(msg, "EcrLan")));
}

// Or via Core:
// var config = SelectedTerminalConfig.Resolve(registered, environment, wifiSecureHashKey);
// var session = EcrSessions.Open(registered.SerialNumber, config.Plan, logger: …);
```

Probe before a money-moving call (Wi-Fi only — the sample’s engine does this automatically):

```csharp
if (!await wifi.IsReachableAsync())
{
    screen.Show("Terminal not reachable — check power, Wi‑Fi, and firewall (TCP 9100)");
    return;
}
```

---

## 2. Wi‑Fi — sale

```csharp
async Task WifiSaleAsync(EcrTerminal wifi, decimal amount, string orderNumber)
{
    // Persist orderNumber before the call — needed if the answer is lost
    switch (await wifi.SaleAsync(amount, merchantReference: orderNumber))
    {
        case EcrResult.Approved a when a.PartialApproval:
            var requested = decimal.Parse(a.RequestedAmount, CultureInfo.InvariantCulture);
            var taken = decimal.Parse(a.Amount, CultureInfo.InvariantCulture);
            screen.Show($"Only {a.Amount} approved. Collect {requested - taken} another way.");
            CompleteSale(a);
            break;

        case EcrResult.Approved a:
            CompleteSale(a);   // a.Amount, a.Rrn, a.AuthCode, a.MaskedPan
            break;

        case EcrResult.Declined d:
            screen.Show(d.Reason);
            break;

        case EcrResult.Failed f when f.Settled:
            Reconcile(((EcrInquiry.Found)f.Recovered!).Transaction);
            break;

        case EcrResult.Failed f:
            HandleUnknownOutcome(orderNumber, f.Failure.Message);
            break;
    }
}
```

**Via the sample engine** (WPF view-model / web `POST /api/transactions`):

```csharp
var state = await engine.RunAsync(
    new TransactionRequest
    {
        TerminalSerial = serial,
        Type = EcrTransactionType.Sale,
        Amount = 1.234m,
        MerchantReference = orderNumber,
    },
    report: ui.ShowBusy,
    cancellationToken);
// TransactionOutcomes classifies Approved / Declined / Failed → screen state
```

**Close receipt** — from the cashier's Close receipt button, not automatically and not on
dialog dismiss (see [section 14](#14-close-receipt-both-transports)):

```csharp
// after the sale was Approved
EcrReceiptClosed closed = await wifi.CloseReceiptAsync();
```


---

## 3. Wi‑Fi — void

```csharp
async Task WifiVoidAsync(EcrTerminal wifi, string receiptNumber)
{
    switch (await wifi.VoidAsync(receiptNumber: receiptNumber))
    {
        case EcrResult.Approved a:
            // Do not print a.AuthCode on a void — it is a placeholder
            RecordVoid(a.Rrn, a.Amount);
            break;
        case EcrResult.Declined d:
            screen.Show(d.Reason);
            break;
        case EcrResult.Failed f:
            HandleUnknownOutcome("", f.Failure.Message);
            break;
    }
}

// Original taken on another till (Wi-Fi only)
await wifi.VoidAsync(receiptNumber: "215", originalTerminalId: "31629");
```

**Close receipt** — from the cashier's Close receipt button, not automatically and not on
dialog dismiss (see [section 14](#14-close-receipt-both-transports)):

```csharp
// after the void was Approved
EcrReceiptClosed closed = await wifi.CloseReceiptAsync();
```


---

## 4. Wi‑Fi — refund

```csharp
async Task WifiRefundAsync(
    EcrTerminal wifi,
    decimal amount,
    string receiptNumber,
    string transactionDate)   // yyyyMMdd
{
    // Cardholder must present the card
    switch (await wifi.RefundAsync(
        amount: amount,
        receiptNumber: receiptNumber,
        transactionDate: transactionDate))
    {
        case EcrResult.Approved a: CompleteRefund(a); break;
        case EcrResult.Declined d: screen.Show(d.Reason); break;
        case EcrResult.Failed f:   HandleUnknownOutcome("", f.Failure.Message); break;
    }
}
```

**Close receipt** — from the cashier's Close receipt button, not automatically and not on
dialog dismiss (see [section 14](#14-close-receipt-both-transports)):

```csharp
// after the refund was Approved
EcrReceiptClosed closed = await wifi.CloseReceiptAsync();
```


---

## 5. Wi‑Fi — inquiry by receipt (STAN)

```csharp
async Task WifiInquireByStanAsync(
    EcrTerminal wifi,
    string receiptNumber,
    string transactionDate)   // yyyyMMdd
{
    switch (await wifi.InquireAsync(
        receiptNumber: receiptNumber,
        transactionDate: transactionDate))
    {
        case EcrInquiry.Found found:
            var txn = found.Transaction;
            screen.Show($"{txn.Type} {txn.Status} {txn.Amount} {txn.Currency}");
            if (txn.CanVoid) EnableVoid();
            if (txn.CanRefund) EnableRefund();
            break;
        case EcrInquiry.NotFound n:
            screen.Show(n.Reason);
            break;
        case EcrInquiry.Failed f:
            screen.Show(f.Failure.Message);   // lookup failed — still unknown
            break;
    }
}
```

---

## 6. Wi‑Fi — inquiry by merchant reference

```csharp
async Task WifiInquireByReferenceAsync(EcrTerminal wifi, string orderNumber)
{
    switch (await wifi.InquireByReferenceAsync(
        originalReference: orderNumber,
        transactionDate: ""))   // blank — do not invent "today"
    {
        case EcrInquiry.Found found:  Reconcile(found.Transaction); break;
        case EcrInquiry.NotFound:     /* nothing taken — safe to offer a new sale */ break;
        case EcrInquiry.Failed:       Escalate(); break;
    }
}
```

In the sample UI: Inquiry → **Look up by reference** (date/receipt fields hidden).

---

## 7. Wi‑Fi — e-receipt

```csharp
async Task WifiReceiptAsync(
    EcrTerminal wifi,
    string receiptNumber,
    string transactionDate)
{
    switch (await wifi.ReceiptAsync(
        receiptNumber: receiptNumber,
        transactionDate: transactionDate))
    {
        case EcrReceipt.Ready ready:         screen.ShowQrCode(ready.Url); break;
        case EcrReceipt.Unavailable missing: screen.Show(missing.Reason); break;
        case EcrReceipt.Failed failed:       screen.Show(failed.Failure.Message); break;
    }
}
```

The samples call `TransactionEngine.FetchReceiptAsync` after an inquiry result and draw a
QR (WPF / server-side SVG on web).

---

## 8. Web Service — setup

TMS: `terminalMode: 1`, `ecrMode: 4`. No terminal IP — HTTPS to the Amwal Hub. Use the
**Web Service** key (not the Wi‑Fi key) and numeric merchant/terminal IDs.

**In the sample UI:** Terminals → Web Service settings (environment + key) → Add terminal
(mode Web Service, merchant ID, terminal ID — no IP) → Transaction → select it.

```csharp
EcrWebServiceTerminal OpenWebServiceTerminal(
    Terminal registered,
    string webServiceSecureHashKey,
    EcrEnvironment environment)
{
    var plan = EcrSessions.Plan(
        link: new EcrLink.WebService(
            MerchantId: registered.MerchantId,
            TerminalId: registered.TerminalId),
        config: new EcrConfig
        {
            SecureHashKey = webServiceSecureHashKey,
            Environment = environment,
            EcrId = "TILL-04",
            CurrencyCode = "512",
            MinorUnitDigits = 3,
        });

    if (!plan.IsReady)
        throw new InvalidOperationException(string.Join("\n", plan.Issues));
    // plan.ConnectionSummary → e.g. "UAT · merchant 13593 · terminal 101311"

    return EcrSessions.WebServiceTerminal(
        registered.SerialNumber,
        plan,
        EcrLogger.From(msg => Debug.WriteLine(msg, "EcrWebService")));
}

// Core path (what TransactionEngine uses):
// var config = SelectedTerminalConfig.Resolve(registered, environment, webServiceSecureHashKey);
// var session = EcrSessions.Open(registered.SerialNumber, config.Plan, logger: …);
// ProbeReachabilityAsync returns null — engine skips it.
```

Hub hostnames are SDK-internal (chosen from SIT / UAT / PROD). Paths:
`/Ecr/Sale`, `/Ecr/Void`, `/Ecr/Refund`, `/Ecr/Inquiry`.

---

## 9. Web Service — sale

```csharp
async Task WebServiceSaleAsync(
    EcrWebServiceTerminal hub,
    decimal amount,
    string orderNumber)
{
    // Always pass and persist merchantReference before the call
    switch (await hub.SaleAsync(amount, merchantReference: orderNumber))
    {
        case EcrResult.Approved a when a.PartialApproval:
            CompleteSale(a);
            break;

        case EcrResult.Approved a:
            CompleteSale(a);
            break;

        case EcrResult.Declined d:
            screen.Show(d.Reason);
            break;

        case EcrResult.Failed f:
            // Timeout / lost connection (outcome unknown: inquire, do not resend),
            // or non-numeric merchant/terminal IDs (nothing was sent).
            // Recovered is always null on Web Service.
            HandleUnknownOutcome(orderNumber, f.Failure.Message);
            break;
    }
}
```

The SDK reports a lost Web Service answer as `Failed`. `TransactionOutcomes` in Core
shows it as **Outcome unknown** with **Inquire by reference** — never a retry button — and
treats a `Declined` carrying `NO_RESPONSE` or `NextStep.InquireByMerchantReference` the
same way, as a defensive fallback.

**Close receipt** — from the cashier's Close receipt button, not automatically and not on
dialog dismiss (see [section 14](#14-close-receipt-both-transports)):

```csharp
// after the sale was Approved: orderNumber is the reference the sale was sent with
EcrReceiptClosed closed = await hub.CloseReceiptAsync(originalMerchantReference: orderNumber);
```


---

## 10. Web Service — void

```csharp
async Task WebServiceVoidAsync(EcrWebServiceTerminal hub, string receiptNumber)
{
    switch (await hub.VoidAsync(receiptNumber: receiptNumber))
    {
        case EcrResult.Approved a:
            RecordVoid(a.Rrn, a.Amount);
            break;
        case EcrResult.Declined d:
            screen.Show(d.Reason);
            break;
        case EcrResult.Failed f:
            HandleUnknownOutcome("", f.Failure.Message);
            break;
    }
}
```

**Close receipt** — from the cashier's Close receipt button, not automatically and not on
dialog dismiss (see [section 14](#14-close-receipt-both-transports)):

```csharp
// after the void was Approved: the result carries the reference the SDK generated
EcrReceiptClosed closed = await hub.CloseReceiptAsync(originalMerchantReference: approved.MerchantReference);
```


---

## 11. Web Service — refund

```csharp
async Task WebServiceRefundAsync(
    EcrWebServiceTerminal hub,
    decimal amount,
    string receiptNumber,
    string transactionDate)   // yyyyMMdd
{
    switch (await hub.RefundAsync(
        amount: amount,
        receiptNumber: receiptNumber,
        transactionDate: transactionDate))
    {
        case EcrResult.Approved a: CompleteRefund(a); break;
        case EcrResult.Declined d: screen.Show(d.Reason); break;
        case EcrResult.Failed f:   HandleUnknownOutcome("", f.Failure.Message); break;
    }
}
```

**Close receipt** — from the cashier's Close receipt button, not automatically and not on
dialog dismiss (see [section 14](#14-close-receipt-both-transports)):

```csharp
// after the refund was Approved
EcrReceiptClosed closed = await hub.CloseReceiptAsync(originalMerchantReference: approved.MerchantReference);
```


---

## 12. Web Service — inquiry by receipt (STAN)

```csharp
async Task WebServiceInquireByStanAsync(
    EcrWebServiceTerminal hub,
    string receiptNumber,
    string transactionDate)   // yyyyMMdd
{
    switch (await hub.InquireAsync(
        receiptNumber: receiptNumber,
        transactionDate: transactionDate))
    {
        case EcrInquiry.Found found:
            Reconcile(found.Transaction);
            break;
        case EcrInquiry.NotFound n:
            screen.Show(n.Reason);
            break;
        case EcrInquiry.Failed f:
            // Lookup failed (often ConnectionLost) — still unknown; ask again
            screen.Show(f.Failure.Message);
            break;
    }
}
```

---

## 13. Web Service — inquiry by merchant reference

```csharp
async Task WebServiceInquireByReferenceAsync(
    EcrWebServiceTerminal hub,
    string orderNumber)
{
    // Prefer after a lost sale. Date may be blank — samples do not invent "today".
    switch (await hub.InquireByReferenceAsync(
        originalReference: orderNumber,
        transactionDate: ""))
    {
        case EcrInquiry.Found found:  Reconcile(found.Transaction); break;
        case EcrInquiry.NotFound:     /* Hub has no match — may still need batch reconcile */ break;
        case EcrInquiry.Failed:       Escalate(); break;
    }
}
```

---

## 14. Close receipt (both transports)

Asks the terminal to put its receipt away. Call it from the till's **Close receipt** action
when the cashier is finished with the result — not as a side effect of dismissing the result
dialog. Wi‑Fi sends `CLOSE_RECEIPT`; Web Service posts `/Ecr/closeReceipt` and **needs** the
sale / void / refund merchant reference as `originalMerchantReference`.

```csharp
// Wi‑Fi: no reference needed
EcrReceiptClosed closed = await wifi.CloseReceiptAsync();

// Web Service: the reference of the sale / void / refund that just finished
EcrReceiptClosed closed = await hub.CloseReceiptAsync(originalMerchantReference: orderNumber);

switch (closed)
{
    case EcrReceiptClosed.Idle:             screen.Show("Terminal is back on its idle screen"); break;
    case EcrReceiptClosed.Refused refused:  screen.Show($"Terminal refused: {refused.Reason}"); break;  // too old to know it
    case EcrReceiptClosed.Failed failed:    screen.Show(failed.Failure.Message); break;
}
```

Through a session (`EcrOpenedSession.CloseReceiptAsync(merchantReference, originalMerchantReference)`)
the same call serves both transports; `TransactionEngine.CloseTerminalReceiptAsync` wraps it and
returns the message to show. The sample offers the button only on a completed sale, void or
refund; on Web Service it takes the reference from the result.

---

## 15. What Web Service does not support

| Operation | Wi‑Fi and USB cable | Web Service |
|---|---|---|
| `ReceiptAsync` / e-receipt QR | Yes | **No** — UI says so; the session returns `EcrReceipt.Unavailable` |
| Reachability probe | Yes | **No** — `ProbeReachabilityAsync` is `null`, skipped |
| `originalTerminalId` (other till) | Yes | Ignored on an opened Web Service session |
| Auto-inquire after lost sale | Yes (SDK default) | **No** — offer **Inquire by reference** |

---

## Related docs

- Sample architecture: [implementation.md](implementation.md) (WPF / Core)
- Web host: [../../AmwalECR-Web-Example/docs/implementation.md](../../AmwalECR-Web-Example/docs/implementation.md)
- Operator steps: [../README.md](../README.md#configure-it)
