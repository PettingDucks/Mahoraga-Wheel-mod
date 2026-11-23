using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // Adaptation is only for the hardest runs. A run qualifies when its actual settings are at least as hard
    // as one of these presets, whatever preset it started from:
    //   Desolate  - loot at 0.4x or less and traps at 1.5x or more
    //   Unchipped - the unchipped setting is on
    //   Endless   - the endless setting is on
    // On any other run the whole mod stays off: no wheel, no adaptation, no healing.
    internal static class Gate
    {
        private static ConfigEntry<bool> RequireHardRun;
        private static ConfigEntry<float> MaxLoot;
        private static ConfigEntry<float> MinTraps;

        private static bool known;
        private static bool allowed;
        private static float nextCheck;

        public static void Init(ConfigFile cfg)
        {
            RequireHardRun = cfg.Bind<bool>("Hard Runs Only", "Enabled", true,
                "Only allow adaptation on the hardest runs (Desolate, Unchipped or Endless, or custom settings at least that hard). On other runs the mod stays off. Set false to allow it on every run.");
            MaxLoot = cfg.Bind<float>("Hard Runs Only", "MaxLootDensity", 0.4f, "A run counts as Desolate-hard when its loot density is at or below this...");
            MinTraps = cfg.Bind<float>("Hard Runs Only", "MinTrapDensity", 1.5f, "...and its trap density is at or above this.");
        }

        // True when the current run allows adaptation. Re-checked every couple of seconds, and right away
        // when a character is loaded, because the run's settings are only filled in once a run starts.
        public static bool Allowed
        {
            get
            {
                if (RequireHardRun == null || !RequireHardRun.Value) return true;
                if (!known || Time.unscaledTime >= nextCheck) Refresh();
                return allowed;
            }
        }

        public static void Refresh()
        {
            nextCheck = Time.unscaledTime + 2f;
            bool was = allowed, wasKnown = known;
            string why;
            allowed = Evaluate(out why);
            known = true;
            if (!wasKnown || was != allowed)
                Plugin.Log.LogInfo("[GATE] adaptation " + (allowed ? "ON" : "OFF") + " for this run (" + why + ")");
        }

        private static bool Evaluate(out string why)
        {
            Dictionary<string, object> rs = null;
            try { rs = WorldGeneration.runSettings; } catch (Exception) { }
            if (rs == null || rs.Count == 0) { why = "run settings not loaded yet"; return false; }
            bool unchipped = GetBool(rs, "unchipped");
            bool endless = GetBool(rs, "endless");
            float loot = GetFloat(rs, "baselootdensity", 1f);
            float traps = GetFloat(rs, "basetrapdensity", 1f);
            why = "loot " + loot.ToString("0.##") + "x, traps " + traps.ToString("0.##") + "x"
                + (unchipped ? ", unchipped" : "") + (endless ? ", endless" : "");
            if (unchipped || endless) return true;
            return loot <= MaxLoot.Value + 0.001f && traps >= MinTraps.Value - 0.001f;
        }

        private static bool GetBool(Dictionary<string, object> rs, string key)
        {
            object v;
            return rs.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        private static float GetFloat(Dictionary<string, object> rs, string key, float fallback)
        {
            object v;
            if (!rs.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToSingle(v); } catch (Exception) { return fallback; }
        }
    }
}
