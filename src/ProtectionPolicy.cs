using System;

namespace RailwaySafetyGadgets
{
    public struct ProtectionPolicy
    {
        public double ToleranceKmh, CriticalKmh, WarningSeconds;
        public static ProtectionPolicy Create(double tolerance, double critical, double seconds)
        {
            tolerance = Clamp(tolerance, 0, 15, 5);
            return new ProtectionPolicy { ToleranceKmh = tolerance,
                CriticalKmh = Clamp(critical, tolerance + 1, 30, Math.Max(10, tolerance + 1)),
                WarningSeconds = Clamp(seconds, 1, 30, 10) };
        }
        public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        public static double Clamp(double value, double low, double high, double fallback)
        { return Finite(value) ? Math.Max(low, Math.Min(high, value)) : fallback; }
    }

    // Uses measured deceleration AND a real brake command. A handle alone is
    // never sufficient. Relative excess must also be falling as a curve falls.
    public sealed class ManualBrakeProgress
    {
        private double time = double.NaN, speed, excess, since = double.NaN;
        public bool Effective { get; private set; }
        public void Reset() { time = since = double.NaN; Effective = false; }
        public void Observe(double now, double speedKmh, double thresholdKmh, bool commanded, bool holding)
        {
            double currentExcess = Math.Max(0, speedKmh - thresholdKmh), dt = now - time;
            if (!ProtectionPolicy.Finite(now) || !ProtectionPolicy.Finite(speedKmh) || !ProtectionPolicy.Finite(thresholdKmh) ||
                !commanded || holding || dt <= 0 || dt > 1)
            { Effective = false; since = double.NaN; }
            else
            {
                double slowing = (speed - speedKmh) / dt / 3.6;
                double closing = (excess - currentExcess) / dt;
                bool improving = slowing >= .05 && closing >= .18 && currentExcess / closing <= 5;
                if (!improving) { Effective = false; since = double.NaN; }
                else
                {
                    if (double.IsNaN(since)) since = now;
                    Effective = now - since >= 1;
                }
            }
            time = now; speed = speedKmh; excess = currentExcess;
        }
    }
}
