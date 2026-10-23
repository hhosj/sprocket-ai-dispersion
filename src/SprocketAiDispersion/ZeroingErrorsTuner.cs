using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SprocketAiDispersion;

/// <summary>
/// The AI does not simply "aim and miss". It <b>zeroes in</b> on a target: every time it
/// attacks the same vehicle the random error added to its aim point gets smaller, driven by
/// a private static table called <c>ZeroingErrors</c> on
/// <c>Sprocket.ArtificialIntelligence.FindAndAimAtTargetGunLayerTask</c>.
///
/// Vanilla shape (conceptually): a random offset of
/// <c>+/- ZeroingErrors[attacksSoFar] * maxRangeError * accuracyModifier * targetSize</c>
/// is applied to the estimated intercept point, plus a fixed
/// <c>Random.insideUnitSphere * 0.25</c> jitter. Once the AI has shot a target a handful of
/// times the zeroing term collapses towards zero, which is why late in a firefight it starts
/// placing shells exactly on weak spots like a turret ring.
///
/// This class amplifies that table (<see cref="Scale"/>) and/or stops it from collapsing
/// (<see cref="FloorFraction"/>).
/// </summary>
internal sealed class ZeroingErrorsTuner
{
    private const string TaskTypeName = "Sprocket.ArtificialIntelligence.FindAndAimAtTargetGunLayerTask";
    private const string TableFieldName = "ZeroingErrors";

    private readonly float _scale;
    private readonly float _floorFraction;
    private readonly bool _debug;

    private Type _taskType;
    private bool _applied;
    private bool _warned;

    internal bool Applied => _applied;

    internal ZeroingErrorsTuner(float scale, float floorFraction, bool debug)
    {
        _scale = scale;
        _floorFraction = floorFraction;
        _debug = debug;
    }

    /// <summary>
    /// Applies the tuning once the game has created (and run the static constructor of) the
    /// task type. Safe to call over and over - it does nothing after the first success.
    /// </summary>
    internal void TryApply()
    {
        if (_applied) return;

        _taskType ??= Interop.FindType(TaskTypeName);
        if (_taskType == null)
        {
            if (!_warned)
            {
                _warned = true;
                AiDispersionPlugin.Logger.LogDebug($"ZeroingErrors: type {TaskTypeName} not found yet.");
            }
            return;
        }

        object raw;
        try
        {
            if (!Interop.TryGetValue(_taskType, null, TableFieldName, out raw)) return;
        }
        catch
        {
            return;
        }

        // The IL2CPP static constructor has not run yet -> the static slot is still null.
        if (raw == null) return;

        if (!Interop.TryAsFloatList(raw, out var table))
        {
            if (!_warned)
            {
                _warned = true;
                AiDispersionPlugin.Logger.LogWarning(
                    $"ZeroingErrors: array type '{raw.GetType().FullName}' is not IList<float>; tuning disabled.");
            }
            return;
        }

        int count = table.Count;
        if (count == 0) return;

        var original = new float[count];
        for (int i = 0; i < count; i++) original[i] = table[i];

        float peak = original.Max();
        if (peak <= 0f)
        {
            AiDispersionPlugin.Logger.LogWarning("ZeroingErrors: table is all zeros, nothing to amplify.");
            _applied = true;
            return;
        }

        float floor = _floorFraction * peak;

        var tuned = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = original[i] * _scale;
            if (v < floor) v = floor;
            tuned[i] = v;
            table[i] = v;
        }

        _applied = true;

        AiDispersionPlugin.Logger.LogInfo(
            $"ZeroingErrors tuned: scale x{_scale:0.###}, floor {_floorFraction * 100f:0.#}% of peak.");
        AiDispersionPlugin.Logger.LogInfo("  before: [" + string.Join(", ", original.Select(v => v.ToString("0.####"))) + "]");
        AiDispersionPlugin.Logger.LogInfo("  after : [" + string.Join(", ", tuned.Select(v => v.ToString("0.####"))) + "]");
    }
}
