# Amwal ECR - Windows (WPF) sample

A working till for Windows built on the C# `AmwalPay.Ecr` SDK: register terminals, key an
amount, pick a terminal, run a sale, void, refund or inquiry, read the result. It is meant to be
read as much as run.

- How it is built (layering, state machine, payment-safety rules, extension recipes):
  [docs/implementation.md](docs/implementation.md)
- Copy-paste SDK code for Wi-Fi and Web Service: [docs/operation-examples.md](docs/operation-examples.md)
- The ASP.NET Core front end on the same Core library: [../AmwalECR-Web-Example](../AmwalECR-Web-Example/README.md)

## Run it

Needs Windows 10 1809+ or Windows 11 and the .NET 8 SDK. From this folder:

```powershell
dotnet run --project src/AmwalECR.Example -p:EcrSdkDependency=project
```

or open `AmwalECR-Example.sln` in Visual Studio 2022 (17.8+) with
`EcrSdkDependency=project` set (see below) and press F5.

### Choosing how the build gets the SDK

`Directory.Build.props` selects the SDK through `EcrSdkDependency`:

| Value | Resolves |
|---|---|
| `nuget` (default) | `AmwalPay.Ecr` `$(EcrSdkVersion)` (default `1.0.1`) from nuget.org |
| `project` | `..\..\AmwalECR-Windows\src\AmwalPay.Ecr\AmwalPay.Ecr.csproj`, a sibling checkout of the SDK; override with `-p:EcrSdkProjectPath=<path to AmwalPay.Ecr.csproj>` |

**Use `project` for now.** The samples call `CloseReceiptAsync`, which exists only in SDK 1.0.2.
nuget.org has 1.0.0 and 1.0.1, so a default (`nuget`) build fails until 1.0.2 is published.

```powershell
git clone https://github.com/amwal-pay/AmwalpayEcrWindows ..\..\AmwalECR-Windows   # next to the repo folder
dotnet build AmwalECR-Example.sln -p:EcrSdkDependency=project
```

Once 1.0.2 is on nuget.org, `dotnet build AmwalECR-Example.NuGet.slnf` builds without a checkout
(the `.slnf` leaves the SDK project out; the full `.sln` lists it). Only `AmwalECR.Example.Core`
references the SDK; the app, the tests and the web sample receive it through Core. After
switching modes, restore again (Core's `obj` is shared). Any value other than `project` or
`nuget`, or a `project` checkout that is missing, fails the build with an explicit message.

### Building off Windows

The tests and Core need only the .NET 8 SDK, on any OS. The WPF project also compiles on
macOS/Linux (`EnableWindowsTargeting` is on) but only with an SDK that ships the Windows Desktop
targets, such as the .NET 10 SDK; Homebrew's .NET 8 SDK fails with `MSB4019`. It cannot run
there. To see it from a Mac use the Windows VM route in [tools/vm/README.md](tools/vm/README.md).

## Configure it

Amwal issues **two different signing secrets**, set once on the **Terminals** screen (not on the
terminal form):

| Settings card | Used for |
|---|---|
| Wi-Fi/LAN secure hash key | Wi-Fi terminals |
| Web Service: environment (`SIT` / `UAT` / `PROD`) + Web Service secure hash key | Web Service terminals |

| Add/Edit terminal | Fields (no key) |
|---|---|
| Wi-Fi | name, serial, IP, port (blank = 9100) |
| Web Service | name, serial, merchant ID, terminal ID (numeric) |

A key must be an even-length hex string of at least 16 characters; it is trimmed, checked with
`EcrConfig.IsValidSecureHashKey`, saved as you type and encrypted with Windows DPAPI.

On the Transaction screen the **config card** shows the transport, connection and key status of
the selected terminal and lists anything the SDK plan says is missing; **Start** stays disabled
until the plan is ready.

Data lives in `%APPDATA%\AmwalECR-Example\`: `terminals.json` and `settings.dat` (environment and
both keys, encrypted for the current Windows user; settings saved by another user cannot be read
back and the app says so).

### Wi-Fi

The PC and terminal must be on the same network (profile **Private**). The terminal shows its IP
beneath the card logos on its idle screen. Check the route with
`Test-NetConnection 192.168.1.50 -Port 9100`.

### Web Service

The till calls the Amwal Hub over HTTPS (`ecrMode: 4`), not the terminal's IP. There is no fake
Hub; you need credentials from Amwal. In the app:

1. **Terminals** -> Web Service card: pick the environment and paste the **Web Service** key
   (not the Wi-Fi key).
2. **Add terminal** -> mode Web Service: serial, merchant ID, terminal ID.
3. **Transaction** -> select it. The config card must read `HTTPS Web Service (JSON HMAC)` and
   *configured*. Blockers it names include a missing or non-numeric ID and
   `Web Service secure hash key is not configured`.
4. Start a sale. The Hub is contacted only then; there is no reachability probe.

Hub hosts come from the SDK's environment setting. SDK checklist:
[integration guide, initialization](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/integration-guide.md#initialization-process).

## What it demonstrates

**Operations.** Sale, void, refund and inquiry. The form follows the SDK's
`EcrTransactionType` flags, so what is shown and what is required cannot disagree:

| | Amount | Receipt number | Date | Merchant reference |
|---|---|---|---|---|
| Sale | yes | | | optional |
| Void | dimmed (the terminal knows it) | yes | | |
| Refund | yes | yes | yes | |
| Inquiry | dimmed | yes | yes | or look up by reference |

Void, refund and inquiry also offer **Different terminal** (original taken elsewhere; its ID is
then required; ignored on Web Service). Date defaults to today, cannot be in the future and goes
on the wire as `yyyyMMdd`. A sale's merchant reference is optional, at most 32 printable ASCII
characters with no space, `&` or `=`; blank means the app generates one. Receipt number is up to
6 digits.

**Amount entry** behaves like the terminal's: no decimal point is typed, digits shift in from
the right against three fixed decimals (`1` -> `0.001`, `1 2 3 4` -> `1.234`), backspace removes
one, Escape clears, the caret is pinned to the end. Bounds are `0.010` to `100000.000`
(`AmountLimits.cs`; on a real terminal the maximum comes from the server, so change it for your
deployment).

**Operation countdown and Cancel.** While waiting, the screen counts down the configured response
timeout (120 s Wi-Fi, 130 s Web Service). **Cancel** only stops waiting: a cancelled sale, void
or refund may still have reached the terminal, so it is reported as an unknown outcome with a
look-up, never as "cancelled". Cancelling an inquiry, or anything before the request is sent,
just returns to idle. Closing the window mid-transaction asks first and cancels nothing.

**Unknown outcomes.** These show as **Outcome unknown** (or **Not completed** when nothing was
sent), and the only follow-up offered is **Inquire by reference**, never a retry:

- `EcrResult.Failed` for a timeout or lost connection;
- `Declined` with `NO_RESPONSE` or `NextStep.InquireByMerchantReference` (the SDK reports these
  as `Failed`; the sample still handles them defensively);
- a stopped wait (Cancel).

If the SDK's automatic inquiry (`AutoInquireOnFailure`, Wi-Fi) finds the transaction, the result
shows it with a note that the answer was lost but the terminal knows it. An unreachable terminal
(the Wi-Fi probe runs first) means nothing was sent: the message says what to check and offers no
look-up.

**Inquiry.** By receipt number and date, or by reference (the form label is **Original merchant
reference**; the date and receipt fields are hidden and the inquiry's own `merchantReference` is
left blank so the SDK mints one). The headline is the *transaction's* status: finding a declined
sale is a successful inquiry. A failed inquiry is a failure ("Not completed", or "Outcome
unknown" with the reference kept for another try), never "Not found". Inquiry reads only, so the
terminal answers it even mid-payment.

**Close receipt.** A separate button on a completed sale, void or refund result asks the terminal
to put its receipt away (Wi-Fi `CLOSE_RECEIPT`; Web Service `/Ecr/closeReceipt`, using the
sale/void/refund merchant reference as `originalMerchantReference`). Dismissing the dialog does
not send it.

**E-receipt QR.** After an inquiry over Wi-Fi the dialog can fetch the transaction's e-receipt
and draw it as a QR code on the PC. Web Service has no receipt operation and the dialog says so.

**Debug output.** A collapsible panel (last 500 lines, Copy, Clear) shows what the SDK logs:
every request and answer, with masked card numbers, never a key. Treat copied logs as
transaction records.

## Not supported

USB cable and Bluetooth terminals can be registered, but the config card says "*X* is not supported in this sample" and Start stays disabled. The
Windows SDK has no USB channel or Bluetooth link, and app-to-app is not offered. The e-receipt QR
is Wi-Fi only.

## Testing without a terminal

The SDK repository's own `fake_pos_server.py` answers unsigned and cannot be used with this SDK.
[`tools/fake_pos_server_signed.py`](tools/fake_pos_server_signed.py) verifies the request
signature, echoes the nonce and signs its answers. It needs only Python 3:

```powershell
python tools/fake_pos_server_signed.py --key 0123456789ABCDEF0123456789ABCDEF --port 19100
```

**Use a port outside 9100-9199.** Dart/Flutter tooling listens in that range on developer
machines and can answer instead of your fake. `19100` is safe; real terminals stay on 9100.

1. In the app, **Terminals**: paste the same key as the Wi-Fi/LAN key.
2. **Add terminal**: any name and serial, mode Wi-Fi, IP `127.0.0.1`, port `19100`.
3. Transaction: key an amount, **Start transaction**.

| Flag | Answers with |
|---|---|
| *(none)* | Approved sales, voids and refunds; inquiries find recorded transactions (or a fixed sample); receipts return a sample URL |
| `--decline 51` | A decline with that code |
| `--partial` | A partial approval (half of every sale) |
| `--delay 3` | Waits first, to mimic card entry (try **Cancel**) |
| `--not-found` | Every inquiry answers 25 *not found*; receipts refuse |
| `--drop-sale` | Records the sale, then hangs up. The SDK's automatic inquiry finds it: the dialog shows the transaction with the *answer was lost* note |
| `--drop-sale --drop-first-inquiry` | As above and the automatic inquiry is dropped too: **Outcome unknown**, then **Inquire by reference** finds it |
| `--host 127.0.0.1` | Listen on loopback only (default `0.0.0.0`) |
| a different key in the app | *"The answer was not signed with this terminal's key"*, an unknown outcome |

The fake has no `CLOSE_RECEIPT` handler; check that action against a real terminal.

## Build, test, publish

```bash
dotnet test tests/AmwalECR.Example.Tests -p:EcrSdkDependency=project   # 189 tests, any OS
dotnet build AmwalECR-Example.sln -p:EcrSdkDependency=project          # WPF compile check (Windows Desktop SDK)
python3 tools/check-xaml-bindings.py                                   # XAML binding guard, any OS
```

The tests cover amount entry, form validation, config resolution, storage, every outcome
classification, result wording and the whole flow against an in-process signed terminal. The
XAML guard catches the one crash a build cannot: a two-way binding onto a read-only property.
See [implementation.md, tests](docs/implementation.md#l-tests).

Publishing a self-contained executable: see the [root README](../README.md#windows-executables-for-testers).
For a non-developer run note, see [tools/START-HERE.txt](tools/START-HERE.txt).

## Known limits

- The window and view-models are not covered by automated tests; focus, IME, HiDPI and DPAPI on
  a real profile are checked by hand.
- No fake Hub and no loopback test for Web Service; only its result classification is tested.
- The amount maximum is a constant; no single-instance guard (two copies overwrite each other's
  terminal list); not localised (currency is OMR).

More in [implementation.md](docs/implementation.md#n-known-limitations).
