#!/bin/bash
# Run this on the Mac. It packs the SDK + the WPF sample into one zip, serves it
# (and the in-VM setup script) over HTTP, and starts the signed fake terminal, so a
# Windows VM on this Mac can fetch everything with one command and talk to the
# "terminal" - no shared folders, no copying.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NATIVE="$(cd "$HERE/../.." && pwd)"                 # .../AmwalECR-Windows-Example/Windows-Native-Example
EXAMPLES="$(cd "$NATIVE/.." && pwd)"               # .../AmwalECR-Windows-Example  (native + web samples)
SDK="$(cd "$EXAMPLES/../AmwalECR-Windows" && pwd)" # the SDK checkout next to it
SHARE="$HERE/.share"
HTTP_PORT="${HTTP_PORT:-8000}"
ECR_PORT="${ECR_PORT:-9100}"
# 32 hex chars = a valid LAN signing key. Paste the same one into the app.
KEY="${ECR_KEY:-0123456789ABCDEF0123456789ABCDEF}"

rm -rf "$SHARE" && mkdir -p "$SHARE/stage"
# The two repositories stay siblings, exactly as on a developer machine: the sample's
# default `project` mode references ..\..\AmwalECR-Windows\src\AmwalPay.Ecr. Build output,
# IDE state and previous publishes stay out.
for dir in "$SDK" "$EXAMPLES"; do
  name="$(basename "$dir")"
  rsync -a --exclude 'bin/' --exclude 'obj/' --exclude '.vs/' --exclude '.git/' --exclude '.idea/' \
        --exclude '.share/' --exclude 'dist/' --exclude 'artifacts/' --exclude 'node_modules/' \
        "$dir/" "$SHARE/stage/$name/"
done
(cd "$SHARE/stage" && zip -qr "$SHARE/amwal-ecr.zip" .)
cp "$HERE/Setup-And-Run.ps1" "$SHARE/"
rm -rf "$SHARE/stage"

# Every non-loopback IPv4 this Mac has; which one the VM can reach depends on the
# UTM network mode (see README.md next to this script).
IPS=$(ifconfig | awk '/inet / && $2 != "127.0.0.1" {print $2}')

cleanup() { kill "${FAKE_PID:-}" "${HTTP_PID:-}" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

python3 "$NATIVE/tools/fake_pos_server_signed.py" --key "$KEY" --port "$ECR_PORT" &
FAKE_PID=$!
(cd "$SHARE" && python3 -m http.server "$HTTP_PORT" --bind 0.0.0.0 >/dev/null 2>&1) &
HTTP_PID=$!
sleep 1

echo
echo "Fake terminal : port $ECR_PORT, LAN signing key $KEY"
echo "Package server: port $HTTP_PORT  ($(du -h "$SHARE/amwal-ecr.zip" | cut -f1) zip)"
echo "This Mac's IPs: $(echo $IPS | tr '\n' ' ')"
echo
echo "In the Windows VM (PowerShell), replace <MAC-IP> with one of the IPs above:"
echo "  iwr http://<MAC-IP>:$HTTP_PORT/Setup-And-Run.ps1 -OutFile \$env:TEMP\\setup.ps1"
echo "  powershell -ExecutionPolicy Bypass -File \$env:TEMP\\setup.ps1 -HostIp <MAC-IP>"
echo
echo "Ctrl+C stops both servers."
wait
