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
    public sealed class SignalsAdapter : ISignalSource, ICabSignalSource, ISignalPassageSource, IModeCabSignalSource
    {
        private readonly Dictionary<RailTrack, List<ISignalReading>> byTrack = new Dictionary<RailTrack, List<ISignalReading>>();
        private SignalManager manager;
        private int count = -1;
        private float refreshAt;
        private static int registryRevision;
        private int seenRevision = -1;
        private struct IndexEntry
        {
            internal BasicSignalController Controller;
            internal RailTrack Track;
            internal int Direction, Point; internal bool Old;
        }
        private readonly List<IndexEntry> indexSnapshot = new List<IndexEntry>();
        private readonly List<RouteLeg<RailTrack>> behind = new List<RouteLeg<RailTrack>>();
        private readonly HashSet<RailTrack> behindVisited = new HashSet<RailTrack>();
        private static readonly ISignalReading[] Empty = new ISignalReading[0];
        public int Generation { get; private set; }
        public bool Ready { get { return !UnloadWatcher.isUnloading && SignalManager.Running && manager != null && manager == SignalManager.Instance && count > 0; } }

        public SignalsAdapter()
        {
            var harmony = new Harmony("denis.railway-safety-gadgets.signals");
            harmony.Patch(AccessTools.Method(typeof(DVSignal), "ChangeAspect"), prefix: new HarmonyMethod(typeof(SignalsAdapter), nameof(BeforeAspect)));
            harmony.Patch(AccessTools.Method(typeof(DVSignal), "TurnOff"), prefix: new HarmonyMethod(typeof(SignalsAdapter), nameof(BeforeTurnOff)));
            harmony.Patch(AccessTools.Method(typeof(SignalManager), "RegisterController"), prefix: new HarmonyMethod(typeof(SignalsAdapter), nameof(RegistryChanged)));
            harmony.Patch(AccessTools.Method(typeof(SignalManager), "UnregisterController"), prefix: new HarmonyMethod(typeof(SignalsAdapter), nameof(RegistryChanged)));
        }
        private static void BeforeAspect(DVSignal __instance, int newAspect)
        {
            // Match native ChangeAspect's early exits. Negative requests call
            // TurnOff, whose prefix samples once before the actual transition.
            if (__instance == null || newAspect < 0 || newAspect == __instance.CurrentAspectIndex ||
                __instance.AllAspects == null || newAspect >= __instance.AllAspects.Length) return;
            SampleAspectChange(__instance);
        }
        private static void BeforeTurnOff(DVSignal __instance)
        {
            if (__instance != null && !__instance.IsOff) SampleAspectChange(__instance);
        }
        private static void SampleAspectChange(DVSignal signal)
        {
            if (signal.Parent == null && !IsDistant(signal.Controller)) LocoService.SampleBeforeAspectChange();
        }
        private static void RegistryChanged() { unchecked { registryRevision++; } }

        private static IndexEntry Entry(BasicSignalController controller)
        {
            var entry = new IndexEntry { Controller = controller, Old = controller.IsOld };
            if (controller != null && controller.Exists && controller.PlacementInfo.HasValue)
            {
                var placement = controller.PlacementInfo.Value;
                entry.Track = placement.Track; entry.Direction = (int)placement.Direction; entry.Point = placement.PointIndex;
            }
            return entry;
        }
        private bool IndexUnchanged(List<BasicSignalController> controllers)
        {
            int index = 0;
            for (int i = 0; i < controllers.Count; i++)
            {
                if (IsDistant(controllers[i])) continue;
                if (index >= indexSnapshot.Count) return false;
                var old = indexSnapshot[index++]; var current = Entry(controllers[i]);
                if (!ReferenceEquals(old.Controller, current.Controller) || old.Track != current.Track ||
                    old.Direction != current.Direction || old.Point != current.Point || old.Old != current.Old) return false;
            }
            return index == indexSnapshot.Count;
        }

        public void Refresh()
        {
            var current = !UnloadWatcher.isUnloading && SignalManager.Running ? SignalManager.Instance : null;
            if (current == null)
            {
                if (manager != null || count != -1) { Generation++; manager = null; count = -1; byTrack.Clear(); indexSnapshot.Clear(); behind.Clear(); behindVisited.Clear(); }
                return;
            }
            if (manager == current && count == current.AllControllers.Count && seenRevision == registryRevision && Time.unscaledTime < refreshAt) return;
            refreshAt = Time.unscaledTime + 2;
            seenRevision = registryRevision;
            // Keep the periodic metadata check for external edits, but preserve
            // the index/readings when nothing changed. Registry changes also
            // invalidate immediately, including replacement at the same count.
            if (manager == current && IndexUnchanged(current.AllControllers)) { count = current.AllControllers.Count; return; }
            Generation++;
            manager = current; count = current.AllControllers.Count;
            byTrack.Clear(); indexSnapshot.Clear();
            foreach (var controller in current.AllControllers)
            {
                if (IsDistant(controller)) continue;
                indexSnapshot.Add(Entry(controller));
                if (controller == null || !controller.Exists || !controller.PlacementInfo.HasValue) continue;
                var track = controller.PlacementInfo.Value.Track;
                if (track == null) continue;
                List<ISignalReading> list;
                if (!byTrack.TryGetValue(track, out list)) byTrack[track] = list = new List<ISignalReading>();
                list.Add(new Reading(controller, CabSignalMode.Normal));
                list.Add(new Reading(controller, CabSignalMode.Shunting));
            }
        }
        private static bool IsDistant(BasicSignalController controller)
        {
            // SignalPlacer also sets ActingAsDistant on MAIN/ENTRY controllers
            // when their head warns about the next main signal. They still own
            // a stop boundary. Only actual distant/repeater identities are excluded.
            return controller == null || controller is DistantSignalController ||
                controller.Type == SignalType.Distant || controller.Type == SignalType.Repeater;
        }
        public IEnumerable<ISignalReading> OnTrack(RailTrack track, int direction)
        { return OnTrack(track, direction, CabSignalMode.Normal); }
        public IEnumerable<ISignalReading> OnTrack(RailTrack track, int direction, CabSignalMode mode)
        {
            List<ISignalReading> list;
            if (!Ready || track == null || !byTrack.TryGetValue(track, out list)) return Empty;
            return Select(list, track, direction, mode);
        }
        private static IEnumerable<ISignalReading> Select(List<ISignalReading> list, RailTrack track, int direction, CabSignalMode mode)
        {
            foreach (Reading reading in list)
                if (reading.Mode == mode && reading.Valid && reading.Track == track && reading.Direction == direction) yield return reading;
        }
        public bool IsBlockBoundary(ISignalReading signal) { return signal is Reading reading && reading.Boundary; }
        public ISignalReading CapturePassage(ISignalReading signal)
        {
            var reading = signal as Reading;
            return reading == null ? null : reading.Capture();
        }

        public void ReadCab(TrainCar car, TrackPosition<RailTrack> position, ISignalReading next,
            ISignalReading forbiddenEntry, SignalDisplay display)
        { ReadCab(car, position, next, forbiddenEntry, display, (next as Reading)?.Mode ?? (forbiddenEntry as Reading)?.Mode ?? CabSignalMode.Normal); }
        public void ReadCab(TrainCar car, TrackPosition<RailTrack> position, ISignalReading next,
            ISignalReading forbiddenEntry, SignalDisplay display, CabSignalMode mode)
        {
            CabSignalLogic.Set(display, CabAspect.White);
            display.ProtectionStop = null;
            if (!Ready) return;
            var manuallyOccupied = TrackChecker.GetAllOccupiedTracks();
            var entered = forbiddenEntry as Reading;
            bool insideEntry = entered != null && entered.Mode == mode && entered.Valid && entered.Contains(position);
            bool forbidden = insideEntry && entered.ProhibitsPassing && (entered.PassageCaptured ? entered.DeniedAtPassage : true);
            // A captured native permission is bounded to the entered block.
            // No distance/time grace or reservation guessed from proximity.
            bool reservedEntry = insideEntry && entered.ReservedAtPassage && ReservationActive(entered.Signal);
            bool shuntingEntry = insideEntry && entered.ShuntingPermissionAtPassage;
            bool? currentOccupied = FindCurrentBlockOccupation(position, car, manuallyOccupied, mode);
            // Native blocks can be briefly dirty while a junction/controller is
            // rebuilt. A dirty boundary is not evidence that the current block
            // is free, so keep walking the selected route for the next block
            // whose membership agrees with the measured locomotive position.
            // The preceding block may be dirty after a junction change, while
            // the selected signal ahead is perfectly readable. Keep that forward
            // indication; unknown current occupancy is not a confirmed clear.
            // A valid start-of-route has no preceding boundary and remains the
            // existing "not occupied" case. A boundary with no valid block is
            // instead WHITE until DV Signals supplies a consistent snapshot.
            bool current = currentOccupied.GetValueOrDefault();
            var upcoming = next as Reading;
            var nextNative = upcoming == null || upcoming.Mode != mode || !upcoming.Valid ? null : upcoming.Signal;
            CabSignalDetail detail;
            bool known;
            var aspect = Evaluate(nextNative, upcoming != null && upcoming.IsOld, current && !reservedEntry && !shuntingEntry, forbidden, out detail, out known);
            if (reservedEntry && !forbidden && (nextNative == null || nextNative.IsOff || nextNative.CurrentAspect == null))
            {
                // Keep the entered permission for protection, but never let the
                // previous reserved signal overwrite a readable next signal.
                aspect = CabAspect.YellowRed; detail = CabSignalDetail.RestrictedEntry; known = true;
            }
            CabSignalLogic.Set(display, aspect, upcoming == null ? null : upcoming.Name);
            display.Detail = detail; display.Known = known;
            display.Key += ":" + detail;
            if (!known) display.ProtectionStop = null;
            // A confirmed STOP remains decisive even if a preceding block is dirty.
            // Unknown occupation cannot rearm an acknowledged RED as a confirmed clear.
            else display.ProtectionStop = false;
        }

        private bool? FindCurrentBlockOccupation(TrackPosition<RailTrack> position, TrainCar car,
            HashSet<RailTrack> manuallyOccupied, CabSignalMode mode)
        {
            bool sawBoundary = false;
            RouteLogic.Walk(NativeRoute.Instance,
                new TrackPosition<RailTrack>(position.Track, position.Span, -position.Direction), 20000, behind, behindVisited);
            foreach (var leg in behind)
            {
                Reading previous = null; double nearest = double.PositiveInfinity;
                foreach (Reading reading in OnTrack(leg.Track, -leg.Direction, mode))
                {
                    if (!reading.Boundary || !leg.Contains(reading.Span)) continue;
                    sawBoundary = true;
                    if (!reading.Contains(position)) continue;
                    double distance = leg.DistanceAt(reading.Span);
                    if (distance < nearest) { nearest = distance; previous = reading; }
                }
                if (previous == null) continue;
                return Occupied(previous.Signal, car, manuallyOccupied, previous);
            }
            return sawBoundary ? (bool?)null : false;
        }

        private static bool? Occupied(DVSignal signal, TrainCar car, HashSet<RailTrack> manuallyOccupied, Reading currentBoundary = null)
        {
            var block = signal == null ? null : signal.Block;
            if (block == null || block.ShouldBeUpdated || block.Tracks.Length == 0) return null;
            foreach (var track in block.AllTracks)
            {
                if (track == null) return null;
                // ExtraTracks protect a junction ahead, but a parked wagon on
                // another branch is not an obstacle inside our current route.
                if (currentBoundary != null && !block.Tracks.Any(t => t.Track == track)) continue;
                if (manuallyOccupied.Contains(track)) return true;
                foreach (var bogie in track.BogiesOnTrack())
                {
                    if (bogie == null || bogie.Car == null) return null;
                    var other = bogie.Car;
                    if (car != null && (other == car || (car.trainset != null && other.trainset == car.trainset))) continue;
                    if (currentBoundary != null)
                    {
                        if (bogie.traveller == null) return null;
                        int dir = currentBoundary.Direction;
                        foreach (var info in block.Tracks) if (info.Track == track) { dir = info.Direction == TrackDirection.Out ? 1 : -1; break; }
                        if (!currentBoundary.Contains(new TrackPosition<RailTrack>(track, bogie.traveller.Span, dir))) continue;
                    }
                    return true;
                }
            }
            return false;
        }
        private static bool ReservationActive(DVSignal signal)
        {
            return signal != null && signal.Block != null && !signal.Block.ShouldBeUpdated &&
                TrackReserver.HasReservation(signal) && !TrackReserver.IsSignalReservedByAnother(signal);
        }
        private static bool ReservedPermission(DVSignal signal)
        {
            return ReservationActive(signal) && !signal.IsOff && signal.CurrentAspect != null &&
                signal.CurrentAspect.GetDefinition() != null && !signal.CurrentAspect.DisallowPassing;
        }
        private static CabAspect Evaluate(DVSignal signal, bool old, bool current, bool forbidden,
            out CabSignalDetail detail, out bool known)
        {
            detail = CabSignalDetail.None; known = true;
            if (current || forbidden) return CabAspect.Red;
            var aspect = signal == null || signal.IsOff ? null : signal.CurrentAspect;
            if (aspect == null) { known = false; return CabAspect.White; }
            // Actual native prohibition wins over a misleading ID or a reservation.
            if (aspect.DisallowPassing) return CabAspect.Red;
            if (signal.Block != null && signal.Block.ShouldBeUpdated) { known = false; return CabAspect.White; }
            var definition = aspect.GetDefinition();
            if (definition == null) { known = false; return CabAspect.White; }
            if (signal.IsShunting || signal.Controller.Type == SignalType.Spacing)
            {
                if (aspect.Id == "CLEAR") return CabAspect.White;
                known = false; return CabAspect.White;
            }
            if (old)
            {
                // Semaphores use movers and night-only lamps. Native IsOld plus
                // semantic aspect identity, not daytime lamp emission, is decisive.
                if (aspect.Id == "CLEAR") return CabAspect.Green;
                if (aspect.Id == "RESTRICTED") return CabAspect.GreenYellow;
                known = false; return CabAspect.White;
            }
            if (definition.LightSequences == null || definition.ColourChangers == null ||
                definition.LightSequences.Length != 0 || definition.ColourChangers.Length != 0)
            { known = false; return CabAspect.White; }
            int on = LightPattern(definition.OnLights), blink = LightPattern(definition.BlinkingLights);
            switch (aspect.Id)
            {
                case "CLEAR": if (on == 4 && blink == 0) return CabAspect.Green; break;
                case "NEXT_STOP": if (on == 2 && blink == 0) return CabAspect.Yellow; break;
                case "NEXT_RESTRICTED":
                    if (on == 0 && blink == 4) { detail = CabSignalDetail.ExpectRestricted; return CabAspect.Green; } break;
                case "RESTRICTED":
                    if (on == 4 && blink == 2) { detail = CabSignalDetail.RestrictedClear; return CabAspect.GreenYellow; } break;
                case "RESTRICTED_NEXT_STOP":
                    if (on == 2 && blink == 2) { detail = CabSignalDetail.RestrictedStop; return CabAspect.Yellow; } break;
                case "RESTRICTED_NEXT_RESTRICTED":
                    if (on == 0 && blink == 6) { detail = CabSignalDetail.RestrictedRestricted; return CabAspect.GreenYellow; } break;
                case "RESTRICTED_ENTRY":
                    if (on == 1 && blink == 2 && definition.UsePassingSpeed && definition.PassingSpeed <= 20 &&
                        definition.PassingSpeed > 0 && ReservedPermission(signal))
                    { detail = CabSignalDetail.RestrictedEntry; return CabAspect.YellowRed; }
                    // An entry request without real permission is not a permissive cab aspect.
                    return CabAspect.Red;
            }
            known = false; return CabAspect.White;
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
            private DVSignal passageSignal;
            internal bool PassageCaptured, DeniedAtPassage, ReservedAtPassage, ShuntingPermissionAtPassage;
            internal readonly CabSignalMode Mode;
            internal bool IsOld { get { return controller.IsOld; } }
            internal Reading(BasicSignalController controller, CabSignalMode mode) { this.controller = controller; Mode = mode; }
            internal Reading Capture()
            {
                var signal = PassageSignal;
                return new Reading(controller, Mode) { passageSignal = signal, PassageCaptured = true,
                    DeniedAtPassage = ProhibitsPassing, ReservedAtPassage = ReservedPermission(signal),
                    ShuntingPermissionAtPassage = signal != null && signal.IsShunting && !signal.IsOff &&
                        signal.CurrentAspect != null && signal.CurrentAspect.GetDefinition() != null && !signal.CurrentAspect.DisallowPassing };
            }
            private DVSignal PassageSignal { get { return Signal; } }
            internal bool Boundary { get { return !IsDistant(controller); } }
            internal DVSignal Signal
            {
                get
                {
                    if (!Boundary || !controller.Exists || !controller.PlacementInfo.HasValue || Track == null ||
                        (controller.IsOld && (Mode == CabSignalMode.Shunting || !Main.IncludeOldSignals))) return null;
                    if (PassageCaptured) return passageSignal;
                    var main = controller.GetControllerSignal();
                    DVSignal candidate;
                    bool shuntingMain = main != null && (main.IsShunting || controller.Type == SignalType.Spacing);
                    if (Mode == CabSignalMode.Shunting) candidate = shuntingMain ? main : controller.GetControllerShuntingSignal();
                    else candidate = shuntingMain ? null : main;
                    // Parent marks an attached distant head. SelfActsAsDistant
                    // is only a prefab capability (also true on main heads),
                    // so the controller's actual role above is authoritative.
                    if (candidate == null || candidate.Parent != null || candidate.Definition == null) return null;
                    return candidate;
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
                    return placement.PointIndex >= 0 && placement.PointIndex < points.Length ? points[placement.PointIndex].span : double.NaN;
                }
            }
            public string Name { get { var s = Signal; return s == null ? "unavailable" : s.Name; } }
            public bool Valid
            {
                get
                {
                    if (Signal == null) return false;
                    var set = Track.GetKinkedPointSet();
                    if (set == null || set.points == null || set.points.Length == 0) return false;
                    int index = controller.PlacementInfo.Value.PointIndex;
                    // The native placer instantiates at points[PointIndex]. An
                    // invalid index supplies no position, not the nearest endpoint.
                    return index >= 0 && index < set.points.Length && ProtectionPolicy.Finite(set.points[index].span);
                }
            }
            public bool ProhibitsPassing { get { var signal = PassageSignal; return Boundary && signal != null && signal.CurrentAspect != null && (signal.CurrentAspect.DisallowPassing || (signal.CurrentAspect.Id == "RESTRICTED_ENTRY" && !ReservedPermission(signal))); } }
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
                    var limit = new Reading(end, Mode);
                    if (limit.Valid && limit.Direction == position.Direction && (position.Span - limit.Span) * position.Direction > .00001) return false;
                }
                return inBlock;
            }
            public SignalDisplay ReadDisplay()
            {
                CabSignalDetail detail;
                bool known;
                CabSignalLogic.Set(display, Evaluate(Valid ? Signal : null, IsOld, false, false, out detail, out known), Name);
                display.Detail = detail; display.Known = known; display.Key += ":" + detail;
                display.ProtectionStop = known ? (bool?)false : null;
                return display;
            }
        }
    }
}
