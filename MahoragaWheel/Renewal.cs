using System;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // RENEWAL: the long-term goal of adapting. A slow meter fills only while you are adapted AND surviving
    // (fed, watered, not being hurt). When it is full the body MENDS back to brand new over a stretch of time
    // (not instantly): every value drifts toward healthy, then breaks/joints/lost limbs are fixed at the end.
    // Mending pauses while you are hit or starving, and it eats food and water as it goes.
    internal static class Renewal
    {
        private static ConfigEntry<bool> Enabled;
        private static ConfigEntry<int> MinTurns;
        private static ConfigEntry<int> FullTurns;
        private static ConfigEntry<float> Minutes;
        private static ConfigEntry<float> CalmSeconds;
        private static ConfigEntry<float> FoodCost;
        private static ConfigEntry<float> WaterCost;
        private static ConfigEntry<float> MendSeconds;

        public static float Progress;          // 0..1 meter (or mending progress while mending)
        public static string State = "Locked";
        public static int Depth;
        public static bool Mending;
        public static Action OnRenewed;        // fired when mending starts

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind<bool>("Renewal", "Enabled", true, "A fully adapted body slowly earns a complete restoration to brand new.");
            MinTurns = cfg.Bind<int>("Renewal", "TurnsToUnlock", 6, "Total wheel turns (all types added together) before renewal starts to build at all.");
            FullTurns = cfg.Bind<int>("Renewal", "TurnsForFullSpeed", 20, "Total wheel turns for renewal to build at full speed. Fewer turns build it proportionally slower.");
            Minutes = cfg.Bind<float>("Renewal", "MinutesAtFullSpeed", 25f, "Minutes of calm, fed survival for one renewal at full speed. Sleeping counts 1.5x.");
            CalmSeconds = cfg.Bind<float>("Renewal", "PauseAfterHitSeconds", 30f, "Renewal (and mending) pauses for this long after anything hits you.");
            FoodCost = cfg.Bind<float>("Renewal", "FoodCost", 25f, "Hunger spent over the course of mending (rebuilding takes resources).");
            WaterCost = cfg.Bind<float>("Renewal", "WaterCost", 25f, "Thirst spent over the course of mending.");
            MendSeconds = cfg.Bind<float>("Renewal", "MendSeconds", 120f, "Once renewal is earned, how long the body takes to mend TEMPORARY harm (wounds, bleeding, infection, blood, broken bones, joints). Pauses while you are in danger.");
            PermanentMinutes = cfg.Bind<float>("Renewal", "PermanentMinutes", 10f, "After that, minutes to restore PERMANENT damage (brain, lost limbs, eyes, hearing, disfigurement), one at a time, worst first. The time is shared between them. Hits do not stop it.");
        }
        private static ConfigEntry<float> PermanentMinutes;

        public static void Reset() { Progress = 0f; State = "Locked"; Depth = 0; Mending = false; Restoring = false; RestoringWhat = ""; itemProg = 0f; limbGrowing = false; }

        public static void Tick(Body b, float dt)
        {
            if (Enabled == null || !Enabled.Value || b == null || dt <= 0f) return;
            int total = 0;
            for (int i = 0; i < Adaptation.Names.Length; i++) total += Adaptation.Get(Adaptation.Names[i]).turns;
            Depth = total;

            bool starving = b.hunger < Adaptation.StarveThreshold.Value || b.thirst < Adaptation.ThirstThreshold.Value;
            bool danger = Time.time - Adaptation.LastHitTime < CalmSeconds.Value;

            if (Restoring)
            {
                if (starving) { State = "Starving"; return; }   // hits do not stop it, hunger does
                RestoreStep(b, dt);
                return;
            }
            if (Mending)
            {
                if (starving) { State = "Starving"; return; }
                if (danger) { State = "In danger"; return; }
                State = "Mending";
                MendStep(b, dt);
                return;
            }

            if (total < MinTurns.Value) { State = "Locked"; return; }
            if (starving) { State = "Starving"; return; }
            if (danger) { State = "In danger"; return; }

            float depth = Mathf.Clamp01(total / Mathf.Max(1f, (float)FullTurns.Value));
            float rate = depth / Mathf.Max(60f, Minutes.Value * 60f);
            if (b.sleeping) rate *= 1.5f;
            State = b.sleeping ? "Resting" : "Renewing";
            Progress += rate * dt;
            if (Progress >= 1f)
            {
                Progress = 0f;
                Mending = true;
                State = "Mending";
                Plugin.Log.LogInfo("[RENEWAL] earned - the body starts mending (" + MendSeconds.Value.ToString("F0") + "s)");
                if (OnRenewed != null)
                {
                    try { OnRenewed(); } catch (Exception e) { Plugin.Log.LogWarning("[RENEWAL] event failed: " + e.Message); }
                }
            }
        }

        // move v toward target by fraction f
        private static float To(float v, float target, float f) { return v + (target - v) * f; }

        // Phase 1 (MendSeconds): temporary harm - wounds, bleeding, infection, blood, bones, joints.
        private static void MendStep(Body b, float dt)
        {
            float dur = Mathf.Max(5f, MendSeconds.Value);
            float remaining = Mathf.Max(dt, (1f - Progress) * dur);
            float f = Mathf.Clamp01(dt / remaining);      // reaches the target exactly when mending ends
            Progress = Mathf.Min(1f, Progress + dt / dur);

            b.bloodVolume = To(b.bloodVolume, 100f, f); b.bloodOxygen = To(b.bloodOxygen, 100f, f);
            b.strokeAmount = To(b.strokeAmount, 0f, f); b.fibrillationProgress = To(b.fibrillationProgress, 0f, f);
            b.septicShock = To(b.septicShock, 0f, f); b.temperature = To(b.temperature, 37f, f); b.sicknessAmount = To(b.sicknessAmount, 0f, f);
            b.radiationSickness = To(b.radiationSickness, 0f, f); b.internalBleeding = To(b.internalBleeding, 0f, f); b.hemothorax = To(b.hemothorax, 0f, f);
            b.traumaAmount = To(b.traumaAmount, 0f, f); b.brainGrowSickness = To(b.brainGrowSickness, 0f, f);
            b.venomCurrent = To(b.venomCurrent, 0f, f); b.venomTotal = To(b.venomTotal, 0f, f); b.venomFake = To(b.venomFake, 0f, f);
            b.shock = To(b.shock, 0f, f); b.clawHealth = To(b.clawHealth, 100f, f); b.barotrauma = To(b.barotrauma, 0f, f);
            if (b.limbs != null)
            {
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l == null || l.dismembered) continue;
                    l.skinHealth = To(l.skinHealth, 100f, f); l.muscleHealth = To(l.muscleHealth, 100f, f);
                    l.bleedAmount = To(l.bleedAmount, 0f, f); l.infectionAmount = To(l.infectionAmount, 0f, f); l.pain = To(l.pain, 0f, f);
                }
            }
            // the cost is paid as the body rebuilds
            b.hunger = Mathf.Max(5f, b.hunger - FoodCost.Value * dt / dur);
            b.thirst = Mathf.Max(5f, b.thirst - WaterCost.Value * dt / dur);

            if (Progress >= 1f) Finish(b);
        }

        // End of phase 1: temporary things that cannot drift (breaks, joints, clots, heart rhythm) are fixed now.
        // Then phase 2 starts if anything permanent is left.
        private static void Finish(Body b)
        {
            Mending = false;
            Progress = 0f;
            b.hasPulmonaryEmbolism = false;
            b.bloodVesselSize = 1f; b.bloodViscosity = 0f;
            if (b.heartRate < 20f) { b.heartRate = 70f; if (b.bloodPressure < 60f) b.bloodPressure = 60f; }
            if (b.limbs != null)
            {
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l == null || l.dismembered) continue;
                    try { l.Heal(); }
                    catch (Exception e) { Plugin.Log.LogWarning("[RENEWAL] limb " + l.shortName + ": " + e.Message); }
                }
            }
            Plugin.Log.LogInfo("[RENEWAL] temporary harm mended");

            int count = CountPermanent(b);
            if (count > 0)
            {
                Restoring = true;
                Progress = 0f;
                itemProg = 0f;
                itemSeconds = Mathf.Max(10f, PermanentMinutes.Value * 60f) / count;   // the whole job takes PermanentMinutes, shared out
                Plugin.Log.LogInfo("[RENEWAL] restoring " + count + " permanent injur" + (count == 1 ? "y" : "ies")
                    + ", " + itemSeconds.ToString("F0") + "s each, one at a time");
            }
            else Plugin.Log.LogInfo("[RENEWAL] body restored - nothing permanent to fix");
        }

        // ---------- Phase 2: permanent damage, one at a time, worst first ----------
        // Order: brain, then lost limbs, then eyes, then hearing, then disfigurement.
        // Hits do not stop it once it has started; starving still pauses it (rebuilding needs food and water).
        public static bool Restoring;
        private static float itemProg, itemSeconds = 60f;
        private static int doneCount, totalCount;
        public static string RestoringWhat = "";

        private static bool BrainHurt(Body b) { return b.brainHealth < 99f; }
        private static bool HearingHurt(Body b) { return b.hearingLoss > 1f; }
        // The first piece of a lost arm or leg: it is gone but the piece above it is not.
        // A whole lost arm (upper arm, forearm, hand) counts as ONE item; Regrow grows its pieces in order.
        private static bool IsLostTop(Limb l)
        {
            if (l == null || !l.dismembered || l.prostheticInfo.hasProsthetic) return false;
            if (l.connectedLimbs != null)
                for (int i = 0; i < l.connectedLimbs.Length; i++)
                {
                    Limb c = l.connectedLimbs[i];
                    if (c != null && c.distanceToHeart < l.distanceToHeart && c.dismembered) return false;
                }
            return true;
        }

        private static Limb LostLimb(Body b)
        {
            if (b.limbs == null) return null;
            for (int i = 0; i < b.limbs.Length; i++) if (IsLostTop(b.limbs[i])) return b.limbs[i];
            return null;
        }

        private static int CountPermanent(Body b)
        {
            int n = 0;
            if (BrainHurt(b)) n++;
            if (b.limbs != null)
                for (int i = 0; i < b.limbs.Length; i++) if (IsLostTop(b.limbs[i])) n++;
            if (b.bothEyesGone) n += 2; else if (b.eyeGone) n++;
            if (HearingHurt(b)) n++;
            if (b.disfigured) n++;
            totalCount = n; doneCount = 0;
            return n;
        }

        private static bool needRecount;

        // Quitting mid-repair keeps the phase. Restoring resumes from the item it was on (that item starts over).
        public static void LoadPhase(string phase)
        {
            Mending = phase == "mending";
            Restoring = phase == "restoring";
            if (Restoring) { needRecount = true; itemProg = 0f; }
        }

        private static void RestoreStep(Body b, float dt)
        {
            if (needRecount)
            {
                needRecount = false;
                int left = CountPermanent(b);
                itemSeconds = left > 0 ? Mathf.Max(10f, PermanentMinutes.Value * 60f) / left : 60f;
            }
            State = "Restoring";   // short: the health menu label is narrow (the item is in the log)

            // a limb is regrowing piece by piece: wait for it (its pieces come back one at a time, shoulder to hand)
            if (limbGrowing)
            {
                if (Regrow.Busy)
                {
                    if (totalCount > 0) Progress = Mathf.Clamp01((doneCount + Regrow.JobProgress) / totalCount);
                    return;
                }
                limbGrowing = false; itemProg = 0f; doneCount++;
                Plugin.Log.LogInfo("[RENEWAL] restored: " + RestoringWhat + " (" + doneCount + "/" + totalCount + ")");
            }

            // pick the current item, worst first (re-checked every frame, so anything healed some other way is skipped)
            Limb lost = null;
            string what;
            if (BrainHurt(b)) what = "brain";
            else if ((lost = LostLimb(b)) != null) what = "limb";
            else if (b.bothEyesGone || b.eyeGone) what = "eye";
            else if (HearingHurt(b)) what = "hearing";
            else if (b.disfigured) what = "face";
            else { EndRestore(); return; }

            RestoringWhat = what == "limb" ? lost.shortName : what;
            if (what == "limb")
            {
                if (Regrow.Busy) return;   // the slow background regrow is already growing something: let it finish first
                if (Regrow.Begin(b, lost, itemSeconds)) { limbGrowing = true; return; }
                // could not start the visual regrow: fall back to the timed reattach below
            }
            float remaining = Mathf.Max(dt, itemSeconds - itemProg);
            float f = Mathf.Clamp01(dt / remaining);
            itemProg += dt;

            // things with a level fade back smoothly over the item's time; things that are simply gone return at the end
            if (what == "brain") b.brainHealth = To(b.brainHealth, 100f, f);
            else if (what == "hearing") b.hearingLoss = To(b.hearingLoss, 0f, f);

            if (totalCount > 0) Progress = Mathf.Clamp01((doneCount + Mathf.Clamp01(itemProg / itemSeconds)) / totalCount);
            if (itemProg < itemSeconds) return;

            // item done
            itemProg = 0f; doneCount++;
            try
            {
                if (what == "brain") b.brainHealth = 100f;
                else if (what == "hearing") b.hearingLoss = 0f;
                else if (what == "limb") { lost.Reattach(); lost.skinHealth = 25f; lost.muscleHealth = 25f; }   // grows back fragile
                else if (what == "eye") { if (b.bothEyesGone) b.bothEyesGone = false; else b.eyeGone = false; }
                else if (what == "face") b.disfigured = false;
                Plugin.Log.LogInfo("[RENEWAL] restored: " + RestoringWhat + " (" + doneCount + "/" + totalCount + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[RENEWAL] restoring " + RestoringWhat + " failed: " + e.Message); }
        }

        private static bool limbGrowing;

        private static void EndRestore()
        {
            Restoring = false; Progress = 0f; RestoringWhat = ""; itemProg = 0f;
            Plugin.Log.LogInfo("[RENEWAL] body fully restored to brand new");
        }
    }
}
