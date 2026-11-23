using UnityEngine;

namespace MahoragaWheel
{
    // Vitals: one catch-all adaptation for the whole body, so there is no need for a separate type per condition.
    // Whenever any vital sign is getting worse - for any reason (cold, heat, low oxygen, a failing heart, shock,
    // sickness, trauma, pressure) - it counts as Vitals exposure and builds turns like every other type.
    // Its resistance slows every one of those declines by the same share, and at high adaptation a stopped heart
    // can restart on its own (rarely, so treatment still matters).
    internal static class Vitals
    {
        private static bool have;
        private static float pTemp, pOxy, pFib, pCons, pShock, pPainShock, pSick, pTrauma, pHorror, pBaro;
        public static int Restarts;

        public static void Reset() { have = false; }

        private static void Snap(Body b)
        {
            pTemp = b.temperature; pOxy = b.bloodOxygen; pFib = b.fibrillationProgress; pCons = b.consciousness;
            pShock = b.shock; pPainShock = b.painShock; pSick = b.sicknessAmount; pTrauma = b.traumaAmount;
            pHorror = b.horrifiedLevel; pBaro = b.barotrauma;
            have = true;
        }

        // value went up, and up is bad
        private static float Up(float prev, float now, float floor, float keep, ref bool worse)
        {
            if (now > prev + 0.00001f && now > floor) { worse = true; return prev + (now - prev) * keep; }
            return now;
        }

        // value went down, and down is bad
        private static float Down(float prev, float now, float ceiling, float keep, ref bool worse)
        {
            if (now < prev - 0.00001f && now < ceiling) { worse = true; return prev + (now - prev) * keep; }
            return now;
        }

        public static void Tick(Body b, float dt)
        {
            if (b == null || dt <= 0f) return;
            if (!have) { Snap(b); return; }
            float r = Adaptation.Resistance("Vitals");
            float keep = 1f - r;
            bool worse = false;

            // temperature: moving away from 37 by more than a degree (freezing or overheating)
            float t = b.temperature;
            float awayPrev = Mathf.Abs(pTemp - 37f), awayNow = Mathf.Abs(t - 37f);
            if (awayNow > 1f && awayNow > awayPrev + 0.00001f) { worse = true; b.temperature = pTemp + (t - pTemp) * keep; }

            b.bloodOxygen = Down(pOxy, b.bloodOxygen, 97f, keep, ref worse);
            b.fibrillationProgress = Up(pFib, b.fibrillationProgress, 0.01f, keep, ref worse);
            if (!b.sleeping) b.consciousness = Down(pCons, b.consciousness, 95f, keep, ref worse);
            b.shock = Up(pShock, b.shock, 0.5f, keep, ref worse);
            b.painShock = Up(pPainShock, b.painShock, 0.5f, keep, ref worse);
            b.sicknessAmount = Up(pSick, b.sicknessAmount, 0.5f, keep, ref worse);
            b.traumaAmount = Up(pTrauma, b.traumaAmount, 0.5f, keep, ref worse);
            b.horrifiedLevel = Up(pHorror, b.horrifiedLevel, 0.5f, keep, ref worse);
            b.barotrauma = Up(pBaro, b.barotrauma, 0.5f, keep, ref worse);

            // the game calls it cardiac arrest when the heart rate is under 20
            if (b.heartRate < 20f)
            {
                worse = true;
                float chance = 0.04f * r * r * Regen.StrengthValue() * dt;   // ~2%/s at the cap, almost nothing early on
                if (chance > 0f && Random.value < chance)
                {
                    b.fibrillationProgress = 0f;
                    b.heartRate = 70f;
                    if (b.bloodPressure < 60f) b.bloodPressure = 60f;
                    Restarts++;
                    Plugin.Log.LogInfo("[VITALS] heart restarted on its own");
                }
            }

            if (worse) Adaptation.MarkOngoing("Vitals");
            Snap(b);
        }
    }
}
