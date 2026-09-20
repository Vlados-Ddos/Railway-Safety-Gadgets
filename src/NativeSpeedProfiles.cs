using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Signs;
using HarmonyLib;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    // Calls the shipped SignPlacer's curve/segment algorithms without Make(),
    // PlaceSigns(), Start(), or any scene sign objects. DT uses its own helpers.
    internal static class NativeSpeedProfiles
    {
        private sealed class Cached { internal SpeedPoint[] Points; internal object Curve; internal double Length; }
        private static readonly Dictionary<RailTrack, Cached> cache = new Dictionary<RailTrack, Cached>();
        private static readonly HashSet<RailTrack> watched = new HashSet<RailTrack>();
        private static readonly Dictionary<Junction, SpeedPost[]> junctions = new Dictionary<Junction, SpeedPost[]>();
        private static Component calculator;
        private static Type type;
        private static bool usingDoubleTrack;
        private static readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>();
        internal static void Clear()
        {
            foreach (var track in watched) if (track != null) track.TrackPointsUpdated -= Changed;
            watched.Clear(); cache.Clear(); junctions.Clear();
            if (calculator != null) UnityEngine.Object.Destroy(calculator.gameObject);
            calculator = null; type = null; methods.Clear();
        }
        private static void Changed(RailTrack track) { cache.Clear(); junctions.Clear(); }
        private static object Call(string name, params object[] args)
        {
            MethodInfo method;
            if (!methods.TryGetValue(name, out method))
                methods[name] = method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Single(m => m.Name == name && m.GetParameters().Length == args.Length);
            return method.Invoke(method.IsStatic ? null : calculator, args);
        }
        private static float Setting(string name) { return (float)AccessTools.Field(type, name).GetValue(calculator); }
        private static bool Flag(string name) { return (bool)AccessTools.Field(type, name).GetValue(calculator); }
        private static float Constant(string vanilla, string dt) { return Convert.ToSingle(AccessTools.Field(type, usingDoubleTrack ? dt : vanilla).GetRawConstantValue()); }
        private static float Field(object segment, string name) { return (float)AccessTools.Field(segment.GetType(), name).GetValue(segment); }
        private static void Assign(object segment, float speed) { AccessTools.Field(segment.GetType(), "assignedSpeed").SetValue(segment, speed); }
        private static float Speed(object segment) { return (float)AccessTools.Method(segment.GetType(), "GetSpeed").Invoke(segment, null); }

        private static void EnsureCalculator(bool doubleTrack)
        {
            if (calculator != null && usingDoubleTrack == doubleTrack) return;
            Clear(); usingDoubleTrack = doubleTrack;
            type = doubleTrack ? AccessTools.TypeByName("DV.Signs.CustomSignPlacer") : typeof(SignPlacer);
            if (type == null) throw new InvalidOperationException("Active speed profile provider unavailable");
            // Inactive before AddComponent: the native Start must never place signs.
            var go = new GameObject("RSG_NativeSpeedCalculator"); go.SetActive(false);
            go.transform.SetParent(Main.Staging.transform, false);
            calculator = go.AddComponent(type);
            var live = UnityEngine.Object.FindObjectOfType(type) as Component;
            if (live != null && live != calculator)
                foreach (string field in new[] { "minArcLength", "error", "minimizeSpeedDiffThreshold", "minimizeSpeedSegmentLengthThreshold",
                    "lastSegmentOptimalLength", "mergeSameSpeedSigns", "minimizeSpeedDifference" })
                {
                    var member = AccessTools.Field(type, field); member.SetValue(calculator, member.GetValue(live));
                }
        }

        internal static IEnumerable<SpeedPoint> Points(RailTrack track, bool doubleTrack)
        {
            if (track == null || !track.CurveIsValid()) return new SpeedPoint[0];
            try
            {
                EnsureCalculator(doubleTrack);
                var length = NativeRoute.Instance.Length(track);
                Cached entry;
                if (cache.TryGetValue(track, out entry) && ReferenceEquals(entry.Curve, track.curve) && Math.Abs(entry.Length - length) < .001) return WithJunctions(track, entry.Points);
                var points = new List<SpeedPoint>();
                Build(track, false, points); Build(track, true, points);
                entry = new Cached { Points = points.ToArray(), Curve = track.curve, Length = length };
                cache[track] = entry;
                if (watched.Add(track)) track.TrackPointsUpdated += Changed;
                return WithJunctions(track, entry.Points);
            }
            catch (Exception ex)
            {
                Main.ErrorOnce("native-speed-profile", ex);
                // Do not invent a speed when the shipped data or API cannot be read.
                return new SpeedPoint[0];
            }
        }

        private static IEnumerable<SpeedPoint> WithJunctions(RailTrack track, SpeedPoint[] basePoints)
        {
            var points = new List<SpeedPoint>(basePoints);
            foreach (var junction in new[] { track.inJunction, track.outJunction }.Where(j => j != null).Distinct())
            {
                SpeedPost[] posts;
                if (!junctions.TryGetValue(junction, out posts))
                {
                    var generated = new List<SpeedPost>();
                    if (junction.inBranch != null && junction.inBranch.track != null && junction.outBranches.Count >= 2 &&
                        junction.outBranches.All(b => b != null && b.track != null &&
                            (b.first ? b.track.outBranch : b.track.inBranch) != null &&
                            (b.first ? b.track.outBranch : b.track.inBranch).track != null))
                        foreach (var data in (IEnumerable)Call("GetJunctionSigns", junction))
                        {
                            var dt = data.GetType();
                            var sourceTrack = (RailTrack)AccessTools.Method(dt, "GetTrack").Invoke(data, null);
                            var position = (Vector3)AccessTools.Method(dt, "GetPosition").Invoke(data, null) - WorldMover.currentMove;
                            var rotation = (Quaternion)AccessTools.Method(dt, "GetRotation").Invoke(data, null);
                            var sign = (Sign)AccessTools.Method(dt, "GetSign").Invoke(data, null);
                            var parameters = (IEnumerable<SignParameters>)AccessTools.Field(typeof(Sign), "signParameters").GetValue(sign);
                            double span; Vector3 tangent; float error;
                            if (!NativeRoute.Project(sourceTrack, position, out span, out tangent, out error)) continue;
                            generated.AddRange(SpeedSigns.Parse(parameters.ToArray(), span, Vector3.Dot(tangent, rotation * Vector3.forward) < 0 ? 1 : -1, junction));
                        }
                    posts = generated.ToArray(); junctions[junction] = posts;
                }
                foreach (var post in posts)
                {
                    if (post.AdvanceWarning) continue;
                    if (junction.inBranch.track == track) points.Add(new SpeedPoint(post.Span, post.Direction, post.Value()));
                    for (int i = 0; i < junction.outBranches.Count; i++)
                    {
                        var branch = junction.outBranches[i];
                        if (branch == null || branch.track != track || post.Branches == null) continue;
                        int index = Array.IndexOf(post.Branches, i);
                        if (index < 0) continue;
                        int dir = branch.first ? 1 : -1;
                        double origin = dir == 1 ? 0 : NativeRoute.Instance.Length(track);
                        points.RemoveAll(p => p.Direction == dir && Math.Abs(p.Span - origin) < .0001);
                        points.Add(new SpeedPoint(origin, dir, post.Values[index]));
                    }
                }
            }
            return points;
        }

        private static void Build(RailTrack track, bool reverse, List<SpeedPoint> result)
        {
            // Same sequence and thresholds as the native GetTrackSigns overload.
            // Keep a value at the track origin even when no first sign is placed.
            var curve = track.curve;
            object list = Call("GetSegmentInfos", curve, Setting("error"), Setting("minArcLength"), reverse);
            list = Call("SplitLastSegmentIfNeeded", list, Setting("lastSegmentOptimalLength"));
            var segments = ((IEnumerable)list).Cast<object>().ToList();
            if (segments.Count == 0) return;
            bool connected = reverse ? track.inIsConnected : track.outIsConnected;
            bool junction = (reverse ? track.inJunction : track.outJunction) != null;
            var branch = reverse ? track.inBranch : track.outBranch;
            float endCap = float.PositiveInfinity;
            float junctionCap = Constant("LAST_SEGMENT_CONNECTED_MAX_SPEED", "lastSegmentConnectedMaxSpeed");
            if (!connected) endCap = Constant("LAST_SEGMENT_UNCONNECTED_MAX_SPEED", "lastSegmentUnconnectedMaxSpeed");
            else if (junction || (branch != null && branch.track != null && branch.track.name == RailTrack.JUNCTION_DIVERGING_TRACK_NAME)) endCap = junctionCap;
            else if (branch != null && branch.track != null)
            {
                // Native helper expects a junction at the far end of this branch.
                var far = branch.first ? branch.track.outJunction : branch.track.inJunction;
                if (far != null)
                {
                    var next = Call("GetSegmentInfoAfterJunction", branch);
                    if (next != null) endCap = Speed(next);
                }
            }
            Assign(segments.Last(), Math.Min(Speed(segments.Last()), endCap));
            if (Flag("mergeSameSpeedSigns")) list = Call("MergeSameSpeed", list);
            if (Flag("minimizeSpeedDifference")) list = Call("MinimizeSpeedDifference", list, Setting("minimizeSpeedDiffThreshold"), Setting("minimizeSpeedSegmentLengthThreshold"));
            segments = ((IEnumerable)list).Cast<object>().ToList();
            bool included = track.isJunctionTrack || (bool)Call("ShouldIncludeTrack", track);
            float yardCap = Constant("YARD_OR_SHORT_TRACK_AFTER_JUNCTION_MAX_SPEED", "yardOrShortTrackMaxSpeed");
            float length = curve.length;
            if (length <= 0) return;
            for (int i = 0; i < segments.Count; i++)
            {
                object current = segments[i], previous = segments[Math.Max(0, i - 1)], next = segments[Math.Min(segments.Count - 1, i + 1)];
                if (Speed(current) > Speed(next) && Field(current, "segmentLength") <= Constant("PREVENT_SPEED_UP_RANGE", "preventSpeedUpRange")) Assign(current, Speed(next));
                float t = Field(current, "bezierStartT");
                if (i > 0 && Speed(current) < Speed(previous))
                    t += (reverse ? 1 : -1) * Constant("SIGN_PULL_BACK_DISTANCE", "signPullBackDistance") / length;
                if (i == segments.Count - 1) Assign(current, Math.Min(Math.Min(Speed(previous), Speed(current)), junctionCap));
                int speed = Mathf.RoundToInt(included ? Speed(current) : Math.Min(yardCap, Speed(current)));
                if (speed <= 0 || speed > 999) throw new InvalidOperationException("Invalid native track limit");
                double span; Vector3 tangent; float error;
                if (i == 0) span = reverse ? NativeRoute.Instance.Length(track) : 0;
                else if (!NativeRoute.Project(track, curve.GetPointAt(Mathf.Clamp01(t)) - WorldMover.currentMove, out span, out tangent, out error))
                    throw new InvalidOperationException("Cannot locate native speed boundary on its own track");
                int direction = reverse ? -1 : 1;
                result.RemoveAll(p => p.Direction == direction && Math.Abs(p.Span - span) < .0001);
                result.Add(new SpeedPoint(span, direction, speed));
            }
        }
    }
}
