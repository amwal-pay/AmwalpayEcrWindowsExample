# Amwal ECR - web sample

The ASP.NET Core front end of the Amwal ECR samples: register POS terminals, key an amount in
the browser, pick a terminal, run a sale, void, refund, inquiry, or settlement, and, on Web
Service, check reachability. It runs **the same Core
library** as the [WPF sample](../Windows-Native-Example/README.md) (terminal storage, form rules,
transaction engine, outcome classification, result wording), so behaviour and wording match. The
client is plain HTML/CSS/ES modules: no build step, no CDN, works offline.

- How it is built (server-owned transactions, API, security model): [docs/implementation.md](docs/implementation.md)
- Copy-paste SDK code: [operation-examples.md](../Windows-Native-Example/docs/operation-examples.md)

> **This sample has no authentication.** Anyone who can reach it can take payments and replace
> signing keys. It listens on `http://127.0.0.1:5080` only; read [Security](#security) before
> changing that.

## Run it

Needs the .NET 8 SDK (Windows, Linux or macOS) and the SDK source next to the samples repository
as `../AmwalECR-Windows` (see the [root README](../README.md#build-and-run) for why the default
NuGet build currently fails). From this folder:

```bash
dotnet run --project src/AmwalECR.WebExample -p:EcrSdkDependency=project
```

Open <http://127.0.0.1:5080>. A second instance on another port and data folder:

```bash
dotnet run --project src/AmwalECR.WebExample -p:EcrSdkDependency=project -- --urls http://127.0.0.1:5091 --DataDir=/tmp/ecr-scratch
```

A published build must be started **from its own folder** (`dotnet AmwalECR.WebExample.dll --urls
http://127.0.0.1:5080`), because the content root is the current directory: from elsewhere `/` is
a 404 and `appsettings.json` is not loaded.

## Configure it

| Setting        | Default                                                             | Override                                               |
| -------------- | ------------------------------------------------------------------- | ------------------------------------------------------ |
| `Urls`         | `http://127.0.0.1:5080` (`appsettings.json`, `launchSettings.json`) | `--urls`, `ASPNETCORE_URLS`                            |
| `AllowedHosts` | `127.0.0.1;localhost;[::1]`                                         | `AllowedHosts` env or `appsettings.{Environment}.json` |
| `DataDir`      | blank = per-user app-data folder `AmwalECR-WebExample`              | `--DataDir=/path`, `DataDir` env                       |

The default data folder is `%LOCALAPPDATA%\AmwalECR-WebExample` on Windows,
`~/Library/Application Support/AmwalECR-WebExample` on macOS and
`~/.local/share/AmwalECR-WebExample` on Linux. It holds `terminals.json`, `settings.dat`
(environment and both signing keys, encrypted) and `keys/` (the Data Protection key ring that
decrypts it). On Unix a folder the app creates is `0700`.

In the UI, keys, environment and terminals are set on the **Terminals** screen, exactly as in
WPF: a _Wi-Fi secure hash key_ card, a _Web Service_ card (environment `SIT`/`UAT`/`PROD` +
Web Service key), and the terminal list. Terminal records hold addresses only (Wi-Fi: IP and
port; Web Service: merchant and terminal IDs), never a key. Keys are validated when saved (even
-length hex, at least 16 characters) and an invalid one is refused.

### Wi-Fi, without a terminal

The signed fake terminal lives with the WPF sample; its flags are listed in the
[WPF README](../Windows-Native-Example/README.md#testing-without-a-terminal). From this folder:

```bash
python3 -u ../Windows-Native-Example/tools/fake_pos_server_signed.py \
    --key 0123456789ABCDEF0123456789ABCDEF --port 19100 --host 127.0.0.1
```

Use a port outside 9100-9199 (Dart/Flutter tooling listens there). Then: **Terminals** -> paste
the same key as the Wi-Fi key -> **Add terminal** (Wi-Fi, IP `127.0.0.1`, port `19100`) ->
**Transaction** -> amount -> **Start transaction**. Useful flags: `--decline 51`, `--partial`,
`--delay 8` (then **Cancel**, or reload the page), `--drop-sale`,
`--drop-sale --drop-first-inquiry`, `--not-found`.

### Web Service

Calls the Amwal Hub over HTTPS (`ecrMode: 4`), not a terminal IP. No fake Hub exists; you need
credentials from Amwal. Set the environment and the **Web Service** key (not the Wi-Fi key) on
the Terminals screen, add a terminal in mode Web Service (serial, numeric merchant and terminal
IDs), and select it on the Transaction screen; the config card
(`GET /api/terminals/{serial}/config` -> `isReady: true`) must read ready before **Start**.
Typical issues: missing or non-numeric IDs, or `Web Service secure hash key is not configured`.
**Check reachability** posts `/Ecr/CheckReachability` and does not start a sale. A sale still
does not probe first; keep the merchant reference so **Inquire by reference** can resolve a
lost answer.

## What it demonstrates

Everything the [WPF sample demonstrates](../Windows-Native-Example/README.md#what-it-demonstrates)
(operations, amount entry, unknown outcomes, inquiry, settlement, Check reachability, Close
receipt, QR) plus:

- **Transactions run on the server and the page polls.** `POST /api/transactions` starts a run
  (`202`); closing the tab or dropping a request never cancels a payment, only the explicit
  Cancel does, and a reloaded page finds its running or unread transaction again.
- **A countdown driven by the server.** Transaction responses carry `timeoutSeconds` (the
  configured response timeout: 120 s Wi-Fi, 130 s Web Service) and `remainingSeconds` (counted on
  the server, `null` once finished); the page counts `m:ss` down from the server's value.
- **Cancel only stops waiting.** A cancelled sale, void or refund becomes an unknown outcome
  with **Inquire by reference**, never "cancelled".
- **Manual settlement** confirms, then `POST /api/transactions/settlement`. Wi-Fi and USB send
  `SETTLEMENT`; Web Service posts `/Ecr/settle`. The result is **Settled**, **Nothing to
  settle**, or **Not settled**. A lost answer is **Not completed**; repeat the call. Cancelling
  the wait returns to idle.
- **Check reachability** is its own button on a Web Service terminal
  (`POST /api/transactions/reachability`). It does not start a transaction. Wi-Fi and USB still
  probe automatically before a sale.
- **Close receipt** is its own button on a completed sale, void, or refund, and on a settled
  result over Wi-Fi or USB (`POST /api/transactions/{id}/close-receipt`): Wi-Fi and USB
  `CLOSE_RECEIPT`, Web Service `/Ecr/closeReceipt` with the sale, void, or refund merchant
  reference. Closing the dialog does not send it. A Web Service settlement result does not
  carry that reference.
- **One running transaction per terminal**, enforced by the server (`409`).
- **Write-only keys.** The API never returns a stored key, not even its length: only
  `configured: true/false` and the key's label. The page offers _Replace / Clear_, not _Show_.
- **The QR is drawn on the server** (`GET /api/qr`, SVG), so the page needs no library.
- Light and dark themes and a phone-width layout.

## API

JSON in, JSON out. Mutating calls (`POST`, `PUT`, `DELETE`) **must** send
`Content-Type: application/json` (`{}` if there is nothing to say). Field errors are
`422 { "errors": { "<field>": "<message>" }, "message"?: "..." }`; other errors are
`{ "error": "..." }`. Shapes, status codes and a curl session:
[docs/implementation.md, the API](docs/implementation.md#5-the-api).

| Route                                                           | Purpose                                                                                          |
| --------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| `GET /api/meta`                                                 | Operations (SDK flags, per-combination field layouts), modes, environments, amount limits, today |
| `GET/POST /api/terminals`, `PUT/DELETE /api/terminals/{serial}` | Terminals, keyed by serial                                                                       |
| `GET /api/terminals/{serial}/config`                            | The config card: what the SDK plan makes of the terminal, before paying                          |
| `GET /api/settings`, `PUT /api/settings/environment`            | Environment; never a key                                                                         |
| `PUT/DELETE /api/settings/keys/{wifi\|webService}`              | Replace or clear a key (write-only)                                                              |
| `GET/POST /api/transactions`                                    | Un-acknowledged runs (newest first); start one (`202`, `422`, `409`)                             |
| `GET /api/transactions/{id}`                                    | Poll the state                                                                                   |
| `POST /api/transactions/{id}/cancel`                            | Stop waiting                                                                                     |
| `POST /api/transactions/{id}/inquire`                           | Look an unknown outcome up by its reference                                                      |
| `POST /api/transactions/{id}/receipt`                           | Fetch the e-receipt of an inquiry result (Wi-Fi and USB cable)                                   |
| `POST /api/transactions/settlement`                             | Close the current batch (`202`, same poll as a sale)                                            |
| `POST /api/transactions/reachability`                           | Web Service only: `{ reachable, message }`. Does not start a transaction                         |
| `POST /api/transactions/{id}/close-receipt`                     | Ask the terminal to put its receipt away                                                         |
| `DELETE /api/transactions/{id}`                                 | The operator has read the result (`409` while running)                                           |
| `GET /api/qr?text=`                                             | `image/svg+xml`                                                                                  |
| `GET/DELETE /api/log`                                           | The last 500 SDK log lines; clear                                                                |

Finished transactions nobody acknowledged are kept for 12 hours, then dropped.

## Security

There is no authentication or authorisation. Anyone who can open the page can start payments,
edit terminals, replace or clear keys and read the debug log (masked card numbers). The defaults
keep that to the machine it runs on:

- Binds to `127.0.0.1:5080`; `AllowedHosts` limits `Host` (defeats DNS rebinding); a start-up
  warning is logged for any non-loopback address.
- Stored keys are never sent to the browser or logged.
- No CORS; mutating calls must be `application/json`; a request from another origin
  (`Origin` / `Sec-Fetch-Site`) is refused with `403`.
- Security headers and a strict CSP (no inline script or style); server text is rendered with
  `textContent`; bodies capped at 64 KB; errors carry no detail.
- The key ring in `keys/` is **not** itself encrypted (Data Protection logs a warning); the
  `0700` folder is what protects it.

### Before you expose this

Do not bind to anything but loopback until you have added: authentication and authorisation on
every endpoint (read-only ones included); TLS, with `AllowedHosts` set to your host names and a
reverse proxy that forwards the original `Host`; per-user terminal ownership; terminals in a
database and keys in a secret store; shared state if you run more than one instance (runs live
in process memory); and audit logging. Details:
[docs/implementation.md, security model](docs/implementation.md#7-security-model).

## Tests and checks

```bash
dotnet test tests/AmwalECR.WebExample.Tests -p:EcrSdkDependency=project   # 113 tests: in-process host, temp data folder, signed loopback terminal
python3 tools/check-web-wiring.py     # page <-> server wiring: element ids, /api routes, imports
node tools/test-amount.mjs            # browser amount entry against Core's AmountEntry vectors
PUPPETEER_DIR=/tmp/pp node tools/smoke-browser.mjs   # app running; needs puppeteer-core and Chrome
```

What each test class pins: [docs/implementation.md, tests](docs/implementation.md#10-tests-and-tools).

## Known limits

- Single operator, single process: runs and the log are in memory, a restart forgets runs (use
  **Inquire** with a reference from the log or the till), and two instances do not share the
  per-terminal guard.
- The server guards a _running_ run, not an unread unknown outcome; the page is what prevents a
  second sale while that result is open.
- Web Service is not exercised against a real Hub; the smoke test drives Chrome only.
- USB cable uses the same session as the native sample, including settlement. The web process must run on the computer the cable is plugged into. Choose the MediaTek USB VCOM device (115200 8N1). Windows assigns the COM port from the device serial, and the number can change. Bluetooth and app-to-app are not offered. The signed fake terminal does not answer `SETTLEMENT` or `CLOSE_RECEIPT`.
- "Today" is the server's date.
