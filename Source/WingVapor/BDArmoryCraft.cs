using System;
using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    // Recognises BDArmory craft by module class name, so there is no hard dependency on BDArmory.
    public static class BDArmoryCraft
    {
        static readonly Dictionary<Type, bool> missileTypes = new Dictionary<Type, bool>();

        // A fired BDArmory missile (missile module, no weapon manager).
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

        public static bool IsMissilePart(Part part)
        {
            if (part == null || part.Modules == null) return false;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module != null && IsMissileModule(module.GetType())) return true;
            }
            return false;
        }

        // a store under a wing: a drop tank (fuel, no engine or intake), a rail, pylon,
        // A hardpoint, pod, or its parent, surface-attached below a wing.
        // directly or through up to two other parts. It carries no vapor; the airframe's own parts stay.
        const float StoreDropM = 0.12f;
        static readonly string[] MountWords = { "rail", "pylon", "hardpoint", "pod", "rack" };
        public static bool IsWingStore(Part part, Vector3 up)
        {
            // the cones and sections stacked on a store are the store's too.
            for (Part p = part; p != null && !Lifts(p); p = p.parent)
                if (IsStoreRoot(p, up)) return true;
            return false;
        }

        static bool IsStoreRoot(Part part, Vector3 up)
        {
            if (IsMissilePart(part)) return true;
            if (!IsStoreKind(part) && !CarriesStore(part, 2)) return false;
            Part p = part;
            for (int depth = 0; depth < 3 && p != null; depth++)
            {
                if (Lifts(p) || p.parent == null || p.attachMode != AttachModes.SRF_ATTACH) return false;
                if (Lifts(p.parent)) return Vector3.Dot(part.transform.position - p.parent.transform.position, up) < -StoreDropM;
                p = p.parent;
            }
            return false;
        }

        // A mount (a strut or adapter holding a tank or missile beneath it).
        static bool CarriesStore(Part part, int depth)
        {
            if (depth <= 0 || part.children == null || Lifts(part)) return false;
            foreach (Part child in part.children)
                if (child.attachMode == AttachModes.SRF_ATTACH && (IsMissilePart(child) || IsStoreKind(child) || CarriesStore(child, depth - 1))) return true;
            return false;
        }

        static bool IsStoreKind(Part part)
        {
            string name = (part.partInfo != null ? part.partInfo.name : part.name).ToLowerInvariant();
            foreach (string word in MountWords) if (name.Contains(word)) return true;
            bool fuel = false;
            if (part.Resources != null) foreach (PartResource r in part.Resources) if (r.resourceName == "LiquidFuel") fuel = true;
            if (!fuel) return false;
            foreach (PartModule m in part.Modules) if (m is ModuleEngines || m is ModuleResourceIntake) return false;
            return true;
        }

        static bool Lifts(Part part)
        {
            if (part == null || part.Modules == null) return false;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m is ModuleLiftingSurface || (m != null && m.moduleName == "FARWingAerodynamicModel")) return true;
            }
            return false;
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
