using System;
using System.Collections.Generic;

namespace VortexVapor
{
    // Recognises BDArmory craft by module class name, so there is no hard dependency on BDArmory.
    public static class BDArmoryCraft
    {
        static readonly Dictionary<Type, bool> missileTypes = new Dictionary<Type, bool>();

        // A fired missile: a craft with a BDArmory missile module (derived from MissileBase) and no
        // weapon manager, which separates it from an aircraft with missiles on its rails. BDArmory
        // makes the active vessel a missile to follow it, so the vapor and the vortex manager must
        // skip it.
        public static bool IsMissile(Vessel v)
        {
            if (v == null || v.parts == null) return false;
            bool missile = false;
            for (int i = 0; i < v.parts.Count; i++)
            {
                Part p = v.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    PartModule pm = p.Modules[m];
                    if (pm == null) continue;
                    if (pm.moduleName == "MissileFire") return false;
                    if (!missile && IsMissileModule(pm.GetType())) missile = true;
                }
            }
            return missile;
        }

        static bool IsMissileModule(Type t)
        {
            bool result;
            if (missileTypes.TryGetValue(t, out result)) return result;
            result = false;
            for (Type b = t; b != null && b != typeof(PartModule); b = b.BaseType)
                if (b.Name == "MissileBase") { result = true; break; }
            missileTypes[t] = result;
            return result;
        }
    }
}
