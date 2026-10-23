using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SprocketAiDispersion;

/// <summary>
/// Sprocket does model gunnery error for the AI, but it also models the AI <em>zeroing in</em>
/// on a target: the random error it adds to its aim point shrinks with every shot it takes at
/// the same vehicle. A few shots into an engagement the AI is effectively firing a laser, and
/// that is what this mod dials back towards something that feels like real tank gunnery.
///
/// Only the AI is touched - the player's own gunnery is left exactly as the game ships it.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class AiDispersionPlugin : BasePlugin
{
    public const string PluginGuid = "sprocket.ai.dispersion";
    public const string PluginName = "Sprocket AI Dispersion";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource Logger;

    // --- config -------------------------------------------------------------------------

    internal static ConfigEntry<bool> CfgEnabled;
    internal static ConfigEntry<bool> CfgDebug;

    internal static ConfigEntry<float> CfgZeroingErrorsMultiplier;
    internal static ConfigEntry<float> CfgZeroingErrorsFloorFraction;

    internal static ConfigEntry<float> CfgRangeErrorMultiplier;
    internal static ConfigEntry<float> CfgRangeErrorMinimum;
    internal static ConfigEntry<float> CfgVelocityErrorMultiplier;
    internal static ConfigEntry<float> CfgVelocityErrorMinimum;

    internal static ConfigEntry<bool> CfgSpreadEnabled;
    internal static ConfigEntry<float> CfgSpreadAccuracyAt100m;
    internal static ConfigEntry<string> CfgSpreadDistribution;
    internal static ConfigEntry<string> CfgSpreadMode;
    internal static ConfigEntry<float> CfgSpreadScale;
    internal static ConfigEntry<float> CfgLooseCompression;
    internal static ConfigEntry<float> CfgRefCaliber;
    internal static ConfigEntry<float> CfgRefBarrelLength;
    internal static ConfigEntry<float> CfgRefMuzzleVelocity;
    internal static ConfigEntry<float> CfgSpreadMin;
    internal static ConfigEntry<float> CfgSpreadMax;

    public override void Load()
    {
        Logger = Log;

        CfgEnabled = Config.Bind(
            "General", "Enabled", true,
            "Master switch. When false the plugin loads but patches nothing.");

        CfgDebug = Config.Bind(
            "General", "DebugLogging", true,
            "Log the vanilla AI error values once, so you can see what you are scaling.");

        CfgZeroingErrorsMultiplier = Config.Bind(
            "AI zeroing-in", "ZeroingErrorsMultiplier", 1.5f,
            new ConfigDescription(
                "The AI gets more accurate the more times it has shot at the same target. This multiplies " +
                "that whole accuracy ramp. 1.0 = vanilla. Vanilla measured in-game: [1, 0.5, 0.3, 0.2, 0.1, 0.025].",
                new AcceptableValueRange<float>(0.1f, 10f)));

        CfgZeroingErrorsFloorFraction = Config.Bind(
            "AI zeroing-in", "ZeroingErrorsFloorFraction", 0.15f,
            new ConfigDescription(
                "Never let the AI's aim error fall below this fraction of its first-shot error. This is the " +
                "main fix: vanilla drops to 2.5% after 6 shots (a laser). 0.0 = vanilla, 0.5 = keeps half " +
                "of its opening spread forever, 1.0 = never improves at all.",
                new AcceptableValueRange<float>(0f, 1f)));

        CfgRangeErrorMultiplier = Config.Bind(
            "AI aim error", "RangeErrorMultiplier", 3.0f,
            new ConfigDescription(
                "Multiplies GunLayerAISettings.maxRangeError (vanilla measured: 0.1 = 10% range error). " +
                "Bigger values make the AI misjudge distance and land shells short or over. " +
                "IMPORTANT: this stacks multiplicatively with ZeroingErrorsMultiplier, and the final aim " +
                "scale is (1 +/- error), so keep ZeroingErrorsMultiplier x RangeErrorMultiplier <= ~5.",
                new AcceptableValueRange<float>(0.1f, 10f)));

        CfgRangeErrorMinimum = Config.Bind(
            "AI aim error", "RangeErrorMinimum", 0.0f,
            new ConfigDescription(
                "Optional floor for maxRangeError as a fraction. 0 = use the game's own value (0.1) times " +
                "the multiplier. Only useful if you set the multiplier below 1.0.",
                new AcceptableValueRange<float>(0f, 2f)));

        CfgVelocityErrorMultiplier = Config.Bind(
            "AI aim error", "VelocityErrorMultiplier", 2.0f,
            new ConfigDescription(
                "Multiplies GunLayerAISettings.maxVelocityError (vanilla measured: 0.1). This is the error in " +
                "the AI's lead/deflection calculation, so it mostly matters against moving targets. " +
                "Stacks multiplicatively like RangeErrorMultiplier.",
                new AcceptableValueRange<float>(0.1f, 10f)));

        CfgVelocityErrorMinimum = Config.Bind(
            "AI aim error", "VelocityErrorMinimum", 0.0f,
            "Optional floor for maxVelocityError as a fraction. See RangeErrorMinimum.");

        // --- shell dispersion (World of Tanks style) ------------------------------------

        CfgSpreadEnabled = Config.Bind(
            "Shell dispersion", "Enabled", true,
            "Rotate each shell slightly inside a random cone when it leaves the barrel. The gun still " +
            "points exactly where it was aimed, so the AI keeps aiming at weak spots - it just cannot " +
            "guarantee the hit. Applies to every shell, player and AI alike (the launch path carries no " +
            "information about who fired).");

        CfgSpreadAccuracyAt100m = Config.Bind(
            "Shell dispersion", "AccuracyAt100m", 0.28f,
            new ConfigDescription(
                "Radius of the dispersion circle, in metres, at 100 m: 95% of shells land inside it. " +
                "Same idea as WoT's 'accuracy' number. 0.20 = tight, 0.40 = default, " +
                "0.60 = loose, 1.00 = spray and pray. It is an ANGLE, so the circle grows " +
                "linearly with range (0.40 m at 100 m becomes 2.0 m at 500 m).",
                new AcceptableValueRange<float>(0f, 10f)));

        CfgSpreadMode = Config.Bind(
            "Shell dispersion", "Mode", "GunData",
            new ConfigDescription(
                "Fixed    = use AccuracyAt100m as a single global value (current behaviour).\n" +
                "GunData  = scale it per gun from real gun data: ratio = (caliber/RefCaliber) * " +
                "(RefBarrelLength/barrelLength) * (RefMuzzleVelocity/muzzleVelocity). This uses the " +
                "same shape as the game's own (unused) Dispersion formula, but normalised so every " +
                "constant cancels - shorter barrel, lower velocity or bigger shell = more spread.",
                new AcceptableValueList<string>("Fixed", "GunData")));

        CfgSpreadScale = Config.Bind(
            "Shell dispersion", "Scale", 1.0f,
            "Extra global multiplier applied on top of the gun-data ratio (GunData mode only).");

        CfgLooseCompression = Config.Bind(
            "Shell dispersion", "LooseSideCompression", 0.4f,
            new ConfigDescription(
                "GunData mode only. The raw shape (caliber / (velocity * barrelLength)) makes short-barrelled " +
                "low-velocity guns VERY loose compared to long fast guns. This compresses only the loose side " +
                "(ratio > 1) towards the reference, so the tight guns stay tight:\n" +
                "  ratio > 1  ->  1 + (ratio - 1) * LooseSideCompression\n" +
                "0 = raw formula (biggest spread between guns), 1 = completely flat. Note that FLATTENING " +
                "ALWAYS LOOSENS the already-tight guns - if you want long-barrel guns tighter, lower " +
                "AccuracyAt100m instead.",
                new AcceptableValueRange<float>(0f, 1f)));

        CfgRefCaliber = Config.Bind(
            "Shell dispersion", "RefCaliber", 0.075f,
            "Reference gun used to normalise GunData mode: its shell caliber, in the SAME UNIT as the " +
            "game reports it. Measured in-game: caliber is in METRES (e.g. 0.075 for 75 mm), barrel " +
            "length is in METRES (3.151 for a 75 mm L/42), muzzle velocity in m/s.");

        CfgRefBarrelLength = Config.Bind(
            "Shell dispersion", "RefBarrelLength", 3.0f,
            "Reference gun barrel length (any unit, as long as it matches the logged value).");

        CfgRefMuzzleVelocity = Config.Bind(
            "Shell dispersion", "RefMuzzleVelocity", 700f,
            "Reference gun muzzle velocity in m/s (the real, measured value).");

        CfgSpreadMin = Config.Bind(
            "Shell dispersion", "MinAccuracyAt100m", 0.05f,
            "Safety clamp for GunData mode: never produce a tighter circle than this.");

        CfgSpreadMax = Config.Bind(
            "Shell dispersion", "MaxAccuracyAt100m", 2.0f,
            "Safety clamp for GunData mode: never produce a wider circle than this.");

        CfgSpreadDistribution = Config.Bind(
            "Shell dispersion", "Distribution", "Gaussian",
            new ConfigDescription(
                "Gaussian = most shells land near the aim point, few at the edge (WoT feel). " +
                "Uniform = evenly spread inside the whole circle.",
                new AcceptableValueList<string>("Gaussian", "Uniform")));

        if (!CfgEnabled.Value)
        {
            Logger.LogInfo($"{PluginName}: disabled in config, nothing patched.");
            return;
        }

        var zeroing = new ZeroingErrorsTuner(
            CfgZeroingErrorsMultiplier.Value,
            CfgZeroingErrorsFloorFraction.Value,
            CfgDebug.Value);

        // Try immediately - if the game already initialised the AI types this works right away,
        // otherwise the tuner retries from the AddGunLayerAIs / Step hooks.
        zeroing.TryApply();

        try
        {
            var harmony = new Harmony(PluginGuid);
            Patches.Apply(harmony, zeroing);
            ShellDispersion.Apply(harmony);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }
        catch (Exception e)
        {
            Logger.LogError($"Failed to apply patches: {e}");
        }
    }
}
