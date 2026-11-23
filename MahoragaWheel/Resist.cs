using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MahoragaWheel
{
    // Applies the adaptation resistance to damage and slow effects.
    internal static class Resist
    {
        // Category of the hazard currently executing (null when none).
        public static string CurrentCat;

        // The health panel picks its infection arrow from the sign of GetInfectionSpeed(). Adaptation can make
        // infection fall while that speed is still positive, which would show the wrong arrow. While the panel is
        // drawing, report a falling infection as a negative speed (display only, gameplay is untouched).
        public static bool InPanel;
        private static System.Collections.Generic.Dictionary<Limb, float> infPrev = new System.Collections.Generic.Dictionary<Limb, float>();
        private static System.Collections.Generic.Dictionary<Limb, float> infTrend = new System.Collections.Generic.Dictionary<Limb, float>();

        public static void SampleInfection(Body b, float elapsed)
        {
            if (b == null || b.limbs == null || elapsed <= 0f) return;
            for (int i = 0; i < b.limbs.Length; i++)
            {
                Limb l = b.limbs[i];
                if (l == null) continue;
                float prev;
                if (infPrev.TryGetValue(l, out prev))
                {
                    float rate = (l.infectionAmount - prev) / elapsed;
                    float old; infTrend.TryGetValue(l, out old);
                    infTrend[l] = Mathf.Lerp(old, rate, 0.3f);
                }
                infPrev[l] = l.infectionAmount;
            }
        }

        // the game sets the character id for a new game and again when a save is loaded
        private static void CharPost(int id) { Persist.OnCharacter(id); }

        private static void PanelPre() { InPanel = true; }
        private static void PanelPost() { InPanel = false; }

        private static bool IsPlayerLimb(Limb l)
        {
            Body p = Tracker.PlayerBody();
            return p != null && l != null && l.body == p;
        }

        private static bool IsPlayerBody(Body b)
        {
            Body p = Tracker.PlayerBody();
            return p != null && b == p;
        }

        public static float MaxResistance()
        {
            float m = 0f;
            for (int i = 0; i < Adaptation.Names.Length; i++)
            {
                float r = Adaptation.Own(Adaptation.Names[i]);   // earned, not the shared share, so it cannot inflate itself
                if (r > m) m = r;
            }
            return m;
        }

        // Give back part of what a hazard just took from the player.
        public static void Refund(string cat, Snap s)
        {
            float r = Adaptation.Resistance(cat);
            float rv = Adaptation.Resistance("Venom");
            float rbr = Adaptation.Resistance("Brain");
            if ((r <= 0f && rv <= 0f && rbr <= 0f) || s == null) return;
            Body b = Tracker.PlayerBody();
            if (b == null || b.limbs == null || b.limbs.Length != s.skinL.Length) return;
            float keep = 1f - r;
            float savedSkin = 0f, savedMus = 0f;
            for (int i = 0; i < b.limbs.Length; i++)
            {
                Limb l = b.limbs[i];
                if (l == null) continue;
                if (l.skinHealth < s.skinL[i]) { float lost = s.skinL[i] - l.skinHealth; l.skinHealth = s.skinL[i] - lost * keep; savedSkin += lost * r; }
                if (l.muscleHealth < s.muscleL[i]) { float lost = s.muscleL[i] - l.muscleHealth; l.muscleHealth = s.muscleL[i] - lost * keep; savedMus += lost * r; }
                if (l.bleedAmount > s.bleedL[i]) l.bleedAmount = s.bleedL[i] + (l.bleedAmount - s.bleedL[i]) * keep;
                if (l.pain > s.painL[i]) l.pain = s.painL[i] + (l.pain - s.painL[i]) * keep;
            }
            float keepB = keep * (1f - rbr);   // hazard resistance and brain resistance both count
            if (b.brainHealth < s.brain) b.brainHealth = s.brain - (s.brain - b.brainHealth) * keepB;
            if (b.hearingLoss > s.hearing) b.hearingLoss = s.hearing + (b.hearingLoss - s.hearing) * keep;
            if (b.bloodVolume < s.blood) b.bloodVolume = s.blood - (s.blood - b.bloodVolume) * keep;
            if (b.shock > s.shock) b.shock = s.shock + (b.shock - s.shock) * keep;
            if (b.internalBleeding > s.internalB) b.internalBleeding = s.internalB + (b.internalBleeding - s.internalB) * keep;
            if (b.radiationSickness > s.rad) b.radiationSickness = s.rad + (b.radiationSickness - s.rad) * keep;
            if (b.septicShock > s.septic) b.septicShock = s.septic + (b.septicShock - s.septic) * keep;
            if (b.venomTotal > s.venom) b.venomTotal = s.venom + (b.venomTotal - s.venom) * (1f - rv);
            if (savedSkin > 0f || savedMus > 0f) Tracker.AddReduce(cat + ".refund(skin+muscle given back)", 0f, savedSkin + savedMus);
        }

        public static void PatchAll(Harmony h)
        {
            int ok = 0, fail = 0;
            ok += Try(h, typeof(Limb), "get_totalBleedAmount", null, "BleedPost", ref fail);
            ok += Try(h, typeof(Limb), "GetInfectionSpeed", null, "InfectionPost", ref fail);
            ok += Try(h, typeof(Body), "get_BaseHungerRate", null, "HungerPost", ref fail);
            ok += Try(h, typeof(Body), "BaseThirstRate", null, "ThirstPost", ref fail);
            ok += Try(h, typeof(Limb), "get_SkinHealRate", null, "HealPost", ref fail);
            ok += Try(h, typeof(Limb), "get_MuscleHealRate", null, "HealPost", ref fail);
            ok += Try(h, typeof(WoundView), "UpdateView", "PanelPre", "PanelPost", ref fail);
            ok += Try(h, typeof(WoundView), "SetCharDetails", null, "CharPost", ref fail);
            ok += Try(h, typeof(Body), "HandlePeriodicChecks", null, "ImmunityPost", ref fail);   // keep this to one small postfix: several patches on this big method don't compile
            ok += Try(h, typeof(Body), "HandleBody", "BrainPre", "BrainPost", ref fail);
            ok += Try(h, typeof(Body), "HandleCirculation", "BrainPre", "BrainPost", ref fail);
            ok += Try(h, typeof(Body), "Disfigure", "BodyGuard", null, ref fail);
            ok += Try(h, typeof(Body), "RemoveEye", "BodyGuard", null, ref fail);
            ok += Try(h, typeof(Limb), "BreakBone", "BoneGuard", null, ref fail);
            ok += Try(h, typeof(Limb), "Dislocate", "BoneGuard", null, ref fail);
            ok += Try(h, typeof(Limb), "Dismember", "BoneGuard", null, ref fail);
            Plugin.Log.LogInfo("[PATCH] resistance hooks ok=" + ok + " failed=" + fail);
        }

        private static int Try(Harmony h, Type t, string method, string pre, string post, ref int fail)
        {
            try
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, method);
                if (m == null) { Plugin.Log.LogWarning("[PATCH] not found: " + t.Name + "." + method); fail++; return 0; }
                HarmonyMethod hp = pre == null ? null : new HarmonyMethod(typeof(Resist).GetMethod(pre, BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod hq = post == null ? null : new HarmonyMethod(typeof(Resist).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic));
                h.Patch(m, hp, hq);
                return 1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[PATCH] failed " + t.Name + "." + method + ": " + e.Message);
                fail++;
                return 0;
            }
        }

        // ---- slow effects ----
        private static void BleedPost(Limb __instance, ref float __result)
        {
            if (__result <= 0f || !IsPlayerLimb(__instance)) return;
            float r = Adaptation.Resistance("Bleeding");
            if (r <= 0f) return;
            float before = __result; __result = before * (1f - r);
            Tracker.AddReduce("Bleeding.totalBleedAmount", before, __result);
        }

        private static void InfectionPost(Limb __instance, ref float __result)
        {
            if (!IsPlayerLimb(__instance)) return;
            float r = Adaptation.Resistance("Infection");
            if (r <= 0f) return;
            float before = __result; __result = before * (1f - r) - Regen.InfectionDecay.Value * r * r;   // can go negative: then the game itself shrinks infection
            Tracker.AddReduce("Infection.speed", before, __result);
            if (InPanel && __result > 0f)
            {
                float trend;
                if (infTrend.TryGetValue(__instance, out trend) && trend < -0.03f) __result = -__result;   // arrow points down while it is really falling
            }
        }

        private static void HungerPost(Body __instance, ref float __result)
        {
            if (!IsPlayerBody(__instance)) return;
            float r = Adaptation.Resistance("Hunger");
            if (r <= 0f) return;
            float before = __result; __result = before * (1f - r);
            Tracker.AddReduce("Hunger.rate", before, __result);
        }

        private static void ThirstPost(Body __instance, ref float __result)
        {
            if (!IsPlayerBody(__instance)) return;
            float r = Adaptation.Resistance("Thirst");
            if (r <= 0f) return;
            float before = __result; __result = before * (1f - r);
            Tracker.AddReduce("Thirst.rate", before, __result);
        }

        // Healing: the best adaptation you have speeds up skin and muscle recovery.
        private static void HealPost(Limb __instance, ref float __result)
        {
            if (!IsPlayerLimb(__instance)) return;
            float r = MaxResistance();
            if (r <= 0f) return;
            float before = __result; __result = before * (1f + r);
            Tracker.AddReduce("Heal.rate", before, __result);
        }

        // The game recomputes immunity from your state every periodic check (about 100 when healthy, lower when
        // starving, dirty, feverish, etc). Earned immunity is added back afterwards and recomputes the infection multiplier.
        private static float lastImmunityWritten = float.NaN;

        // HandlePeriodicChecks runs often but only recomputes immunity now and then. The bonus must be added once per
        // recompute: if the value is still the one this patch wrote last time, the game has not recomputed it.
        private static void ImmunityPost(Body __instance)
        {
            if (!IsPlayerBody(__instance)) return;
            float bonus = Regen.ImmunityBonus;
            if (bonus <= 0f) return;
            float gv = __instance.immunity;
            if (gv == lastImmunityWritten) return;
            float target = Mathf.Min(Regen.ImmunityCap.Value, gv + bonus);
            if (target <= gv) { lastImmunityWritten = float.NaN; return; }
            __instance.immunity = target;
            lastImmunityWritten = target;
            if (__instance.immunityInfectionSpeed != null) __instance.curImmunityMult = __instance.immunityInfectionSpeed.Evaluate(target);
        }

        // two independent protections: 1 - (1-a)(1-b)
        private static float Combine(float a, float b) { return 1f - (1f - a) * (1f - b); }

        // Brain damage and strokes that come from the body's own ticks (low oxygen, blood loss, fever...).
        private static void BrainPre(Body __instance, out float[] __state)
        {
            __state = null;
            if (!IsPlayerBody(__instance)) return;
            if (Adaptation.Resistance("Brain") <= 0f) return;
            __state = new float[] { __instance.brainHealth, __instance.strokeAmount };
        }

        public static float BrainDrainTime = -999f;   // last time the body's own ticks lowered brain health

        private static void BrainPost(Body __instance, float[] __state)
        {
            if (__state == null) return;
            float br = Adaptation.Resistance("Brain");
            if (br <= 0f) return;
            float keep = 1f - br;
            if (__instance.brainHealth < __state[0])
            {
                BrainDrainTime = Time.time;   // the body is starving the brain right now: Regen holds off its brain mending
                // A tick that takes the brain to zero is death, as in the normal game: no refund can undo it.
                if (__instance.brainHealth <= 0f) return;
                float lost = __state[0] - __instance.brainHealth;
                __instance.brainHealth = __state[0] - lost * keep;
                Tracker.AddReduce("Brain.tick refund", 0f, lost * br);
            }
            if (__instance.strokeAmount > __state[1]) __instance.strokeAmount = __state[1] + (__instance.strokeAmount - __state[1]) * keep;
        }

        // Chance to avoid disfigurement and eye loss caused by a hazard.
        private static bool BodyGuard(Body __instance, MethodBase __originalMethod)
        {
            if (CurrentCat == null || !IsPlayerBody(__instance)) return true;
            float r = Combine(Adaptation.Resistance(CurrentCat), Adaptation.Resistance("Injury"));
            if (r <= 0f) return true;
            if (UnityEngine.Random.value < r)
            {
                Tracker.AddReduce("Blocked." + __originalMethod.Name, 0f, 1f);
                return false;
            }
            return true;
        }

        // Chance to avoid breaks, dislocations and dismemberment caused by a hazard.
        private static bool BoneGuard(Limb __instance, MethodBase __originalMethod)
        {
            if (CurrentCat == null || !IsPlayerLimb(__instance)) return true;
            float r = Combine(Adaptation.Resistance(CurrentCat), Adaptation.Resistance("Injury"));
            if (r <= 0f) return true;
            if (UnityEngine.Random.value < r)
            {
                Tracker.AddReduce("Blocked." + __originalMethod.Name, 0f, 1f);
                return false;
            }
            return true;
        }
    }
}
