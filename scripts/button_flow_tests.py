"""Taps every button flow the bot offers, with a correct and a wrong choice, and checks the bot is not stuck.

A tap reaches the bot as its label text (CLAUDE.md: "A tap arrives as its label"), so the same webhook path as typed text is used.
After every flow the script sends "menu": a reply with "Main Menu" means the bot is out of the flow.
Run it against the same bot and dev database as run_live_cases.py, after that run has finished.

  python3 scripts/button_flow_tests.py --url http://localhost:5237 --db src/OrderTrackerBot.Api/ordertrackerbot.dev.db
"""
import argparse
import sys
import time

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from run_live_cases import max_outbound_id, post, wait_for_reply  # noqa: E402

PHONE = "923001234567"

# Each flow: name, list of (sent text, substring the reply must contain or None), and a label to tap in the middle.
# The steps are written so the same flow can be run with the correct tap and then with a wrong tap.
FLOWS = [
    ("draft: Save (correct)", [
        ("Sara, Kurti 2", "Save"),
        ("✅ Save", "saved"),
    ]),
    ("draft: Cancel (correct exit)", [
        ("Sara, Kurti 2", "Save"),
        ("❌ Cancel", "cancel"),
    ]),
    ("draft: Edit (discard, resend)", [
        ("Sara, Kurti 2", "Save"),
        ("✏️ Edit", "resend"),
    ]),
    ("draft: Save with no draft (wrong tap)", [
        ("✅ Save", "purane"),
    ]),
    ("order for customer: usual item tap", [
        ("order for Sara", "items"),
        ("Kurti Rs.2000", None),
        ("❌ Cancel", None),
    ]),
    ("cod pending: 7+ days", [
        ("cod pending", "7+ days"),
        ("7+ days", None),
    ]),
    ("cod pending: All", [
        ("cod pending", "All"),
        ("All", None),
    ]),
    ("cod pending: wrong then right", [
        ("cod pending", "3+ days"),
        ("3+ days", None),
        ("All", None),
    ]),
    ("slow movers: 14 days", [
        ("slow movers", "14 days"),
        ("14 days", None),
    ]),
    ("slow movers: 30 days then 7 days (change choice)", [
        ("slow movers", "30 days"),
        ("30 days", None),
        ("7 days", None),
    ]),
    ("broadcast: SMS (not configured)", [
        ("sab customers ko batao: naya stock aaya", "SMS"),
        ("SMS", None),
    ]),
    ("broadcast: Dono then wrong label", [
        ("sab customers ko batao: naya stock aaya", "Dono"),
        ("Dono", None),
        ("WhatsApp", None),
    ]),
    ("guide: Sirf orders", [
        ("guide", "Sirf orders"),
        ("Sirf orders", None),
    ]),
    ("guide: wrong label then right", [
        ("guide", "Poora guide"),
        ("Guide dekhein", None),
        ("Poora guide", None),
    ]),
    ("quick bar: Menu", [
        ("📋 Menu", "Main Menu"),
    ]),
    ("quick bar: Naya order", [
        ("➕ Naya order", None),
    ]),
    ("quick bar: Guide", [
        ("📖 Guide", None),
    ]),
    ("quick bar: aaj ka hisaab", [
        ("aaj ka hisaab", None),
    ]),
    ("did-you-mean: Naya product", [
        ("Kurti 1, Dupatta 2", "Naya product"),
        ("➕ Naya product", None),
    ]),
    ("did-you-mean: catalog suggestion (wrong item)", [
        ("Kurti 1, Dupatta 2", "Kurti"),
        ("Kurti Rs.2000", None),
    ]),
    ("language: English then Roman Urdu", [
        ("English", None),
        ("Roman Urdu", None),
    ]),
]

RESET = ["cancel", "menu"]


def send(url, db, text, quiet, max_wait):
    last = max_outbound_id(db, PHONE)
    status = post(url, PHONE, text)
    rows = wait_for_reply(db, PHONE, last, quiet, max_wait)
    reply = " || ".join(t for _, t in rows).replace("\n", " / ")
    return status, reply


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://localhost:5237")
    ap.add_argument("--db", default="src/OrderTrackerBot.Api/ordertrackerbot.dev.db")
    ap.add_argument("--quiet", type=float, default=1.2)
    ap.add_argument("--max-wait", type=float, default=12.0)
    args = ap.parse_args()

    failures = 0
    for name, steps in FLOWS:
        print(f"\n=== {name}")
        for text in RESET:
            send(args.url, args.db, text, args.quiet, args.max_wait)
        for text, expect in steps:
            status, reply = send(args.url, args.db, text, args.quiet, args.max_wait)
            ok = expect is None or expect.lower() in reply.lower()
            if not ok:
                failures += 1
            print(f"  {'PASS' if ok else 'FAIL'} tap={text!r} http={status} -> {reply[:150]}")
        # exit check: from wherever the flow ended, "menu" must bring the main menu back
        _, reply = send(args.url, args.db, "menu", args.quiet, args.max_wait)
        exited = "Main Menu" in reply
        if not exited:
            failures += 1
        print(f"  {'EXIT OK' if exited else 'STUCK'} menu -> {reply[:120]}")
    print(f"\nflows: {len(FLOWS)}  failed checks: {failures}")


if __name__ == "__main__":
    main()
