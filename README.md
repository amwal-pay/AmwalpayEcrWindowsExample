# Amwal ECR — C# samples

> Built for **`AmwalPay.Ecr` 1.0.0** (first NuGet release of the Windows/SDK package). Default
> `project` mode builds against a sibling `AmwalECR-Windows` checkout; for the published package
> use `-p:EcrSdkDependency=nuget -p:EcrSdkVersion=1.0.0`.

Two runnable samples for the C# **`AmwalPay.Ecr`** SDK, which lets a till (an *ECR*) take card
payments on an Amwal POS terminal over the shop network or the Amwal Hub. Both are a working
register: register the terminals you own, key an amount, pick a terminal, run a sale, void, refund
or inquiry (by receipt number or merchant reference), and read the result. They port the Kotlin
**ECR simulator** (Android) — same screens, form rules and wording — and are meant to be read as
much as run.

| | [Windows-Native-Example](Windows-Native-Example/README.md) | [AmwalECR-Web-Example](AmwalECR-Web-Example/README.md) |
|---|---|---|
| **What it is** | A WPF desktop app | An ASP.NET Core app with a plain HTML/CSS/JS front end |
| **Platform** | Runs on Windows 10 1809+ / 11 | Runs anywhere .NET 8 does; open `http://127.0.0.1:5080` |
| **Run it** | `dotnet run --project src/AmwalECR.Example` (from `Windows-Native-Example/`) | `dotnet run --project src/AmwalECR.WebExample` (from `AmwalECR-Web-Example/`) |
| **Shows** | An SDK integration in a real till: XAML/MVVM shell, DPAPI-encrypted keys, POS-style amount entry, result dialog, receipt QR | The same logic behind an HTTP API: server-owned transactions that survive a page reload, write-only keys, a security-headers baseline |
| **Read next** | [docs/implementation.md](Windows-Native-Example/docs/implementation.md) | [docs/implementation.md](AmwalECR-Web-Example/docs/implementation.md) |

The web sample has **no authentication** and listens on loopback only; read its README before
changing that.

## Layout

```
AmwalECR-Windows-Example/
  README.md                          this file
  codemagic.yaml                     CI: tests, XAML check, publish win-x64 and win-arm64
  Windows-Native-Example/
    AmwalECR-Example.sln
    Directory.Build.props            SDK switch (project | nuget), C# 12, EnableWindowsTargeting
    src/AmwalECR.Example.Core/       UI-free net8.0 logic: data, form rules, engine, outcomes
    src/AmwalECR.Example/            the WPF shell (net8.0-windows): views, view-models, behaviours
    tests/AmwalECR.Example.Tests/    xUnit tests of Core, incl. a signed loopback terminal
    tools/                           fake_pos_server_signed.py, check-xaml-bindings.py, vm/
    docs/implementation.md
  AmwalECR-Web-Example/
    src/AmwalECR.WebExample/         host, API, wwwroot (no build step)
    tests/AmwalECR.WebExample.Tests/ API tests against an in-process host
    tools/                           check-web-wiring.py, smoke-browser.mjs, test-amount.mjs
```

## How the two relate

The web project has a `ProjectReference` to
`Windows-Native-Example/src/AmwalECR.Example.Core`. Terminal storage, settings, form validation,
config resolution, the transaction engine, outcome classification and result presentation are one
implementation; the samples differ only in the front end. So the rules are identical in both:

- the fields shown and required follow `EcrTransactionType` flags, and the error strings match;
- an unknown outcome is never a refusal and never a reason to retry: the only follow-up is
  **inquire by reference** (LAN and Web Service both support that lookup in SDK 1.0.0);
- inquiry by STAN hides the date when looking up by reference (same as the Android simulator);
- Cancel stops *waiting*, it does not cancel a payment;
- the config card shows what the SDK plan says is missing *before* you pay.

Both depend on the SDK through Core, so the `project`/`nuget` choice below applies to both.

## Prerequisites

- **.NET 8 SDK** builds and runs Core, the tests and the web sample on Windows, macOS and Linux.
  Microsoft ends support for .NET 8 on **Nov 10, 2026**, so the samples should move to
  `net10.0` (a note; not yet done). The SDK supports .NET Framework 4.6.2+ and .NET 8+; see its
  [compatibility notes](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/compatibility.md#support-calendar).
- **Running the WPF app** needs Windows 10 1809+ or Windows 11. It targets `net8.0-windows`.
- **Building the WPF app on macOS/Linux** works (`EnableWindowsTargeting` is on) but needs an SDK
  that ships the Windows Desktop targets, such as the **.NET 10 SDK**. Homebrew's .NET 8 SDK does
  not have them and fails with `MSB4019`. You cannot *run* it there; use Windows or the VM route in
  [`tools/vm/`](Windows-Native-Example/tools/vm/README.md).
- Python 3 for the fake terminal and the check scripts; Node for the web amount test.

## Getting the SDK

The SDK is a separate repository. The default mode, `EcrSdkDependency=project`, builds against a
**sibling checkout named `AmwalECR-Windows`** next to this repository's folder:

```bash
git clone https://github.com/amwal-pay/AmwalpayEcrWindows AmwalECR-Windows
git clone https://github.com/amwal-pay/AmwalpayEcrWindowsExample AmwalECR-Windows-Example
```

Use those folder names: the SDK path is `../AmwalECR-Windows` relative to this folder, and the VM
script expects `AmwalECR-Windows-Example`. A missing SDK stops the build with a message saying so.
To point elsewhere: `-p:EcrSdkProjectPath=/path/to/AmwalPay.Ecr.csproj`. To use the published
package instead: `-p:EcrSdkDependency=nuget -p:EcrSdkVersion=1.0.0` (any other value is rejected). In that
mode build `Windows-Native-Example/AmwalECR-Example.NuGet.slnf`, not the `.sln`, which lists the SDK
project and so needs the sibling checkout ([details](Windows-Native-Example/README.md#choosing-how-the-app-gets-the-sdk)).

## Quick start

**WPF sample** (on Windows):

```powershell
cd Windows-Native-Example
dotnet run --project src/AmwalECR.Example
```

**Web sample** (any OS):

```bash
cd AmwalECR-Web-Example
dotnet run --project src/AmwalECR.WebExample     # then open http://127.0.0.1:5080
```

### Wi‑Fi / LAN (fake or real terminal)

Neither sample needs a real terminal to try LAN: use the fake one below. Then, in either app,
add the SDK's LAN signing key as the *Wi‑Fi/LAN secure hash key*, add a Wi‑Fi terminal at
`127.0.0.1` with the fake's port, and start a sale.

### Web Service initialization (`ecrMode: 4`)

Web Service does **not** use the terminal's LAN IP. The till calls the Amwal Hub over HTTPS;
there is no fake Hub in this repo. You need from Amwal / TMS:

| You supply | Where in the samples |
|---|---|
| Environment (`SIT` / `UAT` / `PROD`) | Settings (WPF: above the terminal list; web: **Settings** tab) |
| **Web Service** secure hash key (not the Wi‑Fi key) | Same Settings — *Web Service secure hash key* |
| Terminal serial | **Add terminal** |
| Merchant ID + terminal ID (numeric) | **Add terminal**, mode **Web Service** |

Then:

1. Save environment and the Web Service key (validated as even-length hex, ≥ 16 chars).
2. Register a terminal with mode **Web Service**, serial, merchant ID, terminal ID (no IP/port).
3. On the Transaction screen, select that terminal — the config card must show **ready**
   (`SelectedTerminalConfig` → `EcrSessions.Plan` → `IsReady`). Typical blockers: missing IDs,
   non-numeric IDs, or *"Web Service secure hash key is not configured"*.
4. Start a sale. The Hub is contacted only then (`EcrSessions.Open` → `SaleAsync`). Store or
   note the merchant reference before the call so you can **Inquire by reference** after a
   lost answer.

Hub hosts are resolved by the SDK from the environment (SIT/UAT/PROD); the samples do not
configure base URLs. Full UI steps:
[WPF — Web Service initialization](Windows-Native-Example/README.md#web-service-initialization)
and [web — Web Service initialization](AmwalECR-Web-Example/README.md#web-service-initialization).
SDK checklist:
[integration guide §4](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/integration-guide.md#initialization-process).

## The fake terminal

[`Windows-Native-Example/tools/fake_pos_server_signed.py`](Windows-Native-Example/tools/fake_pos_server_signed.py)
stands in for a terminal: it verifies the request signature and nonce, answers in the current
signed envelope and can decline, part-approve, delay, drop the answer or report "not found".
(The SDK repo's older `tools/fake_pos_server.py` answers unsigned and **cannot** be used with this
SDK.)

```bash
python3 Windows-Native-Example/tools/fake_pos_server_signed.py --key 0123456789ABCDEF0123456789ABCDEF --port 19100
```

**Use a port outside 9100–9199.** 9100 is the terminals' standard port, but Dart/Flutter tooling
listens in that range on developer machines and will answer instead of your fake (this was seen
listening on 9100 while checking these docs). `19100` is safe. Flags and what each shows are in the
[WPF README](Windows-Native-Example/README.md#testing-without-a-terminal).

## Tests and checks

| Check | Run from | Command | Catches |
|---|---|---|---|
| Core tests (186) | `Windows-Native-Example/` | `dotnet test tests/AmwalECR.Example.Tests` | Rule regressions: amount entry, form validation, config resolution, storage and DPAPI-failure paths, every outcome classification, result wording, and the whole flow against a signed loopback terminal. |
| WPF compile | `Windows-Native-Example/` | `dotnet build AmwalECR-Example.sln` (a Windows Desktop SDK, e.g. .NET 10) | XAML and C# errors in the WPF project (warnings are errors). Not runtime behaviour. |
| XAML bindings | `Windows-Native-Example/` | `python3 tools/check-xaml-bindings.py` | A two-way-by-default binding onto a read-only property, which throws when the window loads. |
| Web tests (110) | `AmwalECR-Web-Example/` | `dotnet test` | The HTTP API: CRUD and messages, keys never echoed, per-terminal `409` guard, lifecycle against a loopback terminal, QR, log, security headers. |
| Web wiring | `AmwalECR-Web-Example/` | `python3 tools/check-web-wiring.py` | Page-to-server mismatches: unknown element ids, unmapped `/api` routes, bad imports. |
| Browser amount | `AmwalECR-Web-Example/` | `node tools/test-amount.mjs` | The browser's amount entry drifting from Core's `AmountEntry` vectors. |
| Browser smoke | `AmwalECR-Web-Example/` | `PUPPETEER_DIR=/tmp/pp node tools/smoke-browser.mjs` (app running; needs `puppeteer-core` and Chrome) | Console errors, CSP blocks, failed requests or a fatal banner when the real page loads. |

The web tests reference Core, so a break in shared logic fails both suites. There are no UI
automation tests for the WPF app and its view-models are not unit-tested; see the
[known limitations](Windows-Native-Example/docs/implementation.md#o-known-limitations).

## Documentation

- [Windows-Native-Example/README.md](Windows-Native-Example/README.md): what the WPF app does, the
  amount and form rules, [Web Service initialization](Windows-Native-Example/README.md#web-service-initialization),
  inquiry and unknown outcomes, testing without a terminal.
- [Windows-Native-Example/docs/implementation.md](Windows-Native-Example/docs/implementation.md):
  layering, wiring, persistence, the SDK call path (including Web Service plan/open), the state
  machine, threading, XAML pitfalls, tests, extension recipes.
- [AmwalECR-Web-Example/README.md](AmwalECR-Web-Example/README.md) and its
  [docs/implementation.md](AmwalECR-Web-Example/docs/implementation.md): the web sample (same Core
  init path behind HTTP).
- The SDK's own `docs/` (integration guide — [Web Service init](https://github.com/amwal-pay/AmwalpayEcrWindows/blob/main/docs/integration-guide.md#initialization-process),
  API reference, wire protocol) in `github.com/amwal-pay/AmwalpayEcrWindows`.

## Building Windows executables

Self-contained, single-file, so the target machine needs no .NET (from `Windows-Native-Example/`;
also works from macOS with the .NET 10 SDK):

```bash
dotnet publish src/AmwalECR.Example/AmwalECR.Example.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o ../dist/win-x64
```

Repeat with `-r win-arm64`. Zip each folder with `tools/fake_pos_server_signed.py` and
`START-HERE.txt` (as [`codemagic.yaml`](codemagic.yaml) does on an `app-*` tag). Local output is
usually `<repo>/dist/AmwalECR-Example-win-x64.zip` and `...-win-arm64.zip`.

### What to share for testing

| Artifact | Audience |
|---|---|
| `dist/AmwalECR-Example-win-x64.zip` | Most Windows PCs (Intel/AMD) — unzip, run `AmwalECR.Example.exe`, read `START-HERE.txt` |
| `dist/AmwalECR-Example-win-arm64.zip` | Windows on ARM |
| Web: publish `AmwalECR.WebExample` and zip the output | Testers with .NET 8 who prefer the browser UI (`dotnet AmwalECR.WebExample.dll --urls http://127.0.0.1:5080`) |

The WPF builds are **unsigned**, so Windows SmartScreen may warn on first run: *More info*, then
*Run anyway*. For LAN testing without hardware, include or point testers at
`tools/fake_pos_server_signed.py` (Python 3). Web Service testing needs a real Hub environment,
merchant/terminal IDs, serial, and the Web Service signing key.
