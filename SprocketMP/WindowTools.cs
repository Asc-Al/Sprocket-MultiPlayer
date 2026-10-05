using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SprocketMP
{
    /// <summary>Test helper: Ctrl+F11 / Ctrl+F12 put the game window on the left / right half of the screen.</summary>
    public static class WindowTools
    {
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int idx);

        static int s_pendingHalf = -1, s_w, s_h;
        static float s_timer;
        public static void Tick()
        {
            if (s_pendingHalf < 0) return;
            s_timer -= Time.unscaledDeltaTime;
            if (s_timer > 0) return;
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            int sh = GetSystemMetrics(1);
            MoveWindow(hwnd, s_pendingHalf * s_w, s_pendingHalf == 0 ? 0 : Math.Max(0, sh - 48 - s_h), s_w, s_h, true);
            s_pendingHalf = -1;
        }

        public static void Place(int half)
        {
            try
            {
                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                int w = sw / 2, h = (w - 16) * 9 / 16 + 39;
                Screen.SetResolution(w - 16, h - 40, FullScreenMode.Windowed);
                s_pendingHalf = half; s_timer = 0.6f; s_w = w; s_h = h;
                Plugin.Log.LogInfo($"Window placed on half {half} ({w}x{h})");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Place window: " + e.Message); }
        }
    }
}
