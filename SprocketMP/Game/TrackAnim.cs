using System;
using System.Collections.Generic;
using Sprocket.ContinuousTracks;
using UnityEngine;

namespace SprocketMP.Game
{
    /// <summary>
    /// Track animation of network puppets. A puppet is a kinematic copy moved by the network, so the game's own
    /// track physics can't tell how fast it drives (it either spins the tracks in place or not at all).
    /// Here wheel angles and belt movement of puppets are computed from the puppet's network velocity.
    /// The scale/sign between "track speed" and the game's angle units is learned from locally driven tanks.
    /// </summary>
    public static class TrackAnim
    {
        sealed class TrackState
        {
            public NetVehicle V;
            public float SideX, SprocketR = 0.3f, Speed, Pre0, LastBelt;
            public float[] Angles;
            public bool Puppet;
        }

        static readonly Dictionary<IntPtr, TrackState> s_tracks = new Dictionary<IntPtr, TrackState>();
        static readonly Dictionary<IntPtr, TrackState> s_belts = new Dictionary<IntPtr, TrackState>();

        // learned conversion factors (defaults: wheel angles in degrees, belt delta in radians)
        static float s_kWheel = -1f, s_kBelt = 1f;   // measured in game: wheel angles are radians, turning "backwards"
        static int s_nWheel, s_nBelt;
        static float s_logTimer;
        static bool s_failed;

        public static void Reset() { s_tracks.Clear(); s_belts.Clear(); }

        public static void Fail(Exception e)
        {
            if (s_failed) return;
            s_failed = true;
            Plugin.Log.LogWarning("TrackAnim: " + e);
        }

        static float TrackSpeed(NetVehicle v, float sideX)
        {
            Vector3 vel, ang;
            if (v.Puppet)
            {
                var b = v.Buffer;
                if (b.Count == 0) return 0;
                var s = b[b.Count - 1];
                vel = s.Vel; ang = s.AngVel;
            }
            else
            {
                if (v.Body == null) return 0;
                vel = v.Body.linearVelocity; ang = v.Body.angularVelocity;
            }
            var fwd = v.Root.forward;
            float vf = Vector3.Dot(vel, fwd);
            float yaw = Vector3.Dot(ang, v.Root.up);
            // point at +x of a body turning with yaw rate w moves "forward" by -w*x
            return vf - yaw * sideX;
        }

        public static void Before(TrackBehaviour tb, float dt)
        {
            var v = BattleSync.VehicleOfTrack(tb);
            if (v == null) return;
            if (!s_tracks.TryGetValue(tb.Pointer, out var st))
            {
                st = new TrackState { V = v };
                try { st.SideX = v.Root.InverseTransformPoint(tb.Transform.position).x; } catch { }
                try
                {
                    var wm = tb.wheelsManaged;
                    int si = tb.sprocketIndex;
                    if (wm != null && si >= 0 && si < wm.Length && wm[si].radius > 0.01f) st.SprocketR = wm[si].radius;
                }
                catch { }
                try { var seg = tb.segmentController; if (seg != null) s_belts[seg.Pointer] = st; } catch { }
                s_tracks[tb.Pointer] = st;
            }
            st.Puppet = v.Puppet;
            st.Speed = TrackSpeed(v, st.SideX);
            if (!tb.TransformRotationsEnabled) tb.TransformRotationsEnabled = true;
            if (!tb.BeltMovementEnabled) tb.BeltMovementEnabled = true;
            if (!st.Puppet)
            {
                var wa = tb.wheelAngles;
                st.Pre0 = wa != null && wa.Length > 0 ? wa[0] : 0;
            }
        }

        public static void After(TrackBehaviour tb, float dt)
        {
            if (!s_tracks.TryGetValue(tb.Pointer, out var st) || dt <= 0) return;
            var wa = tb.wheelAngles;
            var wm = tb.wheelsManaged;
            if (wa == null || wm == null || wa.Length == 0) return;

            if (!st.Puppet)
            {
                // learn: angle change per (metres of track / wheel radius)
                float r0 = wm[0].radius;
                if (Mathf.Abs(st.Speed) > 1.5f && r0 > 0.01f)
                {
                    float d = wa[0] - st.Pre0;
                    if (Mathf.Abs(d) > 180f) d -= Mathf.Sign(d) * 360f;
                    float k = d * r0 / (st.Speed * dt);
                    if (!float.IsNaN(k) && Mathf.Abs(k) > 0.2f && Mathf.Abs(k) < 300f)
                    {
                        s_kWheel = s_nWheel == 0 ? k : Mathf.Lerp(s_kWheel, k, 0.05f);
                        s_nWheel++;
                    }
                }
                LogTick();
                return;
            }

            var la = tb.wheelLastAngles;
            int n = Math.Min(wa.Length, wm.Length);
            if (st.Angles == null || st.Angles.Length != n)
            {
                st.Angles = new float[n];
                for (int i = 0; i < n; i++) st.Angles[i] = wa[i];
            }
            // physically a wheel turns speed*dt/r radians: only the sign and the unit (rad / deg) are taken from the game
            bool degrees = Mathf.Abs(s_kWheel) > 10f;
            float kw = -Mathf.Sign(s_kWheel) * (degrees ? Mathf.Rad2Deg : 1f);   // sign checked visually in game
            float period = degrees ? 360f : Mathf.PI * 2f;
            for (int i = 0; i < n; i++)
            {
                float r = wm[i].radius;
                if (r < 0.01f) r = 0.3f;
                float prev = st.Angles[i];
                float next = prev + kw * st.Speed * dt / r;
                if (Mathf.Abs(next) > period * 1000f) { prev %= period; next %= period; }
                if (la != null && i < la.Length) la[i] = prev;
                wa[i] = next;
                st.Angles[i] = next;
            }
        }

        public static void Belt(TrackBeltBehaviour belt, float dt, ref float sprocketDeltaAngle)
        {
            if (!s_belts.TryGetValue(belt.Pointer, out var st) || dt <= 0) return;
            if (!st.Puppet)
            {
                if (Mathf.Abs(st.Speed) > 1.5f)
                {
                    float k = sprocketDeltaAngle * st.SprocketR / (st.Speed * dt);
                    if (!float.IsNaN(k) && Mathf.Abs(k) > 0.05f && Mathf.Abs(k) < 300f)
                    {
                        s_kBelt = s_nBelt == 0 ? k : Mathf.Lerp(s_kBelt, k, 0.05f);
                        s_nBelt++;
                    }
                }
                return;
            }
            sprocketDeltaAngle = Mathf.Sign(s_kBelt) * st.Speed * dt / st.SprocketR;
            st.LastBelt = sprocketDeltaAngle;
        }

        static void LogTick()
        {
            s_logTimer += Time.fixedDeltaTime;
            if (s_logTimer < 3f || !Cfg.DebugLog.Value) return;
            s_logTimer = 0;
            var pup = new List<string>();
            foreach (var st in s_tracks.Values) if (st.Puppet) pup.Add($"{st.V.Key:X8} x={st.SideX:F1} v={st.Speed:F1} belt={st.LastBelt:F4}");
            Plugin.Log.LogInfo($"TrackAnim: kWheel={s_kWheel:F2} ({s_nWheel}) kBelt={s_kBelt:F3} ({s_nBelt}) | " + string.Join("; ", pup));
        }
    }
}
