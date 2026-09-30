using System;
using System.Collections.Generic;

namespace RailwaySafetyGadgets
{
    public struct LimitBoundary
    {
        public double Distance;
        public int? Limit;
        public double? Deceleration;
        public object Track;
        public double Span;
        public int Direction;
        public LimitBoundary(double distance, int? limit) { Distance = distance; Limit = limit; Deceleration = null; Track = null; Span = 0; Direction = 0; }
    }

    // Presentation target from the SAME occupied/ahead profile used by the
    // protection model. No world queries or brake outputs. The minimum over a
    // moving whole-consist window changes at a head entry or a tail clearance.
    // Reused lists and a monotonic queue keep this linear in profile boundaries.
    internal sealed class ConsistNextLimit
    {
        private readonly List<LimitBoundary> corridor = new List<LimitBoundary>();
        private readonly List<int> minimum = new List<int>();
        private int first;
        private static bool Valid(int? n) { return n.HasValue && n > 0 && n <= 999; }
        internal void Clear() { corridor.Clear(); minimum.Clear(); first = 0; }
        private void Add(LimitBoundary boundary, double offset)
        {
            boundary.Distance += offset;
            if (corridor.Count > 0)
            {
                var previous = corridor[corridor.Count - 1];
                if (boundary.Distance < previous.Distance - .000001) throw new ArgumentException("Unordered consist profile");
                if (boundary.Limit == previous.Limit) return;
                if (Math.Abs(boundary.Distance - previous.Distance) < .000001)
                { corridor[corridor.Count - 1] = boundary; return; }
            }
            corridor.Add(boundary);
        }
        private bool Enter(int index)
        {
            if (!Valid(corridor[index].Limit)) return false;
            while (minimum.Count > first && corridor[minimum[minimum.Count - 1]].Limit >= corridor[index].Limit)
                minimum.RemoveAt(minimum.Count - 1);
            minimum.Add(index); return true;
        }
        internal bool Find(int? occupiedLimit, IList<LimitBoundary> occupied, IList<LimitBoundary> ahead,
            double length, double coveredAhead, out LimitBoundary target, out bool atTail)
        {
            Clear(); target = default(LimitBoundary); atTail = false;
            if (!Valid(occupiedLimit) || !ProtectionPolicy.Finite(length) || length <= 0 ||
                !ProtectionPolicy.Finite(coveredAhead) || coveredAhead < 0 || occupied.Count == 0 || ahead.Count == 0) return false;
            for (int i = 0; i < occupied.Count; i++) Add(occupied[i], 0);
            for (int i = 0; i < ahead.Count; i++) Add(ahead[i], length);
            if (corridor.Count == 0 || Math.Abs(corridor[0].Distance) > .000001) return false;
            int head = -1, tail = 0;
            while (head + 1 < corridor.Count && corridor[head + 1].Distance <= length)
                if (!Enter(++head)) return false;
            if (minimum.Count == first || corridor[minimum[first]].Limit != occupiedLimit) return false;
            while (true)
            {
                double entry = head + 1 < corridor.Count ? corridor[head + 1].Distance - length : double.PositiveInfinity;
                double exit = tail + 1 < corridor.Count ? corridor[tail + 1].Distance : double.PositiveInfinity;
                double travel = Math.Min(entry, exit);
                if (!ProtectionPolicy.Finite(travel) || travel < 0 || travel > coveredAhead) return false;
                while (tail + 1 < corridor.Count && corridor[tail + 1].Distance <= travel) tail++;
                while (first < minimum.Count && minimum[first] < tail) first++;
                while (head + 1 < corridor.Count && corridor[head + 1].Distance - length <= travel)
                    if (!Enter(++head)) return false; // unknown occupied future is not permission to raise
                if (first == minimum.Count) return false;
                var limit = corridor[minimum[first]].Limit;
                if (limit == occupiedLimit) continue;
                atTail = limit > occupiedLimit;
                target = corridor[atTail ? tail : minimum[first]];
                target.Limit = limit; target.Distance = travel;
                return true;
            }
        }
    }

    public static class SpeedEnvelope
    {
        private static readonly Comparison<SpeedPoint> AscendingOrder = Ascending, DescendingOrder = Descending;
        private static bool Valid(int? value) { return value.HasValue && value > 0 && value <= 999; }

        // Every occupied metre is included, including zones wholly between
        // bogies. Higher limits at the head cannot release a lower tail limit.
        public static int? OccupiedLimit<T>(IEnumerable<RouteLeg<T>> route, Func<T, IEnumerable<SpeedPoint>> lookup)
        {
            int? minimum = null;
            foreach (var leg in route)
            {
                int? entry = null; double nearest = double.NegativeInfinity;
                foreach (var p in lookup(leg.Track))
                {
                    if (p.Direction != leg.Direction) continue;
                    double delta = (p.Span - leg.Start) * leg.Direction;
                    if (delta <= .0001 && delta >= nearest) { nearest = delta; entry = p.Value; }
                    if (delta > .0001 && leg.Contains(p.Span))
                    {
                        if (!Valid(p.Value)) return null;
                        minimum = minimum.HasValue ? Math.Min(minimum.Value, p.Value.Value) : p.Value;
                    }
                }
                if (!Valid(entry)) return null;
                minimum = minimum.HasValue ? Math.Min(minimum.Value, entry.Value) : entry;
            }
            return minimum;
        }

        public static void Boundaries<T>(IEnumerable<RouteLeg<T>> route, Func<T, IEnumerable<SpeedPoint>> lookup,
            List<LimitBoundary> output, List<SpeedPoint> scratch)
        {
            output.Clear();
            foreach (var leg in route)
                if (!AppendBoundaries(leg, lookup, output, scratch)) return;
        }
        internal static bool AppendBoundaries<T>(RouteLeg<T> leg, Func<T, IEnumerable<SpeedPoint>> lookup,
            List<LimitBoundary> output, List<SpeedPoint> scratch)
        {
            scratch.Clear(); int? entry = null; double nearest = double.NegativeInfinity;
            foreach (var p in lookup(leg.Track))
            {
                if (p.Direction != leg.Direction) continue;
                double delta = (p.Span - leg.Start) * leg.Direction;
                if (delta <= .0001)
                {
                    if (delta >= nearest) { nearest = delta; entry = p.Value; }
                }
                else if (leg.Contains(p.Span)) scratch.Add(p);
            }
            // Only traversed points need sorting. Entry still considers every
            // upstream point, including the track origin and unknown intervals.
            scratch.Sort(leg.Direction > 0 ? AscendingOrder : DescendingOrder);
            int? previous = output.Count == 0 ? null : output[output.Count - 1].Limit;
            if (output.Count == 0 || entry != previous) output.Add(new LimitBoundary(leg.Distance, entry) { Track = leg.Track, Span = leg.Start, Direction = leg.Direction });
            if (!Valid(entry)) return false;
            previous = entry;
            foreach (var p in scratch)
            {
                if (p.Value != previous) output.Add(new LimitBoundary(leg.DistanceAt(p.Span), p.Value) { Track = leg.Track, Span = p.Span, Direction = leg.Direction });
                if (!Valid(p.Value)) return false;
                previous = p.Value;
            }
            return true;
        }
        private static int Ascending(SpeedPoint a, SpeedPoint b) { return a.Span.CompareTo(b.Span); }
        private static int Descending(SpeedPoint a, SpeedPoint b) { return b.Span.CompareTo(a.Span); }

        public static bool IsReduction(IList<LimitBoundary> ahead, int index, int? occupiedLimit)
        {
            var point = ahead[index];
            int? previous = index == 0 ? occupiedLimit : ahead[index - 1].Limit;
            return Valid(point.Limit) && Valid(previous) && point.Limit < previous && point.Limit < occupiedLimit;
        }

        public static bool FirstReduction(int? currentLimit, IList<LimitBoundary> ahead,
            out int? targetLimit, out double distance, out double? deceleration)
        {
            targetLimit = null; distance = double.PositiveInfinity; deceleration = null;
            if (!Valid(currentLimit)) return false;
            for (int i = 0; i < ahead.Count; i++)
            {
                var point = ahead[i];
                if (!ProtectionPolicy.Finite(point.Distance) || point.Distance < 0) continue;
                if (!Valid(point.Limit)) break;
                if (point.Distance <= .0001) continue;
                if (IsReduction(ahead, i, currentLimit))
                { targetLimit = point.Limit; distance = point.Distance; deceleration = point.Deceleration; return true; }
            }
            return false;
        }

        public static double BrakingDistance(double currentSpeedKmh, double targetSpeedKmh,
            double deceleration, double responseSeconds)
        {
            if (!ProtectionPolicy.Finite(currentSpeedKmh) || !ProtectionPolicy.Finite(targetSpeedKmh) ||
                !ProtectionPolicy.Finite(deceleration) || !ProtectionPolicy.Finite(responseSeconds) ||
                currentSpeedKmh < 0 || targetSpeedKmh < 0 || deceleration <= 0 || responseSeconds < 0) return double.PositiveInfinity;
            double current = currentSpeedKmh / 3.6, target = targetSpeedKmh / 3.6;
            if (current <= target) return 0;
            return current * responseSeconds + (current * current - target * target) / (2 * deceleration);
        }

        // SI units internally. The displayed CLUB-U envelope is a speed
        // ceiling at the boundary, so it uses only the service-braking term
        // v² = v_target² + 2aD. responseSeconds is retained for the route
        // completeness horizon below; the automatic-brake warning interval is
        // deliberately not converted into an extra early display reduction.
        public static double? Calculate(int? occupiedLimit, IList<LimitBoundary> ahead, double deceleration,
            double responseSeconds, double coveredDistance)
        {
            bool complete;
            return Calculate(occupiedLimit, ahead, deceleration, responseSeconds, coveredDistance, out complete);
        }

        public static double? Calculate(int? occupiedLimit, IList<LimitBoundary> ahead, double deceleration,
            double responseSeconds, double coveredDistance, out bool complete)
        { return Calculate(occupiedLimit, ahead, deceleration, responseSeconds, coveredDistance,
            occupiedLimit.GetValueOrDefault(), out complete); }

        public static double? Calculate(int? occupiedLimit, IList<LimitBoundary> ahead, double deceleration,
            double responseSeconds, double coveredDistance, double currentSpeedKmh, out bool complete)
        {
            int controllingBoundary;
            return Calculate(occupiedLimit, ahead, deceleration, responseSeconds, coveredDistance, currentSpeedKmh, out complete, out controllingBoundary);
        }

        public static double? Calculate(int? occupiedLimit, IList<LimitBoundary> ahead, double deceleration,
            double responseSeconds, double coveredDistance, double currentSpeedKmh, out bool complete, out int controllingBoundary)
        {
            controllingBoundary = -1;
            complete = false;
            if (!Valid(occupiedLimit) || !ProtectionPolicy.Finite(deceleration) || deceleration <= 0 ||
                !ProtectionPolicy.Finite(responseSeconds) || responseSeconds < 0 || !ProtectionPolicy.Finite(coveredDistance) || coveredDistance < 0 ||
                !ProtectionPolicy.Finite(currentSpeedKmh) || currentSpeedKmh < 0 || currentSpeedKmh > 999) return null;
            // The measured speed affects the response horizon. The curve itself
            // remains a maximum permissible speed; a stopped train uses the
            // posted limit as a conservative planning seed, while a moving
            // train uses its actual velocity for reaction distance.
            double permitted = occupiedLimit.Value;
            double planningKmh = currentSpeedKmh > .1 ? currentSpeedKmh : permitted;
            double currentMs = planningKmh / 3.6;
            double horizon = currentMs * responseSeconds + currentMs * currentMs / (2 * deceleration) + 10;
            complete = coveredDistance >= horizon;
            bool known = false;
            double previous = -1;
            for (int i = 0; i < ahead.Count; i++)
            {
                var p = ahead[i];
                if (!ProtectionPolicy.Finite(p.Distance) || p.Distance < previous || p.Distance < 0) return null;
                previous = p.Distance;
                if (p.Distance > coveredDistance) { complete = false; break; }
                if (!Valid(p.Limit)) { if (p.Distance <= horizon) complete = false; break; }
                known = true;
                if (!IsReduction(ahead, i, occupiedLimit)) continue;
                double a = p.Deceleration ?? deceleration;
                if (!ProtectionPolicy.Finite(a) || a <= 0)
                {
                    complete = false;
                    return permitted < occupiedLimit.Value ? (double?)permitted : null;
                }
                horizon = Math.Max(horizon, currentMs * responseSeconds + currentMs * currentMs / (2 * a) + 10);
                double target = p.Limit.Value / 3.6;
                // Distance is measured from the leading end to the exact speed
                // boundary, so do not introduce an undocumented fixed offset.
                double distance = p.Distance;
                // Optional response-distance estimates use BrakingDistance
                // separately. A full warning timer here would leave a nonzero
                // v_target*T even as the required speed reduction tends to zero,
                // producing an early plateau at the target speed.
                double speedKmh = distance == 0 ? p.Limit.Value : Math.Max(p.Limit.Value, Math.Sqrt(target * target + 2 * a * distance) * 3.6);
                if (speedKmh < permitted) { permitted = speedKmh; controllingBoundary = i; }
            }
            // A short/partly unknown route must not discard an already confirmed
            // restriction. No prediction is made beyond the first unknown point.
            complete &= coveredDistance >= horizon;
            return known ? (double?)permitted : null;
        }
    }

    public sealed class SteppedSpeedDisplay
    {
        private int? value, pending;
        private double since;
        private int lastStep = 1;
        public void Reset() { value = pending = null; since = 0; }
        public int? Observe(double? permitted, double time)
        { return Observe(permitted, time, 1); }
        public int? Observe(double? permitted, double time, int step)
        {
            if (!permitted.HasValue || !ProtectionPolicy.Finite(permitted.Value) || permitted < 0 || permitted > 999)
            { Reset(); return null; }
            if (step != 1 && step != 5 && step != 10) step = 1;
            int floor = (int)Math.Floor(permitted.Value / step) * step;
            if (step != lastStep) { lastStep = step; value = floor; pending = null; }
            if (!value.HasValue || floor <= value) { value = floor; pending = null; }
            else if (pending != floor || time < since) { pending = floor; since = time; }
            else if (time - since >= 1) { value = floor; pending = null; }
            return value;
        }
    }
}
