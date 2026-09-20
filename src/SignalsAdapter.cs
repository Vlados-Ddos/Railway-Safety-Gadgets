using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Signals.Common;
using Signals.Game;
using Signals.Game.Controllers;
using Signals.Game.Railway;
using UnityEngine;
using DVSignal = Signals.Game.Signal;

namespace RailwaySafetyGadgets.SignalsIntegration
{
    // Loaded only after DV Signals. The core/prefabs have no hard Signals reference.
    public sealed class SignalsAdapter : ISignalSource, ICabSignalSource
    {
        private readonly Dictionary<RailTrack, List<ISignalReading>> byTrack = new Dictionary<RailTrack, List<ISignalReading>>();
        private SignalManager manager;
        private int count = -1;
        private float refreshAt;
        private static readonly ISignalReading[] Empty = new ISignalReading[0];
        public int Generation { get; private set; }
        public bool Ready { get { return SignalManager.Running && manager != null && manager == SignalManager.Instance && count > 0; } }

        public SignalsAdapter()
        {
            var harmony = new Harmony("denis.railway-safety-gadgets.signals");
            harmony.Patch(AccessTools.Method(typeof(DVSignal), "ChangeAspect"), prefix: new HarmonyMethod(typeof(SignalsAdapter), nameof(BeforeAspect)));
            harmony.Patch(AccessTools.Method(typeof(DVSignal), "TurnOff"), prefix: new HarmonyMethod(typeof(SignalsAdapter), nameof(BeforeAspect)));
        }
        private static void BeforeAspect() { LocoService.SampleBeforeAspectChange(); }

        public void Refresh()
        {
            var current = SignalManager.Running ? SignalManager.Instance : null;
            if (current == null)
            {
                if (manager != null || count != -1) { Generation++; manager = null; count = -1; byTrack.Clear(); }
                return;
            }
            if (manager == current && count == current.AllControllers.Count && Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + 2;
            if (manager != current || count != current.AllControllers.Count) Generation++;
            manager = current; count = current.AllControllers.Count;
            byTrack.Clear();
            foreach (var controller in current.AllControllers)
            {
                if (controller == null || !controller.Exists || !controller.PlacementInfo.HasValue) continue;
                var track = controller.PlacementInfo.Value.Track;
                if (track == null) continue;
                List<ISignalReading> list;
                if (!byTrack.TryGetValue(track, out list)) byTrack[track] = list = new List<ISignalReading>();
                list.Add(new Reading(controller));
            }
        }
        public IEnumerable<ISignalReading> OnTrack(RailTrack track, int direction)
        {
            List<ISignalReading> list;
            if (!Ready || track == null || !byTrack.TryGetValue(track, out list)) return Empty;
            return Select(list, track, direction);
        }
        private static IEnumerable<ISignalReading> Select(List<ISignalReading> list, RailTrack track, int direction)
        {
            foreach (var reading in list)
                if (reading.Valid && reading.Track == track && reading.Direction == direction) yield return reading;
        }
        public bool IsBlockBoundary(ISignalReading signal) { return signal is Reading reading && reading.Boundary; }

        public void ReadCab(TrainCar car, TrackPosition<RailTrack> position, ISignalReading next,
            ISignalReading forbiddenEntry, SignalDisplay display)
        {
            CabSignalLogic.Set(display, CabAspect.White);
            if (!Ready) return;
            var manuallyOccupied = TrackChecker.GetAllOccupiedTracks();
            var entered = forbiddenEntry as Reading;
            // A prohibition may also mean an unset route/reservation. Do not
            // invent occupancy merely because a forbidden signal was passed.
            bool forbidden = entered != null && entered.Valid && entered.Contains(position) &&
                Occupied(entered.Signal, car, manuallyOccupied) == true;
            bool? currentOccupied = FindCurrentBlockOccupation(position, car, manuallyOccupied);
            // Native blocks can be briefly dirty while a junction/controller is
            // rebuilt. A dirty boundary is not evidence that the current block
            // is free, so keep walking the selected route for the next block
            // whose membership agrees with the measured locomotive position.
            if (!currentOccupied.HasValue && !forbidden) return;
            // A valid start-of-route has no preceding boundary and remains the
            // existing "not occupied" case. A boundary with no valid block is
            // instead WHITE until DV Signals supplies a consistent snapshot.
            bool current = currentOccupied.GetValueOrDefault();
            var upcoming = next as Reading;
            CabSignalDetail detail;
            var aspect = Evaluate(upcoming == null ? null : upcoming.MainSignal, car, manuallyOccupied, current, forbidden, out detail);
            CabSignalLogic.Set(display, aspect, upcoming == null ? null : upcoming.Name);
            display.Detail = detail;
        }

        private bool? FindCurrentBlockOccupation(TrackPosition<RailTrack> position, TrainCar car,
            HashSet<RailTrack> manuallyOccupied)
        {
            bool sawBoundary = false;
            var behind = RouteLogic.Walk(NativeRoute.Instance,
                new TrackPosition<RailTrack>(position.Track, position.Span, -position.Direction), 20000);
            foreach (var leg in behind)
            {
                Reading previous = null; double nearest = double.PositiveInfinity;
                foreach (Reading reading in OnTrack(leg.Track, -leg.Direction))
                {
                    if (!reading.Boundary || !leg.Contains(reading.Span)) continue;
                    sawBoundary = true;
                    if (!reading.Contains(position)) continue;
                    double distance = leg.DistanceAt(reading.Span);
                    if (distance < nearest) { nearest = distance; previous = reading; }
                }
                if (previous == null) continue;
                return Occupied(previous.Signal, car, manuallyOccupied);
            }
            return sawBoundary ? (bool?)null : false;
        }

        private static bool? Occupied(DVSignal signal, TrainCar car, HashSet<RailTrack> manuallyOccupied)
        {
            var block = signal == null ? null : signal.Block;
            if (block == null || block.ShouldBeUpdated || block.Tracks.Length == 0) return null;
            foreach (var track in block.AllTracks)
            {
                if (track == null) return null;
                if (manuallyOccupied.Contains(track)) return true;
                foreach (var bogie in track.BogiesOnTrack())
                {
                    if (bogie == null || bogie.Car == null) return null;
                    var other = bogie.Car;
                    if (car == null || (other != car && (car.trainset == null || other.trainset != car.trainset))) return true;
                }
            }
            return false;
        }
        private static DVSignal MainSignalOf(BasicSignalController controller)
        {
            // Distant/repeater represents its home, not another free block.
            for (int i = 0; i < 8 && controller is DistantSignalController distant; i++) controller = distant.Home;
            return controller != null && controller.Exists && !(controller is DistantSignalController) ? controller.GetControllerSignal() : null;
        }
        private static CabAspect Evaluate(DVSignal signal, TrainCar car, HashSet<RailTrack> occupied, bool current, bool forbidden,
            out CabSignalDetail detail)
        {
            detail = CabSignalDetail.None;
            if (current || forbidden) return CabAspect.Red;
            var aspect = signal == null || signal.IsOff ? null : signal.CurrentAspect;
            if (aspect == null) return CabAspect.White;
            if (signal.IsShunting) return aspect.DisallowPassing ? CabAspect.YellowRed : CabAspect.White;
            bool? first = Occupied(signal, car, occupied), second = null;
            if (first == false)
            {
                var next = MainSignalOf(signal.GetNextController());
                if (next != null && next != signal && next.Block != signal.Block && !next.IsOff && next.CurrentAspect != null)
                    second = next.CurrentAspect.DisallowPassing ? true : Occupied(next, car, occupied);
            }
            var definition = aspect.GetDefinition();
            if (definition == null) return CabAspect.White;
            detail = ReadDetail(signal, first, second);
            return CabSignalLogic.Decide(false, false, true, aspect.DisallowPassing,
                first, second, definition.UsePassingSpeed || definition.RequireAcknowledging);
        }

        private static CabSignalDetail ReadDetail(DVSignal signal, bool? first, bool? second)
        {
            var aspect = signal.CurrentAspect;
            var definition = aspect.GetDefinition();
            // Only the audited pack's semantic IDs AND its lamp definitions are
            // recognized. Unknown packs, animation sequences and dirty routes
            // retain the previous ALS presentation; lamp emission is not read.
            if (aspect.DisallowPassing || definition == null || signal.Block == null || signal.Block.ShouldBeUpdated ||
                definition.LightSequences == null || definition.ColourChangers == null ||
                definition.LightSequences.Length != 0 || definition.ColourChangers.Length != 0) return CabSignalDetail.None;
            int on = LightPattern(definition.OnLights), blink = LightPattern(definition.BlinkingLights);
            if (aspect.Id == "RESTRICTED_ENTRY")
                return first == true && on == 1 && blink == 2 && definition.UsePassingSpeed &&
                    definition.PassingSpeed == 20 && definition.RequireAcknowledging &&
                    TrackReserver.HasReservation(signal) && !TrackReserver.IsSignalReservedByAnother(signal)
                    ? CabSignalDetail.RestrictedEntry : CabSignalDetail.None;
            if (first != false) return CabSignalDetail.None;
            switch (aspect.Id)
            {
                case "NEXT_RESTRICTED":
                    return second == false && on == 0 && blink == 4 ? CabSignalDetail.ExpectRestricted : CabSignalDetail.None;
                case "RESTRICTED":
                    return second == false && on == 4 && blink == 2 ? CabSignalDetail.RestrictedClear : CabSignalDetail.None;
                case "RESTRICTED_NEXT_STOP":
                    return second != false && on == 2 && blink == 2 ? CabSignalDetail.RestrictedStop : CabSignalDetail.None;
                case "RESTRICTED_NEXT_RESTRICTED":
                    return second == false && on == 0 && blink == 6 ? CabSignalDetail.RestrictedRestricted : CabSignalDetail.None;
                default: return CabSignalDetail.None;
            }
        }

        private static int LightPattern(SignalLightDefinition[] lights)
        {
            if (lights == null) return -1;
            int result = 0;
            foreach (var light in lights)
            {
                if (light == null || light.NightOnly) return -1;
                var c = light.Colour;
                // Colours read from DV Signals 1.1.3 signal_bundle. Narrow
                // matching intentionally falls back for unverified definitions.
                int value = Math.Abs(c.r - 1) < .01f && Math.Abs(c.g - .2f) < .01f && Math.Abs(c.b - .2f) < .01f ? 1 :
                    Math.Abs(c.r - 1) < .01f && Math.Abs(c.g - .8f) < .01f && Math.Abs(c.b) < .01f ? 2 :
                    Math.Abs(c.r - .2f) < .01f && Math.Abs(c.g - 1) < .01f && Math.Abs(c.b - 2f / 3) < .01f ? 4 : 0;
                if (value == 0 || (result & value) != 0) return -1;
                result |= value;
            }
            return result;
        }

        private sealed class Reading : ISignalReading
        {
            private readonly BasicSignalController controller;
            private readonly SignalDisplay display = new SignalDisplay();
            internal Reading(BasicSignalController controller) { this.controller = controller; }
            internal bool Boundary { get { return !(controller is DistantSignalController) && !controller.ActingAsDistant; } }
            internal DVSignal MainSignal { get { return Valid ? (controller is DistantSignalController ? MainSignalOf(controller) : Signal) : null; } }
            internal DVSignal Signal
            {
                get
                {
                    if (controller == null || !controller.Exists || !controller.PlacementInfo.HasValue || Track == null) return null;
                    var main = controller.GetControllerSignal();
                    bool include = Main.IncludeShuntingSignals;
                    if (main != null && FeedbackLogic.IncludeSignal(main.IsShunting, include) && !main.IsOff) return main;
                    if (include)
                    {
                        var shunting = controller.GetControllerShuntingSignal();
                        if (shunting != null && !shunting.IsOff) return shunting;
                    }
                    return main != null && FeedbackLogic.IncludeSignal(main.IsShunting, include) ? main : null;
                }
            }
            public RailTrack Track { get { return controller.PlacementInfo.Value.Track; } }
            public int Direction { get { return controller.PlacementInfo.Value.Direction == TrackDirection.Out ? -1 : 1; } }
            public double Span
            {
                get
                {
                    var placement = controller.PlacementInfo.Value;
                    var points = placement.Track.GetKinkedPointSet().points;
                    return points[Mathf.Clamp(placement.PointIndex, 0, points.Length - 1)].span;
                }
            }
            public string Name { get { var s = Signal; return s == null ? "unavailable" : s.Name; } }
            public bool Valid { get { return Signal != null && Track.GetKinkedPointSet() != null && Track.GetKinkedPointSet().points.Length > 0; } }
            public bool ProhibitsPassing { get { var signal = Signal; return Boundary && signal != null && signal.CurrentAspect != null && signal.CurrentAspect.DisallowPassing; } }
            internal bool Contains(TrackPosition<RailTrack> position)
            {
                var signal = Signal; var block = signal == null ? null : signal.Block;
                if (block == null || block.ShouldBeUpdated || position.Direction * position.Direction != 1) return false;
                bool inBlock = block.Tracks.Any(t => t.Track == position.Track && (t.Direction == TrackDirection.Out ? 1 : -1) == position.Direction);
                if (position.Track == Track)
                {
                    if (position.Direction != Direction || (position.Span - Span) * Direction <= .00001) return false;
                    inBlock = true;
                }
                var end = signal.GetNextController();
                if (end != null && end.Exists && end.PlacementInfo.HasValue && end.PlacementInfo.Value.Track == position.Track)
                {
                    var limit = new Reading(end);
                    if (limit.Valid && limit.Direction == position.Direction && (position.Span - limit.Span) * position.Direction > .00001) return false;
                }
                return inBlock;
            }
            public SignalDisplay ReadDisplay()
            {
                CabSignalDetail detail;
                CabSignalLogic.Set(display, Evaluate(MainSignal, null, TrackChecker.GetAllOccupiedTracks(), false, false, out detail), Name);
                display.Detail = detail;
                return display;
            }
        }
    }
}
