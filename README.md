# Amwal ECR - C# samples

Two runnable tills built on the C# [`AmwalPay.Ecr`](https://github.com/amwal-pay/AmwalpayEcrWindows)
SDK. Each registers terminals, takes an amount, and runs a sale, void, refund, inquiry, or
settlement against an Amwal POS terminal (Wi-Fi or USB cable) or the Amwal Hub (Web Service).
Web Service also has a separate reachability check. Read them as reference code for wiring the
SDK into your own app.

|                     | [Windows-Native-Example](Windows-Native-Example/README.md)         | [AmwalECR-Web-Example](AmwalECR-Web-Example/README.md)           |
| ------------------- | ------------------------------------------------------------------ | ---------------------------------------------------------------- |
| **What it is**      | WPF desktop app                                                    | ASP.NET Core app with a plain HTML/JS page                       |
| **Runs on**         | Windows 10 1809+ / 11                                              | Anything .NET 8 runs on, at `http://127.0.0.1:5080`              |
| **Pick it if**      | You are building a Windows till                                    | You are putting a till behind a web UI                           |
| **How it is built** | [implementation.md](Windows-Native-Example/docs/implementation.md) | [implementation.md](AmwalECR-Web-Example/docs/implementation.md) |

Both use the same UI-free library, `Windows-Native-Example/src/AmwalECR.Example.Core`: terminal
storage, form rules, the transaction engine and the payment-safety rules live there once. Copy
code snippets for your own app from
[operation-examples.md](Windows-Native-Example/docs/operation-examples.md).

The web sample has **no authentication** and listens on loopback only. Read its
[Security](AmwalECR-Web-Example/README.md#security) section before changing that.

## Build and run

The samples depend on the SDK through `Directory.Build.props`. The default,
`EcrSdkDependency=nuget`, restores `AmwalPay.Ecr` 1.0.1 from nuget.org. **That build currently
fails.** The samples call `CloseReceiptAsync`, `SettlementAsync`, `CheckReachabilityAsync`, and
`SerialEcrChannel`. Those members are in the SDK checkout (see its Unreleased changelog) and are
not in the packages on nuget.org (1.0.0 and 1.0.1). Build against the SDK source:

```bash
# SDK checked out next to this folder, as ../AmwalECR-Windows
git clone https://github.com/amwal-pay/AmwalpayEcrWindows ../AmwalECR-Windows

cd Windows-Native-Example && dotnet run --project src/AmwalECR.Example -p:EcrSdkDependency=project
cd AmwalECR-Web-Example   && dotnet run --project src/AmwalECR.WebExample -p:EcrSdkDependency=project
```

Needs the .NET 8 SDK. The WPF app runs on Windows only; it can be built on macOS/Linux with an
SDK that ships the Windows Desktop targets (.NET 10 SDK, not Homebrew's .NET 8). Details are in
each example's README.

No terminal? The signed fake terminal in
[`Windows-Native-Example/tools/`](Windows-Native-Example/README.md#testing-without-a-terminal) stands
in for a Wi-Fi terminal. It does not answer `CLOSE_RECEIPT` or `SETTLEMENT`. USB cable needs the
terminal plugged into this computer. Web Service needs a real Hub environment and credentials.

## What is in the repository

```
Windows-Native-Example/    WPF app, shared Core library, Core tests, docs, tools
AmwalECR-Web-Example/      ASP.NET Core host + wwwroot, API tests, tools
codemagic.yaml             CI: tests, XAML binding check, publish win-x64 / win-arm64
```

## Checks

| Check                | Run from                  | Command                                                                                               |
| -------------------- | ------------------------- | ----------------------------------------------------------------------------------------------------- |
| Core tests (189)     | `Windows-Native-Example/` | `dotnet test tests/AmwalECR.Example.Tests -p:EcrSdkDependency=project`                                |
| Web API tests (113)  | `AmwalECR-Web-Example/`   | `dotnet test tests/AmwalECR.WebExample.Tests -p:EcrSdkDependency=project`                             |
| XAML bindings        | `Windows-Native-Example/` | `python3 tools/check-xaml-bindings.py`                                                                |
| Web wiring           | `AmwalECR-Web-Example/`   | `python3 tools/check-web-wiring.py`                                                                   |
| Browser amount entry | `AmwalECR-Web-Example/`   | `node tools/test-amount.mjs`                                                                          |
| Browser smoke test   | `AmwalECR-Web-Example/`   | `PUPPETEER_DIR=/tmp/pp node tools/smoke-browser.mjs` (app running; needs `puppeteer-core` and Chrome) |

The WPF windows and view-models have no automated tests; the logic that matters is in Core.

## Windows executables for testers

From `Windows-Native-Example/`, a self-contained single file (no .NET needed on the target PC):

```bash
dotnet publish src/AmwalECR.Example/AmwalECR.Example.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:EcrSdkDependency=project -o dist/win-x64
```

Repeat with `-r win-arm64`. [`codemagic.yaml`](codemagic.yaml) does this on an `app-*` tag and zips
each output with `tools/` and `START-HERE.txt`. The builds are unsigned: SmartScreen shows
_More info_, then _Run anyway_. The web sample is published with `dotnet publish` and started with
`dotnet AmwalECR.WebExample.dll --urls http://127.0.0.1:5080` from the publish folder.

## Related

- SDK repository and docs (integration guide, API reference, protocol):
  <https://github.com/amwal-pay/AmwalpayEcrWindows>
- The samples target .NET 8, which Microsoft stops supporting on 10 Nov 2026; plan a move to
  `net10.0`.
