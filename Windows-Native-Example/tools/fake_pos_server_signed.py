#!/usr/bin/env python3
"""Stand-in for the POS application's ECR listener -- signed edition.

A copy of ../../../AmwalECR-Windows/tools/fake_pos_server.py that speaks the protocol
the C# SDK actually requires. That script answers unsigned, in the legacy flat
shape, without echoing the request's nonce. `EcrSessions.Plan` refuses to run a
LAN terminal without a signing key, and a till that holds a key rejects any answer
that is unsigned or does not echo the nonce -- so an unsigned fake can never be
"approved" from this sample. This one:

  * verifies the request's `secureHash` and refuses a repeated `nonce`, as the
    terminal does;
  * answers in the unified envelope { success, responseCode, message, data,
    errorList } and echoes the request's `nonce`;
  * signs the answer: HMAC-SHA256 over the sorted top-level scalar key=value pairs
    joined by '&' (excluding secureHash), keyed with the HEX-DECODED secret, as
    upper-case hex -- see AmwalPay.Ecr/Internal/SecureHash.cs.

Framing is unchanged: a 2-byte big-endian length, then that many bytes of UTF-8 JSON.

    python tools/fake_pos_server_signed.py --key 0123456789ABCDEF0123456789ABCDEF
    python tools/fake_pos_server_signed.py --key <hex> --decline 51 --delay 3
    python tools/fake_pos_server_signed.py --key <hex> --partial
    python tools/fake_pos_server_signed.py --key <hex> --drop-sale     # lost answer, SDK recovers
    python tools/fake_pos_server_signed.py --key <hex> --drop-sale --drop-first-inquiry
                                                                       # lost answer, operator must inquire
    python tools/fake_pos_server_signed.py --key <hex> --not-found

Register the same key as the "Wi-Fi/LAN secure hash key" in the sample app.
"""

import argparse
import hashlib
import hmac
import json
import random
import socket
import socketserver
import threading
import time
from datetime import datetime

MIN_KEY_HEX_CHARS = 16  # EcrConfig: an even-length hex string of at least 16 characters


# ---- signing (mirror of AmwalPay.Ecr/Internal/SecureHash.cs + EcrJson.Compose) ----

def scalar_text(value):
    """A scalar as the SDK reads it: a string unquoted, a number/boolean as JSON writes it."""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, str):
        return value
    return json.dumps(value)


def compose(obj):
    """Sorted `key=value&key=value` of every top-level scalar except secureHash."""
    pairs = [
        (key, scalar_text(value))
        for key, value in obj.items()
        if key != "secureHash" and value is not None and not isinstance(value, (dict, list))
    ]
    pairs.sort(key=lambda pair: pair[0])  # ordinal, like the SDK
    return "&".join(f"{key}={value}" for key, value in pairs)


def sign(obj, key_hex):
    digest = hmac.new(bytes.fromhex(key_hex), compose(obj).encode("utf-8"), hashlib.sha256)
    return digest.hexdigest().upper()


def valid_key(text):
    if len(text) < MIN_KEY_HEX_CHARS or len(text) % 2:
        return False
    try:
        bytes.fromhex(text)
    except ValueError:
        return False
    return True


# ---- framing ----

def read_exactly(sock, count):
    buffer = b""
    while len(buffer) < count:
        chunk = sock.recv(count - len(buffer))
        if not chunk:
            raise ConnectionError("ECR closed the connection")
        buffer += chunk
    return buffer


def major_units(minor, digits=3):
    """000000001234 -> 1.234"""
    if not minor:
        return "0." + "0" * digits
    text = str(int(minor)).rjust(digits + 1, "0")
    return f"{text[:-digits]}.{text[-digits:]}"


class Ledger:
    """What this fake terminal has 'taken', so an inquiry can find it afterwards."""

    def __init__(self):
        self.lock = threading.Lock()
        self.by_reference = {}
        self.seen_nonces = set()
        self.next_stan = 24

    def record(self, reference, transaction):
        with self.lock:
            stan = f"{self.next_stan:06d}"
            self.next_stan += 1
            transaction["stan"] = stan
            self.by_reference[reference] = transaction
            return stan

    def find_by_reference(self, reference):
        with self.lock:
            return self.by_reference.get(reference)

    def find_by_stan(self, stan):
        with self.lock:
            for transaction in self.by_reference.values():
                if transaction["stan"] == stan:
                    return transaction
        return None

    def remember_nonce(self, nonce):
        """False when the nonce was seen before -- a replay."""
        with self.lock:
            if nonce in self.seen_nonces:
                return False
            self.seen_nonces.add(nonce)
            return True


class EcrHandler(socketserver.BaseRequestHandler):

    def handle(self):
        sock = self.request
        who = self.client_address[0]
        try:
            length = int.from_bytes(read_exactly(sock, 2), "big")
            request = json.loads(read_exactly(sock, length).decode("utf-8"))
        except (ConnectionError, ValueError) as error:
            # A reachability probe connects and leaves without sending anything.
            if isinstance(error, ValueError):
                print(f"[{who}] bad request: {error}")
            return

        print(f"[{who}] --> {json.dumps(request)}")

        if self.server.delay:
            time.sleep(self.server.delay)

        message_type = request.get("messageType")
        problem = self.check_request(request)

        if problem:
            print(f"[{who}] !! {problem}")
            response = self.envelope(False, "96", problem, None, [problem])
        elif message_type == "INQUIRY":
            if self.server.take_inquiry_drop():
                print(f"[{who}] .. closing without an answer (--drop-first-inquiry)")
                return
            response = self.inquiry_response(request)
        elif message_type == "RECEIPT":
            response = self.receipt_response(request)
        else:
            response = self.transaction_response(request, message_type)
            if self.server.drop_sale and message_type == "SALE":
                # The terminal took the payment and then lost the connection: the
                # till never hears. Only an inquiry by reference can find out.
                print(f"[{who}] .. sale recorded, closing without an answer (--drop-sale)")
                return

        response["nonce"] = request.get("nonce", "")
        response["secureHash"] = sign(response, self.server.key)

        body = json.dumps(response, ensure_ascii=False).encode("utf-8")
        sock.sendall(len(body).to_bytes(2, "big") + body)
        print(f"[{who}] <-- {json.dumps(response)}")

    # -- request checks --

    def check_request(self, request):
        presented = str(request.get("secureHash", ""))
        if not presented:
            return "request is not signed"
        if not hmac.compare_digest(presented.upper(), sign(request, self.server.key)):
            return "request signature does not match this terminal's key"
        if not self.server.ledger.remember_nonce(request.get("nonce", "")):
            return "nonce already used (replay)"
        return None

    # -- answers --

    @staticmethod
    def envelope(success, code, message, data, errors=None):
        return {
            "success": success,
            "responseCode": code,
            "message": message,
            "data": data,
            "errorList": errors or [],
        }

    def transaction_response(self, request, message_type):
        reference = request.get("merchantReference", "")
        code = self.server.decline_code or "00"
        if code != "00":
            return self.envelope(False, code, "Declined", None, [f"Declined by the issuer ({code})"])

        original = self.server.ledger.find_by_stan(request.get("stan", "")) if message_type != "SALE" else None
        requested = major_units(request.get("amount")) if request.get("amount") else (
            original["amount"] if original else "0.258")
        settled = requested
        partial = False
        if self.server.partial and message_type == "SALE":
            settled = major_units(str(int(request["amount"]) // 2))
            partial = True

        data = {
            "merchantReference": reference,
            "amount": requested,
            "authorizeAmount": settled,
            "isPartialApprove": partial,
            "rrn": f"{random.randrange(10 ** 11, 10 ** 12)}",
            "authCode": "000000" if message_type == "VOID" else f"{random.randrange(100000, 999999)}",
            "cardMask": "483312******1234",
            "terminalSerial": request.get("terminalSerial", ""),
            "transactionType": message_type.title(),
        }
        self.server.ledger.record(reference, {
            "type": message_type.title(),
            "amount": requested,
            "authorized": settled,
            "partial": partial,
            "time": datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
        })
        return self.envelope(True, "00", "Approved", data)

    def find(self, request):
        """The recorded transaction an inquiry/receipt names, or the fixed sample."""
        by_reference = request.get("originalMerchantReference")
        if by_reference:
            return self.server.ledger.find_by_reference(by_reference)  # None -> not found
        stan = request.get("stan", "")
        return self.server.ledger.find_by_stan(stan) or {
            "stan": stan,
            "type": "Purchase",
            "amount": "0.258",
            "authorized": "0.258",
            "partial": False,
            "time": "2026-08-09T16:58:16.804271",
        }

    def not_found(self):
        return self.envelope(False, "25", "No transactions found for the provided STAN and Terminal", None)

    def inquiry_response(self, request):
        """Answers a lookup. --not-found makes every lookup unknown."""
        transaction = None if self.server.inquiry_not_found else self.find(request)
        if transaction is None:
            return self.not_found()

        data = {
            "transactionId": "e970c800-93f1-11f1-9485-e7dd858253ff",
            "stan": transaction["stan"],
            "transactionType": transaction["type"],
            "transactionTypeDisplayName": transaction["type"],
            "status": "Approved",
            # The backend records major units, unlike the wire.
            "amount": transaction["amount"],
            "authorizeAmount": transaction["authorized"],
            "isPartialApprove": transaction["partial"],
            "totalAmount": transaction["amount"],
            "currency": "OMR",
            "transactionTime": transaction["time"],
            "cardNumber": "483312******1234",
            "rrn": "7862802964726844904806",
            "batchId": "00000003",
            "terminalId": request.get("originalTerminalId", "31629") or "31629",
            "isRefunded": False,
            "canVoid": True,
            "canRefund": True,
        }
        return self.envelope(True, "00", "Transaction found", data)

    def receipt_response(self, request):
        """Publishes an e-receipt. --not-found refuses to produce one."""
        if self.server.inquiry_not_found:
            return self.envelope(False, "91", "The receipt could not be generated", None)
        data = {
            "receiptUrl": "https://example.invalid/receipt?stan=" + request.get("stan", ""),
            "transactionId": "e970c800-93f1-11f1-9485-e7dd858253ff",
            "stan": request.get("stan", ""),
        }
        return self.envelope(True, "00", "Receipt ready", data)


class EcrServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, address, handler, key, decline_code, delay, inquiry_not_found, partial, drop_sale,
                 drop_first_inquiry):
        super().__init__(address, handler)
        self.key = key
        self.decline_code = decline_code
        self.delay = delay
        self.inquiry_not_found = inquiry_not_found
        self.partial = partial
        self.drop_sale = drop_sale
        self.ledger = Ledger()
        self._inquiry_drops_left = 1 if drop_first_inquiry else 0
        self._drop_lock = threading.Lock()

    def take_inquiry_drop(self):
        with self._drop_lock:
            if self._inquiry_drops_left:
                self._inquiry_drops_left -= 1
                return True
        return False


def find_lan_ip():
    """This PC's LAN address, without a DNS lookup of the host name (which can stall for seconds)."""
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("10.255.255.255", 1))  # a UDP "connect" sends nothing; it only picks a route
        return probe.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        probe.close()


def main():
    parser = argparse.ArgumentParser(description="Fake POS ECR listener (signed)")
    parser.add_argument("--key", required=True,
                        help="the LAN signing key, hex -- the same one entered in the app "
                             "(even length, at least 16 characters)")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=9100)
    parser.add_argument("--decline", dest="decline_code", default=None,
                        help="respond with this code instead of 00, e.g. 51")
    parser.add_argument("--delay", type=float, default=0,
                        help="seconds to wait before replying, to mimic card entry")
    parser.add_argument("--not-found", dest="inquiry_not_found", action="store_true",
                        help="answer every inquiry with 25, transaction not found; receipts refuse")
    parser.add_argument("--partial", action="store_true",
                        help="approve only half of every sale (a partial approval)")
    parser.add_argument("--drop-sale", action="store_true",
                        help="record each sale, then close the connection without answering: an "
                             "unknown outcome that only an inquiry by reference can resolve")
    parser.add_argument("--drop-first-inquiry", action="store_true",
                        help="also close the first inquiry without answering -- the SDK's automatic "
                             "check after a lost sale -- so the operator has to look it up by reference")
    args = parser.parse_args()

    key = args.key.strip()
    if not valid_key(key):
        parser.error("--key must be an even-length hex string of at least 16 characters")

    lan_ip = find_lan_ip()
    print(f"Listening on {args.host}:{args.port} (register {lan_ip}:{args.port}, or 127.0.0.1:{args.port} on this PC)")
    print("Signing with the key you passed; enter the same key as the Wi-Fi/LAN secure hash key in the app.")

    with EcrServer((args.host, args.port), EcrHandler, key, args.decline_code, args.delay,
                   args.inquiry_not_found, args.partial, args.drop_sale, args.drop_first_inquiry) as server:
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            print("\nStopped")


if __name__ == "__main__":
    main()
