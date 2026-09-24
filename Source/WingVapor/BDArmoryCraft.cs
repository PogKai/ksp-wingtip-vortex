using System;
using System.Collections.Generic;

namespace VortexVapor
{
    // Recognises BDArmory craft by module class name, so there is no hard dependency on BDArmory.
    public static class BDArmoryCraft
    {
        static readonly Dictionary<Type, bool> missileTypes = new Dictionary<Type, bool>();

        // A fired missile: a craft carrying one of BDArmory's missile modules (anything derived
        // from MissileBase) and no weapon manager. The weapon manager is what separates it from an
        // aircraft with missiles still on its rails, which carries both.
        //
        // BDArmory moves the active vessel onto a missile to follow it, so without this the wing
        // vapor followed the camera off the aircraft that fired it, and the vortex manager spent
        // a controller slot on it.
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
