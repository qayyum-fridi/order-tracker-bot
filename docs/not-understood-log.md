# Messages the bot did not understand

`Features:TurnLog` (on in `appsettings.json`) writes one line per message the bot could not read:

```
NotUnderstood path=<where> state=<conversation state> text=<what the seller wrote or said>
```

| path | meaning |
|---|---|
| `ai-clarify` | typed/voice text reached the AI order reader and it asked "kya kehna chahte hain" |
| `router-new_order` / `router-cancel` / `router-other` | the bot was waiting for an answer and the AI routed the reply away from it |
| `voice-clarify` / `voice-reject` | the voice rewrite was not trusted (a number was not heard) |

Loose-rule matches (`Insight`, `FuzzyStatusUpdate`, `CustomerOrderLookup`, `ProductReport`, `CustomerSearch`, `HowTo`) are also logged WITH the text, to review how often a loose rule runs on a sentence that was really something else:

| line | meaning |
|---|---|
| `RuleCheck kind=… fits=…` | the AI was asked (5+ words); no text |
| `NotUnderstood path=rule-overruled-<Kind>` | the AI said "no": the rule was NOT run, text included |
| `RuleConfirmed kind=… fits=True/unknown text=…` | the rule WAS run (AI said yes, or no AI) |
| `RuleShort kind=… text=…` | fewer than 5 words: the rule was run without an AI check |

Collect them on the server:

```
docker logs order-tracker-api 2>&1 | grep -E "NotUnderstood|RuleCheck|RuleConfirmed|RuleShort" > not-understood.txt
```

Send that file (or paste the lines) to whoever maintains the bot: every line is a real way a person said something the bot missed, and goes into the AI prompt and the tests.
The text is the seller's own message, so set `Features:TurnLog` to `false` if logging it is not acceptable for your sellers.
