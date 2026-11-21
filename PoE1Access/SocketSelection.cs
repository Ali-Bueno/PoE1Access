using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using SocketsComp = ExileCore.PoEMemory.Components.Sockets;
using static PoE1Access.Loc;

namespace PoE1Access;

// The game's socket mode (B on an item, then Sockets): the selection moves between the item's
// sockets and nothing else on screen says which one. Each socket is a child of the item
// element's first child, and its state word (+0x2D0, +8 on this game version like the other UI
// fields) has 0x10000 set on the selected socket and 0x100 on one holding a gem: 0x10101 a
// selected socket with a gem, 0x10001 a selected empty one, 0x1 the others (found 21 Nov 2026
// on a three-socket sword with Cleave in the third). Said as "Socket 3 of 3, white, Cleave
// level 3".
public partial class PoE1AccessCore
{
    private Element _tipElement;
    private long _socketItemAddr, _nextSocketPollMs;
    private int _socketSelected = -1, _socketPending = -1;
    private long _socketPendingMs;
    private bool _socketItemChanged;
    private const int SocketStateBase = 0x2D0, SocketSelectedBit = 0x10000;

    private void PollSocketSelection(long now)
    {
        if (now < _nextSocketPollMs) return;
        _nextSocketPollMs = now + 100;
        bool inventory = _openPanels != null && _openPanels.Any(t => IsGameWord(t, InventoryTitle));
        Element itemElement = _tipElement;
        if (!inventory || itemElement == null || itemElement.Address == 0 || _tipItem == null) { _socketSelected = -1; return; }
        if (itemElement.Address != _socketItemAddr) { _socketItemAddr = itemElement.Address; _socketSelected = -1; _socketItemChanged = true; }

        var mem = Mem;
        Element holder = Try<Element>(() => itemElement.GetChildAtIndex(0), null);
        var sockets = holder != null && holder.Address != 0 ? Try<IList<Element>>(() => holder.Children, null) : null;
        if (sockets == null || sockets.Count == 0 || sockets.Count > 6) { _socketSelected = -1; return; }
        int selected = -1;
        for (int i = 0; i < sockets.Count; i++)
        {
            int state = Try(() => mem.Read<int>(sockets[i].Address + SocketStateBase + UiText.Shift), 0);
            if ((state & SocketSelectedBit) != 0) { selected = i; break; }
        }
        // The selection passes over sockets as the cursor moves (with a gem in hand, onto an
        // item and across its sockets): a socket is said once it has stayed selected a moment.
        if (selected != _socketPending) { _socketPending = selected; _socketPendingMs = now; return; }
        // On an item just reached, its description is said first (a moment after the cursor
        // lands); its socket follows it.
        if (selected == _socketSelected || now - _socketPendingMs < (_socketItemChanged ? 600 : 250)) return;
        _socketSelected = selected;
        if (selected < 0) return;   // the socket mode closed
        bool newItem = _socketItemChanged;
        _socketItemChanged = false;

        // The item's sockets in order, read now (the item may have changed since it was said).
        var comp = GetComp<SocketsComp>(_tipItem);
        if (comp == null) return;
        SocketLines(comp);
        int n = _socketTexts.Count, k = selected + 1;
        if (selected >= n) return;
        var (colour, gem, linked) = _socketTexts[selected];
        string where = T($"Socket {k} of {n}", $"Engarce {k} de {n}");
        PluginLog.Write("Socket selected: " + k + " of " + n);
        // After what was just said (the action menu, or the item the cursor came onto); moving
        // between one item's sockets, at once.
        Say(where + ", " + colour + ", " + gem + (linked ? T(", linked", ", enlazado") : ""), !newItem && now - _pad.FacePressedMs > 1500);
    }
}
