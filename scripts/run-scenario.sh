#!/usr/bin/env bash
# Sends a scenario file to the local bot, one WhatsApp text per line, through the dev webhook.
#
#   scripts/run-scenario.sh docs/scenarios/clothing.txt 923001110101 [base-url] [delay-seconds]
#
# - Use a NEW phone number per scenario (or run `reset account` first): each number is its own seller.
# - Blank lines and lines starting with # are skipped.
# - Replies are not printed here: read them in the API console log or in MessageLogs (dev Sqlite).
# - Needs curl and python3. The API must be running (cd src/OrderTrackerBot.Api && dotnet run).
set -euo pipefail

file="${1:?scenario file}"
phone="${2:?seller phone, digits only, e.g. 923001110101}"
base="${3:-http://localhost:5299}"
delay="${4:-2}"

while IFS= read -r line || [[ -n "$line" ]]; do
  [[ -z "$line" || "$line" == \#* ]] && continue

  id="sim-$(date +%s%N)-$RANDOM"  # unique id: the bot drops a repeated message id
  body=$(python3 -c '
import json, sys
print(json.dumps({"entry": [{"changes": [{"value": {"messages": [
    {"id": sys.argv[1], "from": sys.argv[2], "type": "text", "text": {"body": sys.argv[3]}}]}}]}]}))
' "$id" "$phone" "$line")

  echo "> $line"
  curl -s -o /dev/null -w "  http %{http_code}\n" -X POST "$base/webhook/whatsapp" \
    -H "Content-Type: application/json" -d "$body"
  sleep "$delay"
done < "$file"
