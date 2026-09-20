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
        public BrakeCause Causes { get; private set; }
        public bool Warning { get; private set; }
        private double started, previous;
        private bool fired, redHandled;

        public static BrakeCause Evaluate(bool signalOperational, SignalDisplay cab,
            bool useSpeedLimiter, bool speedOperational, int? currentLimit, float speedKmh)
        {
            BrakeCause causes = BrakeCause.None;
            if (signalOperational && cab != null && cab.Known && cab.Red > 0 && !cab.SplitYellowRed)
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
            if (!Warning || time < previous)
            {
                Warning = true; fired = false; started = time;
            }
            previous = time;
            if (fired || time - started < DelaySeconds) return false;
            fired = true;
            return true;
        }

        // Use the same scaled clock for the deadline and 1 Hz, 50% duty blink.
        public bool WarningDue(double time) { return Warning && !fired && time - started >= DelaySeconds; }
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

        public void Reset() { CancelWarning(); redHandled = false; }
    }
}
