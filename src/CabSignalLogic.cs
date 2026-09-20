namespace RailwaySafetyGadgets
{
    public enum CabAspect { White, Green, Yellow, YellowRed, Red }

    // ALS indications are semantic states. Track lamp colours/blink phases are
    // deliberately not inputs: a stop ahead is YR, not the cab's post-entry red.
    public static class CabSignalLogic
    {
        public static CabAspect Decide(bool occupiedCurrent, bool forbiddenEntry,
            bool transmitted, bool stopAhead, bool? firstOccupied, bool? secondOccupied, bool restricted)
        {
            if (occupiedCurrent || forbiddenEntry) return CabAspect.Red;
            if (!transmitted) return CabAspect.White;
            if (stopAhead || firstOccupied == true) return CabAspect.YellowRed;
            if (!firstOccupied.HasValue) return CabAspect.White;
            if (restricted || secondOccupied != false) return CabAspect.Yellow;
            return CabAspect.Green;
        }

        public static void Set(SignalDisplay display, CabAspect aspect, string key = null)
        {
            display.Green = display.Yellow = display.SecondYellow = display.Red = display.White = display.Blue = 0;
            display.Detail = CabSignalDetail.None;
            display.SplitYellowRed = aspect == CabAspect.YellowRed;
            display.Known = aspect != CabAspect.White;
            display.Key = key == null ? "cab:" + aspect : key + ":" + aspect;
            display.Warning = aspect == CabAspect.Red || aspect == CabAspect.YellowRed ? WarningKind.Stop :
                aspect == CabAspect.Yellow ? WarningKind.Caution : aspect == CabAspect.Green ? WarningKind.Change : WarningKind.None;
            switch (aspect)
            {
                case CabAspect.Green: display.Green = 1; break;
                case CabAspect.Yellow: display.Yellow = 1; break;
                case CabAspect.YellowRed: display.SecondYellow = display.Red = 1; break;
                case CabAspect.Red: display.Red = 1; break;
                default: display.White = 1; break;
            }
        }
    }

    public static class CabSignalPresentation
    {
        // Write to a reusable render-only snapshot, never the shared ALS input.
        // Scaled time keeps both faces in phase and freezes blinking on pause.
        public static void CopyTo(SignalDisplay source, SignalDisplay lamps, double time)
        {
            lamps.Green = source.Green; lamps.Yellow = source.Yellow;
            lamps.SecondYellow = source.SecondYellow; lamps.Red = source.Red;
            lamps.White = source.White; lamps.Blue = source.Blue;
            lamps.SplitYellowRed = source.SplitYellowRed;
            if (!source.Known || (source.Red > 0 && !source.SplitYellowRed)) return;
            float blink = time >= 0 && time % 1.0 < .5 ? 1 : 0;
            switch (source.Detail)
            {
                case CabSignalDetail.ExpectRestricted:
                case CabSignalDetail.RestrictedClear:
                case CabSignalDetail.RestrictedStop:
                case CabSignalDetail.RestrictedRestricted:
                case CabSignalDetail.RestrictedEntry:
                    lamps.Green = lamps.Yellow = lamps.SecondYellow = lamps.Red = lamps.White = lamps.Blue = 0;
                    lamps.SplitYellowRed = false;
                    break;
                default: return;
            }
            switch (source.Detail)
            {
                case CabSignalDetail.ExpectRestricted: lamps.Green = blink; break;
                case CabSignalDetail.RestrictedClear: lamps.Green = 1; lamps.Yellow = blink; break;
                case CabSignalDetail.RestrictedStop: lamps.SecondYellow = 1; lamps.Yellow = blink; break;
                case CabSignalDetail.RestrictedRestricted: lamps.Green = lamps.Yellow = blink; break;
                case CabSignalDetail.RestrictedEntry: lamps.Red = 1; lamps.Yellow = blink; break;
            }
        }
    }
}
