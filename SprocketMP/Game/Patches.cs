using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.DamageModelling;
using Sprocket.GameControl;
using Sprocket.SettingConfiguration;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Weapons;
using Sprocket.Vehicles.Weapons.Cannons;
using Sprocket.VehicleControl;

namespace SprocketMP.Game
{
    [HarmonyPatch(typeof(GameController), nameof(GameController.Initiate))]
    static class Patch_GameControllerInitiate
    {
        static void Prefix(GameController __instance, SettingsProfile settings, Il2CppReferenceArray<Il2CppSystem.Object> arguments)
        {
            try { BattleSetup.OnGameControllerInitiate(__instance, arguments); }
            catch (Exception e) { Plugin.Log.LogError("GameController.Initiate hook: " + e); }
        }
    }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ModeUpdate))]
    static class Patch_DMUpdate
    {
        static void Postfix(DeathmatchGameMode __instance)
        {
            try { BattleSync.ModeUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("ModeUpdate hook: " + e); }
        }
    }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ModeFixedUpdate))]
    static class Patch_DMFixedUpdate
    {
        static void Postfix(DeathmatchGameMode __instance)
        {
            try { BattleSync.ModeFixedUpdate(__instance); }
            catch (Exception e) { Plugin.Log.LogError("ModeFixedUpdate hook: " + e); }
        }
    }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ExitGameInternal))]
    static class Patch_DMExit
    {
        static void Prefix()
        {
            try { if (BattleSync.InBattle) BattleSync.EndBattle(); }
            catch (Exception e) { Plugin.Log.LogError("Exit hook: " + e); }
        }
    }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.RetryAsyncVoid))]
    static class Patch_DMRetry
    {
        static bool Prefix()
        {
            try { return BattleSync.OnRetryRequested(); }
            catch (Exception e) { Plugin.Log.LogError("Retry hook: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.CompleteMission))]
    static class Patch_DMComplete { static void Prefix() => Plugin.Log.LogInfo("DM: CompleteMission"); }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.FailMission))]
    static class Patch_DMFail { static void Prefix() => Plugin.Log.LogInfo("DM: FailMission"); }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ShowEndScreen))]
    static class Patch_DMShowEnd
    {
        static void Prefix(string sceneName)
        {
            Plugin.Log.LogInfo("DM: ShowEndScreen " + sceneName);
            BattleSync.ReactivateEndScreens();
        }
    }

    [HarmonyPatch(typeof(MissionEndScreen), nameof(MissionEndScreen.WaitForInput))]
    static class Patch_EndScreenWait
    {
        static void Prefix(Il2CppSystem.Action<MissionEndScreenResult> callback)
        {
            BattleSync.EndScreenCallback = callback;
        }
    }

    // End screen (SPACE after Victory/Defeat) calls Retry() directly: on a client it must not reset on its own.
    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.Retry))]
    static class Patch_DMRetryTask
    {
        static bool Prefix(ref Il2CppSystem.Threading.Tasks.Task __result)
        {
            try
            {
                if (BattleSync.OnRetryRequested()) return true;
                __result = Il2CppSystem.Threading.Tasks.Task.CompletedTask;
                return false;
            }
            catch (Exception e) { Plugin.Log.LogError("Retry(Task) hook: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(DeathmatchGameMode), nameof(DeathmatchGameMode.ResetGame))]
    static class Patch_DMReset
    {
        static void Prefix()
        {
            try { BattleSync.OnResetGame(); }
            catch (Exception e) { Plugin.Log.LogError("Reset hook: " + e); }
        }
    }

    // ---- AI must not drive puppets -------------------------------------------------------------
    [HarmonyPatch(typeof(CommanderAI), nameof(CommanderAI.FastTick))]
    static class Patch_AIFast
    {
        static bool Prefix(CommanderAI __instance)
        {
            if (!BattleSync.Active) return true;
            try { var t = __instance.controlTarget; if (t == null) return true; BattleSync.NoteCommander(t.Pointer, __instance); return !BattleSync.AiBlocked(t.Pointer); }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(CommanderAI), nameof(CommanderAI.SlowTick))]
    static class Patch_AISlow
    {
        static bool Prefix(CommanderAI __instance)
        {
            if (!BattleSync.Active) return true;
            try { var t = __instance.controlTarget; if (t == null) return true; BattleSync.NoteCommander(t.Pointer, __instance); return !BattleSync.AiBlocked(t.Pointer); }
            catch { return true; }
        }
    }

    // ---- turret / gun drives of puppets are driven by the network ------------------------------
    [HarmonyPatch(typeof(TurretBehaviour), nameof(TurretBehaviour.MoveToTarget))]
    static class Patch_TurretMove
    {
        static bool Prefix(TurretBehaviour __instance) => !BattleSync.Active || !BattleSync.IsPuppetRotator(__instance.Pointer);
    }

    [HarmonyPatch(typeof(TurretBehaviour), nameof(TurretBehaviour.Apply))]
    static class Patch_TurretApply
    {
        static bool Prefix(TurretBehaviour __instance) => !BattleSync.Active || !BattleSync.IsPuppetRotator(__instance.Pointer);
    }

    [HarmonyPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.MoveToTarget))]
    static class Patch_LayingMove
    {
        static bool Prefix(LayingDriveBehaviour __instance) => !BattleSync.Active || !BattleSync.IsPuppetRotator(__instance.Pointer);
    }

    [HarmonyPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.Apply))]
    static class Patch_LayingApply
    {
        static bool Prefix(LayingDriveBehaviour __instance) => !BattleSync.Active || !BattleSync.IsPuppetRotator(__instance.Pointer);
    }

    [HarmonyPatch(typeof(LayingDriveBehaviour), nameof(LayingDriveBehaviour.MoveToTargetInstant))]
    static class Patch_LayingInstant
    {
        static bool Prefix(LayingDriveBehaviour __instance) => !BattleSync.Active || !BattleSync.IsPuppetRotator(__instance.Pointer);
    }

    // ---- tracks: puppets get wheel / belt motion computed from their network movement ----
    [HarmonyPatch(typeof(Sprocket.ContinuousTracks.TrackBehaviour), nameof(Sprocket.ContinuousTracks.TrackBehaviour.FixedUpdate))]
    static class Patch_TrackFixed
    {
        static void Prefix(Sprocket.ContinuousTracks.TrackBehaviour __instance, float deltaTime)
        {
            try { TrackAnim.Before(__instance, deltaTime); } catch (Exception e) { TrackAnim.Fail(e); }
        }
        static void Postfix(Sprocket.ContinuousTracks.TrackBehaviour __instance, float deltaTime)
        {
            try { TrackAnim.After(__instance, deltaTime); } catch (Exception e) { TrackAnim.Fail(e); }
        }
    }

    [HarmonyPatch(typeof(Sprocket.ContinuousTracks.TrackBeltBehaviour), nameof(Sprocket.ContinuousTracks.TrackBeltBehaviour.FixedUpdate))]
    static class Patch_BeltFixed
    {
        static void Prefix(Sprocket.ContinuousTracks.TrackBeltBehaviour __instance, float deltaTime, ref float sprocketDeltaAngle)
        {
            try { TrackAnim.Belt(__instance, deltaTime, ref sprocketDeltaAngle); } catch (Exception e) { TrackAnim.Fail(e); }
        }
    }

    // ---- shots ----------------------------------------------------------------------------------
    [HarmonyPatch(typeof(CannonBehaviour), nameof(CannonBehaviour.FireInternal))]
    static class Patch_CannonFire
    {
        static bool Prefix(CannonBehaviour __instance)
        {
            try { return BattleSync.AllowFire(__instance); }
            catch { return true; }
        }

        static void Postfix(CannonBehaviour __instance, ProjectileTypeID projectileID)
        {
            try { BattleSync.OnCannonFired(__instance, projectileID); }
            catch (Exception e) { Plugin.Log.LogError("Fire hook: " + e); }
        }
    }

    // ---- damage ---------------------------------------------------------------------------------
    [HarmonyPatch(typeof(VehicleHealthRegister), nameof(VehicleHealthRegister.Sprocket_DamageModelling_IDamageApplier_HealthDelta),
        new[] { typeof(int), typeof(float), typeof(DamageModelDamageModifiers) })]
    static class Patch_HealthDeltaId
    {
        static bool Prefix(VehicleHealthRegister __instance, int id, float value, DamageModelDamageModifiers modifiers)
        {
            try { return BattleSync.OnHealthDelta(__instance, true, id, value, modifiers); }
            catch (Exception e) { Plugin.Log.LogError("HealthDelta hook: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(VehicleHealthRegister), nameof(VehicleHealthRegister.Sprocket_DamageModelling_IDamageApplier_HealthDelta),
        new[] { typeof(float), typeof(DamageModelDamageModifiers) })]
    static class Patch_HealthDelta
    {
        static bool Prefix(VehicleHealthRegister __instance, float value, DamageModelDamageModifiers modifiers)
        {
            try { return BattleSync.OnHealthDelta(__instance, false, 0, value, modifiers); }
            catch (Exception e) { Plugin.Log.LogError("HealthDelta hook: " + e); return true; }
        }
    }

    // ---- don't let the player jump into vehicles owned by someone else -------------------------
    [HarmonyPatch(typeof(VehicleControlPlayerState), nameof(VehicleControlPlayerState.CycleVehicle))]
    static class Patch_CycleVehicle
    {
        static bool Prefix() => true;   // switching is allowed: BattleSync decides whether you drive or only watch
    }
}
