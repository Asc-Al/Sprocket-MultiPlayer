using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SprocketMP
{
    public static class Cfg
    {
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> LastAddress;
        public static ConfigEntry<string> ToggleKey;
        public static ConfigEntry<int> SendRate;
        public static ConfigEntry<float> InterpDelay;
        public static ConfigEntry<string> MyTank;
        public static ConfigEntry<bool> MenuMultiplayer;
        public static ConfigEntry<bool> DebugLog;

        public static void Init(ConfigFile c)
        {
            PlayerName = c.Bind("General", "PlayerName", "", "Имя в лобби (пусто = имя пользователя Windows)");
            ToggleKey = c.Bind("General", "ToggleKey", "F8", "Клавиша окна мультиплеера (имя клавиши Unity Input System: F8, F9, Backquote...)");
            Port = c.Bind("Network", "Port", 27015, "UDP порт для хоста по IP");
            LastAddress = c.Bind("Network", "LastAddress", "127.0.0.1", "Последний адрес подключения");
            SendRate = c.Bind("Sync", "SendRate", 20, "Сколько раз в секунду отправлять состояние своей техники");
            MyTank = c.Bind("General", "MyTank", "", "Путь к чертежу своего танка для мультиплеера (пусто = танк от хоста)");
            MenuMultiplayer = c.Bind("General", "MenuMultiplayer", false, "В меню «Своё сражение» выбрана игра по сети");
            DebugLog = c.Bind("General", "DebugLog", false, "Подробный отладочный лог синхронизации");
            InterpDelay = c.Bind("Sync", "InterpolationDelay", 0.12f, "Задержка интерполяции чужой техники, сек");
        }
    }

    /// <summary>
    /// Core entry point. Loaded (and hot-reloaded) by the SprocketMP loader plugin via Assembly.Load(bytes),
    /// so the DLL on disk is never locked and can be replaced while the game is running.
    /// </summary>
    public static class Plugin
    {
        public const string Guid = "ascal.sprocketmp";
        public const string Version = "1.0.0";
        public static ManualLogSource Log;
        public static Harmony Harmony;

        // Called by the loader. Returns callbacks for Update / OnGUI / Quit.
        public static void Start(ManualLogSource log, ConfigFile config, out Action update, out Action onGui, out Action stop)
        {
            Log = log;
            // two game copies share one config file: retry if the other one is writing it right now
            for (int attempt = 0; ; attempt++)
            {
                try { Cfg.Init(config); break; }
                catch (System.IO.IOException) when (attempt < 10) { System.Threading.Thread.Sleep(150); }
            }
            try { Application.runInBackground = true; } catch { }
            Harmony = new Harmony(Guid + ".core." + DateTime.Now.Ticks);
            int ok = 0, fail = 0;
            foreach (var t in typeof(Plugin).Assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0))
            {
                try { Harmony.CreateClassProcessor(t).Patch(); ok++; }
                catch (Exception e) { fail++; Log.LogError($"Patch {t.Name} failed: {e.Message}\n{e.InnerException}"); }
            }
            Log.LogInfo($"Core {Version}: Harmony patches {ok} ok, {fail} failed");

            Net.Session.GameMessage = Game.BattleSync.OnMessage;
            Net.Session.PlayerLeft = Game.BattleSync.OnPlayerLeft;

            AutoReconnect.Restore();
            update = Update;
            onGui = OnGUI;
            stop = Stop;
        }

        static void Update()
        {
            try
            {
                UI.HandleHotkeys();
                Net.Session.Tick(Time.unscaledDeltaTime);
                AutoReconnect.Tick(Time.unscaledDeltaTime);
                Game.BattleSync.Update();
                Game.BattleSetup.Update();
                Game.MenuIntegration.Tick();
            }
            catch (Exception e) { Log.LogError("Update: " + e); }
        }

        static void OnGUI()
        {
            try { UI.Draw(); }
            catch (Exception e) { Log.LogError("OnGUI: " + e); }
        }

        static void Stop()
        {
            AutoReconnect.Save();
            try { Net.Session.Stop(); } catch (Exception e) { Log.LogWarning(e.ToString()); }
            try { Harmony?.UnpatchSelf(); } catch (Exception e) { Log.LogWarning(e.ToString()); }
            Log.LogInfo("Core stopped");
        }
    }
}
