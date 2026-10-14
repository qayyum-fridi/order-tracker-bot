# Industry test cases — har dukaan ki qism ke liye poore (end-to-end) test cases

Yeh file seller ki taraf se **WhatsApp par bhejne wali lines** aur **bot ka mutawaqqa jawab** deti hai, har industry ke liye alag, shuru se akhir tak (setup → catalog → orders → payment → reports → galat data → voice).

## Kaise istemal karein

- Har industry ke liye **naya test number** (ya `reset account`) istemal karein, taake pehle ka data na mile.
- Har row ka matlab: **Bhejein** = WhatsApp par jo likhna hai (hu-ba-hu). **Mutawaqqa jawab** = bot ke jawab mein yeh baatein/lafz honay chahiye (poora jumla nahi, kyunke wording polish hoti rehti hai).
- 🤖 = is row mein **AI (OpenAI key)** chahiye (free-form jumla ya voice). Baqi sab rows bina AI ke chalti hain.
- Numbers haath se hisaab kiye hue hain; agar aapke numbers alag aayen to bug likhein.
- Tests Roman Urdu, English aur Urdu (اردو) teeno zubanon mein hain. Zubaan badalne ke baad bot ke jawab usi zubaan mein aane chahiye (translation AI se hoti hai, wording thori alag ho sakti hai).
- Status: ☐ = baqi, ✅ = pass, ❌ = fail (neeche notes mein likhein).

## Fehrist

- [Kapray / Lawn / Boutique](#clothing)
- [Grocery / Kiryana](#grocery)
- [Cosmetics / Jewellery](#beauty)
- [Electronics / Mobile](#electronics)
- [Food / Bakery](#food)
- [Hardware / Building](#hardware)
- [General dukaan](#general)
- [Saari industries ke liye common cases](#common)
- [Poore din ka scenario (overall business)](#day)


---

<a id="clothing"></a>
## Kapray / Lawn / Boutique

Test seller: **Ayesha Collections**, shehar **Lahore**, business type **Clothing**. Test buyer: **Ayesha**, phone `03001234567`, address Gulberg Lahore.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| CLO-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| CLO-02 | `Ayesha Collections` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| CLO-03 | `Lahore, Clothing, @ayesha.collections` | Business info save; Clothing pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| CLO-04 | `industry` | Dukaan ki qism ke buttons ki list (Kapray / Lawn / Boutique samet 7 options) |  | ☐ |
| CLO-05 | `industry clothing` | ✅ Dukaan ki qism: Kapray / Lawn / Boutique; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| CLO-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| CLO-07 | `business type clothing` | Wohi Kapray / Lawn / Boutique guide (alias se pehchana gaya) |  | ☐ |
| CLO-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| CLO-09 | `Sirf orders` | Order ka example: "Ayesha, ..." is dukaan ke mutabiq (General ke ilawa dusri industry ka example) |  | ☐ |
| | **B. Catalog (products)** | | | |
| CLO-10 | `3 Piece Lawn Suit - 3500, cost 2400, stock 20, category lawn` | ✅ 3 Piece Lawn Suit save: Rs.3,500, cost 2,400, stock 20, category lawn |  | ☐ |
| CLO-11 | `3 Piece Lawn Suit - 3500, cost 2400, stock 20, category lawn` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| CLO-12 | `3 Piece Lawn Suit - 3600` | "price updated: 3,500 → 3,600" |  | ☐ |
| CLO-13 | `3 Piece Lawn Suit - 3500` | Price wapas 3,500 |  | ☐ |
| CLO-14 | `Kurti - 1800, cost 1100, stock 15, category stitched<br>Dupatta - 600` | Dono products ek saath save (do lines wala message): Kurti, Dupatta |  | ☐ |
| CLO-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| CLO-16 | `3 Piece Lawn Suit - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| CLO-17 | `3 Piece Lawn Suit - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| CLO-18 | `Design 1203 - 3500, cost 2400, category lawn` | Product ban jata hai, naam 'Design 1203' |  | ☐ |
| CLO-19 | `note 1 sleeves lambi, chaak 6 inch` | Order #1 par note save, receipt par chhapta hai |  | ☐ |
| CLO-20 | `Kurti - 1800, size M, color blue` | Kurti ki details update (size, colour) |  | ☐ |
| | **C. Order banana** | | | |
| CLO-21 | `Ayesha, 2 kurti, 03001234567, Gulberg Lahore` | Draft: customer Ayesha, 2x Kurti, total Rs.3,600; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| CLO-22 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.3,600 |  | ☐ |
| CLO-23 | `order 1` | Order #1 poora: Kurti x2, Rs.3,600, customer Ayesha |  | ☐ |
| CLO-24 | `Ayesha ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| CLO-25 | `Ayesha, 1 kurta, 03001234567, Gulberg Lahore` | "kurta" catalog mein nahi mila + qareeb ka product button (Kurti) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| CLO-26 | `2 Kurti` | Kurti chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| CLO-27 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| CLO-28 | `Ayesha ne Kurti 1500 ki li, 03001234567, Gulberg Lahore` | ⚠️ Qeemat mein farq: catalog mein Rs.1,800, aapne Rs.1,500; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| CLO-29 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| CLO-30 | `2` | Catalog rate Rs.1,800 par draft; "Confirm order" |  | ☐ |
| CLO-31 | `cancel` | Draft khatam |  | ☐ |
| CLO-32 | `Ayesha, Kurti, 03001234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| CLO-33 | `Zara, Kurti, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| CLO-34 | `cancel` | Draft khatam |  | ☐ |
| CLO-35 | `walkin Kurti 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.1,800 |  | ☐ |
| CLO-36 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| CLO-37 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| CLO-38 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| CLO-39 | `add tracking: Ayesha, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| CLO-40 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| CLO-41 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| CLO-42 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| CLO-43 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| CLO-44 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| CLO-45 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| CLO-46 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| CLO-47 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| CLO-48 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| CLO-49 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| CLO-50 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| CLO-51 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| CLO-52 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| CLO-53 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| CLO-54 | `margin` | 3 Piece Lawn Suit: margin 31% ke qareeb, munasib discount ~16%, break-even ~31% |  | ☐ |
| CLO-55 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| CLO-56 | `kaunsa maal khatam hone wala hai` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| CLO-57 | `کس چیز کا اسٹاک ختم ہونے والا ہے` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| CLO-58 | `which products are running out of stock` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| CLO-59 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| CLO-60 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; Kurti ka cost nahi ('Cost price nahi: ...'); jaise line: "3 Piece Lawn Suit - 3500, cost 2400, stock 20, category lawn" |  | ☐ |
| CLO-61 | `Kurti - 1000, cost 1200` | Kurti ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| CLO-62 | `data check` | 🔎 'Cost price >= sale price (1): Kurti (cost 1200 / price 1000)' |  | ☐ |
| CLO-63 | `Kurti - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| CLO-64 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| CLO-65 | `3 piece lawn suit - 3500` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| CLO-66 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| CLO-67 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| CLO-68 | `🎤 (bol kar) "Ayesha ka 2 kurti ka order, phone 03001234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| CLO-69 | `🎤 "kaunsa maal khatam hone wala hai"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| CLO-70 | `🎤 "کس چیز کا اسٹاک ختم ہونے والا ہے"` | Urdu voice se wohi report | 🤖 | ☐ |
| CLO-71 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="grocery"></a>
## Grocery / Kiryana

Test seller: **Bilal Mart**, shehar **Karachi**, business type **Kiryana**. Test buyer: **Bilal**, phone `03111234567`, address DHA Karachi.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| GRO-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| GRO-02 | `Bilal Mart` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| GRO-03 | `Karachi, Kiryana, @bilal.mart` | Business info save; Kiryana pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| GRO-04 | `industry` | Dukaan ki qism ke buttons ki list (Grocery / Kiryana samet 7 options) |  | ☐ |
| GRO-05 | `industry grocery` | ✅ Dukaan ki qism: Grocery / Kiryana; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| GRO-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| GRO-07 | `business type kiryana` | Wohi Grocery / Kiryana guide (alias se pehchana gaya) |  | ☐ |
| GRO-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| GRO-09 | `Sirf orders` | Order ka example: "Bilal, ..." is dukaan ke mutabiq (General ke ilawa dusri industry ka example) |  | ☐ |
| | **B. Catalog (products)** | | | |
| GRO-10 | `Sugar 5 kg - 500, cost 440, stock 40, category rashan` | ✅ Sugar 5 kg save: Rs.500, cost 440, stock 40, category rashan |  | ☐ |
| GRO-11 | `Sugar 5 kg - 500, cost 440, stock 40, category rashan` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| GRO-12 | `Sugar 5 kg - 600` | "price updated: 500 → 600" |  | ☐ |
| GRO-13 | `Sugar 5 kg - 500` | Price wapas 500 |  | ☐ |
| GRO-14 | `Cooking Oil 1 ltr - 520<br>Rice 10 kg - 2600` | Dono products ek saath save (do lines wala message): Cooking Oil 1 ltr, Rice 10 kg |  | ☐ |
| GRO-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| GRO-16 | `Sugar 5 kg - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| GRO-17 | `Sugar 5 kg - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| GRO-18 | `stock Sugar 5 kg 3` | Stock 3 set; ab 3 ya kam hone par low-stock warning aati hai |  | ☐ |
| GRO-19 | `stock` | Tracked stock ki list |  | ☐ |
| | **C. Order banana** | | | |
| GRO-20 | `Bilal, 2 sugar 5 kg, 03111234567, DHA Karachi` | Draft: customer Bilal, 2x Sugar 5 kg, total Rs.1,000; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| GRO-21 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.1,000 |  | ☐ |
| GRO-22 | `order 1` | Order #1 poora: Sugar 5 kg x2, Rs.1,000, customer Bilal |  | ☐ |
| GRO-23 | `Bilal ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| GRO-24 | `Bilal, 1 sugr 5 kg, 03111234567, DHA Karachi` | "sugr 5 kg" catalog mein nahi mila + qareeb ka product button (Sugar 5 kg) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| GRO-25 | `2 Sugar 5 kg` | Sugar 5 kg chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| GRO-26 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| GRO-27 | `Bilal ne Sugar 5 kg 450 ki li, 03111234567, DHA Karachi` | ⚠️ Qeemat mein farq: catalog mein Rs.500, aapne Rs.450; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| GRO-28 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| GRO-29 | `2` | Catalog rate Rs.500 par draft; "Confirm order" |  | ☐ |
| GRO-30 | `cancel` | Draft khatam |  | ☐ |
| GRO-31 | `Bilal, Sugar 5 kg, 03111234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| GRO-32 | `Zara, Sugar 5 kg, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| GRO-33 | `cancel` | Draft khatam |  | ☐ |
| GRO-34 | `walkin Sugar 5 kg 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.500 |  | ☐ |
| GRO-35 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| GRO-36 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| GRO-37 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| GRO-38 | `add tracking: Bilal, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| GRO-39 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| GRO-40 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| GRO-41 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| GRO-42 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| GRO-43 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| GRO-44 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| GRO-45 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| GRO-46 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| GRO-47 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| GRO-48 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| GRO-49 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| GRO-50 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| GRO-51 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| GRO-52 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| GRO-53 | `margin` | Sugar 5 kg: margin 12% ke qareeb, munasib discount ~6%, break-even ~12% |  | ☐ |
| GRO-54 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| GRO-55 | `kaunsa maal khatam hone wala hai` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| GRO-56 | `کس چیز کا اسٹاک ختم ہونے والا ہے` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| GRO-57 | `which products are running out of stock` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| GRO-58 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| GRO-59 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; Cooking Oil 1 ltr ka cost nahi ('Cost price nahi: ...'); jaise line: "Sugar 5 kg - 500, cost 440, stock 40, category rashan" |  | ☐ |
| GRO-60 | `Cooking Oil 1 ltr - 1000, cost 1200` | Cooking Oil 1 ltr ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| GRO-61 | `data check` | 🔎 'Cost price >= sale price (1): Cooking Oil 1 ltr (cost 1200 / price 1000)' |  | ☐ |
| GRO-62 | `Cooking Oil 1 ltr - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| GRO-63 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| GRO-64 | `sugar 5 kg - 500` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| GRO-65 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| GRO-66 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| GRO-67 | `🎤 (bol kar) "Bilal ka 2 sugar 5 kg ka order, phone 03111234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| GRO-68 | `🎤 "kaunsa maal khatam hone wala hai"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| GRO-69 | `🎤 "کس چیز کا اسٹاک ختم ہونے والا ہے"` | Urdu voice se wohi report | 🤖 | ☐ |
| GRO-70 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="beauty"></a>
## Cosmetics / Jewellery

Test seller: **Sana Beauty**, shehar **Lahore**, business type **Cosmetics**. Test buyer: **Sana**, phone `03211234567`, address Johar Town Lahore.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| BEA-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| BEA-02 | `Sana Beauty` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| BEA-03 | `Lahore, Cosmetics, @sana.beauty` | Business info save; Cosmetics pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| BEA-04 | `industry` | Dukaan ki qism ke buttons ki list (Cosmetics / Jewellery samet 7 options) |  | ☐ |
| BEA-05 | `industry beauty` | ✅ Dukaan ki qism: Cosmetics / Jewellery; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| BEA-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| BEA-07 | `business type cosmetics` | Wohi Cosmetics / Jewellery guide (alias se pehchana gaya) |  | ☐ |
| BEA-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| BEA-09 | `Sirf orders` | Order ka example: "Sana, ..." is dukaan ke mutabiq (General ke ilawa dusri industry ka example) |  | ☐ |
| | **B. Catalog (products)** | | | |
| BEA-10 | `Lipstick Matte Red - 950, cost 600, stock 30, category makeup` | ✅ Lipstick Matte Red save: Rs.950, cost 600, stock 30, category makeup |  | ☐ |
| BEA-11 | `Lipstick Matte Red - 950, cost 600, stock 30, category makeup` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| BEA-12 | `Lipstick Matte Red - 1050` | "price updated: 950 → 1,050" |  | ☐ |
| BEA-13 | `Lipstick Matte Red - 950` | Price wapas 950 |  | ☐ |
| BEA-14 | `Bridal Necklace Set - 8500<br>Hand Bag - 3200` | Dono products ek saath save (do lines wala message): Bridal Necklace Set, Hand Bag |  | ☐ |
| BEA-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| BEA-16 | `Lipstick Matte Red - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| BEA-17 | `Lipstick Matte Red - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| BEA-18 | `note 1 gift wrap` | Order #1 par note: gift wrap |  | ☐ |
| BEA-19 | `barcode Lipstick Matte Red 8964000123` | Barcode save (ek product ka ek code) |  | ☐ |
| | **C. Order banana** | | | |
| BEA-20 | `Sana, 2 lipstick matte red, 03211234567, Johar Town Lahore` | Draft: customer Sana, 2x Lipstick Matte Red, total Rs.1,900; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| BEA-21 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.1,900 |  | ☐ |
| BEA-22 | `order 1` | Order #1 poora: Lipstick Matte Red x2, Rs.1,900, customer Sana |  | ☐ |
| BEA-23 | `Sana ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| BEA-24 | `Sana, 1 lipstik matte red, 03211234567, Johar Town Lahore` | "lipstik matte red" catalog mein nahi mila + qareeb ka product button (Lipstick Matte Red) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| BEA-25 | `2 Lipstick Matte Red` | Lipstick Matte Red chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| BEA-26 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| BEA-27 | `Sana ne Lipstick Matte Red 900 ki li, 03211234567, Johar Town Lahore` | ⚠️ Qeemat mein farq: catalog mein Rs.950, aapne Rs.900; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| BEA-28 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| BEA-29 | `2` | Catalog rate Rs.950 par draft; "Confirm order" |  | ☐ |
| BEA-30 | `cancel` | Draft khatam |  | ☐ |
| BEA-31 | `Sana, Lipstick Matte Red, 03211234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| BEA-32 | `Zara, Lipstick Matte Red, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| BEA-33 | `cancel` | Draft khatam |  | ☐ |
| BEA-34 | `walkin Lipstick Matte Red 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.950 |  | ☐ |
| BEA-35 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| BEA-36 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| BEA-37 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| BEA-38 | `add tracking: Sana, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| BEA-39 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| BEA-40 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| BEA-41 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| BEA-42 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| BEA-43 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| BEA-44 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| BEA-45 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| BEA-46 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| BEA-47 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| BEA-48 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| BEA-49 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| BEA-50 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| BEA-51 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| BEA-52 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| BEA-53 | `margin` | Lipstick Matte Red: margin 37% ke qareeb, munasib discount ~18%, break-even ~37% |  | ☐ |
| BEA-54 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| BEA-55 | `sab se zyada profit kis product par hai` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| BEA-56 | `کس پروڈکٹ پر سب سے زیادہ منافع ہے` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| BEA-57 | `which product has the highest margin` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| BEA-58 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| BEA-59 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; Bridal Necklace Set ka cost nahi ('Cost price nahi: ...'); jaise line: "Lipstick Matte Red - 950, cost 600, stock 30, category makeup" |  | ☐ |
| BEA-60 | `Bridal Necklace Set - 1000, cost 1200` | Bridal Necklace Set ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| BEA-61 | `data check` | 🔎 'Cost price >= sale price (1): Bridal Necklace Set (cost 1200 / price 1000)' |  | ☐ |
| BEA-62 | `Bridal Necklace Set - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| BEA-63 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| BEA-64 | `lipstick matte red - 950` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| BEA-65 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| BEA-66 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| BEA-67 | `🎤 (bol kar) "Sana ka 2 lipstick matte red ka order, phone 03211234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| BEA-68 | `🎤 "sab se zyada profit kis product par hai"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| BEA-69 | `🎤 "کس پروڈکٹ پر سب سے زیادہ منافع ہے"` | Urdu voice se wohi report | 🤖 | ☐ |
| BEA-70 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="electronics"></a>
## Electronics / Mobile

Test seller: **Hamza Mobiles**, shehar **Rawalpindi**, business type **Mobile accessories**. Test buyer: **Hamza**, phone `03331234567`, address Saddar Rawalpindi.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| ELE-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| ELE-02 | `Hamza Mobiles` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| ELE-03 | `Rawalpindi, Mobile accessories, @hamza.mobiles` | Business info save; Mobile accessories pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| ELE-04 | `industry` | Dukaan ki qism ke buttons ki list (Electronics / Mobile samet 7 options) |  | ☐ |
| ELE-05 | `industry electronics` | ✅ Dukaan ki qism: Electronics / Mobile; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| ELE-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| ELE-07 | `business type mobile accessories` | Wohi Electronics / Mobile guide (alias se pehchana gaya) |  | ☐ |
| ELE-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| ELE-09 | `Sirf orders` | Order ka example: "Hamza, ..." is dukaan ke mutabiq (General ke ilawa dusri industry ka example) |  | ☐ |
| | **B. Catalog (products)** | | | |
| ELE-10 | `Fast Charger 20W - 1800, cost 1100, stock 25, category charger` | ✅ Fast Charger 20W save: Rs.1,800, cost 1,100, stock 25, category charger |  | ☐ |
| ELE-11 | `Fast Charger 20W - 1800, cost 1100, stock 25, category charger` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| ELE-12 | `Fast Charger 20W - 1900` | "price updated: 1,800 → 1,900" |  | ☐ |
| ELE-13 | `Fast Charger 20W - 1800` | Price wapas 1,800 |  | ☐ |
| ELE-14 | `Earbuds Pro - 3500<br>Phone Cover - 600` | Dono products ek saath save (do lines wala message): Earbuds Pro, Phone Cover |  | ☐ |
| ELE-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| ELE-16 | `Fast Charger 20W - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| ELE-17 | `Fast Charger 20W - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| ELE-18 | `barcode Earbuds Pro 8964000999` | Barcode save |  | ☐ |
| ELE-19 | `scan 8964000999` | Scan cart mein 1 piece Earbuds Pro |  | ☐ |
| ELE-20 | `scan clear` | Cart khali |  | ☐ |
| | **C. Order banana** | | | |
| ELE-21 | `Hamza, 1 fast charger 20w, 03331234567, Saddar Rawalpindi` | Draft: customer Hamza, 1x Fast Charger 20W, total Rs.1,800; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| ELE-22 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.1,800 |  | ☐ |
| ELE-23 | `order 1` | Order #1 poora: Fast Charger 20W x1, Rs.1,800, customer Hamza |  | ☐ |
| ELE-24 | `Hamza ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| ELE-25 | `Hamza, 1 fast chargar 20w, 03331234567, Saddar Rawalpindi` | "fast chargar 20w" catalog mein nahi mila + qareeb ka product button (Fast Charger 20W) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| ELE-26 | `2 Fast Charger 20W` | Fast Charger 20W chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| ELE-27 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| ELE-28 | `Hamza ne Fast Charger 20W 1700 ki li, 03331234567, Saddar Rawalpindi` | ⚠️ Qeemat mein farq: catalog mein Rs.1,800, aapne Rs.1,700; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| ELE-29 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| ELE-30 | `2` | Catalog rate Rs.1,800 par draft; "Confirm order" |  | ☐ |
| ELE-31 | `cancel` | Draft khatam |  | ☐ |
| ELE-32 | `Hamza, Fast Charger 20W, 03331234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| ELE-33 | `Zara, Fast Charger 20W, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| ELE-34 | `cancel` | Draft khatam |  | ☐ |
| ELE-35 | `walkin Fast Charger 20W 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.1,800 |  | ☐ |
| ELE-36 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| ELE-37 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| ELE-38 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| ELE-39 | `add tracking: Hamza, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| ELE-40 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| ELE-41 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| ELE-42 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| ELE-43 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| ELE-44 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| ELE-45 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| ELE-46 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| ELE-47 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| ELE-48 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| ELE-49 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| ELE-50 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| ELE-51 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| ELE-52 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| ELE-53 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| ELE-54 | `margin` | Fast Charger 20W: margin 39% ke qareeb, munasib discount ~19%, break-even ~39% |  | ☐ |
| ELE-55 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| ELE-56 | `kaunsa product kam bikta hai` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| ELE-57 | `کون سا پروڈکٹ کم بک رہا ہے` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| ELE-58 | `which items sell the least` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| ELE-59 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| ELE-60 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; Earbuds Pro ka cost nahi ('Cost price nahi: ...'); jaise line: "Fast Charger 20W - 1800, cost 1100, stock 25, category charger" |  | ☐ |
| ELE-61 | `Earbuds Pro - 1000, cost 1200` | Earbuds Pro ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| ELE-62 | `data check` | 🔎 'Cost price >= sale price (1): Earbuds Pro (cost 1200 / price 1000)' |  | ☐ |
| ELE-63 | `Earbuds Pro - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| ELE-64 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| ELE-65 | `fast charger 20w - 1800` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| ELE-66 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| ELE-67 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| ELE-68 | `🎤 (bol kar) "Hamza ka 1 fast charger 20w ka order, phone 03331234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| ELE-69 | `🎤 "kaunsa product kam bikta hai"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| ELE-70 | `🎤 "کون سا پروڈکٹ کم بک رہا ہے"` | Urdu voice se wohi report | 🤖 | ☐ |
| ELE-71 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="food"></a>
## Food / Bakery

Test seller: **Maryam Bakes**, shehar **Karachi**, business type **Home bakery**. Test buyer: **Maryam**, phone `03451234567`, address Clifton Karachi.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| FOO-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| FOO-02 | `Maryam Bakes` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| FOO-03 | `Karachi, Home bakery, @maryam.bakes` | Business info save; Home bakery pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| FOO-04 | `industry` | Dukaan ki qism ke buttons ki list (Food / Bakery samet 7 options) |  | ☐ |
| FOO-05 | `industry food` | ✅ Dukaan ki qism: Food / Bakery; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| FOO-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| FOO-07 | `business type home bakery` | Wohi Food / Bakery guide (alias se pehchana gaya) |  | ☐ |
| FOO-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| FOO-09 | `Sirf orders` | Order ka example: "Maryam, ..." is dukaan ke mutabiq (General ke ilawa dusri industry ka example) |  | ☐ |
| | **B. Catalog (products)** | | | |
| FOO-10 | `Chocolate Cake 2 lb - 3200, cost 1500, category cakes` | ✅ Chocolate Cake 2 lb save: Rs.3,200, cost 1,500, category cakes |  | ☐ |
| FOO-11 | `Chocolate Cake 2 lb - 3200, cost 1500, category cakes` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| FOO-12 | `Chocolate Cake 2 lb - 3300` | "price updated: 3,200 → 3,300" |  | ☐ |
| FOO-13 | `Chocolate Cake 2 lb - 3200` | Price wapas 3,200 |  | ☐ |
| FOO-14 | `Chicken Biryani Tray - 4500<br>Brownies Box - 1200` | Dono products ek saath save (do lines wala message): Chicken Biryani Tray, Brownies Box |  | ☐ |
| FOO-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| FOO-16 | `Chocolate Cake 2 lb - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| FOO-17 | `Chocolate Cake 2 lb - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| FOO-18 | `delivery 300` | Default delivery charge Rs.300 set |  | ☐ |
| FOO-19 | `order 1 advance 1000` | Advance 1,000 jama; balance = total - 1000 |  | ☐ |
| | **C. Order banana** | | | |
| FOO-20 | `Maryam, 1 chocolate cake 2 lb, 03451234567, Clifton Karachi` | Draft: customer Maryam, 1x Chocolate Cake 2 lb, total Rs.3,200; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| FOO-21 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.3,200 |  | ☐ |
| FOO-22 | `order 1` | Order #1 poora: Chocolate Cake 2 lb x1, Rs.3,200, customer Maryam |  | ☐ |
| FOO-23 | `Maryam ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| FOO-24 | `Maryam, 1 choclate cake 2 lb, 03451234567, Clifton Karachi` | "choclate cake 2 lb" catalog mein nahi mila + qareeb ka product button (Chocolate Cake 2 lb) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| FOO-25 | `2 Chocolate Cake 2 lb` | Chocolate Cake 2 lb chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| FOO-26 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| FOO-27 | `Maryam ne Chocolate Cake 2 lb 3000 ki li, 03451234567, Clifton Karachi` | ⚠️ Qeemat mein farq: catalog mein Rs.3,200, aapne Rs.3,000; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| FOO-28 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| FOO-29 | `2` | Catalog rate Rs.3,200 par draft; "Confirm order" |  | ☐ |
| FOO-30 | `cancel` | Draft khatam |  | ☐ |
| FOO-31 | `Maryam, Chocolate Cake 2 lb, 03451234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| FOO-32 | `Zara, Chocolate Cake 2 lb, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| FOO-33 | `cancel` | Draft khatam |  | ☐ |
| FOO-34 | `walkin Chocolate Cake 2 lb 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.3,200 |  | ☐ |
| FOO-35 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| FOO-36 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| FOO-37 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| FOO-38 | `add tracking: Maryam, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| FOO-39 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| FOO-40 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| FOO-41 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| FOO-42 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| FOO-43 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| FOO-44 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| FOO-45 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| FOO-46 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| FOO-47 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| FOO-48 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| FOO-49 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| FOO-50 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| FOO-51 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| FOO-52 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| FOO-53 | `margin` | Chocolate Cake 2 lb: margin 53% ke qareeb, munasib discount ~27%, break-even ~53% |  | ☐ |
| FOO-54 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| FOO-55 | `kitna payment baaqi hai` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| FOO-56 | `کتنے پیسے گاہکوں سے لینے باقی ہیں` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| FOO-57 | `payment due list` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| FOO-58 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| FOO-59 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; Chicken Biryani Tray ka cost nahi ('Cost price nahi: ...'); jaise line: "Chocolate Cake 2 lb - 3200, cost 1500, category cakes" |  | ☐ |
| FOO-60 | `Chicken Biryani Tray - 1000, cost 1200` | Chicken Biryani Tray ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| FOO-61 | `data check` | 🔎 'Cost price >= sale price (1): Chicken Biryani Tray (cost 1200 / price 1000)' |  | ☐ |
| FOO-62 | `Chicken Biryani Tray - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| FOO-63 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| FOO-64 | `chocolate cake 2 lb - 3200` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| FOO-65 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| FOO-66 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| FOO-67 | `🎤 (bol kar) "Maryam ka 1 chocolate cake 2 lb ka order, phone 03451234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| FOO-68 | `🎤 "kitna payment baaqi hai"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| FOO-69 | `🎤 "کتنے پیسے گاہکوں سے لینے باقی ہیں"` | Urdu voice se wohi report | 🤖 | ☐ |
| FOO-70 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="hardware"></a>
## Hardware / Building

Test seller: **Usman Hardware**, shehar **Gujranwala**, business type **Hardware**. Test buyer: **Usman**, phone `03051234567`, address Gujranwala.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| HAR-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| HAR-02 | `Usman Hardware` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| HAR-03 | `Gujranwala, Hardware, @usman.hardware` | Business info save; Hardware pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| HAR-04 | `industry` | Dukaan ki qism ke buttons ki list (Hardware / Building samet 7 options) |  | ☐ |
| HAR-05 | `industry hardware` | ✅ Dukaan ki qism: Hardware / Building; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| HAR-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| HAR-07 | `business type hardware` | Wohi Hardware / Building guide (alias se pehchana gaya) |  | ☐ |
| HAR-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| HAR-09 | `Sirf orders` | Order ka example: "Usman, ..." is dukaan ke mutabiq (General ke ilawa dusri industry ka example) |  | ☐ |
| | **B. Catalog (products)** | | | |
| HAR-10 | `Wall Paint 4 ltr - 3800, cost 3000, stock 20, category paint` | ✅ Wall Paint 4 ltr save: Rs.3,800, cost 3,000, stock 20, category paint |  | ☐ |
| HAR-11 | `Wall Paint 4 ltr - 3800, cost 3000, stock 20, category paint` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| HAR-12 | `Wall Paint 4 ltr - 3900` | "price updated: 3,800 → 3,900" |  | ☐ |
| HAR-13 | `Wall Paint 4 ltr - 3800` | Price wapas 3,800 |  | ☐ |
| HAR-14 | `PVC Pipe 1 inch - 450<br>Cement Bag 50 kg - 1350` | Dono products ek saath save (do lines wala message): PVC Pipe 1 inch, Cement Bag 50 kg |  | ☐ |
| HAR-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| HAR-16 | `Wall Paint 4 ltr - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| HAR-17 | `Wall Paint 4 ltr - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| HAR-18 | `supplier Wall Paint Ali Traders` | Wall Paint ka supplier Ali Traders save |  | ☐ |
| HAR-19 | `order 1 advance 1000` | Advance jama; baqi balance 'unpaid orders' mein |  | ☐ |
| | **C. Order banana** | | | |
| HAR-20 | `Usman, 5 pvc pipe 1 inch, 03051234567, Gujranwala` | Draft: customer Usman, 5x PVC Pipe 1 inch, total Rs.2,250; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| HAR-21 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.2,250 |  | ☐ |
| HAR-22 | `order 1` | Order #1 poora: PVC Pipe 1 inch x5, Rs.2,250, customer Usman |  | ☐ |
| HAR-23 | `Usman ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| HAR-24 | `Usman, 1 pvc pipee 1 inch, 03051234567, Gujranwala` | "pvc pipee 1 inch" catalog mein nahi mila + qareeb ka product button (PVC Pipe 1 inch) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| HAR-25 | `2 PVC Pipe 1 inch` | PVC Pipe 1 inch chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| HAR-26 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| HAR-27 | `Usman ne PVC Pipe 1 inch 400 ki li, 03051234567, Gujranwala` | ⚠️ Qeemat mein farq: catalog mein Rs.450, aapne Rs.400; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| HAR-28 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| HAR-29 | `2` | Catalog rate Rs.450 par draft; "Confirm order" |  | ☐ |
| HAR-30 | `cancel` | Draft khatam |  | ☐ |
| HAR-31 | `Usman, PVC Pipe 1 inch, 03051234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| HAR-32 | `Zara, PVC Pipe 1 inch, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| HAR-33 | `cancel` | Draft khatam |  | ☐ |
| HAR-34 | `walkin PVC Pipe 1 inch 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.450 |  | ☐ |
| HAR-35 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| HAR-36 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| HAR-37 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| HAR-38 | `add tracking: Usman, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| HAR-39 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| HAR-40 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| HAR-41 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| HAR-42 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| HAR-43 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| HAR-44 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| HAR-45 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| HAR-46 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| HAR-47 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| HAR-48 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| HAR-49 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| HAR-50 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| HAR-51 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| HAR-52 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| HAR-53 | `margin` | Wall Paint 4 ltr: margin 21% ke qareeb, munasib discount ~11%, break-even ~21% |  | ☐ |
| HAR-54 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| HAR-55 | `kis supplier ka maal sab se zyada bik raha hai` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| HAR-56 | `کس سپلائر کا مال سب سے زیادہ بک رہا ہے` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| HAR-57 | `which supplier's goods sell the most` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| HAR-58 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| HAR-59 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; PVC Pipe 1 inch ka cost nahi ('Cost price nahi: ...'); jaise line: "Wall Paint 4 ltr - 3800, cost 3000, stock 20, category paint" |  | ☐ |
| HAR-60 | `PVC Pipe 1 inch - 1000, cost 1200` | PVC Pipe 1 inch ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| HAR-61 | `data check` | 🔎 'Cost price >= sale price (1): PVC Pipe 1 inch (cost 1200 / price 1000)' |  | ☐ |
| HAR-62 | `PVC Pipe 1 inch - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| HAR-63 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| HAR-64 | `wall paint 4 ltr - 3800` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| HAR-65 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| HAR-66 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| HAR-67 | `🎤 (bol kar) "Usman ka 5 pvc pipe 1 inch ka order, phone 03051234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| HAR-68 | `🎤 "kis supplier ka maal sab se zyada bik raha hai"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| HAR-69 | `🎤 "کس سپلائر کا مال سب سے زیادہ بک رہا ہے"` | Urdu voice se wohi report | 🤖 | ☐ |
| HAR-70 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="general"></a>
## General dukaan

Test seller: **Test General Store**, shehar **Lahore**, business type **Mix store**. Test buyer: **Ayesha**, phone `03001234567`, address Gulberg Lahore.

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| | **A. Setup aur dukaan ki qism** | | | |
| GEN-01 | `start` | Language ka sawal ya shuru ka message (Roman Urdu / English / اردو buttons) |  | ☐ |
| GEN-02 | `Test General Store` | Dukaan ka naam save; agla step: shehar / business type maangta hai (ya catalog size) |  | ☐ |
| GEN-03 | `Lahore, Mix store, @test.general.store` | Business info save; Mix store pehchana jaye to "industry" likhne ka mashwara |  | ☐ |
| GEN-04 | `industry` | Dukaan ki qism ke buttons ki list (General dukaan samet 7 options) |  | ☐ |
| GEN-05 | `industry general` | ✅ Dukaan ki qism: General dukaan; product ki examples, order ka tareeqa, Tips, aur "sab se pehle yeh likhein" wali line |  | ☐ |
| GEN-06 | `industry kuch-aur-xyz` | "samajh nahi aaya" aur dobara buttons ki list |  | ☐ |
| GEN-07 | `business type mix store` | Wohi General dukaan guide (alias se pehchana gaya) |  | ☐ |
| GEN-08 | `guide` | Guide ka sawal (Poora guide / Sirf orders / Sirf payments) |  | ☐ |
| GEN-09 | `Sirf orders` | Order ka example "Ayesha, 2 suit, 03001234567" |  | ☐ |
| | **B. Catalog (products)** | | | |
| GEN-10 | `Lawn Suit - 3500, cost 2400, stock 20, category lawn` | ✅ Lawn Suit save: Rs.3,500, cost 2,400, stock 20, category lawn |  | ☐ |
| GEN-11 | `Lawn Suit - 3500, cost 2400, stock 20, category lawn` | "pehle se catalog mein hai — koi tabdeeli nahi" (dobara bhejne par kuch nahi badalta) |  | ☐ |
| GEN-12 | `Lawn Suit - 3600` | "price updated: 3,500 → 3,600" |  | ☐ |
| GEN-13 | `Lawn Suit - 3500` | Price wapas 3,500 |  | ☐ |
| GEN-14 | `Kurti - 1800<br>Sugar 5 kg - 500` | Dono products ek saath save (do lines wala message): Kurti, Sugar 5 kg |  | ☐ |
| GEN-15 | `catalog` | Teeno products ki list, qeematon ke saath |  | ☐ |
| GEN-16 | `Lawn Suit - -5` | Product save NAHI hota (naam '-' par khatam, price -5): `catalog` mein is naam ka koi naya product nahi |  | ☐ |
| GEN-17 | `Lawn Suit - abc` | Product save NAHI hota (price 'abc' number nahi): `catalog` mein badlav nahi |  | ☐ |
| | **C. Order banana** | | | |
| GEN-18 | `Ayesha, 2 lawn suit, 03001234567, Gulberg Lahore` | Draft: customer Ayesha, 2x Lawn Suit, total Rs.7,000; Save / Edit / Cancel buttons ("Confirm order") | 🤖 | ☐ |
| GEN-19 | `✅ Save (ya yes)` | Order #1 save, status PENDING, total Rs.7,000 |  | ☐ |
| GEN-20 | `order 1` | Order #1 poora: Lawn Suit x2, Rs.7,000, customer Ayesha |  | ☐ |
| GEN-21 | `Ayesha ka order` | Is customer ke orders ki list (#1) |  | ☐ |
| GEN-22 | `Ayesha, 1 lwan suit, 03001234567, Gulberg Lahore` | "lwan suit" catalog mein nahi mila + qareeb ka product button (Lawn Suit) aur "➕ Naya product" (did-you-mean) | 🤖 | ☐ |
| GEN-23 | `2 Lawn Suit` | Lawn Suit chun liya gaya; draft aage barhta hai (agar sawal baaqi ho to wohi pooche) |  | ☐ |
| GEN-24 | `cancel` | Draft khatam, koi order nahi bana |  | ☐ |
| GEN-25 | `Ayesha ne Lawn Suit 3200 ki li, 03001234567, Gulberg Lahore` | ⚠️ Qeemat mein farq: catalog mein Rs.3,500, aapne Rs.3,200; 3 options: 1 likhi hui qeemat / 2 catalog rate / 3 catalog update | 🤖 | ☐ |
| GEN-26 | `haan theek hai` | Dobara 1, 2 ya 3 likhne ka kaha jata hai (draft nahi bantaa) |  | ☐ |
| GEN-27 | `2` | Catalog rate Rs.3,500 par draft; "Confirm order" |  | ☐ |
| GEN-28 | `cancel` | Draft khatam |  | ☐ |
| GEN-29 | `Ayesha, Lawn Suit, 03001234567` | Address missing ho to bot address poochta hai; ya draft (address optional jahan setting ho) | 🤖 | ☐ |
| GEN-30 | `Zara, Lawn Suit, 12345` | ⚠️ Phone sahi nahi (5 digits) — dobara sahi number maangta hai; order save NAHI | 🤖 | ☐ |
| GEN-31 | `cancel` | Draft khatam |  | ☐ |
| GEN-32 | `walkin Lawn Suit 1` | Counter sale ka draft (customer 'Walk-in', phone nahi poochta) Rs.3,500 |  | ☐ |
| GEN-33 | `yes` | Order save: Delivered + Paid (counter sale) |  | ☐ |
| GEN-34 | `mark 1 shipped` | Order #1 → SHIPPED; "label 1" ka mashwara |  | ☐ |
| GEN-35 | `label 1` | Parcel label PDF (to/from, order id, COD amount) aata hai |  | ☐ |
| GEN-36 | `add tracking: Ayesha, TCS 123456` | Tracking jud gayi (TCS 123456) |  | ☐ |
| GEN-37 | `mark 1 delivered` | Order #1 → DELIVERED; COD ho to "Cash collect ho gaya?" poochta hai |  | ☐ |
| GEN-38 | `yes` | Cash collect: payment PAID; drawer khula ho to expected cash batata hai |  | ☐ |
| GEN-39 | `receipt 1` | PDF receipt document ke taur par aati hai (seller ke chat mein) |  | ☐ |
| GEN-40 | `order 1` | Status Delivered, payment Paid |  | ☐ |
| GEN-41 | `mark 1 returned` | Delivered se Returned allowed; sales/loyalty se nikal jata hai (ya jawab batata hai kya hua) |  | ☐ |
| GEN-42 | `undo` | Aakhri action wapas (Returned se pehle wali halat) |  | ☐ |
| GEN-43 | `cancel 99` | "Order #99 nahi mila" + orders dekhne ka mashwara |  | ☐ |
| GEN-44 | `orders today` | Aaj ke orders ki list (#1 aur counter sale) |  | ☐ |
| GEN-45 | `today's summary` | Aaj ka summary: orders, sales, status ki ginti |  | ☐ |
| GEN-46 | `aaj ka hisaab` | Summary + sab se zyada bika product + aaj ka kharcha + drawer (band hai) |  | ☐ |
| GEN-47 | `آج کا حساب` | Wohi hisaab (Urdu mein likha) |  | ☐ |
| GEN-48 | `unpaid orders` | Jin orders ka paisa baaqi hai (balance ke saath) |  | ☐ |
| GEN-49 | `expense 500 packaging` | ✅ Kharcha 500 (category packaging) |  | ☐ |
| GEN-50 | `expenses today` | Aaj ka kharcha: packaging 500 |  | ☐ |
| GEN-51 | `margin` | Lawn Suit: margin 31% ke qareeb, munasib discount ~16%, break-even ~31% |  | ☐ |
| GEN-52 | `profit` | Profit = sale − cost (jin orders ka cost likha hai); jin ka cost nahi, unka zikr "cost nahi" ke taur par |  | ☐ |
| | **E. Reports aur hisaab** | | | |
| GEN-53 | `mere paas kitni categories hain` | Seller ke apne alfaaz ka sawal samjha gaya (Roman Urdu); jawab asli data se |  | ☐ |
| GEN-54 | `میرے پاس کتنی کیٹیگریز ہیں` | Wohi sawal Urdu mein; wohi qism ka jawab |  | ☐ |
| GEN-55 | `how many categories do I have` | Wohi sawal English mein; wohi qism ka jawab |  | ☐ |
| GEN-56 | `export` | Excel (.xlsx) file bheji jati hai (Orders, Customers, Catalog... aur Guide tab) |  | ☐ |
| | **F. Data check (missing aur galat data)** | | | |
| GEN-57 | `data check` | 📋 Data check: X/5 mukammal, Y galat entries; Kurti ka cost nahi ('Cost price nahi: ...'); jaise line: "Lawn Suit - 3500, cost 2400, stock 20, category lawn" |  | ☐ |
| GEN-58 | `Kurti - 1000, cost 1200` | Kurti ka cost price (1200) > sale price (1000) save ho jata hai (rok nahi) |  | ☐ |
| GEN-59 | `data check` | 🔎 'Cost price >= sale price (1): Kurti (cost 1200 / price 1000)' |  | ☐ |
| GEN-60 | `Kurti - 1800, cost 1100` | Theek kar diya; cost 1100 |  | ☐ |
| GEN-61 | `data check` | Ab 'Cost price >= sale price' wali line nahi |  | ☐ |
| GEN-62 | `lawn suit - 3500` | Naya product bana ya 'pehle se hai' (case alag) — dono dafa se data check par 'Milte-julte product naam' ho sakta hai |  | ☐ |
| GEN-63 | `checklist` | `data check` jaisa hi jawab |  | ☐ |
| GEN-64 | `ڈیٹا چیک` | Wohi jawab (Urdu mein likha command) |  | ☐ |
| | **G. Voice notes 🎤** | | | |
| GEN-65 | `🎤 (bol kar) "Ayesha ka 2 lawn suit ka order, phone 03001234567"` | Bot pehle "🎤 Maine suna: …" phir "➡️ Samjha: …" likhta hai, phir draft; paisay/qeemat badalne wali baat ho to YES maangta hai | 🤖 | ☐ |
| GEN-66 | `🎤 "mere paas kitni categories hain"` | "Maine suna" + "Samjha" + sawal ka jawab (report) | 🤖 | ☐ |
| GEN-67 | `🎤 "میرے پاس کتنی کیٹیگریز ہیں"` | Urdu voice se wohi report | 🤖 | ☐ |
| GEN-68 | `🎤 (khamoshi / shor)` | "dobara bhejein" jaisa jawab, koi order nahi bantaa | 🤖 | ☐ |

---

<a id="common"></a>
## Saari industries ke liye common cases

| # | Bhejein | Mutawaqqa jawab | AI | Status |
|---|---|---|---|---|
| COM-01 | `menu` | Main Menu list (8 category) |  | ☐ |
| COM-02 | `help` | Zaroori Madad list |  | ☐ |
| COM-03 | `kya karun` | Is waqt ka tip (idle ho to help list) |  | ☐ |
| COM-04 | `version` | Build ka SHA (7 hex) aur process start ka waqt |  | ☐ |
| COM-05 | `change language` | Zubaan ke buttons; English chunne par agla jawab English mein |  | ☐ |
| COM-06 | `اردو` | Urdu script chunne par bot ke jawab اردو mein |  | ☐ |
| COM-07 | `Roman Urdu` | Wapas Roman Urdu |  | ☐ |
| COM-08 | `shortcut off` | Quick-action buttons band; "shortcut on" se wapas |  | ☐ |
| COM-09 | `drawer open 5000` | Drawer khula, opening Rs.5,000 |  | ☐ |
| COM-10 | `cash out 200 chai` | Cash out 200; expected = 4,800 |  | ☐ |
| COM-11 | `cash undo` | Aakhri cash entry wapas |  | ☐ |
| COM-12 | `drawer close 5000` | Counted 5,000 vs expected; farq (short/zyada) batata hai |  | ☐ |
| COM-13 | `create discount: EID10 10%` | EID10 code, 10% discount |  | ☐ |
| COM-14 | `create discount: EID10 10%` | Code pehle se maujood — mana kar deta hai |  | ☐ |
| COM-15 | `create discount: BAD 150%` | Percent 1-100 se bahar — mana |  | ☐ |
| COM-16 | `reset account` | Tasdeeq maangta hai; YES par poora data saaf (naye test se pehle) |  | ☐ |
| COM-17 | `Rs 500 - 300` | Product NAHI banta (koi naam nahi) |  | ☐ |
| COM-18 | `order 12 - 500` | Product NAHI banta (order wala lafz) |  | ☐ |
| COM-19 | `House 5 - 300` | Product NAHI banta (address jaisa) |  | ☐ |
| COM-20 | `🎤 + 31 voice notes ek ghante mein` | Hadd (default 30/ghanta) ke baad "limit" wala jawab | 🤖 | ☐ |
| COM-21 | `(ek hi message dobara bhejna, Meta retry)` | Duplicate nahi banta — ek hi order |  | ☐ |
| COM-22 | `Kurti (naam ke pehle zero-width space copy-paste ho jaye)` | Alag look-alike product NAHI banta; wohi Kurti |  | ☐ |

---

<a id="day"></a>
## Poore din ka scenario (overall business) — kisi bhi ek industry ke data se

Ek dukaan ka poora din, waqt ke hisaab se. Upar wali industry ka koi bhi seller istemal karein; neeche `<P1>` = pehla product, `<P2>` = doosra.

| Waqt | Bhejein | Mutawaqqa jawab | Status |
|---|---|---|---|
| Subah 9:00 | `drawer open 5000` | Drawer khula, opening 5,000 | ☐ |
| 9:15 | `stock <P1> 20` (agar track nahi) | Stock 20 | ☐ |
| 10:00 | Customer 1 ka order (free-form 🤖): `Ali, 2 <P1>, 03001112222, Lahore` | Draft → Save → Order #1 PENDING | ☐ |
| 10:30 | `walkin <P2> 1 yes (cash)` | Counter sale, Delivered+Paid, cash drawer mein | ☐ |
| 11:00 | `mark 1 shipped`  `add tracking: Ali, TCS 777` | Order #1 SHIPPED, tracking jud gayi | ☐ |
| 12:00 | `expense 800 petrol` | Kharcha save | ☐ |
| 13:00 | Customer 2: `Sara ne <P1> <catalog se alag qeemat> ki li, 03003334444, Karachi` 🤖 | Qeemat mein farq → 1/2/3; 1 chunein | ☐ |
| 14:00 | `order 2 advance 500` | Advance 500; balance baqi | ☐ |
| 15:00 | `order for Sara` | Customer ka naam; "items likhein" + usual items ke buttons | ☐ |
| 16:00 | `mark 1 delivered` → `yes` | Cash collect, paid | ☐ |
| 17:00 | `unpaid orders` | Sirf Sara ka order (balance ke saath) | ☐ |
| 18:00 | `aaj ka hisaab` | Aaj ki sale, sab se zyada bika product, kharcha 800, drawer expected cash | ☐ |
| 18:15 | `profit today` | Profit = sale − cost (cost wale orders); jin ka cost nahi unka zikr | ☐ |
| 18:30 | `drawer close <ginti ki hui raqam>` | Expected vs counted ka farq | ☐ |
| 19:00 | `data check` | Gaps ki list (cost / category / stock / purane orders / kharcha) | ☐ |
| 19:15 | `undo` | Aakhri action wapas | ☐ |
| Hafte ke aakhir | `weekly summary` / `sales by category last week` | Hafte ka summary / category-wise sale | ☐ |
| Mahine ke aakhir | `monthly net` / `export` | Sale − kharcha; Excel file | ☐ |

## Bug likhne ka tareeqa

Fail hone par likhein: test number (jaise `CLO-14`), jo bheja (screenshot), bot ne kya jawab diya, aur aap kya expect kar rahe thay. Voice test mein transcript ki line ("🎤 Maine suna") bhi bhejein.

