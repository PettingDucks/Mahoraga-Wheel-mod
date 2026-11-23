using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace MahoragaWheel
{
    internal class WPart
    {
        public Transform tr;
        public Material mat;
        public int kind;    // 0 rim, 1 spoke, 2 knob, 3 hub
        public int index;   // rim segment or handle number
    }

    // The wheel. One instance floats over the player's head in the world (a 3D mesh drawn by the game's camera);
    // a second one (panelMode) is the flat pixel copy shown above the figure in the health panel.
    internal class WheelUI : MonoBehaviour
    {
        private ConfigEntry<bool> Enabled;
        private ConfigEntry<int> Size;
        private ConfigEntry<float> OffsetX, OffsetY;
        private ConfigEntry<KeyCode> ToggleKey;
        private ConfigEntry<bool> AboveHead;
        private ConfigEntry<int> HeadSize;
        private ConfigEntry<float> HeadLift;
        private ConfigEntry<bool> WorldSpace;
        private ConfigEntry<float> WorldScale;
        private ConfigEntry<float> WorldLift;
        private ConfigEntry<float> Tilt;
        private ConfigEntry<bool> PanelEnabled;
        private ConfigEntry<int> PanelSize;
        private ConfigEntry<float> PanelTilt;
        private ConfigEntry<float> PanelRaise;
        private ConfigEntry<bool> ShowCornerList;
        private ConfigEntry<float> TurnSeconds;
        private ConfigEntry<int> PanelRes;
        public bool panelMode;   // true = the static copy shown inside the health panel
        private float prevVx, accelX, sway, swayVel, lagX, lagVel, lagY, lagVelY, prevVy;
        private bool prevSet;

        private const int Layer = 31;
        private const int RimSegs = 96;
        private const float R = 1.0f;

        private bool built, failed, visible = true, inputBroken, world;
        private int topLayerId;
        private GameObject root3d, canvasGO;
        private Transform tilt, spinT;
        private Camera cam;
        private RenderTexture rt;
        private RawImage raw;
        private Text label;
        private List<WPart> parts = new List<WPart>();
        private float spin, notchTarget, notchCur, flash, t, labelTimer;
        private string focus;
        private float spinVel, facing = 1f, tweak, turnQuiet;
        private Vector3 jitterPos;
        private bool lastRight, lastRightSet;
        private float bodyAngle, listLogTimer;
        private WheelMesh wmesh;
        private float turnAnimT = 1f, turnAnimDur = 2.4f, turnAnimTotal, turnApplied;
        private string lastListLogged = "";
        private static readonly Vector3 Light = new Vector3(-0.35f, 0.5f, -0.8f).normalized;

        public void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind<bool>("Wheel UI", "Enabled", true, "Show the on-screen wheel.");
            Size = cfg.Bind<int>("Wheel UI", "SizePixels", 230, "Wheel size in pixels.");
            OffsetX = cfg.Bind<float>("Wheel UI", "OffsetFromRight", 20f, "Distance from the right screen edge.");
            OffsetY = cfg.Bind<float>("Wheel UI", "OffsetFromTop", 300f, "Distance from the top screen edge.");
            ToggleKey = cfg.Bind<KeyCode>("Wheel UI", "ToggleKey", KeyCode.F8, "Key that shows or hides the wheel.");
            AboveHead = cfg.Bind<bool>("Wheel UI", "FloatAboveHead", true, "Float the wheel above your character's head. False pins it to the screen corner.");
            HeadSize = cfg.Bind<int>("Wheel UI", "HeadWheelSizePixels", 130, "Wheel size in pixels when floating above the head.");
            HeadLift = cfg.Bind<float>("Wheel UI", "HeadLiftPixels", 40f, "Extra pixels between the head and the wheel (screen mode only).");
            WorldSpace = cfg.Bind<bool>("Wheel UI", "WorldSpace", true, "Make the wheel a real object in the game world, floating above the head. False uses a screen overlay.");
            WorldScale = cfg.Bind<float>("Wheel UI", "WorldScale", 1.05f, "Size of the in-world wheel (1 = about 3 world units wide).");
            WorldLift = cfg.Bind<float>("Wheel UI", "HoverHeight", 1.45f, "How far above the head center the in-world wheel floats, in world units. Nothing connects it to the head.");
            PanelEnabled = cfg.Bind<bool>("Health Panel Wheel", "Show", true, "Show a static copy of the wheel above the character in the health panel.");
            PanelSize = cfg.Bind<int>("Health Panel Wheel", "SizePixels", 230, "Size of the wheel in the health panel.");
            PanelRes = cfg.Bind<int>("Health Panel Wheel", "PixelResolution", 100, "Pixels across the health panel wheel image. 45 is chunky, 90 is finer, 180 is smooth.");
            TurnSeconds = cfg.Bind<float>("Wheel UI", "SecondsPerRevolution", 2.4f, "How long one full spin takes when a turn completes. Higher is slower. Two spins at full adaptation.");
            ShowCornerList = cfg.Bind<bool>("Wheel UI", "ShowCornerList", false, "Show the text list of adaptations in the screen corner. Off by default: the symbols live in the health menu.");
            PanelRaise = cfg.Bind<float>("Health Panel Wheel", "RaisePixels", 75f, "How far above the head icon the wheel sits. Raise it to clear the mouth slot box.");
            PanelTilt = cfg.Bind<float>("Health Panel Wheel", "TiltDegrees", 60f, "Halo tilt of the health panel wheel.");
            Tilt = cfg.Bind<float>("Wheel UI", "TiltDegrees", 68f, "Halo tilt of the in-world wheel. 0 faces the camera, about 70 looks like a halo above the head.");
        }

        // Each revolution is one "kick": the piston sound and the spin start on the same frame,
        // so they can never drift apart. Kicks wait in this queue (true = heavy, the last one at full adaptation).
        private readonly List<bool> kicks = new List<bool>();
        private float lastUpdateTime = -999f;
        private const float ChainAt = 0.45f;   // the next queued kick may start once the current spin is this far along

        // True when this wheel is on screen and running, so it will play the sound itself in time with the spin.
        public bool Animating { get { return !panelMode && built && visible && Time.unscaledTime - lastUpdateTime < 0.5f; } }

        public void OnTurn(string cat, int turns, bool full)
        {
            kicks.Add(false);
            if (full) kicks.Add(true);           // two revolutions at full adaptation, the second one heavy
            turnQuiet = Mathf.Max(turnQuiet, 1.5f);   // stop the jitter while it spins
            flash = full ? 1.4f : 1f;
            Adaptation.LastCat = cat; Adaptation.LastCatTime = Time.time;
        }

        public void OnRenewal()
        {
            kicks.Add(false); kicks.Add(true);   // two revolutions when the body renews
            turnQuiet = Mathf.Max(turnQuiet, 2f);
        }

        public void OnReset()
        {
            kicks.Clear();
            notchTarget = 0f; notchCur = 0f; flash = 0.6f; focus = null; turnAnimT = 1f;
        }

        // ---------- build ----------
        private WPart Prim(PrimitiveType type, Transform parent, int kind, int index)
        {
            GameObject g = GameObject.CreatePrimitive(type);
            g.layer = world ? 0 : Layer;
            Component col = g.GetComponent("Collider");
            if (col != null) UnityEngine.Object.Destroy(col);
            g.transform.SetParent(parent, false);
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("UI/Default");
            MeshRenderer mr = g.GetComponent<MeshRenderer>();
            Material m = new Material(sh);
            mr.sharedMaterial = m;
            if (world) { mr.sortingLayerID = topLayerId; mr.sortingOrder = 400; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mr.receiveShadows = false; }
            WPart p = new WPart();
            p.tr = g.transform; p.mat = m; p.kind = kind; p.index = index;
            parts.Add(p);
            return p;
        }

        private void Build()
        {
            if (panelMode) { built = true; Plugin.Log.LogInfo("[WHEEL UI] health panel wheel ready (painted image)."); return; }
            world = !panelMode && WorldSpace.Value;
            int lyr = world ? 0 : Layer;
            if (world)
            {
                SortingLayer[] sls = SortingLayer.layers;
                topLayerId = sls.Length > 0 ? sls[sls.Length - 1].id : 0;
            }
            root3d = new GameObject("MW_Wheel3D");
            root3d.layer = lyr;
            root3d.transform.position = new Vector3(5000f, 5000f, 0f);
            root3d.transform.localScale = world ? new Vector3(WorldScale.Value, WorldScale.Value, WorldScale.Value) : Vector3.one;
            UnityEngine.Object.DontDestroyOnLoad(root3d);

            GameObject tg = new GameObject("tilt"); tg.layer = lyr; tg.transform.SetParent(root3d.transform, false); tilt = tg.transform;
            GameObject sg = new GameObject("spin"); sg.layer = lyr; sg.transform.SetParent(tilt, false); spinT = sg.transform;

            // one smooth gold mesh (ring, handles, ball tips, hub), lit and depth-sorted on the CPU
            wmesh = new WheelMesh();
            GameObject mg = new GameObject("wheelmesh");
            mg.layer = lyr;
            mg.transform.SetParent(spinT, false);
            MeshFilter mf = mg.AddComponent<MeshFilter>();
            mf.sharedMesh = wmesh.mesh;
            MeshRenderer mrr = mg.AddComponent<MeshRenderer>();
            Shader sh0 = Shader.Find("Sprites/Default");
            if (sh0 == null) sh0 = Shader.Find("UI/Default");
            Material mat0 = new Material(sh0);
            mat0.color = Color.white;
            mrr.sharedMaterial = mat0;
            if (world)
            {
                mrr.sortingLayerID = topLayerId; mrr.sortingOrder = 400;
                mrr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; mrr.receiveShadows = false;
            }

            // camera + render texture (screen-overlay mode only; world mode is drawn by the game's own camera)
            if (!world) {
            rt = new RenderTexture(256, 256, 16, RenderTextureFormat.ARGB32);
            rt.Create();
            GameObject cg = new GameObject("MW_WheelCam");
            cg.layer = Layer;
            cg.transform.SetParent(root3d.transform, false);
            cg.transform.localPosition = new Vector3(0f, 0f, -8f);
            cam = cg.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 1.95f;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 30f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << Layer;
            cam.targetTexture = rt;
            cam.depth = -50f;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            }

            if (panelMode)
            {
                built = true;
                Plugin.Log.LogInfo("[WHEEL UI] health panel wheel built.");
                return;
            }
            // HUD
            canvasGO = new GameObject("MW_WheelCanvas");
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);
            Canvas cv = canvasGO.AddComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 5000;

            if (!world)
            {
                GameObject rg = new GameObject("wheelimg");
                rg.transform.SetParent(canvasGO.transform, false);
                raw = rg.AddComponent<RawImage>();
                raw.texture = rt;
                raw.raycastTarget = false;
                RectTransform rr = raw.rectTransform;
                rr.anchorMin = rr.anchorMax = rr.pivot = new Vector2(1f, 1f);
                rr.anchoredPosition = new Vector2(-OffsetX.Value, -OffsetY.Value);
                rr.sizeDelta = new Vector2(Size.Value, Size.Value);
            }

            GameObject tgo = new GameObject("wheeltext");
            tgo.transform.SetParent(canvasGO.transform, false);
            label = tgo.AddComponent<Text>();
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (Exception) { }
            if (f == null) { try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (Exception) { } }
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 14);
            label.font = f;
            label.fontSize = 14;
            label.color = Color.white;
            label.alignment = TextAnchor.UpperRight;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            label.supportRichText = false;
            Shadow sh = tgo.AddComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.9f);
            sh.effectDistance = new Vector2(1f, -1f);
            RectTransform tr = label.rectTransform;
            tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(1f, 1f);
            tr.anchoredPosition = new Vector2(-OffsetX.Value, world ? -OffsetY.Value : -OffsetY.Value - Size.Value + 6f);
            tr.sizeDelta = new Vector2(Size.Value + 120f, 260f);
            label.gameObject.SetActive(ShowCornerList.Value);

            built = true;
            Plugin.Log.LogInfo("[WHEEL UI] built (" + (world ? "in-world 3D wheel" : "screen overlay wheel") + ").");
        }

        // ---------- per frame ----------
        private void Update()
        {
            if (Enabled == null || !Enabled.Value) return;
            Body pb = Tracker.PlayerBody();
            if (pb == null) return;
            if (panelMode) { UpdatePanel(pb); return; }

            if (!built && !failed)
            {
                try { Build(); }
                catch (Exception e) { failed = true; Plugin.Log.LogError("[WHEEL UI] build failed: " + e); return; }
            }
            if (!built) return;

            if (!inputBroken)
            {
                try { if (Input.GetKeyDown(ToggleKey.Value)) { visible = !visible; if (canvasGO != null) canvasGO.SetActive(visible); if (root3d != null && world) root3d.SetActive(visible); } }
                catch (Exception) { inputBroken = true; Plugin.Log.LogWarning("[WHEEL UI] toggle key unavailable (input system)."); }
            }
            if (!visible) { if (cam != null) cam.enabled = false; return; }
            if (cam != null) cam.enabled = true;

            float dt = Time.unscaledDeltaTime;
            t += dt;
            lastUpdateTime = Time.unscaledTime;

            // which type does the wheel show
            string f = null;
            if (Adaptation.LastCat != null && Time.time - Adaptation.LastCatTime < 8f) f = Adaptation.LastCat;
            else
            {
                int best = 0;
                foreach (KeyValuePair<string, CatState> kv in Adaptation.All)
                    if (kv.Value.turns > best) { best = kv.Value.turns; f = kv.Key; }
            }
            focus = f;
            int turns = 0; float frac = 0f;
            if (f != null)
            {
                CatState cs = Adaptation.Get(f);
                turns = cs.turns;
                frac = turns >= Adaptation.MaxTurns.Value ? 1f : Mathf.Clamp01(cs.progress / Mathf.Max(1f, Adaptation.SecondsPerTurn.Value));
            }
            // "adapting" = the shown type is hurting you right now and not yet maxed
            bool adapting = false;
            if (f != null)
            {
                CatState acs = Adaptation.Get(f);
                adapting = Time.time < acs.activeUntil && acs.turns < Adaptation.MaxTurns.Value;
            }
            if (turnQuiet > 0f) turnQuiet -= dt;

            // movement only swings the wheel (lean and lag below); it does not spin it
            float vx = 0f, vy = 0f;
            Rigidbody2D prb = pb.rb;
            if (prb != null) { vx = prb.velocity.x; vy = prb.velocity.y; }
            float dtc = Mathf.Clamp(dt, 0.001f, 0.05f);
            if (!prevSet) { prevVx = vx; prevVy = vy; prevSet = true; }
            float axNow = (vx - prevVx) / dtc;
            float ayNow = (vy - prevVy) / dtc;
            prevVx = vx; prevVy = vy;
            accelX = Mathf.Lerp(accelX, axNow, 0.35f);
            // only adaptation spins the wheel, never walking (the shudder below feeds spinVel and fades quickly)
            spinVel *= Mathf.Exp(-6.0f * dtc);
            spinVel = Mathf.Clamp(spinVel, -950f, 950f);
            spin += spinVel * dt;

            // the whole wheel swings like a hanging weight: leans against acceleration and lags behind the head
            swayVel += (-62f * sway - 4.5f * swayVel - accelX * 0.9f) * dtc;
            sway = Mathf.Clamp(sway + swayVel * dtc, -38f, 38f);
            lagVel += ((-vx * 0.06f - lagX) * 55f - 7.5f * lagVel) * dtc;
            lagX += lagVel * dtc;
            lagVelY += ((-vy * 0.035f - lagY) * 55f - 7.5f * lagVelY) * dtc;
            lagY += lagVelY * dtc;

            // One revolution per kick. The piston hits hardest in its first ~0.4s and is gone by ~1s,
            // so the spin is an ease-out: it lurches on the hit and coasts to a soft stop after the sound.
            if (kicks.Count > 0 && (turnAnimT >= 1f || turnAnimT >= ChainAt))
            {
                bool heavy = kicks[0]; kicks.RemoveAt(0);
                float pitch = Sfx.KickPitch(heavy);
                Sfx.PlayKick(pitch, heavy);
                float left = turnAnimT < 1f ? turnAnimTotal - turnApplied : 0f;   // finish the rest of a chained spin too
                turnAnimTotal = 360f + left; turnAnimT = 0f; turnApplied = 0f;
                turnAnimDur = Mathf.Max(0.6f, TurnSeconds.Value / pitch);   // lower pitch plays longer, so it spins longer
                turnQuiet = Mathf.Max(turnQuiet, turnAnimDur);
            }
            if (turnAnimT < 1f)
            {
                turnAnimT = Mathf.Min(1f, turnAnimT + dt / turnAnimDur);
                float u = 1f - turnAnimT;
                float ease = 1f - u * u * u;   // cubic ease-out: half the turn in the first ~0.5s, with the sound
                float targetAngle = ease * turnAnimTotal;
                notchCur += targetAngle - turnApplied; turnApplied = targetAngle;
            }

            // tweaks out while adapting, stronger the closer the next turn is
            float tweakGoal = (adapting && turnQuiet <= 0f) ? 1f : 0f;
            tweak = Mathf.Lerp(tweak, tweakGoal, Mathf.Clamp01(dt * 8f));
            float tw = tweak * (0.7f + 0.3f * frac);
            float jr = (Mathf.PerlinNoise(t * 13f, 1.7f) - 0.5f) * 2f;
            float jt = (Mathf.PerlinNoise(t * 11f, 9.3f) - 0.5f) * 2f;
            jitterPos = new Vector3((Mathf.PerlinNoise(t * 15f, 3.1f) - 0.5f) * 0.16f * tw + lagX,
                                    (Mathf.PerlinNoise(t * 12f, 6.4f) - 0.5f) * 0.12f * tw + lagY, 0f);
            spinVel += jr * 30f * tw * dtc;   // shudder: the wheel stutters against its own spin
            spinT.localRotation = Quaternion.Euler(0f, 0f, -(spin + notchCur + jr * 9f * tw));

            // the wheel opens toward the way the character faces
            bool right = pb.isRight;
            if (!lastRightSet || right != lastRight)
            {
                lastRight = right; lastRightSet = true;
                Plugin.Log.LogInfo("[WHEEL UI] character facing " + (right ? "right" : "left"));
            }
            facing = Mathf.Lerp(facing, right ? 1f : -1f, Mathf.Clamp01(dt * 8f));
            float baseTilt = world ? Tilt.Value : 26f;
            float yaw = world ? -facing * 38f : 0f;
            tilt.localRotation = Quaternion.Euler(baseTilt + 2f * Mathf.Sin(t * 0.9f) + jt * 4f * tw,
                                                  yaw + (world ? 2f : 30f) * Mathf.Sin(t * 0.55f) + jr * 3f * tw, sway);
            if (flash > 0f) flash = Mathf.Max(0f, flash - dt * 1.4f);

            PlaceWheel(pb);
            if (wmesh != null) wmesh.Refresh(spinT.rotation);   // re-light and re-sort for the new orientation
            Recolor(turns, frac, pb);

            labelTimer += dt;
            if (labelTimer >= 0.3f && label.gameObject.activeSelf)
            {
                labelTimer = 0f;
                string txt = BuildLabel();
                label.text = txt;
                listLogTimer += 0.3f;
                if (listLogTimer >= 10f && txt != lastListLogged)
                {
                    listLogTimer = 0f; lastListLogged = txt;
                    Plugin.Log.LogInfo("[LIST] " + txt.Replace("\n", " | "));   // exactly what the on-screen list was told to show
                }
            }
        }

        // ---- static wheel inside the health panel: a transparent painted image above the figure's head ----
        private Texture2D panelTex;
        private int panelKey = -1;

        private readonly Vector3[] panelCorners = new Vector3[4];
        private float lastFitScale = -1f;

        // On a different screen size the wheel could reach the top edge. Shrink it around its centre
        // (which also lifts its bottom further from the slot box) so it never leaves the screen or overlaps.
        private void KeepOnScreen(RectTransform rr)
        {
            float cur = rr.localScale.x <= 0f ? 1f : rr.localScale.x;
            rr.GetWorldCorners(panelCorners);
            Canvas cv = raw.canvas;
            Camera cam = (cv != null && cv.renderMode != RenderMode.ScreenSpaceOverlay) ? cv.worldCamera : null;
            Vector2 bot = RectTransformUtility.WorldToScreenPoint(cam, panelCorners[0]);
            Vector2 top = RectTransformUtility.WorldToScreenPoint(cam, panelCorners[1]);
            float h = (top.y - bot.y) / cur;   // image height at scale 1
            if (h <= 1f) return;
            float centre = (top.y + bot.y) * 0.5f;
            float contentHalf = h * 0.5f * 0.62f;   // the drawn wheel fills about the middle 62% of the image's height at this tilt
            float room = Screen.height - 6f - centre;
            float s = Mathf.Clamp(room / contentHalf, 0.4f, 1f);
            if (Mathf.Abs(s - cur) > 0.01f)
            {
                rr.localScale = new Vector3(s, s, 1f);
                if (Mathf.Abs(s - lastFitScale) > 0.05f)
                {
                    lastFitScale = s;
                    Plugin.Log.LogInfo("[WHEEL UI] panel wheel scaled to " + s.ToString("F2") + " to stay on screen");
                }
            }
        }

        private void UpdatePanel(Body pb)
        {
            if (PanelEnabled == null || !PanelEnabled.Value) { if (raw != null) raw.gameObject.SetActive(false); return; }
            PlayerCamera pc = PlayerCamera.main;
            GameObject wv = pc != null ? pc.woundView : null;
            if (wv == null || !wv.activeInHierarchy) { if (raw != null) raw.gameObject.SetActive(false); return; }
            WoundView view = wv.GetComponentInChildren<WoundView>(true);
            if (view == null || view.limbImages == null) return;

            // the head of the body diagram is the highest visible limb image
            Transform headT = null; float topY = float.MinValue;
            for (int i = 0; i < view.limbImages.Length; i++)
            {
                Image im = view.limbImages[i];
                if (im == null || !im.gameObject.activeInHierarchy) continue;
                float y = im.transform.position.y;
                if (y > topY) { topY = y; headT = im.transform; }
            }
            if (headT == null) return;

            if (!built && !failed) Build();
            if (panelTex == null)
            {
                int res = Mathf.Clamp(PanelRes.Value, 24, 256);
                panelTex = new Texture2D(res, res, TextureFormat.RGBA32, false);   // hard-edged pixels to match the game's art
                panelTex.filterMode = FilterMode.Point;
                panelTex.wrapMode = TextureWrapMode.Clamp;
            }
            if (raw == null)
            {
                GameObject rg = new GameObject("MW_PanelWheel");
                raw = rg.AddComponent<RawImage>();
                raw.texture = panelTex;
                raw.raycastTarget = false;
            }
            if (raw.transform.parent != headT)
            {
                raw.transform.SetParent(headT, false);
                RectTransform rr = raw.rectTransform;
                rr.anchorMin = rr.anchorMax = rr.pivot = new Vector2(0.5f, 0.5f);
                rr.sizeDelta = new Vector2(PanelSize.Value, PanelSize.Value);
                float headH = 40f;
                RectTransform hr = headT as RectTransform;
                if (hr != null && hr.rect.height > 1f) headH = hr.rect.height;
                rr.anchoredPosition = new Vector2(0f, headH * 0.5f + PanelRaise.Value + PanelSize.Value * 0.30f);
                rr.localScale = Vector3.one;
                raw.transform.SetAsLastSibling();
                Plugin.Log.LogInfo("[WHEEL UI] panel wheel anchored to '" + headT.name + "'");
            }
            raw.gameObject.SetActive(true);
            KeepOnScreen(raw.rectTransform);

            string f = null;
            if (Adaptation.LastCat != null && Time.time - Adaptation.LastCatTime < 8f) f = Adaptation.LastCat;
            else
            {
                int best = 0;
                foreach (KeyValuePair<string, CatState> kv in Adaptation.All)
                    if (kv.Value.turns > best) { best = kv.Value.turns; f = kv.Key; }
            }
            int turns = 0; float frac = 0f;
            if (f != null)
            {
                CatState cs = Adaptation.Get(f);
                turns = cs.turns;
                frac = turns >= Adaptation.MaxTurns.Value ? 1f : Mathf.Clamp01(cs.progress / Mathf.Max(1f, Adaptation.SecondsPerTurn.Value));
            }
            int key = turns * 1000 + (int)(frac * 72f);
            if (key != panelKey)
            {
                panelKey = key;
                WheelPainter.Paint(panelTex, PanelTilt.Value, frac, turns, Adaptation.MaxTurns.Value, true);
            }
        }

        // Float the wheel above the player's head by converting the head's world position to screen pixels.
        private void PlaceWheel(Body pb)
        {
            if (world)
            {
                Transform wh = null;
                if (pb.limbs != null)
                {
                    for (int i = 0; i < pb.limbs.Length; i++)
                    {
                        Limb l = pb.limbs[i];
                        if (l != null && l.isHead) { wh = l.transform; break; }
                    }
                }
                if (wh == null) wh = pb.transform;
                // orientation comes from the torso (base limb), not the head, so looking around does not move the wheel
                Transform refT = (pb.baseLimb != null) ? pb.baseLimb.transform : pb.transform;
                float ang = refT.eulerAngles.z;
                bodyAngle = Mathf.LerpAngle(bodyAngle, ang, 1f - Mathf.Exp(-14f * Mathf.Clamp(Time.unscaledDeltaTime, 0.001f, 0.05f)));
                float rad = bodyAngle * Mathf.Deg2Rad;
                Vector3 up = new Vector3(-Mathf.Sin(rad), Mathf.Cos(rad), 0f);
                float lift = WorldLift.Value * Mathf.Max(0.3f, WorldScale.Value / 0.7f) + 0.06f * Mathf.Sin(t * 1.6f);
                root3d.transform.position = wh.position + up * lift + jitterPos;
                root3d.transform.rotation = Quaternion.Euler(0f, 0f, bodyAngle);
                return;
            }
            RectTransform rr = raw.rectTransform;
            if (!AboveHead.Value)
            {
                rr.anchorMin = rr.anchorMax = rr.pivot = new Vector2(1f, 1f);
                rr.anchoredPosition = new Vector2(-OffsetX.Value, -OffsetY.Value);
                rr.sizeDelta = new Vector2(Size.Value, Size.Value);
                return;
            }
            Camera mc = Camera.main;
            if (mc == null || pb.limbs == null) return;
            Transform head = null;
            for (int i = 0; i < pb.limbs.Length; i++)
            {
                Limb l = pb.limbs[i];
                if (l != null && l.isHead) { head = l.transform; break; }
            }
            if (head == null) head = pb.transform;
            Vector3 sp = mc.WorldToScreenPoint(head.position);
            if (sp.z < 0f) return;
            float size = HeadSize.Value;
            rr.anchorMin = rr.anchorMax = new Vector2(0f, 0f);
            rr.pivot = new Vector2(0.5f, 0f);
            rr.sizeDelta = new Vector2(size, size);
            rr.anchoredPosition = new Vector2(sp.x, sp.y + HeadLift.Value + 8f * Mathf.Sin(t * 1.6f));
        }

        private void Recolor(int turns, float frac, Body pb)
        {
            int litHandles = Mathf.Min(8, turns * 2);
            bool half = frac > 0.5f && litHandles < 8;
            int litRim = (int)(frac * RimSegs);
            float pulse = 0.75f + 0.25f * Mathf.Sin(t * 5f);
            bool full = turns >= Adaptation.MaxTurns.Value;
            for (int i = 0; i < parts.Count; i++)
            {
                WPart p = parts[i];
                Color baseC;
                float shade;
                if (p.kind == 0)
                {
                    baseC = p.index < litRim ? new Color(0.35f, 0.95f, 1f) : new Color(0.80f, 0.83f, 0.88f);
                    shade = Cube(p.tr);
                }
                else if (p.kind == 1)
                {
                    bool lit = p.index < litHandles || (half && p.index == litHandles);
                    baseC = lit ? new Color(1f, 0.78f, 0.22f) * (p.index < litHandles ? 1f : pulse) : new Color(0.74f, 0.77f, 0.83f);
                    shade = Cube(p.tr);
                }
                else if (p.kind == 2)
                {
                    bool lit = p.index < litHandles || (half && p.index == litHandles);
                    baseC = lit ? new Color(1f, 0.9f, 0.45f) * (p.index < litHandles ? 1f : pulse) : new Color(0.92f, 0.94f, 0.98f);
                    shade = Sphere(p.tr);
                }
                else if (p.kind == 4)
                {
                    baseC = new Color(0.72f, 0.75f, 0.82f);
                    shade = Cube(p.tr);
                }
                else if (p.kind == 5)
                {
                    baseC = new Color(0.9f, 0.92f, 0.97f);
                    shade = Sphere(p.tr);
                }
                else
                {
                    baseC = full ? new Color(1f, 0.55f, 0.15f) * pulse : new Color(0.9f, 0.93f, 0.98f);
                    shade = Sphere(p.tr);
                }
                baseC = GoldFor(p.kind);   // always plain gold; progress shows through the spin and the sound
                Color c = baseC * shade;
                c.a = 1f;
                p.mat.color = c;
            }
        }

        private static Color GoldFor(int kind)
        {
            if (kind == 0) return new Color(0.93f, 0.72f, 0.20f);
            if (kind == 1) return new Color(0.90f, 0.68f, 0.18f);
            if (kind == 2) return new Color(1.00f, 0.86f, 0.36f);
            if (kind == 3) return new Color(1.00f, 0.80f, 0.28f);
            return new Color(0.90f, 0.70f, 0.20f);
        }

        // fake lighting so the 3D rotation reads as depth (the shader is unlit)
        private static float Cube(Transform tr)
        {
            float s = Mathf.Max(Mathf.Abs(Vector3.Dot(tr.up, Light)),
                      Mathf.Max(Mathf.Abs(Vector3.Dot(tr.forward, Light)), Mathf.Abs(Vector3.Dot(tr.right, Light))));
            return 0.42f + 0.58f * Mathf.Clamp01((s - 0.57f) / 0.43f);
        }

        private float Sphere(Transform tr)
        {
            Vector3 n = (tr.position - spinT.position);
            if (n.sqrMagnitude < 0.0001f) n = Vector3.back; else n = n.normalized;
            return 0.55f + 0.45f * Mathf.Max(0f, Vector3.Dot(n, Light));
        }

        private string BuildLabel()
        {
            StringBuilder sb = new StringBuilder();
            int max = Adaptation.MaxTurns.Value;
            bool any = false;
            for (int i = 0; i < Adaptation.Names.Length; i++)
            {
                string n = Adaptation.Names[i];
                CatState s = Adaptation.Get(n);
                if (s.turns == 0 && s.progress < 0.5f) continue;
                any = true;
                sb.Append(n == focus ? "> " : "  ");
                sb.Append(n.PadRight(10));
                sb.Append('[');
                for (int k = 0; k < max; k++) sb.Append(k < s.turns ? '#' : '-');
                sb.Append("] ");
                sb.Append((Adaptation.Own(n) * 100f).ToString("F0")).Append("%");
                if (s.turns < max && Time.time < s.activeUntil) sb.Append(" ~");
                sb.Append('\n');
            }
            if (!any) sb.Append("Wheel idle");
            return sb.ToString();
        }
    }
}
