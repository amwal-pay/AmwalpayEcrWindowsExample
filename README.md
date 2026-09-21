# Amwal ECR — C# samples

Two runnable samples for the C# **`AmwalPay.Ecr`** SDK, which lets a till (an *ECR*) take card
payments on an Amwal POS terminal over the shop network or the Amwal Hub. Both are a working
register: register the terminals you own, key an amount, pick a terminal, run a sale, void, refund
or inquiry, and read the result. They port the Kotlin **ECR simulator** (Android) — same screens,
form rules and wording — and are meant to be read as much as run.

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
  **inquire by reference**;
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

Neither needs a real terminal to try: use the fake one below. Then, in either app, add the SDK's LAN
signing key as the *Wi-Fi/LAN secure hash key*, add a Wi-Fi terminal at `127.0.0.1` with the fake's
port, and start a sale. Web Service terminals need the real Amwal backend and its own key.

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
  amount and form rules, inquiry and unknown outcomes, testing without a terminal.
- [Windows-Native-Example/docs/implementation.md](Windows-Native-Example/docs/implementation.md):
  layering, wiring, persistence, the SDK call path, the state machine, threading, XAML pitfalls,
  tests, extension recipes.
- [AmwalECR-Web-Example/README.md](AmwalECR-Web-Example/README.md) and its
  [docs/implementation.md](AmwalECR-Web-Example/docs/implementation.md): the web sample.
- The SDK's own `docs/` (integration guide, API reference, wire protocol) in
  `github.com/amwal-pay/AmwalpayEcrWindows`.

## Building Windows executables

Self-contained, single-file, so the target machine needs no .NET (from `Windows-Native-Example/`;
also works from macOS with the .NET 10 SDK):

```bash
dotnet publish src/AmwalECR.Example/AmwalECR.Example.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/win-x64
```

Repeat with `-r win-arm64`. The output is one ~66 MB `AmwalECR.Example.exe`; a local publish lands
in `Windows-Native-Example/dist/` (zips per runtime: `AmwalECR-Example-win-x64.zip` and
`...-win-arm64.zip`, which also carry `tools/fake_pos_server_signed.py`). [`codemagic.yaml`](codemagic.yaml)
does the same in CI on an `app-*` tag and writes to `<repo>/dist/`. The executables are **unsigned**,
so Windows SmartScreen warns on first run: *More info*, then *Run anyway*.
