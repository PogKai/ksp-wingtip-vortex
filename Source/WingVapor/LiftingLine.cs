using System;
using UnityEngine;

namespace VortexVapor
{
    // Prandtl's lifting line over the airframe's panels, which stock aerodynamics leaves out: in
    // stock every part lifts as if it were alone in the flow, so nothing feels its neighbours'
    // downwash, and loading steps at every joint and never falls off toward a tip.
    //
    // Each panel is one horseshoe. Thin-airfoil theory gives Gamma = pi c V alpha, and an induced
    // velocity v changes alpha by (v . l)/V, l being the panel's lift direction. So
    //
    //     Gamma_i = Gamma2D_i + pi c_i (v_i . l_i)
    //
    // where v_i is induced by every trailing line of the structure the panel belongs to. The
    // trailing lines start at the lifting line, so each induces half what an infinite line would.
    // It is taken averaged over the panel's span rather than at its midpoint, because a trailing
    // line can pass through the middle of a long panel (a flap edge, say). That average has a
    // closed form, finite everywhere except exactly on the line.
    //
    // The system is linear in Gamma, so it is factorised once when the geometry or flow direction
    // is refreshed and back-substituted every step. Afterwards each connected structure is scaled
    // back to its stock lift: stock's total is what actually holds the aircraft up, and the wake
    // has to carry exactly that. Only the spanwise distribution comes from here.
    //
    // Pure math on plain arrays, so it can be checked offline against textbook wings.
    public class LiftingLine
    {
        double[] lu = new double[0];
        int[] piv = new int[0];
        int size = -1;
        bool factored = false;
        int[] comp = new int[0], parent = new int[0], first = new int[0], groupComp = new int[0];
        double[] x = new double[0], kern = new double[0];

        public bool Factored { get { return factored; } }

        // Contributions: panel cPanel[k] sheds cSign[k] * Gamma into trailing group cGroup[k].
        public bool Factor(int np, Vector3[] center, Vector3[] span, float[] halfSpan, float[] chord,
            int ng, Vector3[] gPos, float[] gCore, int nc, int[] cPanel, int[] cGroup, float[] cSign,
            Vector3 down)
        {
            factored = false;
            if (np == 0) return false;
            if (size != np)
            {
                lu = new double[np * np];
                piv = new int[np];
                size = np;
            }
            Components(np, ng, nc, cPanel, cGroup);

            // The kernel depends only on (panel, group), so evaluate it once per pair and hand it to
            // every panel that sheds into that group.
            Array.Clear(lu, 0, np * np);
            if (kern.Length < ng) kern = new double[ng];
            if (groupComp.Length < ng) groupComp = new int[ng];
            for (int g = 0; g < ng; g++) groupComp[g] = -1;
            for (int k = 0; k < nc; k++) groupComp[cGroup[k]] = comp[cPanel[k]];
            for (int i = 0; i < np; i++)
            {
                if (chord[i] <= 0f) continue;
                for (int g = 0; g < ng; g++)
                    kern[g] = groupComp[g] == comp[i]
                        ? Math.PI * chord[i] * Kernel(center[i], span[i], halfSpan[i], gPos[g], gCore[g], down)
                        : 0.0;
                for (int k = 0; k < nc; k++)
                {
                    double kv = kern[cGroup[k]];
                    if (kv != 0.0) lu[i * np + cPanel[k]] -= cSign[k] * kv;
                }
            }
            for (int i = 0; i < np; i++) lu[i * np + i] += 1.0;

            // LU with partial pivoting.
            for (int c = 0; c < np; c++)
            {
                int best = c;
                double bestAbs = Math.Abs(lu[c * np + c]);
                for (int r = c + 1; r < np; r++)
                {
                    double a = Math.Abs(lu[r * np + c]);
                    if (a > bestAbs) { bestAbs = a; best = r; }
                }
                if (bestAbs < 1e-9) return false;
                piv[c] = best;
                if (best != c)
                    for (int k = 0; k < np; k++)
                    {
                        double t = lu[c * np + k]; lu[c * np + k] = lu[best * np + k]; lu[best * np + k] = t;
                    }
                double d = lu[c * np + c];
                for (int r = c + 1; r < np; r++)
                {
                    double f = lu[r * np + c] / d;
                    lu[r * np + c] = f;
                    if (f == 0.0) continue;
                    for (int k = c + 1; k < np; k++) lu[r * np + k] -= f * lu[c * np + k];
                }
            }
            factored = true;
            return true;
        }

        // Solves for Gamma from Gamma2D, then scales each connected structure back to its stock
        // lift. Falls back to stock's own loading if not factored or if the answer is implausible,
        // so the lift the wake carries is never lost.
        public void Solve(int np, float[] g2D, float[] gStock, float[] halfSpan, Vector3[] span, Vector3 down, float[] gamma)
        {
            if (!factored || np != size)
            {
                for (int i = 0; i < np; i++) gamma[i] = gStock[i];
                return;
            }
            if (x.Length < np) x = new double[np];
            for (int i = 0; i < np; i++) x[i] = g2D[i];
            for (int c = 0; c < np; c++)
            {
                int p = piv[c];
                if (p != c) { double t = x[c]; x[c] = x[p]; x[p] = t; }
                for (int r = c + 1; r < np; r++) x[r] -= lu[r * np + c] * x[c];
            }
            for (int r = np - 1; r >= 0; r--)
            {
                double s = x[r];
                for (int k = r + 1; k < np; k++) s -= lu[r * np + k] * x[k];
                x[r] = s / lu[r * np + r];
            }
            for (int i = 0; i < np; i++) gamma[i] = (float)x[i];

            // Scale each structure back to its stock lift, as a vector: sum of w * Gamma * l.
            if (stockSum.Length < np)
            {
                stockSum = new Vector3[np]; solvedSum = new Vector3[np]; sharedSum = new Vector3[np];
                absStock = new float[np]; absSolved = new float[np]; scale = new float[np]; mode = new int[np];
            }
            for (int c = 0; c < np; c++)
            {
                stockSum[c] = solvedSum[c] = sharedSum[c] = Vector3.zero;
                absStock[c] = absSolved[c] = 0f; mode[c] = -1;
            }
            for (int i = 0; i < np; i++)
            {
                Vector3 l = Vector3.Cross(down, span[i]);
                float w = 2f * halfSpan[i];
                stockSum[comp[i]] += l * (w * gStock[i]);
                solvedSum[comp[i]] += l * (w * gamma[i]);
                sharedSum[comp[i]] += l * (w * g2D[i]);
                absStock[comp[i]] += Mathf.Abs(w * gStock[i]);
                absSolved[comp[i]] += Mathf.Abs(w * gamma[i]);
            }
            // Per structure: the lifting-line answer if it is plausible, else shared area alone,
            // else stock's own loading. Plausible means it needs a sane scale to reach stock's lift
            // and, once scaled, holds no more total circulation than stock's (1.5x): induction
            // redistributes and reduces loading, it does not pump up opposing panels that cancel.
            Fallbacks = 0;
            for (int c = 0; c < np; c++)
            {
                if (absStock[c] <= 0f && solvedSum[c] == Vector3.zero) continue;
                float s = ScaleTo(stockSum[c], solvedSum[c]);
                if (s > 0.2f && s < 5f && s * absSolved[c] <= 1.5f * absStock[c] + 1e-3f) { mode[c] = 0; scale[c] = s; continue; }
                Fallbacks++;
                s = ScaleTo(stockSum[c], sharedSum[c]);
                if (s > 0.2f && s < 5f) { mode[c] = 1; scale[c] = s; }
                else { mode[c] = 2; scale[c] = 1f; }
            }
            for (int i = 0; i < np; i++)
            {
                int c = comp[i];
                gamma[i] = mode[c] == 0 ? gamma[i] * scale[c] : mode[c] == 1 ? g2D[i] * scale[c] : gStock[i];
            }
        }

        // How many structures fell back from the lifting line on the last solve.
        public int Fallbacks { get; private set; }

        static float ScaleTo(Vector3 target, Vector3 v)
        {
            return v.sqrMagnitude > 1e-9f ? Vector3.Dot(target, v) / v.sqrMagnitude : 0f;
        }

        Vector3[] stockSum = new Vector3[0], solvedSum = new Vector3[0], sharedSum = new Vector3[0];
        float[] absStock = new float[0], absSolved = new float[0], scale = new float[0];
        int[] mode = new int[0];

        // Normal-to-lift velocity per unit circulation of a semi-infinite trailing line at gPos
        // (core a), averaged over the panel's span. With sigma along the span from the panel's
        // centre and H the line's offset from the span line (softened by the core):
        //   (1 / 2hs) * integral (sigma - sg) / ((sigma - sg)^2 + H^2) dsigma, over [-hs, hs]
        //   = ln(((hs - sg)^2 + H^2) / ((hs + sg)^2 + H^2)) / (4 hs)
        // times 1/(4 pi). Positive means upwash along l = down x span.
        public static double Kernel(Vector3 center, Vector3 span, float halfSpan, Vector3 gPos, float core, Vector3 down)
        {
            Vector3 d = gPos - center;
            d -= down * Vector3.Dot(d, down);
            double sg = Vector3.Dot(d, span);
            double h2 = Math.Max(0.0, d.sqrMagnitude - sg * sg) + core * core + 1e-6;
            double hs = Math.Max(halfSpan, 1e-3f);
            double num = (hs - sg) * (hs - sg) + h2, den = (hs + sg) * (hs + sg) + h2;
            return Math.Log(num / den) / (4.0 * hs) / (4.0 * Math.PI);
        }

        // Panels connected through a shared trailing group form one structure.
        void Components(int np, int ng, int nc, int[] cPanel, int[] cGroup)
        {
            if (comp.Length < np) { comp = new int[np]; parent = new int[np]; }
            if (first.Length < ng) first = new int[ng];
            for (int i = 0; i < np; i++) parent[i] = i;
            for (int g = 0; g < ng; g++) first[g] = -1;
            for (int k = 0; k < nc; k++)
            {
                int g = cGroup[k], p = cPanel[k];
                if (first[g] < 0) { first[g] = p; continue; }
                int a = Find(parent, first[g]), b = Find(parent, p);
                if (a != b) parent[b] = a;
            }
            for (int i = 0; i < np; i++) comp[i] = Find(parent, i);
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
    }
}
