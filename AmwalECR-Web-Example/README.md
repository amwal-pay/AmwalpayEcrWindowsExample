# Amwal ECR — web sample

The **ASP.NET Core** front end of the Amwal ECR samples: register the POS terminals you own,
key an amount in the browser, pick a terminal, run a transaction against it. It has the same
screens, form rules, wording and payment-safety behaviour as the
[Windows (WPF) sample](../Windows-Native-Example), because it runs **the same code**:

> The web app references `AmwalECR.Example.Core` — the UI-free library the WPF app is built
> on — through a `ProjectReference`. Terminal storage, settings, form validation, config
> resolution, the transaction engine, outcome classification and result presentation are
> not re-implemented here; the web project is a different front end on that logic. Nothing
> was copied out of Core.

Built on the C# `AmwalPay.Ecr` SDK. Vanilla HTML/CSS/JS on the client: **no build step, no
CDN, works offline.**

> ## This sample has no authentication
>
> It can take payments and replace signing keys for anyone who can reach it. It listens on
> `http://127.0.0.1:5080` only, and must stay that way until you have read
> [Before you expose this](#before-you-expose-this).

## Prerequisites

| | |
|---|---|
| **To run** | The **.NET 8 SDK** (`dotnet --version` ≥ 8.0). Windows, Linux or macOS. |
| **The ECR SDK** | Either a sibling checkout of the SDK repository — clone <https://github.com/amwal-pay/AmwalpayEcrWindows> as `AmwalECR-Windows` **next to this repository's folder** (default, `EcrSdkDependency=project`) — or the published NuGet package (`-p:EcrSdkDependency=nuget`, see [SDK dependency](#sdk-dependency-project--nuget)). |
| **Shared code** | `../Windows-Native-Example/` (this repository) — this app references its `AmwalECR.Example.Core` project. |
| **Optional** | Python 3 (fake terminal, wiring check), Node 18+ (amount test, browser smoke test), Chrome (smoke test). |

Layout the default build expects:

```
<workspace>/
  AmwalECR-Windows/                  the SDK             github.com/amwal-pay/AmwalpayEcrWindows
  AmwalECR-Windows-Example/          this repository     github.com/amwal-pay/AmwalpayEcrWindowsExample
    Windows-Native-Example/          WPF sample + AmwalECR.Example.Core (shared)
    AmwalECR-Web-Example/            this app
```

**Supported platforms.** The samples target **.NET 8**, which Microsoft stops supporting
on **Nov 10, 2026**; they should move to `net10.0` (a note, not yet done). The SDK itself
supports **.NET Framework 4.6.2+ (4.7.2+ recommended) and .NET 8+**, with the .NET 8 minimum
becoming .NET 10 after that date and the .NET Framework floor rising to 4.7.2 after Microsoft
ends 4.6.2 on **Jan 12, 2027** unless the SDK owners decide otherwise. See its
[compatibility notes](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/compatibility.md#support-calendar).

## Run it

From this folder (`AmwalECR-Web-Example/`):

```bash
dotnet build                                      # builds src/ and tests/ through AmwalECR-WebExample.sln
dotnet run --project src/AmwalECR.WebExample      # http://127.0.0.1:5080
```

Open <http://127.0.0.1:5080>. Another port or data folder (for a second instance):

```bash
dotnet run --project src/AmwalECR.WebExample -- --urls http://127.0.0.1:5091 --DataDir=/tmp/ecr-scratch
```

| | |
|---|---|
| **Address** | `http://127.0.0.1:5080`, set in `appsettings.json` (`Urls`) and `Properties/launchSettings.json`. Loopback only: the app logs a warning at start-up if it is ever bound elsewhere. Override with `--urls` or `ASPNETCORE_URLS`. |
| **Data folder** | Config key `DataDir`. Blank (the default) means a per-user app-data folder: `~/Library/Application Support/AmwalECR-WebExample` on macOS, `~/.local/share/AmwalECR-WebExample` on Linux, `%LOCALAPPDATA%\AmwalECR-WebExample` on Windows. Created on first run, private to your account on Unix (`0700`). Override with `--DataDir=/path`, the `DataDir` environment variable, or `appsettings.{Environment}.json`. |
| **What is stored** | `terminals.json` (the terminal list), `settings.dat` (environment and both signing keys, encrypted) and `keys/` (the Data Protection key ring that decrypts it). Details: [docs/implementation.md](docs/implementation.md#3-secrets-at-rest). |

### Try it without a terminal

The signed fake terminal lives with the WPF sample — see its
[README](../Windows-Native-Example/README.md) for what each flag does. From this folder:

```bash
python3 -u ../Windows-Native-Example/tools/fake_pos_server_signed.py \
    --key 0123456789ABCDEF0123456789ABCDEF --port 19100
```

(It listens on all interfaces unless you add `--host 127.0.0.1`.)

> **Use a port outside 9100–9199.** The terminals' standard port is 9100, but on a
> developer machine other tooling (the Dart/Flutter tools, for one) may already listen in
> that range and will silently answer instead of your fake. `19100` is safe.

1. **Settings** → paste the same key as the *Wi‑Fi/LAN secure hash key* → **Save key**.
2. **Terminals** → **Add terminal**: any name and serial, mode Wi‑Fi, IP `127.0.0.1`,
   port `19100`.
3. **Transaction** → key an amount → **Start transaction**.

Flags worth trying: `--decline 51`, `--partial`, `--delay 8` (then **Cancel**, or reload the
page), `--drop-sale`, `--drop-sale --drop-first-inquiry` (an unknown outcome you resolve
with **Inquire by reference**), `--not-found`. Web Service terminals need the real backend.

## Parity with the WPF sample

Every WPF feature exists here and is driven by Core.

| WPF sample | Web sample | Driven by |
|---|---|---|
| **Terminals** list / add / edit / delete, keyed by serial | Same screen and dialog; `GET`/`POST /api/terminals`, `PUT`/`DELETE /api/terminals/{serial}` | `TerminalRepository` (same JSON file format), `TerminalValidator` — same fields and messages |
| Modes Wi‑Fi, USB cable, Bluetooth, Web Service; USB and Bluetooth registrable but "not supported in this sample" | Same, with the same hints and notes | `EcrModes`, `SelectedTerminalConfig` |
| Connection summary per terminal | Same text (`127.0.0.1:9100`, `Merchant 12 · Terminal 34`, `USB cable`) | `Terminal.ConnectionSummary()` |
| Environment (SIT/UAT/PROD) and both signing keys, key label per mode, key validation | **Settings** screen; keys checked with `EcrConfig.IsValidSecureHashKey` | `EcrExampleSettings.SecureHashKeyFor(mode)` |
| Secrets encrypted at rest with DPAPI | Encrypted at rest with ASP.NET Core Data Protection, key ring persisted under the data folder | `ISecretProtector` (a second implementation) |
| Transaction screen: terminal dropdown "name (serial)", operation dropdown | Same | `EcrTransactionTypes.MenuOptions` |
| Config card: mode, transport, connection, active key and whether it is set, SDK plan issues — shown **before** paying, refreshed when keys or terminals change | Same card; refreshed on returning to the screen, on selecting a terminal and when the tab regains focus | `SelectedTerminalConfig.Resolve` → `EcrSessions.Plan` |
| Fields shown and required only by the type's flags; dimmed amount with its explanation; different-terminal switch; look up by reference; merchant reference; date defaulting to today, `yyyyMMdd` on the wire | Same. `GET /api/meta` sends the SDK's flags and, for each operation and switch combination, the field layout Core decides; the page carries no copy of the rules | `EcrTransactionType` flags, `TransactionFormState` |
| Validation messages under the fields | Identical strings; the **server** validates the raw form and answers `422` with per-field errors | `TransactionFormState.Validated` |
| Amount entry: no decimal point, digits shift in from the right, `0.000` when empty, caret pinned to the end, backspace, paste, fresh amount on focus, Esc clears; bounds `0.010`–`100000.000` | Same behaviour in `wwwroot/js/amount.js` (checked against Core's vectors, see [Tests](#tests)); the bounds and their messages come from Core | `AmountEntry`, `AmountLimits` |
| Flow: probe first (Wi‑Fi), in progress, result | Same state machine, run **on the server**, one run per terminal | `TransactionEngine`, `TransactionState` |
| Cancel = stop waiting; a cancelled sale is an **unknown outcome** with a look-up, never "cancelled"; the app generates the reference | Same | `TransactionEngine.CancelledState` |
| Unknown outcome offers **Inquire by reference** only, never a retry; `Declined` with `NextStep.InquireByMerchantReference` and Web Service `NO_RESPONSE` are unknown outcomes; auto-inquiry recovery | Same | `TransactionOutcomes` |
| In-flight guard (busy UI) | Enforced **server-side**: `409` for a second transaction on the same terminal | `TransactionRegistry` |
| Closing the window mid-transaction asks first | The browser's own "leave page?" prompt while a transaction runs; the run itself survives and reappears on reload | — |
| Result dialog: approved / declined visuals, partial-approval callout, inquiry summary, Reference, can-void / can-refund hints, raw response, Copy | Same, as a modal `<dialog>` | `ResultPresenter` |
| E-receipt → QR (Wi‑Fi only; Web Service results say so) | Same; the QR is drawn on the server | `ResultPresenter`, `EcrOpenedSession.ReceiptAsync` |
| Debug output panel: last 500 lines, Copy, Clear | Same, polls while open; `GET/DELETE /api/log` | `LogBuffer`, `EcrLogger.From` |
| Transaction / Terminals screens, Esc goes back, brand-blue app bar, light theme | Transaction / Terminals / Settings; the WPF brand colours as CSS custom properties; light **and dark**; responsive to phone width | `Themes/Colors.xaml` |

### Deliberate differences

- **Signing keys are write-only.** The WPF app can show a stored key. This app has no
  authentication, so the API **never returns a stored key** — only `configured: true/false`
  and the key's label, and not even its length. The page has *Configured ✓ / Replace /
  Clear* instead of *Show / Hide*, and a key you type is wiped from the page the moment the
  server has it. A key is checked when saved, and an invalid one is refused; the WPF app
  saves what is typed key by key and flags it.
- **Transactions run on the server and the page polls.** A web request lives for
  milliseconds and a card payment does not, so `POST /api/transactions` starts a run
  (`202`) and the page polls it. Closing the tab, or a dropped request, **never** cancels a
  payment — only the explicit Cancel does, exactly as in WPF — and a reloaded page finds its
  running transaction (or an unread result) again. Why, and how, is
  [the central design](docs/implementation.md#4-the-central-design-transactions-run-on-the-server).
- **The QR is generated server-side** (QRCoder `SvgQRCode`, `GET /api/qr`), so the page needs
  no library and no network.
- **Data Protection instead of DPAPI.** Same `ISecretProtector` seam; the key ring is a file
  under the data folder, so it is only as private as that folder. A lost or copied key ring
  makes `settings.dat` unreadable and the Settings screen says so
  ([details](docs/implementation.md#3-secrets-at-rest)).
- **No USB channel**, as in the WPF sample: the Windows SDK ships none.
- **A different date default.** "Today" is the server's date.
- **Settings is its own tab** (the WPF app puts it above the terminal list).
- One operator per server: state is in memory and the terminal list is one shared file.

## API

JSON in, JSON out. Every mutating call (`POST`, `PUT`, `DELETE`) **must** send
`Content-Type: application/json`, with `{}` if there is nothing to say. Field errors are
`422 { "errors": { "<field>": "<message>" }, "message"?: "…" }`; other errors are
`{ "error": "…" }`. The full reference — request and response shapes, every status code, the
polling contract and a worked curl session — is in
[docs/implementation.md](docs/implementation.md#5-the-api).

| Method and path | Purpose |
|---|---|
| `GET /api/meta` | What the page needs to build its form: operations (SDK flags, per-combination field layouts), modes, environments, amount limits, today's date |
| `GET/POST /api/terminals`, `PUT/DELETE /api/terminals/{serial}` | Terminals, keyed by serial |
| `GET /api/terminals/{serial}/config` | The config card: what the SDK plan makes of the terminal, before paying |
| `GET /api/settings`, `PUT /api/settings/environment` | Environment; **never a key** |
| `PUT/DELETE /api/settings/keys/{wifi\|webService}` | Replace or clear a signing key (write-only) |
| `GET /api/transactions` | Runs not yet acknowledged, newest first (what a reloaded page resumes) |
| `POST /api/transactions` | Start (`202`); `422` field errors; `409` if that terminal is already running one |
| `GET /api/transactions/{id}` | Poll the state |
| `POST /api/transactions/{id}/cancel` | Stop waiting. A money-moving call becomes an **unknown outcome** |
| `POST /api/transactions/{id}/inquire` | Look an unknown outcome up by its reference — the only follow-up |
| `POST /api/transactions/{id}/receipt` | Fetch the e-receipt of an inquiry result (Wi‑Fi) |
| `DELETE /api/transactions/{id}` | The operator has read the result (`409` while it runs) |
| `GET /api/qr?text=` | `image/svg+xml`, drawn on the server |
| `GET/DELETE /api/log` | The last 500 SDK log lines; clear |

Finished transactions nobody acknowledged are kept for 12 hours (an unknown outcome's
reference must survive a night away from the till), then dropped.

## Security

**There is no authentication or authorisation.** Anyone who can open the page can start
payments, add and delete terminals, replace or clear the signing keys, and read the debug
log (which holds masked card numbers). The defaults keep that to the machine it runs on:

- Binds to **`127.0.0.1:5080`** only; `AllowedHosts` limits `Host` (defeats DNS rebinding).
- **Stored keys are never sent to the browser**, and the log never contains one.
- **No CORS**; mutating calls must be `application/json`, and a request from another origin
  (`Origin` / `Sec-Fetch-Site`) is refused with `403`.
- Security headers and a strict CSP (no inline script or style); everything server-supplied
  is rendered with `textContent`; request bodies capped at 64 KB; errors carry no detail.
- The data folder is `0700` on Unix (files inside keep the default mode, typically `0644`, and are
  protected by the folder). The key ring inside it is **not** itself encrypted (Data Protection logs a
  warning saying so), so protect the folder.

How each control works, and what is deliberately missing, is in
[docs/implementation.md](docs/implementation.md#7-security-model).

### Before you expose this

Do not bind it to anything but loopback until you have done all of these (each is expanded
in the [implementation guide](docs/implementation.md#before-you-expose-this)):

1. **Authentication and authorisation** on every endpoint, read-only ones included.
2. **TLS** end to end, `AllowedHosts` set to your real host name(s); a reverse proxy must
   forward the original `Host` and `Origin`, which the guard compares.
3. **Per-user terminal ownership** — today every visitor drives every terminal.
4. **Terminals in a real database, signing keys in a secret store or HSM**, not a local key ring.
5. **Accept that state is single-operator-per-server**: transactions live in process memory (a
   restart forgets a run, though not the terminal's own record — use **Inquire**); scale-out
   needs shared state for the registry and its per-terminal guard.
6. **Audit logging** of who started, cancelled and looked up what.

## SDK dependency (`project` | `nuget`)

`AmwalECR.Example.Core` (in `../Windows-Native-Example`) owns the switch, so the web app has
none of its own; the rule lives in
[`Windows-Native-Example/Directory.Build.props`](../Windows-Native-Example/Directory.Build.props).
By default the SDK is the sibling checkout `<workspace>/AmwalECR-Windows/src/AmwalPay.Ecr`
(see the layout under [Prerequisites](#prerequisites); point `-p:EcrSdkProjectPath=…` at a
checkout elsewhere). To use the NuGet package instead:

```bash
dotnet run --project src/AmwalECR.WebExample -p:EcrSdkDependency=nuget -p:EcrSdkVersion=1.0.0
```

A `-p:` global property reaches Core through the project reference, and Core rejects any
value other than `project` or `nuget`. (Core, the WPF app and this app share Core's `obj`
folder: after switching, restore again in the other mode.)

## What is where

```
AmwalECR-WebExample.sln              src/ and tests/ (Core is reached through the ProjectReference)
docs/implementation.md               how it works and why — read this before changing it
src/AmwalECR.WebExample/
  Program.cs                 composition, middleware order, endpoints
  Hosting/                   DataPaths, DataProtectionSecretProtector, EcrApp (stores + loggers + engine), SecurityMiddleware
  Services/TransactionRegistry.cs   server-side runs: per-terminal guard, server-owned cancellation, eviction
  Api/                       Terminal / Settings / Transaction / Meta / Log+QR endpoints and their DTOs
  wwwroot/                   index.html, app.css, app.js, js/{api,amount,dom,transaction,terminals,settings,log}.js
tests/AmwalECR.WebExample.Tests/    the API against an in-process signed terminal
tools/check-web-wiring.py           page <-> server wiring: element ids, /api routes, imports
tools/smoke-browser.mjs             loads the running app in headless Chrome
tools/test-amount.mjs               amount entry vs Core's vectors, under node
../Windows-Native-Example/
  src/AmwalECR.Example.Core/        the shared, UI-free logic (referenced by this app)
  tools/fake_pos_server_signed.py   the signed fake terminal
```

Reused from Core, unchanged: `TerminalRepository`, `Terminal`, `TerminalValidator`,
`EcrExampleSettings`, `ISecretProtector`, `SelectedTerminalConfig`, `TransactionEngine`,
`TransactionFormState`, `TransactionState`, `TransactionOutcomes`, `ResultPresenter`,
`AmountEntry`, `AmountLimits`, `LogBuffer`. Added here: the host, the server-side transaction
registry, the Data Protection protector, the API, and the browser UI
([why each](docs/implementation.md#1-goals-and-architecture)).

## Tests

```bash
dotnet test                       # the API: in-process host, temp data folder, loopback signed terminal
node tools/test-amount.mjs        # the browser's amount entry against Core's AmountEntry vectors
python3 tools/check-web-wiring.py # page <-> server wiring: element ids, /api routes, imports
PUPPETEER_DIR=/tmp/pp node tools/smoke-browser.mjs   # loads the running app in headless Chrome
(cd ../Windows-Native-Example && dotnet test tests/AmwalECR.Example.Tests)   # Core, still green
```

`dotnet test` covers terminals, settings (keys never echoed, encrypted at rest, surviving a
restart, a lost key ring), config-card issues per mode, form validation per operation
(compared with Core's own answers), the transaction lifecycle against a signed terminal on
loopback (approved, declined, partial, unreachable, a lost answer recovered or resolved by
**Inquire**, cancel during the wait, `409` on a second transaction including eight at once, a
page that goes away mid-payment, eviction), receipt and QR, the log, and the security
headers and guards. What each test class pins is listed in the
[implementation guide](docs/implementation.md#10-tests-and-tools).

### Runtime-only mistakes

The page is plain JavaScript and the server is C#, so nothing compiles one against the other:
a misspelled element id, a call to a route that does not exist, or an import of a name that is
not exported only shows up as an error in the browser. Two checks close that gap (the WPF
sample has the same class of problem with XAML bindings, and a matching script):

- `tools/check-web-wiring.py` needs only Python. It fails when a script looks up an id that
  `index.html` does not define, when `js/api.js` calls a verb + route the server does not map,
  or when an `import { x }` names something the target does not export.
- `tools/smoke-browser.mjs` loads the **running** app in headless Chrome, opens every section,
  and fails on any console error, uncaught exception, CSP block, failed or 4xx/5xx request, or a
  visible fatal-error banner. It uses `puppeteer-core` (install once with
  `npm i --prefix /tmp/pp puppeteer-core`; it is not a dependency of the sample) and your
  installed Chrome (`CHROME=/path/to/chrome` to override). Pass a base URL as the first
  argument for an instance that is not on `:5080`.
