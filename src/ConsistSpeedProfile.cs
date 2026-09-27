using System;
using System.Collections.Generic;
using UnityEngine;
using DV.Simulation.Brake;

namespace RailwaySafetyGadgets
{
    // Structure is rebuilt only on a native trainset change. Moving boundaries,
    // brake condition and occupied positions are sampled at the display's 5 Hz.
    internal sealed class ConsistSpeedProfile : IDisposable
    {
        private TrainCar owner, front, rear;
        private Trainset set;
        private int frontOut, rearOut;
        private double length;
        private bool dirty = true, subscribed;
        private int lastDirection;
        private int structureRebuilds, directionInvalidations, queries;
        private int? lastConfirmedLimit;
        private double? lastCurve;
        private LimitBoundary? heldTarget;
        private readonly List<RouteLeg<RailTrack>> forecastRoute = new List<RouteLeg<RailTrack>>();
        private readonly List<TrainCar> cars = new List<TrainCar>();
        private readonly List<RouteLeg<RailTrack>> aheadBuffer = new List<RouteLeg<RailTrack>>();
        private readonly HashSet<RailTrack> aheadVisited = new HashSet<RailTrack>();
        private readonly List<RouteLeg<RailTrack>> occupied = new List<RouteLeg<RailTrack>>();
        private readonly HashSet<RailTrack> occupiedVisited = new HashSet<RailTrack>();
        private readonly List<LimitBoundary> limits = new List<LimitBoundary>();
        private readonly List<LimitBoundary> occupiedLimits = new List<LimitBoundary>();
        private readonly ConsistNextLimit nextProfile = new ConsistNextLimit();
        private readonly List<SpeedPoint> scratch = new List<SpeedPoint>();
        private readonly Dictionary<RailTrack, IEnumerable<SpeedPoint>> queryPoints = new Dictionary<RailTrack, IEnumerable<SpeedPoint>>();
        private struct GradeSegment { internal double Start, End, Slope; }
        private readonly Dictionary<RailTrack, GradeSegment[]> grades = new Dictionary<RailTrack, GradeSegment[]>();
        private readonly List<GradeSegment> routeGrades = new List<GradeSegment>();
        private readonly Dictionary<BrakeSystem, bool> readyBrakes = new Dictionary<BrakeSystem, bool>();
        private readonly HashSet<BrakeSystem> visitingBrakes = new HashSet<BrakeSystem>();
        private readonly SteppedSpeedDisplay steps = new SteppedSpeedDisplay();
        private readonly Func<RailTrack, IEnumerable<SpeedPoint>> readPoints;
        internal ConsistSpeedProfile() { readPoints = Points; }
        internal int? TrackLimit { get; private set; }
        internal int? Display { get; private set; }
        internal int? NextLimit { get; private set; }
        internal LimitBoundary? NextTarget { get; private set; }
        internal bool NextTargetAtTail { get; private set; }
        internal double? Permitted { get; private set; }
        internal bool CurveAvailable { get; private set; }
        internal bool WholeConsistAvailable { get; private set; }
        internal double ClubUCurrentSpeedKmh { get; private set; }
        internal int? ClubUTargetLimit { get; private set; }
        internal double ClubUTargetDistance { get; private set; }
        internal double ClubUDeceleration { get; private set; }
        internal double ClubURequiredBrakingDistance { get; private set; }
        internal string BrakeDataStatus { get; private set; }
        internal LimitBoundary? CurveTarget { get; private set; }
        internal TrackPosition<RailTrack> LeadingEnd { get; private set; }
        internal int StructureRebuilds { get { return structureRebuilds; } }
        internal int DirectionInvalidations { get { return directionInvalidations; } }
        internal int QueryCount { get { return queries; } }
        internal string Status { get; private set; }
        internal double LengthMetres { get; private set; }

        internal void Reset() { NextTarget = null; NextTargetAtTail = false; nextProfile.Clear(); occupiedLimits.Clear(); TrackLimit = Display = NextLimit = lastConfirmedLimit = ClubUTargetLimit = null; Permitted = null; ForgetCurve(); forecastRoute.Clear(); CurveTarget = null; LeadingEnd = default(TrackPosition<RailTrack>); BrakeDataStatus = "position unavailable"; Status = "Consist position unavailable; native limit fallback"; ClubUCurrentSpeedKmh = ClubUDeceleration = 0; ClubUTargetDistance = ClubURequiredBrakingDistance = double.PositiveInfinity; CurveAvailable = WholeConsistAvailable = false; steps.Reset(); dirty = true; lastDirection = 0; }
        public void Dispose()
        {
            if (subscribed) Trainset.TrainsetsChanged -= Changed;
            subscribed = false;
            occupied.Clear(); occupiedVisited.Clear();
            if (owner != null) owner.OnRerailed -= Reset;
            foreach (var track in grades.Keys) if (track != null) track.TrackPointsUpdated -= TrackChanged;
            grades.Clear(); queryPoints.Clear(); routeGrades.Clear(); readyBrakes.Clear(); visitingBrakes.Clear(); cars.Clear(); aheadBuffer.Clear(); aheadVisited.Clear(); limits.Clear(); scratch.Clear(); owner = front = rear = null; set = null; Reset();
        }
        private void Changed(bool merged)
        {
            // Native Merge/Split replaces the affected Trainset. Another train's
            // coupling must not invalidate this train's structure or held curve.
            if (owner == null || !ReferenceEquals(set, owner.trainset) || (set != null && set.cars.Count != cars.Count)) dirty = true;
        }
        private void TrackChanged(RailTrack track) { if (track != null) grades[track] = null; }

        private bool Structure(TrainCar car)
        {
            if (!subscribed) { Trainset.TrainsetsChanged += Changed; subscribed = true; }
            if (owner != car || !ReferenceEquals(set, car.trainset)) dirty = true;
            if (!dirty) return front != null && rear != null;
            structureRebuilds++;
            if (owner != car) { if (owner != null) owner.OnRerailed -= Reset; car.OnRerailed += Reset; }
            owner = car; set = car.trainset; cars.Clear(); length = 0; front = rear = null;
            lastConfirmedLimit = null; ForgetCurve(); forecastRoute.Clear(); steps.Reset();
            if (set == null) cars.Add(car); else cars.AddRange(set.cars);
            foreach (var item in cars) { if (item == null) return false; length += item.InterCouplerDistance; }
            if (cars.Count == 0 || !End(car, 1, out front, out frontOut) || !End(car, -1, out rear, out rearOut)) return false;
            // Publish only a complete topology. A transient native load/coupling
            // failure must retry, not cache partial endpoints as a valid consist.
            dirty = false;
            return true;
        }
        private bool End(TrainCar start, int direction, out TrainCar end, out int outward)
        {
            end = start; outward = direction;
            for (int i = 0; i < cars.Count; i++)
            {
                var coupling = outward > 0 ? end.frontCoupler : end.rearCoupler;
                if (coupling == null) return false;
                var other = coupling.coupledTo;
                if (other == null) return true;
                if (other.train == null || !cars.Contains(other.train)) return false;
                end = other.train;
                outward = other.isFrontCoupler ? -1 : 1;
            }
            return false; // cyclic or inconsistent coupling graph
        }
        private IEnumerable<SpeedPoint> Points(RailTrack track)
        {
            IEnumerable<SpeedPoint> result;
            if (!queryPoints.TryGetValue(track, out result)) queryPoints[track] = result = SpeedSigns.Points(track);
            return result;
        }
        private static bool OnRoute(Bogie bogie, List<RouteLeg<RailTrack>> occupied)
        {
            if (bogie == null || !bogie.fullyInitialized || bogie.HasDerailed || bogie.track == null || bogie.traveller == null) return false;
            foreach (var leg in occupied) if (leg.Track == bogie.track && leg.Contains(bogie.traveller.Span)) return true;
            return false;
        }
        private GradeSegment[] TrackGrades(RailTrack track)
        {
            GradeSegment[] cached;
            if (!grades.TryGetValue(track, out cached)) { track.TrackPointsUpdated += TrackChanged; grades[track] = null; }
            if (cached != null) return cached;
            var pointSet = track.GetKinkedPointSet();
            var points = pointSet == null ? null : pointSet.points;
            if (points == null || points.Length < 2) return null;
            cached = new GradeSegment[Math.Max(0, points.Length - 1)];
            for (int i = 0; i + 1 < points.Length; i++)
            {
                // Binary interval lookup requires native ordered, finite spans.
                // Corrupt geometry is unavailable data, never an assumed flat track.
                if (!ProtectionPolicy.Finite(points[i].span) || !ProtectionPolicy.Finite(points[i + 1].span) || points[i + 1].span < points[i].span) return null;
                Vector3 delta = (Vector3)points[i + 1].position - (Vector3)points[i].position;
                cached[i] = new GradeSegment { Start = points[i].span, End = points[i + 1].span,
                    Slope = delta.magnitude < .001f ? 0 : delta.y / delta.magnitude };
            }
            grades[track] = cached; return cached;
        }
        private double ReadGrades(List<RouteLeg<RailTrack>> route, bool collect, double until = double.PositiveInfinity)
        {
            double worst = 0;
            if (collect) routeGrades.Clear();
            foreach (var leg in route)
            {
                if (leg.Distance >= until) break;
                var sections = TrackGrades(leg.Track);
                if (sections == null)
                {
                    if (!collect) return double.NaN;
                    routeGrades.Add(new GradeSegment { Start = leg.Distance, End = leg.DistanceAt(leg.End), Slope = double.NaN });
                    continue;
                }
                double start = Math.Min(leg.Start, leg.End), end = Math.Max(leg.Start, leg.End);
                // Cached spans are ordered. Skip geometry outside this leg in
                // logarithmic time, retaining every overlapping native segment.
                int lo = 0, hi = sections.Length;
                while (lo < hi) { int mid = lo + (hi - lo) / 2; if (sections[mid].End <= start) lo = mid + 1; else hi = mid; }
                int first = lo; hi = sections.Length;
                while (lo < hi) { int mid = lo + (hi - lo) / 2; if (sections[mid].Start < end) lo = mid + 1; else hi = mid; }
                int stop = lo;
                for (int n = first; n < stop; n++)
                {
                    var g = sections[leg.Direction > 0 ? n : stop - 1 - (n - first)];
                    double low = Math.Max(g.Start, Math.Min(leg.Start, leg.End));
                    double high = Math.Min(g.End, Math.Max(leg.Start, leg.End));
                    if (high <= low) continue;
                    if (leg.DistanceAt(leg.Direction > 0 ? low : high) >= until) break;
                    double downhill = Math.Max(0, -g.Slope * leg.Direction);
                    worst = Math.Max(worst, downhill);
                    if (collect) routeGrades.Add(new GradeSegment { Start = leg.DistanceAt(leg.Direction > 0 ? low : high),
                        End = leg.DistanceAt(leg.Direction > 0 ? high : low), Slope = downhill });
                }
            }
            return worst;
        }
        private void ApplyTargetGrades(double levelDeceleration, double occupiedDownhill)
        {
            int section = 0; double worst = occupiedDownhill;
            for (int i = 0; i < limits.Count; i++)
            {
                var limit = limits[i];
                while (section < routeGrades.Count && routeGrades[section].Start < limit.Distance)
                    worst = Math.Max(worst, routeGrades[section++].Slope);
                // Apply the planning cap AFTER subtracting gravity. A remote
                // descent beyond this target cannot cancel an earlier curve.
                limit.Deceleration = Math.Min(.35, levelDeceleration - Math.Abs(Physics.gravity.y) * worst);
                limits[i] = limit;
            }
        }
        private bool BrakeReady(BrakeSystem brake)
        {
            if (brake == null || !ProtectionPolicy.Finite(brake.controlReservoirPressure) || brake.controlReservoirPressure < 4.5f) return false;
            bool ready;
            if (readyBrakes.TryGetValue(brake, out ready)) return ready;
            if (!visitingBrakes.Add(brake)) return false;
            if (brake.hasCompressor) ready = ProtectionPolicy.Finite(brake.mainReservoirPressure) && brake.mainReservoirPressure >= 4.5f;
            else if (brake.brakeCylinderPressureCalculation == BrakeSystem.BrakeCylinderPressureCalculation.Regular)
                ready = ProtectionPolicy.Finite(brake.auxReservoirPressure) && brake.auxReservoirPressure >= 4.5f;
            else
            {
                var mode = brake.brakeCylinderPressureCalculation;
                ready = (mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyFront || mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyMax) &&
                    brake.Front.IsFullyConnected && BrakeReady(brake.Front.connectedTo.parentSystem);
                if (!ready && (mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyRear || mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyMax))
                    ready = brake.Rear.IsFullyConnected && BrakeReady(brake.Rear.connectedTo.parentSystem);
            }
            visitingBrakes.Remove(brake); readyBrakes[brake] = ready; return ready;
        }
        private double BrakeDeceleration()
        {
            double force = 0, mass = 0;
            readyBrakes.Clear(); visitingBrakes.Clear();
            var brakeSet = owner.brakeSystem == null ? null : owner.brakeSystem.brakeset;
            BrakeDataStatus = "ready";
            if (brakeSet == null) { BrakeDataStatus = "missing brake set"; return 0; }
            // Native SimulateTrainBrake returns when this field is FALSE.
            // Despite its name, TRUE means the controlling valve is cut IN.
            if (!owner.brakeSystem.trainBrakeCutout) { BrakeDataStatus = "train brake valve cut out"; return 0; }
            foreach (var car in cars)
            {
                if (car.rb == null || car.FrontBogie == null || car.RearBogie == null || car.FrontBogie.rb == null || car.RearBogie.rb == null)
                { BrakeDataStatus = "missing body/bogie mass"; return 0; }
                mass += car.rb.mass + car.FrontBogie.rb.mass + car.RearBogie.rb.mass;
                var b = car.brakeSystem;
                // Disconnected/unready vehicles contribute mass, not fictitious
                // brake force. CopyFront/Rear/Max use their real supplying valve.
                if (b == null || b.brakeset != brakeSet || b.heatController == null || !BrakeReady(b)) continue;
                force += (car.FrontBogie.maxBrakingForcePerKg * car.FrontBogie.rb.mass +
                    car.RearBogie.maxBrakingForcePerKg * car.RearBogie.rb.mass) * Math.Max(0, Math.Min(1, b.heatController.overheatReductionFactor));
            }
            if (mass <= 0 || !ProtectionPolicy.Finite(mass) || !ProtectionPolicy.Finite(force))
            { BrakeDataStatus = "invalid mass/brake force"; return 0; }
            if (force <= 0) BrakeDataStatus = "no charged connected brakes";
            return .35 * force / mass;
        }
        private void Fallback(int? limit, bool enableCurve)
        {
            TrackLimit = limit; Permitted = limit; Display = limit;
            if (enableCurve && lastCurve.HasValue)
            {
                if (!Permitted.HasValue || lastCurve < Permitted) Permitted = lastCurve;
            }
        }

        internal void Query(TrainCar car, int direction, int? legacyCurrent, bool enableCurve, double warningSeconds, double currentSpeedKmh,
            List<RouteLeg<RailTrack>> sharedAhead = null, int displayStep = 1)
        {
            // Publish once, including early returns/failures. Intermediate native
            // fallback values must never enter the display's increase hysteresis.
            try { QueryCore(car, direction, legacyCurrent, enableCurve, warningSeconds, currentSpeedKmh, sharedAhead); }
            catch { NextTarget = null; NextLimit = null; throw; }
            finally
            {
                if (enableCurve) Display = steps.Observe(Permitted, Time.time, displayStep);
                else { steps.Reset(); Display = TrackLimit; }
            }
        }

        private void ForgetCurve() { lastCurve = null; heldTarget = null; }

        // An unavailable sample is not proof that a target disappeared. A
        // confirmed different branch, passage, or known edited profile is.
        private void ReconcileRoute(List<RouteLeg<RailTrack>> ahead)
        {
            if (forecastRoute.Count > 0 && ahead.Count > 0)
            {
                int offset = -1;
                for (int i = 0; i < forecastRoute.Count; i++)
                    if (forecastRoute[i].Track == ahead[0].Track && forecastRoute[i].Direction == ahead[0].Direction) { offset = i; break; }
                if (offset < 0) ForgetCurve();
                else
                {
                    int lastRelevant = forecastRoute.Count - 1;
                    if (heldTarget.HasValue)
                        for (int i = 0; i < forecastRoute.Count; i++)
                            if (ReferenceEquals(forecastRoute[i].Track, heldTarget.Value.Track) && forecastRoute[i].Direction == heldTarget.Value.Direction) { lastRelevant = i; break; }
                    for (int i = 0; i < ahead.Count && i + offset <= lastRelevant; i++)
                        if (ahead[i].Track != forecastRoute[i + offset].Track || ahead[i].Direction != forecastRoute[i + offset].Direction) { ForgetCurve(); break; }
                    if (heldTarget.HasValue)
                        for (int i = 0; i < offset; i++)
                            if (ReferenceEquals(forecastRoute[i].Track, heldTarget.Value.Track) && forecastRoute[i].Direction == heldTarget.Value.Direction) { ForgetCurve(); break; }
                }
            }
            if (heldTarget.HasValue && ahead.Count > 0) {
                var target = heldTarget.Value; var head = ahead[0];
                if (ReferenceEquals(head.Track, target.Track) && head.Direction == target.Direction &&
                    (head.Start - target.Span) * head.Direction > .0001) ForgetCurve();
            }
            forecastRoute.Clear(); forecastRoute.AddRange(ahead);
        }

        private void ReconcileCurve(List<RouteLeg<RailTrack>> ahead, int? occupiedLimit)
        {
            if (heldTarget.HasValue)
            {
                var target = heldTarget.Value;
                foreach (var leg in ahead)
                {
                    if (!ReferenceEquals(leg.Track, target.Track) || leg.Direction != target.Direction) continue;
                    if ((leg.Start - target.Span) * leg.Direction > .0001) { ForgetCurve(); break; }
                    if (!leg.Contains(target.Span)) continue;
                    double distance = leg.DistanceAt(target.Span);
                    bool known = true, present = false;
                    for (int i = 0; i < limits.Count; i++)
                    {
                        var point = limits[i];
                        if (point.Distance > distance + .0001) break;
                        if (!point.Limit.HasValue || point.Limit <= 0 || point.Limit > 999) { known = false; break; }
                        if (point.Track == target.Track && point.Direction == target.Direction && Math.Abs(point.Span - target.Span) < .0001 &&
                            point.Limit == target.Limit && SpeedEnvelope.IsReduction(limits, i, occupiedLimit)) present = true;
                    }
                    if (known && !present) ForgetCurve();
                    break;
                }
            }
        }

        private void QueryCore(TrainCar car, int direction, int? legacyCurrent, bool enableCurve, double warningSeconds, double currentSpeedKmh,
            List<RouteLeg<RailTrack>> sharedAhead)
        {
            queries++;
            CurveAvailable = WholeConsistAvailable = false; Status = "Consist position unavailable; native limit fallback";
            ClubUCurrentSpeedKmh = ProtectionPolicy.Finite(currentSpeedKmh) ? Math.Max(0, currentSpeedKmh) : 0;
            ClubUTargetLimit = null; CurveTarget = null; ClubUTargetDistance = ClubURequiredBrakingDistance = double.PositiveInfinity; ClubUDeceleration = 0;
            BrakeDataStatus = "position unavailable";
            NextLimit = null; NextTarget = null; NextTargetAtTail = false;
            if (direction != lastDirection) { lastDirection = direction; directionInvalidations++; lastConfirmedLimit = null; ForgetCurve(); forecastRoute.Clear(); steps.Reset(); }
            if (car != null && (owner != car || !ReferenceEquals(set, car.trainset) || dirty)) { lastConfirmedLimit = null; ForgetCurve(); }
            if (!enableCurve) ForgetCurve();
            // A temporarily unresolvable occupied route cannot increase a
            // previously confirmed lower tail limit on the same consist/direction.
            int? fallback = legacyCurrent;
            if (lastConfirmedLimit.HasValue && (!fallback.HasValue || fallback > lastConfirmedLimit)) fallback = lastConfirmedLimit;
            Fallback(fallback, enableCurve);
            queryPoints.Clear();
            if (car == null || !Structure(car)) { return; }
            var headCar = direction > 0 ? front : rear; var tailCar = direction > 0 ? rear : front;
            int headOut = direction > 0 ? frontOut : rearOut, tailOut = direction > 0 ? rearOut : frontOut;
            TrackPosition<RailTrack> head, tail;
            if (!NativeRoute.LeadingPosition(headCar, headOut, out head) || !NativeRoute.LeadingPosition(tailCar, tailOut, out tail))
            { return; }
            LeadingEnd = head;
            var ahead = sharedAhead;
            // The cab route can be reused only when its origin is the actual
            // leading end. A locomotive inside/pushing a consist has another origin.
            if (ahead == null || ahead.Count == 0 || ahead[0].Track != head.Track || ahead[0].Direction != head.Direction ||
                Math.Abs(ahead[0].Start - head.Span) > .000001)
            {
                RouteLogic.Walk(NativeRoute.Instance, head, 20000, aheadBuffer, aheadVisited);
                ahead = aheadBuffer;
            }
            if (ahead.Count == 0) { return; }
            if (enableCurve) { ReconcileRoute(ahead); Fallback(fallback, enableCurve); }
            tail.Direction = -tail.Direction;
            double measured;
            if (!RouteLogic.Swept(NativeRoute.Instance, tail, head, length * 1.25 + 30, occupied, occupiedVisited, out measured))
            { return; }
            foreach (var member in cars)
                if (member == null || !OnRoute(member.FrontBogie, occupied) || !OnRoute(member.RearBogie, occupied)) { return; }
            LengthMetres = measured;
            var wholeLimit = SpeedEnvelope.OccupiedLimit(occupied, readPoints);
            if (!wholeLimit.HasValue) { Status = "Occupied route contains unknown limits; native limit fallback"; return; }
            Fallback(wholeLimit, enableCurve);
            WholeConsistAvailable = true;
            lastConfirmedLimit = wholeLimit;
            Status = "Whole-consist limit";
            if (!enableCurve)
            {
                int? headLimit, firstLimit; TrackPosition<RailTrack>? firstPosition; double firstDistance;
                RouteLogic.FindProfileLimits(ahead, readPoints, out headLimit, out firstLimit, out firstPosition, out firstDistance);
                // When the occupied minimum still reaches the head, the first
                // lower boundary necessarily applies before any tail release.
                // Preserve the original cheap next-boundary query for this case.
                if (headLimit == wholeLimit && firstLimit > 0 && firstLimit < wholeLimit && firstPosition.HasValue)
                {
                    var p = firstPosition.Value;
                    NextLimit = firstLimit;
                    NextTarget = new LimitBoundary(firstDistance, firstLimit) { Track = p.Track, Span = p.Span, Direction = p.Direction };
                    return;
                }
            }
            SpeedEnvelope.Boundaries(occupied, readPoints, occupiedLimits, scratch);
            LimitBoundary next; bool atTail;
            if (!enableCurve)
            {
                // Read no farther than necessary to prove the next effective
                // change. A tail clearance may need several future legs; an
                // earlier known head reduction often needs just the first one.
                limits.Clear();
                foreach (var leg in ahead)
                {
                    bool known = SpeedEnvelope.AppendBoundaries(leg, readPoints, limits, scratch);
                    if (nextProfile.Find(wholeLimit, occupiedLimits, limits, measured, leg.DistanceAt(leg.End), out next, out atTail))
                    { NextTarget = next; NextLimit = next.Limit; NextTargetAtTail = atTail; break; }
                    if (!known) break;
                }
                return;
            }
            SpeedEnvelope.Boundaries(ahead, readPoints, limits, scratch);
            var covered = ahead[ahead.Count - 1];
            if (nextProfile.Find(wholeLimit, occupiedLimits, limits, measured, covered.DistanceAt(covered.End), out next, out atTail))
            { NextTarget = next; NextLimit = next.Limit; NextTargetAtTail = atTail; }
            ReconcileCurve(ahead, wholeLimit);
            Fallback(wholeLimit, enableCurve);
            bool knownProfile = true; double lastReduction = 0;
            for (int i = 0; i < limits.Count; i++)
            {
                if (!limits[i].Limit.HasValue || limits[i].Limit <= 0 || limits[i].Limit > 999) { knownProfile = false; break; }
                if (SpeedEnvelope.IsReduction(limits, i, wholeLimit)) lastReduction = limits[i].Distance;
            }
            if (knownProfile && lastReduction == 0 && !lastCurve.HasValue)
            {
                // No curve can lower the occupied cap. Still sample positions,
                // route selection and limits every query; skip only unused physics.
                Permitted = wholeLimit; CurveAvailable = true; ClubUDeceleration = double.NaN;
                BrakeDataStatus = "not required"; Status = "No reduction in known profile; whole-consist limit"; return;
            }
            double levelDeceleration = BrakeDeceleration();
            double occupiedDownhill = ReadGrades(occupied, false);
            ReadGrades(ahead, true, lastReduction); ApplyTargetGrades(levelDeceleration, occupiedDownhill);
            double deceleration = Math.Min(.35, levelDeceleration - Math.Abs(Physics.gravity.y) * occupiedDownhill);
            ClubUDeceleration = deceleration;
            SpeedEnvelope.FirstReduction(wholeLimit, limits, out var target, out var targetDistance, out var targetDeceleration);
            foreach (var boundary in limits)
                if (boundary.Distance == targetDistance && boundary.Limit == target) { CurveTarget = boundary; break; }
            ClubUTargetLimit = target; ClubUTargetDistance = targetDistance;
            if (targetDeceleration.HasValue) ClubUDeceleration = targetDeceleration.Value;
            if (!ProtectionPolicy.Finite(ClubUDeceleration)) BrakeDataStatus = "track grade unavailable";
            if (BrakeDataStatus == "ready" && ClubUDeceleration < .05) BrakeDataStatus = "insufficient deceleration after grade allowance";
            if (target.HasValue)
                ClubURequiredBrakingDistance = SpeedEnvelope.BrakingDistance(ClubUCurrentSpeedKmh, target.Value,
                    targetDeceleration ?? deceleration, warningSeconds + AutomaticBrakeWarning.MaxManualExtensionSeconds);
            var last = ahead[ahead.Count - 1];
            // Exactly the existing warning plus its bounded manual extension.
            // No additional arbitrary time allowance is introduced here.
            bool complete; int controllingBoundary;
            var curve = SpeedEnvelope.Calculate(wholeLimit, limits, deceleration,
                warningSeconds + AutomaticBrakeWarning.MaxManualExtensionSeconds, last.DistanceAt(last.End), ClubUCurrentSpeedKmh, out complete, out controllingBoundary);
            if (!curve.HasValue) { Status = "Curve unavailable: route/brake data; whole-consist limit"; return; }
            if (!complete && lastCurve.HasValue && lastCurve.Value < curve.Value) curve = lastCurve;
            else if (controllingBoundary >= 0) { lastCurve = curve; heldTarget = limits[controllingBoundary]; }
            else ForgetCurve();
            CurveAvailable = true; Status = complete ? "Route curve active" : "Known route curve; further route unavailable";
            Permitted = curve;
        }
    }
}
