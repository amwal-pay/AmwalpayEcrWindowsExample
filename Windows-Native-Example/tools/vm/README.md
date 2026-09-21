# Running the sample in a Windows VM on a Mac

WPF only runs on Windows, and Windows containers cannot show a window, so a VM is
the way to see the WPF sample on a Mac. These scripts do everything except installing
Windows itself. (Background on the sample: [../../README.md](../../README.md),
[../../docs/implementation.md](../../docs/implementation.md); the repository overview is the
[umbrella README](../../../README.md). The web sample needs no VM: it runs on the Mac.)

Needs on the Mac: Python 3, `zip`, `rsync`, and the two repositories checked out as siblings
(`AmwalECR-Windows` for the SDK and `AmwalECR-Windows-Example` for the samples, exactly those
folder names). In the VM: Windows 10 1809+ / 11 and internet access for `winget`.

## 1. Create the Windows VM (once)

Apple silicon: use **Windows 11 ARM** (x86 Windows would be emulated and slow).

1. Open **UTM** -> *Create a New Virtual Machine* -> **Virtualize** -> **Windows**.
2. Tick **Install Windows 10 or higher**. UTM links to a helper that downloads the
   Windows 11 ARM ISO from Microsoft - pick that ISO as the boot image. Also tick
   *Install drivers and SPICE tools* (gives clipboard sharing and a resizable screen).
3. Sizing: 4 CPU cores, 8 GB RAM, 64 GB disk. Finish and start the VM, then install
   Windows (choose "I don't have a product key"; sign in with a local account by
   skipping the network step if the installer allows it).
4. Networking: keep UTM's default **Shared Network**. The Mac is then reachable from
   the VM at the VM's default gateway (usually `192.168.64.1`). *Bridged* also works;
   use the Mac's LAN IP instead.

## 2. Serve the SDK, the sample and the fake terminal (on the Mac)

```bash
AmwalECR-Windows-Example/Windows-Native-Example/tools/vm/prepare-on-mac.sh
```

(Run it from the folder that holds both repositories, or give the full path; the script finds its
own location.) It zips the SDK checkout and the samples repository — both samples, without `bin/`,
`obj/`, `dist/` and similar — keeping them as siblings so the sample's default `project` SDK mode
resolves in the VM as on a developer machine. It serves the zip on port 8000, starts the signed
fake terminal on port 9100 and prints the commands to paste into the VM.
If macOS asks whether Python may accept incoming connections, choose **Allow**.
Options: `ECR_KEY=<hex>` (default `0123456789ABCDEF0123456789ABCDEF`), `HTTP_PORT`, `ECR_PORT`.

> **Port 9100 may already be taken on a Mac that runs Flutter/Dart tooling**, which listens in
> 9100–9199 and can answer instead of the fake terminal or stop it from binding. If so, start with
> `ECR_PORT=19100 .../prepare-on-mac.sh` and use port `19100` in the app below.

## 3. Run it (in the VM, PowerShell)

```powershell
iwr http://<MAC-IP>:8000/Setup-And-Run.ps1 -OutFile $env:TEMP\setup.ps1
powershell -ExecutionPolicy Bypass -File $env:TEMP\setup.ps1 -HostIp <MAC-IP>
```

It installs the .NET 8 SDK with winget if missing, downloads and extracts the zip to
`C:\amwal` (giving `C:\amwal\AmwalECR-Windows` and `C:\amwal\AmwalECR-Windows-Example`), and
runs `dotnet run --project src\AmwalECR.Example -c Release` from
`C:\amwal\AmwalECR-Windows-Example\Windows-Native-Example`. The first run compiles, so allow a
minute. In the app:

1. **Terminals** (top right) -> **Add terminal**: mode Wi-Fi, IP `<MAC-IP>`, port `9100` (or
   the `ECR_PORT` you used), any name and serial.
2. On the same page set the **Wi-Fi/LAN secure hash key** to the key the script printed.
3. **Back**, pick the terminal, Sale, key an amount, **Start transaction**.

To rehearse the unhappy paths, the script's own fake terminal answers plainly, so start a second
one by hand on the Mac on another port, with a flag, and register a terminal at that port:

```bash
python3 Windows-Native-Example/tools/fake_pos_server_signed.py --key <same-hex> --port 19101 --decline 51
```

Other flags: `--partial`, `--drop-sale`, `--not-found`, `--delay 3`. Every flag and what it shows
is in the [WPF README](../../README.md#testing-without-a-terminal).

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| VM cannot download the zip | Wrong Mac IP for the UTM network mode, or the macOS firewall is blocking Python. Test with `Test-NetConnection <MAC-IP> -Port 8000` |
| App says terminal unreachable | Same check on port 9100, and the terminal's IP in the app must be the Mac's IP as the VM sees it |
| "The answer was not signed with this terminal's key" | The key in the app differs from the fake terminal's `--key` |
| `winget` not found | Update *App Installer* from the Microsoft Store, or install the .NET 8 SDK manually from dotnet.microsoft.com |
| `prepare-on-mac.sh` fails on `../AmwalECR-Windows` | The SDK must be checked out next to the samples repository, named `AmwalECR-Windows` |
| The VM script cannot find `AmwalECR-Windows-Example\Windows-Native-Example` | The samples repository folder on the Mac has another name; the script and zip use the folder's own name, and `Setup-And-Run.ps1` expects `AmwalECR-Windows-Example` |
| Connecting to the fake terminal reaches something else, or the fake will not start | Another process owns port 9100 (Dart/Flutter tooling does); use `ECR_PORT=19100` |
