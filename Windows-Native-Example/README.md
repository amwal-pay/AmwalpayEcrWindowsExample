# Amwal ECR — Windows sample app

A working Electronic Cash Register for Windows: register the terminals you own,
key an amount, pick a terminal, run a transaction against it. It is the Windows
counterpart of the Android **ECR simulator**, built on the C# `AmwalPay.Ecr` SDK,
with the same screens, form rules and wording.

It is meant to be read as much as run — it is the reference for how the SDK is
used from a WPF till. (`Core/` below is `src/AmwalECR.Example.Core/`; `Views/`,
`ViewModels/` and `Ui/` are under `src/AmwalECR.Example/`.)

- **How it is built** (layering, state machine, threading, extension recipes):
  [docs/implementation.md](docs/implementation.md).
- **The whole repository** (this sample, the ASP.NET Core web sample that reuses Core, prerequisites,
  every check): the [umbrella README](../README.md).

| | |
|---|---|
| `Views/TransactionView.xaml` | The form, driven by `EcrTransactionType` |
| `ViewModels/TransactionViewModel.cs` | Holds the in-flight transaction, its `CancellationTokenSource`, and what the screen shows |
| `Core/Services/TransactionEngine.cs` | Calls `EcrSessions`: plan, open, probe, run, classify the answer |
| `Core/Services/SelectedTerminalConfig.cs` | Maps registered terminals to `EcrLink` + `EcrSessionPlan` |
| `Core/Transaction/TransactionFormState.cs` | Which fields exist, what is required, `Validated()` |
| `Core/Transaction/TransactionOutcomes.cs` | Turns `EcrResult` / `EcrInquiry` into screen state — the payment-safety rules |
| `Views/TerminalConfigCard.xaml` | Shows active transport, connection and config issues before you pay |
| `Views/Terminals*`, `Views/TerminalEditWindow.xaml` | Registering terminals: list, add, edit, delete |
| `Core/Data/EcrExampleSettings.cs` | LAN and Web Service signing keys, environment (encrypted with DPAPI) |
| `Core/Data/TerminalRepository.cs` | JSON store keyed by terminal serial number |
| `Ui/Behaviors/AmountInput.cs` | POS-style amount entry on a plain `TextBox` |

`AmwalECR.Example.Core` is a plain `net8.0` library holding everything that is not a
window, so its logic (amount entry, form rules, config resolution, the transaction
flow) is unit-tested by `dotnet test` on any OS. `AmwalECR.Example` is the WPF shell.

**Terminals are keyed by serial number**, and the transaction screen keeps only that
serial — the address is looked up when it sends. Selecting a terminal in the dropdown
loads its mode (LAN vs Web Service), validates credentials via `EcrSessions.Plan()`,
and shows any missing configuration before you pay.

Supported operations are **sale, void, refund and inquiry**, with the e-receipt of any
inquiry result over Wi‑Fi. Wi‑Fi (LAN) and Web Service use **different signing keys** —
configure both on the Terminals screen.

## Modes

| Mode | In this sample |
|---|---|
| Wi‑Fi (LAN) | Supported |
| Web Service | Supported |
| USB cable | Registrable, **not supported**: the Windows SDK ships no USB channel |
| Bluetooth | Registrable, **not supported** (as in the Android sample) |

USB cable and Bluetooth terminals can be added so a terminal list matches the Android
one, but their config card reads "*X* is not supported in this sample" and Start stays
disabled.

## Running it

Requires **Windows 10 1809+ or Windows 11** and the **.NET 8 SDK**, plus the SDK source checked
out beside this repository (see *Choosing how the app gets the SDK* below). From this folder:

```powershell
dotnet run --project src/AmwalECR.Example
```

or open `AmwalECR-Example.sln` in Visual Studio 2022 (17.8+) and press F5.

To run without installing anything, publish a self-contained executable — see
[Publishing](#publishing). To see it from a Mac, use the Windows VM route in
[tools/vm/README.md](tools/vm/README.md).

The PC and the terminal must be on the same network, and the network profile should be
**Private** (Windows applies its strictest rules to Public networks). The terminal shows
its address on its idle screen, beneath the card scheme logos. To check a route:

```powershell
Test-NetConnection 192.168.1.50 -Port 9100
```

Data lives in `%APPDATA%\AmwalECR-Example\`: `terminals.json` (the terminal list) and
`settings.dat` (environment and both signing keys, encrypted with Windows DPAPI for the
current user — the equivalent of Android's `EncryptedSharedPreferences`). Keys are
trimmed, checked with `EcrConfig.IsValidSecureHashKey`, and saved as you type; settings
saved by another Windows user cannot be read back, and the app says so rather than
failing silently.

### Choosing how the app gets the SDK

The counterpart of `ecrSdkDependency` in the Android sample's `gradle.properties`. In
[`Directory.Build.props`](Directory.Build.props) (or on the command line) set
`EcrSdkDependency`:

| Value | Resolves from |
| --- | --- |
| `project` (default) | `..\..\AmwalECR-Windows\src\AmwalPay.Ecr\AmwalPay.Ecr.csproj`, i.e. a checkout of the SDK repo named `AmwalECR-Windows` **next to the samples repo folder** (`AmwalECR-Windows-Example`) |
| `nuget` | `AmwalPay.Ecr` version `$(EcrSdkVersion)` (default `1.0.0`) from your NuGet sources |

```powershell
# the layout `project` mode expects
git clone https://github.com/amwal-pay/AmwalpayEcrWindows AmwalECR-Windows
git clone https://github.com/amwal-pay/AmwalpayEcrWindowsExample AmwalECR-Windows-Example

# or use the package (no SDK checkout needed)
dotnet build src/AmwalECR.Example -p:EcrSdkDependency=nuget -p:EcrSdkVersion=1.0.0
```

**In NuGet mode open or build `AmwalECR-Example.NuGet.slnf`, not `AmwalECR-Example.sln`.** The `.sln`
lists the SDK project (`..\..\AmwalECR-Windows\src\AmwalPay.Ecr`) so that project mode shows the SDK
source in the IDE; without that checkout, building it fails with "project file not found". The
solution filter loads only this sample's three projects and works in both modes:

```powershell
dotnet build AmwalECR-Example.NuGet.slnf -p:EcrSdkDependency=nuget -p:EcrSdkVersion=1.0.0
```

NuGet mode was verified (Core, the tests, the WPF app and the web sample all build, the tests pass, and
the SDK resolves as a package) from a clone of only the samples repository, against a locally packed
`AmwalPay.Ecr`, using `-p:RestoreAdditionalProjectSources=<folder with the .nupkg>`. It has **not** been
run against nuget.org, because the package is not published yet.

Only `AmwalECR.Example.Core` references the SDK; the app and the tests receive it
transitively (and so does the web sample). Any value other than `project` or `nuget` fails the
build with a message saying what is allowed, and so does a `project` checkout that is not where
expected; point `-p:EcrSdkProjectPath=<path to AmwalPay.Ecr.csproj>` at another location.
After switching mode, restore again (Core, the app, the tests and the web sample share Core's `obj`).

### Debug output

The SDK writes its diagnostics — every request and response, the signing details, probe
failures — to an `IEcrLogger`. The sample routes it to a collapsible **Debug output**
panel at the bottom of the window (last 500 lines, with **Copy log** and **Clear**) and
to `Debug.WriteLine`. It is the Windows equivalent of Logcat and how a tester sees the
wire traffic. The lines include the masked card number: treat what you copy as
transaction records.

## Amount entry

The amount field behaves like the terminal's own: no decimal point is typed, digits
shift in from the right against three fixed decimals, and it reads `0.000` when empty.

| Keys pressed    | Shows | Sent as        |
| --------------- | ----- | -------------- |
| `1`             | 0.001 | `000000000001` |
| `1` `2`         | 0.012 | `000000000012` |
| `1` `2` `3` `4` | 1.234 | `000000001234` |
| backspace       | 0.123 | `000000000123` |

The caret cannot be placed: the box is rendered from its keyed digits with the selection
pinned to the end, so clicking the middle of the number does not make the next digit land
there. Pasted text is keyed in as digits; moving focus into the field from another control
starts a fresh amount; Escape clears it; Delete, Space and Cut do nothing. At most nine digits
are accepted, and the form then rejects anything over the maximum.

Amount bounds live in `AmwalECR.Example.Core/Ui/AmountLimits.cs` and mirror the terminal's
purchase form: required, a minimum of `0.010`, then the terminal's transaction limit
(`100000.000`). On a real terminal that limit comes from the server, so change `Max` to
match your deployment.

## What the form asks for

Which fields appear is decided by `EcrTransactionType`, so the form and the wire format
cannot disagree about what an operation needs.

|             | Amount | Receipt number | Date | Merchant reference |
| ----------- | ------ | -------------- | ---- | ------------------ |
| **Sale**    | ✔      |                |      | optional           |
| **Void**    |        | ✔              |      |                    |
| **Refund**  | ✔      | ✔              | ✔    |                    |
| **Inquiry** |        | ✔              | ✔    | (or look up by reference instead) |

A void takes the original transaction's amount, which only the terminal knows, so the
amount field is dimmed rather than hidden — the operator can see it is not theirs to set.
An inquiry dims it too, and says why: nothing is charged.

- **Different terminal** (void, refund, inquiry): the original may have been taken on
  another terminal; its ID is then required.
- **Date** defaults to today, cannot be in the future, and goes on the wire as `yyyyMMdd`.
- **Merchant reference** (sale, optional): at most 32 characters, printable ASCII, no
  space, `&` or `=` — the SDK's own rule, checked before anything is sent. Blank means the
  app generates one.
- **Receipt number** is digits only, up to 6.

## Inquiry

Looks an earlier transaction up by its receipt number and the day it was taken — or, with
**Look up by reference**, by the reference the till sent it with — and shows what became
of it: **receipt number, type, card, amount, status and date/time**, plus whether the
backend says it can still be voided or refunded. Everything else the terminal sent is under
"Show raw response".

It reads and nothing more, so the terminal answers it **even mid-payment**. That is the
point: after a transaction whose outcome is unknown, this is how the till finds out,
instead of retrying and charging the customer twice.

The dialog's headline is the _transaction's_ status, not the lookup's: finding a declined
sale is a successful inquiry.

**QR receipt.** Over Wi‑Fi the result carries a button that fetches the transaction's
e-receipt and draws it as a QR code (QRCoder, rendered on the PC, so it appears at once and
survives the network dropping). The customer scans it and reads the receipt on their own
phone — nothing is printed. Receipt is not a transaction type in the dropdown: it follows a
transaction the operator already has on screen. The Web Service transport has no receipt
operation, so its results say so instead of offering the button.

## When the answer never comes

A timeout, a dropped connection, an answer that cannot be verified: the request may have
reached the terminal, and the sale may have gone through. The sample follows the SDK's
rules to the letter:

- The screen says **Outcome unknown**, never "Declined" or "Cancelled", and offers exactly
  one action — **Inquire by reference** — never a retry. Sending the sale again is how a
  customer gets charged twice.
- If the SDK's automatic inquiry (`AutoInquireOnFailure`, on by default) finds the
  transaction, the dialog shows it as an inquiry result: *the answer was lost, but the
  terminal knows this transaction — do not send it again.*
- **Cancel** (shown while waiting) only stops waiting here. If the request already reached
  the terminal it can still complete, so a cancelled sale is reported as an unknown
  outcome with the same look-up, not as "cancelled". The app generates the sale's
  reference itself for exactly this reason: the SDK returns one on a result, and a
  cancelled call returns none.
- On **Web Service** a network error does not arrive as `Failed`; it arrives as `Declined`
  with `ResponseCode == "NO_RESPONSE"`. The sample treats that — and any decline whose
  `NextStep` says to inquire — as an unknown outcome, not a refusal.
- Closing the window mid-transaction asks first; it does not cancel anything on the
  terminal.
- Nothing was sent when the terminal cannot be reached (the Wi‑Fi probe runs first), so
  that message says what to check and offers no look-up.

On Web Service the SDK looks transactions up by receipt number and date, so a look-up by
reference is best-effort there, and the sample says so beside the switch and in the result.

## Testing without a terminal

The SDK repository's own `tools/fake_pos_server.py` (`AmwalECR-Windows/tools/`) **cannot be used
with this SDK as it is**:
`EcrSessions.Plan` reports "Wi-Fi/LAN secure hash key is not configured" until a key is
set, and a till that holds a key discards any answer that is not signed or does not echo
the request's nonce — the script answers unsigned, in the older flat shape. (Checked: with a
key configured, its answers surface as *"The answer was not signed with this terminal's
key"*.) So this repo carries a signed copy,
[`tools/fake_pos_server_signed.py`](tools/fake_pos_server_signed.py): it verifies the
request signature and nonce, answers in the unified envelope, echoes the nonce and signs
the answer (HMAC-SHA256 over the sorted scalar `key=value&…` pairs, hex-decoded key, upper
case hex — `AmwalPay.Ecr/Internal/SecureHash.cs`). It was checked against this SDK.

```powershell
python tools/fake_pos_server_signed.py --port 19100 --key 0123456789ABCDEF0123456789ABCDEF
```

(Run from this folder. It needs only Python 3.)

> **Use a port outside 9100–9199.** 9100 is the terminals' standard port, but on a developer
> machine other tooling — the Dart/Flutter tools, for one — may already listen in that range and
> answer instead of your fake, or make the fake fail to bind. `19100` is safe. Real terminals
> stay on 9100; the port field in the app is optional and blank means 9100.

1. Start the server with any hex key of at least 16 characters (even length).
2. In the app open **Terminals** and paste **the same key** as the *Wi-Fi/LAN secure hash
   key*.
3. **Add terminal**: any name and serial number, mode Wi‑Fi, IP `127.0.0.1` (this PC) or the
   LAN address the server prints, port `19100` (the port you started it on).
4. Back on the main screen key an amount and press **Start transaction**.

Flags, for the outcomes worth seeing:

| Flag | Answers with |
|---|---|
| *(none)* | Approved sales, voids, refunds; inquiries find what the server has taken (or a fixed sample by receipt number); receipts return a sample URL |
| `--decline 51` | A decline with that code |
| `--partial` | A partial approval — half of every sale |
| `--delay 3` | Waits first, to mimic card entry (try **Cancel** here) |
| `--not-found` | Every inquiry answers 25 *not found*; receipts refuse |
| `--drop-sale` | Records the sale, then hangs up without answering. The SDK's automatic inquiry finds it: the dialog shows the transaction with the *answer was lost* note |
| `--drop-sale --drop-first-inquiry` | As above, and the automatic inquiry is dropped too: **Outcome unknown**, then **Inquire by reference** finds it |
| a different key in the app | *"The answer was not signed with this terminal's key"* — an unknown outcome |

Web Service needs the real backend (SIT/UAT/PROD, merchant and terminal IDs, and the Web
Service key); there is no fake for it.

## Building and testing

From this folder (`Windows-Native-Example/`):

```bash
dotnet test tests/AmwalECR.Example.Tests                 # any OS, .NET 8 SDK: Core and the flow against a loopback terminal
/usr/local/share/dotnet/dotnet build AmwalECR-Example.sln   # WPF compile check off Windows; needs a Windows Desktop SDK (.NET 10)
python3 tools/check-xaml-bindings.py                     # the XAML binding guard, any OS
```

Prerequisites, verified:

- The tests and Core need only the **.NET 8 SDK**, on any OS. Microsoft ends support for .NET 8
  on **Nov 10, 2026**; the samples should move to `net10.0` (a note; not yet done). The SDK
  supports .NET Framework 4.6.2+ and .NET 8+ (see its
  [compatibility notes](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/compatibility.md#support-calendar)).
- The WPF project *compiles* off Windows because `EnableWindowsTargeting` is set in
  [`Directory.Build.props`](Directory.Build.props), but only with an SDK that ships the Windows
  Desktop targets: the Microsoft builds of the **.NET 10 SDK** do, **Homebrew's .NET 8 SDK does
  not** (`MSB4019: Microsoft.NET.Sdk.WindowsDesktop.targets was not found`). It cannot *run* off
  Windows.
- Running it needs Windows 10 1809+ or Windows 11.

The tests (186) cover the amount rules, form validation, config resolution, terminal and
settings storage, the classification of every outcome, the result dialog's content, and the
transaction flow end to end against an in-process signed terminal on loopback (approval,
decline, unreachable, lost answer, recovered answer, wrong key, cancel before and during
the wait, inquiry, receipt). The XAML is checked by building it and by
`tools/check-xaml-bindings.py`, which catches the one crash a build cannot: a two-way binding
onto a read-only property. Neither the view-models nor the running window have automated tests;
see the [known limitations](docs/implementation.md#o-known-limitations) and
[what each check catches](docs/implementation.md#l-tests).

## Publishing

A self-contained single-file executable, so the target PC needs no .NET (works from macOS too
with the .NET 10 SDK):

```bash
dotnet publish src/AmwalECR.Example/AmwalECR.Example.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/win-x64
```

Repeat with `-r win-arm64`. Output goes to `dist/` here (git-ignored; the release zips are
`dist/AmwalECR-Example-win-x64.zip` and `...-win-arm64.zip`, each holding the exe and
`tools/fake_pos_server_signed.py`). The CI workflow ([`../codemagic.yaml`](../codemagic.yaml))
publishes both on an `app-*` tag. The builds are **unsigned**: Windows SmartScreen warns on
first run (*More info*, then *Run anyway*).

## Differences from the Android sample

- **USB cable** is not driven (no USB channel in the Windows SDK); see *Modes*.
- **Different terminal** switch is live. It is commented out of the Android screen, but
  the form state and SDK support it.
- The **signing keys** live in a DPAPI-encrypted file rather than `EncryptedSharedPreferences`.
- **Look-up by reference** is offered only after an unknown outcome *if the request went
  out* (`Failure.OutcomeUnknown`); Android offers it after any failure.
- A decline whose `NextStep` asks for an inquiry, and Web Service `NO_RESPONSE`, are
  treated as unknown outcomes.
- A **Cancel** button, the **Debug output** panel, the *Different terminal* switch and an
  optional-port rule (blank means 9100) are additions; the config card refreshes when keys
  change on the Terminals screen.
