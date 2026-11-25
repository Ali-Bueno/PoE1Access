using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using BaseComp = ExileCore.PoEMemory.Components.Base;
using StackComp = ExileCore.PoEMemory.Components.Stack;
using RenderComp = ExileCore.PoEMemory.Components.RenderItem;
using static PoE1Access.Loc;

namespace PoE1Access;

// Currency and vendor prices.
//
// Path of Exile 1 has no single money: vendors price items in currency items (Scrolls of Wisdom,
// Portal Scrolls, Orbs of Transmutation, Alteration, Chance, Alchemy, Chaos Orbs and the rest, and
// their shards). Since 3.19 a purchase takes the currency from your bag and from any stash tabs
// you have opened. Gold (since 3.25) pays for passive respecs and the Currency Exchange, not for
// vendors' items.
//
//   Your currency: in a window or menu, hold L3 and push the right stick down: your gold, then
//   every currency in your bag, and in the open stash tab, by name and count. (A key can be set
//   for it in Exile API's menu, CurrencyHotkey.)
//
//   A vendor's price: the game shows it only on screen, in the selected item's tooltip, as a
//   currency's picture and a number ("1x" beside an Orb of Transmutation). With a vendor's window
//   open, the pictures of currency that appear when you select an item are matched to their
//   names (by your own currency's pictures, which the game names in its language, else by the
//   picture's file), with the number beside each: "Price: 1 Orb of Transmutation, you have 3",
//   or "you need 2 more". Hold L3 and push the right stick up to hear it again.
//
//   Selling: what the vendor offers in return is read as it changes ("Vendor offers: 3 Alteration
//   Shard"), from the sell window's own list.
//
// NOT YET SEEN IN THE GAME (written 25 Nov 2026): how the price is laid out on screen is not
// known; when a price cannot be found, what appeared on screen is written to the log ("Vendor
// price not found: ..."), so it can be made exact from the first test.
public partial class PoE1AccessCore
{
    private long _priceAnchorMs = -100000, _priceItemAddr;
    private bool _priceDone = true;
    private string _priceText = "";
    private readonly HashSet<long> _priceLogged = new HashSet<long>();

    // ---- What you have ----

    private static bool IsCurrency(Entity item) =>
        (Try(() => item.Path, "") ?? "").StartsWith("Metadata/Items/Currency/", StringComparison.OrdinalIgnoreCase);

    private static string ArtOfItem(Entity item) =>
        Try(() => item.GetComponent<RenderComp>()?.ResourcePath, "") ?? "";

    /// <summary>Currency in the bag and the open stash tab: name -> (count, picture).</summary>
    private Dictionary<string, (int Count, string Art)> CurrencyHeld(out bool stashCounted)
    {
        var held = new Dictionary<string, (int Count, string Art)>(StringComparer.OrdinalIgnoreCase);
        void Add(Entity item)
        {
            if (item == null || item.Address == 0 || !IsCurrency(item)) return;
            string name = CleanText(Try(() => GetComp<BaseComp>(item)?.Name, "") ?? "");
            if (name.Length == 0) return;
            int n = Math.Max(1, Try(() => GetComp<StackComp>(item)?.Size ?? 1, 1));
            held.TryGetValue(name, out var was);
            held[name] = (was.Count + n, was.Art ?? ArtOfItem(item));
        }
        var server = GetServerData();
        if (server != null) foreach (var s in Slots(server, InventorySlotE.MainInventory1)) Add(s.Item);
        stashCounted = false;
        var stash = Try(() => GameController.IngameState.IngameUi.StashElement, null);
        if (stash != null && Try(() => stash.IsVisible, false))
        {
            var items = Try(() => stash.VisibleStash?.VisibleInventoryItems?.ToList(), null);
            if (items != null)
            {
                stashCounted = true;
                foreach (var it in items) Add(Try(() => it.Item, null));
            }
        }
        return held;
    }

    private void SayCurrency()
    {
        var held = CurrencyHeld(out bool stash);
        int gold = Try(() => GameController.IngameState.ServerData.Gold, -1);
        var parts = new List<string>();
        if (gold > 0) parts.Add(T($"Gold {gold}", $"Oro {gold}"));
        if (held.Count == 0) parts.Add(stash ? T("No currency in your bag or this stash tab", "No hay moneda en tu bolsa ni en esta pestaña del alijo") : T("No currency in your bag", "No hay moneda en tu bolsa"));
        else
        {
            parts.Add(stash ? T("Currency in your bag and this stash tab", "Moneda en tu bolsa y esta pestaña del alijo") : T("Currency in your bag", "Moneda en tu bolsa"));
            parts.AddRange(held.OrderByDescending(kv => kv.Value.Count).Select(kv => kv.Value.Count + " " + kv.Key));
        }
        Say(JoinSpeech(parts), true);
    }

    // ---- A vendor's price ----

    private bool PurchaseOpen()
    {
        var ui = Try(() => GameController.IngameState.IngameUi, null);
        if (ui == null) return false;
        return Try(() => ui.PurchaseWindow?.IsVisible ?? false, false) || Try(() => ui.PurchaseWindowHideout?.IsVisible ?? false, false);
    }

    /// <summary>An item was selected; with a vendor's window open and the item not yours, its price is looked for.</summary>
    private void PriceAnchor(Entity item, bool yours)
    {
        if (item == null || yours || !PurchaseOpen()) return;
        if (item.Address == _priceItemAddr && !_priceDone) return;
        _priceItemAddr = item.Address;
        _priceAnchorMs = _clock.ElapsedMilliseconds;
        _priceDone = false;
        _priceText = "";
    }

    // Called from the focus walk with every element shown now and those new since the last walk.
    private void CatchPrice(long now, Dictionary<long, (Element E, string Path)> elements)
    {
        if (_priceDone) return;
        long since = now - _priceAnchorMs;
        if (since < 150) return;
        var mem = Mem;

        // Currency pictures shown since the item was selected; after a moment, any shown outside
        // the vendor's and your own grids (a tooltip kept on screen).
        var fresh = _recentFresh.Where(f => f.Ms >= _priceAnchorMs - 300).Select(f => f.Addr).ToHashSet();
        var found = new List<(string Path, string Art, int Amount)>();
        foreach (var kv in elements)
        {
            if (since < 500 && !fresh.Contains(kv.Key)) continue;
            string art = ArtPathOf(kv.Value.E);
            if (!art.StartsWith("Art/2DItems/Currency/", StringComparison.OrdinalIgnoreCase)) continue;
            if (Try(() => UiText.TypeOf(mem, kv.Value.E), (ElementType?)null) == ElementType.InventoryItem) continue;
            int amount = AmountBeside(kv.Value.E);
            if (amount <= 0) continue;
            found.Add((kv.Value.Path, art, amount));
        }

        if (found.Count == 0)
        {
            if (since < 1200) return;
            _priceDone = true;
            if (_priceLogged.Add(_priceItemAddr) && _priceLogged.Count < 100)
            {
                var seen = _recentFresh.Where(f => f.Ms >= _priceAnchorMs - 300 && elements.ContainsKey(f.Addr)).Take(60)
                    .Select(f => { var el = elements[f.Addr]; string t = CleanText(UiText.Text(mem, el.E)); string a = ArtPathOf(el.E); return "[" + el.Path + "]" + (t.Length > 0 ? " \"" + t + "\"" : "") + (a.Length > 0 ? " " + a : ""); })
                    .Where(s => s.Contains('"') || s.Contains("Art/"));
                PluginLog.Write("Vendor price not found: " + string.Join(" | ", seen));
            }
            return;
        }

        _priceDone = true;
        var held = CurrencyHeld(out _);
        var parts = new List<string>();
        foreach (var f in found.GroupBy(x => x.Art).Select(g => g.First()))
        {
            string name = CurrencyNameForArt(f.Art, held);
            int have = held.TryGetValue(name, out var h) ? h.Count : 0;
            string afford = have >= f.Amount ? T($"you have {have}", $"tienes {have}")
                : have == 0 ? T("you have none", "no tienes")
                : T($"you have {have}, {f.Amount - have} more needed", $"tienes {have}, faltan {f.Amount - have}");
            parts.Add(f.Amount + " " + name + ", " + afford);
            PluginLog.Write("Vendor price: [" + f.Path + "] " + f.Art + " x" + f.Amount + " -> " + name + ", have " + have);
        }
        _priceText = T("Price: ", "Precio: ") + string.Join(". ", parts);
        Say(_priceText, false);
    }

    private void SayPriceAgain()
    {
        if (!PurchaseOpen()) { Say(T("No vendor window open", "No hay ventana de vendedor abierta"), true); return; }
        Say(_priceText.Length > 0 ? _priceText : T("No price found for this item", "No se encontró precio para este objeto"), true);
    }

    // The number beside a currency picture: in it, its parent's other children, or its
    // grandparent's ("1x", "x1", "3").
    private int AmountBeside(Element icon)
    {
        var mem = Mem;
        int Parse(string t)
        {
            var m = Regex.Match(CleanText(t ?? ""), @"^(?:[x×]\s*(\d+)|(\d+)\s*[x×]?)$", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            return int.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out int n) ? n : 0;
        }
        int own = Parse(UiText.Text(mem, icon));
        if (own > 0) return own;
        Element cur = icon;
        for (int up = 0; up < 2; up++)
        {
            Element parent = UiText.ParentOf(mem, cur);
            if (parent == null || parent.Address == 0) break;
            var kids = Try<IList<Element>>(() => parent.Children, null);
            if (kids == null || kids.Count > 12) break;
            foreach (Element k in kids)
            {
                if (k.Address == cur.Address) continue;
                var texts = new List<string>();
                CollectText(k, 2, texts);
                foreach (string t in texts) { int n = Parse(t); if (n > 0) return n; }
            }
            cur = parent;
        }
        return 0;
    }

    // The first art file an element shows ("Art/2DItems/Currency/CurrencyUpgradeToMagic.dds").
    private string ArtPathOf(Element e)
    {
        var mem = Mem;
        foreach (int off in new[] { 0x230, 0x510 })
        {
            try
            {
                long p = mem.Read<long>(e.Address + off + UiText.Shift);
                if (p < 0x10000 || p > 0x7FFF_FFFF_FFFF) continue;
                long s = mem.Read<long>(p + 8);
                if (s < 0x10000 || s > 0x7FFF_FFFF_FFFF) continue;
                string file = ReadAsciiOrWide(s);
                if (file.StartsWith("Art/", StringComparison.OrdinalIgnoreCase)) return file.Split('|')[0];
            }
            catch { }
        }
        return "";
    }

    // A currency's name from its picture: the game's name for your own currency with that
    // picture, else a known picture, else the file's name in words.
    private static readonly Dictionary<string, string> CurrencyArt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CurrencyIdentification"] = "Scroll of Wisdom", ["CurrencyIdentificationShard"] = "Scroll Fragment",
        ["CurrencyPortal"] = "Portal Scroll", ["CurrencyUpgradeToMagic"] = "Orb of Transmutation",
        ["CurrencyUpgradeToMagicShard"] = "Transmutation Shard", ["CurrencyRerollMagic"] = "Orb of Alteration",
        ["CurrencyRerollMagicShard"] = "Alteration Shard", ["CurrencyAddModToMagic"] = "Orb of Augmentation",
        ["CurrencyUpgradeRandomly"] = "Orb of Chance", ["CurrencyUpgradeToRare"] = "Orb of Alchemy",
        ["CurrencyUpgradeToRareShard"] = "Alchemy Shard", ["CurrencyRerollRare"] = "Chaos Orb",
        ["CurrencyUpgradeMagicToRare"] = "Regal Orb", ["CurrencyRerollSocketColours"] = "Chromatic Orb",
        ["CurrencyRerollSocketNumbers"] = "Jeweller's Orb", ["CurrencyRerollSocketLinks"] = "Orb of Fusing",
        ["CurrencyConvertToNormal"] = "Orb of Scouring", ["CurrencyPassiveRefund"] = "Orb of Regret",
        ["CurrencyRerollImplicit"] = "Blessed Orb", ["CurrencyModValues"] = "Divine Orb",
        ["CurrencyAddModToRare"] = "Exalted Orb", ["CurrencyVaal"] = "Vaal Orb", ["CurrencyGemQuality"] = "Gemcutter's Prism",
        ["CurrencyArmourQuality"] = "Armourer's Scrap", ["CurrencyWeaponQuality"] = "Blacksmith's Whetstone",
        ["CurrencyFlaskQuality"] = "Glassblower's Bauble", ["CurrencyMapQuality"] = "Cartographer's Chisel",
    };

    private static string CurrencyNameForArt(string art, Dictionary<string, (int Count, string Art)> held)
    {
        string file = System.IO.Path.GetFileNameWithoutExtension(art ?? "");
        foreach (var kv in held)
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(kv.Value.Art ?? ""), file, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        if (CurrencyArt.TryGetValue(file, out string known)) return known;
        string w = file.StartsWith("Currency", StringComparison.OrdinalIgnoreCase) ? file.Substring(8) : file;
        return Regex.Replace(w, "(?<=[a-z])(?=[A-Z0-9])", " ");
    }

    // ---- Selling: what the vendor offers ----

    private long _nextOfferPollMs;
    private string _offerSig = "";

    private void PollSellOffer(long now)
    {
        if (now < _nextOfferPollMs) return;
        _nextOfferPollMs = now + 300;
        var ui = Try(() => GameController.IngameState.IngameUi, null);
        var sell = Try(() => ui?.SellWindow, null);
        if (sell == null || !Try(() => sell.IsVisible, false)) sell = Try(() => ui?.SellWindowHideout, null);
        if (sell == null || !Try(() => sell.IsVisible, false)) { _offerSig = ""; return; }
        var items = Try(() => sell.OtherOfferItems?.ToList(), null);
        if (items == null) return;
        var names = items.Select(i => Try(() => i.Item, null)).Where(i => i != null && i.Address != 0).Select(i => ItemTitle(i)).Where(n => n.Length > 0).ToList();
        string sig = string.Join("|", names);
        if (sig == _offerSig) return;
        bool first = _offerSig.Length == 0 && names.Count == 0;
        _offerSig = sig;
        if (first) return;
        Say(names.Count > 0 ? T($"Vendor offers: {string.Join(". ", names)}", $"El vendedor ofrece: {string.Join(". ", names)}")
                            : T("Vendor offers nothing", "El vendedor no ofrece nada"), false);
    }
}
