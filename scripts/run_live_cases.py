"""Sends docs/live-test-cases.csv to a running bot and records what it replied.

The bot must be running (dotnet run in src/OrderTrackerBot.Api). Replies are read from the
dev database's MessageLogs table (outbound rows), so no WhatsApp token is needed.

Usage:
  python3 scripts/run_live_cases.py --url http://localhost:5237 \
      --db src/OrderTrackerBot.Api/ordertrackerbot.dev.db --out results.csv
"""
import argparse
import csv
import json
import sqlite3
import time
import urllib.request
import uuid


def max_outbound_id(db, phone):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        row = con.execute("SELECT COALESCE(MAX(Id), 0) FROM MessageLogs WHERE Phone = ? AND Direction = 'outbound'", (phone,)).fetchone()
        return row[0]
    finally:
        con.close()


def replies_after(db, phone, last_id):
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        rows = con.execute("SELECT Id, RawText FROM MessageLogs WHERE Phone = ? AND Direction = 'outbound' AND Id > ? ORDER BY Id",
                           (phone, last_id)).fetchall()
        return rows
    finally:
        con.close()


def post(url, phone, body):
    payload = {"entry": [{"changes": [{"value": {"messages": [
        {"id": f"live-{uuid.uuid4().hex}", "from": phone, "type": "text", "text": {"body": body}}]}}]}]}
    req = urllib.request.Request(url.rstrip("/") + "/webhook/whatsapp", data=json.dumps(payload).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status
    except urllib.error.HTTPError as e:
        return e.code


def wait_for_reply(db, phone, last_id, quiet_seconds, max_seconds):
    """Waits until no new outbound row has appeared for quiet_seconds (or max_seconds in total)."""
    start = time.time()
    seen = last_id
    last_change = time.time()
    while time.time() - start < max_seconds:
        time.sleep(0.4)
        now_max = max_outbound_id(db, phone)
        if now_max != seen:
            seen = now_max
            last_change = time.time()
        elif seen != last_id and time.time() - last_change >= quiet_seconds:
            break
    return replies_after(db, phone, last_id)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--csv", default="docs/live-test-cases.csv")
    ap.add_argument("--url", default="http://localhost:5237")
    ap.add_argument("--db", default="src/OrderTrackerBot.Api/ordertrackerbot.dev.db")
    ap.add_argument("--phone", default="923001234567")
    ap.add_argument("--out", default="live-results.csv")
    ap.add_argument("--quiet", type=float, default=1.2, help="seconds without a new reply before moving on")
    ap.add_argument("--max-wait", type=float, default=12.0)
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--start", type=int, default=0, help="skip this many cases first (used after setup is finished by hand)")
    args = ap.parse_args()

    with open(args.csv, encoding="utf-8") as f:
        cases = list(csv.DictReader(f))
    cases = cases[args.start:]
    if args.limit:
        cases = cases[: args.limit]

    fields = ["id", "section", "noun", "verb", "message", "expected", "http", "bot_reply"]
    with open(args.out, "w", newline="", encoding="utf-8") as out:
        writer = csv.DictWriter(out, fieldnames=fields)
        writer.writeheader()
        for i, case in enumerate(cases, 1):
            last = max_outbound_id(args.db, args.phone)
            status = post(args.url, args.phone, case["message"])
            rows = wait_for_reply(args.db, args.phone, last, args.quiet, args.max_wait)
            reply = " || ".join(text for _, text in rows).replace("\n", " / ")
            writer.writerow({**{k: case.get(k, "") for k in fields if k not in ("http", "bot_reply")},
                             "http": status, "bot_reply": reply})
            out.flush()
            print(f"[{i + args.start}/{len(cases) + args.start}] {case['id']} http={status} replies={len(rows)}", flush=True)


if __name__ == "__main__":
    main()
