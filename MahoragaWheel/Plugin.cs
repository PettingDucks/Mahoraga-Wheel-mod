using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MahoragaWheel
{
    [BepInPlugin("local.mahoragawheel", "Mahoraga Wheel", "0.22.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private float flushTimer;
        private float slowTimer;
        private bool wasAlive = true;
        private Body lastBody;
        private float slowCheck;
        private float lastBrain = -1f;
        private float saveTimer;

        private void OnApplicationQuit()
        {
            Body b = Tracker.PlayerBody();
            if (b != null && b.alive) Persist.Save();
        }
        private WheelUI wheel;
        private WheelUI panelWheel;
        private LimbAdaptPanel limbPanel;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Mahoraga Wheel 0.22.1 (adaptation + reduction + regeneration + wheel + sounds) loading.");
            Adaptation.Init(Config);
            Sfx.Init(Config);
            Regen.Init(Config);
            Renewal.Init(Config);
            Persist.Init(Config);
            SoundDump.Init(Config, gameObject);
            Regrow.Init(Config, gameObject);
            StartCoroutine(Sfx.LoadGameSounds());
            Harmony h = new Harmony("local.mahoragawheel");
            Hazards.PatchAll(h);
            Resist.PatchAll(h);

            wheel = gameObject.AddComponent<WheelUI>();
            wheel.Init(Config);
            panelWheel = gameObject.AddComponent<WheelUI>();
            panelWheel.panelMode = true;
            panelWheel.Init(Config);
            limbPanel = gameObject.AddComponent<LimbAdaptPanel>();
            limbPanel.Init(Config);
            Adaptation.TurnEvent = delegate(string cat, int turns, bool full)
            {
                // When the wheel is on screen it plays the sound itself, on the frame each spin starts.
                // Hidden (F8) or not built: play the sound now so a turn is never silent.
                if (wheel.Animating) wheel.OnTurn(cat, turns, full);
                else Sfx.Play(full ? "adapted" : "turn");
            };
            Adaptation.ResetEvent = delegate(string why)
            {
                if (why == "death") Sfx.Play("reset");
                wheel.OnReset();
            };
            Renewal.OnRenewed = delegate
            {
                if (wheel.Animating) wheel.OnRenewal();
                else Sfx.Play("adapted");
            };
        }

        private void Update()
        {
            flushTimer += Time.unscaledDeltaTime;
            slowTimer += Time.unscaledDeltaTime;
            if (flushTimer >= 1.5f) { flushTimer = 0f; Tracker.Flush(); }

            Body b = Tracker.PlayerBody();
            if (b == null) return;
            if (b != lastBody)
            {
                // which character's adaptation is active is decided by the game's character id (see Persist.OnCharacter)
                lastBody = b;
                wasAlive = b.alive;
            }
            bool alive = b.alive;
            if (wasAlive && !alive) { Log.LogInfo("[PLAYER] died"); Adaptation.Reset("death"); Persist.Clear(); }
            if (!wasAlive && alive) Log.LogInfo("[PLAYER] alive again");
            wasAlive = alive;

            if (alive)
            {
                Adaptation.Tick(Time.deltaTime);
                Regen.Tick(b, Time.deltaTime);
                Vitals.Tick(b, Time.deltaTime);
                Renewal.Tick(b, Time.deltaTime);
                saveTimer += Time.unscaledDeltaTime;
                if (saveTimer >= 20f) { saveTimer = 0f; Persist.Save(); }
                slowCheck += Time.deltaTime;
                if (slowCheck >= 0.25f)
                {
                    float elapsedCheck = slowCheck;
                    slowCheck = 0f;
                    Resist.SampleInfection(b, elapsedCheck);
                    float bl = 0f, inf2 = 0f;
                    for (int i = 0; i < b.limbs.Length; i++)
                    {
                        Limb l = b.limbs[i];
                        if (l == null) continue;
                        bl += l.bleedAmount * (l.blockedBleeding ? 0f : 1f);
                        if (l.infectionAmount > inf2) inf2 = l.infectionAmount;
                    }
                    if (bl > 0.5f) Adaptation.MarkOngoing("Bleeding");
                    if (inf2 > Adaptation.InfectionVisibleAt.Value) Adaptation.MarkOngoing("Infection");
                    if (b.radiationSickness > 0.001f) Adaptation.MarkOngoing("Radiation");
                    if (b.venomTotal > 0.5f) Adaptation.MarkOngoing("Venom");
                    if (b.internalBleeding > 0.05f) Adaptation.MarkOngoing("Bleeding");
                    for (int li = 0; li < b.limbs.Length; li++)   // living with a fracture, a dislocation or a missing limb counts too
                    {
                        Limb il = b.limbs[li];
                        if (il != null && (il.broken || il.dislocated || il.dismembered)) { Adaptation.MarkOngoing("Injury"); break; }
                    }   // internal bleeding counts as bleeding
                    if (lastBrain < 0f) lastBrain = b.brainHealth;
                    // a stroke, brain health dropping, or just living with real brain damage (like Injury does for limbs)
                    if (b.strokeAmount > 0.01f || lastBrain - b.brainHealth > 0.0005f
                        || b.brainHealth < Adaptation.BrainDamagedBelow.Value) Adaptation.MarkOngoing("Brain");
                    lastBrain = b.brainHealth;
                    if (b.hunger < Adaptation.StarveThreshold.Value) Adaptation.MarkOngoing("Hunger");
                    if (b.thirst < Adaptation.ThirstThreshold.Value) Adaptation.MarkOngoing("Thirst");
                }
            }

            if (slowTimer >= 5f)
            {
                slowTimer = 0f;
                if (!alive) return;
                float bleed = 0f, inf = 0f;
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l == null) continue;
                    bleed += l.bleedAmount * (l.blockedBleeding ? 0f : 1f);
                    if (l.infectionAmount > inf) inf = l.infectionAmount;
                }
                Log.LogInfo("[SLOW] bleed=" + bleed.ToString("F1") + " maxInfection=" + inf.ToString("F1")
                    + " hunger=" + b.hunger.ToString("F1") + " thirst=" + b.thirst.ToString("F1")
                    + " radiation=" + b.radiationSickness.ToString("F2") + " temp=" + b.temperature.ToString("F1")
                    + " blood=" + b.bloodVolume.ToString("F1") + " brain=" + b.brainHealth.ToString("F1")
                    + " renewal=" + (Renewal.Progress * 100f).ToString("F1") + "% (" + Renewal.State + ", depth " + Renewal.Depth + ")" + " internalBleed=" + b.internalBleeding.ToString("F2") + " hearingLoss=" + b.hearingLoss.ToString("F1") + " immunity=" + b.immunity.ToString("F1") + " (earned +" + Regen.ImmunityBonus.ToString("F2") + ")");
            }
        }
    }

    // Snapshot of the player's health values (per limb and body totals).
    internal class Snap
    {
        public float[] skinL, muscleL, bleedL, painL;
        public float brain, blood, rad, septic, venom, temp, shock, internalB, hearing;
        public string prevCat;
        public float skin { get { float s = 0; for (int i = 0; i < skinL.Length; i++) s += skinL[i]; return s; } }
        public float muscle { get { float s = 0; for (int i = 0; i < muscleL.Length; i++) s += muscleL[i]; return s; } }
    }

    internal static class Tracker
    {
        private static Dictionary<string, float[]> acc = new Dictionary<string, float[]>();
        private static Dictionary<string, float[]> red = new Dictionary<string, float[]>();

        public static Body PlayerBody()
        {
            PlayerCamera pc = PlayerCamera.main;
            if (pc == null) return null;
            return pc.body;
        }

        public static Snap Take()
        {
            Body b = PlayerBody();
            if (b == null || b.limbs == null) return null;
            int n = b.limbs.Length;
            Snap s = new Snap();
            s.skinL = new float[n]; s.muscleL = new float[n]; s.bleedL = new float[n]; s.painL = new float[n];
            for (int i = 0; i < n; i++)
            {
                Limb l = b.limbs[i];
                if (l == null) continue;
                s.skinL[i] = l.skinHealth; s.muscleL[i] = l.muscleHealth;
                s.bleedL[i] = l.bleedAmount; s.painL[i] = l.pain;
            }
            s.brain = b.brainHealth; s.blood = b.bloodVolume; s.rad = b.radiationSickness;
            s.septic = b.septicShock; s.venom = b.venomTotal; s.temp = b.temperature;
            s.shock = b.shock; s.internalB = b.internalBleeding; s.hearing = b.hearingLoss;
            return s;
        }

        public static void Record(string key, Snap before)
        {
            Snap after = Take();
            if (before == null || after == null) return;
            float dSkin = before.skin - after.skin;
            float dMus = before.muscle - after.muscle;
            float dBrain = before.brain - after.brain;
            float dBlood = before.blood - after.blood;
            float dRad = after.rad - before.rad;
            float dSep = after.septic - before.septic;
            float dVen = after.venom - before.venom;
            float dTemp = after.temp - before.temp;
            float dHear = after.hearing - before.hearing;
            if (dSkin <= 0.001f && dMus <= 0.001f && dBrain <= 0.001f && dBlood <= 0.001f
                && dRad <= 0.0001f && dSep <= 0.0001f && dVen <= 0.0001f && dHear <= 0.0001f && Mathf.Abs(dTemp) <= 0.01f) return;
            int colon = key.IndexOf(':');
            float magnitude = Mathf.Max(0, dSkin) + Mathf.Max(0, dMus) + Mathf.Max(0, dBrain) * 4f + Mathf.Max(0, dBlood) * 3f
                + Mathf.Max(0, dRad) * 200f + Mathf.Max(0, dSep) * 50f + Mathf.Max(0, dHear) * 1f;
            if (colon > 0) Adaptation.MarkHit(key.Substring(0, colon), magnitude);
            if (dVen > 0.0001f) Adaptation.MarkHit("Venom", dVen * 3f);
            if (dBrain > 0.05f) Adaptation.MarkHit("Brain", dBrain * 8f);
            Add(key, new float[] { Mathf.Max(0, dSkin), Mathf.Max(0, dMus), Mathf.Max(0, dBrain), Mathf.Max(0, dBlood),
                Mathf.Max(0, dRad), Mathf.Max(0, dSep), Mathf.Max(0, dVen), dTemp, 1f });
        }

        public static void Add(string key, float[] v)
        {
            float[] cur;
            if (!acc.TryGetValue(key, out cur)) { acc[key] = v; return; }
            for (int i = 0; i < v.Length; i++) cur[i] += v[i];
        }

        // Records "before -> after" for scaled values, so the log can prove the reduction.
        public static void AddReduce(string key, float before, float after)
        {
            float[] cur;
            if (!red.TryGetValue(key, out cur)) { cur = new float[3]; red[key] = cur; }
            cur[0] += before; cur[1] += after; cur[2] += 1f;
        }

        public static void Flush()
        {
            if (acc.Count > 0)
            {
                foreach (KeyValuePair<string, float[]> kv in acc)
                {
                    float[] v = kv.Value;
                    Plugin.Log.LogInfo("[HIT] " + kv.Key + " x" + v[8].ToString("F0")
                        + " skin-" + v[0].ToString("F1") + " muscle-" + v[1].ToString("F1")
                        + " brain-" + v[2].ToString("F2") + " blood-" + v[3].ToString("F2")
                        + " rad+" + v[4].ToString("F3") + " septic+" + v[5].ToString("F3")
                        + " venom+" + v[6].ToString("F3") + " temp" + (v[7] >= 0 ? "+" : "") + v[7].ToString("F2"));
                }
                acc.Clear();
            }
            if (red.Count > 0)
            {
                foreach (KeyValuePair<string, float[]> kv in red)
                {
                    float[] v = kv.Value;
                    if (v[2] < 1f) continue;
                    Plugin.Log.LogInfo("[REDUCED] " + kv.Key + " avg " + (v[0] / v[2]).ToString("F3") + " -> " + (v[1] / v[2]).ToString("F3") + " (x" + v[2].ToString("F0") + ")");
                }
                red.Clear();
            }
        }
    }
}
