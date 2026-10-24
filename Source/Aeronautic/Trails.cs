// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7),
// packages/wing-vapor/src/trails.ts, with three.js r186 quaternions.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;

namespace VortexVapor.Aeronautic
{
    // Both tip trails' centrelines in the canonical frame, Points apiece, point k being
    // k * SpacingM along the air from the tip's trailing edge; three floats a point.
    public class TrailPaths
    {
        public const int Points = 64;
        public float[] Positive = new float[Points * 3], Negative = new float[Points * 3];
        public double SpacingM;
    }

    public struct TrailLayout
    {
        public double TipX, TipY, SemispanM, CosAlpha, SinAlpha, FlowZ, Descent, RollupM, LengthM;
    }

    // A rotation, as three.js holds one (Hamilton, x y z w), with r186's slerp.
    public struct Quat
    {
        public double X, Y, Z, W;

        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public Quat Conjugate() { return new Quat(-X, -Y, -Z, W); }

        // Vector3.applyQuaternion.
        public void Rotate(double vx, double vy, double vz, out double x, out double y, out double z)
        {
            double tx = 2 * (Y * vz - Z * vy);
            double ty = 2 * (Z * vx - X * vz);
            double tz = 2 * (X * vy - Y * vx);
            x = vx + W * tx + Y * tz - Z * ty;
            y = vy + W * ty + Z * tx - X * tz;
            z = vz + W * tz + X * ty - Y * tx;
        }

        // Quaternion.slerpQuaternions(a, b, t).
        public static Quat Slerp(Quat a, Quat b, double t)
        {
            double x = b.X, y = b.Y, z = b.Z, w = b.W;
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0)
            {
                x = -x; y = -y; z = -z; w = -w;
                dot = -dot;
            }
            double s = 1 - t;
            if (dot < 0.9995)
            {
                double theta = Math.Acos(dot);
                double sin = Math.Sin(theta);
                s = Math.Sin(s * theta) / sin;
                t = Math.Sin(t * theta) / sin;
                return new Quat(a.X * s + x * t, a.Y * s + y * t, a.Z * s + z * t, a.W * s + w * t);
            }
            var q = new Quat(a.X * s + x * t, a.Y * s + y * t, a.Z * s + z * t, a.W * s + w * t);
            double l = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            if (l == 0) return Identity;
            l = 1 / l;
            return new Quat(q.X * l, q.Y * l, q.Z * l, q.W * l);
        }
    }

    // The craft's path through the air, a fixed ring of samples; trails are laid along it.
    public class TrailHistory
    {
        // The most frames of history kept; a frame's sub-steps may overrun it before the trim.
        public const int MaxSamples = 1024;
        const int Capacity = MaxSamples + 128;
        // A gap longer than this restarts the history; a slow frame is split into steps this long.
        const double MaxGapS = 2, MaxStepS = 1.0 / 30;

        readonly double[] px = new double[Capacity], py = new double[Capacity], pz = new double[Capacity], flownAt = new double[Capacity];
        readonly Quat[] attitudes = new Quat[Capacity];
        int start, count;
        double posX, posY, posZ, flown;

        public int Count { get { return count; } }

        int At(int i) { return (start + i) % Capacity; }

        // Forget everything: the next frame starts a new trail.
        public void Reset()
        {
            start = count = 0;
            flown = posX = posY = posZ = 0;
        }

        // One frame: duration, attitude, airspeed, free-stream direction, trail length to keep.
        public void Advance(double deltaS, Quat attitude, double airspeed, double cosAlpha, double sinAlpha, double keepM, double flowZ = 0)
        {
            if (deltaS > MaxGapS) Reset();
            int previous = count > 0 ? At(count - 1) : -1;
            double stepS = Math.Max(deltaS, 0);
            int steps = previous >= 0 ? Math.Min(Math.Max((int)Math.Ceiling(stepS / MaxStepS), 1), 120) : 1;
            for (int step = 1; step <= steps; step++)
            {
                // Turned evenly from the last frame's attitude to this one's.
                Quat turned = previous >= 0 ? Quat.Slerp(attitudes[previous], attitude, (double)step / steps) : attitude;
                // The aircraft flies against the free stream: forward and down from its nose by
                // the angle of attack.
                double vx, vy, vz;
                turned.Rotate(-cosAlpha, -sinAlpha, -flowZ, out vx, out vy, out vz);
                posX += vx * airspeed * (stepS / steps);
                posY += vy * airspeed * (stepS / steps);
                posZ += vz * airspeed * (stepS / steps);
                flown += airspeed * stepS / steps;
                // Nothing has moved: the last sample stands for this frame too.
                if (count > 0 && flown - flownAt[At(count - 1)] < 1e-6)
                {
                    attitudes[At(count - 1)] = turned;
                    continue;
                }
                if (count == Capacity) { start = (start + 1) % Capacity; count--; }
                int at = At(count++);
                px[at] = posX; py[at] = posY; pz[at] = posZ;
                attitudes[at] = turned;
                flownAt[at] = flown;
            }
            // Older than the trail is long, but one, so the end always has a sample behind it.
            while (count > 2 && (flown - flownAt[At(1)] > keepM || count > MaxSamples))
            {
                start = (start + 1) % Capacity;
                count--;
            }
        }

        // Both trails laid back along the history, straight past its end.
        public TrailPaths Paths(TrailLayout layout, TrailPaths into = null)
        {
            if (count < 2) return Trails.Straight(layout, into);
            TrailPaths paths = into ?? new TrailPaths();
            double spacing = layout.LengthM / (TrailPaths.Points - 1);
            paths.SpacingM = spacing;
            Lay(layout, spacing, 1, paths.Positive);
            Lay(layout, spacing, -1, paths.Negative);
            return paths;
        }

        void Lay(TrailLayout layout, double spacing, int side, float[] points)
        {
            int now = At(count - 1);
            Quat intoNow = attitudes[now].Conjugate();
            int older = count - 1;
            for (int index = 0; index < TrailPaths.Points; index++)
            {
                double along = index * spacing;
                double air = flownAt[now] - along;
                // The two frames either side of when this air was passed through.
                while (older > 0 && flownAt[At(older)] > air) older--;
                int before = At(older), after = At(Math.Min(older + 1, count - 1));
                double gap = flownAt[after] - flownAt[before];
                double t = gap > 1e-6 ? Math.Min(Math.Max((air - flownAt[before]) / gap, 0), 1) : 0;
                Quat attitude = Quat.Slerp(attitudes[before], attitudes[after], t);
                double wx = px[before] + (px[after] - px[before]) * t;
                double wy = py[before] + (py[after] - py[before]) * t;
                double wz = pz[before] + (pz[after] - pz[before]) * t;
                // Past the oldest frame: straight on, back along its flight path.
                double beyond = Math.Max(flownAt[before] - air, 0);
                if (beyond > 0)
                {
                    double sx, sy, sz;
                    attitudes[before].Rotate(layout.CosAlpha, layout.SinAlpha, layout.FlowZ, out sx, out sy, out sz);
                    wx += sx * beyond; wy += sy * beyond; wz += sz * beyond;
                }
                // The vortex where the tip left it, in the air, then into the frame the aircraft has now.
                double ox, oy, oz;
                Trails.ShedOffset(layout, along, side, out ox, out oy, out oz);
                double rx, ry, rz;
                attitude.Rotate(ox, oy, oz, out rx, out ry, out rz);
                double fx, fy, fz;
                intoNow.Rotate(rx + wx - px[now], ry + wy - py[now], rz + wz - pz[now], out fx, out fy, out fz);
                points[index * 3] = (float)fx;
                points[index * 3 + 1] = (float)fy;
                points[index * 3 + 2] = (float)fz;
            }
        }
    }

    public static class Trails
    {
        // Straight back along the free stream, sinking and rolling inboard to pi/4 of the span;
        // into `into` if given.
        public static TrailPaths Straight(TrailLayout layout, TrailPaths into = null)
        {
            double spacing = layout.LengthM / (TrailPaths.Points - 1);
            TrailPaths paths = into ?? new TrailPaths();
            paths.SpacingM = spacing;
            Lay(layout, spacing, 1, paths.Positive);
            Lay(layout, spacing, -1, paths.Negative);
            return paths;
        }

        static void Lay(TrailLayout layout, double spacing, int side, float[] points)
        {
            for (int i = 0; i < TrailPaths.Points; i++)
            {
                double along = i * spacing;
                double x, y, z;
                ShedOffset(layout, along, side, out x, out y, out z);
                // The air the vortex was shed into has gone aft, along the stream.
                x += along * layout.CosAlpha;
                y += along * layout.SinAlpha;
                z += along * layout.FlowZ;
                points[i * 3] = (float)x;
                points[i * 3 + 1] = (float)y;
                points[i * 3 + 2] = (float)z;
            }
        }

        // Where a vortex is, metres of air after its tip shed it, in the frame the aircraft had
        // then: sunk under its downwash and rolled inboard towards pi/4 of the span.
        public static void ShedOffset(TrailLayout layout, double along, int side, out double x, out double y, out double z)
        {
            double rolled = 1 - Math.Exp(-along / Math.Max(layout.RollupM, 1e-3));
            x = layout.TipX;
            y = layout.TipY - along * layout.Descent;
            z = side * layout.SemispanM * (1 - (1 - Aerodynamics.VortexSpacing) * rolled);
        }

        public static void Point(float[] points, double spacing, double along, out double x, out double y, out double z)
        {
            double position = Math.Min(Math.Max(along / Math.Max(spacing, 1e-6), 0), TrailPaths.Points - 1);
            int i = Math.Min((int)Math.Floor(position), TrailPaths.Points - 2);
            double t = position - i;
            x = points[i * 3] + ((double)points[i * 3 + 3] - points[i * 3]) * t;
            y = points[i * 3 + 1] + ((double)points[i * 3 + 4] - points[i * 3 + 1]) * t;
            z = points[i * 3 + 2] + ((double)points[i * 3 + 5] - points[i * 3 + 2]) * t;
        }

        // How far along a trail its nearest point to (x, y, z) is, metres; negative ahead of its start.
        public static double NearestAlong(float[] points, double spacing, double cosAlpha, double sinAlpha, double flowZ, double x, double y, double z)
        {
            double length = spacing * (TrailPaths.Points - 1);
            double along = (x - points[0]) * cosAlpha + (y - points[1]) * sinAlpha + (z - points[2]) * flowZ;
            if (along < 0) return along;
            for (int step = 0; step < 2; step++)
            {
                double here = Math.Min(along, length);
                double ax, ay, az, bx, by, bz;
                Point(points, spacing, here, out ax, out ay, out az);
                Point(points, spacing, Math.Min(here + spacing, length), out bx, out by, out bz);
                double tx = bx - ax, ty = by - ay, tz = bz - az;
                double l = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (l < 1e-6) break;
                tx /= l; ty /= l; tz /= l;
                along = Math.Max(here + (x - ax) * tx + (y - ay) * ty + (z - az) * tz, 0);
            }
            return along;
        }
    }
}
