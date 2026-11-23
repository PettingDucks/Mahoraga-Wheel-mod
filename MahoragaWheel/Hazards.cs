using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MahoragaWheel
{
    internal static class Hazards
    {
        // category, type, method
        private static readonly string[][] List = new string[][]
        {
            new[]{"Sharp","SawbladeScript","OnCollisionEnter2D"},
            new[]{"Sharp","FanTrap","OnWillRenderObject"},
            new[]{"Sharp","BearTrap","OnTriggerEnter2D"},
            new[]{"Sharp","SpikeStabberScript","CheckStab"},
            new[]{"Sharp","BarbedFence","OnTriggerEnter2D"},
            new[]{"Sharp","GroundGlass","OnCollisionEnter2D"},
            new[]{"Sharp","CactusScript","OnCollisionEnter2D"},
            new[]{"Impact","DamagingCrate","OnCollisionEnter2D"},
            new[]{"Explosion","WorldGeneration","CreateExplosion"},
            new[]{"Explosion","TurretScript","Shoot"},
            new[]{"Explosion","Observer","Update"},
            new[]{"Burn","CrystalBurning","Burn"},
            new[]{"Cold","ChilledLimb","Update"},
            new[]{"Cold","ColdPuffScript","Blow"},
            new[]{"Electric","CoilScript","Shock"},
            new[]{"Electric","CrystalElectric","Shock"},
            new[]{"Energy","LaserDroneScript","FireShot"},
            new[]{"Energy","SoundCannon","Update"},
            new[]{"Creature","SpiderHandler","DamageLimb"},
            new[]{"Creature","SpiderHandlerTBE","DamageLimb"},
            new[]{"Creature","CrystalEnemy","Lunge"},
            new[]{"Creature","BloodSuckerScript","BeginSucc"},
            new[]{"Creature","BloodSuckerScript","OnWillRenderObject"},
            new[]{"Creature","GrabberPlant","Update"},
            new[]{"Radiation","RadiationLine","Update"},
            new[]{"Radiation","RadioactiveObject","Update"},
            new[]{"Sepsis","CrystalSeptic","Update"},
            new[]{"Sepsis","XalorisScript","OnWillRenderObject"},
        };

        public static void PatchAll(Harmony h)
        {
            HarmonyMethod pre = new HarmonyMethod(typeof(Hazards).GetMethod("Pre", BindingFlags.Static | BindingFlags.NonPublic));
            HarmonyMethod post = new HarmonyMethod(typeof(Hazards).GetMethod("Post", BindingFlags.Static | BindingFlags.NonPublic));
            int ok = 0, fail = 0;
            for (int i = 0; i < List.Length; i++)
            {
                string tn = List[i][1], mn = List[i][2];
                try
                {
                    Type t = AccessTools.TypeByName(tn);
                    if (t == null) { Plugin.Log.LogWarning("[PATCH] type not found: " + tn); fail++; continue; }
                    MethodInfo m = AccessTools.DeclaredMethod(t, mn);
                    if (m == null) { Plugin.Log.LogWarning("[PATCH] method not found: " + tn + "." + mn); fail++; continue; }
                    h.Patch(m, pre, post);
                    ok++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[PATCH] failed " + tn + "." + mn + ": " + e.Message);
                    fail++;
                }
            }

            try
            {
                MethodInfo imp = AccessTools.DeclaredMethod(typeof(Limb), "ImpactDamage");
                h.Patch(imp, new HarmonyMethod(typeof(Hazards).GetMethod("ImpactPre", BindingFlags.Static | BindingFlags.NonPublic)),
                             new HarmonyMethod(typeof(Hazards).GetMethod("ImpactPost", BindingFlags.Static | BindingFlags.NonPublic)));
                ok++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[PATCH] failed Limb.ImpactDamage: " + e.Message); fail++; }

            Plugin.Log.LogInfo("[PATCH] hazard hooks ok=" + ok + " failed=" + fail);
        }

        private static string CatOf(MethodBase m)
        {
            for (int i = 0; i < List.Length; i++)
                if (List[i][1] == m.DeclaringType.Name && List[i][2] == m.Name) return List[i][0];
            return "?";
        }

        private static void Pre(MethodBase __originalMethod, out Snap __state)
        {
            __state = Tracker.Take();
            if (__state != null) { __state.prevCat = Resist.CurrentCat; Resist.CurrentCat = CatOf(__originalMethod); }
        }

        private static void Post(MethodBase __originalMethod, Snap __state)
        {
            if (__state == null) return;
            string cat = CatOf(__originalMethod);
            Resist.CurrentCat = __state.prevCat;
            Resist.Refund(cat, __state);
            Tracker.Record(cat + ":" + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name, __state);
        }

        private static void ImpactPre(Limb __instance, ref float force, out Snap __state)
        {
            __state = null;
            Body p = Tracker.PlayerBody();
            if (p != null && __instance != null && __instance.body == p)
            {
                __state = Tracker.Take();
                float r = Adaptation.Resistance("Impact");
                if (r > 0f)
                {
                    float before = force;
                    force = force * (1f - r);
                    Tracker.AddReduce("Impact.force", before, force);
                }
            }
        }

        private static void ImpactPost(Limb __instance, Snap __state)
        {
            if (__state == null) return;
            Tracker.Record("Impact:Limb.ImpactDamage(" + __instance.shortName + ")", __state);
        }
    }
}
