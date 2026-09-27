using System;
using System.Collections.Generic;
using DV.Simulation.Cars;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    public interface ISignalReading
    {
        RailTrack Track { get; }
        double Span { get; }
        int Direction { get; }
        string Name { get; }
        bool Valid { get; }
        bool ProhibitsPassing { get; }
        SignalDisplay ReadDisplay();
    }
    public interface ISignalSource
    {
        bool Ready { get; }
        int Generation { get; }
        void Refresh();
        IEnumerable<ISignalReading> OnTrack(RailTrack track, int travelDirection);
    }

    public interface ICabSignalSource
    {
        void ReadCab(TrainCar car, TrackPosition<RailTrack> position, ISignalReading next,
            ISignalReading forbiddenEntry, SignalDisplay display);
        bool IsBlockBoundary(ISignalReading signal);
    }

    public interface ISignalPassageSource
    {
        ISignalReading CapturePassage(ISignalReading signal);
    }
    public enum CabSignalMode { Normal, Shunting }
    public interface IModeCabSignalSource
    {
        IEnumerable<ISignalReading> OnTrack(RailTrack track, int direction, CabSignalMode mode);
        void ReadCab(TrainCar car, TrackPosition<RailTrack> position, ISignalReading next,
            ISignalReading forbiddenEntry, SignalDisplay display, CabSignalMode mode);
    }

    public sealed class LocoService : MonoBehaviour
    {
        private static readonly HashSet<LocoService> Services = new HashSet<LocoService>();
        public static ISignalSource Signals;
        public static LocoService Get(TrainCar car)
        {
            var result = car.GetComponent<LocoService>();
            if (result == null) result = car.gameObject.AddComponent<LocoService>();
            return result;
        }

        public static void SampleBeforeAspectChange()
        {
            foreach (var service in Services) if (service != null && service.enabled) service.SamplePassage();
        }

        public static IEnumerable<LocoService> All { get { return Services; } }
        private readonly List<SafetyGadget> gadgets = new List<SafetyGadget>();
        private readonly List<RouteLeg<RailTrack>> aheadBuffer = new List<RouteLeg<RailTrack>>();
        private readonly HashSet<RailTrack> aheadVisited = new HashSet<RailTrack>();
        private readonly List<RouteLeg<RailTrack>> sweptBuffer = new List<RouteLeg<RailTrack>>();
        private readonly HashSet<RailTrack> sweptVisited = new HashSet<RailTrack>();
        private struct PassedSignal { internal ISignalReading Signal; internal double Distance; internal int Order; }
        private readonly List<PassedSignal> passedSignals = new List<PassedSignal>();
        private static readonly Comparison<PassedSignal> PassageOrder = (a, b) =>
        { int distance = a.Distance.CompareTo(b.Distance); return distance != 0 ? distance : a.Order.CompareTo(b.Order); };
        private TrainCar car;
        private bool history, sampling, deadlineRead;
        private readonly ConfirmedDirection confirmedDirection = new ConfirmedDirection();
        private bool curveMode, speedProtection = true, pendingPassage, turnHistory;
        private TrackPosition<RailTrack> turnPosition;
        private Vector3 turnAbsolute;
        private float turnTime;
        private ISignalReading passedStop, retainedRed;
        public int ActiveDirection { get { return direction; } }
        public bool CurveMode { get { return curveMode; } }
        public bool SpeedProtection { get { return speedProtection; } }
        private CabSignalMode signalMode;
        private bool includeOldSignals;
        public CabSignalMode SignalMode { get { return signalMode; } }
        private TrackPosition<RailTrack> previous;
        private int direction = 1, signalGeneration = -1;
        private float lastTime, lastSpeed, queryTime;
        private float displaySampleTime = float.NaN;
        private Vector3 absolutePosition;
        private readonly SignalWarningGate signalWarnings = new SignalWarningGate();
        private readonly SpeedWarningGate speedWarnings = new SpeedWarningGate();
        private readonly CurveWarningGate curveWarnings = new CurveWarningGate();
        private readonly EmergencyBrakeLock brakeLock = new EmergencyBrakeLock();
        private readonly AutomaticBrakeWarning brakeWarning = new AutomaticBrakeWarning();
        private readonly ManualBrakeProgress manualBrake = new ManualBrakeProgress();
        private readonly ConsistSpeedProfile speedProfile = new ConsistSpeedProfile();
        private float manualSampleAt;
        public bool BrakeWarningActive { get { return brakeWarning.Warning; } }
        internal bool BrakeWarningLampOn { get { return brakeWarning.WarningLampOn(Time.time); } }
        public ISignalReading NextSignal { get; private set; }
        public readonly SignalDisplay CabDisplay = new SignalDisplay();
        private ISignalReading forbiddenEntry;
        public int? CurrentSpeed { get; private set; }
        public int? NextSpeed { get; private set; }
        public double? NextSpeedDistance { get; private set; }
        public int? DisplayDistance { get; private set; }
        public int DistanceDisplayStep { get { return distanceReadout.Step; } }
        private readonly DistanceReadout distanceReadout = new DistanceReadout();
        private void ClearDistance() { NextSpeedDistance = null; DisplayDistance = null; distanceReadout.Reset(); }
        public int? DisplaySpeed { get; private set; }
        public double? PermittedSpeed { get; private set; }
        public bool CurveAvailable { get { return speedProfile.CurveAvailable; } }
        public string SpeedProfileStatus { get { return speedProfile.Status; } }
        public double ClubUCurrentSpeedKmh { get { return speedProfile.ClubUCurrentSpeedKmh; } }
        public int? ClubUTargetLimit { get { return speedProfile.ClubUTargetLimit; } }
        public double ClubUTargetDistance { get { return speedProfile.ClubUTargetDistance; } }
        public double ClubUDeceleration { get { return speedProfile.ClubUDeceleration; } }
        public double ClubURequiredBrakingDistance { get { return speedProfile.ClubURequiredBrakingDistance; } }
        public string BrakeDataStatus { get { return speedProfile.BrakeDataStatus; } }
        public string LastPassage { get; private set; }
        public TrainCar Car { get { return car; } }
        public int DeviceCount { get { return gadgets.Count; } }

        private void Awake() { car = GetComponent<TrainCar>(); if (car != null) car.OnRerailed += ResetHistory; Services.Add(this); enabled = false; }
        private void OnDestroy() { ClearDistance(); if (car != null) car.OnRerailed -= ResetHistory; brakeWarning.Reset(); brakeLock.Dispose(); speedProfile.Dispose(); curveWarnings.Reset(); Services.Remove(this); }
        private void OnDisable() { ClearDistance(); history = turnHistory = pendingPassage = false; passedStop = retainedRed = forbiddenEntry = null; brakeWarning.Reset(); brakeLock.Dispose(); speedProfile.Dispose(); curveWarnings.Reset(); manualBrake.Reset(); }
        public void Add(SafetyGadget gadget) { if (!gadgets.Contains(gadget)) gadgets.Add(gadget); enabled = gadgets.Count > 0; DeviceStateChanged(); }
        public void Remove(SafetyGadget gadget) { gadgets.Remove(gadget); MaintainBrake(); enabled = gadgets.Count > 0; DeviceStateChanged(); }
        public void ResetHistory() { confirmedDirection.Reset(); turnHistory = pendingPassage = false; passedStop = retainedRed = null; brakeWarning.Reset(); manualBrake.Reset(); speedProfile.Reset(); ResetReadings(); }
        internal void RestoreDirection(int saved)
        {
            if (saved != -1 && saved != 1) return;
            confirmedDirection.Restore(saved); direction = saved;
            history = turnHistory = false; queryTime = 0;
        }
        private void ResetReadings() { ClearDistance(); history = false; forbiddenEntry = null; NextSignal = null; DisplaySpeed = CurrentSpeed = NextSpeed = null; PermittedSpeed = null; queryTime = 0; ClearCabReading(); }
        private void ClearCabReading()
        {
            // A retained RED is useful across a transient unreadable signal,
            // but it must not survive loss of the entire signal provider. In
            // that state ALS is explicitly WHITE and non-protective; any
            // already latched emergency hold remains owned by BrakeIncident.
            var red = Signals != null && Signals.Ready ? passedStop ?? retainedRed : null;
            CabSignalLogic.Set(CabDisplay, red != null ? CabAspect.Red : CabAspect.White, red != null ? red.Name : null);
            CabDisplay.ProtectionStop = false;
        }
        internal void DeviceStateChanged()
        {
            // Preserve the speed deadline and RED acknowledgement across device
            // changes. A temporary missing reading must not rearm the same RED.
            queryTime = 0;
            // Brake control and duplicate devices must not erase a valid entry
            // authorization or mutate the independent ALS passage state.
            if (!Has(DeviceKind.Signal)) { history = false; forbiddenEntry = null; CabSignalLogic.Set(CabDisplay, CabAspect.White); }
            if (!Has(DeviceKind.Speed)) { ClearDistance(); DisplaySpeed = CurrentSpeed = NextSpeed = null; PermittedSpeed = null; speedProfile.Dispose(); }
            if (!Has(DeviceKind.Brake)) brakeWarning.CancelWarning();
        }
        public void Acknowledge(SafetyGadget gadget)
        {
            if (!gadgets.Contains(gadget) || gadget.kind != DeviceKind.Brake) return;
            foreach (var device in gadgets) if (device != null && device.kind == DeviceKind.Brake) device.Incident.Acknowledge();
            brakeWarning.Acknowledge(BrakeCause.None); pendingPassage = false;
            MaintainBrake();
        }

        private SafetyGadget ActiveDevice(DeviceKind kind)
        {
            foreach (var gadget in gadgets) if (gadget != null && gadget.kind == kind && gadget.Operational &&
                (kind != DeviceKind.Brake || gadget.ProtectionEnabled)) return gadget;
            return null;
        }
        private bool Has(DeviceKind kind) { return ActiveDevice(kind) != null; }
        internal void GadgetModeChanged()
        {
            RefreshSignalMode();
            var selected = ModeSelector(DeviceKind.Speed);
            bool curve = selected != null && selected.CurveMode;
            bool supervise = false;
            foreach (var gadget in gadgets)
            {
                if (gadget == null || gadget.kind != DeviceKind.Brake) continue;
                if (!gadget.SpeedProtection) gadget.Incident.Remove(BrakeHoldCause.Speed);
                if (gadget.Operational && gadget.ProtectionEnabled && gadget.SpeedProtection) supervise = true;
            }
            if (curveMode != curve) { curveMode = curve; queryTime = 0; }
            if (speedProtection != supervise)
            {
                speedProtection = supervise;
                if (!supervise) { brakeWarning.CancelWarning(); manualBrake.Reset(); deadlineRead = false; }
            }
            MaintainBrake();
        }
        private SafetyGadget ModeSelector(DeviceKind kind)
        {
            SafetyGadget selected = null;
            foreach (var gadget in gadgets)
                if (gadget != null && gadget.kind == kind && gadget.Operational && gadget.HasModeSwitch &&
                    (selected == null || gadget.UID < selected.UID)) selected = gadget;
            return selected;
        }
        internal void SignalSelectionChanged() { RefreshSignalMode(); }
        private void InvalidateSignalSelection()
        {
            history = turnHistory = false; forbiddenEntry = passedStop = retainedRed = null; NextSignal = null; queryTime = 0;
            CabSignalLogic.Set(CabDisplay, CabAspect.White); CabDisplay.ProtectionStop = null;
            // Keep emergency holds, RED acknowledgement, speed profile and deadlines.
        }
        private void RefreshSignalMode()
        {
            // Duplicate cab repeaters share this locomotive's reading. Choose the
            // lowest native UID with a wired mode selector, independent of load order.
            var selected = ModeSelector(DeviceKind.Signal);
            var mode = selected != null && selected.ShuntingMode ? CabSignalMode.Shunting : CabSignalMode.Normal;
            bool changed = mode != signalMode || (mode == CabSignalMode.Normal && includeOldSignals != Main.IncludeOldSignals);
            signalMode = mode; includeOldSignals = Main.IncludeOldSignals;
            if (changed) InvalidateSignalSelection();
        }
        private IEnumerable<ISignalReading> SignalsOnTrack(RailTrack track, int travelDirection)
        {
            var modes = Signals as IModeCabSignalSource;
            return modes != null ? modes.OnTrack(track, travelDirection, signalMode) : Signals.OnTrack(track, travelDirection);
        }

        internal void PressBrakeButton()
        {
            foreach (var gadget in gadgets)
                if (gadget != null && gadget.kind == DeviceKind.Brake && gadget.Operational)
                { if (gadget.PressButton()) return; }
        }

        private void LateUpdate()
        {
            if (car == null || gadgets.Count == 0 || UnloadWatcher.isUnloading) { brakeLock.Dispose(); speedProfile.Dispose(); ResetHistory(); return; }
            try
            {
                if (Signals != null)
                    try { Signals.Refresh(); }
                    catch (Exception ex) { Main.ErrorOnce("signals-refresh", ex); }
                GadgetModeChanged();
                SamplePassage();
                MaintainBrake();
                if (Time.unscaledTime >= queryTime)
                {
                    queryTime = Time.unscaledTime + .2f;
                    QueryDisplays();
                }
                UpdateAutomaticBrake();
            }
            catch (Exception ex) { brakeWarning.CancelWarning(); ResetReadings(); Main.ErrorOnce("loco-service", ex); }
        }

        private BaseControlsOverrider Controls
        {
            get { foreach (var g in gadgets) if (g != null && g.Controls != null) return g.Controls; return null; }
        }

        private bool Position(out TrackPosition<RailTrack> position, out int travelDirection, out float speed)
        {
            position = default(TrackPosition<RailTrack>); travelDirection = direction; speed = 0;
            if (car == null || car.rb == null || !car.IsLoco) return false;
            speed = car.GetForwardSpeed();
            var controls = Controls;
            double? reverser = controls != null && controls.Reverser != null ? (double?)controls.Reverser.Value : null;
            travelDirection = confirmedDirection.Observe(speed, reverser, Time.time);
            return NativeRoute.LeadingPosition(car, travelDirection, out position);
        }

        private void SamplePassage()
        {
            if (sampling || car == null || gadgets.Count == 0 || UnloadWatcher.isUnloading) return;
            sampling = true;
            try
            {
                RefreshSignalMode();
                TrackPosition<RailTrack> now; int nextDirection; float speed;
                bool signalActive = Has(DeviceKind.Signal);
                bool ready = Signals != null && Signals.Ready;
                int generation = Signals == null ? -1 : Signals.Generation;
                if (!Position(out now, out nextDirection, out speed)) { brakeWarning.CancelWarning(); ResetReadings(); return; }
                float t = Time.time;
                Vector3 absolute = car.transform.position - WorldMover.currentMove;
                if (!signalActive || !ready || generation != signalGeneration || nextDirection != direction)
                {
                    if (history || forbiddenEntry != null || generation != signalGeneration || nextDirection != direction) queryTime = 0;
                    if (!signalActive || !ready || generation != signalGeneration) turnHistory = false;
                    history = false; forbiddenEntry = null;
                    if (nextDirection != direction) passedStop = retainedRed = null;
                }
                if (nextDirection != direction && turnHistory && turnPosition.Direction * turnPosition.Direction == 1)
                {
                    previous = turnPosition; lastTime = turnTime; lastSpeed = 0; absolutePosition = turnAbsolute;
                    history = signalActive && ready; turnHistory = false;
                }
                else if (confirmedDirection.Pending != 0 && confirmedDirection.Pending != nextDirection && Math.Abs(speed) <= ConfirmedDirection.StopSpeed)
                {
                    turnHistory = NativeRoute.LeadingPosition(car, confirmedDirection.Pending, out turnPosition);
                    turnTime = t; turnAbsolute = absolute;
                }
                else if (confirmedDirection.Pending == 0 || confirmedDirection.Pending == nextDirection) turnHistory = false;
                float elapsed = t - lastTime;
                // Time, velocity and independent body displacement reject loads/rerails/teleports.
                if (history && elapsed >= 0 && elapsed <= 1.0f)
                {
                    double maximum = (Math.Max(Math.Abs(speed), Math.Abs(lastSpeed)) + 2) * elapsed + .20;
                    double physical = Vector3.Distance(absolute, absolutePosition);
                    if (physical <= maximum && (speed * nextDirection > 0 ||
                        (Math.Abs(speed) <= ConfirmedDirection.StopSpeed && lastSpeed * nextDirection > 0)))
                    {
                        double travelled;
                        bool onRoute = RouteLogic.Swept(NativeRoute.Instance, previous, now, maximum, sweptBuffer, sweptVisited, out travelled);
                        if (onRoute && travelled > .00001)
                        {
                            foreach (var leg in sweptBuffer)
                            {
                                passedSignals.Clear();
                                foreach (var candidate in SignalsOnTrack(leg.Track, leg.Direction))
                                    if (candidate.Valid && RouteLogic.ActuallyPassed(leg, candidate.Span, travelled))
                                        passedSignals.Add(new PassedSignal { Signal = candidate, Distance = leg.DistanceAt(candidate.Span), Order = passedSignals.Count });
                                if (passedSignals.Count > 1) passedSignals.Sort(PassageOrder);
                                foreach (var passed in passedSignals)
                                {
                                    var signal = passed.Signal;
                                    if (signal.Valid && RouteLogic.ActuallyPassed(leg, signal.Span, travelled))
                                    {
                                        var cabSource = Signals as ICabSignalSource;
                                        if (cabSource != null && cabSource.IsBlockBoundary(signal))
                                        {
                                            var capture = Signals as ISignalPassageSource;
                                            forbiddenEntry = capture != null ? capture.CapturePassage(signal) : signal.ProhibitsPassing ? signal : null;
                                        }
                                        if (signal.ProhibitsPassing)
                                        {
                                            LastPassage = signal.Name; pendingPassage = true; passedStop = signal;
                                        }
                                        queryTime = 0;
                                    }
                                }
                            }
                        }
                        else if (!onRoute) forbiddenEntry = null;
                    }
                    else if (physical > maximum) forbiddenEntry = null;
                }
                previous = now; direction = nextDirection; lastSpeed = speed; lastTime = t;
                absolutePosition = absolute; signalGeneration = generation;
                if (elapsed > 1.0f || elapsed < 0) forbiddenEntry = null;
                history = signalActive && ready;
            }
            catch (Exception ex) { history = false; Main.ErrorOnce("passage", ex); }
            finally { passedSignals.Clear(); sampling = false; }
        }

        private void MaintainBrake()
        {
            bool holding = false;
            foreach (var g in gadgets)
                if (g != null && g.kind == DeviceKind.Brake && g.Tripped) { holding = true; break; }
            if (!holding || car == null || UnloadWatcher.isUnloading) { brakeLock.Dispose(); return; }
            var controls = Controls;
            if (controls == null || controls.Brake == null) return;
            brakeLock.Hold(car, controls.Brake);
        }

        private void UpdateAutomaticBrake()
        {
            bool speedHolding = false, anyHolding = false;
            foreach (var device in gadgets)
                if (device != null && device.kind == DeviceKind.Brake)
                {
                    anyHolding |= device.Tripped;
                    speedHolding |= (device.Incident.Causes & BrakeHoldCause.Speed) != 0;
                }
            if (car == null || car.rb == null || UnloadWatcher.isUnloading)
            { brakeWarning.CancelWarning(); pendingPassage = false; return; }
            // A queued measured crossing is consumed once, independent of the
            // displayed RED and of any speed latch already holding the brake.
            if (pendingPassage)
            {
                pendingPassage = false;
                bool applied = false;
                foreach (var device in gadgets)
                    if (device != null && device.kind == DeviceKind.Brake && device.Operational && device.ProtectionEnabled)
                    { device.Incident.Trip(BrakeHoldCause.SignalPassage); applied = true; }
                if (applied) { Main.Log(Texts.Pick("Проезд запрещающего сигнала: ", "Prohibited signal passed: ") + car.ID); MaintainBrake(); }
            }
            if (!speedProtection || !Has(DeviceKind.Speed))
            { brakeWarning.CancelWarning(); manualBrake.Reset(); return; }
            if (!brakeWarning.WarningDue(Time.time)) deadlineRead = false;
            else if (!deadlineRead)
            {
                if (displaySampleTime != Time.unscaledTime) QueryDisplays();
                deadlineRead = true;
            }
            var policy = Main.Protection;
            double speedKmh = Math.Abs(car.GetForwardSpeed()) * 3.6f;
            double? permitted = PermittedSpeed;
            if (Time.time >= manualSampleAt || Time.time < manualSampleAt - 1)
            {
                manualSampleAt = Time.time + .2f;
                var controls = Controls;
                bool commanded = controls != null && ((controls.Brake != null && controls.Brake.Value > .05f) ||
                    (controls.DynamicBrake != null && controls.DynamicBrake.Value > .05f));
                manualBrake.Observe(Time.time, speedKmh, permitted.GetValueOrDefault(double.NaN) + policy.ToleranceKmh, commanded, anyHolding);
            }
            bool trip = brakeWarning.ObserveSpeed(false, false, permitted, speedKmh, Time.time, speedHolding,
                true, manualBrake.Effective, policy);
            if (brakeWarning.SoundRequested) PlayWarning(DeviceKind.Brake, WarningKind.Caution);
            if (!trip) return;
            foreach (var device in gadgets)
                if (device != null && device.kind == DeviceKind.Brake && device.Operational && device.ProtectionEnabled && device.SpeedProtection)
                    device.Incident.Trip(BrakeHoldCause.Speed);
            Main.Log(Texts.Pick("Автоматическое торможение на ", "Automatic brake applied on ") + car.ID + ": " +
                (brakeWarning.Critical ? Texts.Pick("критическое превышение, немедленно", "critical overspeed, immediate") :
                Texts.Pick("превышение после предупреждения", "overspeed after warning")));
            brakeWarning.CancelWarning(); MaintainBrake();
        }

        private void QueryDisplays()
        {
            displaySampleTime = Time.unscaledTime;
            ClearCabReading();
            bool showSignal = Has(DeviceKind.Signal), showSpeed = Has(DeviceKind.Speed);
            if (!showSignal && !showSpeed) { ClearDistance(); NextSignal = null; DisplaySpeed = CurrentSpeed = NextSpeed = null; PermittedSpeed = null; return; }
            TrackPosition<RailTrack> now; int dir; float speed;
            if (!Position(out now, out dir, out speed)) { ClearDistance(); NextSignal = null; DisplaySpeed = CurrentSpeed = NextSpeed = null; PermittedSpeed = null; return; }
            RouteLogic.Walk(NativeRoute.Instance, now, 20000, aheadBuffer, aheadVisited);
            var ahead = aheadBuffer;
            try { QuerySignal(ahead, now, showSignal); }
            catch (Exception ex)
            {
                NextSignal = null; 
                ClearCabReading();
                Main.ErrorOnce("cab-reading", ex);
            }
            if (!showSpeed) { ClearDistance(); DisplaySpeed = CurrentSpeed = NextSpeed = null; PermittedSpeed = null; return; }
            try { QuerySpeed(ahead, dir, speed); }
            catch (Exception ex)
            {
                ClearDistance(); DisplaySpeed = CurrentSpeed = NextSpeed = null; PermittedSpeed = null;
                speedProfile.Reset(); Main.ErrorOnce("speed-reading", ex);
            }
        }

        private void QuerySignal(List<RouteLeg<RailTrack>> ahead, TrackPosition<RailTrack> now, bool showSignal)
        {
            NextSignal = null;
            if (showSignal && (Signals == null || !Signals.Ready))
            {
                // A provider outage is distinct from a temporarily unreadable
                // signal. Drop provider-owned RED/entry state so ALS stays
                // WHITE and cannot resurrect stale protection when the source
                // later returns.
                passedStop = retainedRed = forbiddenEntry = null;
                return;
            }
            if (showSignal && Signals != null && Signals.Ready)
            {
                double nearest = double.PositiveInfinity;
                foreach (var leg in ahead)
                {
                    // Walk legs are in increasing travelled distance. Keep the
                    // Contains tolerance at shared endpoints, then stop before
                    // querying unrelated tracks beyond the chosen signal.
                    if (leg.Distance > nearest + .0001) break;
                    foreach (var signal in SignalsOnTrack(leg.Track, leg.Direction))
                        if (signal.Valid && leg.Contains(signal.Span))
                        {
                            double distance = leg.DistanceAt(signal.Span);
                            if (distance < 0 || distance >= nearest) continue;
                            nearest = distance; NextSignal = signal;
                        }
                }
            }
            var cab = Signals as ICabSignalSource;
            if (showSignal && cab != null && Signals.Ready)
            {
                var modes = Signals as IModeCabSignalSource;
                if (modes != null) modes.ReadCab(car, now, NextSignal, forbiddenEntry, CabDisplay, signalMode);
                else cab.ReadCab(car, now, NextSignal, forbiddenEntry, CabDisplay);
            }
            if (showSignal)
            {
                // Losing the previously observed RED's controller/placement is
                // not a clear from that source. A farther readable head cannot
                // release it while the original reading is unavailable.
                if (NextSignal != null && CabDisplay.Known && (retainedRed == null || retainedRed.Valid))
                    retainedRed = CabDisplay.Red > 0 && !CabDisplay.SplitYellowRed ? NextSignal : null;
                var previousRed = passedStop ?? (!CabDisplay.Known || (retainedRed != null && !retainedRed.Valid) ? retainedRed : null);
                if (previousRed != null)
                {
                    // A native clear ends retained RED; temporary data loss does not.
                    var live = previousRed.ReadDisplay();
                    if (live.Known && (live.Red <= 0 || live.SplitYellowRed)) passedStop = retainedRed = null;
                    else CabSignalLogic.Set(CabDisplay, CabAspect.Red, previousRed.Name);
                }
                // Display state never substitutes for a measured crossing.
                CabDisplay.ProtectionStop = false;
            }
            WarningKind warning = signalWarnings.Observe(CabDisplay.Known ? CabDisplay.Key : null, CabDisplay.Warning, Time.time);
            if (showSignal && warning != WarningKind.None) PlayWarning(DeviceKind.Signal, warning);
        }

        private void QuerySpeed(List<RouteLeg<RailTrack>> ahead, int dir, float speed)
        {
            int? current, next;
            SpeedSigns.Query(ahead, out current, out next);
            // A prediction failure cannot erase ALS or the independent native
            // speed reading. Keep a known stricter cap until fresh data returns.
            try { speedProfile.Query(car, dir, current, curveMode, Main.Protection.WarningSeconds, Math.Abs(speed) * 3.6f, ahead, Main.DisplayStep); }
            catch (Exception ex)
            {
                // The profile publishes its fallback after checking direction /
                // trainset identity. Do not carry a previous cab snapshot across
                // a reversal or coupling just because this query failed.
                ClearDistance(); CurrentSpeed = speedProfile.TrackLimit ?? current; NextSpeed = next;
                PermittedSpeed = speedProfile.Permitted ?? current;
                DisplaySpeed = speedProfile.Display ?? current;
                Main.ErrorOnce("speed-curve", ex); return;
            }
            CurrentSpeed = speedProfile.TrackLimit; DisplaySpeed = speedProfile.Display;
            PermittedSpeed = speedProfile.Permitted; NextSpeed = speedProfile.WholeConsistAvailable ? speedProfile.NextLimit : next;
            // Publish from one target record. Never mix a locomotive fallback
            // speed with a whole-consist target distance. Invalid data clears UI.
            var nextTarget = speedProfile.NextTarget;
            if (speedProfile.WholeConsistAvailable && nextTarget.HasValue && nextTarget.Value.Limit == NextSpeed)
            {
                var target = nextTarget.Value;
                NextSpeedDistance = target.Distance;
                DisplayDistance = distanceReadout.Observe(target.Track, target.Span, target.Direction, target.Limit,
                    speedProfile.NextTargetAtTail, target.Distance, Math.Abs(speed) * 3.6);
            }
            else ClearDistance();
            if (curveMode)
            {
                var target = speedProfile.CurveTarget.GetValueOrDefault();
                var head = speedProfile.LeadingEnd;
                bool active = speedProfile.CurveAvailable && PermittedSpeed.HasValue && CurrentSpeed.HasValue &&
                    PermittedSpeed.Value < CurrentSpeed.Value - .000001;
                if (curveWarnings.Observe(speedProfile.WholeConsistAvailable, active, target.Track, target.Span,
                    target.Direction, target.Limit, head.Track, head.Span, head.Direction)) PlayWarning(DeviceKind.Speed, WarningKind.Caution);
            }
            // With prediction disabled retain the existing physical-sign warning.
            else if (speedWarnings.Observe(CurrentSpeed, NextSpeed, Time.time)) PlayWarning(DeviceKind.Speed, WarningKind.Caution);
        }

        private void PlayWarning(DeviceKind kind, WarningKind warning)
        {
            // A locomotive with duplicate displays produces one notification.
            var device = ActiveDevice(kind);
            if (device != null) device.PlayWarning(warning);
        }
    }
}
