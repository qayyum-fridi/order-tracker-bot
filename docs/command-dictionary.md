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
| New order | `new order` · `naya order banao` · forward the customer's message, send a 🎤 voice note or a 📷 screenshot | `Ayesha, 2 suit, 03001234567` |
| Orders of a day / period | `orders today` | `show orders` · `orders yesterday` · `orders 30 days` · `aaj ke orders` |
| Pending / unpaid / COD owed | `pending orders` · `unpaid orders` · `cod pending` | `pending` · `active orders` · `pending payments` · `payment due list` (unpaid) |
| One order in full | `order 12` | `#12` · `order 12 details` · `find order 12` |
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
| Receipt PDF | `receipt` · `receipt 12` · `Ayesha ki receipt` · `print receipt` · `print bill` · `duplicate receipt` |
| Design the receipt live | `receipt design` (then `color blue` · `title Invoice` · `layout center` · `logo off` · `text large` · `preview` · `reset` · `done`) |
| Paper size | `receipt size 80mm` (`58mm` · `A5` · `A4`) |
| Header / contact / footer | `receipt header [line]` · `receipt contact [line]` · `receipt footer [up to 4 lines]` — `off` clears |
| All receipt settings | `receipt settings` |
| Email | `email ali@shop.com` · `email receipt 12` |
| Parcel / shop label | `label 12` · `print label` · `order label` (asks which label) · `business label` · `label size a6` |
| Logo / banner | `logo` · `banner` (then send the picture) |
| Printer help | `printer` |

## Catalog and stock

| Task | Write |
|---|---|
| See / share | `catalog` (or `products` · `maal` · `rate list` · `price list` · `catalogue` · `product list` · `show products` · `all products` · `products dikhao`) · `share catalog` |
| Add a product | `Kurti - 1800` · `naya product: Kurti - 1800` · `Polo Shirt - 500, cost 300, stock 10, category: Shirts` |
| Change / remove | `edit product: Kurti - 1900` · `delete product: Kurti` |
| Stock | `stock` (or `low stock` · `short stock` · `kam stock` · `out of stock` · `khatam maal` · `stock check` · `show stock` · `current stock` · `stock report` · `restock list` · `maal kitna hai`) · `stock Kurti 20` (`+10` · `off`) |
| Filter | `products category Shirts` · `products vendor Ali` |
| Barcode | `barcode Kurti 8961234567890` · `scan cart` · `scan done` · `scan clear` |

## Money

| Task | Write |
|---|---|
| Today's account | `aaj ka hisaab` · `today's summary` · `hisaab` · `hisaab kitab` · `daily report` · `daily summary` · `today report` · `aaj ka account` · `آج کا حساب` (sales, top item, expenses, drawer cash) |
| Other days | `kal ka hisaab` · `weekly summary` |
| Profit | `profit` · `profit today` · `munafa` |
| Sales split by category | `sales by category` · `category wise sales` · `sales by category this week` (default: last 30 days) |
| One product's sales, this month against last | `Polo Shirt ka report` |
| Margin / how much discount a product can take | `margin` · `margin list` · `profit margin` · `discount room` |
| Expenses | `expense 500 packaging` · `kharcha 800 petrol` · `expenses` · `monthly net` |
| Payment link / methods | `payment link` · `add payment: jazzcash, 03001234567` |
| Cash drawer | (`gala` · `galla` = `drawer`) `drawer open 5000` · `cash in 500 float` · `cash out 300 chai` · `drawer` · `drawer close 12000` |
| Courier payouts | `payout pending` · `courier payout` · `payout 11700 TCS orders 1 2 3` · `payout list` |
| Counter sale | `walkin Kurti 2` · `counter sale 1500` |

## Ask a business question in your own words

These are recognised from a few key words, in any word order, in Roman Urdu, English or Urdu script (typed or spoken). The bot answers from your saved data; it never guesses a number.
Add a period if you want one (`pichle hafte`, `last month`, `is mahine`, `30 din`, `1 May se 15 May`).

| Ask | Example |
|---|---|
| Dearest / cheapest product and how often it sold | `sab se mehnga product kaun sa hai` · `sab se sasta product` |
| Average daily sale (and best day) | `pichle 30 din ki average sale` · `roz ki average bikri` |
| Should the price go up? | `dupatte ki qeemat barhani chahiye` · `price review` |
| What a discount does to the margin | `lawn suit par 10% discount do to margin kitna bachega` |
| Discount given | `pichle mahine kitna discount diya` |
| Lowest margin / best margin / room for a discount | `sab se kam margin kis item par hai` · `kis product par sab se zyada munafa` · `kitna discount de sakta hoon` |
| Customers who stopped coming | `kaun se customers pichle ek mahine se wapas nahi aaye` · `... 14 din se nahi aaye` |
| Biggest / repeat / new customers | `mera sab se bada customer kaun hai` · `bar bar aane wale customers` · `naye customers kitne aaye` |
| Who owes how much | `Bilal ka kitna udhaar hai` · `kis kis ka udhaar baqi hai` (unpaid and part-paid orders) |
| Last purchase | `Sara ne pichli baar kya kharida` |
| Cash against Easypaisa / bank | `log zyada tar COD lete hain ya easypaisa` · `payment method split` |
| Orders from a place | `Model Town se kitne orders aaye` · `city wise orders` |
| Cancelled and returned | `kitne orders cancel ya return hue` · `return rate` |
| Delivery time per courier | `TCS ka average delivery time` · `kaun sa courier sab se tez hai` (needs `mark N shipped` and `mark N delivered`) |
| Stock running out / how many days it lasts | `kaun sa stock agle 3 din mein khatam hoga` · `polo shirt kitne din chalega` · `polo shirt ka stock kitna bacha hai` |
| Re-order? | `lawn suit ka naya batch mangwana chahiye` (first tell the supplier's time once: `maal aane mein 5 din lagte hain`) |
| Products whose stock is not tracked | `kitne products ka stock track nahi kiya` |
| Spending on one thing / biggest expense | `petrol par ab tak kitna kharcha hua` · `sab se zyada kharcha kis cheez par` · `total kharche kitne hain` |
| Today against yesterday / this week or month against the last | `kal ke muqable aaj sale kaisi rahi` · `is mahine ki sale pichle mahine se kaisi hai` |
| Profit or loss (product profit minus expenses) | `kya main faide mein hoon ya nuksan mein` |
| Sales of one product, this month against last | `polo shirt ki sale pichle mahine se behtar hai ya kam` |
| Sales per category | `kapron se kitni sale hui aur grocery se` |
| Top / slow sellers (also by season) | `sab se zyada kya bika` · `kaun sa maal nahi bik raha` · `pichle saal sardiyon mein kya bika` |
| Net sale / revenue / net profit for a period | `pichle hafte ki net sale kitni thi` · `is mahine ka total revenue` · `aaj ka khalis munafa` |
| Drawer short or over | `aaj galle mein kitna cash short ya zyada hai` |
| Which products sell together / never alone / a bundle idea | `kurti ke saath kya bikta hai` · `kaun se products akele nahi bikte` · `bundle offer suggest karo` |
| Regular customers who went quiet / whose order size halved | `kaun se customers ne 60 din se order nahi diya lekin pehle har hafte kharidte the` · `kin customers ka order size aadha reh gaya` |
| What a win-back discount costs | `agar 50 purane customers ko 200 rupay ka discount doon to` (arithmetic and past win-back rate, not a forecast) |
| One customer's buying pattern | `Sara Khan ki kharidari ka pattern` |
| Cost went up: new price? / price change effect | `lawn suit ki cost 300 barh gayi nayi price kya ho` · `polo shirt 500 se 550 kar dun to kya asar` |
| Dear product with a thin margin | `kaun sa product mehnga hai magar margin sab se kam` |
| Sales and profit per supplier | `kis supplier ka maal zyada bika` (save `supplier: Shah Alam` on the product line) |
| Courier returns / loss on returns / outside the city / cancel by payment | `kis courier ke orders zyada return hote hain` · `return ka dead loss` · `Lahore ke bahar delivery time` · `COD zyada cancel hote hain ya advance` |
| Cash: drawer shortage, delivered but unpaid, money stuck, expense share | `pichle 7 din drawer mein kitna cash short hua` · `delivered orders ka paisa jama nahi` · `mera kitna paisa phansa hua hai` · `kharche meri sale ka kitne fisad hain` |
| Stock capital: locked, overstocked, until Eid | `kitna sarmaya 90 din se na bikne wale stock mein phansa hai` · `overstocked products` · `agli eid tak kaun sa stock khatam ho jayega` |
| Ramadan / Eid sales; category mix between months | `pichle saal ramazan mein roz ki sale` · `september se october mein grocery aur kapron ka ratio` |

## Customers and discounts

| Task | Write |
|---|---|
| List / detail / search | `customer list` (or `customers` · `show customers` · `all customers` · `saare customers`) · `customer 1` · `search customer: Sara` |
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
| Menu / guide / language | `help` (or `commands` · `how to use` · `madad chahiye`) · `menu` · `guide` · `change language` |
| Business info / plan | `update business info` · `subscribe` |
| Bottom buttons | `shortcut off` (`shortcut on`) |
| Data | `export` · `export orders 30 days` · `import` |
| Which build is running | `version` |
| Tell us something | `feedback: [message]` |
| Wipe everything | `reset account` (asks to confirm) |

## Not commands (handled inside a question)

`cancel` and `kya karun` are understood in any step but are not in the command table: `cancel` only means something while a question is open, and `kya karun` answers with a tip for the current step.

## Left out on purpose (more than one meaning)

`udhaar` on its own (credit given — there is no credit ledger; "Bilal ka kitna udhaar hai" is read as the unpaid balance of his orders), `ledger`, `items`, `slip` · `courier slip`, `all orders` · `orders` · `recent orders` (all of today? all ever?), `aaj ki kamai` (sales or profit?), `minus 1000` (expense or cash out?), `mark delivered` / `mark paid` without an order number, `Sara ka balance`, `discount 100`, `loyalty`, `kurti ka rate` (a price lookup does not exist yet). These go to the assistant, which offers the possible actions as buttons.
