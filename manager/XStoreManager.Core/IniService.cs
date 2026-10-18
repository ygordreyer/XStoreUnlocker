using System.Text;
using System.Text.RegularExpressions;

namespace XStoreManager.Core;

public static class IniService
{
    public static bool IsStoreId(string id) => Regex.IsMatch(id, "^[A-Za-z0-9]{12}$", RegexOptions.CultureInvariant);
    public static string Serialize(GameProfile profile)
    {
        var text = new StringBuilder($"[Settings]\r\nenabled={(profile.Enabled ? 1 : 0)}\r\nunlock_all={(profile.UnlockAll ? 1 : 0)}\r\nlog_enabled={(profile.EnableLogging ? 1 : 0)}\r\n\r\n[DLCs]\r\n");
        foreach (var dlc in profile.Dlcs) if (!IsStoreId(dlc.StoreId.Trim())) throw new InvalidDataException($"Invalid Store ID: {dlc.StoreId}");
        var groups = profile.Dlcs.GroupBy(d => d.StoreId.Trim().ToUpperInvariant()).ToArray();
        foreach (var group in groups.Where(g => g.Any(d => d.Selected) && !g.Any(d => d.Blocked))) text.AppendLine(group.Key + "=1");
        text.AppendLine("\r\n[Blacklist]");
        foreach (var group in groups.Where(g => g.Any(d => d.Blocked))) text.AppendLine(group.Key + "=1");
        text.AppendLine("\r\n[MountPaths]");
        foreach (var group in groups.Where(g => g.Any(d => d.Selected) && !g.Any(d => d.Blocked)))
        {
            var paths = group.Where(d => !string.IsNullOrWhiteSpace(d.MountPath)).Select(d => d.MountPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 1) throw new InvalidDataException("Conflicting mount paths for Store ID " + group.Key);
            if (paths.Length == 0) continue;
            if (paths[0].IndexOfAny(['\r','\n']) >= 0) throw new InvalidDataException("Mount paths cannot contain newlines."); text.AppendLine(group.Key + "=" + paths[0]);
        }
        return text.ToString();
    }
    public static void Import(string text, GameProfile profile)
    {
        var entries = new Dictionary<(string Section, string Key), string>(); string section = "";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim(); if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].ToLowerInvariant(); continue; }
            int eq = line.IndexOf('='); if (eq < 1) continue;
            entries[(section, line[..eq].Trim().ToUpperInvariant())] = line[(eq + 1)..].Trim();
        }
        bool Flag(string sectionName, string key, bool fallback) => entries.TryGetValue((sectionName, key), out string? v) ? v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;
        profile.Enabled = Flag("settings", "ENABLED", false);
        profile.UnlockAll = Flag("settings", "UNLOCK_ALL", false);
        profile.EnableLogging = Flag("settings", "LOG_ENABLED", true);
        foreach (var dlc in profile.Dlcs) { dlc.Selected = false; dlc.Blocked = false; dlc.MountPath = ""; }
        foreach (var entry in entries.Where(e => e.Key.Section is "dlcs" or "blacklist" or "mountpaths"))
        {
            if (!IsStoreId(entry.Key.Key)) throw new InvalidDataException("Invalid Store ID in INI: " + entry.Key.Key);
            var dlc = profile.Dlcs.FirstOrDefault(d => d.StoreId.Equals(entry.Key.Key, StringComparison.OrdinalIgnoreCase));
            if (dlc is null) { dlc = new() { StoreId = entry.Key.Key, Name = entry.Key.Key }; profile.Dlcs.Add(dlc); }
            if (entry.Key.Section == "dlcs") dlc.Selected = Flag("dlcs", entry.Key.Key, false);
            if (entry.Key.Section == "blacklist") dlc.Blocked = Flag("blacklist", entry.Key.Key, false);
            if (entry.Key.Section == "mountpaths") dlc.MountPath = entry.Value;
        }
    }
}
