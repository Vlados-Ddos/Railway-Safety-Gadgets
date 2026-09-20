using System;
using System.Collections.Generic;
using System.Linq;
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
        private TrainCar car;
        private bool history, sampling, cabReadingValid;
        private TrackPosition<RailTrack> previous;
        private int direction = 1, signalGeneration = -1;
        private float lastTime, lastSpeed, queryTime;
        private Vector3 absolutePosition;
        private readonly SignalWarningGate signalWarnings = new SignalWarningGate();
        private readonly SpeedWarningGate speedWarnings = new SpeedWarningGate();
        private readonly EmergencyBrakeLock brakeLock = new EmergencyBrakeLock();
        private readonly AutomaticBrakeWarning brakeWarning = new AutomaticBrakeWarning();
        public bool BrakeWarningActive { get { return brakeWarning.Warning; } }
        internal bool BrakeWarningLampOn { get { return brakeWarning.WarningLampOn(Time.time); } }
        public ISignalReading NextSignal { get; private set; }
        public readonly SignalDisplay CabDisplay = new SignalDisplay();
        private ISignalReading forbiddenEntry;
        public int? CurrentSpeed { get; private set; }
        public int? NextSpeed { get; private set; }
        public string LastPassage { get; private set; }
        public TrainCar Car { get { return car; } }
        public int DeviceCount { get { return gadgets.Count; } }

        private void Awake() { car = GetComponent<TrainCar>(); Services.Add(this); enabled = false; }
        private void OnDestroy() { brakeWarning.Reset(); brakeLock.Dispose(); Services.Remove(this); }
        private void OnDisable() { brakeWarning.Reset(); brakeLock.Dispose(); }
        public void Add(SafetyGadget gadget) { if (!gadgets.Contains(gadget)) gadgets.Add(gadget); enabled = gadgets.Count > 0; DeviceStateChanged(); }
        public void Remove(SafetyGadget gadget) { gadgets.Remove(gadget); MaintainBrake(); enabled = gadgets.Count > 0; DeviceStateChanged(); }
        public void ResetHistory() { brakeWarning.Reset(); ResetReadings(); }
        private void ResetReadings() { history = false; cabReadingValid = false; forbiddenEntry = null; NextSignal = null; CurrentSpeed = NextSpeed = null; queryTime = 0; CabSignalLogic.Set(CabDisplay, CabAspect.White); }
        internal void DeviceStateChanged()
        {
            // Preserve the speed deadline and RED acknowledgement across device
            // changes. A temporary missing reading must not rearm the same RED.
            ResetReadings();
            if (!Has(DeviceKind.Brake)) brakeWarning.CancelWarning();
        }
        public void Acknowledge(SafetyGadget gadget)
        {
            if (!gadgets.Contains(gadget) || gadget.kind != DeviceKind.Brake) return;
            foreach (var device in gadgets) if (device != null && device.kind == DeviceKind.Brake) device.Incident.Acknowledge();
            brakeWarning.Acknowledge(CurrentBrakeCauses());
            MaintainBrake();
        }

        private SafetyGadget ActiveDevice(DeviceKind kind)
        {
            foreach (var gadget in gadgets) if (gadget != null && gadget.kind == kind && gadget.Operational) return gadget;
            return null;
        }
        private bool Has(DeviceKind kind) { return ActiveDevice(kind) != null; }

        private void LateUpdate()
        {
            if (car == null || gadgets.Count == 0 || UnloadWatcher.isUnloading) { brakeLock.Dispose(); ResetHistory(); return; }
            try
            {
                if (Signals != null) Signals.Refresh();
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
            travelDirection = RouteLogic.ResolveDirection(speed, reverser, direction);
            return NativeRoute.LeadingPosition(car, travelDirection, out position);
        }

        private void SamplePassage()
        {
            if (sampling || car == null || gadgets.Count == 0 || UnloadWatcher.isUnloading) return;
            sampling = true;
            try
            {
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
                    history = false; forbiddenEntry = null;
                }
                float elapsed = t - lastTime;
                // Time, velocity and independent body displacement reject loads/rerails/teleports.
                if (history && elapsed >= 0 && elapsed <= 1.0f)
                {
                    double maximum = (Math.Max(Math.Abs(speed), Math.Abs(lastSpeed)) + 2) * elapsed + .20;
                    double physical = Vector3.Distance(absolute, absolutePosition);
                    if (physical <= maximum && (Math.Abs(speed) > .01 || Math.Abs(lastSpeed) > .01))
                    {
                        List<RouteLeg<RailTrack>> swept; double travelled;
                        bool onRoute = RouteLogic.Swept(NativeRoute.Instance, previous, now, maximum, out swept, out travelled);
                        if (onRoute && travelled > .00001)
                        {
                            foreach (var leg in swept)
                                foreach (var signal in Signals.OnTrack(leg.Track, leg.Direction).Where(s => s.Valid).OrderBy(s => leg.DistanceAt(s.Span)))
                                    if (signal.Valid && RouteLogic.ActuallyPassed(leg, signal.Span, travelled))
                                    {
                                        var cabSource = Signals as ICabSignalSource;
                                        if (cabSource != null && cabSource.IsBlockBoundary(signal))
                                            forbiddenEntry = signal.ProhibitsPassing ? signal : null;
                                        if (signal.ProhibitsPassing) LastPassage = signal.Name;
                                        queryTime = 0;
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
            finally { sampling = false; }
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
            bool holding = false;
            foreach (var device in gadgets)
                if (device != null && device.kind == DeviceKind.Brake && device.Tripped) { holding = true; break; }
            if ((!holding && !Has(DeviceKind.Brake)) || car == null || car.rb == null || UnloadWatcher.isUnloading)
            { brakeWarning.CancelWarning(); return; }
            // Recheck CURRENT at the speed deadline, before committing a trip.
            // RED from the actual cab display has no preliminary warning.
            if (brakeWarning.WarningDue(Time.time)) QueryDisplays();
            BrakeCause causes = CurrentBrakeCauses();
            if (!brakeWarning.Observe(causes, Time.time, holding, cabReadingValid && Has(DeviceKind.Signal))) return;
            BrakeCause trigger = brakeWarning.Causes;
            foreach (var device in gadgets)
                if (device != null && device.kind == DeviceKind.Brake && device.Operational) device.Incident.Trip();
            brakeWarning.CancelWarning();
            Main.Log(Texts.Pick("Автоматическое торможение на ", "Automatic brake applied on ") + car.ID + ": " +
                (trigger == BrakeCause.CabRed ? Texts.Pick("красный сигнал АЛС, немедленно (CabRed)", "red cab signal, immediate (CabRed)") :
                Texts.Pick("превышение скорости после 10 секунд предупреждения (Overspeed)", "overspeed after a 10-second warning (Overspeed)")));
            MaintainBrake();
        }

        private BrakeCause CurrentBrakeCauses()
        {
            return AutomaticBrakeWarning.Evaluate(Has(DeviceKind.Signal), CabDisplay,
                Main.UseSpeedLimiter, Has(DeviceKind.Speed), CurrentSpeed, car == null || car.rb == null ? float.NaN : Math.Abs(car.GetForwardSpeed()) * 3.6f);
        }

        private void QueryDisplays()
        {
            cabReadingValid = false;
            CabSignalLogic.Set(CabDisplay, CabAspect.White);
            bool showSignal = Has(DeviceKind.Signal), showSpeed = Has(DeviceKind.Speed);
            if (!showSignal && !showSpeed) { NextSignal = null; CurrentSpeed = NextSpeed = null; return; }
            TrackPosition<RailTrack> now; int dir; float speed;
            if (!Position(out now, out dir, out speed)) { NextSignal = null; CurrentSpeed = NextSpeed = null; return; }
            var ahead = RouteLogic.Walk(NativeRoute.Instance, now, 20000);
            NextSignal = null;
            if (showSignal && Signals != null && Signals.Ready)
            {
                double nearest = double.PositiveInfinity;
                foreach (var leg in ahead)
                    foreach (var signal in Signals.OnTrack(leg.Track, leg.Direction))
                        if (signal.Valid && leg.Contains(signal.Span))
                        {
                            double distance = leg.DistanceAt(signal.Span);
                            if (distance < 0 || distance >= nearest) continue;
                            nearest = distance; NextSignal = signal;
                        }
            }
            var cab = Signals as ICabSignalSource;
            if (showSignal && cab != null && Signals.Ready)
            {
                cab.ReadCab(car, now, NextSignal, forbiddenEntry, CabDisplay);
                // WHITE is a real displayed transition even though Known is
                // false (no transmission). A completed cab query can rearm RED.
                cabReadingValid = true;
            }
            WarningKind warning = signalWarnings.Observe(CabDisplay.Known ? CabDisplay.Key : null, CabDisplay.Warning, Time.time);
            if (showSignal && warning != WarningKind.None) PlayWarning(DeviceKind.Signal, warning);
            int? current, next;
            if (showSpeed) SpeedSigns.Query(ahead, out current, out next);
            else { CurrentSpeed = NextSpeed = null; return; }
            CurrentSpeed = current; NextSpeed = next;
            if (speedWarnings.Observe(current, next, Time.time)) PlayWarning(DeviceKind.Speed, WarningKind.Caution);
        }

        private void PlayWarning(DeviceKind kind, WarningKind warning)
        {
            // A locomotive with duplicate displays produces one notification.
            var device = ActiveDevice(kind);
            if (device != null) device.PlayWarning(warning);
        }
    }
}
