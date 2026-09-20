using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using DV.Signs;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    internal sealed class SpeedPost
    {
        internal double Span;
        internal int Direction;
        internal int[] Values;
        internal int[] Branches;
        internal Junction Junction;
        internal bool AdvanceWarning;
        internal bool HasArrows;
        internal int? Value()
        {
            if (AdvanceWarning || Values == null || Values.Length == 0) return null;
            if (!HasArrows) return Values[0];
            if (Junction == null) return null;
            int i = Array.IndexOf(Branches, Junction.selectedBranch);
            return i < 0 ? (int?)null : Values[i];
        }
    }

    internal static class SpeedSigns
    {
        private static readonly Dictionary<RailTrack, List<SpeedPost>> Posts = new Dictionary<RailTrack, List<SpeedPost>>();
        private static readonly FieldInfo ParametersField = AccessTools.Field(typeof(Sign), "signParameters");
        private static JObject catalogue;
        private static bool catalogueChecked, catalogueValid, doubleTrack, bound;
        private static RailTrackRegistryBase registry;
        private static RailTrack anchor;
        private static int installedCount;
        private static float nextAttempt;

        internal static void InstallPatches(Harmony harmony)
        {
            Type dt = AccessTools.TypeByName("DV.Signs.CustomSignPlacer");
            doubleTrack = dt != null && Main.ModActive("DoubleTrack");
            // UMM IDs have changed in some releases: the loaded component itself
            // switches this adapter to DT when its real placement method is called.
            if (dt != null)
            {
                var place = AccessTools.Method(dt, "PlaceSign");
                if (place != null) harmony.Patch(place, postfix: new HarmonyMethod(typeof(SpeedSigns), nameof(CapturePlaced)));
            }
            harmony.Patch(AccessTools.Method(typeof(SignGenerator), "Start"), prefix: new HarmonyMethod(typeof(SpeedSigns), nameof(CaptureBaked)));
        }

        private static void CapturePlaced(object[] __args)
        {
            try
            {
                CheckSession();
                if (!doubleTrack) { doubleTrack = true; Posts.Clear(); installedCount = 0; bound = true; }
                object data = __args[0]; Type type = data.GetType();
                var track = (RailTrack)AccessTools.Method(type, "GetTrack").Invoke(data, null);
                var position = (Vector3)AccessTools.Method(type, "GetPosition").Invoke(data, null) - WorldMover.currentMove;
                var rotation = (Quaternion)AccessTools.Method(type, "GetRotation").Invoke(data, null);
                var sign = (Sign)AccessTools.Method(type, "GetSign").Invoke(data, null);
                double span; Vector3 tangent; float error;
                if (!NativeRoute.Project(track, position, out span, out tangent, out error)) return;
                int direction = Vector3.Dot(tangent, rotation * Vector3.forward) < 0 ? 1 : -1;
                Add(track, span, direction, ((IEnumerable<SignParameters>)ParametersField.GetValue(sign)).ToArray());
                bound = true;
            }
            catch (Exception ex) { Main.ErrorOnce("double-track-signs", ex); }
        }

        private static void CaptureBaked(SignGenerator __instance)
        {
            // The baked catalogue is authoritative at initial load. Native objects
            // are additionally observed to cover scene-local modifications.
            if (__instance == null || __instance.data == null || doubleTrack) return;
            try
            {
                EnsureBound();
                if (!bound) return;
                Bind(__instance.transform.position - WorldMover.currentMove, __instance.transform.forward, __instance.data.signParameters);
            }
            catch (Exception ex) { Main.ErrorOnce("live-signs", ex); }
        }

        private static void CheckSession()
        {
            var current = RailTrackRegistryBase.Instance;
            if (registry != current || (bound && anchor == null))
            {
                ClearSession(); registry = current;
            }
        }

        internal static void ClearSession()
        {
            registry = null; bound = false; anchor = null; Posts.Clear(); installedCount = 0; candidates.Clear(); nextAttempt = 0;
            NativeSpeedProfiles.Clear();
        }

        private sealed class Candidate { internal RailTrack Track; internal Bounds Bounds; }
        private static readonly List<Candidate> candidates = new List<Candidate>();

        private static void EnsureBound()
        {
            CheckSession();
            if (bound || doubleTrack || Time.unscaledTime < nextAttempt || registry == null) return;
            nextAttempt = Time.unscaledTime + 2;
            var tracks = registry.AllTracks;
            if (tracks == null || tracks.Length == 0) return;
            if (!catalogueChecked)
            {
                catalogueChecked = true;
                string path = Path.Combine(Main.ModPath, "Assets", "vanilla_signs.json");
                catalogue = JObject.Parse(File.ReadAllText(path));
                catalogueValid = true;
                foreach (var source in catalogue["sources"])
                {
                    string level = Path.Combine(Application.dataPath, (string)source["file"]);
                    if (!File.Exists(level) || Hash(level) != (string)source["sha256"]) { catalogueValid = false; break; }
                }
                if (!catalogueValid) Main.Log(Texts.Pick("Данные карты изменились. Сохранённый список знаков отключён; используются загруженные знаки.",
                    "Map data has changed. The saved sign list is disabled; using loaded signs."));
            }
            foreach (var track in tracks)
            {
                if (track == null || track.GetKinkedPointSet() == null || track.GetKinkedPointSet().points.Length < 2) continue;
                var points = track.GetKinkedPointSet().points;
                var bounds = new Bounds((Vector3)points[0].position, Vector3.zero);
                foreach (var point in points) bounds.Encapsulate((Vector3)point.position);
                bounds.Expand(8);
                candidates.Add(new Candidate { Track = track, Bounds = bounds });
                if (anchor == null) anchor = track;
            }
            if (candidates.Count == 0) return;
            int unresolved = 0;
            if (catalogueValid)
                foreach (var post in catalogue["signs"])
                {
                    var parameters = post["parameters"].Select(p => new SignParameters { type = (SignType)(int)p["type"], signText = (string)p["text"] }).ToArray();
                    if (!Bind(ModelAssets.Vector(post["position"]), ModelAssets.Vector(post["forward"]), parameters)) unresolved++;
                }
            bound = true;
            Main.Log(Texts.Pick("Знаки на пути: ", "Track signs: ") + installedCount + Texts.Pick("; не найдено соответствий: ", "; unmatched: ") + unresolved);
        }

        private static string Hash(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static bool Bind(Vector3 position, Vector3 forward, SignParameters[] parameters)
        {
            // Native placer moves signs -2 m along their local right. Undo that
            // placement offset to identify the actual rail, including parallel lines.
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            Vector3 center = position + 2 * right;
            RailTrack best = null; double bestSpan = 0; int bestDir = 0;
            float bestError = 3.0f, second = float.PositiveInfinity;
            foreach (var candidate in candidates)
            {
                if (candidate.Track == null || !candidate.Bounds.Contains(center)) continue;
                double span; Vector3 tangent; float distance;
                if (!NativeRoute.Project(candidate.Track, center, out span, out tangent, out distance)) continue;
                float alignment = Vector3.Dot(tangent, forward);
                if (Mathf.Abs(alignment) < .9f) continue;
                if (distance < bestError)
                {
                    second = bestError; bestError = distance; best = candidate.Track; bestSpan = span; bestDir = alignment < 0 ? 1 : -1;
                }
                else second = Mathf.Min(second, distance);
            }
            if (best == null || second - bestError < .15f) return false;
            Add(best, bestSpan, bestDir, parameters); return true;
        }

        private static void Add(RailTrack track, double span, int direction, SignParameters[] parameters)
        {
            foreach (var post in Parse(parameters, span, direction, direction > 0 ? track.outJunction : track.inJunction))
            {
                if (anchor == null) anchor = track;
                List<SpeedPost> list;
                if (!Posts.TryGetValue(track, out list)) Posts[track] = list = new List<SpeedPost>();
                int duplicate = list.FindIndex(p => p.Direction == direction && Math.Abs(p.Span - span) < .75);
                if (duplicate >= 0) list[duplicate] = post;
                else { list.Add(post); installedCount++; }
            }
        }

        internal static IEnumerable<SpeedPost> Parse(SignParameters[] parameters, double span, int direction, Junction junction)
        {
            var values = new List<int>(); var branches = new List<int>(); bool upcoming = false;
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.type == SignType.UpcomingJunction || p.type == SignType.UpcomingJunctionOld) upcoming = true;
                if ((int)p.type < 0 || (int)p.type > 3) continue;
                int value;
                if (!int.TryParse(p.signText, out value) || value <= 0 || value > 99) continue;
                int branch = -1;
                if (i + 1 < parameters.Length)
                {
                    var next = parameters[i + 1].type;
                    if (next == SignType.ArrowLeft) branch = 0;
                    if (next == SignType.ArrowRight) branch = 1;
                }
                values.Add(value * 10); branches.Add(branch);
            }
            if (values.Count == 0) yield break;
            bool hasArrows = branches.Any(b => b >= 0);
            var post = new SpeedPost { Span = span, Direction = direction, Values = values.ToArray(), Branches = branches.ToArray(), HasArrows = hasArrows,
                Junction = junction, AdvanceWarning = upcoming && hasArrows };
            yield return post;
        }

        internal static void Query(List<RouteLeg<RailTrack>> ahead, out int? current, out int? next)
        {
            EnsureBound();
            if (registry == null) { current = next = null; return; }
            RouteLogic.FindProfileLimits(ahead, Points, out current, out next);
        }

        private static IEnumerable<SpeedPoint> Points(RailTrack track)
        {
            var points = new List<SpeedPoint>(NativeSpeedProfiles.Points(track, doubleTrack));
            List<SpeedPost> posts;
            if (Posts.TryGetValue(track, out posts))
                foreach (var post in posts)
                {
                    if (post.AdvanceWarning) continue;
                    // Authored/live native signs override coincident calculated
                    // boundaries. They are an additional source, not a prerequisite.
                    points.RemoveAll(p => p.Direction == post.Direction && Math.Abs(p.Span - post.Span) < .75);
                    points.Add(new SpeedPoint(post.Span, post.Direction, post.Value()));
                }
            return points;
        }
    }
}
