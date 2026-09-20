using System;
using System.Collections.Generic;

namespace RailwaySafetyGadgets
{
    // Small geometric supplement to native connections. No switch or speed rules.
    public interface IRouteGraph<T>
    {
        bool Valid(T track);
        double Length(T track);
        bool Next(T track, int direction, out T next, out int nextDirection);
    }

    public struct TrackPosition<T>
    {
        public T Track;
        public double Span;
        public int Direction;
        public TrackPosition(T track, double span, int direction) { Track = track; Span = span; Direction = direction; }
    }

    public struct RouteLeg<T>
    {
        public T Track;
        public int Direction;
        public double Start, End, Distance;
        public double DistanceAt(double span) { return Distance + (span - Start) * Direction; }
        public bool Contains(double span) { return (span - Start) * Direction >= -0.0001 && (End - span) * Direction >= -0.0001; }
    }

    public struct SpeedPoint
    {
        public double Span;
        public int Direction;
        public int? Value;
        public SpeedPoint(double span, int direction, int? value) { Span = span; Direction = direction; Value = value; }
    }

    public static class RouteLogic
    {
        // Profiles include an origin value (possibly unknown) for every track.
        // Never infer the current limit from a different branch behind the train.
        public static void FindProfileLimits<T>(IEnumerable<RouteLeg<T>> ahead,
            Func<T, IEnumerable<SpeedPoint>> lookup, out int? current, out int? next)
        {
            current = next = null; bool first = true;
            foreach (var leg in ahead)
            {
                var points = new List<SpeedPoint>();
                foreach (var p in lookup(leg.Track)) if (p.Direction == leg.Direction) points.Add(p);
                points.Sort((a, b) => (a.Span * leg.Direction).CompareTo(b.Span * leg.Direction));
                int? entry = null;
                foreach (var p in points) if ((p.Span - leg.Start) * leg.Direction <= .0001) entry = p.Value;
                if (first) { current = entry; first = false; }
                else if (!entry.HasValue) return;
                else if (entry != current) { next = entry; return; }
                foreach (var p in points)
                {
                    if (!leg.Contains(p.Span) || (p.Span - leg.Start) * leg.Direction <= .0001) continue;
                    if (!p.Value.HasValue) return;
                    if (p.Value != current) { next = p.Value; return; }
                }
            }
        }

        public static List<RouteLeg<T>> Walk<T>(IRouteGraph<T> graph, TrackPosition<T> start, double distance, int maxTracks = 64)
        {
            var legs = new List<RouteLeg<T>>();
            var visited = new HashSet<T>();
            T track = start.Track;
            double span = start.Span, used = 0;
            int direction = start.Direction;
            while (legs.Count < maxTracks && graph.Valid(track) && visited.Add(track) && used <= distance)
            {
                double length = graph.Length(track);
                if (length <= 0 || direction * direction != 1 || span < -0.01 || span > length + 0.01) break;
                span = Math.Max(0, Math.Min(length, span));
                double available = direction > 0 ? length - span : span;
                double step = Math.Min(available, distance - used);
                legs.Add(new RouteLeg<T> { Track = track, Direction = direction, Start = span, End = span + direction * step, Distance = used });
                used += step;
                if (step < available || used >= distance) break;
                if (!graph.Next(track, direction, out track, out direction)) break;
                span = direction > 0 ? 0 : graph.Length(track);
            }
            return legs;
        }

        public static bool Advance<T>(IRouteGraph<T> graph, TrackPosition<T> start, double distance, out TrackPosition<T> result)
        {
            // Most body overhang samples stay on this track. Keep Walk's span
            // tolerance and exact-endpoint semantics without allocating a route.
            if (distance >= 0 && graph.Valid(start.Track) && start.Direction * start.Direction == 1)
            {
                double length = graph.Length(start.Track);
                if (length > 0 && start.Span >= -.01 && start.Span <= length + .01)
                {
                    double span = Math.Max(0, Math.Min(length, start.Span));
                    double available = start.Direction > 0 ? length - span : span;
                    if (distance <= available)
                    {
                        result = new TrackPosition<T>(start.Track, span + start.Direction * distance, start.Direction);
                        return true;
                    }
                }
            }
            var legs = Walk(graph, start, distance);
            result = start;
            if (legs.Count == 0) return false;
            var last = legs[legs.Count - 1];
            result = new TrackPosition<T>(last.Track, last.End, last.Direction);
            return last.DistanceAt(last.End) >= distance - 0.001;
        }

        public static bool Swept<T>(IRouteGraph<T> graph, TrackPosition<T> before, TrackPosition<T> after,
            double maximum, out List<RouteLeg<T>> legs, out double distance)
        {
            legs = Walk(graph, before, maximum); distance = 0;
            for (int i = 0; i < legs.Count; i++)
            {
                var leg = legs[i];
                if (!EqualityComparer<T>.Default.Equals(leg.Track, after.Track) || leg.Direction != after.Direction || !leg.Contains(after.Span)) continue;
                distance = leg.DistanceAt(after.Span);
                if (distance < -0.0001) return false;
                leg.End = after.Span; legs[i] = leg;
                legs.RemoveRange(i + 1, legs.Count - i - 1);
                return true;
            }
            legs.Clear(); return false;
        }

        public static bool ActuallyPassed<T>(RouteLeg<T> leg, double signalSpan, double totalDistance)
        {
            // Merely reaching the plane is not passing it. A subsequent measured
            // movement from the plane to its far side is a real passage.
            double d = leg.DistanceAt(signalSpan);
            return leg.Contains(signalSpan) && d >= -0.00001 && d < totalDistance - 0.00001;
        }

        public static int ResolveDirection(double signedSpeed, double? reverser, int previous)
        {
            if (Math.Abs(signedSpeed) > 0.035) return signedSpeed > 0 ? 1 : -1;
            if (reverser.HasValue && Math.Abs(reverser.Value - 0.5) > 0.1) return reverser.Value > 0.5 ? 1 : -1;
            return previous == -1 ? -1 : 1;
        }

        private static readonly int[] Digits = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
        public static int DigitSegments(int? value, int digit)
        {
            if (!value.HasValue || value < 0 || value > 999) return 0x40;
            int divisor = digit == 0 ? 100 : digit == 1 ? 10 : 1;
            if (digit < 2 && value < divisor) return 0;
            return Digits[value.Value / divisor % 10];
        }
    }
}
