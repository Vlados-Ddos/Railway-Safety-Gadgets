using System;

namespace RailwaySafetyGadgets
{
    [Flags]
    public enum BrakeCause { None = 0, CabRed = 1, Overspeed = 2 }

    // One speed-warning clock and one continuous-red latch per locomotive.
    // World signals and NEXT limits are not inputs.
    public sealed class AutomaticBrakeWarning
    {
        public const double DelaySeconds = 10;
        public const double MaxManualExtensionSeconds = 5;
        public BrakeCause Causes { get; private set; }
        public bool Warning { get; private set; }
        private double started, previous, delay = DelaySeconds, recoveredSince = double.NaN;
        private bool fired, redHandled;
        private bool soundArmed = true;
        public bool SoundRequested { get; private set; }
        public bool Critical { get; private set; }

        public bool ObserveSpeed(bool signalStop, bool signalKnown, double? permittedKmh, double speedKmh,
            double time, bool holding, bool enabled, bool manualEffective, ProtectionPolicy policy)
        {
            SoundRequested = false;
            Critical = false;
            if (signalKnown && !signalStop) redHandled = false;
            if (!enabled) { CancelWarning(); return false; }
            bool valid = permittedKmh.HasValue && ProtectionPolicy.Finite(permittedKmh.Value) &&
                permittedKmh.Value > 0 && permittedKmh.Value <= 999 && ProtectionPolicy.Finite(speedKmh);
            double excess = valid ? Math.Abs(speedKmh) - permittedKmh.Value : double.NaN;
            bool overspeed = valid && excess > policy.ToleranceKmh;
            // Rearm the one-shot only after a stable recovery, not a threshold
            // wobble, missing data, a display step or a settings refresh.
            if (valid && excess <= policy.ToleranceKmh - 1 && ProtectionPolicy.Finite(time))
            {
                if (double.IsNaN(recoveredSince) || time < recoveredSince) recoveredSince = time;
                if (time - recoveredSince >= 2) soundArmed = true;
            }
            else recoveredSince = double.NaN;
            delay = policy.WarningSeconds;
            bool wasWarning = Warning;
            bool immediate = valid && excess >= policy.CriticalKmh;
            BrakeCause causes = (signalStop ? BrakeCause.CabRed : BrakeCause.None) |
                (overspeed ? BrakeCause.Overspeed : BrakeCause.None);
            bool trip = ObserveCore(causes, time, holding, signalKnown, immediate, manualEffective);
            Critical = trip && Causes == BrakeCause.Overspeed && immediate;
            if (!trip && !holding && Warning && !wasWarning && soundArmed)
            { SoundRequested = true; soundArmed = false; }
            return trip;
        }

        public static BrakeCause Evaluate(bool signalOperational, SignalDisplay cab,
            bool useSpeedLimiter, bool speedOperational, int? currentLimit, float speedKmh)
        {
            BrakeCause causes = BrakeCause.None;
            if (signalOperational && cab != null && cab.Known &&
                (cab.ProtectionStop ?? (cab.Red > 0 && !cab.SplitYellowRed)))
                causes |= BrakeCause.CabRed;
            if (useSpeedLimiter && speedOperational && currentLimit.HasValue && currentLimit.Value > 0 && currentLimit.Value <= 999 &&
                !float.IsNaN(speedKmh) && !float.IsInfinity(speedKmh) && Math.Abs(speedKmh) > currentLimit.Value)
                causes |= BrakeCause.Overspeed;
            return causes;
        }

        // Preserve the two-argument public entry point from 1.0.7.
        public bool Observe(BrakeCause causes, double time) { return Observe(causes, time, false, true); }

        // A missing/unpowered cab reading is not evidence that RED ended.
        // Keep observing while the brake is held, so a real non-red rearms it.
        public bool Observe(BrakeCause causes, double time, bool holding = false, bool signalKnown = true)
        {
            delay = DelaySeconds;
            return ObserveCore(causes, time, holding, signalKnown, false, false);
        }

        private bool ObserveCore(BrakeCause causes, double time, bool holding, bool signalKnown, bool critical, bool manualEffective)
        {
            bool red = (causes & BrakeCause.CabRed) != 0;
            if (signalKnown && !red) redHandled = false;
            if (red && !redHandled)
            {
                redHandled = true;
                CancelWarning();
                Causes = BrakeCause.CabRed;
                return !holding;
            }
            if (holding || (causes & BrakeCause.Overspeed) == 0 || double.IsNaN(time) || double.IsInfinity(time))
            { CancelWarning(); return false; }
            Causes = BrakeCause.Overspeed;
            if (critical) { Warning = false; if (fired) return false; fired = true; return true; }
            if (!Warning || time < previous)
            {
                Warning = true; fired = false; started = time;
            }
            previous = time;
            if (fired || time - started < delay) return false;
            // A short, bounded extension only while verified braking will
            // remove the excess within five seconds. Critical excess bypasses it.
            if (manualEffective && time - started < delay + MaxManualExtensionSeconds) return false;
            fired = true;
            return true;
        }

        // Use the same scaled clock for the deadline and 1 Hz, 50% duty blink.
        public bool WarningDue(double time) { return Warning && !fired && time - started >= delay; }
        public bool WarningLampOn(double time) { return Warning && time >= started && (time - started) % 1.0 < .5; }

        public void Acknowledge(BrakeCause causes)
        {
            if ((causes & BrakeCause.CabRed) != 0) redHandled = true;
            CancelWarning();
        }

        public void CancelWarning()
        {
            Causes = BrakeCause.None; Warning = fired = false; started = previous = 0;
        }

        public void Reset() { CancelWarning(); redHandled = false; soundArmed = true; recoveredSince = double.NaN; SoundRequested = Critical = false; }
    }
}
