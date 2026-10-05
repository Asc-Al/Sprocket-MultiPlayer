using System;
using System.Collections.Generic;
using System.Linq;
using SprocketMP.Game;

namespace SprocketMP.Net
{
    public enum Role { None, Host, Client }

    public sealed class Player
    {
        public int Id;          // 0 = host
        public string Name;
        public int Team = -1;   // config team index, -1 = auto
        public int Conn = -1;   // transport connection (host side), -1 for local
        public bool InBattle;
        public int Ping;
        public string Tank = ""; // picked vehicle (unit name in the lobby roster), "" = none
        public int PickTeam = -1; // team of the picked vehicle
    }

    /// <summary>Lobby / connection layer: who is connected, chat, roster, routing of game messages.</summary>
    public static class Session
    {
        public const int ProtocolVersion = 3;
        public static Role Role { get; private set; }
        public static ITransport Transport { get; private set; }
        public static int LocalId { get; private set; } = -1;
        public static readonly Dictionary<int, Player> Players = new Dictionary<int, Player>();
        public static readonly List<string> ChatLog = new List<string>();
        public static string LastError = "";

        public static bool Active => Role != Role.None;
        public static bool IsHost => Role == Role.Host;
        public static bool IsClient => Role == Role.Client;
        public static bool Connected => Role == Role.Host || (Role == Role.Client && LocalId >= 0);
        public static Player Local => Players.TryGetValue(LocalId, out var p) ? p : null;
        public static int RemoteCount => Players.Count(p => p.Key != LocalId);

        static readonly Dictionary<int, int> s_connToPlayer = new Dictionary<int, int>();
        static int s_nextPlayerId = 1;
        static int s_nextChunkId = 1;
        static readonly Dictionary<(int conn, int id), byte[][]> s_chunks = new Dictionary<(int, int), byte[][]>();
        const int ChunkSize = 48 * 1024;

        /// <summary>Game-level message handler (set by BattleSync). Args: sender player id, reader.</summary>
        public static Action<int, Reader, byte[]> GameMessage;
        public static Action<int> PlayerLeft;

        // ---------------------------------------------------------------- lifecycle
        public static void HostIP(int port)
        {
            Stop();
            try { Attach(LnlTransport.Host(port), Role.Host); }
            catch (Exception e) { LastError = e.Message; Stop(); }
        }

        public static void JoinIP(string addr, int port)
        {
            Stop();
            try { Attach(LnlTransport.Join(addr, port), Role.Client); }
            catch (Exception e) { LastError = e.Message; Stop(); }
        }

        static void Attach(ITransport t, Role role)
        {
            Transport = t;
            Role = role;
            LastError = "";
            t.Connected += OnConnected;
            t.Disconnected += OnDisconnected;
            t.Received += OnReceived;
            Players.Clear();
            s_connToPlayer.Clear();
            if (role == Role.Host)
            {
                LocalId = 0;
                Players[0] = new Player { Id = 0, Name = MyName(), Conn = -1 };
                s_nextPlayerId = 1;
            }
            else LocalId = -1;
            Plugin.Log.LogInfo($"Session started as {role} via {t.Name}");
        }

        public static void Stop()
        {
            if (Transport != null)
            {
                try { Transport.Shutdown(); } catch (Exception e) { Plugin.Log.LogWarning(e.ToString()); }
            }
            Transport = null;
            Role = Role.None;
            LocalId = -1;
            Players.Clear();
            s_connToPlayer.Clear();
            s_chunks.Clear();
        }

        /// <summary>Name of this game copy for the current run (two copies on one PC share the config file).</summary>
        public static string NameOverride;

        public static string MyName()
        {
            if (!string.IsNullOrWhiteSpace(NameOverride)) return NameOverride;
            var n = Cfg.PlayerName.Value;
            if (string.IsNullOrWhiteSpace(n)) n = Environment.UserName;
            return n;
        }

        static float s_pingTimer;
        public static void Tick(float dt)
        {
            if (Transport == null) return;
            try { Transport.Poll(); }
            catch (Exception e) { Plugin.Log.LogError("Transport poll: " + e); }

            if (IsHost)
            {
                if (Players.TryGetValue(0, out var hostP) && hostP.InBattle != Game.BattleSync.InGameMode)
                {
                    hostP.InBattle = Game.BattleSync.InGameMode;
                    BroadcastRoster();
                }
                s_pingTimer += dt;
                if (s_pingTimer > 2f)
                {
                    s_pingTimer = 0;
                    bool changed = false;
                    foreach (var p in Players.Values)
                    {
                        if (p.Conn < 0) continue;
                        int ping = Transport.Ping(p.Conn);
                        if (ping != p.Ping) { p.Ping = ping; changed = true; }
                    }
                    if (changed) BroadcastRoster();
                }
            }
        }

        // ---------------------------------------------------------------- transport events
        static void OnConnected(int conn)
        {
            if (IsClient)
            {
                Send(new Writer(Msg.Hello).Int(ProtocolVersion).Str(Plugin.Version).Str(MyName()), true);
            }
        }

        static void OnDisconnected(int conn, string reason)
        {
            if (IsHost)
            {
                if (s_connToPlayer.TryGetValue(conn, out var pid))
                {
                    s_connToPlayer.Remove(conn);
                    if (Players.TryGetValue(pid, out var p))
                    {
                        Players.Remove(pid);
                        AddChat($"* {p.Name} отключился ({reason})");
                        PlayerLeft?.Invoke(pid);
                    }
                    BroadcastRoster();
                }
            }
            else
            {
                AddChat("* Соединение с хостом потеряно: " + reason);
                if (Transport is LnlTransport && reason != "DisconnectPeerCalled") AutoReconnect.ClientLost(Cfg.LastAddress.Value, Cfg.Port.Value);
                LastError = "Отключено: " + reason;
                var t = Transport;
                Transport = null;
                Role = Role.None;
                LocalId = -1;
                Players.Clear();
                try { t?.Shutdown(); } catch { }
                PlayerLeft?.Invoke(0);
            }
        }

        static void OnReceived(int conn, byte[] data)
        {
            Reader r;
            try { r = new Reader(data); }
            catch { return; }

            if (r.Type == Msg.Chunk)
            {
                int id = r.Int(), idx = r.Int(), total = r.Int();
                var part = r.Bytes();
                var key = (conn, id);
                if (!s_chunks.TryGetValue(key, out var parts)) s_chunks[key] = parts = new byte[total][];
                parts[idx] = part;
                if (parts.All(x => x != null))
                {
                    s_chunks.Remove(key);
                    var whole = Compression.Unpack(parts.SelectMany(x => x).ToArray());
                    OnReceived(conn, whole);
                }
                return;
            }

            try
            {
                if (IsHost) HostHandle(conn, r, data);
                else ClientHandle(r, data);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Error handling {r.Type}: {e}");
            }
        }

        static void HostHandle(int conn, Reader r, byte[] raw)
        {
            if (r.Type == Msg.Hello)
            {
                int proto = r.Int();
                string ver = r.Str();
                string name = r.Str();
                if (proto != ProtocolVersion || ver != Plugin.Version)
                {
                    SendTo(conn, new Writer(Msg.Kick).Str($"Версия мода не совпадает: у хоста {Plugin.Version}, у тебя {ver}"), true);
                    Transport.Disconnect(conn);
                    return;
                }
                // Joining during a battle: if the player is in the same battle (reconnect), re-sync everyone.
                if (BattleSync.InGameMode) AutoReconnect.PendingResync = true;
                var p = new Player { Id = s_nextPlayerId++, Name = name, Conn = conn };
                Players[p.Id] = p;
                s_connToPlayer[conn] = p.Id;
                SendTo(conn, new Writer(Msg.Welcome).Int(p.Id), true);
                BroadcastRoster();
                AddChat($"* {name} подключился");
                Broadcast(new Writer(Msg.Chat).Str($"* {name} подключился"), true, -1);
                return;
            }

            if (!s_connToPlayer.TryGetValue(conn, out var pid)) return;
            var player = Players[pid];
            switch (r.Type)
            {
                case Msg.Chat:
                {
                    var text = $"{player.Name}: {r.Str()}";
                    AddChat(text);
                    Broadcast(new Writer(Msg.Chat).Str(text), true, -1);
                    break;
                }
                case Msg.TeamRequest:
                    player.Team = r.Int();
                    BroadcastRoster();
                    break;
                case Msg.Rename:
                {
                    var nn = CleanName(r.Str());
                    if (nn.Length > 0 && nn != player.Name)
                    {
                        var line = $"* {player.Name} теперь {nn}";
                        player.Name = nn;
                        AddChat(line);
                        Broadcast(new Writer(Msg.Chat).Str(line), true, -1);
                        BroadcastRoster();
                    }
                    break;
                }
                default:
                    GameMessage?.Invoke(pid, r, raw);
                    break;
            }
        }

        static void ClientHandle(Reader r, byte[] raw)
        {
            switch (r.Type)
            {
                case Msg.Welcome:
                    LocalId = r.Int();
                    AddChat("* Подключено к хосту");
                    break;
                case Msg.Roster:
                {
                    int n = r.Int();
                    Players.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Player { Id = r.Int(), Name = r.Str(), Team = r.Int(), Ping = r.Int(), InBattle = r.Bool(), Tank = r.Str(), PickTeam = r.Int() };
                        Players[p.Id] = p;
                    }
                    break;
                }
                case Msg.Chat:
                    AddChat(r.Str());
                    break;
                case Msg.Kick:
                    LastError = r.Str();
                    AddChat("* Хост отключил: " + LastError);
                    break;
                default:
                    GameMessage?.Invoke(0, r, raw);
                    break;
            }
        }

        // ---------------------------------------------------------------- sending
        public static void BroadcastRoster()
        {
            if (!IsHost) return;
            var w = new Writer(Msg.Roster).Int(Players.Count);
            foreach (var p in Players.Values.OrderBy(p => p.Id))
                w.Int(p.Id).Str(p.Name).Int(p.Team).Int(p.Ping).Bool(p.InBattle).Str(p.Tank).Int(p.PickTeam);
            Broadcast(w, true, -1);
        }

        /// <summary>Client: send to host. Host: broadcast to every client.</summary>
        public static void Send(Writer w, bool reliable)
        {
            if (Transport == null) return;
            if (IsClient) Transport.Send(0, w.ToArray(), reliable);
            else Broadcast(w, reliable, -1);
        }

        public static void Broadcast(Writer w, bool reliable, int exceptPlayer) => BroadcastRaw(w.ToArray(), reliable, exceptPlayer);

        public static void BroadcastRaw(byte[] data, bool reliable, int exceptPlayer)
        {
            if (Transport == null || !IsHost) return;
            foreach (var p in Players.Values)
                if (p.Conn >= 0 && p.Id != exceptPlayer) Transport.Send(p.Conn, data, reliable);
        }

        public static void SendToPlayer(int playerId, Writer w, bool reliable)
        {
            if (Transport == null) return;
            if (IsClient) { Transport.Send(0, w.ToArray(), reliable); return; }
            if (Players.TryGetValue(playerId, out var p) && p.Conn >= 0) Transport.Send(p.Conn, w.ToArray(), reliable);
        }

        static void SendTo(int conn, Writer w, bool reliable) => Transport?.Send(conn, w.ToArray(), reliable);

        /// <summary>Large reliable message (battle setup with blueprints): compressed and chunked.</summary>
        public static void BroadcastLarge(byte[] payload) => SendLarge(payload, -1);

        /// <summary>Large message to one player (host) or -1 = everyone / the host (client).</summary>
        public static void SendLarge(byte[] payload, int playerId)
        {
            if (Transport == null) return;
            var packed = Compression.Pack(payload);
            int total = (packed.Length + ChunkSize - 1) / ChunkSize;
            int id = s_nextChunkId++;
            for (int i = 0; i < total; i++)
            {
                int len = Math.Min(ChunkSize, packed.Length - i * ChunkSize);
                var part = new byte[len];
                Buffer.BlockCopy(packed, i * ChunkSize, part, 0, len);
                var w = new Writer(Msg.Chunk).Int(id).Int(i).Int(total).Bytes(part);
                if (playerId >= 0 && IsHost) SendToPlayer(playerId, w, true); else Send(w, true);
            }
            Plugin.Log.LogInfo($"Sent large message: {payload.Length} bytes -> {packed.Length} packed, {total} chunks");
        }

        public static void SendChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (IsHost)
            {
                var line = $"{Local?.Name}: {text}";
                AddChat(line);
                Broadcast(new Writer(Msg.Chat).Str(line), true, -1);
            }
            else Send(new Writer(Msg.Chat).Str(text), true);
        }

        public static string CleanName(string n)
        {
            n = (n ?? "").Replace("\n", " ").Replace("<", "").Replace(">", "").Trim();
            return n.Length > 24 ? n.Substring(0, 24) : n;
        }

        /// <summary>Change my name (saved to config, sent to the host).</summary>
        public static void Rename(string name)
        {
            name = CleanName(name);
            if (name.Length == 0) return;
            Cfg.PlayerName.Value = name;
            NameOverride = name;
            if (IsHost && Local != null && Local.Name != name)
            {
                var line = $"* {Local.Name} теперь {name}";
                Local.Name = name;
                AddChat(line);
                Broadcast(new Writer(Msg.Chat).Str(line), true, -1);
                BroadcastRoster();
            }
            else if (IsClient) Send(new Writer(Msg.Rename).Str(name), true);
        }

        public static void RequestTeam(int team)
        {
            if (IsHost) { if (Local != null) Local.Team = team; BroadcastRoster(); }
            else Send(new Writer(Msg.TeamRequest).Int(team), true);
        }

        public static void SetPlayerTeam(int playerId, int team)
        {
            if (!IsHost || !Players.TryGetValue(playerId, out var p)) return;
            p.Team = team;
            BroadcastRoster();
        }

        public static void AddChat(string line)
        {
            ChatLog.Add(line);
            if (ChatLog.Count > 60) ChatLog.RemoveAt(0);
            Plugin.Log.LogInfo("[chat] " + line);
        }
    }
}
