using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Room names chosen in the app. Quest's Space Setup never hands a room name to apps (MRUK has no such field), so
/// the app lets you pick one from a list; it is stored per room UUID next to the saved scans and used everywhere a
/// room name appears (plans, report, export folders).
/// </summary>
public static class RoomNames
{
    public static readonly string[] Presets =
    {
        "LIVING_ROOM", "BEDROOM", "KITCHEN", "BATHROOM", "TOILET", "HALLWAY", "OFFICE", "DINING_ROOM",
        "CHILDRENS_ROOM", "STORAGE", "UTILITY_ROOM", "GARAGE", "BALCONY", "TERRACE",
    };

    [Serializable]
    class Store
    {
        public List<string> keys = new List<string>();
        public List<string> values = new List<string>();
    }

    static Dictionary<string, string> map;
    static string path;

    /// <summary>Where the names are kept; defaults to room_names.json in the scan cache folder.</summary>
    public static string FilePath
    {
        get => path ?? Path.Combine(MRUKSceneCache.GetCacheRoot(), "room_names.json");
        set { path = value; map = null; }
    }

    static void EnsureLoaded()
    {
        if (map != null) return;
        map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(FilePath)) return;
            var store = JsonUtility.FromJson<Store>(File.ReadAllText(FilePath));
            for (int i = 0; i < store.keys.Count && i < store.values.Count; i++) map[store.keys[i]] = store.values[i];
        }
        catch { /* a damaged names file just means automatic names */ }
    }

    static void Save()
    {
        try
        {
            var store = new Store();
            foreach (var kv in map) { store.keys.Add(kv.Key); store.values.Add(kv.Value); }
            File.WriteAllText(FilePath, JsonUtility.ToJson(store, true));
        }
        catch (Exception ex) { Debug.LogWarning("[RoomNames] Could not save: " + ex.Message); }
    }

    public static bool TryGet(string roomKey, out string name)
    {
        name = null;
        if (string.IsNullOrEmpty(roomKey)) return false;
        EnsureLoaded();
        return map.TryGetValue(roomKey, out name);
    }

    /// <summary>Sets a custom name; null or empty goes back to the automatic name.</summary>
    public static void Set(string roomKey, string name)
    {
        if (string.IsNullOrEmpty(roomKey)) return;
        EnsureLoaded();
        if (string.IsNullOrEmpty(name)) map.Remove(roomKey); else map[roomKey] = name;
        Save();
    }

    /// <summary>Steps automatic -&gt; each preset -&gt; automatic. Returns the new custom name, or null for "automatic".</summary>
    public static string Cycle(string roomKey)
    {
        if (string.IsNullOrEmpty(roomKey)) return null;
        EnsureLoaded();
        string next;
        if (!map.TryGetValue(roomKey, out var current)) next = Presets[0];
        else
        {
            int i = Array.IndexOf(Presets, current);
            next = i >= 0 && i + 1 < Presets.Length ? Presets[i + 1] : null;
        }
        Set(roomKey, next);
        return next;
    }
}

