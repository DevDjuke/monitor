#!/usr/bin/env python3
import asyncio
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HTTP_LOG = "/tmp/monitor-p9-http.ndjson"
SMTP_LOG = "/tmp/monitor-p9-smtp.eml"

class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length).decode("utf-8")
        with open(HTTP_LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps({"path": self.path, "headers": dict(self.headers), "body": body}) + "\n")
        status = 204 if self.path.startswith("/discord") else 200
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        if status != 204:
            self.wfile.write(b'{"status":"ok"}')

    def log_message(self, *_args):
        pass

async def smtp_client(reader, writer):
    writer.write(b"220 monitor-p9.local ESMTP\r\n")
    await writer.drain()
    collecting = False
    message = []
    try:
        while True:
            line = await reader.readline()
            if not line:
                break
            text = line.decode("utf-8", errors="replace").rstrip("\r\n")
            upper = text.upper()
            if collecting:
                if text == ".":
                    with open(SMTP_LOG, "a", encoding="utf-8") as f:
                        f.write("\n".join(message) + "\n\n---MESSAGE---\n")
                    message = []
                    collecting = False
                    writer.write(b"250 2.0.0 queued\r\n")
                else:
                    message.append(text[1:] if text.startswith("..") else text)
            elif upper.startswith("EHLO") or upper.startswith("HELO"):
                writer.write(b"250-monitor-p9.local\r\n250 SIZE 10485760\r\n")
            elif upper.startswith("MAIL FROM") or upper.startswith("RCPT TO") or upper == "RSET":
                writer.write(b"250 2.1.0 ok\r\n")
            elif upper == "DATA":
                collecting = True
                writer.write(b"354 End data with <CR><LF>.<CR><LF>\r\n")
            elif upper == "QUIT":
                writer.write(b"221 2.0.0 bye\r\n")
                await writer.drain()
                break
            else:
                writer.write(b"250 ok\r\n")
            await writer.drain()
    finally:
        writer.close()
        await writer.wait_closed()

async def main():
    open(HTTP_LOG, "w", encoding="utf-8").close()
    open(SMTP_LOG, "w", encoding="utf-8").close()
    http = ThreadingHTTPServer(("127.0.0.1", 5096), Handler)
    thread = threading.Thread(target=http.serve_forever, daemon=True)
    thread.start()
    smtp = await asyncio.start_server(smtp_client, "127.0.0.1", 2525)
    try:
        async with smtp:
            await smtp.serve_forever()
    finally:
        http.shutdown()

if __name__ == "__main__":
    asyncio.run(main())
