using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace MahoragaWheel
{
    // Adaptation belongs to one character in one world. The game gives every character a random id (shown as
    // "#12345" on the health panel), creates it for a new game and restores it when a save is loaded, so the
    // save file is keyed by that id: rejoin the same game = same data, new game / other world / multiplayer
    // character = its own data. Dying wipes that character's file.
    internal static class Persist
    {
        private static ConfigEntry<bool> Enabled;
        public static int CurrentId;

        private static string Dir { get { return Path.Combine(Paths.ConfigPath, "mahoragawheel_saves"); } }
        private static string FileFor(int id) { return Path.Combine(Dir, "char_" + id + ".state"); }

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind<bool>("Saving", "KeepAdaptationBetweenSessions", true, "Remember each character's adaptation, immunity and renewal when you quit, separately per character/world. Wiped when that character dies.");
            // the old single shared slot (from before per-character saving) is retired, not deleted
            try
            {
                string old = Path.Combine(Paths.ConfigPath, "local.mahoragawheel.state");
                if (File.Exists(old)) { File.Move(old, old + ".old"); Plugin.Log.LogInfo("[SAVE] retired the old shared save slot (kept as .old)"); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[SAVE] could not retire old slot: " + e.Message); }
        }

        // Called whenever the game sets the character id (new game, or a save being loaded).
        public static void OnCharacter(int id)
        {
            if (id == 0 || id == CurrentId) return;
            CurrentId = id;
            Adaptation.Reset("character #" + id);
            Load();
        }

        private static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        public static void Save()
        {
            if (Enabled == null || !Enabled.Value || CurrentId == 0) return;
            try
            {
                Directory.CreateDirectory(Dir);
                List<string> lines = new List<string>();
                lines.Add("v1");
                for (int i = 0; i < Adaptation.Names.Length; i++)
                {
                    CatState s = Adaptation.Get(Adaptation.Names[i]);
                    lines.Add("cat|" + Adaptation.Names[i] + "|" + s.turns + "|" + F(s.progress));
                }
                lines.Add("immunity|" + F(Regen.ImmunityBonus));
                lines.Add("renewal|" + F(Renewal.Progress));
                lines.Add("phase|" + (Renewal.Restoring ? "restoring" : Renewal.Mending ? "mending" : "building"));
                File.WriteAllLines(FileFor(CurrentId), lines.ToArray());
            }
            catch (Exception e) { Plugin.Log.LogWarning("[SAVE] failed: " + e.Message); }
        }

        public static void Load()
        {
            if (Enabled == null || !Enabled.Value || CurrentId == 0) return;
            try
            {
                string path = FileFor(CurrentId);
                if (!File.Exists(path)) { Plugin.Log.LogInfo("[SAVE] character #" + CurrentId + ": no adaptation yet (fresh start)"); return; }
                string[] lines = File.ReadAllLines(path);
                int restored = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] p = lines[i].Split('|');
                    if (p.Length == 4 && p[0] == "cat")
                    {
                        CatState s = Adaptation.Get(p[1]);
                        s.turns = Math.Max(0, Math.Min(Adaptation.MaxTurns.Value, int.Parse(p[2], CultureInfo.InvariantCulture)));
                        s.progress = float.Parse(p[3], CultureInfo.InvariantCulture);
                        if (s.turns > 0) restored++;
                    }
                    else if (p.Length == 2 && p[0] == "immunity") Regen.ImmunityBonus = float.Parse(p[1], CultureInfo.InvariantCulture);
                    else if (p.Length == 2 && p[0] == "renewal") Renewal.Progress = float.Parse(p[1], CultureInfo.InvariantCulture);
                    else if (p.Length == 2 && p[0] == "phase") Renewal.LoadPhase(p[1]);
                }
                Plugin.Log.LogInfo("[SAVE] character #" + CurrentId + ": adaptation restored (" + restored + " types)");
            }
            catch (Exception e) { Plugin.Log.LogWarning("[SAVE] load failed: " + e.Message); }
        }

        public static void Clear()
        {
            if (CurrentId == 0) return;
            try { string path = FileFor(CurrentId); if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Plugin.Log.LogWarning("[SAVE] clear failed: " + e.Message); }
        }
    }
}
