# Command dictionary

Every phrase below was checked against the real parser (`CommandParser.TryParse`) and is pinned by `CommandDictionaryTests`.
Capital words in `[ ]` are values you fill in. Case does not matter. Roman Urdu, English and Urdu script all work where shown.

## Words used everywhere

| Meaning | Words |
|---|---|
| Yes | `yes` `y` `ha` `haan` `han` `hanji` `ji` `jee` `ہاں` `جی` (a tap on ✅ Save also counts) |
| OK (only for "save this order?") | `ok` `okay` `theek hai` `thik hai` `👍` `✅` |
| No | `no` `n` `nahi` `nahin` `nhi` `nai` `نہیں` |
| Cancel / leave a question | `cancel` `stop` `back` `chhod do` `chhodo` `rehne do` `رہنے دو` (a tap on ❌ Cancel too) |
| Finished (edit / design / product list) | `done` |
| Help | `help` · `kya karun` · `kaise karun` · `?` |
| Today / yesterday | `aaj` `آج` · `kal` |
| Marked done | `ho gaya` `kar diya` `کر دیا` can follow a status word: `shipped kar diya` |

## Orders

| Task | Write | Also works |
|---|---|---|
| New order | forward the customer's message, send a 🎤 voice note or a 📷 screenshot | `Ayesha, 2 suit, 03001234567` |
| Orders of a day / period | `orders today` | `show orders` · `orders yesterday` · `orders 30 days` · `aaj ke orders` |
| Pending / unpaid / COD owed | `pending orders` · `unpaid orders` · `cod pending` | `pending` · `active orders` |
| One order in full | `order 12` | `#12` |
| Change status | `mark 12 shipped` | `delivered` · `returned` · `paid` · `bhej diya` · `mukammal` |
| Cancel an order | `cancel 12` | |
| Edit a saved order | `edit order 12` | `edit order` (the latest) — inside: `1 = 3` · `price 1 = 1500` · `remove 2` · `add Kurti 2` · `phone ...` · `address ...` · `delivery 250` · `done` |
| A customer's orders | `Ayesha ka order` | |
| Order for a saved customer | `order for Sara` | her usual items appear as buttons |
| Advance / part payment | `order 12 advance 500` | |
| Delivery charge | `delivery 200` (default) · `free delivery` | `order 12 delivery 300` |
| Order note | `note 12 gift wrap` | `note 12 off` |
| Undo the last action | `undo` | |

## Receipts, labels, printing

| Task | Write |
|---|---|
| Receipt PDF | `receipt` · `receipt 12` · `Ayesha ki receipt` |
| Design the receipt live | `receipt design` (then `color blue` · `title Invoice` · `layout center` · `logo off` · `text large` · `preview` · `reset` · `done`) |
| Paper size | `receipt size 80mm` (`58mm` · `A5` · `A4`) |
| Header / contact / footer | `receipt header [line]` · `receipt contact [line]` · `receipt footer [up to 4 lines]` — `off` clears |
| All receipt settings | `receipt settings` |
| Email | `email ali@shop.com` · `email receipt 12` |
| Parcel / shop label | `label 12` · `print label` (asks which label) · `business label` · `label size a6` |
| Logo / banner | `logo` · `banner` (then send the picture) |
| Printer help | `printer` |

## Catalog and stock

| Task | Write |
|---|---|
| See / share | `catalog` (or `products` · `maal` · `rate list` · `price list`) · `share catalog` |
| Add a product | `Kurti - 1800` · `naya product: Kurti - 1800` · `Polo Shirt - 500, cost 300, stock 10, category: Shirts` |
| Change / remove | `edit product: Kurti - 1900` · `delete product: Kurti` |
| Stock | `stock` (or `low stock` · `short stock` · `kam stock` · `out of stock` · `khatam maal` · `stock check`) · `stock Kurti 20` (`+10` · `off`) |
| Filter | `products category Shirts` · `products vendor Ali` |
| Barcode | `barcode Kurti 8961234567890` · `scan cart` · `scan done` · `scan clear` |

## Money

| Task | Write |
|---|---|
| Today's account | `aaj ka hisaab` · `today's summary` · `hisaab` · `hisaab kitab` · `daily report` · `آج کا حساب` (sales, top item, expenses, drawer cash) |
| Other days | `kal ka hisaab` · `weekly summary` |
| Profit | `profit` · `profit today` · `munafa` |
| Expenses | `expense 500 packaging` · `kharcha 800 petrol` · `expenses` · `monthly net` |
| Payment link / methods | `payment link` · `add payment: jazzcash, 03001234567` |
| Cash drawer | (`gala` · `galla` = `drawer`) `drawer open 5000` · `cash in 500 float` · `cash out 300 chai` · `drawer` · `drawer close 12000` |
| Courier payouts | `payout pending` · `payout 11700 TCS orders 1 2 3` · `payout list` |
| Counter sale | `walkin Kurti 2` · `counter sale 1500` |

## Customers and discounts

| Task | Write |
|---|---|
| List / detail / search | `customer list` (or `customers`) · `customer 1` · `search customer: Sara` |
| Correct details | `Sara ka phone 03001234567` · `Sara ka address ...` |
| Delete / restore | `delete customer Sara` · `restore customer Sara` |
| Support / Instagram | `support queries` · `comment leads` · `connect instagram` |
| Message all customers | `sab customers ko batao: naya stock aaya` |
| Discount codes | `create discount: EID10, 10 percent` · `discount list` · `discount performance` |
| Loyalty | `create loyalty` · `loyal customers` · `campaign status` |
| Reports | `trending products` · `slow movers` · `customer feedback` |

## Account and settings

| Task | Write |
|---|---|
| Menu / guide / language | `menu` · `guide` · `change language` |
| Business info / plan | `update business info` · `subscribe` |
| Bottom buttons | `shortcut off` (`shortcut on`) |
| Data | `export` · `export orders 30 days` · `import` |
| Which build is running | `version` |
| Tell us something | `feedback: [message]` |
| Wipe everything | `reset account` (asks to confirm) |

## Not commands (handled inside a question)

`cancel` and `kya karun` are understood in any step but are not in the command table: `cancel` only means something while a question is open, and `kya karun` answers with a tip for the current step.

## Left out on purpose (more than one meaning)

`udhaar` (credit given — there is no credit ledger), `ledger`, `items`, `slip`, `all orders` (all of today? all ever?), `aaj ki kamai` (sales or profit?), `minus 1000` (expense or cash out?), `kurti ka rate` (a price lookup does not exist yet). These go to the assistant, which offers the possible actions as buttons.
