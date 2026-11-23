using System;
using System.Collections.Generic;
using UnityEngine;

namespace MahoragaWheel
{
    // The wheel as ONE smooth mesh (torus ring, eight handles, ball tips, hub), lit on the CPU each frame
    // with diffuse + a soft metallic highlight, and drawn back to front. The game's 2D renderer only offers
    // unlit, depth-less shaders, so the lighting and the depth ordering are done here.
    internal class WheelMesh
    {
        public Mesh mesh;
        private List<Vector3> lv = new List<Vector3>();
        private List<Vector3> ln = new List<Vector3>();
        private List<Color> lc = new List<Color>();
        private List<int> lt = new List<int>();

        private Vector3[] V, N, triN, triC;
        private Color[] B;
        private Color32[] outCols;
        private int[] T, ids, outIdx;
        private float[] keys;
        private int triCount;

        private static readonly Vector3 Light = new Vector3(-0.35f, 0.5f, -0.8f).normalized;
        private static readonly Vector3 Half = (new Vector3(-0.35f, 0.5f, -0.8f).normalized + new Vector3(0f, 0f, -1f)).normalized;

        private static readonly Color RingGold = new Color(0.93f, 0.72f, 0.20f);
        private static readonly Color SpokeGold = new Color(0.90f, 0.68f, 0.18f);
        private static readonly Color TipGold = new Color(1.00f, 0.86f, 0.36f);
        private static readonly Color HubGold = new Color(1.00f, 0.80f, 0.28f);

        public WheelMesh()
        {
            AddTorus(1.0f, 0.085f, 96, 14, RingGold);
            float len = 1.62f;
            for (int k = 0; k < 8; k++)
            {
                float a = k * 45f * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                AddCylinder(dir * 0.1f, dir * len, 0.045f, 12, SpokeGold);
                AddSphere(dir * (len + 0.10f), 0.20f, 22, 14, TipGold);
            }
            AddSphere(Vector3.zero, 0.21f, 22, 14, HubGold);

            V = lv.ToArray(); N = ln.ToArray(); B = lc.ToArray(); T = lt.ToArray();
            triCount = T.Length / 3;
            triN = new Vector3[triCount]; triC = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int a = T[t * 3], b = T[t * 3 + 1], c = T[t * 3 + 2];
                triC[t] = (V[a] + V[b] + V[c]) / 3f;
                triN[t] = (N[a] + N[b] + N[c]).normalized;
            }
            outCols = new Color32[V.Length];
            ids = new int[triCount]; keys = new float[triCount]; outIdx = new int[triCount * 3];

            mesh = new Mesh();
            mesh.name = "MahoragaWheelMesh";
            mesh.MarkDynamic();
            mesh.vertices = V;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(8f, 8f, 8f));
            lv = null; ln = null; lc = null; lt = null;
            Refresh(Quaternion.identity);
        }

        private void Add(Vector3 p, Vector3 n, Color c) { lv.Add(p); ln.Add(n); lc.Add(c); }

        private void AddTorus(float R, float r, int segs, int sides, Color col)
        {
            int start = lv.Count;
            for (int i = 0; i <= segs; i++)
            {
                float a = 2f * Mathf.PI * i / segs; float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                for (int j = 0; j <= sides; j++)
                {
                    float b = 2f * Mathf.PI * j / sides; float cb = Mathf.Cos(b), sb = Mathf.Sin(b);
                    Vector3 n = new Vector3(cb * ca, cb * sa, sb);
                    Add(new Vector3(R * ca, R * sa, 0f) + n * r, n, col);
                }
            }
            for (int i = 0; i < segs; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = start + i * (sides + 1) + j, b = a + sides + 1;
                    lt.Add(a); lt.Add(b); lt.Add(a + 1);
                    lt.Add(a + 1); lt.Add(b); lt.Add(b + 1);
                }
        }

        private void AddCylinder(Vector3 p0, Vector3 p1, float rad, int sides, Color col)
        {
            Vector3 axis = (p1 - p0).normalized;
            Vector3 u = Vector3.Cross(axis, Vector3.forward);
            if (u.sqrMagnitude < 0.0001f) u = Vector3.right;
            u.Normalize();
            Vector3 v = Vector3.Cross(axis, u).normalized;
            int start = lv.Count;
            for (int s = 0; s <= sides; s++)
            {
                float a = 2f * Mathf.PI * s / sides;
                Vector3 n = u * Mathf.Cos(a) + v * Mathf.Sin(a);
                Add(p0 + n * rad, n, col);
                Add(p1 + n * rad, n, col);
            }
            for (int s = 0; s < sides; s++)
            {
                int a = start + s * 2;
                lt.Add(a); lt.Add(a + 1); lt.Add(a + 2);
                lt.Add(a + 2); lt.Add(a + 1); lt.Add(a + 3);
            }
        }

        private void AddSphere(Vector3 c, float rad, int lon, int lat, Color col)
        {
            int start = lv.Count;
            for (int i = 0; i <= lat; i++)
            {
                float th = Mathf.PI * i / lat;
                for (int j = 0; j <= lon; j++)
                {
                    float ph = 2f * Mathf.PI * j / lon;
                    Vector3 n = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    Add(c + n * rad, n, col);
                }
            }
            for (int i = 0; i < lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    int a = start + i * (lon + 1) + j, b = a + lon + 1;
                    lt.Add(a); lt.Add(b); lt.Add(a + 1);
                    lt.Add(a + 1); lt.Add(b); lt.Add(b + 1);
                }
        }

        // rot = the wheel's current world rotation. The camera looks along +Z, so a surface faces it when its normal points to -Z.
        public void Refresh(Quaternion rot)
        {
            Matrix4x4 m = Matrix4x4.Rotate(rot);
            for (int i = 0; i < V.Length; i++)
            {
                Vector3 nw = m.MultiplyVector(N[i]);
                float d = Mathf.Max(0f, Vector3.Dot(nw, Light));
                float s = Mathf.Max(0f, Vector3.Dot(nw, Half));
                float s2 = s * s, s4 = s2 * s2, s8 = s4 * s4, s16 = s8 * s8, s32 = s16 * s16;
                float spec = s32 * 0.30f;                 // soft highlight, not a glow
                float sh = 0.36f + 0.64f * d;
                Color c = B[i];
                outCols[i] = new Color32(
                    (byte)(Mathf.Min(1f, c.r * sh + spec) * 255f),
                    (byte)(Mathf.Min(1f, c.g * sh + spec * 0.92f) * 255f),
                    (byte)(Mathf.Min(1f, c.b * sh + spec * 0.7f) * 255f), 255);
            }
            int cnt = 0;
            for (int t = 0; t < triCount; t++)
            {
                Vector3 tn = m.MultiplyVector(triN[t]);
                if (tn.z > 0.08f) continue;               // facing away
                keys[cnt] = -m.MultiplyPoint3x4(triC[t]).z;   // far triangles first
                ids[cnt] = t;
                cnt++;
            }
            Array.Sort(keys, ids, 0, cnt);
            for (int k = 0; k < cnt; k++)
            {
                int t3 = ids[k] * 3;
                outIdx[k * 3] = T[t3]; outIdx[k * 3 + 1] = T[t3 + 1]; outIdx[k * 3 + 2] = T[t3 + 2];
            }
            mesh.colors32 = outCols;
            mesh.SetTriangles(outIdx, 0, cnt * 3, 0, false);
        }
    }
}
