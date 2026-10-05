using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Sprocket.CustomBattles;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP.Game
{
    /// <summary>
    /// One shared lobby on the Custom Battle screen. The host's battle config is the truth:
    ///  * host -> clients: map + team rosters (and the blueprint files clients don't have yet);
    ///  * clients -> host: "add this vehicle to team N", "remove vehicle", "I pick vehicle X of team N";
    ///  * every player picks one vehicle in a roster; one vehicle instance can be picked by one player only.
    /// </summary>
    public static class LobbySync
    {
        sealed class UnitInfo { public string Name, Hash, File; public int Count; }
        sealed class TeamInfo { public int Flags; public List<UnitInfo> Units = new List<UnitInfo>(); }

        static BattleConfigModule s_module;
        static string s_lastKey = "";
        static float s_timer;
        static readonly Dictionary<int, HashSet<string>> s_sentHashes = new Dictionary<int, HashSet<string>>();
        static readonly Dictionary<string, (DateTime mtime, string hash)> s_hashCache = new Dictionary<string, (DateTime, string)>();

        // client: last state from the host
        static string s_pendingMap;
        static List<TeamInfo> s_pendingTeams;
        static string s_appliedKey = "";
        public static string HostMap { get; private set; }

        public static void SetModule(BattleConfigModule m) => s_module = m;

        static bool ModuleAlive
        {
            get
            {
                if (s_module != null && !s_module.WasCollected) return true;
                try { s_module = UnityEngine.Object.FindObjectOfType<TeamSelector>(); } catch { s_module = null; }
                return s_module != null;
            }
        }

        public static string CacheDir()
        {
            var dir = Path.Combine(BepInEx.Paths.CachePath, "SprocketMP", "blueprints");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string HashOf(byte[] data)
        {
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").Substring(0, 12);
        }

        static string HashOfFile(string path)
        {
            var mt = File.GetLastWriteTimeUtc(path);
            if (s_hashCache.TryGetValue(path, out var c) && c.mtime == mt) return c.hash;
            var h = HashOf(File.ReadAllBytes(path));
            s_hashCache[path] = (mt, h);
            return h;
        }

        static string CachedPath(string hash, string file)
        {
            var dir = Path.Combine(CacheDir(), hash);
            if (!Directory.Exists(dir)) return null;
            var f = Path.Combine(dir, file);
            if (File.Exists(f)) return f;
            return Directory.GetFiles(dir, "*.blueprint").FirstOrDefault();
        }

        static string Store(string hash, string file, byte[] data)
        {
            var dir = Path.Combine(CacheDir(), hash);
            Directory.CreateDirectory(dir);
            foreach (var c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
            if (string.IsNullOrEmpty(file)) file = "vehicle.blueprint";
            var f = Path.Combine(dir, file);
            if (!File.Exists(f)) File.WriteAllBytes(f, data);
            return f;
        }

        // ------------------------------------------------------------------ reading / writing the config
        static List<TeamInfo> ReadTeams(bool withHashes)
        {
            var list = new List<TeamInfo>();
            int n = s_module.TeamCount;
            for (int t = 0; t < n; t++)
            {
                var ti = new TeamInfo();
                try { ti.Flags = (int)s_module.GetTeamFlag(t); } catch { }
                var units = s_module.GetUnits(t);
                if (units != null)
                {
                    foreach (var kv in units)
                    {
                        var ud = kv.Key;
                        if (ud == null) continue;
                        var u = new UnitInfo { Name = ud.Name ?? "", Count = kv.Value?.Count ?? 1, File = Path.GetFileName(ud.Path ?? "") };
                        if (withHashes)
                        {
                            try { u.Hash = File.Exists(ud.Path) ? HashOfFile(ud.Path) : ""; } catch { u.Hash = ""; }
                        }
                        ti.Units.Add(u);
                    }
                }
                list.Add(ti);
            }
            return list;
        }

        static string Key(string map, List<TeamInfo> teams) =>
            map + "#" + string.Join("/", teams.Select(t => t.Flags + ":" + string.Join(",", t.Units.Select(u => $"{u.Name}*{u.Count}*{u.Hash}"))));

        /// <summary>Host: the icon image of a unit (IconPath may be the image itself or a folder of images).</summary>
        static (string file, bool isDir) ResolveIcon(string iconPath, string bpPath, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(iconPath)) return (null, false);
                if (File.Exists(iconPath)) return (iconPath, false);
                if (Directory.Exists(iconPath))
                {
                    var stem = Path.GetFileNameWithoutExtension(bpPath);
                    foreach (var cand in new[] { stem, name })
                    {
                        if (string.IsNullOrEmpty(cand)) continue;
                        var f = Directory.GetFiles(iconPath, cand + ".*").FirstOrDefault();
                        if (f != null) return (f, true);
                    }
                }
                Plugin.Log.LogInfo($"Icon not found: IconPath='{iconPath}' unit='{name}'");
            }
            catch { }
            return (null, false);
        }

        /// <summary>Client: icon path for a cached blueprint, in the same form (file / folder) as on the host.</summary>
        static string CachedIcon(string hash)
        {
            try
            {
                var mode = Path.Combine(CacheDir(), hash, "icon.mode");
                if (!File.Exists(mode)) return null;
                var parts = File.ReadAllText(mode).Split('|');
                var dir = Path.Combine(CacheDir(), hash, "icon");
                return parts[0] == "dir" ? dir + Path.DirectorySeparatorChar : Path.Combine(dir, parts[1]);
            }
            catch { return null; }
        }

        static UnitDefinition NewDefinition(string path, string name)
        {
            UnitDefinition ud = null;
            try
            {
                var card = Sprocket.Vehicles.Serialization.VehicleCard.LoadVehicleCard(path);
                if (card != null) ud = UnitSelectionLoader.NewUnit(card);
            }
            catch (Exception e) { Plugin.Log.LogWarning("NewUnit(" + path + "): " + e.Message); }
            if (ud == null) ud = new UnitDefinition();
            ud.Path = path;
            if (!string.IsNullOrEmpty(name)) ud.Name = name;
            ud.Valid = true;
            return ud;
        }

        static void MarkDirty()
        {
            try { s_module.context.dirtyFlags = BattleConfigDirtyFlags.Everything; } catch { }
            try { s_module.MarkConfigurationChanged(); } catch { }
        }

        // ------------------------------------------------------------------ host
        public static void HostTick()
        {
            if (!Session.IsHost || !ModuleAlive) return;
            s_timer += Time.unscaledDeltaTime;
            if (s_timer < 0.4f) return;
            s_timer = 0;
            string map = "";
            try { map = s_module.MapName ?? ""; } catch { }
            List<TeamInfo> teams;
            try { teams = ReadTeams(true); } catch (Exception e) { Plugin.Log.LogWarning("Lobby read: " + e.Message); return; }
            var key = Key(map, teams) + "|" + string.Join(",", Session.Players.Keys.OrderBy(x => x));
            ValidatePicks(teams);
            if (key == s_lastKey) return;
            s_lastKey = key;
            if (Session.RemoteCount == 0) return;

            // blueprints the clients don't have yet
            var paths = new Dictionary<string, string>(); // hash -> path
            var icons = new Dictionary<string, (string iconPath, string name)>();
            for (int t = 0; t < teams.Count; t++)
            {
                var units = s_module.GetUnits(t);
                if (units == null) continue;
                foreach (var kv in units)
                {
                    var p = kv.Key?.Path;
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                    try { var h = HashOfFile(p); paths[h] = p; icons[h] = (kv.Key.IconPath, kv.Key.Name); } catch { }
                }
            }
            foreach (var pl in Session.Players.Values.Where(p => p.Id != 0).ToList())
            {
                if (!s_sentHashes.TryGetValue(pl.Id, out var sent)) s_sentHashes[pl.Id] = sent = new HashSet<string>();
                foreach (var kv in paths)
                {
                    if (sent.Contains(kv.Key)) continue;
                    try
                    {
                        var data = File.ReadAllBytes(kv.Value);
                        var w0 = new Writer(Msg.Blueprint).Str(kv.Key).Str(Path.GetFileName(kv.Value)).Bytes(data);
                        icons.TryGetValue(kv.Key, out var ic);
                        var (iconFile, isDir) = ResolveIcon(ic.iconPath, kv.Value, ic.name);
                        byte[] iconData = Array.Empty<byte>();
                        try { if (iconFile != null) iconData = File.ReadAllBytes(iconFile); } catch { }
                        w0.Str(iconFile != null ? Path.GetFileName(iconFile) : "").Bool(isDir).Bytes(iconData);
                        Session.SendLarge(w0.ToArray(), pl.Id);
                        sent.Add(kv.Key);
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Send blueprint: " + e.Message); }
                }
            }
            foreach (var id in s_sentHashes.Keys.Where(id => !Session.Players.ContainsKey(id)).ToList()) s_sentHashes.Remove(id);

            var w = new Writer(Msg.Lobby).Str(map).Int(teams.Count);
            foreach (var t in teams)
            {
                w.Int(t.Flags).Int(t.Units.Count);
                foreach (var u in t.Units) w.Str(u.Name).Str(u.Hash ?? "").Str(u.File ?? "").Int(u.Count);
            }
            Session.SendLarge(w.ToArray(), -1);
        }

        /// <summary>Drop picks of vehicles that are no longer in the roster (or over-picked).</summary>
        static void ValidatePicks(List<TeamInfo> teams)
        {
            bool changed = false;
            foreach (var p in Session.Players.Values.OrderBy(p => p.Id))
            {
                if (p.PickTeam < 0) continue;
                bool ok = p.PickTeam < teams.Count && CanPick(teams[p.PickTeam], p.PickTeam, p.Tank, p.Id);
                if (!ok) { p.Tank = ""; p.PickTeam = -1; changed = true; }
            }
            if (changed) Session.BroadcastRoster();
        }

        static bool CanPick(TeamInfo team, int teamIdx, string name, int playerId)
        {
            int count = team.Units.Where(u => u.Name == name).Sum(u => u.Count);
            int others = Session.Players.Values.Count(o => o.Id != playerId && o.PickTeam == teamIdx && o.Tank == name &&
                                                         o.Id < playerId); // earlier players keep their pick
            return count > 0 && others < count;
        }

        static bool TryPick(int playerId, int team, string name)
        {
            if (!Session.Players.TryGetValue(playerId, out var p)) return false;
            if (string.IsNullOrEmpty(name) || team < 0)
            {
                p.Tank = ""; p.PickTeam = -1;
                Session.BroadcastRoster();
                return true;
            }
            if (!ModuleAlive) return false;
            var teams = ReadTeams(false);
            if (team >= teams.Count) return false;
            int count = teams[team].Units.Where(u => u.Name == name).Sum(u => u.Count);
            int taken = Session.Players.Values.Count(o => o.Id != playerId && o.PickTeam == team && o.Tank == name);
            if (count <= taken)
            {
                Plugin.Log.LogInfo($"Pick refused: player {playerId} '{name}' team {team} count={count} taken={taken}");
                if (playerId == Session.LocalId) Session.AddChat("* Этот танк уже занят");
                else Session.SendToPlayer(playerId, new Writer(Msg.Chat).Str("* Этот танк уже занят"), true);
                return false;
            }
            p.Tank = name; p.PickTeam = team; p.Team = team;
            Plugin.Log.LogInfo($"Pick: player {playerId} -> '{name}' team {team}");   // picking a vehicle also puts you into its team
            Session.BroadcastRoster();
            return true;
        }

        public static void OnMessage(int from, Reader r)
        {
            switch (r.Type)
            {
                case Msg.Pick:
                    if (Session.IsHost) TryPick(from, r.Int(), r.Str());
                    break;
                case Msg.LobbyAdd:
                    if (Session.IsHost && ModuleAlive)
                    {
                        int team = r.Int(); string name = r.Str(); string file = r.Str(); var data = r.Bytes();
                        if (data.Length == 0) break;
                        var path = Store(HashOf(data), file, data);
                        HostAdd(team, path, name);
                    }
                    break;
                case Msg.LobbyRemove:
                    if (Session.IsHost && ModuleAlive) HostRemove(r.Int(), r.Str());
                    break;
                case Msg.Blueprint:
                    if (Session.IsClient)
                    {
                        var h = r.Str(); var f = r.Str(); Store(h, f, r.Bytes());
                        var iname = r.Str(); bool isDir = r.Bool(); var idata = r.Bytes();
                        if (iname.Length > 0 && idata.Length > 0)
                        {
                            var dir = Path.Combine(CacheDir(), h, "icon");
                            Directory.CreateDirectory(dir);
                            File.WriteAllBytes(Path.Combine(dir, iname), idata);
                            File.WriteAllText(Path.Combine(CacheDir(), h, "icon.mode"), (isDir ? "dir|" : "file|") + iname);
                        }
                    }
                    break;
                case Msg.Lobby:
                    if (Session.IsClient)
                    {
                        s_pendingMap = r.Str();
                        int nt = r.Int();
                        var teams = new List<TeamInfo>();
                        for (int t = 0; t < nt; t++)
                        {
                            var ti = new TeamInfo { Flags = r.Int() };
                            int nu = r.Int();
                            for (int i = 0; i < nu; i++) ti.Units.Add(new UnitInfo { Name = r.Str(), Hash = r.Str(), File = r.Str(), Count = r.Int() });
                            teams.Add(ti);
                        }
                        s_pendingTeams = teams;
                        HostMap = s_pendingMap;
                        s_appliedKey = "";
                    }
                    break;
            }
        }

        static void HostAdd(int team, string path, string name)
        {
            if (team < 0 || team >= s_module.TeamCount) return;
            var units = s_module.GetUnits(team) ?? new Il2CppSystem.Collections.Generic.Dictionary<UnitDefinition, UnitInstanceInfo>();
            var full = SafeFull(path);
            foreach (var kv in units)
            {
                if (kv.Key != null && kv.Key.Name == name && kv.Value != null)
                {
                    kv.Value.Count = kv.Value.Count + 1;
                    s_module.SetUnits(team, units);
                    MarkDirty();
                    return;
                }
            }
            var ud = NewDefinition(full, name);
            var info = new UnitInstanceInfo { Count = 1 };
            units.Add(ud, info);
            s_module.SetUnits(team, units);
            MarkDirty();
        }

        static void HostRemove(int team, string name)
        {
            if (team < 0 || team >= s_module.TeamCount) return;
            var units = s_module.GetUnits(team);
            if (units == null) return;
            UnitDefinition key = null;
            foreach (var kv in units) if (kv.Key != null && kv.Key.Name == name) { key = kv.Key; break; }
            if (key == null) return;
            var info = units[key];
            if (info != null && info.Count > 1) info.Count = info.Count - 1;
            else units.Remove(key);
            s_module.SetUnits(team, units);
            MarkDirty();
        }

        static string SafeFull(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

        // ------------------------------------------------------------------ client
        public static void ClientTick()
        {
            if (!Session.IsClient || !ModuleAlive || s_pendingTeams == null) return;
            var teams = s_pendingTeams;
            var key = Key(s_pendingMap ?? "", teams);
            if (key == s_appliedKey) return;
            // all blueprint files present?
            foreach (var t in teams)
                foreach (var u in t.Units)
                    if (!string.IsNullOrEmpty(u.Hash) && CachedPath(u.Hash, u.File) == null) return;
            s_appliedKey = key;
            try
            {
                if (!string.IsNullOrEmpty(s_pendingMap)) { try { if (s_module.MapName != s_pendingMap) s_module.SetMap(s_pendingMap); } catch { } }
                int n = Math.Min(teams.Count, s_module.TeamCount);
                for (int t = 0; t < n; t++)
                {
                    var dict = new Il2CppSystem.Collections.Generic.Dictionary<UnitDefinition, UnitInstanceInfo>();
                    foreach (var u in teams[t].Units)
                    {
                        var path = string.IsNullOrEmpty(u.Hash) ? null : CachedPath(u.Hash, u.File);
                        if (path == null) continue;
                        var ud = NewDefinition(path, u.Name);
                        var icon = CachedIcon(u.Hash);
                        if (icon != null) ud.IconPath = icon;
                        dict.Add(ud, new UnitInstanceInfo { Count = u.Count });
                    }
                    s_module.SetUnits(t, dict);
                    try { s_module.SetTeamFlag(t, (TeamDefinitionFlags)(byte)teams[t].Flags); } catch { }
                }
                MarkDirty();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Lobby apply: " + e); }
        }

        /// <summary>Client clicked a vehicle in the list: ask the host to add it to the team being viewed.</summary>
        public static void RequestAdd(UnitDefinition ud)
        {
            if (ud == null || string.IsNullOrEmpty(ud.Path) || !ModuleAlive) return;
            byte[] data;
            try { data = File.ReadAllBytes(ud.Path); } catch (Exception e) { Session.AddChat("* Не удалось прочитать чертёж: " + e.Message); return; }
            Session.SendLarge(new Writer(Msg.LobbyAdd).Int(s_module.ActiveTeamIndex).Str(ud.Name ?? "").Str(Path.GetFileName(ud.Path)).Bytes(data).ToArray(), -1);
        }

        public static void RequestRemove(UnitDefinition ud)
        {
            if (ud == null || !ModuleAlive) return;
            Session.Send(new Writer(Msg.LobbyRemove).Int(s_module.ActiveTeamIndex).Str(ud.Name ?? ""), true);
        }

        /// <summary>Pick (or unpick, ud == null) a vehicle of the team being viewed.</summary>
        public static void Pick(UnitDefinition ud)
        {
            int team = ud == null || !ModuleAlive ? -1 : s_module.ActiveTeamIndex;
            string name = ud?.Name ?? "";
            if (Session.IsHost) TryPick(Session.LocalId, team, name);
            else Session.Send(new Writer(Msg.Pick).Int(team).Str(name), true);
        }

        public static int ActiveTeam => ModuleAlive ? s_module.ActiveTeamIndex : -1;

        public static void Reset()
        {
            s_lastKey = ""; s_appliedKey = ""; s_pendingTeams = null; s_sentHashes.Clear(); HostMap = null;
        }
    }
}
