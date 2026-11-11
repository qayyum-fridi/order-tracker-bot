# Voice test cases

Manual WhatsApp voice tests for the bot, plus the developer test commands. Send each line as a voice note and record what the bot did.

## A. Manual voice tests (real WhatsApp)

| # | Say (Roman Urdu / Urdu) | Expected reply | Notes |
|---|---|---|---|
| 1 | "naya product kaise add karun" | How-to reply: type `add product` plus example lines. Must NOT run `contact support`. | Regression for the fix on PR #192 |
| 2 | "میں نئی پراڈکٹ ایڈ کرنی ہے وہ کیسے کر سکتا ہوں" | Same as 1 | Regression |
| 3 | "میں پراڈکٹ میں کیسے ایڈ کر سکتا؟" | Same as 1 | Seen live: the how-to reply was given, but a second note with the same words got "Koi baat nahi, main sahi se samajh nahi pa raha" |
| 4 | "mujhe support se baat karni hai" | `contact support` runs, support confirmation sent | |
| 5 | "Kurti ka rate 2100 kar do" | Price of Kurti becomes 2100 | |
| 6 | "Kurti mein 5 aur aaye" | Kurti stock +5 | |
| 7 | "teen Kurti do Lawn Suit ek, Sara ke liye" | Draft order for Sara, asks YES | |
| 8 | "pehla wala" (after a numbered list) | First option chosen | |
| 9 | "haan kar do" (after a YES question) | Treated as yes | |
| 10 | "aaj ka hisaab" | Today's account summary | |
| 11 | "410 nahi, 420" (inside an order) | Final amount 420 | |
| 12 | "order 2 cancel kar do" | Asks for confirmation, does not cancel directly | |
| 13 | "payout 11700 TCS orders 1 2 3" | Asks YES first; nothing saved before YES | Risky command |
| 14 | "kya karun" | Tip for the current step, state unchanged | |
| 15 | "Sara ko message bhejo" | Clear notice: bot does not message buyers | |
| 16 | Silence or noisy recording | "samajh nahi aaya" or ask to resend; no crash | |
| 17 | Very long voice (30 s+) | Sensible reply or limit message; no crash | |
| 18 | 31 voice notes in one hour | Voice limit message | |

For each result, write: right / wrong, and what the bot replied.

## B. Developer test commands

| Test | Command | Needs |
|---|---|---|
| Unit tests (includes voice guards) | `dotnet test` | none |
| Real transcript report | `VOICE_TRANSCRIPTS=<folder, txt file or copy of app .db> dotnet test --filter VoiceTranscriptReport --logger "console;verbosity=detailed"` | read-only .db copy |
| Yes/No AI eval | `OTB_VOICE_EVAL=1 OPENAI_API_KEY=... dotnet test --filter VoiceYesNoEval` | OpenAI key |
| Rewrite AI eval | `OTB_VOICE_EVAL=1 OPENAI_API_KEY=... dotnet test --filter VoiceRewriteEval --logger "console;verbosity=detailed"` | OpenAI key |
| Agent plan eval | `OTB_AGENT_EVAL=1 OPENAI_API_KEY=... dotnet test --filter AgentPlanEval` | Invented ids and unsaid numbers must be 0 |
| Live agent engine | `OTB_AGENT_LIVE=1 OPENAI_API_KEY=... dotnet test --filter AgentLiveEngineTests` | Local only |

## C. Questions to answer after testing

1. Tests 1 and 2 pass on the live bot?
2. `dotnet test`: how many failures?
3. `VoiceRewriteEval`: percent correct?
4. `AgentPlanEval`: invented ids and unsaid numbers (both must be 0)?
5. Is the eval data enough to turn on `Features:VoiceIntentFirst`?
6. Voice review CSV (`/api/admin/reports/voice-review?days=7`): right/wrong ratio?
