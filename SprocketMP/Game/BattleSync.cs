using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.DamageModelling;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons;
using Sprocket.Vehicles.Turrets;
using Sprocket.Vehicles.Weapons;
using Sprocket.Vehicles.Weapons.Cannons;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP.Game
{
    struct Snapshot
    {
        public float Time;
        public Vector3 Pos, Vel, AngVel;
        public Quaternion Rot;
        public Quaternion[] Rotators;
    }

    public sealed class NetVehicle
    {
        public uint Key;
        public int TeamId;
        public string Name;
        public VehicleBehaviour Behaviour;
        public Rigidbody Body;
        public Transform Root;
        public IntPtr BehaviourPtr, HealthPtr;
        public VehicleHealthRegister Health;
        public readonly List<Transform> Rotators = new List<Transform>();
        public readonly List<CannonBehaviour> Cannons = new List<CannonBehaviour>();
        public int Owner;
        public bool Puppet;
        public bool WasKinematic;
        // "ghost": puppet colliders don't push physics bodies (prevents spawn/teleport overlaps catapulting tanks)
        public bool Ghost;
        public float GhostUntil;
        public Collider[] Colliders;
        public float NetSteer, NetThrottle;   // owner's driver input (drives the puppet's tracks/engine)
        internal readonly List<Snapshot> Buffer = new List<Snapshot>();
        internal Quaternion[] TargetRotators;

        public bool Alive
        {
            get
            {
                try { return Behaviour != null && !Behaviour.WasCollected && Root != null; }
                catch { return false; }
            }
        }
    }

    /// <summary>
    /// In-battle synchronisation. Model: every vehicle has exactly one owner machine which simulates it
    /// (player-driven or AI). Everyone else turns it into a kinematic "puppet" that follows the owner's snapshots.
    /// Shots are replayed from the puppet's gun on every machine; damage is computed only by the victim's owner
    /// and replicated as raw HealthDelta calls.
    /// </summary>
    public static class BattleSync
    {
        public static bool InBattle;
        public static string StatusLine;
        public static DeathmatchGameMode Mode;
        public static readonly Dictionary<uint, NetVehicle> Vehicles = new Dictionary<uint, NetVehicle>();
        static readonly Dictionary<IntPtr, NetVehicle> s_byBehaviour = new Dictionary<IntPtr, NetVehicle>();
        static readonly Dictionary<IntPtr, NetVehicle> s_byRoot = new Dictionary<IntPtr, NetVehicle>();
        static readonly Dictionary<IntPtr, NetVehicle> s_byTrack = new Dictionary<IntPtr, NetVehicle>();

        /// <summary>
        /// Track physics of a puppet that stands still is skipped: the kinematic copy can't move, so the simulated
        /// drive just spins the tracks in place ("driving animation while standing").
        /// </summary>
        /// <summary>Vehicle a track belongs to (cached), or null.</summary>
        public static NetVehicle VehicleOfTrack(Sprocket.ContinuousTracks.TrackBehaviour tb)
        {
            if (!Active) return null;
            if (!s_byTrack.TryGetValue(tb.Pointer, out var v))
            {
                v = null;
                try
                {
                    var t = tb.Transform;
                    for (int i = 0; t != null && i < 12; i++, t = t.parent)
                        if (s_byRoot.TryGetValue(t.Pointer, out v)) break;
                }
                catch { }
                s_byTrack[tb.Pointer] = v;
            }
            return v;
        }

        static readonly Dictionary<IntPtr, NetVehicle> s_byHealth = new Dictionary<IntPtr, NetVehicle>();
        static readonly Dictionary<IntPtr, NetVehicle> s_byRotatorBehaviour = new Dictionary<IntPtr, NetVehicle>();
        static readonly Dictionary<IntPtr, (NetVehicle v, int idx)> s_byCannon = new Dictionary<IntPtr, (NetVehicle, int)>();
        static readonly Dictionary<uint, int> s_owners = new Dictionary<uint, int>();   // authoritative table (host) / last known (client)

        static BattleDesc s_desc;
        static bool s_synced;          // battle was started through the network
        static bool s_initialized;
        static float s_runningTime;
        static float s_sendTimer, s_debugTimer;
        public static bool Replaying;  // true while we execute a network-originated action
        static uint s_myKey, s_hostKey;
        static Dictionary<uint, int> s_broughtBy = new Dictionary<uint, int>();
        /// <summary>Vehicle each human is currently in (host decides; sent to everyone with Owners).</summary>
        static readonly Dictionary<int, uint> s_active = new Dictionary<int, uint>();
        /// <summary>Driver crew task of each vehicle (vehicle behaviour ptr -> IDrivable), learned from its CommanderAI.</summary>
        static readonly Dictionary<IntPtr, Sprocket.VehicleControl.IDrivable> s_drivers = new Dictionary<IntPtr, Sprocket.VehicleControl.IDrivable>();

        public static void NoteCommander(IntPtr vehicle, Sprocket.ArtificialIntelligence.CommanderAI c)
        {
            if (s_drivers.ContainsKey(vehicle)) return;
            try { var d = c.driver?.target; if (d != null) s_drivers[vehicle] = d; } catch { }
        }

        static void ReadDrive(NetVehicle v, out float steer, out float throttle)
        {
            steer = 0; throttle = 0;
            if (!s_drivers.TryGetValue(v.BehaviourPtr, out var d)) return;
            try { var sol = d.Solution; steer = sol.Steer; throttle = sol.Throttle; } catch { }
        }

        static void ApplyDrive(NetVehicle v)
        {
            if (!s_drivers.TryGetValue(v.BehaviourPtr, out var d)) return;
            try { d.Solution = new Sprocket.VehicleControl.DriveSolution { Steer = v.NetSteer, Throttle = v.NetThrottle }; } catch { }
        }
        static bool s_controlApplied;
        static float s_controlRetry;

        // ------------------------------------------------------------------------------- lifecycle
        public static void PrepareBattle(BattleDesc desc, bool synced, bool reset = false)
        {
            ClearVehicles(reset);
            s_desc = desc;
            s_synced = synced && desc != null && Session.Active;
            InBattle = s_synced;
            s_initialized = false;
            s_runningTime = 0;
            s_owners.Clear();
            s_active.Clear();
            s_myKey = 0;
            s_controlApplied = false;
            StatusLine = s_synced ? "Загрузка боя..." : null;
            Plugin.Log.LogInfo("PrepareBattle synced=" + s_synced);
        }

        /// <summary>Host: re-synchronise a battle that is already running (after reconnect / mod reload).</summary>
        public static void HostResync()
        {
            if (!Session.IsHost || Mode == null) return;
            var d = new BattleDesc();
            foreach (var p in Session.Players.Values) d.PlayerTeams[p.Id] = p.Id == 0 ? -2 : p.Team;
            var w = new Writer(Msg.Resync).Int(d.PlayerTeams.Count);
            foreach (var kv in d.PlayerTeams) w.Int(kv.Key).Int(kv.Value);
            Session.Send(w, true);
            PrepareBattle(d, true);
            Plugin.Log.LogInfo("Battle resynced");
        }

        /// <summary>Dev: teleport my vehicle 60 m in front of another human player's vehicle.</summary>
        public static void DevTeleportToOpponent()
        {
            if (!Active || !Vehicles.TryGetValue(s_myKey, out var me) || me.Puppet) return;
            NetVehicle target = null;
            if (Session.IsClient && Vehicles.TryGetValue(s_hostKey, out var h)) target = h;
            else target = Vehicles.Values.FirstOrDefault(v => v.Key != s_myKey && v.Owner != 0 && v.Owner != Session.LocalId);
            if (target == null) { Plugin.Log.LogInfo("Teleport: no target"); return; }
            var tp = target.Root.position;
            // place myself so that the target is straight down my gun barrel, 60 m away
            Vector3 gunFwd = me.Root.forward;
            try { var cam = Camera.main; if (cam != null) gunFwd = cam.transform.forward; } catch { }
            gunFwd.y = 0; if (gunFwd.sqrMagnitude < 0.01f) gunFwd = me.Root.forward; gunFwd.Normalize();
            var pos = tp - gunFwd * 60f + Vector3.up * 1.5f;
            var rot = me.Root.rotation;
            if (me.Body != null) { me.Body.position = pos; me.Body.rotation = rot; me.Body.linearVelocity = Vector3.zero; me.Body.angularVelocity = Vector3.zero; }
            me.Root.SetPositionAndRotation(pos, rot);
            Plugin.Log.LogInfo("Teleported");
        }

        public static bool RemoteRetry;
        static float s_lastRetryRequest = -10f;
        /// <summary>Callback of the currently open Victory/Defeat screen (captured from MissionEndScreen.WaitForInput).</summary>
        public static Il2CppSystem.Action<MissionEndScreenResult> EndScreenCallback;

        /// <summary>Retry (SPACE after victory/defeat): host restarts for everyone, clients follow the host.</summary>
        public static bool OnRetryRequested()
        {
            // clients may retry locally too: the reset hook turns it into a restart request to the host
            return true;
        }

        /// <summary>Game is being reset (respawn): forget vehicles, re-discover them once running again.</summary>
        static float s_remoteResetUntil, s_localResetAt = -100f;

        public static void OnResetGame()
        {
            if (!s_synced) return;
            EndScreenCallback = null;
            float now = Time.unscaledTime;
            bool remote = now < s_remoteResetUntil;
            s_remoteResetUntil = 0;
            if (Session.IsHost) Session.Send(new Writer(Msg.Retry), true);
            else if (!remote)
            {
                // client pressed SPACE on its end screen: it already restarts locally, ask the host to restart for everyone
                s_localResetAt = now;
                if (now - s_lastRetryRequest > 2f)
                {
                    s_lastRetryRequest = now;
                    Session.Send(new Writer(Msg.Retry), true);
                    Plugin.Log.LogInfo("Retry request sent to host");
                }
            }
            var d = s_desc;
            PrepareBattle(d, true, true);
            Plugin.Log.LogInfo($"Battle reset -> resync scheduled (remote={remote}, host={Session.IsHost})");
        }

        /// <summary>World positions (above the vehicle) and player ids of allied human-driven vehicles, except mine.</summary>
        public static IEnumerable<(Vector3 pos, int player)> AllyPlates()
        {
            if (!s_initialized || !Vehicles.TryGetValue(s_myKey, out var me)) yield break;
            foreach (var v in Vehicles.Values)
            {
                if (v.Key == s_myKey || v.TeamId != me.TeamId || !v.Alive) continue;
                int pid = -1;
                foreach (var kv in s_active) if (kv.Value == v.Key) { pid = kv.Key; break; }
                if (pid < 0) continue;
                if (pid == Session.LocalId) continue;
                Vector3 p;
                try { p = v.Root.position; } catch { continue; }
                yield return (p + Vector3.up * 4.5f, pid);
            }
        }

        public static void HostRestart()
        {
            if (!Session.IsHost || Mode == null) return;
            var cb = EndScreenCallback;
            EndScreenCallback = null;
            Plugin.Log.LogInfo("Host restart");
            if (cb != null)
            {
                try { cb.Invoke(MissionEndScreenResult.Retry); return; }
                catch (Exception e) { Plugin.Log.LogWarning("HostRestart end screen: " + e.Message); }
            }
            try { Mode.RetryAsyncVoid(); }
            catch (Exception e) { Plugin.Log.LogWarning("HostRestart: " + e.Message); }
            s_endScreenCleanupAt = Time.unscaledTime + 2.5f;
        }

        public static void QuitToMenu()
        {
            try
            {
                var gc = UnityEngine.Object.FindObjectOfType<Sprocket.GameControl.GameController>();
                if (gc == null) { Plugin.Log.LogInfo("GameController not found"); return; }
                if (Session.IsHost && InBattle) Session.Send(new Writer(Msg.BattleEnd), true);
                // a Victory / Defeat screen is waiting for input: leave through it, like pressing its "exit" key
                var cb = EndScreenCallback;
                EndScreenCallback = null;
                if (cb != null)
                {
                    try { cb.Invoke(MissionEndScreenResult.Escape); Plugin.Log.LogInfo("Quit via end screen"); return; }
                    catch (Exception e) { Plugin.Log.LogWarning("Quit via end screen: " + e.Message); }
                }
                gc.gameTime.RequestQuit(QuitType.MainMenu);
            }
            catch (Exception e) { Plugin.Log.LogWarning("QuitToMenu: " + e); }
        }

        public static bool InGameMode
        {
            get { try { return Mode != null && !Mode.WasCollected; } catch { return false; } }
        }

        public static void EndBattle()
        {
            if (InBattle && Session.IsHost) Session.Send(new Writer(Msg.BattleEnd), true);
            InBattle = false;
            s_synced = false;
            s_initialized = false;
            Mode = null;
            ClearVehicles();
            StatusLine = null;
        }

        static readonly List<(Rigidbody body, bool wasKinematic)> s_staleGhosts = new List<(Rigidbody, bool)>();

        static void ClearVehicles(bool reset = false)
        {
            // on a battle reset the game respawns everything at once: keep puppets kinematic ghosts until re-discovery,
            // otherwise a respawned tank can appear inside a stale puppet and be launched into orbit
            foreach (var v in Vehicles.Values)
            {
                if (reset && v.Puppet) { SetGhost(v, true); s_staleGhosts.Add((v.Body, v.WasKinematic)); }
                else RestoreLocal(v);
            }
            Vehicles.Clear();
            s_drivers.Clear();
            s_byRoot.Clear(); s_byTrack.Clear(); TrackAnim.Reset();
            s_byBehaviour.Clear(); s_byHealth.Clear(); s_byRotatorBehaviour.Clear(); s_byCannon.Clear();
        }

        public static void OnPlayerLeft(int playerId)
        {
            PlayerTanks.OnPlayerLeft(playerId);
            if (!InBattle) return;
            if (Session.IsHost)
            {
                // take over vehicles of the player who left (they keep standing still under host AI)
                foreach (var kv in s_owners.Where(k => k.Value == playerId).ToList()) s_owners[kv.Key] = 0;
                ApplyOwners();
                BroadcastOwners();
            }
            else if (playerId == 0)
            {
                StatusLine = "Хост отключился — бой продолжается локально";
                foreach (var v in Vehicles.Values) { v.Owner = Session.LocalId; }
                InBattle = false;
                ClearVehicles();
            }
        }

        // ------------------------------------------------------------------------------- per-frame (from patches)
        public static void ModeUpdate(DeathmatchGameMode dm)
        {
            Mode = dm;
            if (!s_synced) return;
            bool running;
            try { running = dm.gameState == DeathmatchGameMode.ModeState.Running; }
            catch { running = false; }
            if (!running) return;

            s_runningTime += Time.deltaTime;
            if (!s_initialized && s_runningTime > 1.0f) InitVehicles(dm);
            if (!s_initialized) return;

            s_controlRetry -= Time.deltaTime;
            if (s_controlRetry <= 0)
            {
                s_controlRetry = 1f;
                if (!s_controlApplied) TakeControl(dm);
                else CheckSwitch(dm);
            }

            s_debugTimer += Time.deltaTime;
            if (s_debugTimer > 5f && Cfg.DebugLog.Value)
            {
                s_debugTimer = 0;
                var parts = new List<string>();
                foreach (var v in Vehicles.Values)
                {
                    bool interesting = v.Key == s_myKey || v.Key == s_hostKey || (s_owners.TryGetValue(v.Key, out var o) && o != 0);
                    if (!interesting || !v.Alive) continue;
                    var p = v.Root.position;
                    string rot = v.Rotators.Count > 0 && v.Rotators[0] != null ? v.Rotators[0].localEulerAngles.y.ToString("F0") : "-";
                    string bodyInfo = "";
                    try { if (v.Body != null) { var bp = v.Body.position; bodyInfo = $" body=({bp.x:F1},{bp.y:F1},{bp.z:F1}) kin={v.Body.isKinematic} col={v.Body.detectCollisions} same={(v.Body.transform.Pointer == v.Root.Pointer)}"; } } catch { }
                    string last = v.Buffer.Count > 0 ? $" last=({v.Buffer[v.Buffer.Count - 1].Pos.x:F1},{v.Buffer[v.Buffer.Count - 1].Pos.z:F1})" : "";
                    parts.Add($"{v.Key:X8}[{(v.Puppet ? "P" : "L")} own={v.Owner}] pos=({p.x:F1},{p.y:F1},{p.z:F1}){bodyInfo}{last} turret={rot} buf={v.Buffer.Count} vel={(v.Buffer.Count > 0 ? v.Buffer[v.Buffer.Count - 1].Vel.magnitude : -1):F1} drv={v.NetThrottle:F2}/{v.NetSteer:F2} tracks={s_byTrack.Values.Count(x => x == v)}");
                }
                Plugin.Log.LogInfo($"SYNC dmg sent={s_dmgSent} replayed={s_dmgReplayed} blocked={s_dmgBlocked} | " + string.Join(" | ", parts));
                StatusLine = $"Машин: {Vehicles.Count}, моих: {Vehicles.Values.Count(v => !v.Puppet)}";
            }

            // turret / gun orientation of puppets (MoveToTarget is suppressed for them)
            foreach (var v in Vehicles.Values)
            {
                if (!v.Puppet || v.TargetRotators == null || !v.Alive) continue;
                ApplyRotators(v, v.TargetRotators);
            }
        }

        public static void ModeFixedUpdate(DeathmatchGameMode dm)
        {
            if (!s_synced || !s_initialized) return;
            float now = Time.realtimeSinceStartup;
            float renderTime = now - Cfg.InterpDelay.Value;
            foreach (var v in Vehicles.Values)
            {
                if (!v.Puppet || !v.Alive || v.Buffer.Count == 0) continue;
                Interpolate(v, renderTime);
                UpdateGhost(v, now);
                ApplyDrive(v);
            }

            s_sendTimer += Time.fixedDeltaTime;
            float interval = 1f / Math.Max(5, Cfg.SendRate.Value);
            if (s_sendTimer >= interval)
            {
                s_sendTimer = 0;
                SendStates();
            }
        }

        /// <summary>Called from MPBehaviour.Update (always ticking, also outside battle).</summary>
        static float s_endScreenCleanupAt = -1;
        public static void ScheduleEndScreenCleanup(float delay) => s_endScreenCleanupAt = Time.unscaledTime + delay;

        /// <summary>Removes Victory/Defeat screens left over after a host-driven retry.</summary>
        /// <summary>Before the game shows a Victory/Defeat screen: un-hide screens we hid earlier (the game may reuse them).</summary>
        public static void ReactivateEndScreens()
        {
            try
            {
                foreach (var es in UnityEngine.Object.FindObjectsOfType<Sprocket.EndScreen>(true))
                {
                    if (es == null) continue;
                    var root = es.transform.root.gameObject;
                    if (!root.activeSelf) { root.SetActive(true); Plugin.Log.LogInfo("Re-enabled hidden end screen"); }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("ReactivateEndScreens: " + e.Message); }
        }

        public static void CleanupEndScreens()
        {
            var screens = UnityEngine.Object.FindObjectsOfType<Sprocket.EndScreen>();
            int n = 0;
            foreach (var es in screens)
            {
                if (es == null) continue;
                var go = es.gameObject;
                var sc = go.scene;
                // only hide it: unloading the scene behind the game's back breaks its next Victory/Defeat screen
                if (!go.activeSelf) continue;
                go.transform.root.gameObject.SetActive(false);
                n++;
                Plugin.Log.LogInfo($"End screen '{go.name}' scene='{sc.name}' roots={sc.rootCount} active='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'");
            }
            if (n > 0) Plugin.Log.LogInfo($"Removed {n} leftover end screen(s)");
        }

        public static void Update()
        {
            if (s_endScreenCleanupAt > 0 && Time.unscaledTime >= s_endScreenCleanupAt)
            {
                s_endScreenCleanupAt = -1;
                if (Session.Active) { try { CleanupEndScreens(); } catch (Exception e) { Plugin.Log.LogWarning("EndScreen cleanup: " + e.Message); } }
            }
            if (InBattle && Mode != null)
            {
                try { if (Mode.WasCollected) { Plugin.Log.LogInfo("Game mode destroyed — battle over"); EndBattle(); } }
                catch { EndBattle(); }
            }
        }

        // ------------------------------------------------------------------------------- vehicle discovery
        static uint KeyOf(EntityID id)
        {
            return ((uint)id.TeamID << 24) | ((uint)(byte)id.SpawnerID << 16) | ((uint)id.GroupID << 8) | id.BuildNumber;
        }

        static void InitVehicles(DeathmatchGameMode dm)
        {
            s_initialized = true;
            ClearVehicles();
            var reg = dm.vehicleRegister?.TryCast<VehicleRegister>();
            if (reg == null) { Plugin.Log.LogError("VehicleRegister not found"); return; }
            var list = reg.register;
            var raw = new List<NetVehicle>();
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    var gw = list[i];
                    var veh = gw?.TryCast<Sprocket.Vehicles.Vehicle>();
                    if (veh == null) continue;
                    var vb = veh.behaviour;
                    if (vb == null) continue;
                    var nv = new NetVehicle
                    {
                        Behaviour = vb,
                        Key = KeyOf(vb.ID),
                        TeamId = (int)vb.ID.TeamID,
                        Body = vb.Rigidbody,
                        Root = vb.transform,
                        BehaviourPtr = vb.Pointer,
                        Name = veh.gameObject.name,
                    };
                    var hr = vb.healthRegister;
                    if (hr != null) { nv.Health = hr; nv.HealthPtr = hr.Pointer; s_byHealth[hr.Pointer] = nv; }
                    CollectParts(veh, nv);
                    raw.Add(nv);
                }
                catch (Exception e) { Plugin.Log.LogError("Vehicle discovery: " + e); }
            }
            // BuildNumber grows on every reset (and may differ if one side reset more often) —
            // normalize it per spawner group so keys stay identical on all machines.
            foreach (var grp in raw.GroupBy(v => v.Key & 0xFFFFFF00u))
            {
                uint min = grp.Min(v => v.Key & 0xFFu);
                foreach (var nv in grp)
                {
                    nv.Key = grp.Key | ((nv.Key & 0xFFu) - min);
                    if (Vehicles.ContainsKey(nv.Key)) { Plugin.Log.LogWarning("Duplicate vehicle key " + nv.Key.ToString("X8")); continue; }
                    Vehicles[nv.Key] = nv;
                    s_byBehaviour[nv.BehaviourPtr] = nv;
                    try { s_byRoot[nv.Root.Pointer] = nv; } catch { }
                    nv.Owner = 0;
                }
            }

            var manifest = string.Join(";", Vehicles.Values.OrderBy(v => v.Key).Select(v => $"{v.Key:X8}:{v.Rotators.Count}/{v.Cannons.Count}"));
            Plugin.Log.LogInfo($"Battle vehicles ({Vehicles.Count}): {manifest}");
            Plugin.Log.LogInfo($"playerTeamID={dm.playerTeamID}");

            // My vehicle: k-th vehicle (sorted by key) of my team, k = my index among humans of that team
            s_myKey = 0;
            int myTeamIdx = int.MinValue;
            bool hasTeam = s_desc != null && s_desc.PlayerTeams.TryGetValue(Session.LocalId, out myTeamIdx);
            // vehicles a player brought himself are named "<tank> [<player>]"
            var broughtBy = new Dictionary<uint, int>();
            foreach (var v in Vehicles.Values)
            {
                var bp = PlayerTanks.OwnerOf(v.Name);
                if (bp != null) broughtBy[v.Key] = bp.Id;
            }
            s_broughtBy = broughtBy;
            Plugin.Log.LogInfo($"Drivers: {string.Join(", ", broughtBy.Select(kv => $"{kv.Key:X8}->{kv.Value}"))} (me={Session.LocalId})");
            var own = broughtBy.FirstOrDefault(kv => kv.Value == Session.LocalId);
            if (own.Key != 0)
            {
                s_myKey = own.Key;
            }
            else if (hasTeam)
            {
                // players without their own tank get the remaining vehicles of their team in key order
                var humans = s_desc.PlayerTeams.Where(kv => kv.Value == myTeamIdx && !broughtBy.ContainsValue(kv.Key)).Select(kv => kv.Key).OrderBy(x => x).ToList();
                int slot = humans.IndexOf(Session.LocalId);
                int myTeamId = (int)dm.playerTeamID;
                var mine = Vehicles.Values.Where(v => v.TeamId == myTeamId && !broughtBy.ContainsKey(v.Key)).OrderBy(v => v.Key).ToList();
                if (slot >= 0 && slot < mine.Count) s_myKey = mine[slot].Key;
                else Session.AddChat("* Для тебя не хватило техники в команде — режим наблюдателя");
            }

            if (Session.IsHost)
            {
                foreach (var v in Vehicles.Values) if (!s_owners.ContainsKey(v.Key)) s_owners[v.Key] = 0;
                if (s_myKey != 0) s_owners[s_myKey] = 0;
                // vehicles picked in the lobby belong to their drivers, whatever the clients claim
                foreach (var kv in broughtBy) if (Session.Players.ContainsKey(kv.Value)) { s_owners[kv.Key] = kv.Value; s_active[kv.Value] = kv.Key; }
                if (s_myKey != 0) s_active[0] = s_myKey;
                ApplyOwners();
                BroadcastOwners();
            }
            else
            {
                // optimistic: everything is host's, except my own vehicle
                foreach (var v in Vehicles.Values) if (!s_owners.ContainsKey(v.Key)) s_owners[v.Key] = 0;
                if (s_myKey != 0)
                {
                    s_owners[s_myKey] = Session.LocalId;
                    Session.Send(new Writer(Msg.Claim).UInt(s_myKey), true);
                }
                ApplyOwners();
                Session.Send(new Writer(Msg.ClientInBattle).Str(manifest), true);
            }
            StatusLine = $"Синхронизация: {Vehicles.Count} машин, твоя: {(s_myKey != 0 ? s_myKey.ToString("X8") : "нет")}";
        }

        static void CollectParts(Sprocket.Vehicles.Vehicle veh, NetVehicle nv)
        {
            var go = veh.gameObject;
            foreach (var tm in go.GetComponentsInChildren<TraverseMotor>(true))
            {
                var tb = tm.behaviour?.TryCast<TurretBehaviour>();
                if (tb == null || tb.transform == null) continue;
                s_byRotatorBehaviour[tb.Pointer] = nv;
                nv.Rotators.Add(tb.transform);
            }
            foreach (var ld in go.GetComponentsInChildren<LayingDrive>(true))
            {
                var lb = ld.behaviour?.TryCast<LayingDriveBehaviour>();
                if (lb == null || lb.trunnions == null) continue;
                s_byRotatorBehaviour[lb.Pointer] = nv;
                nv.Rotators.Add(lb.trunnions);
            }
            foreach (var c in go.GetComponentsInChildren<Cannon>(true))
            {
                AddCannon(nv, c.behaviour);
                var kids = c.childBehaviours;
                if (kids != null)
                    for (int i = 0; i < kids.Length; i++) AddCannon(nv, kids[i]?.TryCast<CannonBehaviour>());
            }
        }

        static void AddCannon(NetVehicle nv, CannonBehaviour cb)
        {
            if (cb == null || s_byCannon.ContainsKey(cb.Pointer)) return;
            s_byCannon[cb.Pointer] = (nv, nv.Cannons.Count);
            nv.Cannons.Add(cb);
        }

        // ------------------------------------------------------------------------------- ownership / puppets
        static void ApplyOwners()
        {
            foreach (var v in Vehicles.Values)
            {
                v.Owner = s_owners.TryGetValue(v.Key, out var o) ? o : 0;
                bool puppet = v.Owner != Session.LocalId;
                if (puppet && !v.Puppet) MakePuppet(v);
                else if (!puppet && v.Puppet) RestoreLocal(v);
            }
            // bodies kept as ghosts over a reset that are no longer puppets go back to normal physics
            foreach (var (body, wasKin) in s_staleGhosts)
            {
                try
                {
                    if (body == null || body.WasCollected) continue;
                    if (Vehicles.Values.Any(v => v.Puppet && v.Body != null && v.Body.Pointer == body.Pointer)) continue;
                    body.isKinematic = wasKin; body.detectCollisions = true;
                }
                catch { }
            }
            s_staleGhosts.Clear();
        }

        static void MakePuppet(NetVehicle v)
        {
            v.Puppet = true;
            // the game keeps writing velocities into puppet bodies -> "kinematic body" warning spam each frame
            try { var lg = Debug.unityLogger.TryCast<UnityEngine.Logger>(); if (lg != null) lg.filterLogType = LogType.Error; } catch { }
            try
            {
                if (v.Body != null)
                {
                    v.WasKinematic = v.Body.isKinematic;
                    foreach (var sg in s_staleGhosts) { try { if (sg.body != null && sg.body.Pointer == v.Body.Pointer) v.WasKinematic = sg.wasKinematic; } catch { } }
                    v.Body.isKinematic = true;
                    v.Body.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("MakePuppet: " + e.Message); }
            SetGhost(v, true);
            v.GhostUntil = Time.realtimeSinceStartup + 1f;
        }

        static void SetGhost(NetVehicle v, bool ghost)
        {
            v.Ghost = ghost;
            try { if (v.Body != null && !v.Body.WasCollected) v.Body.detectCollisions = !ghost; } catch { }
        }

        static Collider[] CollidersOf(NetVehicle v)
        {
            if (v.Colliders == null)
            {
                try { v.Colliders = v.Root.GetComponentsInChildren<Collider>().ToArray(); }
                catch { v.Colliders = Array.Empty<Collider>(); }
            }
            return v.Colliders;
        }

        static bool BoundsOf(NetVehicle v, out Bounds b)
        {
            b = default; bool any = false;
            foreach (var c in CollidersOf(v))
            {
                try
                {
                    if (c == null || c.WasCollected || c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
                catch { }
            }
            return any;
        }

        /// <summary>Re-enable puppet collisions once it has settled and doesn't overlap any locally simulated vehicle.</summary>
        static void UpdateGhost(NetVehicle v, float now)
        {
            if (!v.Ghost || now < v.GhostUntil || v.Buffer.Count == 0) return;
            if (BoundsOf(v, out var pb))
            {
                pb.Expand(0.5f);
                foreach (var o in Vehicles.Values)
                {
                    if (o.Puppet || !o.Alive) continue;
                    if (BoundsOf(o, out var ob) && pb.Intersects(ob)) { v.GhostUntil = now + 0.25f; return; }
                }
            }
            SetGhost(v, false);
        }

        static void RestoreLocal(NetVehicle v)
        {
            if (!v.Puppet) return;
            v.Puppet = false;
            SetGhost(v, false);
            try { if (v.Body != null && !v.Body.WasCollected) v.Body.isKinematic = v.WasKinematic; } catch { }
            v.Buffer.Clear();
        }

        static void BroadcastOwners()
        {
            if (!Session.IsHost) return;
            var w = new Writer(Msg.Owners).Int(s_owners.Count);
            foreach (var kv in s_owners) w.UInt(kv.Key).Int(kv.Value);
            w.UInt(s_myKey);
            var act = s_active.Where(kv => Session.Players.ContainsKey(kv.Key)).ToList();
            w.Int(act.Count);
            foreach (var kv in act) w.Int(kv.Key).UInt(kv.Value);
            Session.Send(w, true);
        }

        static NetVehicle CurrentTarget(DeathmatchGameMode dm)
        {
            try
            {
                var st = dm.vehicleControlState?.TryCast<Sprocket.VehicleControl.VehicleControlPlayerState>();
                var t = st?.vehicleController?.controlTarget;
                if (t == null) return null;
                return s_byBehaviour.TryGetValue(t.Pointer, out var v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The player switched vehicles (Tab / death switch): a vehicle driven by the AI becomes mine,
        /// a vehicle driven by another player can only be watched.
        /// </summary>
        static void CheckSwitch(DeathmatchGameMode dm)
        {
            var v = CurrentTarget(dm);
            if (v == null || v.Key == s_myKey) return;
            Plugin.Log.LogInfo($"Switched to {v.Key:X8} {v.Name}");
            s_myKey = v.Key;
            if (Session.IsHost)
            {
                if (!HostTryAssign(0, v.Key)) Session.AddChat("* Этим танком управляет игрок — только наблюдение");
            }
            else if (v.Owner != Session.LocalId || !s_active.TryGetValue(Session.LocalId, out var cur) || cur != v.Key)
            {
                Session.Send(new Writer(Msg.Claim).UInt(v.Key), true);
            }
        }

        /// <summary>Host: give vehicle <paramref name="key"/> to player <paramref name="pid"/> unless another human is in it.</summary>
        static bool HostTryAssign(int pid, uint key)
        {
            if (!Vehicles.ContainsKey(key)) return false;
            foreach (var kv in s_active)
                if (kv.Key != pid && kv.Value == key && Session.Players.ContainsKey(kv.Key))
                {
                    Plugin.Log.LogInfo($"{key:X8} requested by {pid}: driven by {kv.Key}, watch only");
                    if (pid == 0) s_active.Remove(0);
                    BroadcastOwners();
                    return false;
                }
            s_owners[key] = pid;
            s_active[pid] = key;
            Plugin.Log.LogInfo($"{key:X8} -> player {pid}");
            if (s_initialized) ApplyOwners();
            BroadcastOwners();
            return true;
        }

        static void TakeControl(DeathmatchGameMode dm)
        {
            if (s_myKey == 0) { s_controlApplied = true; return; }
            if (!Vehicles.TryGetValue(s_myKey, out var v) || !v.Alive) { s_controlApplied = true; return; }
            try
            {
                var state = dm.vehicleControlState;
                if (state == null) return;
                state.SetControlTarget(new IVehicleBehaviour(v.BehaviourPtr));
                s_controlApplied = true;
                // drop whatever throttle the AI was holding before I took over
                if (s_drivers.TryGetValue(v.BehaviourPtr, out var d)) { try { d.Solution = new Sprocket.VehicleControl.DriveSolution(); } catch { } }
                Plugin.Log.LogInfo("Took control of " + v.Key.ToString("X8") + " " + v.Name);
            }
            catch (Exception e) { Plugin.Log.LogWarning("SetControlTarget failed: " + e.Message); }
        }

        // ------------------------------------------------------------------------------- queries used by patches
        public static bool IsPuppetBehaviour(IntPtr vehicleBehaviour) => s_synced && s_byBehaviour.TryGetValue(vehicleBehaviour, out var v) && v.Puppet;
        /// <summary>AI must not drive puppets nor the vehicle I drive myself.</summary>
        public static bool AiBlocked(IntPtr vehicleBehaviour) => s_synced && s_byBehaviour.TryGetValue(vehicleBehaviour, out var v) && (v.Puppet || (s_myKey != 0 && v.Key == s_myKey));
        public static bool IsPuppetRotator(IntPtr rotatorBehaviour) => s_synced && s_byRotatorBehaviour.TryGetValue(rotatorBehaviour, out var v) && v.Puppet;
        public static bool Active => s_synced && s_initialized;

        // ------------------------------------------------------------------------------- state sync
        static void SendStates()
        {
            if (Session.Transport == null) return;
            var owned = Vehicles.Values.Where(v => !v.Puppet && v.Alive).ToList();
            if (owned.Count == 0) return;
            // LiteNetLib unreliable packets are limited to ~1400 bytes: send in batches
            const int perPacket = 7;
            for (int start = 0; start < owned.Count; start += perPacket)
            {
                int n = Math.Min(perPacket, owned.Count - start);
                var w = new Writer(Msg.State).Int(n);
                for (int i = start; i < start + n; i++)
                {
                    var v = owned[i];
                    Vector3 pos = v.Root.position, vel = Vector3.zero, ang = Vector3.zero;
                    Quaternion rot = v.Root.rotation;
                    try { if (v.Body != null) { pos = v.Body.position; rot = v.Body.rotation; vel = v.Body.linearVelocity; ang = v.Body.angularVelocity; } } catch { }
                    w.UInt(v.Key).Vec(pos).Quat(rot).Vec(vel).Vec(ang);
                    WriteRotators(w, v);
                    ReadDrive(v, out var steer, out var thr);
                    w.Float(steer).Float(thr);
                }
                Session.Send(w, false);
            }
        }

        static void WriteRotators(Writer w, NetVehicle v)
        {
            w.Byte((byte)v.Rotators.Count);
            foreach (var t in v.Rotators)
            {
                Quaternion q = Quaternion.identity;
                try { if (t != null) q = t.localRotation; } catch { }
                w.Quat(q);
            }
        }

        static Quaternion[] ReadRotators(Reader r)
        {
            int n = r.Byte();
            var a = new Quaternion[n];
            for (int i = 0; i < n; i++) a[i] = r.Quat();
            return a;
        }

        static void ApplyRotators(NetVehicle v, Quaternion[] rots)
        {
            int n = Math.Min(rots.Length, v.Rotators.Count);
            for (int i = 0; i < n; i++)
            {
                try { var t = v.Rotators[i]; if (t != null) t.localRotation = rots[i]; } catch { }
            }
        }

        static void Interpolate(NetVehicle v, float renderTime)
        {
            var b = v.Buffer;
            // drop old snapshots, keep one older than renderTime
            while (b.Count >= 2 && b[1].Time <= renderTime) b.RemoveAt(0);
            Vector3 pos; Quaternion rot;
            if (b.Count >= 2 && b[0].Time <= renderTime)
            {
                var a = b[0]; var c = b[1];
                float t = Mathf.Clamp01((renderTime - a.Time) / Math.Max(0.001f, c.Time - a.Time));
                pos = Vector3.Lerp(a.Pos, c.Pos, t);
                rot = Quaternion.Slerp(a.Rot, c.Rot, t);
            }
            else
            {
                // extrapolate a little from the newest snapshot
                var s = b[b.Count - 1];
                float dt = Mathf.Clamp(renderTime - s.Time, 0f, 0.25f);
                pos = s.Pos + s.Vel * dt;
                rot = s.Rot;
            }
            try
            {
                if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z) || float.IsNaN(rot.w)) return;
                // big jump (spawn / resync / teleport): pass through things for a moment instead of shoving them
                if ((v.Root.position - pos).sqrMagnitude > 25f)
                {
                    if (!v.Ghost) SetGhost(v, true);
                    v.GhostUntil = Time.realtimeSinceStartup + 0.5f;
                }
                if (v.Body != null) { v.Body.position = pos; v.Body.rotation = rot; }
                v.Root.SetPositionAndRotation(pos, rot);
            }
            catch { }
        }

        // ------------------------------------------------------------------------------- shots
        /// <summary>Owner side: a cannon on a locally simulated vehicle fired.</summary>
        public static void OnCannonFired(CannonBehaviour cb, ProjectileTypeID type)
        {
            if (!Active || Replaying) return;
            if (!s_byCannon.TryGetValue(cb.Pointer, out var e) || e.v.Puppet) return;
            var v = e.v;
            var w = new Writer(Msg.Fire).UInt(v.Key).Byte((byte)e.idx).Int(type.Value).Str(GuidOf(type));
            Vector3 pos = v.Root.position; Quaternion rot = v.Root.rotation;
            try { if (v.Body != null) { pos = v.Body.position; rot = v.Body.rotation; } } catch { }
            w.Vec(pos).Quat(rot);
            WriteRotators(w, v);
            Session.Send(w, true);
            Plugin.Log.LogInfo($"FIRE sent {v.Key:X8} cannon {e.idx} type {type.Value}");
        }

        /// <summary>Should a FireInternal call be allowed? Puppets may only fire when replaying network shots.</summary>
        public static bool AllowFire(CannonBehaviour cb)
        {
            if (!Active || Replaying) return true;
            return !(s_byCannon.TryGetValue(cb.Pointer, out var e) && e.v.Puppet);
        }

        // Projectile type IDs are allocated at runtime and differ between machines: translate via the shell GUID.
        static string GuidOf(ProjectileTypeID id)
        {
            try
            {
                var reg = IProjectileTypeRegister.Instance;
                if (reg != null && reg.TryGet(id, out IProjectileType t) && t != null) return t.Guid.ToString();
            }
            catch (Exception e) { Plugin.Log.LogWarning("GuidOf: " + e.Message); }
            return "";
        }

        static ProjectileTypeID IdOf(string guid, CannonBehaviour cb, int fallback)
        {
            try
            {
                var reg = IProjectileTypeRegister.Instance;
                if (reg != null && !string.IsNullOrEmpty(guid))
                {
                    var g = Il2CppSystem.Guid.Parse(guid);
                    if (reg.IsRegistered(g)) return reg.GetID(g);
                }
                var loaded = cb.LoadedProjectileTypeID;
                if (loaded.Value > 0) return loaded;
            }
            catch (Exception e) { Plugin.Log.LogWarning("IdOf: " + e.Message); }
            return new ProjectileTypeID(fallback);
        }

        static void ReplayFire(Reader r)
        {
            uint key = r.UInt(); int idx = r.Byte(); int type = r.Int(); string guid = r.Str();
            var pos = r.Vec(); var rot = r.Quat(); var rots = ReadRotators(r);
            if (!Vehicles.TryGetValue(key, out var v) || !v.Puppet || !v.Alive) return;
            if (idx < 0 || idx >= v.Cannons.Count) return;
            try
            {
                // snap the puppet exactly to the shooter's pose so the shell leaves the right barrel
                if (v.Body != null) { v.Body.position = pos; v.Body.rotation = rot; }
                v.Root.SetPositionAndRotation(pos, rot);
                ApplyRotators(v, rots);
                v.TargetRotators = rots;
                Replaying = true;
                var cb = v.Cannons[idx];
                var local = IdOf(guid, cb, type);
                cb.FireInternal(local);
                Plugin.Log.LogInfo($"FIRE replayed {key:X8} cannon {idx} type {type}->{local.Value} ({guid})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("ReplayFire: " + e.Message); }
            finally { Replaying = false; }
        }

        // ------------------------------------------------------------------------------- damage
        /// <summary>Returns false if the damage must be suppressed (puppet).</summary>
        public static bool OnHealthDelta(VehicleHealthRegister reg, bool withId, int id, float value, DamageModelDamageModifiers mods)
        {
            if (!Active || Replaying) return true;
            if (!s_byHealth.TryGetValue(reg.Pointer, out var v)) return true;
            if (v.Puppet) { s_dmgBlocked++; return false; }
            var w = new Writer(Msg.Damage).UInt(v.Key).Bool(withId).Int(id).Float(value)
                .Float(mods.Piercing).Float(mods.BluntForce).Float(mods.Pressure).Float(mods.Thermal);
            Session.Send(w, true);
            s_dmgSent++;
            return true;
        }
        static int s_dmgSent, s_dmgReplayed, s_dmgBlocked;

        static void ReplayDamage(Reader r)
        {
            uint key = r.UInt(); bool withId = r.Bool(); int id = r.Int(); float value = r.Float();
            var f = new float[] { r.Float(), r.Float(), r.Float(), r.Float() };
            var mods = System.Runtime.InteropServices.MemoryMarshal.Read<DamageModelDamageModifiers>(System.Runtime.InteropServices.MemoryMarshal.AsBytes(f.AsSpan()));
            if (!Vehicles.TryGetValue(key, out var v) || !v.Puppet || v.Health == null) return;
            try
            {
                Replaying = true;
                s_dmgReplayed++;
                if (withId) v.Health.Sprocket_DamageModelling_IDamageApplier_HealthDelta(id, value, mods);
                else v.Health.Sprocket_DamageModelling_IDamageApplier_HealthDelta(value, mods);
            }
            catch (Exception e) { Plugin.Log.LogWarning("ReplayDamage: " + e.Message); }
            finally { Replaying = false; }
        }

        // ------------------------------------------------------------------------------- messages
        public static void OnMessage(int from, Reader r, byte[] raw)
        {
            switch (r.Type)
            {
                case Msg.Lobby:
                case Msg.Blueprint:
                case Msg.LobbyAdd:
                case Msg.LobbyRemove:
                case Msg.Pick:
                    LobbySync.OnMessage(from, r);
                    break;
                case Msg.BattleStart:
                    if (Session.IsClient) BattleSetup.OnBattleStartMessage(r);
                    break;
                case Msg.Retry:
                    if (Session.IsHost && InGameMode && s_synced)
                    {
                        Plugin.Log.LogInfo($"Retry requested by player {from}");
                        HostRestart();
                        break;
                    }
                    if (Session.IsClient && InGameMode)
                    {
                        s_endScreenCleanupAt = Time.unscaledTime + 2.5f;
                        if (Time.unscaledTime - s_localResetAt < 6f)
                        {
                            Plugin.Log.LogInfo("Host restart after our own reset — already restarted");
                            s_localResetAt = -100f;
                            break;
                        }
                        s_remoteResetUntil = Time.unscaledTime + 8f;
                        var cb = EndScreenCallback;
                        EndScreenCallback = null;
                        if (cb != null)
                        {
                            // close the end screen the same way SPACE would; the game then calls Retry() itself
                            try { cb.Invoke(MissionEndScreenResult.Retry); Plugin.Log.LogInfo("Remote retry via end screen"); }
                            catch (Exception e) { Plugin.Log.LogWarning("EndScreen retry: " + e.Message); goto direct; }
                            break;
                        }
                    direct:
                        try { RemoteRetry = true; Mode.RetryAsyncVoid(); }
                        catch (Exception e) { Plugin.Log.LogWarning("Retry: " + e.Message); }
                        finally { RemoteRetry = false; }
                    }
                    break;
                case Msg.Resync:
                    if (Session.IsClient)
                    {
                        var d = new BattleDesc();
                        int n = r.Int();
                        for (int i = 0; i < n; i++) d.PlayerTeams[r.Int()] = r.Int();
                        if (InGameMode) { PrepareBattle(d, true); Plugin.Log.LogInfo("Host resynced the battle"); }
                        else Session.AddChat("* Матч уже идёт — присоединишься к следующему");
                    }
                    break;
                case Msg.BattleEnd:
                    if (Session.IsClient)
                    {
                        Session.AddChat("* Хост завершил бой");
                        StatusLine = "Хост вышел из боя";
                        InBattle = false;
                        s_synced = false;
                        ClearVehicles();
                        if (InGameMode) QuitToMenu();
                    }
                    break;
                case Msg.ClientInBattle:
                    if (Session.IsHost)
                    {
                        var manifest = r.Str();
                        if (Session.Players.TryGetValue(from, out var p)) { p.InBattle = true; Session.BroadcastRoster(); }
                        var mine = string.Join(";", Vehicles.Values.OrderBy(v => v.Key).Select(v => $"{v.Key:X8}:{v.Rotators.Count}/{v.Cannons.Count}"));
                        if (s_initialized && manifest != mine)
                        {
                            Plugin.Log.LogWarning($"Vehicle manifest mismatch with player {from}:\n host={mine}\n them={manifest}");
                            Plugin.Log.LogInfo($"Vehicle manifest of {p?.Name} differs");
                        }
                        BroadcastOwners();
                    }
                    break;
                case Msg.Claim:
                    if (Session.IsHost)
                    {
                        uint key = r.UInt();
                        if (!HostTryAssign(from, key))
                            Session.SendToPlayer(from, new Writer(Msg.Chat).Str("* Этим танком управляет игрок — только наблюдение"), true);
                        if (s_initialized) ApplyOwners();
                        BroadcastOwners();
                    }
                    break;
                case Msg.Owners:
                    if (Session.IsClient)
                    {
                        int n = r.Int();
                        for (int i = 0; i < n; i++) s_owners[r.UInt()] = r.Int();
                        s_hostKey = r.UInt();
                        s_active.Clear();
                        int na = r.Int();
                        for (int i = 0; i < na; i++) s_active[r.Int()] = r.UInt();
                        if (s_initialized)
                        {
                            // at the start the host decides who drives what: follow it
                            var mine = s_owners.Where(kv => kv.Value == Session.LocalId && Vehicles.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
                            if (!s_controlApplied && mine.Count > 0 && !mine.Contains(s_myKey))
                            {
                                var pick = mine.FirstOrDefault(k => s_broughtBy.TryGetValue(k, out var d) && d == Session.LocalId);
                                s_myKey = pick != 0 ? pick : mine[0];
                                s_controlApplied = false;
                                Plugin.Log.LogInfo($"Host assigned me {s_myKey:X8}");
                            }
                            ApplyOwners();
                        }
                    }
                    break;
                case Msg.State:
                    if (Session.IsHost) Session.BroadcastRaw(raw, false, from);
                    ReadStates(from, r);
                    break;
                case Msg.Fire:
                    if (Session.IsHost) Session.BroadcastRaw(raw, true, from);
                    if (Active) ReplayFire(r);
                    break;
                case Msg.Damage:
                    if (Session.IsHost) Session.BroadcastRaw(raw, true, from);
                    if (Active) ReplayDamage(r);
                    break;
            }
        }

        static void ReadStates(int from, Reader r)
        {
            if (!Active) return;
            int n = r.Int();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < n; i++)
            {
                uint key = r.UInt();
                var s = new Snapshot { Time = now, Pos = r.Vec(), Rot = r.Quat(), Vel = r.Vec(), AngVel = r.Vec() };
                s.Rotators = ReadRotators(r);
                float steer = r.Float(), thr = r.Float();
                if (!Vehicles.TryGetValue(key, out var v) || !v.Puppet) continue;
                v.NetSteer = steer; v.NetThrottle = thr;
                v.Buffer.Add(s);
                if (v.Buffer.Count > 30) v.Buffer.RemoveAt(0);
                v.TargetRotators = s.Rotators;
            }
        }
    }
}
