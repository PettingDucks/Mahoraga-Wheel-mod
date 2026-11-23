using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // Wheel sounds. Every turn, full adaptation and Renewal use only one of the game's own sounds (the piston by
    // default). The death sweep is generated in code; the chime and chord are only a backup if that sound is missing.
    internal static class Sfx
    {
        private static ConfigEntry<float> Volume;
        private static Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private static AudioSource src;
        private static AudioSource gameSrc;
        private const int Rate = 44100;

        private static ConfigEntry<string> TurnSound;
        private static ConfigEntry<float> PitchVariation;
        private static AudioClip gameTurn;   // the game's own sound, played from the game's files (not shipped with the mod)

        public static void Init(ConfigFile cfg)
        {
            Volume = cfg.Bind<float>("Sound", "Volume", 0.5f, "Volume of wheel sounds, 0 to 1. Set 0 to mute.");
            TurnSound = cfg.Bind<string>("Sound", "TurnSound", "piston",
                "Name of the game sound played when the wheel turns. Leave empty to use the built-in chime.");
            PitchVariation = cfg.Bind<float>("Sound", "PitchVariation", 0.08f,
                "How far the turn sound's pitch can randomly move from the original each time (0.08 = up to 8% higher or lower).");
        }

        // Finds the game sound once at the main menu, so the first turn does not stall while it loads.
        public static IEnumerator LoadGameSounds()
        {
            for (int i = 0; i < 120; i++) yield return null;
            string want = TurnSound == null ? "" : TurnSound.Value.Trim();
            if (want.Length == 0) yield break;
            gameTurn = Find(Resources.FindObjectsOfTypeAll<AudioClip>(), want);
            if (gameTurn == null)
            {
                gameTurn = Find(Resources.LoadAll<AudioClip>(""), want);
                Resources.UnloadUnusedAssets();   // keeps the one we hold, frees the rest
            }
            if (gameTurn != null)
            {
                gameTurn.LoadAudioData();
                Plugin.Log.LogInfo("[SFX] turn sound: game sound '" + gameTurn.name + "' (" + gameTurn.length.ToString("F2") + "s)");
            }
            else Plugin.Log.LogWarning("[SFX] game sound '" + want + "' not found, using the built-in chime.");
        }

        // Random pitch near the original; the heavy kick (full adaptation, renewal) sits a little lower.
        public static float KickPitch(bool heavy)
        {
            float v = PitchVariation == null ? 0f : Mathf.Clamp(PitchVariation.Value, 0f, 0.5f);
            return Mathf.Clamp(UnityEngine.Random.Range(1f - v, 1f + v) * (heavy ? 0.9f : 1f), 0.3f, 3f);
        }

        // One wheel revolution's sound, started by the wheel on the same frame its spin starts.
        public static void PlayKick(float pitch, bool heavy)
        {
            try
            {
                if (Volume == null || Volume.Value <= 0.001f) return;
                Ensure();
                float vol = Mathf.Clamp01(Volume.Value);
                AudioClip c;
                if (gameTurn != null)
                {
                    // Metal only: the heavy kick is the same piston, just lower (see KickPitch). Its own source,
                    // because pitch applies to everything an audio source is playing.
                    gameSrc.pitch = pitch;
                    gameSrc.PlayOneShot(gameTurn, vol);
                }
                else if (clips.TryGetValue(heavy ? "adapted" : "turn", out c)) src.PlayOneShot(c, vol);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[SFX] kick failed: " + e.Message); }
        }

        private static AudioClip Find(AudioClip[] all, string name)
        {
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && string.Equals(all[i].name, name, StringComparison.OrdinalIgnoreCase)) return all[i];
            return null;
        }

        private static void Ensure()
        {
            if (src != null) return;
            GameObject go = new GameObject("MahoragaWheelAudio");
            UnityEngine.Object.DontDestroyOnLoad(go);
            src = go.AddComponent<AudioSource>();
            src.spatialBlend = 0f;
            src.playOnAwake = false;
            gameSrc = go.AddComponent<AudioSource>();
            gameSrc.spatialBlend = 0f;
            gameSrc.playOnAwake = false;
            clips["turn"] = Chime(new float[] { 523.25f, 783.99f }, 0.14f, 0.55f);                       // C5 -> G5, quick two-note chime
            clips["adapted"] = Chord(new float[] { 523.25f, 659.25f, 783.99f, 1046.5f }, 1.1f);           // bright C major chord
            clips["reset"] = Sweep(330f, 110f, 0.7f);                                                     // falling tone
        }

        public static void Play(string name)
        {
            try
            {
                if (Volume == null || Volume.Value <= 0.001f) return;
                Ensure();
                float vol = Mathf.Clamp01(Volume.Value);
                if (name == "turn" || name == "adapted") { PlayKick(KickPitch(name == "adapted"), name == "adapted"); return; }
                AudioClip c;
                if (clips.TryGetValue(name, out c)) src.PlayOneShot(c, vol);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[SFX] " + name + " failed: " + e.Message); }
        }

        private static AudioClip Make(string name, float[] data)
        {
            AudioClip c = AudioClip.Create(name, data.Length, 1, Rate, false);
            c.SetData(data, 0);
            return c;
        }

        // Several notes in sequence, each a decaying sine with a little harmonic.
        private static AudioClip Chime(float[] notes, float noteLen, float amp)
        {
            int per = (int)(Rate * noteLen);
            int tail = (int)(Rate * 0.35f);
            float[] d = new float[per * notes.Length + tail];
            for (int n = 0; n < notes.Length; n++)
            {
                for (int i = 0; i < per + tail && n * per + i < d.Length; i++)
                {
                    float t = i / (float)Rate;
                    float env = Mathf.Exp(-t * 7f) * Mathf.Min(1f, i / 120f);
                    float s = Mathf.Sin(2f * Mathf.PI * notes[n] * t) + 0.35f * Mathf.Sin(2f * Mathf.PI * notes[n] * 2f * t);
                    d[n * per + i] += s * env * amp * 0.6f;
                }
            }
            for (int i = 0; i < d.Length; i++) d[i] = Mathf.Clamp(d[i], -0.45f, 0.45f);
            return Make("mw_turn", d);
        }

        private static AudioClip Chord(float[] notes, float len)
        {
            float[] d = new float[(int)(Rate * len)];
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Exp(-t * 3.2f) * Mathf.Min(1f, i / 200f);
                float s = 0f;
                for (int n = 0; n < notes.Length; n++)
                {
                    float start = n * 0.06f;               // slight arpeggio
                    if (t < start) continue;
                    float tt = t - start;
                    s += Mathf.Sin(2f * Mathf.PI * notes[n] * tt) * Mathf.Exp(-tt * 1.5f);
                }
                d[i] = Mathf.Clamp(s * env * 0.11f, -0.45f, 0.45f);   // four notes can add up past 1.0 and distort, so keep it low
            }
            return Make("mw_adapted", d);
        }

        private static AudioClip Sweep(float f0, float f1, float len)
        {
            float[] d = new float[(int)(Rate * len)];
            float phase = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)d.Length;
                float f = Mathf.Lerp(f0, f1, t);
                phase += 2f * Mathf.PI * f / Rate;
                float env = (1f - t) * Mathf.Min(1f, i / 200f);
                d[i] = Mathf.Clamp((Mathf.Sin(phase) + 0.4f * Mathf.Sin(phase * 0.5f)) * env * 0.28f, -0.45f, 0.45f);
            }
            return Make("mw_reset", d);
        }
    }
}
