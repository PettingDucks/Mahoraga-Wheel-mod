using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // Adaptation state for the current character: progress and turns for each kind of harm.
    internal class CatState
    {
        public float progress;      // seconds of exposure toward the next turn
        public int turns;           // completed turns, 0..MaxTurns
        public float activeUntil;   // game time until which this category counts as "hurting"
        public float lastHurt = -999f; // game time of the last hit or ongoing exposure
        public float lastBonus = -999f; // game time the last hit bonus was granted
    }

    internal static class Adaptation
    {
        public static ConfigEntry<float> SecondsPerTurn;
        public static ConfigEntry<float> ResistancePerTurn;
        public static ConfigEntry<int> MaxTurns;
        public static ConfigEntry<float> HitWindow;
        public static ConfigEntry<float> StarveThreshold;
        public static ConfigEntry<float> ThirstThreshold;
        public static ConfigEntry<float> InfectionVisibleAt;
        public static ConfigEntry<float> BrainDamagedBelow;
        public static ConfigEntry<float> SpillOver;
        public static ConfigEntry<float> BonusBase;
        public static ConfigEntry<float> BonusPerDamage;
        public static ConfigEntry<float> BonusMax;
        public static ConfigEntry<float> BonusCooldown;

        private static Dictionary<string, CatState> cats = new Dictionary<string, CatState>();
        private static float statusTimer;

        public static Action<string, int, bool> TurnEvent;   // category, turns, fullyAdapted
        public static Action<string> ResetEvent;             // reason
        public static string LastCat;
        public static float LastHitTime = -999f;   // last time a discrete hit hurt you (renewal pauses after it)
        public static float LastCatTime = -999f;

        public static IEnumerable<KeyValuePair<string, CatState>> All { get { return cats; } }

        public static readonly string[] Names = new string[]
        {
            "Impact","Sharp","Explosion","Burn","Cold","Electric","Energy","Creature",
            "Radiation","Sepsis","Venom","Brain","Injury","Vitals","Bleeding","Infection","Hunger","Thirst"
        };

        public static void Init(ConfigFile cfg)
        {
            SecondsPerTurn = cfg.Bind<float>("Wheel", "SecondsPerTurn", 180f, "Seconds of exposure to a damage type needed for one wheel turn.");
            ResistancePerTurn = cfg.Bind<float>("Wheel", "ResistancePerTurn", 0.1875f, "Resistance gained per completed turn (0.1875 x 4 turns = 75%).");
            MaxTurns = cfg.Bind<int>("Wheel", "MaxTurns", 4, "Turns until a damage type is fully adapted.");
            HitWindow = cfg.Bind<float>("Wheel", "HitWindowSeconds", 3f, "After a hit, the type counts as active for this many seconds.");
            StarveThreshold = cfg.Bind<float>("Wheel", "StarveBelowHunger", 25f, "Hunger value below which starvation counts as hurting (guess, to be tuned).");
            ThirstThreshold = cfg.Bind<float>("Wheel", "DehydrateBelowThirst", 25f, "Thirst value below which dehydration counts as hurting (guess, to be tuned).");
            SpillOver = cfg.Bind<float>("Wheel", "GeneralResilience", 0.4f, "How much of your best adaptation also protects you against every other type, so you do not have to adapt to each one from scratch. 0 turns it off.");
            BrainDamagedBelow = cfg.Bind<float>("Wheel", "BrainDamagedBelow", 90f, "Living with brain health below this counts as Brain exposure, the same way a broken or missing limb counts for Injury.");
            InfectionVisibleAt = cfg.Bind<float>("Wheel", "InfectionCountsAbove", 25f, "Infection only counts as hurting once a limb's infection passes this value. 25 is when the health panel starts showing it.");
            BonusBase = cfg.Bind<float>("Hit Bonus", "BaseSeconds", 2f, "Seconds of progress a damaging hit adds on top of the active window.");
            BonusPerDamage = cfg.Bind<float>("Hit Bonus", "SecondsPerDamage", 0.03f, "Extra seconds per point of damage dealt, so bigger hits count for more.");
            BonusMax = cfg.Bind<float>("Hit Bonus", "MaxSeconds", 6f, "Most progress a single hit can add.");
            BonusCooldown = cfg.Bind<float>("Hit Bonus", "CooldownSeconds", 1f, "A type can only earn the hit bonus this often, so rapid ticks and multi-limb hits count once.");
            for (int i = 0; i < Names.Length; i++) cats[Names[i]] = new CatState();
        }

        public static CatState Get(string cat)
        {
            CatState s;
            if (!cats.TryGetValue(cat, out s)) { s = new CatState(); cats[cat] = s; }
            return s;
        }

        // What you have earned for this type alone (this is what the wheel list shows), 0..0.75.
        public static float Own(string cat)
        {
            CatState s = Get(cat);
            return Mathf.Clamp01(s.turns * ResistancePerTurn.Value);
        }

        // What actually protects you: your own adaptation plus a share of your best adaptation to anything else,
        // so the body adapts in general. Never above the 4-turn cap.
        public static float Resistance(string cat)
        {
            float own = Own(cat);
            float share = SpillOver == null ? 0f : SpillOver.Value;
            if (share <= 0f) return own;
            float best = 0f;
            for (int i = 0; i < Names.Length; i++)
            {
                if (Names[i] == cat) continue;
                float o = Own(Names[i]);
                if (o > best) best = o;
            }
            float eff = 1f - (1f - own) * (1f - share * best);
            return Mathf.Min(eff, Mathf.Clamp01(MaxTurns.Value * ResistancePerTurn.Value));
        }

        // Called when a hazard of this category actually hurt the player.
        public static void MarkHit(string cat, float magnitude)
        {
            if (!Gate.Allowed) return;
            CatState hs = Get(cat);
            if (magnitude > 0f && hs.turns < MaxTurns.Value && Time.time - hs.lastBonus >= BonusCooldown.Value)
            {
                hs.lastBonus = Time.time;
                hs.progress += Mathf.Clamp(BonusBase.Value + magnitude * BonusPerDamage.Value, BonusBase.Value, BonusMax.Value);
            }
            MarkHit(cat);
        }

        public static void MarkHit(string cat)
        {
            if (!Gate.Allowed) return;
            CatState hs = Get(cat);
            hs.activeUntil = Time.time + HitWindow.Value;
            hs.lastHurt = Time.time;
            LastHitTime = Time.time;
            LastCat = cat; LastCatTime = Time.time;
        }

        // Called every frame while the effect is ongoing (bleeding, infection, ...).
        public static void MarkOngoing(string cat)
        {
            if (!Gate.Allowed) return;
            CatState os = Get(cat);
            os.activeUntil = Time.time + 0.5f;
            os.lastHurt = Time.time;
            LastCat = cat; LastCatTime = Time.time;
        }

        public static void Tick(float dt)
        {
            float now = Time.time;
            bool any = false;
            foreach (KeyValuePair<string, CatState> kv in cats)
            {
                CatState s = kv.Value;
                if (s.turns >= MaxTurns.Value) continue;
                if (now > s.activeUntil) continue;
                s.progress += dt;
                any = true;
                if (s.progress >= SecondsPerTurn.Value)
                {
                    s.progress = Mathf.Max(0f, s.progress - SecondsPerTurn.Value);   // keep any overflow
                    s.turns++;
                    Plugin.Log.LogInfo("[WHEEL] " + kv.Key + " turned: " + s.turns + "/" + MaxTurns.Value
                        + " resistance " + (Own(kv.Key) * 100f).ToString("F0") + "%"
                        + (s.turns >= MaxTurns.Value ? " (fully adapted)" : ""));
                    if (TurnEvent != null)
                    {
                        try { TurnEvent(kv.Key, s.turns, s.turns >= MaxTurns.Value); }
                        catch (Exception e) { Plugin.Log.LogWarning("[WHEEL] turn event failed: " + e.Message); }
                    }
                }
            }
            statusTimer += dt;
            if (any && statusTimer >= 10f)
            {
                statusTimer = 0f;
                string line = "";
                foreach (KeyValuePair<string, CatState> kv in cats)
                {
                    CatState s = kv.Value;
                    if (s.turns == 0 && s.progress <= 0f) continue;
                    line += kv.Key + " " + s.turns + "/" + MaxTurns.Value + " (" + s.progress.ToString("F0") + "s)  ";
                }
                if (line.Length > 0) Plugin.Log.LogInfo("[WHEEL] status: " + line);
            }
        }

        public static void Reset(string why)
        {
            foreach (KeyValuePair<string, CatState> kv in cats)
            {
                kv.Value.progress = 0f; kv.Value.turns = 0; kv.Value.activeUntil = 0f;
            }
            Regen.ImmunityBonus = 0f;
            Renewal.Reset();
            Vitals.Reset();
            Plugin.Log.LogInfo("[WHEEL] all adaptations reset (" + why + ")");
            LastCat = null;
            if (ResetEvent != null)
            {
                try { ResetEvent(why); }
                catch (Exception e) { Plugin.Log.LogWarning("[WHEEL] reset event failed: " + e.Message); }
            }
        }
    }
}
