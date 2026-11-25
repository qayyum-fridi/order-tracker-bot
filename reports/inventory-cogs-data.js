// Sample data for the Inventory COGS report. Replace window.COGS_DATA with the API response when it is ready.
// Shape: products[] -> variants[] -> events[]. Event qty: + adds stock (restock, return, adjust), - removes it (sale).
// unitCost is the cost per unit of the variant's own unit. Opening stock is the first event (type "opening").
window.COGS_DATA = {
  generatedAt: "2026-11-25",
  products: [
    { name: "Lawn Suit", sku: "LS-01", category: "Clothing", unit: "piece", variants: [
      { label: "S / Red", unitCost: 2400, events: [
        { date: "2026-10-01", type: "opening", qty: 10 }, { date: "2026-10-18", type: "sale", qty: -3 },
        { date: "2026-11-02", type: "restock", qty: 5 }, { date: "2026-11-20", type: "sale", qty: -2 } ] },
      { label: "M / Blue", unitCost: 2400, events: [
        { date: "2026-10-01", type: "opening", qty: 8 }, { date: "2026-11-05", type: "sale", qty: -4 },
        { date: "2026-11-14", type: "return", qty: 1 } ] } ] },
    { name: "Men's Cotton Shirt", sku: "MS-01", category: "Clothing", unit: "piece", variants: [
      { label: "M / White", unitCost: 1000, events: [
        { date: "2026-10-01", type: "opening", qty: 15 }, { date: "2026-10-25", type: "sale", qty: -6 } ] },
      { label: "L / Black", unitCost: 1000, events: [
        { date: "2026-10-01", type: "opening", qty: 12 }, { date: "2026-11-11", type: "adjust", qty: -2 }, { date: "2026-11-22", type: "sale", qty: -3 } ] } ] },
    { name: "Tapal Danedar Tea", sku: "TD-950", category: "Grocery", unit: "pack", variants: [
      { label: "950 g", unitCost: 950, events: [
        { date: "2026-10-01", type: "opening", qty: 20 }, { date: "2026-10-30", type: "sale", qty: -5 }, { date: "2026-11-24", type: "sale", qty: -2 } ] } ] },
    { name: "Nestle Milk", sku: "NM-1L", category: "Grocery", unit: "pack", variants: [
      { label: "1 L", unitCost: 250, events: [
        { date: "2026-10-01", type: "opening", qty: 30 }, { date: "2026-11-03", type: "sale", qty: -10 }, { date: "2026-11-15", type: "restock", qty: 12 } ] } ] },
    { name: "Surf Excel", sku: "SE-1KG", category: "Household", unit: "pack", variants: [
      { label: "1 kg", unitCost: 500, events: [
        { date: "2026-10-01", type: "opening", qty: 6 }, { date: "2026-11-08", type: "sale", qty: -2 } ] } ] }
  ]
};
