namespace RailwaySafetyGadgets
{
    public enum WarningKind { None, Change, Caution, Stop }

    // Optional, confirmed DV Signals detail for the lamps only. The existing
    // ALS channels below remain the inputs to warnings and automatic braking.
    public enum CabSignalDetail { None, ExpectRestricted, RestrictedClear, RestrictedStop, RestrictedRestricted, RestrictedEntry }

    public sealed class SignalDisplay
    {
        public string Key;
        public bool Known, SplitYellowRed;
        public WarningKind Warning;
        public CabSignalDetail Detail;
        public float Green, Yellow, SecondYellow, Red, White, Blue;
    }

    // Shared eligibility and speed warning rules. CabSignalLogic owns ALS states.
    public static class FeedbackLogic
    {
        public static bool IsReduction(int? current, int? next)
        {
            return current.HasValue && next.HasValue && current > 0 && next > 0 && next < current;
        }

        public static bool IncludeSignal(bool isShunting, bool includeShunting)
        {
            return !isShunting || includeShunting;
        }

    }

    public sealed class SignalWarningGate
    {
        private string previous;
        private WarningKind previousKind;
        private double nextAllowed;
        public WarningKind Observe(string key, WarningKind kind, double time)
        {
            // Transient missing data and blink-off phases do not rearm a warning.
            if (string.IsNullOrEmpty(key) || kind == WarningKind.None || key == previous) return WarningKind.None;
            bool initial = previous == null;
            bool escalation = kind > previousKind;
            previous = key;
            previousKind = kind;
            if ((initial && kind == WarningKind.Change) || (!escalation && time < nextAllowed)) return WarningKind.None;
            nextAllowed = time + 1.5;
            return kind;
        }
    }

    public sealed class SpeedWarningGate
    {
        private bool reducing;
        private int? lastCurrent, lastNext;
        private double nextAllowed;
        public bool Observe(int? current, int? next, double time)
        {
            if (!current.HasValue || !next.HasValue) return false;
            bool lower = FeedbackLogic.IsReduction(current, next);
            bool changed = lower && (!reducing || lastCurrent != current || lastNext != next);
            reducing = lower; lastCurrent = current; lastNext = next;
            if (!changed || time < nextAllowed) return false;
            nextAllowed = time + 1.5;
            return true;
        }
    }

    public sealed class BrakeIncident
    {
        public bool Holding { get; private set; }
        public bool AlarmPending { get; private set; }
        public void Trip() { Holding = AlarmPending = true; }
        public void Acknowledge() { Holding = AlarmPending = false; }
        public void Clear() { Holding = AlarmPending = false; }
        public void Restore(bool holding, bool alarm) { Holding = holding; AlarmPending = alarm; }
    }
}
