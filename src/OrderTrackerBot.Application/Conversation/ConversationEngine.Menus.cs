using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

public partial class ConversationEngine
{
    // The menus show examples from the seller's kind of shop, never a fixed one (a grocer must not see a lawn suit).
    // Each template takes its own arguments, numbered from {0} (the test enforces it): menu.orders {0} order example;
    // menu.reports {0} simple product name; menu.catalog {0} full product line, {1} simple product name, {2} category word;
    // menu.shop {0} simple product name, {1} menu tip; commands.041 {0} menu tip; commands.042 {0} menu tip, {1} shop title.
    private static object?[] MenuBodyArgs(string bodyKey, IndustryProfile industry) => bodyKey switch
    {
        "menu.orders" => new object?[] { industry.OrderExample },
        "menu.reports" => new object?[] { industry.SimpleProductName },
        "menu.catalog" => new object?[] { industry.FullProduct, industry.SimpleProductName, industry.Category },
        "menu.shop" => new object?[] { industry.SimpleProductName, industry.MenuTip },
        _ => Array.Empty<object?>(),
    };

    // Screen 10: the full command list (the 9 core commands are what a seller memorizes; the rest lives under "menu").
    private static string HelpTextFor(Seller seller) => MessageTemplates.Get("commands.041", Industry(seller).MenuTip);

    // Screen 10b: categorized menu.
    private static string MenuTextFor(Seller seller) => MessageTemplates.Get("commands.042", Industry(seller).MenuTip, Industry(seller).Title);
}
