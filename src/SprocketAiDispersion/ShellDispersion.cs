using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.DamageModelling;
using UnityEngine;

namespace SprocketAiDispersion;

/// <summary>
/// World-of-Tanks style shell dispersion: the barrel keeps pointing exactly where it was aimed
/// (so the AI still aims at weak spots), but the shell leaves the muzzle inside a random cone.
///
/// Where this hooks
/// ----------------
/// IL2CPP inlined the game's own helpers (<c>GetFireDirection</c> / <c>GetRandomAccuracyOffset</c>)
/// so they cannot be patched. The single funnel every shell goes through is
/// <c>ProjectileRegister.Launch()</c>, a plain class method that owns the projectile pool
/// (<c>private ProjectileInstance[] pool</c>).
///
/// Why we write with raw pointers
/// ------------------------------
/// <c>ProjectileInstance</c> is a non-blittable value type, so <c>Il2CppReferenceArray</c> stores
/// the elements INLINE (<c>ourElementIsValueType == true</c>, stride =
/// <c>il2cpp_class_value_size</c>). The generated element wrapper is a view that does not reliably
/// propagate field writes back into the array, so we address the element ourselves:
///
///     element(i) = pool.Pointer + 0x20 + i * elementSize
///     velocity   = element + 0x34 (three floats)
///
/// Built in verification
/// ---------------------
/// After writing we remember the projectile id. On the following <c>FixedUpdate</c> we read the
/// velocity back from the pool and log whether our change survived. If it did not, the log says
/// so explicitly instead of us guessing.
/// </summary>
internal static class ShellDispersion
{
    private const int MaxDiagnosticShots = 12;

    // IL2CPP array layout: klass(0x0) monitor(0x8) bounds(0x10) maxLength(0x18) vector(0x20)
    private const int ArrayVectorOffset = 0x20;
    private const int ArrayLengthOffset = 0x18;

    // ProjectileInstance field offsets (from the game's metadata)
    private const int OffId = 0x18;
    private const int OffVelocity = 0x34;
    private const int OffDef = 0x08;            // ProjectileInstance.def
    private const int OffDefDiameter = 0x34;    // ProjectileDefinition.Diameter
    private const int OffFlags = 0x48;
    private const byte FlagActive = 1;

    private static readonly System.Random Rng = new System.Random();

    private static int _shotsSeen;
    private static bool _warnedNotFound;
    private static int _lastPerturbedId = int.MinValue;

    private static int _elementSize = -1;
    private static readonly HashSet<string> LoggedGuns = new HashSet<string>();
    private static readonly Dictionary<int, Vector3> Pending = new Dictionary<int, Vector3>();

    internal static void Apply(Harmony harmony)
    {
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(ShellDispersion), nameof(LaunchPostfix)));
        int patched = 0;
        foreach (var name in new[] { "Launch", "Sprocket_DamageModelling_IProjectileFactory_Launch" })
        {
            var method = typeof(ProjectileRegister).GetMethod(name, Interop.InstanceAny);
            if (method == null) continue;
            harmony.Patch(method, postfix: postfix);
            patched++;
        }

        var fixedUpdate = typeof(ProjectileRegister).GetMethod("FixedUpdate", Interop.InstanceAny);
        if (fixedUpdate != null)
            harmony.Patch(fixedUpdate, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(ShellDispersion), nameof(FixedUpdatePrefix))));

        if (patched == 0)
        {
            AiDispersionPlugin.Logger.LogError("ProjectileRegister.Launch not found - shell dispersion disabled.");
            return;
        }

        AiDispersionPlugin.Logger.LogInfo(
            $"Patched ProjectileRegister.Launch ({patched} entry point(s)) + FixedUpdate - shell dispersion " +
            $"{AiDispersionPlugin.CfgSpreadAccuracyAt100m.Value:0.###} m @ 100 m, " +
            $"{AiDispersionPlugin.CfgSpreadDistribution.Value}, elementSize={ElementSize()}.");
    }

    // --- hook bodies ---------------------------------------------------------------------

    private static void LaunchPostfix(ProjectileRegister __instance,
        ref ProjectileLauncherInfo launcherInfo, ref ProjectileFireInfo fireInfo)
    {
        try
        {
            if (!AiDispersionPlugin.CfgSpreadEnabled.Value) return;

            int id = fireInfo.ProjectileID;

            // The public Launch() and the explicit IProjectileFactory.Launch() are both real entry
            // points and fire back-to-back for one shell; only handle the shell once.
            if (id == _lastPerturbedId) return;
            _lastPerturbedId = id;

            var pool = __instance.pool;
            if (pool == null) return;

            IntPtr element = FindElement(pool.Pointer, id);
            if (element == IntPtr.Zero)
            {
                if (!_warnedNotFound)
                {
                    _warnedNotFound = true;
                    AiDispersionPlugin.Logger.LogWarning(
                        $"Shell dispersion: projectile id {id} not found in pool (length {pool.Length}, " +
                        $"elementSize {ElementSize()}); disabling shell dispersion.");
                    AiDispersionPlugin.CfgSpreadEnabled.Value = false;
                }
                return;
            }

            Perturb(element, id, launcherInfo);
        }
        catch (Exception e)
        {
            AiDispersionPlugin.Logger.LogError($"Shell dispersion postfix failed: {e}");
            AiDispersionPlugin.CfgSpreadEnabled.Value = false;
        }
    }

    /// <summary>First FixedUpdate after a shot: did our write to the pool element survive?</summary>
    private static void FixedUpdatePrefix(ProjectileRegister __instance)
    {
        if (Pending.Count == 0) return;
        try
        {
            var pool = __instance.pool;
            if (pool == null) return;

            foreach (var kv in Pending)
            {
                IntPtr element = FindElement(pool.Pointer, kv.Key);
                if (element == IntPtr.Zero) continue;

                float x = ReadF(element, OffVelocity);
                float y = ReadF(element, OffVelocity + 4);
                float z = ReadF(element, OffVelocity + 8);

                float dx = x - kv.Value.x, dy = y - kv.Value.y, dz = z - kv.Value.z;
                float err = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                AiDispersionPlugin.Logger.LogInfo(
                    err < 0.01f
                        ? $"[verify] id={kv.Key} OK  (our write survived, v={x:0.#},{y:0.#},{z:0.#})"
                        : $"[verify] id={kv.Key} LOST (expected {kv.Value.x:0.#},{kv.Value.y:0.#},{kv.Value.z:0.#} " +
                          $"but pool has {x:0.#},{y:0.#},{z:0.#})");
            }
            Pending.Clear();
        }
        catch (Exception e)
        {
            AiDispersionPlugin.Logger.LogError($"Shell dispersion verify failed: {e}");
            Pending.Clear();
        }
    }

    // --- core ----------------------------------------------------------------------------

    private static void Perturb(IntPtr element, int id, ProjectileLauncherInfo launcherInfo)
    {
        float vx = ReadF(element, OffVelocity);
        float vy = ReadF(element, OffVelocity + 4);
        float vz = ReadF(element, OffVelocity + 8);

        float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        if (!float.IsFinite(speed) || speed < 1f || speed > 4000f)
        {
            AiDispersionPlugin.Logger.LogWarning(
                $"Shell dispersion: implausible muzzle speed {speed} for projectile {id}; skipping.");
            return;
        }

        float radius95 = EffectiveRadius95(element, launcherInfo, speed);
        float maxAngle = MathF.Atan(radius95 / 100f);
        if (maxAngle <= 0f) return;

        // AccuracyAt100m is the 95% circle radius (metres at 100 m). For a 2D Gaussian the radial
        // error is Rayleigh distributed: 95% of shots fall within 2.447 sigma.
        float sigma = maxAngle / 2.447f;

        float u, v;
        if (string.Equals(AiDispersionPlugin.CfgSpreadDistribution.Value, "Uniform",
                StringComparison.OrdinalIgnoreCase))
        {
            double r = maxAngle * Math.Sqrt(Rng.NextDouble());
            double a = Rng.NextDouble() * Math.PI * 2.0;
            u = (float)(r * Math.Cos(a));
            v = (float)(r * Math.Sin(a));
        }
        else
        {
            u = sigma * (float)Gaussian();
            v = sigma * (float)Gaussian();
        }

        float dx = vx / speed, dy = vy / speed, dz = vz / speed;

        float ax = 0f, ay = 1f, az = 0f;
        if (MathF.Abs(dy) > 0.99f) { ax = 1f; ay = 0f; az = 0f; }

        float rx = dy * az - dz * ay;
        float ry = dz * ax - dx * az;
        float rz = dx * ay - dy * ax;
        float rl = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-6f) return;
        rx /= rl; ry /= rl; rz /= rl;

        float ux = ry * dz - rz * dy;
        float uy = rz * dx - rx * dz;
        float uz = rx * dy - ry * dx;

        float nx = dx + rx * u + ux * v;
        float ny = dy + ry * u + uy * v;
        float nz = dz + rz * u + uz * v;
        float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (nl < 1e-6f) return;
        nx /= nl; ny /= nl; nz /= nl;

        var applied = new Vector3(nx * speed, ny * speed, nz * speed);
        WriteF(element, OffVelocity, applied.x);
        WriteF(element, OffVelocity + 4, applied.y);
        WriteF(element, OffVelocity + 8, applied.z);

        if (Pending.Count > 64) Pending.Clear();
        Pending[id] = applied;

        if (_shotsSeen < MaxDiagnosticShots)
        {
            _shotsSeen++;
            float offset = MathF.Sqrt(u * u + v * v);
            AiDispersionPlugin.Logger.LogInfo(
                $"[spread] shot {_shotsSeen}: id={id} speed={speed:0.#} m/s  " +
                $"dir {dx:0.###},{dy:0.###},{dz:0.###} -> {nx:0.###},{ny:0.###},{nz:0.###}  " +
                $"offset={offset * 1000f:0.##} mrad  cone={maxAngle * 1000f:0.##} mrad");
        }
    }

    /// <summary>
    /// Element size of the pool array. <c>Il2CppReferenceArray&lt;T&gt;.ourElementTypeSize</c> is set
    /// by Il2CppInterop to <c>il2cpp_class_value_size</c> for value-type elements.
    /// </summary>
    private static int ElementSize()
    {
        if (_elementSize > 0) return _elementSize;

        for (var t = typeof(Il2CppReferenceArray<ProjectileInstance>); t != null; t = t.BaseType)
        {
            var f = t.GetField("ourElementTypeSize", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null)
            {
                _elementSize = Convert.ToInt32(f.GetValue(null));
                break;
            }
        }

        if (_elementSize <= 0)
        {
            // Fallback: ProjectileInstance's last field is a byte at 0x48, so 0x50 is the aligned size.
            _elementSize = 0x50;
            AiDispersionPlugin.Logger.LogWarning(
                $"Shell dispersion: could not read ourElementTypeSize, assuming {_elementSize}.");
        }
        return _elementSize;
    }

    private static IntPtr FindElement(IntPtr array, int id)
    {
        int size = ElementSize();
        int length = Marshal.ReadInt32(array, ArrayLengthOffset);
        for (int i = 0; i < length; i++)
        {
            IntPtr element = array + ArrayVectorOffset + i * size;
            if (Marshal.ReadInt32(element, OffId) == id) return element;
        }
        return IntPtr.Zero;
    }

    private static unsafe float ReadF(IntPtr p, int offset) => *(float*)((byte*)p + offset);

    private static unsafe void WriteF(IntPtr p, int offset, float value) => *(float*)((byte*)p + offset) = value;

    /// <summary>
    /// The 95% circle radius (metres at 100 m) to use for this shell.
    ///
    /// Fixed mode returns the configured constant. GunData mode scales it by how this gun compares
    /// to a reference gun, using the same shape as the game's own (unused) dispersion formula
    /// (ShellProperties.CalculateDispersion): dispersion ~ caliber / (velocity * barrelLength).
    /// Normalising by the reference gun cancels every constant and every unit, so this reduces to:
    ///
    ///     ratio = (caliber / RefCaliber) * (RefBarrelLength / barrelLength) * (RefMuzzleVelocity / velocity)
    ///
    /// i.e. shorter barrel, lower muzzle velocity or bigger shell = more spread. The result is
    /// clamped, so a weird gun can never produce an absurd circle.
    /// </summary>
    private static float EffectiveRadius95(IntPtr element, ProjectileLauncherInfo launcherInfo, float speed)
    {
        float configured = AiDispersionPlugin.CfgSpreadAccuracyAt100m.Value;

        IntPtr def = Marshal.ReadIntPtr(element, OffDef);
        float caliber = def != IntPtr.Zero ? ReadF(def, OffDefDiameter) : 0f;
        float barrel = launcherInfo.BarrelLength;

        if (caliber <= 0f || barrel <= 0f || speed <= 0f)
        {
            LogGunOnce(caliber, barrel, speed, 1f, configured, "missing data, falling back to Fixed value", 1f);
            return configured;
        }

        float ratio = (caliber / AiDispersionPlugin.CfgRefCaliber.Value)
                      * (AiDispersionPlugin.CfgRefBarrelLength.Value / barrel)
                      * (AiDispersionPlugin.CfgRefMuzzleVelocity.Value / speed);

        // Compress only the loose side, so long-barrel / high-velocity guns keep their advantage.
        float rawRatio = ratio;
        float compression = AiDispersionPlugin.CfgLooseCompression.Value;
        if (ratio > 1f && compression < 1f)
            ratio = 1f + (ratio - 1f) * compression;

        float gunDataR95 = configured * AiDispersionPlugin.CfgSpreadScale.Value * ratio;
        gunDataR95 = Math.Clamp(gunDataR95, AiDispersionPlugin.CfgSpreadMin.Value,
            AiDispersionPlugin.CfgSpreadMax.Value);

        // Always record the gun's data, whatever the mode - this is the P0 measurement pass.
        LogGunOnce(caliber, barrel, speed, ratio, gunDataR95, null, rawRatio);

        bool useGunData = string.Equals(AiDispersionPlugin.CfgSpreadMode.Value, "GunData",
            StringComparison.OrdinalIgnoreCase);
        return useGunData ? gunDataR95 : configured;
    }

    private static void LogGunOnce(float caliber, float barrel, float speed, float ratio, float r95, string note, float rawRatio = 0f)
    {
        if (LoggedGuns.Count >= 24) return;
        string key = $"{caliber:0.######}|{barrel:0.######}|{speed:0.#}";
        if (!LoggedGuns.Add(key)) return;

        bool gunData = string.Equals(AiDispersionPlugin.CfgSpreadMode.Value, "GunData",
            StringComparison.OrdinalIgnoreCase);
        AiDispersionPlugin.Logger.LogInfo(
            $"[gun] caliber={caliber:0.######} barrel={barrel:0.######} v={speed:0.#} m/s" +
            $" -> ratio={rawRatio:0.###} (compressed {ratio:0.###}) -> r95(GunData)={r95:0.###} m @100m" +
            (note != null ? $"  ({note})" : "") +
            $"  [mode={AiDispersionPlugin.CfgSpreadMode.Value}" +
            (gunData ? " -> applied]" : " -> not applied, Fixed " +
                $"{AiDispersionPlugin.CfgSpreadAccuracyAt100m.Value:0.###} is in effect]"));
    }

    /// <summary>Standard normal sample (Box-Muller).</summary>
    private static double Gaussian()
    {
        double u1 = 1.0 - Rng.NextDouble();
        double u2 = 1.0 - Rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
