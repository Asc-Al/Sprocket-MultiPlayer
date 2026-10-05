using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Preloader.Core.Patching;
using HarmonyLib;

namespace SprocketMP.InteropFix
{
    /// <summary>
    /// Il2CppInterop locates Class::GetDefaultFieldValue by signature/xref scan. On Sprocket
    /// (Unity 6000.3, metadata v39) the hooked function crashes the game with AccessViolation inside
    /// Class_GetFieldDefaultValue_Hook. That hook only exists to support EnumInjector (adding new
    /// values to IL2CPP enums), which we never use. We redirect the hook to a private dummy native
    /// stub that nobody ever calls, so the real il2cpp function stays untouched.
    /// (Patching the generic Hook&lt;T&gt;.ApplyHook breaks shared generic code, so we don't.)
    /// </summary>
    [PatcherPluginInfo("sprocketmp.interopfix", "SprocketMP Il2CppInterop Fix", "1.0.2")]
    public class InteropFixPatcher : BasePatcher
    {
        static BepInEx.Logging.ManualLogSource s_log;
        static IntPtr s_stub;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);

        // Dobby needs the hooked function within +-2GB of its trampolines, so the stub must live
        // right next to GameAssembly.dll in memory.
        static IntPtr AllocStub()
        {
            var mod = GetModuleHandleW("GameAssembly.dll");
            IntPtr mem = IntPtr.Zero;
            if (mod != IntPtr.Zero)
            {
                long b = mod.ToInt64() & ~0xFFFFL;
                for (long off = 0x10000; off < 0x70000000L && mem == IntPtr.Zero; off += 0x10000)
                {
                    mem = VirtualAlloc(new IntPtr(b - off), (UIntPtr)4096, 0x3000, 0x40);
                    if (mem == IntPtr.Zero) mem = VirtualAlloc(new IntPtr(b + 0x8000000L + off), (UIntPtr)4096, 0x3000, 0x40);
                }
            }
            if (mem == IntPtr.Zero) mem = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
            var code = new byte[64];
            for (int i = 0; i < 32; i++) code[i] = 0x90;
            code[32] = 0x31; code[33] = 0xC0; code[34] = 0xC3;
            for (int i = 35; i < 64; i++) code[i] = 0xCC;
            Marshal.Copy(code, 0, mem, code.Length);
            s_log?.LogInfo($"Stub at 0x{mem.ToInt64():X}, GameAssembly at 0x{mod.ToInt64():X}");
            return mem;
        }

        public override void Initialize()
        {
            s_log = Log;
            try
            {

                var asm = typeof(Il2CppInterop.Runtime.IL2CPP).Assembly;
                var hookType = asm.GetType("Il2CppInterop.Runtime.Injection.Hooks.Class_GetFieldDefaultValue_Hook", true);
                var find = hookType.GetMethod("FindTargetMethod", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                new Harmony("sprocketmp.interopfix").Patch(find,
                    prefix: new HarmonyMethod(typeof(InteropFixPatcher).GetMethod(nameof(FindPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.LogInfo("Class::GetDefaultFieldValue hook will be redirected to a dummy stub");
            }
            catch (Exception e)
            {
                Log.LogError("Failed to install Il2CppInterop fix: " + e);
            }
        }

        static bool FindPrefix(ref IntPtr __result)
        {
            if (s_stub == IntPtr.Zero) s_stub = AllocStub();
            __result = s_stub;
            s_log?.LogWarning("Redirected Class_GetFieldDefaultValue_Hook to dummy stub (EnumInjector disabled)");
            return false;
        }
    }
}
