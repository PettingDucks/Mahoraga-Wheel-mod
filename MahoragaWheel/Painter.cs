using System;
using UnityEngine;

namespace MahoragaWheel
{
    // Draws the wheel into a transparent texture with plain code (no camera), used in the health panel.
    // Depth cues so it's always clear which side is in front:
    //   - near parts are brighter and a little bigger (perspective), far parts darker and smaller
    //   - a per-pixel depth buffer, so near parts truly cover far parts
    //   - a dark pixel-art outline around the silhouette and wherever a near part crosses a far one
    internal static class WheelPainter
    {
        private static readonly Vector3 L = new Vector3(-0.4f, 0.5f, 0.77f).normalized;
        private const float Persp = 0.18f;        // how much bigger the near side is than the far side
        private const float EdgeDepth = 0.22f;    // depth jump that counts as "one part in front of another"
        private const byte FillAlpha = 225, LineAlpha = 240;
        private static readonly Color32 Outline = new Color32(58, 36, 8, 255);

        private class Ctx
        {
            public int n;
            public Color32[] col;
            public float[] dep;
            public float cx, cy, sc, ct, st;
            public bool pixel;
        }

        public static void Paint(Texture2D tex, float tiltDeg, float frac, int turns, int maxTurns, bool pixel)
        {
            Color32[] buf = PaintBuffer(tex.width, tiltDeg, frac, turns, maxTurns, pixel);
            tex.SetPixels32(buf);
            tex.Apply(false);
        }

        // Pure drawing with no Unity objects, so it can also be run outside the game to preview it.
        public static Color32[] PaintBuffer(int n, float tiltDeg, float frac, int turns, int maxTurns, bool pixel)
        {
            Ctx c = new Ctx();
            c.n = n; c.pixel = pixel;
            c.col = new Color32[n * n];
            c.dep = new float[n * n];
            for (int i = 0; i < c.dep.Length; i++) c.dep[i] = float.MaxValue;
            float th = tiltDeg * Mathf.Deg2Rad;
            c.ct = Mathf.Cos(th); c.st = Mathf.Sin(th);
            c.sc = n * 0.5f / 2.25f;          // room for the tips plus the bigger near side and the outline
            c.cx = n * 0.5f; c.cy = n * 0.5f;

            int lit = Mathf.Min(8, turns * 2);
            Color32 rimC = new Color32(232, 184, 52, 255);
            Color32 spokeC = new Color32(222, 172, 44, 255);
            Color32 tipC = new Color32(255, 218, 96, 255);
            Color32 hubC = new Color32(255, 212, 88, 255);

            // rim: a ring of small shaded balls, which reads as a round tube
            int segs = 96;
            for (int i = 0; i < segs; i++)
            {
                float a = i * 2f * Mathf.PI / segs;
                Ball(c, Mathf.Cos(a), Mathf.Sin(a), 0.085f, rimC);
            }
            // spokes: drawn in short pieces so each piece sits at its own depth against the rim
            float len = 1.62f;
            for (int k = 0; k < 8; k++)
            {
                float a = k * 45f * Mathf.Deg2Rad;
                float ux = Mathf.Cos(a), uy = Mathf.Sin(a);
                int pieces = 10;
                for (int s = 0; s < pieces; s++)
                {
                    float r0 = 0.18f + (len - 0.18f) * s / pieces, r1 = 0.18f + (len - 0.18f) * (s + 1) / pieces;
                    Rod(c, ux * r0, uy * r0, ux * r1, uy * r1, 0.05f, spokeC);
                }
                Ball(c, ux * len, uy * len, 0.2f, k < lit ? tipC : spokeC);
            }
            Ball(c, 0f, 0f, 0.21f, hubC);

            Outlines(c);
            return c.col;
        }

        // wheel-plane point (x along, y across) to screen; d = depth, + is far, - is near
        private static void Project(Ctx c, float x, float y, out float sx, out float sy, out float d, out float f)
        {
            d = -y * c.st;   // seen from below: the top edge is the near one, so the wheel faces up
            f = 1f / (1f + Persp * d);
            sx = c.cx + x * c.sc * f;
            sy = c.cy + y * c.ct * c.sc * f;
        }

        // far side darker, near side brighter
        private static float DepthShade(Ctx c, float d)
        {
            float near = Mathf.InverseLerp(c.st, -c.st, d);   // 0 far .. 1 near
            return Mathf.Lerp(0.55f, 1.08f, near);
        }

        private static void Ball(Ctx c, float x, float y, float rad, Color32 col)
        {
            float sx, sy, d, f;
            Project(c, x, y, out sx, out sy, out d, out f);
            float r = Mathf.Max(0.8f, rad * c.sc * f);
            float shade = DepthShade(c, d);
            int x0 = Mathf.FloorToInt(sx - r - 1), x1 = Mathf.CeilToInt(sx + r + 1);
            int y0 = Mathf.FloorToInt(sy - r - 1), y1 = Mathf.CeilToInt(sy + r + 1);
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    float dx = px + 0.5f - sx, dy = py + 0.5f - sy;
                    float dd = dx * dx + dy * dy;
                    if (dd > r * r) continue;
                    float nx = dx / r, ny = dy / r;
                    float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
                    float b = 0.5f + 0.6f * Mathf.Max(0f, nx * L.x + ny * L.y + nz * L.z);
                    if (c.pixel) b = b > 0.98f ? 1.12f : (b > 0.72f ? 0.92f : 0.7f);   // three flat shades
                    // a ball's own surface bulges toward the viewer a little
                    Put(c, px, py, d - nz * rad * c.st * 0.5f, col, b * shade);
                }
        }

        private static void Rod(Ctx c, float xa, float ya, float xb, float yb, float rad, Color32 col)
        {
            float ax, ay, da, fa, bx, by, db, fb;
            Project(c, xa, ya, out ax, out ay, out da, out fa);
            Project(c, xb, yb, out bx, out by, out db, out fb);
            float r = Mathf.Max(0.6f, rad * c.sc * (fa + fb) * 0.5f);
            int x0 = Mathf.FloorToInt(Mathf.Min(ax, bx) - r - 1), x1 = Mathf.CeilToInt(Mathf.Max(ax, bx) + r + 1);
            int y0 = Mathf.FloorToInt(Mathf.Min(ay, by) - r - 1), y1 = Mathf.CeilToInt(Mathf.Max(ay, by) + r + 1);
            float vx = bx - ax, vy = by - ay, vl = vx * vx + vy * vy;
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    float qx = px + 0.5f - ax, qy = py + 0.5f - ay;
                    float t = vl > 0f ? Mathf.Clamp01((qx * vx + qy * vy) / vl) : 0f;
                    float ex = qx - vx * t, ey = qy - vy * t;
                    float dist = Mathf.Sqrt(ex * ex + ey * ey);
                    if (dist > r) continue;
                    float d = Mathf.Lerp(da, db, t);
                    // light from above: the upper edge of the rod is brighter
                    float side = r > 0f ? ey / r : 0f;
                    float b = c.pixel ? (side > 0.35f ? 1.08f : (side < -0.35f ? 0.74f : 0.9f)) : 0.9f + 0.2f * side;
                    Put(c, px, py, d, col, b * DepthShade(c, d));
                }
        }

        private static void Put(Ctx c, int x, int y, float d, Color32 col, float bright)
        {
            if (x < 0 || y < 0 || x >= c.n || y >= c.n) return;
            int i = y * c.n + x;
            if (d >= c.dep[i]) return;   // something nearer is already here
            c.dep[i] = d;
            c.col[i] = new Color32((byte)Mathf.Min(255f, col.r * bright), (byte)Mathf.Min(255f, col.g * bright),
                                   (byte)Mathf.Min(255f, col.b * bright), FillAlpha);
        }

        // Pixel-art outline: one dark pixel around the silhouette, and a dark pixel on the far side
        // wherever a nearer part crosses in front, so front and back can always be told apart.
        private static void Outlines(Ctx c)
        {
            int n = c.n;
            bool[] edge = new bool[n * n];
            bool[] outer = new bool[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    bool filled = c.col[i].a > 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= n || ny >= n) continue;
                        int j = ny * n + nx;
                        bool nf = c.col[j].a > 0;
                        if (!filled && nf) outer[i] = true;
                        else if (filled && nf && c.dep[j] < c.dep[i] - EdgeDepth) edge[i] = true;   // neighbour is in front of me
                    }
                }
            for (int i = 0; i < n * n; i++)
            {
                if (outer[i]) c.col[i] = new Color32(Outline.r, Outline.g, Outline.b, LineAlpha);
                else if (edge[i]) c.col[i] = new Color32(Outline.r, Outline.g, Outline.b, LineAlpha);
            }
        }
    }
}
