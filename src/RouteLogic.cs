using System;
using System.Collections.Generic;

namespace RailwaySafetyGadgets
{
    // The reverser is a native normalized control: 0 reverse, .5 neutral, 1 forward.
    // Speed validates a selected reversal; rollback never selects a direction.
    public sealed class ConfirmedDirection
    {
        public const double StopSpeed = .035, StartSpeed = .15, ConfirmationSeconds = .15;
        private bool initialized, stopped;
        private int active = 1, candidate;
        private double since = double.NaN, lastTime = double.NaN;
        public int Active { get { return active; } }
        public int Pending { get { return stopped ? candidate : 0; } }
        public void Reset() { initialized = stopped = false; candidate = 0; since = lastTime = double.NaN; }
        public void Restore(int direction)
        {
            if (direction != -1 && direction != 1) return;
            Reset(); active = direction; initialized = true;
        }
        public int Observe(double speed, double? reverser, double time)
        {
            int selected = reverser.HasValue && !double.IsNaN(reverser.Value) && !double.IsInfinity(reverser.Value) ?
                reverser.Value > .6 ? 1 : reverser.Value < .4 ? -1 : 0 : 0;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || double.IsNaN(time) || double.IsInfinity(time))
            { stopped = false; since = double.NaN; return active; }
            if (!initialized) { active = selected != 0 ? selected : active; initialized = true; }
            if (!double.IsNaN(lastTime) && (time < lastTime || time - lastTime > 1)) { stopped = false; since = double.NaN; }
            lastTime = time;
            if (selected != candidate) { candidate = selected; since = double.NaN; }
            if (Math.Abs(speed) <= StopSpeed) stopped = true;
            // Departure in the old direction consumes the stop, even when the
            // reverser is opposite. A later reversal needs a newly observed stop.
            else if (speed * active >= StartSpeed) { stopped = false; since = double.NaN; }
            if (selected == 0 || selected == active) { since = double.NaN; if (Math.Abs(speed) >= StartSpeed) stopped = false; return active; }
            if (!stopped || speed * selected < StartSpeed) { since = double.NaN; return active; }
            if (double.IsNaN(since)) since = time;
            if (time - since >= ConfirmationSeconds)
            { active = selected; stopped = false; since = double.NaN; }
            return active;
        }
    }

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

    // Display-only quantization. The source distance, curve and protection
    // never receive this value. Called once per existing profile sample.
    internal sealed class DistanceReadout
    {
        internal const int Overflow = -1;
        private static readonly double[] Thresholds = { 10, 30, 60, 100 };
        private static readonly int[] Steps = { 1, 5, 10, 50, 100 };
        private int band = -1, lastStep;
        private object track;
        private double span;
        private int direction, limit;
        private bool tail;
        private int? displayed;
        internal int Step { get { return band < 0 ? 0 : Steps[band]; } }
        internal void Reset() { band = -1; displayed = null; track = null; lastStep = 0; }
        private static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
        internal int? Observe(object targetTrack, double targetSpan, int targetDirection, int? targetLimit,
            bool atTail, double? distance, double speedKmh)
        {
            if (targetTrack == null || !Finite(targetSpan) || (targetDirection != 1 && targetDirection != -1) ||
                !targetLimit.HasValue || targetLimit <= 0 || targetLimit > 999 || !distance.HasValue ||
                !Finite(distance.Value) || distance.Value < 0 || !Finite(speedKmh))
            { Reset(); return null; }
            // Native velocity is float m/s: e.g. 10 km/h round-trips as
            // 9.9999996. Normalize only this display-band input, not physics.
            double speed = Math.Round(Math.Abs(speedKmh), 3, MidpointRounding.AwayFromZero);
            if (band < 0)
            {
                band = 0; while (band < 4 && speed >= Thresholds[band]) band++;
            }
            else
            {
                // Small Schmitt band prevents 9.99/10.01 etc. from toggling the
                // resolution. Large speed changes cross all needed bands at once.
                while (band < 4 && speed >= Thresholds[band] + .25) band++;
                while (band > 0 && speed < Thresholds[band - 1] - .25) band--;
            }
            int step = Steps[band];
            bool same = ReferenceEquals(track, targetTrack) && span == targetSpan &&
                direction == targetDirection && limit == targetLimit && tail == atTail;
            track = targetTrack; span = targetSpan; direction = targetDirection; limit = targetLimit.Value; tail = atTail;
            int next = distance > 9999 ? Overflow : (int)(Math.Floor(distance.Value / step) * step);
            // Decreases are immediate. For the same target and step, suppress
            // only tiny upward boundary jitter; displayed distance stays <= true.
            if (same && lastStep == step && displayed.HasValue && displayed >= 0 && next >= 0 && next > displayed &&
                distance.Value < next + Math.Min(1, step * .1)) next = displayed.Value;
            lastStep = step; displayed = next; return next;
        }
    }

    public static class RouteLogic
    {
        // Profiles include an origin value (possibly unknown) for every track.
        // Never infer the current limit from a different branch behind the train.
        public static void FindProfileLimits<T>(IEnumerable<RouteLeg<T>> ahead,
            Func<T, IEnumerable<SpeedPoint>> lookup, out int? current, out int? next)
        {
            TrackPosition<T>? position; double distance;
            FindProfileLimits(ahead, lookup, out current, out next, out position, out distance);
        }
        internal static void FindProfileLimits<T>(IEnumerable<RouteLeg<T>> ahead,
            Func<T, IEnumerable<SpeedPoint>> lookup, out int? current, out int? next,
            out TrackPosition<T>? nextPosition, out double nextDistance)
        {
            current = next = null; nextPosition = null; nextDistance = double.PositiveInfinity; bool first = true;
            foreach (var leg in ahead)
            {
                var points = lookup(leg.Track);
                int? entry = null; double entryDistance = double.NegativeInfinity;
                foreach (var p in points)
                {
                    if (p.Direction != leg.Direction) continue;
                    double delta = (p.Span - leg.Start) * leg.Direction;
                    if (delta <= .0001 && delta >= entryDistance) { entryDistance = delta; entry = p.Value; }
                }
                if (first) { current = entry; first = false; }
                else if (!entry.HasValue) return;
                else if (entry != current) { next = entry; nextPosition = new TrackPosition<T>(leg.Track, leg.Start, leg.Direction); nextDistance = leg.Distance; return; }
                double nearest = double.PositiveInfinity, nearestSpan = 0; int? following = null;
                foreach (var p in points)
                {
                    if (p.Direction != leg.Direction || !leg.Contains(p.Span)) continue;
                    double delta = (p.Span - leg.Start) * leg.Direction;
                    if (delta <= .0001 || delta >= nearest || (p.Value.HasValue && p.Value == current)) continue;
                    nearest = delta; nearestSpan = p.Span; following = p.Value;
                }
                if (!double.IsPositiveInfinity(nearest))
                {
                    next = following;
                    if (following.HasValue) { nextPosition = new TrackPosition<T>(leg.Track, nearestSpan, leg.Direction); nextDistance = leg.Distance + nearest; }
                    return;
                }
            }
        }

        public static List<RouteLeg<T>> Walk<T>(IRouteGraph<T> graph, TrackPosition<T> start, double distance, int maxTracks = 64)
        {
            var legs = new List<RouteLeg<T>>();
            var visited = new HashSet<T>();
            Walk(graph, start, distance, legs, visited, maxTracks);
            return legs;
        }

        public static void Walk<T>(IRouteGraph<T> graph, TrackPosition<T> start, double distance,
            List<RouteLeg<T>> legs, HashSet<T> visited, int maxTracks = 64)
        {
            legs.Clear(); visited.Clear();
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
            legs = new List<RouteLeg<T>>();
            return Swept(graph, before, after, maximum, legs, new HashSet<T>(), out distance);
        }

        public static bool Swept<T>(IRouteGraph<T> graph, TrackPosition<T> before, TrackPosition<T> after,
            double maximum, List<RouteLeg<T>> legs, HashSet<T> visited, out double distance)
        {
            Walk(graph, before, maximum, legs, visited); distance = 0;
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
        internal static int DistanceDigitSegments(int? value, int digit)
        {
            if (digit < 0 || digit > 3) return 0;
            if (!value.HasValue) return 0x40; // ---- : no current paired target
            if (value == DistanceReadout.Overflow) return digit == 2 ? 0x76 : digit == 3 ? 0x06 : 0; // HI
            if (value < 0 || value > 9999) return 0x40;
            int divisor = digit == 0 ? 1000 : digit == 1 ? 100 : digit == 2 ? 10 : 1;
            if (digit < 3 && value < divisor) return 0;
            return Digits[value.Value / divisor % 10];
        }
    }
}
