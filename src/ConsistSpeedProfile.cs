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
        private struct GradeSegment { internal double Start, End, Slope, Height; }
        private readonly Dictionary<RailTrack, GradeSegment[]> grades = new Dictionary<RailTrack, GradeSegment[]>();
        private readonly List<GradeSegment> routeGrades = new List<GradeSegment>();
        private readonly Dictionary<BrakeSystem, double> readyBrakes = new Dictionary<BrakeSystem, double>();
        private struct MassPoint { internal double Position, Mass, Height, Slope; }
        private readonly List<MassPoint> massPoints = new List<MassPoint>();
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
        public double ConsistMassKg { get; private set; }
        public double ConnectedBrakeForce { get; private set; }
        internal double AppliedBrakeForce { get; private set; }
        internal double OpposingTractionForce { get; private set; }

        internal void Reset() { ConsistMassKg=ConnectedBrakeForce=AppliedBrakeForce=OpposingTractionForce=0; massPoints.Clear(); NextTarget = null; NextTargetAtTail = false; nextProfile.Clear(); occupiedLimits.Clear(); TrackLimit = Display = NextLimit = lastConfirmedLimit = ClubUTargetLimit = null; Permitted = null; ForgetCurve(); forecastRoute.Clear(); CurveTarget = null; LeadingEnd = default(TrackPosition<RailTrack>); BrakeDataStatus = "position unavailable"; Status = "Consist position unavailable; native limit fallback"; ClubUCurrentSpeedKmh = ClubUDeceleration = 0; ClubUTargetDistance = ClubURequiredBrakingDistance = double.PositiveInfinity; CurveAvailable = WholeConsistAvailable = false; steps.Reset(); dirty = true; lastDirection = 0; }
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
            ConsistMassKg=ConnectedBrakeForce=AppliedBrakeForce=OpposingTractionForce=0; massPoints.Clear();
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
        private bool AppendGrades(List<RouteLeg<RailTrack>> route, double offset, double until)
        {
            foreach (var leg in route)
            {
                if (leg.Distance >= until) break;
                var sections = TrackGrades(leg.Track);
                if (sections == null)
                {
                    // Unknown geometry beyond an earlier target must not erase
                    // that known curve. Height remains unknown after this gap.
                    routeGrades.Add(new GradeSegment { Start=offset+leg.Distance,
                        End=offset+Math.Min(until,leg.DistanceAt(leg.End)), Slope=double.NaN, Height=double.NaN });
                    continue;
                }
                double low = Math.Min(leg.Start, leg.End), high = Math.Max(leg.Start, leg.End);
                int lo = 0, hi = sections.Length;
                while (lo < hi) { int mid = lo + (hi - lo) / 2; if (sections[mid].End <= low) lo = mid + 1; else hi = mid; }
                int first = lo; hi = sections.Length;
                while (lo < hi) { int mid = lo + (hi - lo) / 2; if (sections[mid].Start < high) lo = mid + 1; else hi = mid; }
                int stop = lo;
                for (int n = first; n < stop; n++)
                {
                    var g = sections[leg.Direction > 0 ? n : stop - 1 - (n - first)];
                    double a = Math.Max(g.Start, low), b = Math.Min(g.End, high);
                    double from = offset + leg.DistanceAt(leg.Direction > 0 ? a : b);
                    double to = Math.Min(offset + until, offset + leg.DistanceAt(leg.Direction > 0 ? b : a));
                    if (to <= from) continue;
                    double height = 0;
                    if (routeGrades.Count > 0)
                    {
                        var previous = routeGrades[routeGrades.Count - 1];
                        if (Math.Abs(previous.End - from) > .01) return false;
                        height = previous.Height + (previous.End - previous.Start) * previous.Slope;
                    }
                    routeGrades.Add(new GradeSegment { Start = from, End = to, Slope = g.Slope * leg.Direction, Height = height });
                }
            }
            return routeGrades.Count > 0;
        }
        private double BogiePosition(Bogie bogie)
        {
            if (bogie == null || bogie.traveller == null) return double.NaN;
            foreach (var leg in occupied)
                if (leg.Track == bogie.track && leg.Contains(bogie.traveller.Span)) return leg.DistanceAt(bogie.traveller.Span);
            return double.NaN;
        }
        private bool PrepareGrades(List<RouteLeg<RailTrack>> ahead, double measuredLength, double until)
        {
            routeGrades.Clear(); massPoints.Clear();
            if ((measuredLength > .001 && !AppendGrades(occupied, 0, measuredLength)) || !AppendGrades(ahead, measuredLength, until)) return false;
            foreach (var car in cars)
            {
                double mass = car.massController.TotalMass;
                double bodyPosition = (BogiePosition(car.FrontBogie) + BogiePosition(car.RearBogie)) * .5;
                if (car.rb == null || !ProtectionPolicy.Finite(car.rb.mass) || car.rb.mass <= 0 ||
                    !ProtectionPolicy.Finite(mass) || mass <= 0) return false;
                double rigidMass = car.rb.mass;
                foreach (var bogie in car.Bogies)
                {
                    if (bogie == null || bogie.rb == null || !OnRoute(bogie, occupied) ||
                        !ProtectionPolicy.Finite(bogie.rb.mass) || bogie.rb.mass <= 0) return false;
                    rigidMass += bogie.rb.mass;
                }
                if (!ProtectionPolicy.Finite(bodyPosition) || rigidMass <= 0) return false;
                if (!AddMassPoint(bodyPosition, mass * car.rb.mass / rigidMass)) return false;
                foreach (var bogie in car.Bogies)
                {
                    double position = BogiePosition(bogie);
                    if (!ProtectionPolicy.Finite(position)) return false;
                    if (!AddMassPoint(position, mass * bogie.rb.mass / rigidMass)) return false;
                }
            }
            return true;
        }
        private bool AddMassPoint(double position, double mass)
        {
            if (!GradeAt(position, out double height, out double slope)) return false;
            // Starting heights are identical for every target in this query.
            massPoints.Add(new MassPoint { Position=position, Mass=mass, Height=height, Slope=slope });
            return true;
        }
        private double ReadTraction()
        {
            double total=0;
            foreach(var car in cars)
            {
                var sim=car.SimController;
                var drive=sim == null ? null : sim.drivingForce;
                if(drive == null || !drive.enabled) continue;
                double force=drive.generatedForce;
                var adhesion=car.adhesionController;
                if(adhesion != null)
                {
                    if(adhesion.wheelSlide > 0) continue; // native DrivingForce suppresses traction
                    if(adhesion.wheelslipController.IsSome(out var slip))
                        force=Math.Max(-slip.TotalForceLimit,Math.Min(slip.TotalForceLimit,force));
                }
                // Oppositely oriented locomotives use their local force sign.
                // Credit no future dynamic/reverse braking; retain traction
                // opposing the selected route until the driver actually removes it.
                double orientation=BogiePosition(car.FrontBogie)-BogiePosition(car.RearBogie);
                if(!ProtectionPolicy.Finite(force) || !ProtectionPolicy.Finite(orientation)) return double.NaN;
                total+=Math.Max(0,force*Math.Sign(orientation));
            }
            return total;
        }
        private bool GradeAt(double position, out double height, out double slope)
        {
            height = slope = double.NaN;
            int lo = 0, hi = routeGrades.Count;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (routeGrades[mid].End < position) lo = mid + 1; else hi = mid; }
            if (lo >= routeGrades.Count) return false;
            var section = routeGrades[lo];
            if (position < section.Start - .001) return false;
            slope = section.Slope; height = section.Height + (position - section.Start) * slope;
            return ProtectionPolicy.Finite(height) && ProtectionPolicy.Finite(slope);
        }
        private double EffectiveDeceleration(double level, double distance)
        {
            // Potential-energy change of every mass point along the selected
            // route. A short dip is not assigned to the entire train/distance;
            // uphill work is retained. At zero distance use the current profile.
            double grade = 0;
            foreach (var point in massPoints)
            {
                double slope = point.Slope;
                if (distance > 0)
                {
                    if (!GradeAt(point.Position + distance, out double endHeight, out slope)) return double.NaN;
                    slope = (endHeight - point.Height) / distance;
                }
                grade += point.Mass * slope;
            }
            return level + Math.Abs(Physics.gravity.y) * grade / ConsistMassKg;
        }
        // Settled full-service cylinder factor from native pressure limits.
        // This does not integrate pressure propagation or invent a fixed delay.
        private double ServiceFactor(BrakeSystem brake, double pipePressure)
        {
            if (brake == null) return 0;
            if (readyBrakes.TryGetValue(brake, out double cached)) return cached;
            if (!visitingBrakes.Add(brake)) return 0;
            double factor = 0;
            if (!ProtectionPolicy.Finite(brake.controlReservoirPressure) ||
                !ProtectionPolicy.Finite(brake.brakeCylinderPressureUnsmoothed)) factor = double.NaN;
            else if (!brake.controlReservoirPressureReleased)
            {
                var mode = brake.brakeCylinderPressureCalculation;
                if (mode != BrakeSystem.BrakeCylinderPressureCalculation.Regular)
                {
                    if ((mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyFront || mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyMax) && brake.Front.IsFullyConnected)
                        factor = ServiceFactor(brake.Front.connectedTo.parentSystem, pipePressure);
                    if ((mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyRear || mode == BrakeSystem.BrakeCylinderPressureCalculation.CopyMax) && brake.Rear.IsFullyConnected)
                        factor = Math.Max(factor, ServiceFactor(brake.Rear.connectedTo.parentSystem, pipePressure));
                }
                else
                {
                    double drop = brake.controlReservoirPressure - pipePressure;
                    double target = drop < BrakeSystemConsts.INITIAL_APPLICATION_PRESSURE_DROP ? 1 :
                        1.5 + 3 * Math.Max(0, Math.Min(1, (drop - BrakeSystemConsts.INITIAL_APPLICATION_PRESSURE_DROP) /
                        (BrakeSystemConsts.FULL_APPLICATION_PRESSURE_DROP - BrakeSystemConsts.INITIAL_APPLICATION_PRESSURE_DROP)));
                    double supply = brake.hasCompressor ? brake.mainReservoirPressure : brake.auxReservoirPressure;
                    double volume = brake.hasCompressor ? brake.mainResVolume : BrakeSystemConsts.AUX_RES_VOLUME;
                    // Available air mass bounds the attainable cylinder pressure;
                    // no credit for future compressor work or reservoir recharge.
                    if (!ProtectionPolicy.Finite(supply) || !ProtectionPolicy.Finite(volume) || volume <= 0) factor = double.NaN;
                    else
                    {
                        double equilibrium = (supply * volume + brake.brakeCylinderPressureUnsmoothed * BrakeSystemConsts.CYLINDER_VOLUME) /
                            (volume + BrakeSystemConsts.CYLINDER_VOLUME);
                        double cylinder = Math.Min(target, Math.Max(brake.brakeCylinderPressureUnsmoothed, equilibrium));
                        factor = Math.Max(0, Math.Min(1, (cylinder - BrakeSystemConsts.MIN_APPLICATION_PRESSURE) /
                            (BrakeSystemConsts.MAX_BRAKE_CYLINDER_PRESSURE - BrakeSystemConsts.MIN_APPLICATION_PRESSURE)));
                    }
                }
            }
            visitingBrakes.Remove(brake); readyBrakes[brake] = factor; return factor;
        }
        private double BrakeDeceleration()
        {
            double force = 0, mass = 0;
            ConsistMassKg = ConnectedBrakeForce = AppliedBrakeForce = OpposingTractionForce = 0;
            readyBrakes.Clear(); visitingBrakes.Clear();
            var command = owner.brakeSystem;
            var brakeSet = command == null ? null : command.brakeset;
            BrakeDataStatus = "ready";
            if (brakeSet == null) { BrakeDataStatus = "missing brake set"; return 0; }
            if (!command.trainBrakeCutout) { BrakeDataStatus = "train brake valve cut out"; return 0; }
            double commandFactor = command.trainBrakeCurve == null ? 1 : command.trainBrakeCurve.Evaluate(1);
            if (!ProtectionPolicy.Finite(commandFactor)) { BrakeDataStatus = "invalid brake command curve"; return 0; }
            double targetPipe = command.selfLappingController ? 6 - 1.5 * Math.Max(0, Math.Min(1, commandFactor)) : 4.5;
            foreach (var car in cars)
            {
                if (car == null || car.massController == null || car.Bogies == null || car.Bogies.Length == 0)
                { BrakeDataStatus = "invalid native vehicle mass"; return 0; }
                double carMass = car.massController.TotalMass;
                if (!ProtectionPolicy.Finite(carMass) || carMass <= 0) { BrakeDataStatus = "invalid native vehicle mass"; return 0; }
                mass += carMass;
                double capacity = 0;
                foreach (var bogie in car.Bogies)
                {
                    if (bogie == null || bogie.rb == null || !ProtectionPolicy.Finite(bogie.rb.mass) || bogie.rb.mass <= 0 ||
                        !ProtectionPolicy.Finite(bogie.maxBrakingForcePerKg) || bogie.maxBrakingForcePerKg < 0)
                    { BrakeDataStatus = "invalid native bogie force"; return 0; }
                    capacity += bogie.maxBrakingForcePerKg * bogie.rb.mass;
                }
                var b = car.brakeSystem;
                if (b == null) continue; // unbraked cars still contribute mass
                if (b.heatController == null || !ProtectionPolicy.Finite(b.heatController.overheatReductionFactor) || !ProtectionPolicy.Finite(b.brakingFactor))
                { BrakeDataStatus = "invalid native brake factor"; return 0; }
                double heat = Math.Max(0, Math.Min(1, b.heatController.overheatReductionFactor));
                double applied = Math.Max(0, b.brakingFactor); // native factor already includes heat
                double service = b.brakeset == brakeSet ? ServiceFactor(b, targetPipe) * heat : 0;
                if (!ProtectionPolicy.Finite(service)) { BrakeDataStatus = "invalid native brake pressure"; return 0; }
                var adhesion = car.adhesionController;
                if (adhesion != null)
                {
                    if (!ProtectionPolicy.Finite(adhesion.wheelSlide)) { BrakeDataStatus = "invalid native adhesion"; return 0; }
                    double slide = Math.Max(0, Math.Min(1, adhesion.wheelSlide));
                    applied = applied * (1 - slide) + .4 * slide;
                    // Native sliding blends towards 40% of capacity. Do not
                    // credit an increase for an unavailable/overheated brake.
                    service = Math.Min(service, service * (1 - slide) + .4 * slide);
                    if (adhesion.wheelslipController.IsSome(out var slip) && slip.wheelslip > 0) service = applied = 0;
                }
                AppliedBrakeForce += capacity * applied;
                force += capacity * service;
            }
            if (mass <= 0 || !ProtectionPolicy.Finite(mass) || !ProtectionPolicy.Finite(force))
            { BrakeDataStatus = "invalid mass/brake force"; return 0; }
            ConsistMassKg = mass; ConnectedBrakeForce = force;
            if (force <= 0) BrakeDataStatus = "no charged connected brakes";
            // Native Newtons / total kilograms. No planning fraction or cap.
            return force / mass;
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
            if (!enableCurve) { ForgetCurve(); ConsistMassKg=ConnectedBrakeForce=AppliedBrakeForce=OpposingTractionForce=0; massPoints.Clear(); }
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
            if (ConsistMassKg <= 0 || ConnectedBrakeForce <= 0)
            { Status = "Curve unavailable: brake capability; whole-consist limit"; return; }
            bool gradeKnown = PrepareGrades(ahead, measured, lastReduction);
            OpposingTractionForce = gradeKnown ? ReadTraction() : double.NaN;
            levelDeceleration -= OpposingTractionForce / ConsistMassKg;
            double deceleration = gradeKnown ? EffectiveDeceleration(levelDeceleration, 0) : double.NaN;
            for (int i = 0; i < limits.Count; i++)
            {
                var boundary = limits[i];
                if (SpeedEnvelope.IsReduction(limits, i, wholeLimit))
                    boundary.Deceleration = gradeKnown ? EffectiveDeceleration(levelDeceleration, boundary.Distance) : double.NaN;
                limits[i] = boundary;
            }
            ClubUDeceleration = deceleration;
            SpeedEnvelope.FirstReduction(wholeLimit, limits, out var target, out var targetDistance, out var targetDeceleration);
            foreach (var boundary in limits)
                if (boundary.Distance == targetDistance && boundary.Limit == target) { CurveTarget = boundary; break; }
            ClubUTargetLimit = target; ClubUTargetDistance = targetDistance;
            if (targetDeceleration.HasValue) ClubUDeceleration = targetDeceleration.Value;
            if (!gradeKnown || (ConsistMassKg > 0 && !ProtectionPolicy.Finite(ClubUDeceleration))) BrakeDataStatus = "track grade unavailable";
            if (BrakeDataStatus == "ready" && ClubUDeceleration <= 0) BrakeDataStatus = "insufficient service deceleration";
            // The driver envelope assumes service braking is established.
            // The automatic-brake warning is not an air-brake response delay.
            const double planningResponseSeconds = 0;
            if (target.HasValue)
                ClubURequiredBrakingDistance = SpeedEnvelope.BrakingDistance(ClubUCurrentSpeedKmh, target.Value,
                    targetDeceleration ?? deceleration, planningResponseSeconds);
            var last = ahead[ahead.Count - 1];
            // Use the same single planning interval for the exact curve. No
            // additional warning-state grace period or arbitrary margin is
            // introduced here.
            bool complete; int controllingBoundary;
            var curve = SpeedEnvelope.Calculate(wholeLimit, limits, deceleration,
                planningResponseSeconds, last.DistanceAt(last.End), ClubUCurrentSpeedKmh, out complete, out controllingBoundary);
            if (!curve.HasValue) { Status = "Curve unavailable: route/brake data; whole-consist limit"; return; }
            if (!complete && lastCurve.HasValue && lastCurve.Value < curve.Value) curve = lastCurve;
            else if (controllingBoundary >= 0) { lastCurve = curve; heldTarget = limits[controllingBoundary]; }
            else ForgetCurve();
            CurveAvailable = true; Status = complete ? "Route curve active" : "Known route curve; further route unavailable";
            Permitted = curve;
        }
    }
}
