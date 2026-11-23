using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace MahoragaWheel
{
    // Regrows a lost arm or leg piece by piece, from the body outward (upper arm, forearm, hand),
    // pixel by pixel: a pale bone pushes out of the stump first and raw flesh follows right behind it,
    // settling into the limb's normal skin. Each piece only works (and can be hit) once it has fully grown.
    //
    // How it draws: the limb's own pixel art is copied once, the real sprite is hidden while it grows,
    // and a temporary copy is revealed one pixel row at a time along the limb from the joint to the tip.
    internal class Regrow : MonoBehaviour
    {
        public static Regrow Instance;
        private static ConfigEntry<bool> TestEnabled;
        private static ConfigEntry<KeyCode> TestKey;
        private static ConfigEntry<float> TestSeconds;
        private static ConfigEntry<KeyCode> CutKey;
        private static ConfigEntry<KeyCode> RegrowNowKey;

        private static readonly Color32 BoneC = new Color32(236, 228, 204, 255);
        private static readonly Color32 BoneEdge = new Color32(176, 164, 136, 255);
        private static readonly Color32 RawC = new Color32(168, 34, 40, 255);
        private const float FleshLag = 0.28f;    // flesh trails the bone by this share of the piece's length
        private const float SettleShare = 0.22f; // how far behind the flesh front it takes to look like normal skin

        public static void Init(ConfigFile cfg, GameObject host)
        {
            TestEnabled = cfg.Bind<bool>("Regrow Test", "Enabled", false,
                "Testing only: press the key to replay the regrow effect on one arm (forearm and hand). Nothing is cut off; it only hides and regrows the look.");
            TestKey = cfg.Bind<KeyCode>("Regrow Test", "Key", KeyCode.F10, "Key that replays the regrow effect.");
            TestSeconds = cfg.Bind<float>("Regrow Test", "Seconds", 12f, "How long the test regrow takes.");
            CutKey = cfg.Bind<KeyCode>("Regrow Test", "CutOffKey", KeyCode.F9,
                "Testing only: REALLY cuts off one forearm (and the hand below it) with the game's own dismember, so bleeding, gore and dropped items all apply.");
            RegrowNowKey = cfg.Bind<KeyCode>("Regrow Test", "RegrowNowKey", KeyCode.F11,
                "Testing only: starts a real regrow of a lost limb right away (takes 'Seconds'), instead of waiting for Renewal.");
            Instance = host.AddComponent<Regrow>();
        }

        // ---------- public API ----------
        public static bool Busy { get { return Instance != null && Instance.job != null; } }
        public static float JobProgress { get { return Instance != null && Instance.job != null ? Instance.job.Progress : 0f; } }

        // Regrow every lost piece of the extremity that 'limb' belongs to, over 'seconds' in total.
        public static bool Begin(Body b, Limb limb, float seconds)
        {
            if (Instance == null || Instance.job != null || b == null || limb == null) return false;
            Limb top = TopOf(limb);
            List<Limb> chain = Chain(top);
            List<Limb> lost = new List<Limb>();
            for (int i = 0; i < chain.Count; i++)
                if (chain[i].dismembered && !chain[i].prostheticInfo.hasProsthetic) lost.Add(chain[i]);
            if (lost.Count == 0) return false;
            Instance.job = new Job(b, lost, seconds, false);
            Plugin.Log.LogInfo("[REGROW] regrowing " + lost.Count + " piece(s) from " + lost[0].shortName + ", " + seconds.ToString("F0") + "s");
            return true;
        }

        // ---------- limb chain helpers (the game links pieces with connectedLimbs and distanceToHeart) ----------
        private static bool IsCore(Limb l) { return l.isVital || l.isHead || l.isAbdomen; }

        private static Limb TopOf(Limb l)
        {
            Limb cur = l;
            for (int guard = 0; guard < 8; guard++)
            {
                Limb up = null;
                if (cur.connectedLimbs != null)
                    for (int i = 0; i < cur.connectedLimbs.Length; i++)
                    {
                        Limb c = cur.connectedLimbs[i];
                        if (c != null && c.distanceToHeart < cur.distanceToHeart && !IsCore(c)) { up = c; break; }
                    }
                if (up == null) break;
                cur = up;
            }
            return cur;
        }

        // the piece and everything below it, nearest the body first
        private static List<Limb> Chain(Limb top)
        {
            List<Limb> list = new List<Limb>();
            Limb cur = top;
            for (int guard = 0; guard < 8 && cur != null; guard++)
            {
                list.Add(cur);
                Limb down = null;
                if (cur.connectedLimbs != null)
                    for (int i = 0; i < cur.connectedLimbs.Length; i++)
                    {
                        Limb c = cur.connectedLimbs[i];
                        if (c != null && c.distanceToHeart > cur.distanceToHeart && !list.Contains(c)) { down = c; break; }
                    }
                cur = down;
            }
            return list;
        }

        // ---------- the job ----------
        private Job job;

        private class Job
        {
            public Body body;
            public List<Limb> pieces;
            public float perPiece;
            public bool visualOnly;
            public int index = -1;
            public float t;
            public Piece cur;
            public float Progress { get { return pieces.Count == 0 ? 1f : Mathf.Clamp01((Mathf.Max(0, index) + (cur != null ? Mathf.Clamp01(t / perPiece) : 0f)) / pieces.Count); } }
            public Job(Body b, List<Limb> p, float seconds, bool visual)
            {
                body = b; pieces = p; visualOnly = visual;
                perPiece = Mathf.Max(1f, seconds) / Mathf.Max(1, p.Count);
            }
        }

        private class Piece
        {
            public Limb limb;
            public SpriteRenderer real;
            public Collider2D col;
            public GameObject go;
            public SpriteRenderer sr;
            public Texture2D tex;
            public Color32[] src, buf;
            public float[] proj;
            public bool[] bone;
            public float maxProj;
            public int lastStep = -1;
        }

        private void Update()
        {
            try { TestInput(); } catch (Exception) { }
            if (job == null) return;
            try { Step(Time.deltaTime); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[REGROW] stopped: " + e.Message);
                Abort();
            }
        }

        private void Step(float dt)
        {
            if (job.body == null || !job.body.alive) { Abort(); return; }
            if (job.cur == null)
            {
                job.index++;
                if (job.index >= job.pieces.Count) { Plugin.Log.LogInfo("[REGROW] done"); job = null; return; }
                job.cur = StartPiece(job.pieces[job.index], job.visualOnly);
                job.t = 0f;
                if (job.cur == null) return;   // could not draw it: it was attached plainly, move on
            }
            job.t += dt;
            float k = Mathf.Clamp01(job.t / job.perPiece);
            Draw(job.cur, k);
            if (k >= 1f) { EndPiece(job.cur, job.visualOnly); job.cur = null; }
        }

        private Piece StartPiece(Limb l, bool visualOnly)
        {
            if (!visualOnly)
            {
                // Attach just this piece (the game's own Reattach would bring the whole chain back at once).
                // It stays flagged as missing while it grows: the game checks that flag to decide whether a hand
                // can hold or use things and whether attacks can target the piece, so it is unusable until done.
                l.dismembered = true;
                // The stump seals as new tissue grows out of it. Cutting a limb off leaves the piece above it
                // bleeding hard; the game's own Reattach heals from the shoulder down, so do the same for the bleed.
                if (l.connectedLimbs != null)
                    for (int i = 0; i < l.connectedLimbs.Length; i++)
                    {
                        Limb up = l.connectedLimbs[i];
                        if (up != null && up.distanceToHeart < l.distanceToHeart && !up.dismembered && up.bleedAmount > 0f)
                        {
                            Plugin.Log.LogInfo("[REGROW] stump on " + up.shortName + " sealed (bleed " + up.bleedAmount.ToString("F1") + ")");
                            up.bleedAmount = 0f;
                        }
                    }
                l.transform.localPosition = Vector3.zero;
                l.gameObject.SetActive(true);
                l.Heal();
                // Not usable yet (muscle 1). Skin stays at 80 while growing because the game rolls for infection
                // every 40s on any limb below 80 skin, and the lower the skin the higher the chance.
                l.skinHealth = 80f; l.muscleHealth = 1f;
                Collider2D mine = l.GetComponent<Collider2D>();
                if (mine != null && job.body.limbs != null)
                    for (int i = 0; i < job.body.limbs.Length; i++)
                    {
                        Limb o = job.body.limbs[i];
                        if (o == null || o == l) continue;
                        Collider2D oc = o.GetComponent<Collider2D>();
                        if (oc != null) Physics2D.IgnoreCollision(mine, oc, true);
                    }
            }
            Piece p = new Piece();
            p.limb = l;
            p.real = l.GetComponent<SpriteRenderer>();
            p.col = l.GetComponent<Collider2D>();
            if (p.real == null || p.real.sprite == null) { FinishLimb(l, visualOnly); return null; }
            if (!Prepare(p)) { FinishLimb(l, visualOnly); return null; }
            p.real.enabled = false;
            if (p.col != null) p.col.enabled = false;   // cannot be hit until it has grown
            Plugin.Log.LogInfo("[REGROW] growing " + l.shortName);
            return p;
        }

        private static void FinishLimb(Limb l, bool visualOnly)
        {
            if (visualOnly) return;
            l.dismembered = false;   // fully grown: now it works and can be hit
            l.bleedAmount = 0f;      // new tissue is closed
            l.skinHealth = 25f; l.muscleHealth = Mathf.Max(l.muscleHealth, 25f);   // grows back fragile, heals up normally
        }

        private void EndPiece(Piece p, bool visualOnly)
        {
            if (p.real != null) p.real.enabled = true;
            if (p.col != null) p.col.enabled = true;
            Cleanup(p);
            FinishLimb(p.limb, visualOnly);
        }

        // The temporary sprite, texture and object made for one growing piece. Sprites made with Sprite.Create
        // are not freed with their object, so destroy it explicitly or every regrow leaks one.
        private void Cleanup(Piece p)
        {
            if (p.sr != null && p.sr.sprite != null) Destroy(p.sr.sprite);
            if (p.go != null) Destroy(p.go);
            if (p.tex != null) Destroy(p.tex);
        }

        private void Abort()
        {
            if (job != null && job.cur != null)
            {
                // a real regrow cut short (death): the half-grown piece is still missing, so hide it again
                if (!job.visualOnly && job.cur.limb != null && job.cur.limb.dismembered) job.cur.limb.gameObject.SetActive(false);
                if (job.cur.real != null) job.cur.real.enabled = true;
                if (job.cur.col != null) job.cur.col.enabled = true;
                Cleanup(job.cur);
            }
            // the test hides pieces that have not had their turn yet: show them again
            if (job != null && job.visualOnly)
                for (int i = 0; i < job.pieces.Count; i++)
                {
                    SpriteRenderer sr = job.pieces[i] != null ? job.pieces[i].GetComponent<SpriteRenderer>() : null;
                    if (sr != null) sr.enabled = true;
                }
            job = null;
        }

        // Copy the limb's pixels (the game's textures are not readable directly, so go through the GPU),
        // and work out, for every pixel, how far along the limb it is from the joint.
        private bool Prepare(Piece p)
        {
            Sprite s = p.real.sprite;
            Rect r = s.textureRect;
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            if (w < 2 || h < 2 || w > 512 || h > 512) return false;
            Texture st = s.texture;
            RenderTexture rt = RenderTexture.GetTemporary(st.width, st.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(st, rt);
            RenderTexture.active = rt;
            Texture2D copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
            copy.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            p.src = copy.GetPixels32();
            p.buf = new Color32[p.src.Length];
            p.tex = copy;
            p.tex.filterMode = FilterMode.Point;
            p.tex.wrapMode = TextureWrapMode.Clamp;

            // joint position in the sprite's pixels; without a joint, assume the top middle
            float ppu = s.pixelsPerUnit;
            Vector2 pivotPx = s.pivot - (s.textureRectOffset);
            Vector2 anchor;
            HingeJoint2D j = p.limb.joint;
            if (j != null) anchor = j.anchor * ppu + pivotPx;
            else anchor = new Vector2(w * 0.5f, h - 1);
            // the tip is the solid pixel farthest from the joint
            Vector2 far = anchor; float best = -1f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (p.src[y * w + x].a < 20) continue;
                    float d = (new Vector2(x + 0.5f, y + 0.5f) - anchor).sqrMagnitude;
                    if (d > best) { best = d; far = new Vector2(x + 0.5f, y + 0.5f); }
                }
            Vector2 dir = far - anchor;
            if (dir.sqrMagnitude < 1f) dir = Vector2.down;
            dir.Normalize();
            Vector2 side = new Vector2(-dir.y, dir.x);

            p.proj = new float[w * h];
            float[] lat = new float[w * h];
            int bins = 64;
            float[] half = new float[bins];
            p.maxProj = 1f;
            for (int i = 0; i < p.src.Length; i++)
            {
                if (p.src[i].a < 20) continue;
                Vector2 q = new Vector2(i % w + 0.5f, i / w + 0.5f) - anchor;
                p.proj[i] = Mathf.Max(0f, Vector2.Dot(q, dir));
                lat[i] = Vector2.Dot(q, side);
                if (p.proj[i] > p.maxProj) p.maxProj = p.proj[i];
            }
            for (int i = 0; i < p.src.Length; i++)
            {
                if (p.src[i].a < 20) continue;
                int b = Mathf.Min(bins - 1, (int)(p.proj[i] / p.maxProj * bins));
                if (Mathf.Abs(lat[i]) > half[b]) half[b] = Mathf.Abs(lat[i]);
            }
            // bone: a narrow core down the middle of each slice of the limb
            p.bone = new bool[w * h];
            for (int i = 0; i < p.src.Length; i++)
            {
                if (p.src[i].a < 20) continue;
                int b = Mathf.Min(bins - 1, (int)(p.proj[i] / p.maxProj * bins));
                p.bone[i] = Mathf.Abs(lat[i]) <= Mathf.Max(0.75f, half[b] * 0.3f);
            }

            p.go = new GameObject("MW_Regrow");
            p.go.transform.SetParent(p.limb.transform, false);
            p.sr = p.go.AddComponent<SpriteRenderer>();
            p.sr.sprite = Sprite.Create(p.tex, new Rect(0, 0, w, h), new Vector2(pivotPx.x / w, pivotPx.y / h), ppu);
            p.sr.sortingLayerID = p.real.sortingLayerID;
            p.sr.sortingOrder = p.real.sortingOrder;
            p.sr.flipX = p.real.flipX; p.sr.flipY = p.real.flipY;
            p.sr.color = p.real.color;
            Material m = null;
            try { if (WorldGeneration.world != null) m = WorldGeneration.world.defaultMat; } catch (Exception) { }
            if (m != null) p.sr.sharedMaterial = m;
            return true;
        }

        // k: 0..1 through this piece. Bone front leads, flesh front trails, both in whole pixels.
        private static void Draw(Piece p, float k)
        {
            float total = p.maxProj * (1f + FleshLag);
            int step = Mathf.FloorToInt(k * total);
            if (step == p.lastStep && k < 1f) return;
            p.lastStep = step;
            float boneFront = step;
            float fleshFront = step - p.maxProj * FleshLag;
            float settle = Mathf.Max(1f, p.maxProj * SettleShare);
            for (int i = 0; i < p.src.Length; i++)
            {
                Color32 s = p.src[i];
                if (s.a < 20 || k >= 1f) { p.buf[i] = s; continue; }
                float d = p.proj[i];
                if (d <= fleshFront)
                {
                    float m = Mathf.Clamp01((fleshFront - d) / settle);   // 0 = raw, 1 = normal skin
                    p.buf[i] = Color32.Lerp(RawC, s, m);
                    p.buf[i].a = s.a;
                }
                else if (d <= boneFront && p.bone[i])
                {
                    bool tip = boneFront - d < 1.5f;
                    p.buf[i] = tip ? BoneEdge : BoneC;
                }
                else p.buf[i] = new Color32(0, 0, 0, 0);
            }
            p.tex.SetPixels32(p.buf);
            p.tex.Apply(false);
            if (p.sr != null && p.real != null) { p.sr.flipX = p.real.flipX; p.sr.flipY = p.real.flipY; }
        }

        // ---------- test key: replay the look on one arm without cutting anything off ----------
        private void TestInput()
        {
            if (TestEnabled == null || !TestEnabled.Value || job != null) return;
            Body b = Tracker.PlayerBody();
            if (b == null || b.limbs == null || !b.alive) return;

            if (Input.GetKeyDown(CutKey.Value))
            {
                // the forearm of the first whole arm, cut with the game's own routine
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l == null || !l.isArm || l.dismembered || l.prostheticInfo.hasProsthetic) continue;
                    List<Limb> ch = Chain(TopOf(l));
                    Limb fore = ch.Count > 1 ? ch[1] : ch[0];
                    if (fore.dismembered) continue;
                    Plugin.Log.LogInfo("[REGROW] test: cutting off " + fore.shortName + " for real");
                    fore.Dismember();
                    return;
                }
                Plugin.Log.LogInfo("[REGROW] test: no whole arm to cut");
                return;
            }
            if (Input.GetKeyDown(RegrowNowKey.Value))
            {
                for (int i = 0; i < b.limbs.Length; i++)
                {
                    Limb l = b.limbs[i];
                    if (l != null && l.dismembered && !l.prostheticInfo.hasProsthetic)
                    {
                        if (!Begin(b, l, TestSeconds.Value)) Plugin.Log.LogInfo("[REGROW] test: could not start");
                        return;
                    }
                }
                Plugin.Log.LogInfo("[REGROW] test: nothing is missing");
                return;
            }

            if (!Input.GetKeyDown(TestKey.Value)) return;
            Limb arm = null;
            for (int i = 0; i < b.limbs.Length; i++)
            {
                Limb l = b.limbs[i];
                if (l != null && l.isArm && !l.dismembered && !l.prostheticInfo.hasProsthetic) { arm = l; break; }
            }
            if (arm == null) { Plugin.Log.LogInfo("[REGROW] test: no whole arm to show it on"); return; }
            List<Limb> chain = Chain(TopOf(arm));
            List<Limb> show = new List<Limb>();
            for (int i = 1; i < chain.Count; i++) if (!chain[i].dismembered) show.Add(chain[i]);   // forearm and hand
            if (show.Count == 0) show.Add(chain[0]);
            // hide the later pieces until their turn, like a real regrow
            for (int i = 0; i < show.Count; i++) { SpriteRenderer sr = show[i].GetComponent<SpriteRenderer>(); if (sr != null) sr.enabled = false; }
            job = new Job(b, show, TestSeconds.Value, true);
            Plugin.Log.LogInfo("[REGROW] test: replaying on " + show.Count + " piece(s) starting at " + show[0].shortName);
        }
    }
}
