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
        // Explicit protection result, independent of supplementary lamp colours.
        // null preserves the public legacy semantic-display contract.
        public bool? ProtectionStop;
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

    // Identify a curve by its physical boundary, not its changing speed or distance.
    // Remember recent boundaries across unavailable frames and direction changes.
    public sealed class CurveWarningGate
    {
        private struct Notice { internal object Track; internal double Span; internal int Direction, Limit; }
        private readonly Notice[] notices = new Notice[16];
        private int cursor;
        public void Reset() { System.Array.Clear(notices, 0, notices.Length); cursor = 0; }
        public bool Observe(bool known, bool active, object track, double span, int direction, int? target,
            object headTrack, double headSpan, int headDirection)
        {
            if (!known) return false;
            for (int i = 0; i < notices.Length; i++)
            {
                var n = notices[i];
                // Only confirmed physical passage retires a notice. Losing the
                // route or reversing at a standstill must not rearm its sound.
                if (n.Track != null && ReferenceEquals(n.Track, headTrack) && n.Direction == headDirection &&
                    (headSpan - n.Span) * headDirection > .001) notices[i] = default(Notice);
            }
            if (!active || track == null || !target.HasValue) return false;
            for (int i = 0; i < notices.Length; i++)
                if (ReferenceEquals(notices[i].Track, track) && notices[i].Direction == direction &&
                    System.Math.Abs(notices[i].Span - span) < .001 && notices[i].Limit == target.Value) return false;
            notices[cursor] = new Notice { Track = track, Span = span, Direction = direction, Limit = target.Value };
            cursor = (cursor + 1) % notices.Length;
            return true;
        }
    }

    [System.Flags]
    public enum BrakeHoldCause { None = 0, SignalPassage = 1, Speed = 2, Legacy = 4 }
    public sealed class BrakeIncident
    {
        public BrakeHoldCause Causes { get; private set; }
        public bool Holding { get { return Causes != BrakeHoldCause.None; } }
        public bool AlarmPending { get; private set; }
        public void Trip() { Trip(BrakeHoldCause.Legacy); }
        public void Trip(BrakeHoldCause cause) { Causes |= cause; if (Holding) AlarmPending = true; }
        public void Remove(BrakeHoldCause cause)
        {
            var before = Causes; Causes &= ~cause;
            if (Causes != before && !Holding) AlarmPending = false;
        }
        public void Acknowledge() { Causes = BrakeHoldCause.None; AlarmPending = false; }
        public void Clear() { Acknowledge(); }
        public void Restore(bool holding, bool alarm) { Restore(holding, alarm, BrakeHoldCause.Legacy); }
        public void Restore(bool holding, bool alarm, BrakeHoldCause causes)
        {
            causes &= BrakeHoldCause.SignalPassage | BrakeHoldCause.Speed | BrakeHoldCause.Legacy;
            Causes = holding ? causes == BrakeHoldCause.None ? BrakeHoldCause.Legacy : causes : BrakeHoldCause.None;
            AlarmPending = alarm;
        }
    }
}
