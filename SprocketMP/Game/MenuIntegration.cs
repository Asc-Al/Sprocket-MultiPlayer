using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.CustomBattles;
using SprocketMP.Net;
using UnityEngine;
using UnityEngine.Events;

namespace SprocketMP.Game
{
    /// <summary>
    /// Multiplayer controls built into the game's own "Custom Battle" screen:
    ///  * right column (under Map / Clouds / Fog): Single / Multiplayer, IP, port, host / join, chat;
    ///  * bottom panel: players in the lobby;
    ///  * team cards: player names instead of PLAYER / AI, the ⇄ button switches my team.
    /// </summary>
    public static class MenuIntegration
    {
        public static bool Multiplayer
        {
            get => Cfg.MenuMultiplayer.Value || Session.Active;
            set => Cfg.MenuMultiplayer.Value = value;
        }

        static string s_joinAddr, s_port;
        static DynamicGUI.LabelField s_chatLabel;
        static DynamicGUI.InputField s_chatInput;
        static int s_chatShown = -1;
        static string s_stateKey = "", s_rosterKey = "";
        static MapConfigurator s_map;
                static TeamSelector s_selector;

        const int ChatLines = 8;

        static T D<T>(Delegate d) where T : Il2CppSystem.Delegate => DelegateSupport.ConvertDelegate<T>(d);

        static Il2CppSystem.Collections.Generic.IReadOnlyList<string> Options(params string[] items)
        {
            var l = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var i in items) l.Add(i);
            return l.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyList<string>>();
        }

        static void Button(DynamicGUI.DynamicGUILayout l, string text, Action a)
        {
            var tt = new Sprocket.UI.UITooltip();
            l.Button(text, D<UnityAction>(a), ref tt);
        }

        static T LastElement<T>(DynamicGUI.DynamicGUILayout l) where T : Il2CppSystem.Object
        {
            try
            {
                var els = l.activeElements;
                if (els == null || els.Count == 0) return null;
                return els[els.Count - 1]?.TryCast<T>();
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ right column
        public static void DrawMapColumn(MapConfigurator mc, Sprocket.UI.IGUILayout layout)
        {
            s_map = mc;
            var l = layout?.TryCast<DynamicGUI.DynamicGUILayout>();
            if (l == null) return;
            s_chatLabel = null; s_chatInput = null; s_chatShown = -1;
            s_joinAddr ??= Cfg.LastAddress.Value;
            s_port ??= Cfg.Port.Value.ToString();

            l.AddSpace(10);
            l.Dropdown("Игра", Options("Одиночная", "По сети"), Multiplayer ? 1 : 0, D<UnityAction<int>>(new Action<int>(i =>
            {
                bool mp = i == 1;
                if (!mp && Session.Active) { BattleSync.EndBattle(); Session.Stop(); }
                Multiplayer = mp;
                RequestRepaint();
            })), "Одиночная игра или игра по сети (по IP)");
            if (!Multiplayer) { s_stateKey = StateKey(); return; }

            if (!Session.Active)
            {
                var ips = LocalAddresses();
                l.InfoField("Твой IP: " + (ips.Count > 0 ? string.Join(", ", ips) : "не найден"), 2);
                if (ips.Count > 0) Button(l, "Скопировать мой IP", () => { GUIUtility.systemCopyBuffer = ips[0]; Session.AddChat("* IP скопирован"); });
                l.InputField("Порт", s_port, D<UnityAction<string>>(new Action<string>(v => s_port = v)));
                Button(l, "Создать сервер", () =>
                {
                    int port = int.TryParse(s_port, out var p) && p > 0 ? p : 27015;
                    Cfg.Port.Value = port;
                    Session.HostIP(port);
                    RequestRepaint();
                });
                l.AddSpace(6);
                l.InputField("IP сервера для подключения", s_joinAddr, D<UnityAction<string>>(new Action<string>(v => s_joinAddr = v)));
                Button(l, "Подключиться", () =>
                {
                    int port = int.TryParse(s_port, out var p) && p > 0 ? p : 27015;
                    var addr = (s_joinAddr ?? "").Trim();
                    if (addr.Length == 0) return;
                    Cfg.LastAddress.Value = addr; Cfg.Port.Value = port;
                    Session.JoinIP(addr, port);
                    RequestRepaint();
                });
                if (!string.IsNullOrEmpty(Session.LastError)) l.InfoField(Session.LastError, 2, new Color(1f, 0.45f, 0.45f));
            }
            else
            {
                if (Session.IsHost)
                {
                    var ips = LocalAddresses();
                    l.InfoField($"Сервер: {(ips.Count > 0 ? ips[0] : "?")} : {Cfg.Port.Value}", 1);
                    if (ips.Count > 0) Button(l, "Скопировать мой IP", () => { GUIUtility.systemCopyBuffer = $"{ips[0]}"; Session.AddChat("* IP скопирован"); });
                    MyTankRows(l);
                    Button(l, "Закрыть сервер", () => { BattleSync.EndBattle(); Session.Stop(); RequestRepaint(); });
                }
                else
                {
                    l.InfoField(Session.Connected ? $"Подключено к {Cfg.LastAddress.Value}" : "Подключение...", 1);
                    MyTankRows(l);
                    Button(l, "Отключиться", () => { BattleSync.EndBattle(); Session.Stop(); RequestRepaint(); });
                }

                DrawPlayerRows(l);
                l.AddSpace(8);
                l.Header("Чат");
                l.InfoField(ChatText(), ChatLines);
                s_chatLabel = LastElement<DynamicGUI.LabelField>(l);
                s_chatShown = Session.ChatLog.Count;
                l.InputField("Сообщение (Enter — отправить)", "", D<UnityAction<string>>(new Action<string>(OnChatEdit)));
                s_chatInput = LastElement<DynamicGUI.InputField>(l);
            }
            s_stateKey = StateKey();
            s_rosterKey = RosterKey();
        }

        static void OnChatEdit(string text) { }

        static bool s_chatWasFocused;

        static bool ChatFocused
        {
            get { try { return s_chatInput != null && !s_chatInput.WasCollected && s_chatInput.inputField != null && s_chatInput.inputField.isFocused; } catch { return false; } }
        }

        /// <summary>Every frame: Enter in the chat box sends the message (TMP ends editing on Enter, we read the text).</summary>
        public static void Tick()
        {
            PollNameClick();
            if (s_focusNameNext && s_nameInput != null)
            {
                s_focusNameNext = false;
                try { s_nameInput.inputField.ActivateInputField(); s_nameInput.inputField.Select(); } catch { }
            }
            if (s_chatInput == null) return;
            bool focused = ChatFocused;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool enter = kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame);
            if (enter && (focused || s_chatWasFocused))
            {
                try
                {
                    var f = s_chatInput.inputField;
                    var text = f.text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Session.SendChat(text.Trim());
                        f.SetTextWithoutNotify("");
                    }
                    f.ActivateInputField();
                    f.Select();
                    focused = true;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Chat send: " + e.Message); }
            }
            s_chatWasFocused = focused;
        }

        const int ChatWrap = 40;

        static string ChatText()
        {
            // newest messages that fit into ChatLines visual lines (long ones wrap)
            var picked = new List<string>();
            int used = 0;
            for (int i = Session.ChatLog.Count - 1; i >= 0; i--)
            {
                var m = Session.ChatLog[i];
                int h = Math.Max(1, (m.Length + ChatWrap - 1) / ChatWrap);
                if (used + h > ChatLines) break;
                used += h;
                picked.Insert(0, m);
            }
            return string.Join("\n", picked);
        }

        // ------------------------------------------------------------------ bottom panel: players
        static void MyTankRows(DynamicGUI.DynamicGUILayout l)
        {
            var me = Session.Local;
            string tn = me != null && me.PickTeam >= 0 && !string.IsNullOrEmpty(me.Tank) ? $"{me.Tank} (команда {me.PickTeam + 1})" : "не выбран";
            l.InfoField("Мой танк: " + tn, 1, new Color(0.95f, 0.85f, 0.55f));
        }

        static bool s_editingName;
        static DynamicGUI.LabelField s_myNameLabel;
        static DynamicGUI.InputField s_nameInput;

        static void DrawPlayerRows(DynamicGUI.DynamicGUILayout l)
        {
            s_myNameLabel = null; s_nameInput = null;
            l.AddSpace(8);
            l.Header($"Игроки ({Session.Players.Count})");
            foreach (var p in Session.Players.Values.OrderBy(p => p.Id))
            {
                int team = EffectiveTeam(p);
                var bits = new List<string> { "команда " + (team >= 0 ? (team + 1).ToString() : "?") };
                if (!string.IsNullOrEmpty(p.Tank)) bits.Add(p.Tank);
                if (p.Id == 0) bits.Add("хост"); else if (p.Ping > 0) bits.Add(p.Ping + " мс");
                string info = string.Join(" · ", bits);
                bool me = p.Id == Session.LocalId;
                if (me && s_editingName)
                {
                    l.InputField("Ник", p.Name, D<UnityAction<string>>(new Action<string>(v =>
                    {
                        s_editingName = false;
                        var n = Session.CleanName(v);
                        if (n.Length > 0 && n != p.Name) Session.Rename(n);
                        RequestRepaint();
                    })));
                    s_nameInput = LastElement<DynamicGUI.InputField>(l);
                    s_focusNameNext = true;
                    continue;
                }
                string col = ColorUtility.ToHtmlStringRGB(UI.PlayerColor(p.Id));
                l.InfoField($"<color=#{col}><b>{p.Name}</b></color>   <color=#a8a8a8>{info}</color>", 1);
                if (me) s_myNameLabel = LastElement<DynamicGUI.LabelField>(l);
            }
        }

        /// <summary>Click on my own name in the player list turns it into an edit box.</summary>
        static void PollNameClick()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;
            if (s_myNameLabel == null || s_editingName) return;
            try
            {
                if (s_myNameLabel.WasCollected || !s_myNameLabel.gameObject.activeInHierarchy) return;
                var rt = s_myNameLabel.GetComponent<RectTransform>();
                var canvas = s_myNameLabel.GetComponentInParent<Canvas>();
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rt, mouse.position.ReadValue(), cam)) return;
                s_editingName = true;
                RequestRepaint();
                s_focusNameNext = true;
            }
            catch { }
        }

        static bool s_focusNameNext;

        // ------------------------------------------------------------------ team cards
        static int HostTeamIndex(TeamSelector ts)
        {
            try
            {
                for (int i = 0; i < ts.TeamCount; i++)
                    if ((ts.GetTeamFlag(i) & TeamDefinitionFlags.Player) != 0) return i;
            }
            catch { }
            return 0;
        }

        /// <summary>Team index a player will be in (host: the team with the "Player" flag; auto = the other one).</summary>
        public static int EffectiveTeam(Player p)
        {
            Session.Players.TryGetValue(0, out var host);
            int hostTeam = host != null && host.Team >= 0 ? host.Team : 0;
            if (p.Id == 0) return hostTeam;
            return p.Team >= 0 ? p.Team : (hostTeam == 0 ? 1 : 0);
        }

        public static void UpdateTeamCards(TeamSelector ts)
        {
            s_selector = ts;
            if (!Multiplayer) return;
            if (Session.IsHost && Session.Players.TryGetValue(0, out var me))
            {
                int ht = HostTeamIndex(ts);
                if (me.PickTeam >= 0 && me.PickTeam != ht) { SetHostTeam(ts, me.PickTeam); ht = me.PickTeam; }
                if (me.Team != ht) { me.Team = ht; Session.BroadcastRoster(); }
            }
            var cards = ts.teams;
            if (cards == null) return;
            for (int c = 0; c < cards.Length; c++)
            {
                var card = cards[c];
                if (card == null) continue;
                int idx = card.TeamIndex;
                var names = Session.Players.Values.Where(p => EffectiveTeam(p) == idx).OrderBy(p => p.Id)
                    .Select(p => $"<color=#{ColorUtility.ToHtmlStringRGB(UI.PlayerColor(p.Id))}>{p.Name}</color>").ToList();
                string text = names.Count > 0 ? string.Join(", ", names) : "ИИ";
                try
                {
                    var tr = card.transform.Find("Controller");
                    var tmp = tr != null ? tr.GetComponent<TMPro.TMP_Text>() : null;
                    if (tmp != null) { tmp.richText = true; if (tmp.text != text) tmp.text = text; }
                }
                catch { }
            }
        }

        static void SetHostTeam(TeamSelector ts, int team)
        {
            try
            {
                for (int i = 0; i < ts.TeamCount; i++)
                {
                    var f = ts.GetTeamFlag(i);
                    f = i == team ? (f | TeamDefinitionFlags.Player) : (f & ~TeamDefinitionFlags.Player);
                    ts.SetTeamFlag(i, f);
                }
                ts.MarkConfigurationChanged();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Switch host team: " + e.Message); }
        }

        /// <summary>⇄ button: in multiplayer it moves me to the other team instead of swapping attacker/defender.</summary>
        public static bool OnToggleStance(TeamSelector ts)
        {
            if (!Multiplayer || !Session.Active) return true;
            var me = Session.Local;
            if (me == null) return false;
            int cur = EffectiveTeam(me);
            int other = cur == 0 ? 1 : 0;
            if (me.PickTeam >= 0) LobbySync.Pick(null);   // switching team drops the picked vehicle
            if (Session.IsHost)
            {
                SetHostTeam(ts, other);
                me.Team = other;
                Session.BroadcastRoster();
            }
            else Session.RequestTeam(other);
            UpdateTeamCards(ts);
            return false;
        }

        // ------------------------------------------------------------------ per-frame
        static float s_tick;

        public static void MenuUpdate(CustomBattleCreation cbc)
        {
            s_tick += Time.unscaledDeltaTime;
            // live chat without rebuilding the column (keeps the input focused)
            if (s_chatLabel != null && s_chatShown != Session.ChatLog.Count)
            {
                s_chatShown = Session.ChatLog.Count;
                try { s_chatLabel.SetText(ChatText()); } catch { s_chatLabel = null; }
            }
            if (s_tick < 0.25f) return;
            s_tick = 0;
            if (s_map == null || s_map.WasCollected || s_selector == null || s_selector.WasCollected)
            {
                // first frame of the screen (or after a mod reload): find the modules and force our rows in
                s_map = UnityEngine.Object.FindObjectOfType<MapConfigurator>();
                s_selector = UnityEngine.Object.FindObjectOfType<TeamSelector>();
                LobbySync.SetModule(s_selector);
                if (s_map != null) { try { BattleSync.CleanupEndScreens(); } catch { } }   // leftovers from a battle block the menu
                RequestRepaint();
            }
            if (Multiplayer)
            {
                try { LobbySync.HostTick(); LobbySync.ClientTick(); } catch (Exception e) { Plugin.Log.LogWarning("Lobby: " + e.Message); }
            }

            if (s_map != null && StateKey() != s_stateKey) Repaint(s_map);
            if (s_map != null && RosterKey() != s_rosterKey && !ChatFocused) Repaint(s_map);
            if (s_selector != null) { try { UpdateTeamCards(s_selector); } catch { } }
            try { UpdateHighlight(); } catch (Exception e) { Plugin.Log.LogWarning("Highlight: " + e.Message); }

            // clients don't start battles: the host does
            try
            {
                var btn = cbc.confirmButton;
                if (btn != null)
                {
                    bool show = !(Multiplayer && Session.IsClient);
                    if (btn.gameObject.activeSelf != show) btn.gameObject.SetActive(show);
                }
            }
            catch { }
        }

        static void RequestRepaint() { s_stateKey = "?"; s_rosterKey = "?"; }

        static void Repaint(Component module)
        {
            try
            {
                var insp = module.GetComponentInChildren<BattleConfigInspector>(true) ?? module.GetComponentInParent<BattleConfigInspector>();
                if (insp == null) insp = FindInspectorFor(module);
                insp?.Repaint();
            }
            catch (Exception e) { Plugin.Log.LogWarning("Repaint: " + e.Message); }
        }

        static BattleConfigInspector FindInspectorFor(Component module)
        {
            foreach (var insp in UnityEngine.Object.FindObjectsOfType<BattleConfigInspector>())
            {
                try
                {
                    var arr = insp.inspectors;
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Length; i++) if (arr[i] != null && arr[i].Pointer == module.Pointer) return insp;
                }
                catch { }
            }
            return null;
        }

        static string StateKey() => $"{Multiplayer}|{Session.Role}|{Session.Connected}|{Session.LastError}";

        static string RosterKey() => Multiplayer + "|" + string.Join(";", Session.Players.Values.OrderBy(p => p.Id).Select(p => $"{p.Id},{p.Name},{p.Team},{p.Tank},{p.PickTeam},{p.InBattle}"));

        static string s_lastClickPath;
        static float s_lastClickTime = -10f;

        static string FullPath(string p) { try { return string.IsNullOrEmpty(p) ? "" : System.IO.Path.GetFullPath(p); } catch { return p ?? ""; } }

        /// <summary>Roster click. Returns true to let the game remove the vehicle (host double click).</summary>
        public static bool OnRosterClick(UnitDefinition ud)
        {
            if (ud == null || !Multiplayer || !Session.Active) return true;
            var key = (ud.Name ?? "") + "|" + LobbySync.ActiveTeam;
            float now = Time.unscaledTime;
            if (key == s_lastClickPath && now - s_lastClickTime < 0.45f)
            {
                s_lastClickPath = null;
                if (Session.IsHost) return true;          // double click: host removes it right here
                LobbySync.RequestRemove(ud);              // client: ask the host
                return false;
            }
            s_lastClickPath = key;
            s_lastClickTime = now;
            var me = Session.Local;
            if (me == null || me.Tank != ud.Name || me.PickTeam != LobbySync.ActiveTeam) LobbySync.Pick(ud);
            return false;
        }

        /// <summary>Click on a vehicle in the list on the left. Returns true to let the game add it locally.</summary>
        public static bool OnListClick(UnitDefinition ud)
        {
            if (ud == null || !Multiplayer || !Session.IsClient) return true;
            LobbySync.RequestAdd(ud);
            return false;
        }

        // outlines: my pick gold, other players' picks in their colour
        static void UpdateHighlight()
        {
            var roster = UnityEngine.Object.FindObjectOfType<Roster>();
            var cards = roster?.display?.cards;
            if (cards == null) return;
            int team = LobbySync.ActiveTeam;
            var pickers = new Dictionary<string, Queue<Player>>();
            if (Multiplayer && Session.Active)
                foreach (var p in Session.Players.Values.Where(p => p.PickTeam == team && !string.IsNullOrEmpty(p.Tank)).OrderBy(p => p.Id))
                {
                    if (!pickers.TryGetValue(p.Tank, out var q)) pickers[p.Tank] = q = new Queue<Player>();
                    q.Enqueue(p);
                }
            for (int i = 0; i < cards.Length; i++)
            {
                var c = cards[i];
                if (c == null) continue;
                Player who = null;
                try
                {
                    var d = c.displayed;
                    if (d != null && c.gameObject.activeInHierarchy && pickers.TryGetValue(d.Name ?? "", out var q) && q.Count > 0) who = q.Dequeue();
                }
                catch { }
                var o = c.GetComponent<UnityEngine.UI.Outline>();
                if (o == null)
                {
                    if (who == null) continue;
                    o = c.gameObject.AddComponent(Il2CppType.Of<UnityEngine.UI.Outline>()).Cast<UnityEngine.UI.Outline>();
                }
                if (who != null)
                {
                    bool me = who.Id == Session.LocalId;
                    o.effectColor = me ? new Color(1f, 0.78f, 0.2f, 1f) : UI.PlayerColor(who.Id);
                    o.effectDistance = me ? new Vector2(5f, -5f) : new Vector2(3f, -3f);
                }
                if (o.enabled != (who != null)) o.enabled = who != null;
            }
        }

        public static bool ScreenOpen
        {
            get { try { return s_map != null && !s_map.WasCollected && s_map.isActiveAndEnabled; } catch { return false; } }
        }

        /// <summary>Host is in a battle while I'm in the menu.</summary>
        public static bool MatchRunning =>
            Multiplayer && Session.IsClient && !BattleSync.InGameMode &&
            Session.Players.TryGetValue(0, out var h) && h.InBattle;

        // ------------------------------------------------------------------ helpers
        static List<string> s_ips;
        static float s_ipsAt = -100;

        public static List<string> LocalAddresses()
        {
            if (s_ips != null && Time.unscaledTime - s_ipsAt < 30) return s_ips;
            var list = new List<(string ip, int prio)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ua.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;
                        string n = (ni.Name + " " + ni.Description).ToLowerInvariant();
                        int prio = n.Contains("radmin") || s.StartsWith("26.") ? 0 : n.Contains("zerotier") || n.Contains("hamachi") ? 1 : 2;
                        list.Add((s, prio));
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("IP list: " + e.Message); }
            s_ips = list.OrderBy(x => x.prio).Select(x => x.ip).Distinct().Take(3).ToList();
            s_ipsAt = Time.unscaledTime;
            return s_ips;
        }
    }

    // ---------------------------------------------------------------------- patches
    [HarmonyPatch(typeof(MapConfigurator), nameof(MapConfigurator.DrawGUI))]
    static class Patch_MapDrawGUI
    {
        static void Postfix(MapConfigurator __instance, Sprocket.UI.IGUILayout layout)
        {
            try { MenuIntegration.DrawMapColumn(__instance, layout); }
            catch (Exception e) { Plugin.Log.LogError("Menu (map column): " + e); }
        }
    }

    // Multiplayer: one click on a vehicle in the team roster picks it as "my tank", a double click removes it.
    [HarmonyPatch(typeof(Roster), nameof(Roster.OnUnitMoved))]
    static class Patch_RosterClick
    {
        static bool Prefix(UnitDefinition arg2)
        {
            try { return MenuIntegration.OnRosterClick(arg2); }
            catch (Exception e) { Plugin.Log.LogError("Menu (roster click): " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(UnitSelection), nameof(UnitSelection.OnUnitMoved))]
    static class Patch_ListClick
    {
        static bool Prefix(UnitDefinition arg2)
        {
            try { return MenuIntegration.OnListClick(arg2); }
            catch (Exception e) { Plugin.Log.LogError("Menu (list click): " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(TeamSelector), nameof(TeamSelector.ModuleUpdate))]
    static class Patch_TeamSelectorUpdate
    {
        static void Postfix(TeamSelector __instance)
        {
            try { MenuIntegration.UpdateTeamCards(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Menu (team cards): " + e); }
        }
    }

    [HarmonyPatch(typeof(TeamSelector), nameof(TeamSelector.ToggleTeamStance))]
    static class Patch_TeamToggle
    {
        static bool Prefix(TeamSelector __instance)
        {
            try { return MenuIntegration.OnToggleStance(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Menu (toggle): " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(CustomBattleCreation), nameof(CustomBattleCreation.MenuUpdate))]
    static class Patch_CBCUpdate
    {
        static void Postfix(CustomBattleCreation __instance)
        {
            try { MenuIntegration.MenuUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("Menu (update): " + e); }
        }
    }
}
