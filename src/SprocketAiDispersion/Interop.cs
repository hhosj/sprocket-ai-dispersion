using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SprocketAiDispersion;

/// <summary>
/// Small helpers for talking to the IL2CPP game types from a BepInEx 6 (IL2CPP) plugin.
///
/// Everything here works through <see cref="System.Reflection"/> against the proxy
/// (interop) assemblies that BepInEx generates in <c>BepInEx/interop</c>. That means the
/// plugin compiles without a single compile-time reference to the game, so it does not
/// break when the game changes - missing types/members just make a patch log a warning
/// and disable itself.
/// </summary>
internal static class Interop
{
    internal const BindingFlags InstanceAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    internal const BindingFlags StaticAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>Assemblies we may need to force-load if they are not loaded yet.</summary>
    private static readonly string[] AssemblyHints =
    {
        "Sprocket.Vehicles.AI",
        "Sprocket.Vehicles.AI.OrderConstruction",
        "Sprocket.Projectiles",
        "Sprocket.Orders",
        "Sprocket.Vehicles.Missions",
        "Assembly-CSharp",
    };

    internal static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = SafeGetType(asm, fullName);
            if (t != null) return t;
        }

        foreach (var hint in AssemblyHints)
        {
            try
            {
                var asm = Assembly.Load(new AssemblyName(hint));
                var t = SafeGetType(asm, fullName);
                if (t != null) return t;
            }
            catch
            {
                // ignored - the assembly simply may not exist in this game build
            }
        }

        return null;
    }

    private static Type SafeGetType(Assembly asm, string name)
    {
        try { return asm.GetType(name, false); }
        catch { return null; }
    }

    /// <summary>
    /// Il2CppInterop turns IL2CPP fields into C# *properties* (field accessors) on the proxy
    /// types. Some types/fields however end up as real fields, so we look for both.
    /// </summary>
    internal static MemberInfo FindMember(Type type, string name, bool isStatic)
    {
        var flags = isStatic ? StaticAny : InstanceAny;

        var prop = type.GetProperty(name, flags);
        if (prop != null) return prop;

        var field = type.GetField(name, flags);
        if (field != null) return field;

        return null;
    }

    internal static bool TryGetValue(Type type, object instance, string name, out object value)
    {
        value = null;
        var member = FindMember(type, name, instance == null);
        if (member == null) return false;

        try
        {
            switch (member)
            {
                case PropertyInfo p: value = p.GetValue(instance); return true;
                case FieldInfo f: value = f.GetValue(instance); return true;
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }

    internal static bool TrySetValue(Type type, object instance, string name, object value)
    {
        var member = FindMember(type, name, instance == null);
        if (member == null) return false;

        try
        {
            switch (member)
            {
                case PropertyInfo p: p.SetValue(instance, value); return true;
                case FieldInfo f: f.SetValue(instance, value); return true;
                default: return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Write a float at a fixed byte offset from the native object pointer.</summary>
    internal static unsafe void WriteFloatAtPointer(IntPtr pointer, int offset, float value)
    {
        *(float*)((byte*)pointer + offset) = value;
    }

    /// <summary>Read a float at a fixed byte offset from the native object pointer.</summary>
    internal static unsafe float ReadFloatAtPointer(IntPtr pointer, int offset)
        => *(float*)((byte*)pointer + offset);

    /// <summary>
    /// The proxy arrays Il2CppInterop generates (<c>Il2CppStructArray&lt;float&gt;</c>) implement
    /// <see cref="IList{T}"/>, which is the easiest way to read/write them without a
    /// compile-time reference to Il2CppInterop's array types.
    /// </summary>
    internal static bool TryAsFloatList(object array, out IList<float> list)
    {
        list = array as IList<float>;
        return list != null;
    }
}
