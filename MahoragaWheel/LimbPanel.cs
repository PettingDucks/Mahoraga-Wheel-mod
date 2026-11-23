using System;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MahoragaWheel
{
    // Health menu only: (1) a strip of adaptation symbols with pips, (2) before/after numbers for the limb you are looking at.
    internal class LimbAdaptPanel : MonoBehaviour
    {
        private class Row
        {
            public GameObject go;
            public RawImage icon;
            public TextMeshProUGUI label;
            public Image[] pips;
        }

        private const int MaxPips = 8;
        private ConfigEntry<bool> Show;
        private GameObject canvasGO;
        private RectTransform box, strip;
        private TextMeshProUGUI txt;
        private Row[] rows;
        private Row renewRow;
        private bool failed;
        private WoundView cachedView;
        private float refresh;
        private string lastText = "";
        private readonly Vector3[] corners = new Vector3[4];
        private static readonly Color GoldC = new Color(0.93f, 0.74f, 0.22f, 1f);
        private static readonly Color DimC = new Color(0.93f, 0.74f, 0.22f, 0.22f);

        public void Init(ConfigFile cfg)
        {
            Show = cfg.Bind<bool>("Health Panel Wheel", "ShowLimbAdaptation", true, "Show adaptation symbols and before/after numbers in the health menu.");
        }

        private void Update()
        {
            if (Show == null || !Show.Value || failed) return;
            try { Tick(); }
            catch (Exception e) { failed = true; Plugin.Log.LogError("[LIMB PANEL] disabled after error: " + e); }
        }

        private void Hide() { if (canvasGO != null && canvasGO.activeSelf) canvasGO.SetActive(false); }

        private void Tick()
        {
            Body pb = Tracker.PlayerBody();
            PlayerCamera pc = PlayerCamera.main;
            GameObject wv = pc != null ? pc.woundView : null;
            if (pb == null || wv == null || !wv.activeInHierarchy) { Hide(); return; }
            if (cachedView == null) cachedView = wv.GetComponentInChildren<WoundView>(true);
            WoundView view = cachedView;
            if (view == null || view.limbNameText == null) { Hide(); return; }
            if (canvasGO == null) Build(view);
            canvasGO.SetActive(true);

            // anchor: under the lowest stat line of the limb panel, aligned with the limb title
            Canvas gc = view.GetComponentInParent<Canvas>();
            Camera cam = (gc != null && gc.renderMode != RenderMode.ScreenSpaceOverlay) ? gc.worldCamera : null;
            view.limbNameText.rectTransform.GetWorldCorners(corners);
            float x = float.MaxValue;
            for (int i = 0; i < 4; i++) x = Mathf.Min(x, RectTransformUtility.WorldToScreenPoint(cam, corners[i]).x);
            float y = float.MaxValue;
            TMP_Text[] lines = new TMP_Text[] { view.limbHealTimeText, view.limbForceText, view.limbBleedText, view.limbPainText, view.infecText, view.skinText, view.muscleText };
            for (int k = 0; k < lines.Length; k++)
            {
                if (lines[k] == null || !lines[k].gameObject.activeInHierarchy) continue;
                lines[k].rectTransform.GetWorldCorners(corners);
                for (int i = 0; i < 4; i++) y = Mathf.Min(y, RectTransformUtility.WorldToScreenPoint(cam, corners[i]).y);
            }
            if (y == float.MaxValue) y = 120f;
            float cursor = y - 10f;

            // before / after readout for the limb being looked at
            int idx = view.limbLookingAt;
            bool limbOk = pb.limbs != null && idx >= 0 && idx < pb.limbs.Length && pb.limbs[idx] != null;
            refresh += Time.unscaledDeltaTime;
            if (refresh >= 0.25f)
            {
                refresh = 0f;
                string s = limbOk ? BuildText(pb.limbs[idx]) : "";
                if (s != lastText) { lastText = s; txt.text = s; }
            }
            bool showBox = limbOk && lastText.Length > 0;
            box.gameObject.SetActive(showBox);
            if (showBox)
            {
                box.anchoredPosition = new Vector2(x, cursor);
                Vector2 pref = txt.GetPreferredValues();
                box.sizeDelta = new Vector2(pref.x + 12f, pref.y + 8f);
                cursor -= box.sizeDelta.y + 8f;
            }

            // symbol strip: renewal first, then what you have earned or are earning
            int n = 0;
            visible.Clear();
            {
                Row r = renewRow;
                r.go.SetActive(true);
                visible.Add(r);
                r.label.text = Renewal.State;
                float pr = Mathf.Clamp01(Renewal.Progress) * 4f;
                int full = Mathf.FloorToInt(pr);
                bool locked = Renewal.State == "Locked";
                for (int p = 0; p < MaxPips; p++)
                {
                    r.pips[p].gameObject.SetActive(p < 4);
                    if (locked) r.pips[p].color = DimC;
                    else if (p < full) r.pips[p].color = GoldC;
                    else if (p == full) r.pips[p].color = Color.Lerp(DimC, GoldC, (pr - full) * 0.6f);
                    else r.pips[p].color = DimC;
                }
                n = 1;
            }
            for (int i = 0; i < Adaptation.Names.Length; i++)
            {
                string name = Adaptation.Names[i];
                CatState cs = Adaptation.Get(name);
                Row r = rows[i];
                bool on = cs.turns > 0 || cs.progress >= 0.5f;
                r.go.SetActive(on);
                if (!on) continue;
                visible.Add(r);
                int max = Mathf.Min(MaxPips, Adaptation.MaxTurns.Value);
                float frac = cs.turns >= max ? 0f : Mathf.Clamp01(cs.progress / Mathf.Max(1f, Adaptation.SecondsPerTurn.Value));
                for (int p = 0; p < MaxPips; p++)
                {
                    r.pips[p].gameObject.SetActive(p < max);
                    if (p < cs.turns) r.pips[p].color = GoldC;
                    else if (p == cs.turns) r.pips[p].color = Color.Lerp(DimC, GoldC, frac * 0.6f);   // the next pip warms up as progress builds, no glow
                    else r.pips[p].color = DimC;
                }
                n++;
            }
            strip.gameObject.SetActive(n > 0);
            if (n > 0) LayoutStrip(x, cursor, y, showBox);
        }

        private readonly System.Collections.Generic.List<Row> visible = new System.Collections.Generic.List<Row>();
        private const float RowH = 22f, ColW = 168f, BottomGap = 100f;   // keep clear of the game's hotbar

        // Keep the strip on screen: flow rows into extra columns when there is not enough room below,
        // and if even that is too cramped, put it beside the before/after box instead of under it.
        private void LayoutStrip(float x, float top, float panelBottom, bool boxShown)
        {
            int count = visible.Count;
            float sx = x, sy = top;
            int perCol = Mathf.FloorToInt((sy - BottomGap - 8f) / RowH);
            if (perCol < 4 && boxShown)
            {
                sx = x + box.sizeDelta.x + 8f;
                sy = panelBottom - 10f;
                perCol = Mathf.FloorToInt((sy - BottomGap - 8f) / RowH);
            }
            perCol = Mathf.Max(1, perCol);
            int cols = Mathf.CeilToInt(count / (float)perCol);
            for (int k = 0; k < count; k++)
            {
                int col = k / perCol, row = k % perCol;
                ((RectTransform)visible[k].go.transform).anchoredPosition = new Vector2(6f + col * ColW, -4f - row * RowH);
            }
            float w = cols * ColW + 6f;
            float h = 8f + Mathf.Min(count, perCol) * RowH;
            sx = Mathf.Min(sx, Screen.width - w - 4f);   // never past the right edge
            sx = Mathf.Max(4f, sx);
            sy = Mathf.Max(h + BottomGap, sy);           // never into the hotbar at the bottom of the screen
            strip.anchoredPosition = new Vector2(sx, sy);
            strip.sizeDelta = new Vector2(w, h);
        }

        private void Build(WoundView view)
        {
            canvasGO = new GameObject("MW_LimbAdaptCanvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);
            Canvas c = canvasGO.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 5001;
            float fs = Mathf.Max(10f, view.limbNameText.fontSize * 0.75f);

            // before / after box
            GameObject bg = new GameObject("readout");
            bg.transform.SetParent(canvasGO.transform, false);
            Image im = bg.AddComponent<Image>();
            im.color = new Color(0.02f, 0.06f, 0.04f, 0.82f);
            im.raycastTarget = false;
            box = im.rectTransform;
            box.anchorMin = box.anchorMax = new Vector2(0f, 0f);
            box.pivot = new Vector2(0f, 1f);
            GameObject tg = new GameObject("text");
            tg.transform.SetParent(bg.transform, false);
            txt = tg.AddComponent<TextMeshProUGUI>();
            txt.font = view.limbNameText.font;
            txt.fontSize = fs;
            txt.color = new Color(0.55f, 1f, 0.7f, 1f);
            txt.alignment = TextAlignmentOptions.TopLeft;
            txt.enableWordWrapping = false;
            txt.raycastTarget = false;
            RectTransform tr = txt.rectTransform;
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(6f, 4f); tr.offsetMax = new Vector2(-6f, -4f);

            // symbol strip
            GameObject sg = new GameObject("strip");
            sg.transform.SetParent(canvasGO.transform, false);
            Image sim = sg.AddComponent<Image>();
            sim.color = new Color(0.02f, 0.06f, 0.04f, 0.82f);
            sim.raycastTarget = false;
            strip = sim.rectTransform;
            strip.anchorMin = strip.anchorMax = new Vector2(0f, 0f);
            strip.pivot = new Vector2(0f, 1f);

            rowFont = view.limbNameText.font;
            rows = new Row[Adaptation.Names.Length];
            renewRow = MakeRow("Renewal", fs);
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = MakeRow(Adaptation.Names[i], fs);
            }
            Plugin.Log.LogInfo("[LIMB PANEL] built (symbols + before/after + renewal).");
        }

        private TMP_FontAsset rowFont;

        private Row MakeRow(string cat, float fs)
        {
            Row r = new Row();
            r.go = new GameObject("row_" + cat);
            r.go.transform.SetParent(strip, false);
            RectTransform rr = r.go.AddComponent<RectTransform>();
            rr.anchorMin = rr.anchorMax = new Vector2(0f, 1f);
            rr.pivot = new Vector2(0f, 1f);
            rr.sizeDelta = new Vector2(164f, 22f);

            GameObject ig = new GameObject("icon");
            ig.transform.SetParent(r.go.transform, false);
            r.icon = ig.AddComponent<RawImage>();
            r.icon.texture = Glyphs.Get(cat);
            r.icon.raycastTarget = false;
            RectTransform ir = r.icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0f, 0.5f); ir.pivot = new Vector2(0f, 0.5f);
            ir.anchoredPosition = new Vector2(0f, 0f); ir.sizeDelta = new Vector2(18f, 18f);

            GameObject lg = new GameObject("name");
            lg.transform.SetParent(r.go.transform, false);
            r.label = lg.AddComponent<TextMeshProUGUI>();
            r.label.font = rowFont;
            r.label.fontSize = Mathf.Max(9f, fs * 0.9f);
            r.label.color = GoldC;
            r.label.alignment = TextAlignmentOptions.MidlineLeft;
            r.label.enableWordWrapping = false;
            r.label.raycastTarget = false;
            r.label.text = cat;
            RectTransform lr = r.label.rectTransform;
            lr.anchorMin = lr.anchorMax = new Vector2(0f, 0.5f); lr.pivot = new Vector2(0f, 0.5f);
            lr.anchoredPosition = new Vector2(24f, 0f); lr.sizeDelta = new Vector2(78f, 20f);

            r.pips = new Image[MaxPips];
            for (int p = 0; p < MaxPips; p++)
            {
                GameObject pg = new GameObject("pip" + p);
                pg.transform.SetParent(r.go.transform, false);
                Image pi = pg.AddComponent<Image>();
                pi.raycastTarget = false;
                RectTransform pr = pi.rectTransform;
                pr.anchorMin = pr.anchorMax = new Vector2(0f, 0.5f); pr.pivot = new Vector2(0f, 0.5f);
                pr.anchoredPosition = new Vector2(108f + p * 10f, 0f); pr.sizeDelta = new Vector2(7f, 7f);
                r.pips[p] = pi;
            }
            r.go.SetActive(false);
            return r;
        }

        private static string F(float v, string fmt) { return v.ToString(fmt); }

        private string BuildText(Limb l)
        {
            float k = Regen.StrengthValue();
            float mr = Resist.MaxResistance();
            StringBuilder sb = new StringBuilder();
            int n = 0;
            bool hurt = l.skinHealth < 100f || l.muscleHealth < 100f || l.broken || l.dislocated;

            if (mr > 0f && hurt)
            { sb.Append("Healing   x1.00 -> x").Append(F(1f + mr, "F2")).Append("\n"); n++; }

            float hp = Regen.HealthPower();
            if (hp > 0f && (l.skinHealth < 100f || l.muscleHealth < 100f))
            { sb.Append("Regrowth  0 -> +").Append(F(0.6f * hp * k, "F2")).Append("/s\n"); n++; }

            float rb = Adaptation.Resistance("Bleeding");
            if (l.bleedAmount > 0f)
            {
                float before = l.bleedAmount * (l.blockedBleeding ? 0f : 1f);
                float after = l.totalBleedAmount;
                if (before > 0f && rb > 0f)
                {
                    sb.Append("Bleeding  ").Append(F(before, "F1")).Append(" -> ").Append(F(after, "F1"));
                    float close = 0.45f * rb * rb * k;
                    if (close > 0.005f) sb.Append("  closing ").Append(F(close, "F2")).Append("/s");
                    sb.Append("\n"); n++;
                }
            }

            float ri = Adaptation.Resistance("Infection");
            if (l.infectionAmount > 0f && l.infected && ri > 0f)
            {
                float after = l.GetInfectionSpeed();
                float before = ri < 0.999f ? (after + Regen.InfectionDecay.Value * ri * ri) / (1f - ri) : after;
                sb.Append("Infection ").Append(F(before, "F1")).Append(" -> ").Append(F(after, "F1"));
                if (after < 0f) sb.Append("  shrinking");
                sb.Append("\n"); n++;
            }

            if (l.broken || l.dislocated)
            {
                float mi = Mathf.Max(mr, Adaptation.Resistance("Injury"));
                sb.Append(l.broken ? "Fracture   x1.0 -> x" : "Dislocation x1.0 -> x").Append(F(1f + Regen.BoneBonusValue() * mi, "F1")).Append("\n");
                n++;
            }

            if (l.skinHealth <= 0f && l.muscleHealth <= 0f && !l.dismembered)
            { sb.Append("Dead limb  recovering\n"); n++; }

            if (n == 0) return "";
            return "<b>Adaptation</b>\n" + sb.ToString().TrimEnd('\n');
        }
    }
}
