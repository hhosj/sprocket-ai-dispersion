using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;

namespace SprocketAiDispersion;

internal static class Patches
{
    private const string AssemblyStageTypeName = "Sprocket.Vehicle.VehicleAIAssemblyStage";
    private const string GunLayerAiTypeName = "Sprocket.ArtificialIntelligence.GunLayerAI";

    // Byte offsets into GunLayerAI / GunLayerAISettings, used only if reflection is unavailable.
    private const int OffsetMaxRangeError = 0x10;
    private const int OffsetMaxVelocityError = 0x14;

    private static ZeroingErrorsTuner _zeroing;

    // Guards against scaling the same settings object more than once. The value we last wrote is
    // stored alongside the pointer, so a fresh object that happens to reuse an address is still
    // processed correctly and a shared settings object is never scaled twice.
    private static readonly Dictionary<IntPtr, float> ScaledRangeError = new();
    private static readonly Dictionary<IntPtr, float> ScaledVelocityError = new();

    private static bool _loggedVanillaRangeError;
    private static bool _loggedVanillaVelocityError;

    internal static void Apply(Harmony harmony, ZeroingErrorsTuner zeroing)
    {
        _zeroing = zeroing;

        PatchGunLayerSettings(harmony);
        PatchGunLayerTick(harmony);
    }

    /// <summary>
    /// <c>VehicleAIAssemblyStage.AddGunLayerAIs(crew, settings)</c> is where every AI vehicle's
    /// gun layers are created and where <c>GunLayerAISettings.maxRangeError</c> /
    /// <c>maxVelocityError</c> are copied onto them (the <c>GunLayerAI.SetSettings</c> helper is
    /// inlined away by IL2CPP, so patching that would do nothing).
    /// </summary>
    private static void PatchGunLayerSettings(Harmony harmony)
    {
        var stage = Interop.FindType(AssemblyStageTypeName);
        if (stage == null)
        {
            AiDispersionPlugin.Logger.LogError(
                $"{AssemblyStageTypeName} not found - the game probably updated. Range/lead error scaling disabled.");
            return;
        }

        var target = stage.GetMethod("AddGunLayerAIs", Interop.StaticAny);
        if (target == null)
        {
            AiDispersionPlugin.Logger.LogError("VehicleAIAssemblyStage.AddGunLayerAIs not found.");
            return;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(Patches), nameof(AddGunLayerAIsPrefix))));

        AiDispersionPlugin.Logger.LogInfo(
            "Patched VehicleAIAssemblyStage.AddGunLayerAIs (AI range/lead error scaling).");
    }

    /// <summary>
    /// <c>GunLayerAI.Step</c> runs for every AI gun layer every frame. We only use it as a cheap
    /// tick so the <c>ZeroingErrors</c> table gets tuned as early as possible.
    /// </summary>
    private static void PatchGunLayerTick(Harmony harmony)
    {
        var gunLayerAi = Interop.FindType(GunLayerAiTypeName);
        if (gunLayerAi == null) return;

        var step = gunLayerAi.GetMethod("Step", Interop.InstanceAny);
        if (step == null) return;

        harmony.Patch(step, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(Patches), nameof(GunLayerStepPrefix))));
    }

    // --- patch bodies -------------------------------------------------------------------

    private static void AddGunLayerAIsPrefix(Il2CppObjectBase settings)
    {
        try
        {
            _zeroing?.TryApply();

            if (settings == null) return;

            ScaleField(settings, "maxRangeError", OffsetMaxRangeError,
                AiDispersionPlugin.CfgRangeErrorMultiplier.Value,
                AiDispersionPlugin.CfgRangeErrorMinimum.Value,
                ScaledRangeError);

            ScaleField(settings, "maxVelocityError", OffsetMaxVelocityError,
                AiDispersionPlugin.CfgVelocityErrorMultiplier.Value,
                AiDispersionPlugin.CfgVelocityErrorMinimum.Value,
                ScaledVelocityError);
        }
        catch (Exception e)
        {
            AiDispersionPlugin.Logger.LogError($"AddGunLayerAIs prefix failed: {e}");
        }
    }

    private static void GunLayerStepPrefix()
    {
        if (_zeroing == null || _zeroing.Applied) return;
        _zeroing.TryApply();
    }

    // --- helpers ------------------------------------------------------------------------

    private static void ScaleField(Il2CppObjectBase instance, string fieldName, int offset,
        float multiplier, float minimum, Dictionary<IntPtr, float> alreadyScaled)
    {
        IntPtr pointer = instance.Pointer;
        if (pointer == IntPtr.Zero) return;

        var type = instance.GetType();

        float original;
        bool read = Interop.TryGetValue(type, instance, fieldName, out var raw);
        original = read && raw is float ? (float)raw : Interop.ReadFloatAtPointer(pointer, offset);

        // Already handled this exact object (value unchanged since we wrote it) -> leave it alone.
        if (alreadyScaled.TryGetValue(pointer, out var last) && Math.Abs(last - original) < 1e-7f)
            return;

        float scaled = original * multiplier;
        if (scaled < minimum) scaled = minimum;

        bool alreadyLogged = fieldName == "maxRangeError" ? _loggedVanillaRangeError : _loggedVanillaVelocityError;
        if (AiDispersionPlugin.CfgDebug.Value && !alreadyLogged && read)
        {
            if (fieldName == "maxRangeError") _loggedVanillaRangeError = true;
            else _loggedVanillaVelocityError = true;

            AiDispersionPlugin.Logger.LogInfo(
                $"[debug] vanilla {fieldName} = {original:0.####} -> {scaled:0.####} " +
                $"(x{multiplier:0.###}, min {minimum:0.###})");
        }

        if (!Interop.TrySetValue(type, instance, fieldName, scaled))
            Interop.WriteFloatAtPointer(pointer, offset, scaled);

        if (alreadyScaled.Count > 4096) alreadyScaled.Clear();
        alreadyScaled[pointer] = scaled;
    }
}
