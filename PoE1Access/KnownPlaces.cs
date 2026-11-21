using System;
using System.Collections.Generic;
using System.IO;

namespace PoE1Access;

/// <summary>
/// Exits and quest things you have seen, on any character: once one has been seen, the scanner
/// lists it from then on in that area, on every character, before you have seen it again. Each
/// visit to an area lays it out anew, so what is kept is that the area has it (its id and the
/// thing's name: "1_1_2|exit|The Mud Flats", "1_1_3|quest|Rhoa Nest"); where it stands is taken
/// from the game each time. Kept in app\known-places.txt, which updates leave alone.
/// </summary>
internal sealed class KnownPlaces
{
    private readonly HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly string _file;

    public KnownPlaces()
    {
        try
        {
            _file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "known-places.txt"));
            if (File.Exists(_file))
                foreach (string line in File.ReadAllLines(_file))
                    if (line.Length > 0) _known.Add(line.Trim());
        }
        catch (Exception ex) { PluginLog.Write("Known places not read: " + ex.Message); }
    }

    private static string Key(string area, string kind, string name) => area + "|" + kind + "|" + name;

    public bool Contains(string area, string kind, string name) =>
        !string.IsNullOrEmpty(area) && !string.IsNullOrEmpty(name) && _known.Contains(Key(area, kind, name));

    /// <summary>Notes a thing as seen; written to the file the first time.</summary>
    public void Add(string area, string kind, string name)
    {
        if (string.IsNullOrEmpty(area) || string.IsNullOrEmpty(name)) return;
        string key = Key(area, kind, name);
        if (!_known.Add(key)) return;
        try { if (_file != null) File.AppendAllText(_file, key + Environment.NewLine); }
        catch { }
    }
}
