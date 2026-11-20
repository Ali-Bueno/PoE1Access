using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.Shared.Nodes;
using static PoE1Access.Loc;

namespace PoE1Access;

// The mod's own spoken settings menu, as in Poe2Access (its "Mod settings" menu): no need to
// see Exile API's F12 window.
//
//   Open:   hold L3 for a second and a half in play, or hold F3 for a second.
//   Move:   right stick up / down (arrow keys up / down).
//   Change: right stick left / right (arrow keys left / right): opens a submenu, toggles a
//           switch, moves a volume or speed in steps of 5, plays the sound it controls.
//   Back:   tap L3 (Backspace) goes up one level; at the top it closes.
//   Close:  double tap L3, or hold F3 again: "Settings closed".
// Exile API saves the values itself (config\PoE1Access\settings), as for its F12 window.
public partial class PoE1AccessCore
{
    private sealed class MenuItem
    {
        public string Label;
        public List<MenuItem> Children;               // a submenu
        public ToggleNode Toggle;                     // a switch
        public RangeNode<int> Range;                  // a number
        public int Step = 5;
        public Func<int, string> Words;               // how a number is said ("100 percent")
        public Action<int> OnChange;                  // a sample of what it controls
    }

    private List<MenuItem> _menuRoot;
    private readonly List<(List<MenuItem> Items, int Index)> _menuStack = new List<(List<MenuItem>, int)>();
    private readonly Dictionary<List<MenuItem>, int> _menuLastIndex = new Dictionary<List<MenuItem>, int>();
    public bool MenuOpen => _menuStack.Count > 0;

    private List<MenuItem> BuildMenu()
    {
        string Pct(int v) => T($"{v} percent", $"{v} por ciento");
        MenuItem Vol(string label, RangeNode<int> node, Action<int> sample) =>
            new MenuItem { Label = label, Range = node, Words = Pct, OnChange = sample };
        MenuItem Sw(string label, ToggleNode node) => new MenuItem { Label = label, Toggle = node };
        MenuItem Sub(string label, params MenuItem[] items) => new MenuItem { Label = label, Children = items.ToList() };

        return new List<MenuItem>
        {
            Sub(T("Sound volumes", "Volúmenes de sonido"),
                Vol(T("Master volume", "Volumen general"), Settings.MasterVolume, _ => SampleSound("loot")),
                Vol(T("Enemy, NPC and combat sounds", "Sonidos de enemigos, PNJ y combate"), Settings.EnemyVolume, _ => SampleSound("enemy")),
                Vol(T("Loot sounds", "Sonidos de botín"), Settings.LootVolume, _ => SampleSound("loot")),
                Vol(T("Radar", "Radar"), Settings.RadarVolume, _ => SampleSound("radar")),
                Vol(T("Guide", "Guía"), Settings.GuideVolume, _ => SampleSound("guide"))),
            Sub(T("Speech", "Voz"),
                new MenuItem
                {
                    Label = T("Speech speed", "Velocidad de la voz"), Range = Settings.SpeechRate, Words = Pct,
                    OnChange = v => ApplySpeechRate(true),
                },
                Sw(T("Describe areas on arrival", "Describir las áreas al llegar"), Settings.DescribeAreas),
                Sw(T("Name enemies coming into view", "Nombrar a los enemigos que aparecen a la vista"), Settings.AnnounceEnemies),
                Sw(T("Say which way enemies are", "Decir en qué dirección están los enemigos"), Settings.EnemyDirections),
                Sw(T("Name the targeted enemy", "Nombrar al enemigo apuntado"), Settings.AnnounceTarget),
                Sw(T("Life, mana and shield warnings", "Avisos de vida, maná y escudo"), Settings.AnnounceResources),
                Sw(T("Name things you walk up to", "Nombrar lo que tienes cerca al acercarte"), Settings.AnnounceNearby),
                Sw(T("Turn prompts while guiding", "Avisos de giro durante la guía"), Settings.RoutePrompts),
                Sw(T("Announce panels", "Anunciar los paneles"), Settings.AnnouncePanels)),
            Sub(T("Sounds on or off", "Sonidos activados o desactivados"),
                Sw(T("Wall radar", "Radar de paredes"), Settings.Radar),
                Sw(T("Unexplored ground pips", "Pitidos del terreno sin explorar"), Settings.UnexploredMark),
                Sw(T("Enemy sounds", "Sonidos de enemigos"), Settings.EnemySounds),
                Sw(T("NPC and player sounds", "Sonidos de PNJ y jugadores"), Settings.NpcSounds),
                Sw(T("Loot sounds", "Sonidos de botín"), Settings.LootSounds),
                new MenuItem
                {
                    Label = T("Which loot sounds", "Qué sonidos de botín"), Range = Settings.LootFilter, Step = 1,
                    Words = v => v == 0 ? T("all items", "todos los objetos") : v == 1 ? T("magic or better", "mágicos o mejores") : T("valuable only", "solo los valiosos"),
                },
                Sw(T("Targeting blips", "Pitidos de apuntado"), Settings.CrosshairBeep),
                Sw(T("Melee reach ping", "Aviso de alcance cuerpo a cuerpo"), Settings.ReachPing),
                new MenuItem { Label = T("Melee reach distance", "Distancia de alcance cuerpo a cuerpo"), Range = Settings.ReachDistance, Words = v => v.ToString() }),
            Sub(T("Reading", "Lectura"),
                Sw(T("Read the controller selection", "Leer la selección del mando"), Settings.ReadFocus),
                Sw(T("Read what is under the cursor", "Leer lo que hay bajo el cursor"), Settings.ReadOnHover)),
        };
    }

    private void OpenMenu()
    {
        _menuRoot ??= BuildMenu();
        _menuStack.Clear();
        _menuStack.Add((_menuRoot, _menuLastIndex.TryGetValue(_menuRoot, out int i) ? i : 0));
        _pad.MenuMode = true;
        PluginLog.Write("Settings menu opened");
        Say(T($"Mod settings. {DescribeMenuItem()}", $"Ajustes del mod. {DescribeMenuItem()}"), true);
    }

    private void CloseMenu()
    {
        if (!MenuOpen) return;
        foreach (var (items, index) in _menuStack) _menuLastIndex[items] = index;
        _menuStack.Clear();
        _pad.MenuMode = false;
        PluginLog.Write("Settings menu closed");
        Say(T("Settings closed", "Ajustes cerrados"), true);
    }

    private string DescribeMenuItem()
    {
        var (items, index) = _menuStack[^1];
        MenuItem it = items[index];
        string value = it.Children != null ? T("submenu", "submenú")
            : it.Toggle != null ? (it.Toggle.Value ? T("on", "activado") : T("off", "desactivado"))
            : it.Range != null ? (it.Words ?? (v => v.ToString()))(it.Range.Value)
            : "";
        return T($"{it.Label}, {value}, {index + 1} of {items.Count}", $"{it.Label}, {value}, {index + 1} de {items.Count}");
    }

    // dy: -1 up, +1 down; dx: -1 left, +1 right.
    private void MenuStep(int dy, int dx)
    {
        if (!MenuOpen) return;
        var (items, index) = _menuStack[^1];
        if (dy != 0)
        {
            int next = Math.Max(0, Math.Min(items.Count - 1, index + dy));
            _menuStack[^1] = (items, next);
            Say(DescribeMenuItem() + (next == index ? (dy > 0 ? T(", last", ", último") : T(", first", ", primero")) : ""), true);
            return;
        }

        MenuItem it = items[index];
        if (it.Children != null)
        {
            if (dx < 0) { Say(DescribeMenuItem(), true); return; }
            _menuLastIndex[items] = index;
            _menuStack.Add((it.Children, _menuLastIndex.TryGetValue(it.Children, out int i) ? i : 0));
            Say(T($"{it.Label}. {DescribeMenuItem()}", $"{it.Label}. {DescribeMenuItem()}"), true);
            return;
        }
        if (it.Toggle != null)
        {
            it.Toggle.Value = !it.Toggle.Value;
            Say(it.Toggle.Value ? T($"{it.Label}, on", $"{it.Label}, activado") : T($"{it.Label}, off", $"{it.Label}, desactivado"), true);
            return;
        }
        if (it.Range != null)
        {
            int v = Math.Max(it.Range.Min, Math.Min(it.Range.Max, it.Range.Value + dx * it.Step));
            bool edge = v == it.Range.Value;
            it.Range.Value = v;
            Say((it.Words ?? (n => n.ToString()))(v) + (edge ? (dx > 0 ? T(", highest", ", máximo") : T(", lowest", ", mínimo")) : ""), true);
            it.OnChange?.Invoke(v);
        }
    }

    private void MenuBack()
    {
        if (!MenuOpen) return;
        if (_menuStack.Count == 1) { CloseMenu(); return; }
        var (items, index) = _menuStack[^1];
        _menuLastIndex[items] = index;
        _menuStack.RemoveAt(_menuStack.Count - 1);
        Say(DescribeMenuItem(), true);
    }

    // A sample of the sound a volume controls, at the new volume.
    private void SampleSound(string kind)
    {
        if (_audio == null) return;
        float master = Settings.MasterVolume.Value / 100f;
        AudioEngine.Master = master;
        switch (kind)
        {
            case "enemy":
                _audio.Play("sample:enemy", () => Synth.Growl(ExileCore.Shared.Enums.MonsterRarity.White, 0f, 1f), Settings.EnemyVolume.Value / 100f);
                break;
            case "loot":
                _audio.Play("sample:loot", () => Synth.LootCue(Synth.Loot.Rare, 0f, 1f), Settings.LootVolume.Value / 100f * 1.4f);
                break;
            case "radar":
                _audio.Play("sample:radar", () => Synth.Tock(0f, 1f), Settings.RadarVolume.Value / 100f);
                break;
            case "guide":
                _audio.Play("sample:guide", () => Synth.Chime(0f, 1f), Settings.GuideVolume.Value / 100f);
                break;
        }
    }

    // Speech speed: Prism passes it to voices that take one (the Windows voice); NVDA and JAWS
    // keep the speed set in the screen reader.
    private int _appliedRate = -1;

    private void ApplySpeechRate(bool fromMenu)
    {
        int rate = Settings.SpeechRate.Value;
        if (rate == _appliedRate && !fromMenu) return;
        if (!fromMenu && _appliedRate < 0 && rate == 50) { _appliedRate = rate; return; }   // untouched: leave the voice as it is
        _appliedRate = rate;
        bool ok = _speech is PrismSpeech p && p.SetRate(rate / 100f);
        if (fromMenu && !ok)
            Say(_speech?.Name is string reader
                ? T($"Your screen reader keeps its own speed. Change it in {reader}'s settings", $"Tu lector de pantalla mantiene su propia velocidad. Cámbiala en los ajustes de {reader}")
                : T("Your screen reader keeps its own speed. Change it in the screen reader's settings", "Tu lector de pantalla mantiene su propia velocidad. Cámbiala en los ajustes del lector de pantalla"), false);
    }
}
