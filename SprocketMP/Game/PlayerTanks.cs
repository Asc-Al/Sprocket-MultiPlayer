using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP.Game
{
    /// <summary>
    /// "Bring your own tank": a client picks one of its blueprints in the lobby, it is sent to the host,
    /// and the host puts it into the client's team when the battle starts. The spawned vehicle gets the
    /// name "<tank> [<player>]", which is how every machine finds out who drives it.
    /// </summary>
    public static class PlayerTanks
    {
        public struct Entry { public string Name; public byte[] Data; }
        public struct FileInfo { public string Path, Name, Source; }

        /// <summary>Host: tanks received from clients (player id -> blueprint).</summary>
        public static readonly Dictionary<int, Entry> Received = new Dictionary<int, Entry>();

        static int s_sentForId = -1;
        static string s_sentPath;

        // ------------------------------------------------------------------ client
        public static string SelectedPath => Cfg.MyTank.Value ?? "";

        public static string SelectedName
        {
            get
            {
                var p = SelectedPath;
                if (p.Length == 0) return null;
                var f = Files.FirstOrDefault(x => string.Equals(Full(x.Path), p, StringComparison.OrdinalIgnoreCase));
                return f.Name ?? System.IO.Path.GetFileNameWithoutExtension(p);
            }
        }

        static string Full(string p) { try { return System.IO.Path.GetFullPath(p); } catch { return p; } }

        public static void Select(string path)
        {
            try { if (!string.IsNullOrEmpty(path)) path = System.IO.Path.GetFullPath(path); } catch { }
            Cfg.MyTank.Value = path ?? "";
            s_sentPath = null; // resend
        }

        public static void Update()
        {
            if (!Session.IsClient || !Session.Connected) { s_sentForId = -1; return; }
            if (s_sentForId == Session.LocalId && s_sentPath == SelectedPath) return;
            s_sentForId = Session.LocalId;
            s_sentPath = SelectedPath;
            byte[] data = Array.Empty<byte>();
            string file = "";
            if (SelectedPath.Length > 0)
            {
                try { data = File.ReadAllBytes(SelectedPath); file = System.IO.Path.GetFileName(SelectedPath); }
                catch (Exception e) { Session.AddChat("* Не удалось прочитать чертёж: " + e.Message); data = Array.Empty<byte>(); }
            }
            Session.BroadcastLarge(new Writer(Msg.MyTank).Str(file).Bytes(data).ToArray());
        }

        // ------------------------------------------------------------------ host
        public static void OnMessage(int from, Reader r)
        {
            if (!Session.IsHost) return;
            r.Str();
            var data = r.Bytes();
            if (!Session.Players.TryGetValue(from, out var p)) return;
            if (data.Length == 0)
            {
                Received.Remove(from);
                if (p.Tank != "") { p.Tank = ""; Session.BroadcastRoster(); }
                return;
            }
            var name = HeaderName(data) ?? "Танк";
            Received[from] = new Entry { Name = name, Data = data };
            p.Tank = name;
            Session.BroadcastRoster();
            Plugin.Log.LogInfo($"{p.Name} brings {name}");
        }

        public static void OnPlayerLeft(int playerId) => Received.Remove(playerId);

        // ------------------------------------------------------------------ naming
        /// <summary>Text in brackets that identifies the player (name, plus id if two players share a name).</summary>
        public static string Tag(Player p)
        {
            bool dup = Session.Players.Values.Count(o => o.Name == p.Name) > 1;
            return dup ? $"{p.Name}#{p.Id}" : p.Name;
        }

        public static string VehicleName(string tank, Player p) => $"{tank} [{Tag(p)}]";

        static readonly Regex s_tagRx = new Regex(@"\[([^\[\]]+)\]");

        /// <summary>Player who brought this vehicle (by its name), or null.</summary>
        public static Player OwnerOf(string vehicleName)
        {
            if (string.IsNullOrEmpty(vehicleName)) return null;
            // Unity may append " (1)" etc. to the object name: take the last [...] anywhere in it
            var ms = s_tagRx.Matches(vehicleName);
            if (ms.Count == 0) return null;
            var tag = ms[ms.Count - 1].Groups[1].Value;
            return Session.Players.Values.FirstOrDefault(p => Tag(p) == tag);
        }

        static readonly Regex s_headerRx = new Regex("\"header\"\\s*:\\s*\\{[^{}]*?\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Singleline);

        static string Text(byte[] data)
        {
            var t = Encoding.UTF8.GetString(data);
            return t.Length > 0 && t[0] == '﻿' ? t.Substring(1) : t;
        }

        public static string HeaderName(byte[] data)
        {
            try
            {
                var m = s_headerRx.Match(Text(data));
                return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
            }
            catch { return null; }
        }

        /// <summary>Copy of the blueprint with header.name replaced.</summary>
        public static byte[] Rename(byte[] data, string newName)
        {
            var t = Text(data);
            var m = s_headerRx.Match(t);
            if (!m.Success) return data;
            var esc = newName.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var g = m.Groups[1];
            t = t.Substring(0, g.Index) + esc + t.Substring(g.Index + g.Length);
            return Encoding.UTF8.GetBytes(t);
        }

        // ------------------------------------------------------------------ local blueprint list
        static List<FileInfo> s_files;
        static float s_scannedAt = -100;

        public static List<FileInfo> Files
        {
            get
            {
                if (s_files == null || Time.unscaledTime - s_scannedAt > 10f) { s_files = Scan(); s_scannedAt = Time.unscaledTime; }
                return s_files;
            }
        }

        static List<FileInfo> Scan()
        {
            var list = new List<FileInfo>();
            void Add(string dir, string source)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    foreach (var f in Directory.GetFiles(dir, "*.blueprint"))
                    {
                        string name = null;
                        try
                        {
                            // header is at the top of the file: read just the beginning
                            using var fs = File.OpenRead(f);
                            var buf = new byte[Math.Min(4096, (int)fs.Length)];
                            fs.Read(buf, 0, buf.Length);
                            name = HeaderName(buf);
                        }
                        catch { }
                        list.Add(new FileInfo { Path = f, Name = name ?? System.IO.Path.GetFileNameWithoutExtension(f), Source = source });
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Blueprint scan " + dir + ": " + e.Message); }
            }

            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var factions = System.IO.Path.Combine(docs, "My Games", "Sprocket", "Factions");
                if (Directory.Exists(factions))
                    foreach (var fdir in Directory.GetDirectories(factions))
                        Add(System.IO.Path.Combine(fdir, "Blueprints", "Vehicles"), System.IO.Path.GetFileName(fdir));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Factions scan: " + e.Message); }
            try { Add(System.IO.Path.Combine(Application.streamingAssetsPath, "Blueprints", "Vehicles"), "Игра"); } catch { }
            return list.OrderBy(f => f.Source == "Игра" ? 1 : 0).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }
}
