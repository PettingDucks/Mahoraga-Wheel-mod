using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // One-time helper: saves chosen game sounds as WAV files so they can be listened to outside the game.
    // Only runs when [Sound Dump] Enabled = true. Files stay on this PC; nothing is shipped with the mod.
    internal class SoundDump : MonoBehaviour
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<string> Names;

        private const string DefaultNames =
            "steel,scrapmetal,wrenchhit,robothit,piston,unpiston,Hydraulics,elevator,elevatorEnd,lockBreak,unlock," +
            "beartrap,beartrapunlatch,gunrack,shotgunrack,gununrack,gunsafety,gunjam,prosthesisclick,prostheticBreak," +
            "miniClick,click,switch,handCrankLoop,landheavy1,landheavy2,fence,droneOpen,droneClose,shuttleOpen,endlabDoor," +
            "techbounce,boneHit,headhit,stunhit,levelup,blueHeartObtain,crystalheal,crystaltick,heartthump-heavy,time," +
            "fansnap,centrifuge,boneweld,prosthesisrepair,lockpickLoop,tutorialdoorsequence,tutorialdispense";

        public static void Init(ConfigFile cfg, GameObject host)
        {
            Enabled = cfg.Bind<bool>("Sound Dump", "Enabled", false,
                "Save the sounds listed below as WAV files to BepInEx/plugins/MahoragaWheel/sound_preview on the next launch, then turn itself off.");
            Names = cfg.Bind<string>("Sound Dump", "Names", DefaultNames, "Comma-separated game sound names to save.");
            if (Enabled.Value) host.AddComponent<SoundDump>();
        }

        private IEnumerator Start()
        {
            // Let the main menu finish loading first.
            for (int i = 0; i < 180; i++) yield return null;

            string dir = Path.Combine(Path.Combine(Paths.PluginPath, "MahoragaWheel"), "sound_preview");
            Directory.CreateDirectory(dir);

            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in Names.Value.Split(','))
            {
                string t = n.Trim();
                if (t.Length > 0) wanted.Add(t);
            }

            Dictionary<string, AudioClip> found = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            Collect(Resources.FindObjectsOfTypeAll<AudioClip>(), wanted, found);
            if (found.Count < wanted.Count)
            {
                Plugin.Log.LogInfo("[DUMP] " + found.Count + "/" + wanted.Count + " already loaded, loading the rest from Resources...");
                yield return null;
                Collect(Resources.LoadAll<AudioClip>(""), wanted, found);
            }

            int saved = 0;
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, AudioClip> kv in found)
            {
                AudioClip c = kv.Value;
                if (c.loadState != AudioDataLoadState.Loaded)
                {
                    c.LoadAudioData();
                    float wait = 0f;
                    while (c.loadState == AudioDataLoadState.Loading && wait < 5f) { wait += Time.unscaledDeltaTime; yield return null; }
                }
                try
                {
                    float[] data = new float[c.samples * c.channels];
                    if (!c.GetData(data, 0))
                    {
                        Plugin.Log.LogWarning("[DUMP] could not read " + c.name + " (load type " + c.loadType + ")");
                        continue;
                    }
                    WriteWav(Path.Combine(dir, c.name + ".wav"), data, c.channels, c.frequency);
                    lines.Add(c.name + "|" + c.length.ToString("F2") + "|" + c.channels + "|" + c.frequency);
                    saved++;
                }
                catch (Exception e) { Plugin.Log.LogWarning("[DUMP] " + c.name + " failed: " + e.Message); }
                yield return null;
            }
            File.WriteAllLines(Path.Combine(dir, "list.txt"), lines.ToArray());

            List<string> missing = new List<string>();
            foreach (string w in wanted) if (!found.ContainsKey(w)) missing.Add(w);
            Plugin.Log.LogInfo("[DUMP] saved " + saved + " sounds to " + dir
                + (missing.Count > 0 ? "  not found: " + string.Join(", ", missing.ToArray()) : ""));

            Enabled.Value = false;   // one-time
            found.Clear();
            Resources.UnloadUnusedAssets();   // drop the sounds LoadAll pulled in that the game is not using
            Destroy(this);
        }

        private static void Collect(AudioClip[] clips, HashSet<string> wanted, Dictionary<string, AudioClip> found)
        {
            for (int i = 0; i < clips.Length; i++)
            {
                AudioClip c = clips[i];
                if (c == null || !wanted.Contains(c.name) || found.ContainsKey(c.name)) continue;
                found[c.name] = c;
            }
        }

        private static void WriteWav(string path, float[] data, int channels, int rate)
        {
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                int bytes = data.Length * 2;
                w.Write(new char[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes);
                w.Write(new char[] { 'W', 'A', 'V', 'E' });
                w.Write(new char[] { 'f', 'm', 't', ' ' }); w.Write(16);
                w.Write((short)1); w.Write((short)channels); w.Write(rate);
                w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
                w.Write(new char[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
                for (int i = 0; i < data.Length; i++)
                    w.Write((short)Mathf.RoundToInt(Mathf.Clamp(data[i], -1f, 1f) * 32767f));
            }
        }
    }
}
