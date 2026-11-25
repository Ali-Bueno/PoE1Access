using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using static PoE1Access.Loc;

namespace PoE1Access;

// Your hideout (written 26 Nov 2026, not yet tried in the game):
//   * When you have one (the game's own record of it, Player.Hideout), a tip says so once, with
//     how to get there: the hideout button on a waypoint's world map.
//   * In a hideout, everything you can use (the stash, the waypoint, the crafting bench, the map
//     device, the characters who live there) is under Objects in the scanner, and its
//     decorations, which do nothing, are left out.
//   * At the crafting bench, the cost of the option you move to is said with whether you can pay
//     it, from the currency pictures and numbers in its row, as vendor prices are.
public partial class PoE1AccessCore
{
    private long _nextHideoutPollMs;

    private void PollHideout(long now)
    {
        if (now < _nextHideoutPollMs) return;
        _nextHideoutPollMs = now + 3000;
        string name = Try(() => GameController.Player.GetComponent<ExileCore.PoEMemory.Components.Player>()?.Hideout?.Name, "") ?? "";
        if (name.Length > 0)
            Tip("hideout", T($"Tip: you have a hideout, {name}. Go there from any waypoint: its world map has a hideout button. It holds your stash, a waypoint, a crafting bench and the map device. More in the tutorial, Game mechanics.",
                             $"Consejo: tienes una guarida, {name}. Ve desde cualquier transportador: su mapa del mundo tiene un botón de guarida. Tiene tu alijo, un transportador, un banco de artesanía y el dispositivo de mapas. Más en el tutorial, Mecánicas del juego."));
        if (Try(() => GameController.Area.CurrentArea.IsHideout, false))
            Tip("hideout visit", T("Tip: this is your hideout, safe like a town. What you can use is under Objects in the scanner; its decorations are left out. More in the tutorial, Game mechanics.",
                                   "Consejo: esta es tu guarida, segura como una ciudad. Lo que puedes usar está en Objetos del entorno en el escáner; sus decoraciones se omiten. Más en el tutorial, Mecánicas del juego."));
    }

    private bool BenchOpen() => Try(() => GameController.IngameState.IngameUi.CraftBench?.IsVisible ?? false, false);

    /// <summary>The crafting bench option at a selection: "Cost: 1 Orb of Transmutation, you have 3", or "".</summary>
    private string BenchCost(Element e)
    {
        if (e == null || !BenchOpen()) return "";
        var mem = Mem;
        // The option's row: the selection or a parent holding the cost's pictures.
        Element row = e;
        for (int up = 0; up < 4 && row != null && row.Address != 0; up++)
        {
            var costs = new List<(string Art, int Amount)>();
            var stack = new Stack<(Element E, int D)>();
            stack.Push((row, 0));
            int seen = 0;
            while (stack.Count > 0 && seen++ < 80)
            {
                var (x, d) = stack.Pop();
                string art = ArtPathOf(x);
                if (art.StartsWith("Art/2DItems/Currency/", StringComparison.OrdinalIgnoreCase))
                {
                    int n = AmountBeside(x);
                    if (n > 0 && !costs.Any(c => c.Art == art)) costs.Add((art, n));
                }
                if (d >= 4) continue;
                var kids = Try<IList<Element>>(() => x.Children, null);
                if (kids != null && kids.Count <= 30) foreach (Element k in kids) stack.Push((k, d + 1));
            }
            if (costs.Count > 0)
            {
                var held = CurrencyHeld(out _);
                var parts = costs.Select(c =>
                {
                    string name = CurrencyNameForArt(c.Art, held);
                    int have = held.TryGetValue(name, out var h) ? h.Count : 0;
                    string afford = have >= c.Amount ? T($"you have {have}", $"tienes {have}")
                        : have == 0 ? T("you have none", "no tienes")
                        : T($"you have {have}, {c.Amount - have} more needed", $"tienes {have}, faltan {c.Amount - have}");
                    return c.Amount + " " + name + ", " + afford;
                });
                string text = T("Cost: ", "Coste: ") + string.Join(". ", parts);
                PluginLog.Write("Bench cost: " + text);
                return text;
            }
            // Stop at a list: higher up the pictures belong to other options.
            Element parent = UiText.ParentOf(mem, row);
            if (parent == null || Try(() => parent.ChildCount, 99L) > 6) break;
            row = parent;
        }
        return "";
    }
}
