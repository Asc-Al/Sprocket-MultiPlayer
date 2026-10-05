using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.CustomBattles;
using Sprocket.SceneManagement;
using SprocketMP.Net;

namespace SprocketMP.Game
{
    /// <summary>Description of a battle as it travels over the network.</summary>
    public sealed class BattleDesc
    {
        public string Scene;
        public string MapName;
        public sbyte Mode;
        public float TimeCycle, CloudR, CloudG, CloudB, CloudA, Fog;
        public float CloudMapX, CloudMapY, CloudMapZ, CloudMapW;
        public List<TeamDesc> Teams = new List<TeamDesc>();
        public List<byte[]> Blueprints = new List<byte[]>();
        public List<string> BlueprintNames = new List<string>();
        public Dictionary<int, int> PlayerTeams = new Dictionary<int, int>(); // player id -> team index
        public int HostTeam;
    }

    public sealed class TeamDesc
    {
        public string Name;
        public byte Flags;
        public int MaxUnits;
        public float Wear, Dirt;
        public string PaintScheme;
        public int PaintBlueprint = -1;
        public List<UnitDesc> Units = new List<UnitDesc>();
    }

    public sealed class UnitDesc
    {
        public string Name;
        public int Blueprint;   // index into BattleDesc.Blueprints
        public int Count;
        public bool PlayerTank;  // a client's own vehicle (not serialized; host-side bookkeeping)
    }

    /// <summary>
    /// Host: captures the custom battle that is being launched and sends it (with all blueprint files) to clients.
    /// Client: rebuilds the same BattleConfig with its own team marked as "Player" and loads the same scene.
    /// </summary>
    public static class BattleSetup
    {
        public static string PendingInfo;
        public static BattleDesc Current;
        static BattleDesc s_toLaunch;
        static float s_launchDelay;
        public static bool LaunchedByNetwork;

        const byte FlagPlayer = 1;   // TeamDefinitionFlags.Player (resolved at runtime below)

        static byte PlayerFlag
        {
            get
            {
                try { return (byte)TeamDefinitionFlags.Player; } catch { return FlagPlayer; }
            }
        }

        // ------------------------------------------------------------------------------------------------ host
        public static void OnGameControllerInitiate(Sprocket.GameControl.GameController gc, Il2CppReferenceArray<Il2CppSystem.Object> arguments)
        {
            string scene = "";
            try { scene = gc.gameObject.scene.name; } catch { }
            GameSetupContext setup = null;
            if (arguments != null)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    var a = arguments[i];
                    string tn = a == null ? "null" : a.GetIl2CppType().FullName;
                    Plugin.Log.LogInfo($"GameController.Initiate arg[{i}] = {tn}");
                    setup ??= a?.TryCast<GameSetupContext>();
                }
            }
            Plugin.Log.LogInfo($"Battle scene '{scene}' starting, mode={(setup != null ? setup.Mode.ToString() : "?")}, networkLaunch={LaunchedByNetwork}");

            if (Session.IsClient)
            {
                if (!LaunchedByNetwork) Plugin.Log.LogWarning("Клиент запустил бой сам — синхронизации не будет. Жди старта от хоста.");
                BattleSync.PrepareBattle(Current, LaunchedByNetwork);
                LaunchedByNetwork = false;
                return;
            }

            if (!Session.IsHost) return;
            AutoReconnect.PendingResync = false;   // a fresh battle is being sent to everyone anyway
            if (setup == null || setup.SetupConfig == null)
            {
                Plugin.Log.LogInfo("No GameSetupContext — not a custom battle, not synchronising.");
                return;
            }
            try
            {
                var desc = Capture(scene, setup);
                if (AddPlayerTanks(desc))
                {
                    // the host's own game must spawn the clients' tanks too: launch from a rebuilt config
                    var paths = WriteBlueprints(desc);
                    var cfg = BuildConfig(desc, paths, desc.HostTeam);
                    setup.SetupConfig = new IBattleConfig(cfg.Pointer);
                }
                Current = desc;
                if (Session.RemoteCount > 0)
                {
                    var payload = Serialize(desc);
                    Session.BroadcastLarge(payload);
                    Plugin.Log.LogInfo($"Battle '{desc.MapName}' sent ({desc.Blueprints.Count} blueprints)");
                }
                BattleSync.PrepareBattle(desc, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Failed to capture battle setup: " + e);
                Session.AddChat("* Ошибка подготовки боя: " + e.Message);
            }
        }

        static BattleDesc Capture(string scene, GameSetupContext setup)
        {
            var d = new BattleDesc { Scene = scene, Mode = (sbyte)setup.Mode };
            var icfg = setup.SetupConfig;
            d.MapName = icfg.MapName;
            if (string.IsNullOrEmpty(d.Scene)) d.Scene = d.MapName;
            var env = icfg.Environment;
            if (env != null)
            {
                d.TimeCycle = env.TimeCycleFraction; d.Fog = env.FogDistance;
                d.CloudR = env.CloudMapR; d.CloudG = env.CloudMapG; d.CloudB = env.CloudMapB; d.CloudA = env.CloudMapA;
                var cm = env.CloudChannelMapping; d.CloudMapX = cm.x; d.CloudMapY = cm.y; d.CloudMapZ = cm.z; d.CloudMapW = cm.w;
            }

            var cfg = icfg.TryCast<BattleConfig>();
            if (cfg == null) throw new Exception("SetupConfig is " + new Il2CppSystem.Object(icfg.Pointer).GetIl2CppType().FullName + ", not BattleConfig");
            var bpIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int BlueprintFor(string path)
            {
                if (string.IsNullOrEmpty(path)) return -1;
                if (bpIndex.TryGetValue(path, out var idx)) return idx;
                if (!File.Exists(path)) { Plugin.Log.LogWarning("Blueprint not found on disk: " + path); return -1; }
                idx = d.Blueprints.Count;
                d.Blueprints.Add(File.ReadAllBytes(path));
                d.BlueprintNames.Add(Path.GetFileName(path));
                bpIndex[path] = idx;
                return idx;
            }

            var teams = cfg.Teams;
            for (int t = 0; t < teams.Length; t++)
            {
                var td = teams[t];
                var tdesc = new TeamDesc
                {
                    Name = td.Name,
                    Flags = (byte)td.Flags,
                    MaxUnits = td.MaxUnits,
                    Wear = td.UnitWearLevel,
                    Dirt = td.UnitDirtLevel,
                    PaintScheme = td.PaintSchemePath ?? "",
                };
                tdesc.PaintBlueprint = BlueprintFor(tdesc.PaintScheme);
                if ((tdesc.Flags & PlayerFlag) != 0) d.HostTeam = t;
                var units = td.Units;
                if (units != null)
                {
                    foreach (var kv in units)
                    {
                        var ud = kv.Key;
                        var u = new UnitDesc { Name = ud.Name, Count = kv.Value?.Count ?? 1, Blueprint = BlueprintFor(ud.Path) };
                        Plugin.Log.LogInfo($"  team {t} unit '{u.Name}' x{u.Count} path={ud.Path}");
                        if (u.Blueprint < 0) throw new Exception("Нет файла чертежа: " + ud.Path);
                        tdesc.Units.Add(u);
                    }
                }
                d.Teams.Add(tdesc);
            }

            // Team assignment of human players
            foreach (var p in Session.Players.Values)
            {
                int team = p.Id == 0 ? d.HostTeam : p.Team;
                if (team < 0 || team >= d.Teams.Count) team = AutoTeam(d);
                d.PlayerTeams[p.Id] = team;
            }
            return d;
        }

        /// <summary>
        /// Host: every player who picked a vehicle in the lobby gets one instance of it renamed to "<tank> [<player>]",
        /// which is how all machines know who drives it.
        /// </summary>
        static bool AddPlayerTanks(BattleDesc d)
        {
            bool any = false;
            foreach (var p in Session.Players.Values.OrderBy(p => p.Id))
            {
                if (p.PickTeam < 0 || p.PickTeam >= d.Teams.Count || string.IsNullOrEmpty(p.Tank)) continue;
                var td = d.Teams[p.PickTeam];
                var src = td.Units.FirstOrDefault(u => !u.PlayerTank && u.Count > 0 && u.Name == p.Tank);
                if (src == null || src.Blueprint < 0) { Plugin.Log.LogWarning($"Pick of {p.Name} ('{p.Tank}') not found in team {p.PickTeam}"); continue; }
                src.Count--;
                if (src.Count <= 0) td.Units.Remove(src);
                string uname = PlayerTanks.VehicleName(src.Name, p);
                int idx = d.Blueprints.Count;
                d.Blueprints.Add(PlayerTanks.Rename(d.Blueprints[src.Blueprint], uname));
                d.BlueprintNames.Add(SafeName(uname) + ".blueprint");
                td.Units.Add(new UnitDesc { Name = uname, Blueprint = idx, Count = 1, PlayerTank = true });
                d.PlayerTeams[p.Id] = p.PickTeam;
                if (p.Id == 0) d.HostTeam = p.PickTeam;
                Plugin.Log.LogInfo($"  player {p.Id} '{p.Name}' drives '{uname}' (team {p.PickTeam})");
                any = true;
            }
            return any;
        }

        static int AutoTeam(BattleDesc d)
        {
            // Opposite of the host, first team that actually has units
            for (int i = 0; i < d.Teams.Count; i++)
            {
                int t = (d.HostTeam + 1 + i) % d.Teams.Count;
                if (t != d.HostTeam && d.Teams[t].Units.Count > 0) return t;
            }
            return d.HostTeam;
        }

        static byte[] Serialize(BattleDesc d)
        {
            var w = new Writer(Msg.BattleStart);
            w.Str(d.Scene).Str(d.MapName).Byte((byte)d.Mode);
            w.Float(d.TimeCycle).Float(d.CloudR).Float(d.CloudG).Float(d.CloudB).Float(d.CloudA).Float(d.Fog);
            w.Float(d.CloudMapX).Float(d.CloudMapY).Float(d.CloudMapZ).Float(d.CloudMapW);
            w.Int(d.HostTeam);
            w.Int(d.Blueprints.Count);
            for (int i = 0; i < d.Blueprints.Count; i++) w.Str(d.BlueprintNames[i]).Bytes(d.Blueprints[i]);
            w.Int(d.Teams.Count);
            foreach (var t in d.Teams)
            {
                w.Str(t.Name).Byte(t.Flags).Int(t.MaxUnits).Float(t.Wear).Float(t.Dirt).Str(t.PaintScheme).Int(t.PaintBlueprint);
                w.Int(t.Units.Count);
                foreach (var u in t.Units) w.Str(u.Name).Int(u.Blueprint).Int(u.Count);
            }
            w.Int(d.PlayerTeams.Count);
            foreach (var kv in d.PlayerTeams) w.Int(kv.Key).Int(kv.Value);
            return w.ToArray();
        }

        static BattleDesc Deserialize(Reader r)
        {
            var d = new BattleDesc { Scene = r.Str(), MapName = r.Str(), Mode = (sbyte)r.Byte() };
            d.TimeCycle = r.Float(); d.CloudR = r.Float(); d.CloudG = r.Float(); d.CloudB = r.Float(); d.CloudA = r.Float(); d.Fog = r.Float();
            d.CloudMapX = r.Float(); d.CloudMapY = r.Float(); d.CloudMapZ = r.Float(); d.CloudMapW = r.Float();
            d.HostTeam = r.Int();
            int nb = r.Int();
            for (int i = 0; i < nb; i++) { d.BlueprintNames.Add(r.Str()); d.Blueprints.Add(r.Bytes()); }
            int nt = r.Int();
            for (int i = 0; i < nt; i++)
            {
                var t = new TeamDesc { Name = r.Str(), Flags = r.Byte(), MaxUnits = r.Int(), Wear = r.Float(), Dirt = r.Float(), PaintScheme = r.Str(), PaintBlueprint = r.Int() };
                int nu = r.Int();
                for (int j = 0; j < nu; j++) t.Units.Add(new UnitDesc { Name = r.Str(), Blueprint = r.Int(), Count = r.Int() });
                d.Teams.Add(t);
            }
            int np = r.Int();
            for (int i = 0; i < np; i++) d.PlayerTeams[r.Int()] = r.Int();
            return d;
        }

        // ------------------------------------------------------------------------------------------------ client
        public static void OnBattleStartMessage(Reader r)
        {
            var d = Deserialize(r);
            Current = d;
            int myTeam = d.PlayerTeams.TryGetValue(Session.LocalId, out var t) ? t : -1;
            PendingInfo = $"Хост запускает бой: {d.MapName}. Твоя команда: {(myTeam >= 0 ? (myTeam + 1).ToString() : "наблюдатель")}";
            Plugin.Log.LogInfo(PendingInfo);
            s_toLaunch = d;
            s_launchDelay = 0.3f;
        }

        public static void Update()
        {
            if (s_toLaunch == null) return;
            s_launchDelay -= UnityEngine.Time.unscaledDeltaTime;
            if (s_launchDelay > 0) return;
            var d = s_toLaunch;
            s_toLaunch = null;
            try { Launch(d); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Launch failed: " + e);
                Session.AddChat("* Не удалось запустить бой: " + e.Message);
                PendingInfo = null;
            }
        }

        static string CacheDir()
        {
            var dir = Path.Combine(BepInEx.Paths.CachePath, "SprocketMP", "blueprints");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Launch(BattleDesc d)
        {
            var paths = WriteBlueprints(d);
            int myTeam = d.PlayerTeams.TryGetValue(Session.LocalId, out var mt) ? mt : -1;
            var cfg = BuildConfig(d, paths, myTeam);

            var setup = new GameSetupContext();
            setup.Mode = (SupportedGameModes)d.Mode;
            setup.SetupConfig = new IBattleConfig(cfg.Pointer);

            var args = new Il2CppReferenceArray<Il2CppSystem.Object>(1);
            args[0] = setup;

            var sm = ISceneManager.Instance;
            if (sm == null) throw new Exception("ISceneManager.Instance == null");
            LaunchedByNetwork = true;
            Plugin.Log.LogInfo($"Client launching scene '{d.Scene}' (map {d.MapName}), my team {myTeam}");
            sm.Load(d.Scene, args, SceneLoadOptions.Animations, new Il2CppSystem.Threading.CancellationToken());
            PendingInfo = null;
        }

        /// <summary>Writes the battle's blueprints to the local cache, returns their paths.</summary>
        static string[] WriteBlueprints(BattleDesc d)
        {
            var paths = new string[d.Blueprints.Count];
            using (var sha = SHA1.Create())
            {
                for (int i = 0; i < d.Blueprints.Count; i++)
                {
                    var hash = BitConverter.ToString(sha.ComputeHash(d.Blueprints[i])).Replace("-", "").Substring(0, 12);
                    var dir = Path.Combine(CacheDir(), hash);
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, SafeName(d.BlueprintNames[i]));
                    if (!File.Exists(file)) File.WriteAllBytes(file, d.Blueprints[i]);
                    paths[i] = file;
                }
            }
            return paths;
        }

        /// <summary>Builds a BattleConfig from the description with <paramref name="myTeam"/> as the "Player" team.</summary>
        static BattleConfig BuildConfig(BattleDesc d, string[] paths, int myTeam)
        {
            byte playerFlag = PlayerFlag;

            // 2. rebuild BattleConfig
            var cfg = new BattleConfig();
            cfg.MapName = d.MapName;
            var env = new EnvironmentConfig();
            env.TimeCycleFraction = d.TimeCycle; env.FogDistance = d.Fog;
            env.CloudMapR = d.CloudR; env.CloudMapG = d.CloudG; env.CloudMapB = d.CloudB; env.CloudMapA = d.CloudA;
            env.CloudChannelMapping = new UnityEngine.Vector4(d.CloudMapX, d.CloudMapY, d.CloudMapZ, d.CloudMapW);
            cfg.Environment = env;

            var teams = new Il2CppReferenceArray<TeamDefinition>(d.Teams.Count);
            for (int t = 0; t < d.Teams.Count; t++)
            {
                var src = d.Teams[t];
                var td = new TeamDefinition();
                td.Name = src.Name;
                byte flags = (byte)(src.Flags & ~playerFlag);
                // our own team is the "Player" team; if we are a spectator keep the host's team as player team
                if (t == myTeam || (myTeam < 0 && t == d.HostTeam)) flags |= playerFlag;
                td.Flags = (TeamDefinitionFlags)flags;
                td.MaxUnits = src.MaxUnits;
                td.UnitWearLevel = src.Wear;
                td.UnitDirtLevel = src.Dirt;
                td.PaintSchemePath = src.PaintBlueprint >= 0 ? paths[src.PaintBlueprint] : "";
                var dict = new Il2CppSystem.Collections.Generic.Dictionary<UnitDefinition, UnitInstanceInfo>();
                foreach (var u in src.Units)
                {
                    var ud = new UnitDefinition();
                    ud.Name = u.Name;
                    ud.Path = paths[u.Blueprint];
                    ud.Valid = true;
                    var info = new UnitInstanceInfo();
                    info.Count = u.Count;
                    dict.Add(ud, info);
                }
                td.Units = dict;
                teams[t] = td;
            }
            cfg.Teams = teams;
            return cfg;
        }

        static string SafeName(string n)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            return string.IsNullOrEmpty(n) ? "vehicle.blueprint" : n;
        }
    }
}
