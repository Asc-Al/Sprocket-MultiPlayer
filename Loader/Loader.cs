using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SprocketMP.Loader
{
    /// <summary>
    /// Thin, stable plugin. Loads core/SprocketMP.Core.dll from bytes and reloads it when the file changes
    /// (or on Ctrl+F10), so the mod can be updated without restarting Sprocket.
    /// </summary>
    [BepInPlugin("ascal.sprocketmp", "Sprocket Multiplayer", "0.1.0")]
    public class LoaderPlugin : BasePlugin
    {
        internal static LoaderPlugin I;
        static string s_corePath;
        static DateTime s_loadedStamp;
        static Action s_update, s_gui, s_stop;
        static float s_checkTimer;
        static bool s_reloadRequested;

        public override void Load()
        {
            I = this;
            var dir = Path.GetDirectoryName(typeof(LoaderPlugin).Assembly.Location);
            s_corePath = Path.Combine(dir, "core", "SprocketMP.Core.bin");
            LoadCore();
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<MPBehaviour>();
                var go = new GameObject("SprocketMP");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<MPBehaviour>();
            }
            catch (Exception e) { Log.LogError("Failed to create MPBehaviour: " + e); }
        }

        static void LoadCore()
        {
            try
            {
                if (!File.Exists(s_corePath)) { I.Log.LogError("Core not found: " + s_corePath); return; }
                s_stop?.Invoke();
                s_update = s_gui = s_stop = null;
                var stamp = File.GetLastWriteTimeUtc(s_corePath);
                var bytes = File.ReadAllBytes(s_corePath);
                var asm = Assembly.Load(bytes);
                var entry = asm.GetType("SprocketMP.Plugin", true);
                var start = entry.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
                var args = new object[] { I.Log, I.Config, null, null, null };
                start.Invoke(null, args);
                s_update = (Action)args[2]; s_gui = (Action)args[3]; s_stop = (Action)args[4];
                s_loadedStamp = stamp;
                I.Log.LogInfo($"Core loaded ({bytes.Length} bytes, {stamp:HH:mm:ss}). Ctrl+F10 = перезагрузка, авто при замене файла.");
            }
            catch (Exception e) { I.Log.LogError("Core load failed: " + e); }
        }

        internal static void Tick()
        {
            s_checkTimer += Time.unscaledDeltaTime;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f10Key.wasPressedThisFrame && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)) s_reloadRequested = true;
            if (s_checkTimer > 1f)
            {
                s_checkTimer = 0;
                try
                {
                    if (File.Exists(s_corePath))
                    {
                        var st = File.GetLastWriteTimeUtc(s_corePath);
                        // wait until the file is at least 1s old so we don't read a half-written dll
                        if (st != s_loadedStamp && (DateTime.UtcNow - st).TotalSeconds > 1) s_reloadRequested = true;
                    }
                }
                catch { }
            }
            if (s_reloadRequested) { s_reloadRequested = false; I.Log.LogInfo("Reloading core..."); LoadCore(); }
            s_update?.Invoke();
        }

        internal static void Gui() => s_gui?.Invoke();
        internal static void Quit() { try { s_stop?.Invoke(); } catch { } }
    }

    public class MPBehaviour : MonoBehaviour
    {
        public MPBehaviour(IntPtr ptr) : base(ptr) { }
        void Update() { try { LoaderPlugin.Tick(); } catch (Exception e) { LoaderPlugin.I.Log.LogError(e.ToString()); } }
        void OnGUI() { try { LoaderPlugin.Gui(); } catch (Exception e) { LoaderPlugin.I.Log.LogError(e.ToString()); } }
        void OnApplicationQuit() => LoaderPlugin.Quit();
    }
}
