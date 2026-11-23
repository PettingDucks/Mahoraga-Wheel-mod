using System;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // Counter-effects: the more adapted you are to something, the more your body fights it off.
    // Strength scales with resistance squared, so partial adaptation does very little and
    // full adaptation gives a slow, modest recovery (not a heal-everything button).
    internal static class Regen
    {
        private static ConfigEntry<float> Strength;
        private static ConfigEntry<float> RecentSeconds;
        private static ConfigEntry<float> ImmunityRate;
        private static ConfigEntry<bool> RecoverPermanent;
        private static ConfigEntry<float> RecoverSeconds;
        private static ConfigEntry<float> BoneHealBonus;
        private static float baseBone, baseDisl, recoverProg, regrowProg;
        private static ConfigEntry<bool> RegrowLimbs;
        private static ConfigEntry<float> RegrowSeconds;
        private static bool baseCaptured;
        public static ConfigEntry<float> ImmunityCap;
        public static ConfigEntry<float> InfectionDecay;
        public static float ImmunityBonus;   // earned immunity, added on top of the game's own immunity value; resets on death
        private static float logTimer, healedAcc, bledAcc;
        private static readonly string[] HealthCats = new string[] { "Impact", "Sharp", "Explosion", "Burn", "Cold", "Electric", "Energy", "Creature" };

        public static void Init(ConfigFile cfg)
        {
            Strength = cfg.Bind<float>("Regeneration", "Strength", 1f, "Multiplier for adaptation regeneration. 0 turns it off, 2 doubles it.");
            ImmunityRate = cfg.Bind<float>("Immunity", "GainPerSecond", 0.03f, "Immunity points earned per second at full infection adaptation (scaled by resistance squared). Very slow on purpose.");
            InfectionDecay = cfg.Bind<float>("Regeneration", "InfectionFightBack", 9f, "How hard a well-adapted body fights infection. The game grows infection in proportion to how much there is, so it needs a pull in proportion too. Subtracted from the infection growth speed, scaled by resistance squared (higher = stronger).");
            ImmunityCap = cfg.Bind<float>("Immunity", "Cap", 100f, "Adaptation never lifts your immunity above this.");
            RecoverPermanent = cfg.Bind<bool>("Recovery", "HealPermanentInjuries", true, "Let a well-adapted body slowly undo disfigurement and lost eyes (experimental: visuals may not refresh).");
            RegrowLimbs = cfg.Bind<bool>("Recovery", "RegrowLostLimbs", true, "A well-adapted body slowly regrows a lost arm or leg (uses the game's own reattach routine).");
            RegrowSeconds = cfg.Bind<float>("Recovery", "SecondsPerRegrow", 600f, "Recovery time units to regrow a lost limb. Counts faster the more adapted you are (resistance squared), about 18 minutes at the cap.");
            RecoverSeconds = cfg.Bind<float>("Recovery", "SecondsPerRecovery", 300f, "Recovery time units per healed injury. Time counts faster the more adapted you are (resistance squared), about 9 minutes at the cap.");
            BoneHealBonus = cfg.Bind<float>("Recovery", "BoneHealBonus", 2f, "Extra bone and dislocation healing speed at full adaptation (2 = up to 3x speed).");
            RecentSeconds = cfg.Bind<float>("Regeneration", "OnlyAfterHitSeconds", 0f, "0 = regeneration is always on. A number limits damage regeneration to types that hurt you within that many seconds.");
        }

        private static float Sq(float r) { return r * r; }

        public static float StrengthValue() { return Strength == null ? 1f : Strength.Value; }
        public static float BoneBonusValue() { return BoneHealBonus == null ? 0f : BoneHealBonus.Value; }

        // strongest damage-type regeneration power right now (for the limb readout)
        public static float HealthPower()
        {
            float best = 0f;
            for (int i = 0; i < HealthCats.Length; i++)
            {
                CatState s = Adaptation.Get(HealthCats[i]);
                if (RecentSeconds != null && RecentSeconds.Value > 0f && Time.time - s.lastHurt > RecentSeconds.Value) continue;
                float p = Sq(Adaptation.Resistance(HealthCats[i]));
                if (p > best) best = p;
            }
            float ip = Sq(Adaptation.Resistance("Injury"));
            if (ip > best) best = ip;
            return best;
        }

        public static void Tick(Body b, float dt)
        {
            if (Strength == null || Strength.Value <= 0f || b == null || b.limbs == null || dt <= 0f) return;
            float k = Strength.Value;

            // best squared resistance among health-damage types hurt recently
            float healPower = 0f;
            for (int i = 0; i < HealthCats.Length; i++)
            {
                CatState s = Adaptation.Get(HealthCats[i]);
                if (RecentSeconds.Value > 0f && Time.time - s.lastHurt > RecentSeconds.Value) continue;
                float p = Sq(Adaptation.Resistance(HealthCats[i]));
                if (p > healPower) healPower = p;
            }
            float injuryPower = Sq(Adaptation.Resistance("Injury"));   // living through injuries counts too, even when nothing hurt you recently
            if (injuryPower > healPower) healPower = injuryPower;
            float bleedPower = Sq(Adaptation.Resistance("Bleeding"));
            float immPower = Sq(Adaptation.Resistance("Infection"));
            if (immPower > 0f) ImmunityBonus = Mathf.Min(ImmunityCap.Value, ImmunityBonus + ImmunityRate.Value * immPower * k * dt);
            float infPower = Sq(Adaptation.Resistance("Infection"));

            for (int i = 0; i < b.limbs.Length; i++)
            {
                Limb l = b.limbs[i];
                if (l == null || l.dismembered) continue;
                if (healPower > 0f)
                {
                    float amt = 0.6f * healPower * k * dt;   // hit points per second per limb at full strength = 0.34
                    if (l.skinHealth < 100f) { float add = Mathf.Min(amt, 100f - l.skinHealth); l.skinHealth += add; healedAcc += add; }
                    if (l.muscleHealth < 100f) { float add = Mathf.Min(amt, 100f - l.muscleHealth); l.muscleHealth += add; healedAcc += add; }
                }
                if (bleedPower > 0f && l.bleedAmount > 0f)
                {
                    float cut = Mathf.Min(l.bleedAmount, 0.45f * bleedPower * k * dt);
                    l.bleedAmount -= cut; bledAcc += cut;
                }
                if (infPower > 0f && l.infectionAmount > 0f)
                    l.infectionAmount = Mathf.Max(0f, l.infectionAmount - 0.3f * infPower * k * dt);
            }

            if (bleedPower > 0f && b.bloodVolume < 100f)
                b.bloodVolume = Mathf.Min(100f, b.bloodVolume + 0.1f * bleedPower * k * dt);

            // slow stored effects fade a little faster, proportionally
            float radP = Sq(Adaptation.Resistance("Radiation"));
            if (radP > 0f && b.radiationSickness > 0f) b.radiationSickness = Mathf.Max(0f, b.radiationSickness * (1f - 0.02f * radP * k * dt));
            float sepP = Sq(Adaptation.Resistance("Sepsis"));
            if (sepP > 0f && b.septicShock > 0f) b.septicShock = Mathf.Max(0f, b.septicShock * (1f - 0.02f * sepP * k * dt));
            float venP = Sq(Adaptation.Resistance("Venom"));
            if (venP > 0f && b.venomTotal > 0f) b.venomTotal = Mathf.Max(0f, b.venomTotal * (1f - 0.02f * venP * k * dt));

            // internal bleeding is bleeding too: the body closes it up as it adapts
            if (bleedPower > 0f)
            {
                if (b.internalBleeding > 0f) b.internalBleeding = Mathf.Max(0f, b.internalBleeding * (1f - 0.03f * bleedPower * k * dt));
                if (b.hemothorax > 0f) b.hemothorax = Mathf.Max(0f, b.hemothorax * (1f - 0.03f * bleedPower * k * dt));
            }

            // hearing comes back faster if you are adapted to the things that deafen you
            float hearP = Sq(Mathf.Max(Adaptation.Resistance("Explosion"), Adaptation.Resistance("Energy")));
            if (hearP > 0f && b.hearingLoss > 0f) b.hearingLoss = Mathf.Max(0f, b.hearingLoss * (1f - 0.03f * hearP * k * dt));

            // brain damage slowly mends
            float brainP = Sq(Adaptation.Resistance("Brain"));
            if (brainP > 0f && b.brainHealth < 100f) b.brainHealth = Mathf.Min(100f, b.brainHealth + 0.02f * brainP * k * dt);
            if (brainP > 0f && b.strokeAmount > 0f) b.strokeAmount = Mathf.Max(0f, b.strokeAmount * (1f - 0.02f * brainP * k * dt));

            // broken bones and dislocations heal faster
            if (!baseCaptured) { baseBone = Limb.boneHealSpeed; baseDisl = Limb.dislocationHealSpeed; baseCaptured = true; }
            float mr = Mathf.Max(Resist.MaxResistance(), Adaptation.Resistance("Injury"));   // living with injuries adapts you to them
            Limb.boneHealSpeed = baseBone * (1f + BoneHealBonus.Value * mr);
            Limb.dislocationHealSpeed = baseDisl * (1f + BoneHealBonus.Value * mr);

            // permanent injuries: a slow clock heals one at a time
            if (RecoverPermanent.Value && (b.disfigured || b.eyeGone || b.bothEyesGone || b.hasPulmonaryEmbolism))
            {
                recoverProg += dt * Sq(mr) * k;
                if (recoverProg >= RecoverSeconds.Value)
                {
                    recoverProg = 0f;
                    string what = null;
                    if (b.disfigured) { b.disfigured = false; what = "disfigurement"; }
                    else if (b.bothEyesGone) { b.bothEyesGone = false; what = "one eye (both were gone)"; }
                    else if (b.eyeGone) { b.eyeGone = false; what = "a lost eye"; }
                    else if (b.hasPulmonaryEmbolism) { b.hasPulmonaryEmbolism = false; what = "a pulmonary embolism"; }
                    if (what != null) Plugin.Log.LogInfo("[REGEN] recovered: " + what);
                }
            }

            // lost limbs regrow, very slowly, using the game's own reattach routine
            if (RegrowLimbs.Value)
            {
                bool lost = false;
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb ll = b.limbs[i];
                    if (ll != null && ll.dismembered && !ll.prostheticInfo.hasProsthetic) { lost = true; break; }
                }
                if (lost && !Regrow.Busy && !Renewal.Restoring)   // never two regrows at once; Renewal's restore takes priority
                {
                    regrowProg += dt * Sq(mr) * k;
                    if (regrowProg >= RegrowSeconds.Value)
                    {
                        regrowProg = 0f;
                        // grow it back piece by piece, bone first, over a minute
                        Limb first = null;
                        for (int i = 0; i < b.limbs.Length && first == null; i++)
                        {
                            Limb ll = b.limbs[i];
                            if (ll != null && ll.dismembered && !ll.prostheticInfo.hasProsthetic) first = ll;
                        }
                        if (first != null && Regrow.Begin(b, first, 60f)) { }
                        else try
                        {
                            bool[] was = new bool[b.limbs.Length];
                            for (int i = 0; i < b.limbs.Length; i++) was[i] = b.limbs[i] != null && b.limbs[i].dismembered;
                            for (int i = 0; i < b.limbs.Length; i++)
                            {
                                Limb ll = b.limbs[i];
                                if (ll == null || !ll.dismembered || ll.prostheticInfo.hasProsthetic) continue;
                                ll.Reattach();
                                break;
                            }
                            string names = "";
                            for (int i = 0; i < b.limbs.Length; i++)
                            {
                                Limb ll = b.limbs[i];
                                if (ll != null && was[i] && !ll.dismembered)
                                {
                                    ll.skinHealth = 25f; ll.muscleHealth = 25f; ll.bleedAmount = 0f;   // grows back fragile, then heals
                                    names += ll.shortName + " ";
                                }
                            }
                            if (names.Length > 0) Plugin.Log.LogInfo("[REGEN] regrew: " + names);
                        }
                        catch (Exception e) { Plugin.Log.LogWarning("[REGEN] regrow failed: " + e.Message); }
                    }
                }
            }

            // everything else that wears the body down eases off a little, scaled by your best adaptation
            float gen = Sq(mr) * k;
            if (gen > 0f)
            {
                float f = 1f - 0.02f * gen * dt;
                if (b.traumaAmount > 0f) b.traumaAmount *= f;
                if (b.horrifiedLevel > 0f) b.horrifiedLevel *= f;
                if (b.sicknessAmount > 0f) b.sicknessAmount *= f;
                if (b.barotrauma > 0f) b.barotrauma *= f;
            }

            logTimer += dt;
            if (logTimer >= 10f)
            {
                logTimer = 0f;
                if (healedAcc > 0.01f || bledAcc > 0.01f)
                    Plugin.Log.LogInfo("[REGEN] last 10s: limb health restored " + healedAcc.ToString("F1") + ", bleeding closed " + bledAcc.ToString("F1"));
                healedAcc = 0f; bledAcc = 0f;
            }
        }
    }
}
