using System;
using System.IO;
using SprocketMP.Net;

namespace SprocketMP
{
    /// <summary>
    /// Dev convenience: when the core is hot-reloaded, remember the IP session and restore it
    /// (host re-opens the server and re-syncs the running battle when the client is back).
    /// </summary>
    public static class AutoReconnect
    {
        static string FilePath => Path.Combine(BepInEx.Paths.CachePath, "SprocketMP", "reconnect-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".txt");
        public static bool PendingResync;
        static float s_resyncDelay;

        public static void Save()
        {
            try
            {
                string role = null;
                if (Session.Transport is LnlTransport && Session.Active) role = Session.IsHost ? "host" : "client";
                else if (s_reconnectAddr != null) role = "client";
                if (role == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, $"{role}\n{Cfg.LastAddress.Value}\n{Cfg.Port.Value}\n{DateTime.UtcNow.Ticks}\n{Session.Local?.Name ?? Session.NameOverride ?? ""}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("AutoReconnect.Save: " + e.Message); }
        }

        public static void Restore()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath);
                File.Delete(FilePath);
                if (lines.Length < 4) return;
                var age = DateTime.UtcNow - new DateTime(long.Parse(lines[3]));
                if (age.TotalSeconds > 30) return;
                int port = int.Parse(lines[2]);
                if (lines.Length > 4 && lines[4].Length > 0) Session.NameOverride = lines[4];
                if (lines[0] == "host") { Session.HostIP(port); PendingResync = Game.BattleSync.InGameMode; }
                else ClientLost(lines[1], port);
                Plugin.Log.LogInfo("AutoReconnect: restoring " + lines[0]);
            }
            catch (Exception e) { Plugin.Log.LogWarning("AutoReconnect.Restore: " + e.Message); }
        }

        static string s_reconnectAddr; static int s_reconnectPort, s_attempts; static float s_reconnectDelay;

        /// <summary>Client lost the host (or the mod was reloaded): keep trying to reconnect for ~40 s.</summary>
        public static void ClientLost(string addr, int port)
        {
            if (s_reconnectAddr != null) return;
            s_reconnectAddr = addr; s_reconnectPort = port; s_reconnectDelay = 2f; s_attempts = 0;
        }

        public static void Tick(float dt)
        {
            if (s_reconnectAddr != null)
            {
                if (Session.IsClient && Session.Connected) { s_reconnectAddr = null; }
                else
                {
                    s_reconnectDelay -= dt;
                    if (s_reconnectDelay <= 0)
                    {
                        if (++s_attempts > 10) { s_reconnectAddr = null; Session.AddChat("* Не удалось переподключиться"); }
                        else { Plugin.Log.LogInfo($"Reconnecting ({s_attempts})"); Session.JoinIP(s_reconnectAddr, s_reconnectPort); s_reconnectDelay = 4f; }
                    }
                }
            }
            if (PendingResync && Session.IsHost && Session.RemoteCount > 0 && Game.BattleSync.InGameMode)
            {
                s_resyncDelay += dt;
                if (s_resyncDelay > 1.5f) { PendingResync = false; s_resyncDelay = 0; Game.BattleSync.HostResync(); }
            }
        }
    }
}
