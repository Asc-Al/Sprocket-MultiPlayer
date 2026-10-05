using System;
using System.Linq;
using SprocketMP.Game;
using SprocketMP.Net;
using UnityEngine;

namespace SprocketMP
{
    public static class UI
    {
        public static bool Visible = false;
        static string s_addr, s_port, s_name, s_lobbyId = "", s_chat = "";
        static bool s_init;
        static UnityEngine.InputSystem.Key s_toggle = UnityEngine.InputSystem.Key.F8;
        static float s_y;
        static Rect s_win;
        static GUIStyle s_box, s_label, s_small;

        static void Init()
        {
            if (s_init) return;
            s_init = true;
            s_addr = Cfg.LastAddress.Value;
            s_port = Cfg.Port.Value.ToString();
            s_name = Cfg.PlayerName.Value;
            if (Enum.TryParse<UnityEngine.InputSystem.Key>(Cfg.ToggleKey.Value, true, out var k)) s_toggle = k;
        }

        public static void HandleHotkeys()
        {
            Init();
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb[s_toggle].wasPressedThisFrame) Visible = !Visible;
            if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
            {
            }
            WindowTools.Tick();
            if (Visible) PollInput(); else { s_click = null; s_focus = -1; }
            if (Visible && (BattleSync.InBattle || BattleSync.InGameMode))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        static void Styles()
        {
            if (s_box != null) return;
            s_box = new GUIStyle(GUI.skin.box);
            s_label = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            s_small = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11, wordWrap = true };
        }

        // ---- tiny manual layout helpers (avoid GUILayout params arrays on IL2CPP) ----
        static float X => s_win.x + 10;
        static float W => s_win.width - 20;
        static void Label(string text, float h = 20, GUIStyle st = null) { GUI.Label(new Rect(X, s_y, W, h), text, st ?? s_label); s_y += h; }
        static bool Button(float x, float w, string text) => Btn(new Rect(x, s_y, w, 24), text);
        static void Space(float h = 6) => s_y += h;

        // ---- Input -------------------------------------------------------------------------------
        // IMGUI mouse/keyboard events are unreliable in this IL2CPP build, so we poll the Input System
        // in Update and do our own hit-testing against the rects drawn in OnGUI.
        static float s_scale = 1f;
        static Vector2? s_click;              // pending click in GUI (unscaled) coordinates
        static readonly System.Text.StringBuilder s_typed = new System.Text.StringBuilder();
        static int s_focus = -1, s_fieldCounter;

        public static bool Typing => Visible && s_focus >= 0;

        static void PollInput()
        {
            s_scale = Mathf.Max(1f, Screen.height / 1080f);
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                var mp = mouse.position.ReadValue();
                s_click = new Vector2(mp.x / s_scale, (Screen.height - mp.y) / s_scale);
            }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || s_focus < 0) return;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            if (ctrl && kb.vKey.wasPressedThisFrame) { s_typed.Append((GUIUtility.systemCopyBuffer ?? "").Replace("\n", "").Replace("\r", "")); return; }
            if (kb.backspaceKey.wasPressedThisFrame) s_typed.Append('\b');
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) s_typed.Append('\n');
            if (kb.escapeKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) { s_focus = -1; return; }
            if (ctrl) return;
            for (var k = UnityEngine.InputSystem.Key.A; k <= UnityEngine.InputSystem.Key.Z; k++)
                if (kb[k].wasPressedThisFrame) s_typed.Append((char)((shift ? 'A' : 'a') + (k - UnityEngine.InputSystem.Key.A)));
            for (var k = UnityEngine.InputSystem.Key.Digit1; k <= UnityEngine.InputSystem.Key.Digit0; k++)
                if (kb[k].wasPressedThisFrame) s_typed.Append(k == UnityEngine.InputSystem.Key.Digit0 ? '0' : (char)('1' + (k - UnityEngine.InputSystem.Key.Digit1)));
            for (var k = UnityEngine.InputSystem.Key.Numpad0; k <= UnityEngine.InputSystem.Key.Numpad9; k++)
                if (kb[k].wasPressedThisFrame) s_typed.Append((char)('0' + (k - UnityEngine.InputSystem.Key.Numpad0)));
            if (kb.periodKey.wasPressedThisFrame || kb.numpadPeriodKey.wasPressedThisFrame) s_typed.Append('.');
            if (kb.minusKey.wasPressedThisFrame) s_typed.Append(shift ? '_' : '-');
            if (kb.spaceKey.wasPressedThisFrame) s_typed.Append(' ');
            if (kb.slashKey.wasPressedThisFrame) s_typed.Append(shift ? '?' : '/');
            if (kb.commaKey.wasPressedThisFrame) s_typed.Append(',');
            if (kb.semicolonKey.wasPressedThisFrame) s_typed.Append(shift ? ':' : ';');
            if (shift && kb.digit1Key.wasPressedThisFrame) { s_typed.Length--; s_typed.Append('!'); }
        }

        static GUIStyle s_fill;
        static bool s_fillBroken;
        static void Fill(Rect r, Color c)
        {
            if (s_fillBroken) return;
            try
            {
                if (s_fill == null)
                {
                    s_fill = new GUIStyle();
                    s_fill.normal.background = Texture2D.whiteTexture;
                }
                var old = GUI.color;
                GUI.color = c;
                GUI.Box(r, "", s_fill);
                GUI.color = old;
            }
            catch (Exception e) { s_fillBroken = true; Plugin.Log.LogWarning("Fill disabled: " + e.Message); }
        }

        static bool IsRepaint => Event.current != null && Event.current.type == EventType.Repaint;

        static bool ConsumeClick(Rect r)
        {
            if (!IsRepaint || s_click == null || !r.Contains(s_click.Value)) return false;
            s_click = null;
            return true;
        }

        static bool Btn(Rect r, string text)
        {
            GUI.Button(r, text);
            return ConsumeClick(r);
        }

        static string Field(float x, float w, string v) => Field(x, w, v, out _);

        static string Field(float x, float w, string v, out bool enter)
        {
            enter = false;
            v ??= "";
            int id = s_fieldCounter++;
            var r = new Rect(x, s_y, w, 22);
            if (ConsumeClick(r)) { s_focus = id; s_typed.Clear(); }
            bool focused = s_focus == id;
            if (focused && IsRepaint && s_typed.Length > 0)
            {
                foreach (var c in s_typed.ToString())
                {
                    if (c == '\b') { if (v.Length > 0) v = v.Substring(0, v.Length - 1); }
                    else if (c == '\n') enter = true;
                    else v += c;
                }
                s_typed.Clear();
            }
            Fill(r, focused ? new Color(0.25f, 0.25f, 0.3f, 0.95f) : new Color(0.12f, 0.12f, 0.14f, 0.95f));
            bool caret = focused && ((int)(Time.unscaledTime * 2) % 2 == 0);
            GUI.Label(new Rect(x + 4, s_y + 1, w - 8, 22), (focused ? "<color=#ffff90>" : "") + Escape(v) + (caret ? "|" : "") + (focused ? "</color>" : ""), s_label);
            return v;
        }
        static string Escape(string v) => v.Replace("<", "\u2039").Replace(">", "\u203A");

        public static void Draw()
        {
            Init();
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s_scale, s_scale, 1f));
            DrawOverlay();
            if (!Visible) return;
            Styles();
            s_fieldCounter = 0;
            if (!Session.Active)
            {
                s_win = new Rect(20, 60, 460, 70);
                Fill(s_win, new Color(0.05f, 0.05f, 0.06f, 0.88f));
                s_y = s_win.y + 8;
                Label("<b>Мультиплеер</b>", 22);
                Label("Создать сервер или подключиться: «Своё сражение» → Игра: По сети.", 36, s_small);
                return;
            }
            s_win = new Rect(20, 60, 460, 300);
            Fill(s_win, new Color(0.05f, 0.05f, 0.06f, 0.88f));
            s_y = s_win.y + 8;
            Label("<b>Мультиплеер</b>   " + string.Join("  ", Session.Players.Values.OrderBy(p => p.Id).Select(p =>
                $"<color=#{ColorUtility.ToHtmlStringRGB(PlayerColor(p.Id))}>{Escape(p.Name)}</color>")), 22);
            DrawChatAndButtons();
        }

        static void DrawChatAndButtons()
        {
            float chatH = 170;
            var lines = Session.ChatLog.Skip(Math.Max(0, Session.ChatLog.Count - 9));
            Fill(new Rect(X, s_y, W, chatH), new Color(0.12f, 0.12f, 0.14f, 0.9f));
            GUI.Label(new Rect(X + 4, s_y + 2, W - 8, chatH - 4), string.Join("\n", lines), s_small);
            s_y += chatH + 4;
            s_chat = Field(X, W - 90, s_chat, out var chatEnter);
            if ((Button(X + W - 84, 84, "Отпр.") || chatEnter) && s_chat.Length > 0)
            {
                Session.SendChat(s_chat);
                s_chat = "";
            }
            s_y += 30;
            if (BattleSync.InGameMode && Button(X, 160, "Выйти в меню")) BattleSync.QuitToMenu();
            if (Session.IsHost && BattleSync.Active && Button(X + 170, 250, "Рестарт боя (для всех)")) BattleSync.HostRestart();
        }

        static bool s_picker;
        static int s_page;

        static void DrawTankPicker()
        {
            const int perPage = 14;
            var files = PlayerTanks.Files;
            var r = new Rect(s_win.xMax + 8, s_win.y, 340, 60 + perPage * 26);
            Fill(r, new Color(0.05f, 0.05f, 0.06f, 0.92f));
            float y = r.y + 8, x = r.x + 10;
            GUI.Label(new Rect(x, y, 320, 22), "<b>Свой танк</b> (заменит один танк твоей команды)", s_small);
            y += 24;
            if (Btn(new Rect(x, y, 320, 22), "— Танк от хоста —")) { PlayerTanks.Select(""); s_picker = false; }
            y += 26;
            int pages = Math.Max(1, (files.Count + perPage - 1) / perPage);
            s_page = Mathf.Clamp(s_page, 0, pages - 1);
            var sel = PlayerTanks.SelectedPath;
            foreach (var f in files.Skip(s_page * perPage).Take(perPage))
            {
                bool cur = string.Equals(f.Path, sel, StringComparison.OrdinalIgnoreCase);
                string label = (cur ? "▶ " : "") + f.Name + (f.Source == "Игра" ? "  (стандартный)" : "");
                if (Btn(new Rect(x, y, 320, 22), label)) { PlayerTanks.Select(f.Path); s_picker = false; }
                y += 24;
            }
            y = r.yMax - 30;
            if (pages > 1)
            {
                if (Btn(new Rect(x, y, 60, 22), "<")) s_page = (s_page + pages - 1) % pages;
                GUI.Label(new Rect(x + 70, y, 120, 22), $"{s_page + 1} / {pages}", s_small);
                if (Btn(new Rect(x + 130, y, 60, 22), ">")) s_page = (s_page + 1) % pages;
            }
            if (files.Count == 0) GUI.Label(new Rect(x, r.y + 60, 320, 40), "Чертежи не найдены", s_small);
        }

        static void Apply(int playerId, int team)
        {
            if (Session.IsHost) Session.SetPlayerTeam(playerId, team);
            else Session.RequestTeam(team);
        }

        static GUIStyle s_plate, s_plateShadow, s_banner;

        static readonly Color[] s_palette =
        {
            new Color(0.45f, 0.85f, 1f), new Color(0.55f, 1f, 0.5f), new Color(1f, 0.8f, 0.35f), new Color(1f, 0.55f, 0.85f),
            new Color(0.75f, 0.6f, 1f), new Color(0.4f, 1f, 0.85f), new Color(1f, 0.6f, 0.45f), new Color(0.85f, 1f, 0.4f),
        };

        public static Color PlayerColor(int id) => s_palette[((id % s_palette.Length) + s_palette.Length) % s_palette.Length];

        static void DrawNameplates()
        {
            if (!Session.Active || !BattleSync.InBattle) return;
            Camera cam = null;
            try { cam = Camera.main; } catch { }
            if (cam == null) return;
            if (s_plate == null)
            {
                s_plate = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 15, richText = false };
                s_plateShadow = new GUIStyle(s_plate);
                s_plateShadow.normal.textColor = new Color(0, 0, 0, 0.8f);
            }
            var camPos = cam.transform.position;
            foreach (var (pos, playerId) in BattleSync.AllyPlates())
            {
                var sp = cam.WorldToScreenPoint(pos);
                if (sp.z <= 0.5f) continue;
                if (!Session.Players.TryGetValue(playerId, out var p)) continue;
                float dist = Vector3.Distance(camPos, pos);
                float alpha = Mathf.Clamp01(1.2f - dist / 1500f);
                if (alpha <= 0.05f) continue;
                float x = sp.x / s_scale, y = (Screen.height - sp.y) / s_scale;
                var r = new Rect(x - 120, y - 24, 240, 22);
                var c = PlayerColor(playerId); c.a = alpha;
                s_plateShadow.normal.textColor = new Color(0, 0, 0, 0.8f * alpha);
                GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), p.Name, s_plateShadow);
                s_plate.normal.textColor = c;
                GUI.Label(r, p.Name, s_plate);
            }
        }

        static void DrawOverlay()
        {
            try { DrawNameplates(); } catch (Exception e) { Plugin.Log.LogWarning("Nameplates: " + e.Message); }
            try
            {
                if (MenuIntegration.ScreenOpen && MenuIntegration.MatchRunning)
                {
                    if (s_banner == null) s_banner = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 26, fontStyle = FontStyle.Bold, richText = true };
                    float w = Screen.width / s_scale;
                    var r = new Rect(w / 2 - 300, 22, 600, 40);
                    Fill(r, new Color(0, 0, 0, 0.55f));
                    GUI.Label(r, "<color=#ffcc55>Матч идёт</color>  <size=16>— хост уже в бою</size>", s_banner);
                }
            }
            catch { }
            if (!Session.Active || !BattleSync.InBattle || Visible) return;
            Styles();

        }
    }
}
