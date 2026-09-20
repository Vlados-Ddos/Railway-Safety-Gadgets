using System;
using System.Collections.Generic;
using DV.CabControls;
using DV.Customization;
using DV.Customization.Gadgets;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    public enum DeviceKind { Signal, Speed, Brake }

    public sealed class GadgetAuthoring : custom_item_components.GadgetBase
    {
        public DeviceKind kind;
    }

    public sealed class SafetyGadget : GadgetBase
    {
        public DeviceKind kind;
        public readonly BrakeIncident Incident = new BrakeIncident();
        public bool Tripped { get { return Incident.Holding; } }
        internal bool BrakeWarningActive { get { return kind == DeviceKind.Brake && service != null && service.BrakeWarningActive; } }
        internal bool ForceRemovalInProgress { get; private set; }
        private LocoService service;
        private GadgetVisuals visuals;
        private ButtonBase button;
        private GadgetAudio audio;

        public bool Operational { get { return IsOnTrainCar && IsLinked && MountedOn != null && PowerState && IsSoldered && ArePlacementRequirementsMet; } }

        protected override void Awake()
        {
            base.Awake();
            visuals = new GadgetVisuals(this);
            audio = new GadgetAudio(transform);
        }

        private void Start()
        {
            if (kind != DeviceKind.Brake) return;
            button = GetComponentInChildren<ButtonBase>(true);
            if (button != null) button.Used += ResetActivation;
        }

        public override bool IsValidTarget(Customization target, Collider hitCollider)
        {
            var owner = target as TrainCarCustomization;
            if (owner == null || owner.TrainCar == null || !owner.TrainCar.IsLoco || owner.Controls == null || owner.TrainCar.carLivery.interiorPrefab == null) return false;
            if (hitCollider != null)
            {
                var car = owner.TrainCar;
                bool interior = car.interior != null && hitCollider.transform.IsChildOf(car.interior);
                if (car.loadedInterior != null) interior |= hitCollider.transform.IsChildOf(car.loadedInterior.transform);
                if (!interior) return false;
            }
            return base.IsValidTarget(target, hitCollider);
        }

        protected override void OnAfterLinked()
        {
            base.OnAfterLinked();
            if (TrainCar != null && TrainCar.IsLoco)
            {
                service = LocoService.Get(TrainCar);
                service.Add(this);
            }
        }

        protected override void OnBeforeUnlinked()
        {
            if (service != null) service.Remove(this);
            service = null;
            // The native train brake remains wherever the driver/system left it.
            Incident.Clear();
            if (audio != null) audio.StopAll();
            base.OnBeforeUnlinked();
        }

        protected override void OnPowerStateChanged(bool newState)
        {
            if (service != null) service.DeviceStateChanged();
            if (!newState && audio != null) audio.StopAll();
        }

        private void Update()
        {
            bool operational = Operational;
            if (visuals != null) visuals.Update(service, operational);
            if (audio != null) audio.Update(operational, kind == DeviceKind.Brake && (Incident.AlarmPending || BrakeWarningActive));
        }

        internal void PlayWarning(WarningKind warning) { if (Operational && audio != null) audio.Play(warning); }
        private void OnDisable() { if (audio != null) audio.StopAll(); }

        private new void OnDestroy()
        {
            if (button != null) button.Used -= ResetActivation;
            if (service != null) service.Remove(this);
            base.OnDestroy();
        }

        private void ResetActivation()
        {
            if (service != null) service.Acknowledge(this);
            else Incident.Acknowledge();
            if (audio != null) audio.Update(Operational, Incident.AlarmPending);
        }

        public override GadgetItem ForceRemove(bool reparentToTrainCar = true)
        {
            // Native ForceRemove invokes Remove, which checks removal permissions.
            // Bypass only our incident restriction during this native lifecycle call.
            ForceRemovalInProgress = true;
            try { return base.ForceRemove(reparentToTrainCar); }
            finally { ForceRemovalInProgress = false; }
        }

        public override void SaveDataRequested(JObject dst)
        {
            base.SaveDataRequested(dst);
            dst["rsgVersion"] = 4; dst["rsgPower"] = PowerSwitch;
            dst["rsgTripped"] = Tripped;
            dst["rsgAlarmPending"] = Incident.AlarmPending;
        }

        public override void SaveDataLoaded(JObject src)
        {
            base.SaveDataLoaded(src);
            // Native GadgetItem has already restored the installed root pose.
            // Preserve the world position of old models while changing the
            // origin contract from bottom/front to centre, once per old load.
            if (((int?)src["rsgVersion"] ?? 1) < 3)
            {
                Vector3 offset = ModelAssets.ExportCenter(BoundsSize);
                if (Custom != null) transform.localPosition += transform.localRotation * offset;
                else if (GadgetItem != null && GadgetItem.Item != null && GadgetItem.gameObject.activeInHierarchy && !GadgetItem.Item.IsGrabbed())
                    // StartingItemsController restores world-item transforms
                    // before LoadItemData. Preserve their pose above the floor.
                    GadgetItem.transform.position += GadgetItem.transform.TransformVector(offset);
            }
            PowerSwitch = (bool?)src["rsgPower"] ?? true;
            bool held = (bool?)src["rsgTripped"] ?? false;
            Incident.Restore(held, (bool?)src["rsgAlarmPending"] ?? held);
            if (service != null) service.ResetHistory();
        }

        public override void AfterSaveDataLoaded(JObject src)
        {
            base.AfterSaveDataLoaded(src);
            if (service != null) service.ResetHistory();
        }
    }

    internal sealed class GadgetVisuals
    {
        private readonly SafetyGadget gadget;
        private readonly Dictionary<Renderer, Color> last = new Dictionary<Renderer, Color>();
        private readonly Dictionary<Renderer, Texture2D> lastSplitMap = new Dictionary<Renderer, Texture2D>();
        private readonly Dictionary<Renderer, Texture> lastSplitBase = new Dictionary<Renderer, Texture>();
        private readonly Renderer[,] lenses = new Renderer[2, 5];
        private readonly Renderer[,,] segments = new Renderer[2, 3, 7];
        private readonly Renderer arrow, power, brake;
        private bool speedInitialized, lastActive;
        private int? lastNext, lastCurrent;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private readonly SignalDisplay signalLamps = new SignalDisplay();
        private static readonly int Emission = Shader.PropertyToID("_EmissionColor");
        private static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
        private static readonly int MainTex = Shader.PropertyToID("_MainTex");

        internal GadgetVisuals(SafetyGadget gadget)
        {
            this.gadget = gadget;
            var renderers = new Dictionary<string, Renderer>();
            foreach (var r in gadget.GetComponentsInChildren<Renderer>(true)) renderers[r.name] = r;
            Renderer Find(string name) { Renderer value; return renderers.TryGetValue(name, out value) ? value : null; }
            string[] sides = { "L", "R" }, colours = { "Green", "Yellow", "Red", "White", "YellowRed" };
            for (int side = 0; side < 2; side++)
                for (int colour = 0; colour < 5; colour++)
                    lenses[side, colour] = Find("S2_5M_Lens_" + sides[side] + "_" + colours[colour]);
            for (int row = 0; row < 2; row++)
                for (int digit = 0; digit < 3; digit++)
                    for (int seg = 0; seg < 7; seg++)
                        segments[row, digit, seg] = Find((row == 0 ? "Display_Next_" : "Display_Current_") + "Digit_" + (digit + 1) + "_Segment_" + (char)('A' + seg));
            arrow = Find("Red_Prismatic_Indicator_Lens");
            power = Find("Power_Green_Convex_Lens"); brake = Find("Brake_Red_Convex_Lens");
        }

        internal void Update(LocoService service, bool active)
        {
            if (gadget.kind == DeviceKind.Signal)
            {
                SignalDisplay value = active && service != null ? service.CabDisplay : null;
                if (value != null)
                {
                    CabSignalPresentation.CopyTo(value, signalLamps, Time.time);
                    value = signalLamps;
                }
                for (int side = 0; side < 2; side++)
                {
                    Set(lenses[side, 0], new Color(.025f, 2.2f, .08f) * (value == null ? 0 : value.Green), true);
                    Set(lenses[side, 1], new Color(2.2f, 1.25f, .015f) * (value == null ? 0 : value.Yellow), true);
                    Set(lenses[side, 2], new Color(2.2f, .012f, .008f) * (value == null || value.SplitYellowRed ? 0 : value.Red), true);
                    Set(lenses[side, 3], value == null ? Color.black :
                        value.Blue > 0 ? new Color(.01f, .35f, 2.6f) * value.Blue : Color.white * (value.White * 2.2f), true);
                    SplitLens(lenses[side, 4], value);
                }
            }
            else if (gadget.kind == DeviceKind.Speed)
            {
                int? next = service == null ? null : service.NextSpeed, current = service == null ? null : service.CurrentSpeed;
                if (speedInitialized && lastActive == active && lastNext == next && lastCurrent == current) return;
                speedInitialized = true; lastActive = active; lastNext = next; lastCurrent = current;
                for (int row = 0; row < 2; row++)
                {
                    int? value = row == 0 ? next : current;
                    for (int digit = 0; digit < 3; digit++)
                    {
                        int mask = active ? RouteLogic.DigitSegments(value, digit) : 0;
                        for (int seg = 0; seg < 7; seg++)
                            Set(segments[row, digit, seg],
                                (mask & (1 << seg)) != 0 ? (row == 0 ? new Color(.70f, .075f, .008f) : Color.white * .60f) : Color.black, true);
                    }
                }
                Set(arrow, active && FeedbackLogic.IsReduction(current, next)
                    ? new Color(.65f, .01f, .005f) : Color.black, true);
            }
            else
            {
                Set(power, active ? new Color(.025f, 1.8f, .06f) : Color.black);
                Set(brake, active && (gadget.Tripped || gadget.Incident.AlarmPending || (service != null && service.BrakeWarningLampOn)) ? new Color(2.2f, .025f, .015f) : Color.black);
            }
        }

        private void Set(Renderer renderer, Color color, bool solidEmission = false)
        {
            Color old;
            if (renderer == null) return;
            if (last.TryGetValue(renderer, out old) && old == color) return;
            renderer.GetPropertyBlock(block); block.SetColor(Emission, color);
            if (solidEmission) block.SetTexture(EmissionMap, Texture2D.whiteTexture);
            renderer.SetPropertyBlock(block);
            last[renderer] = color;
        }

        private void SplitLens(Renderer renderer, SignalDisplay display)
        {
            if (renderer == null) return;
            Texture baseMap = renderer.sharedMaterial.mainTexture ?? Texture2D.whiteTexture;
            Texture2D map = Texture2D.blackTexture;
            Color color = Color.black;
            if (display != null && display.SplitYellowRed)
            {
                map = ModelAssets.SplitEmission(renderer.sharedMaterial, display.Red > .1f, display.SecondYellow > .1f);
                color = Color.white * (2.2f * Mathf.Max(display.Red, display.SecondYellow));
            }
            else if (display != null && display.SecondYellow > .001f)
            {
                // The same central lens supplies the second yellow for the
                // documented two-yellow restricted-speed aspect.
                baseMap = map = Texture2D.whiteTexture;
                color = new Color(2.2f, 1.25f, .015f) * display.SecondYellow;
            }
            Color old; Texture2D oldMap; Texture oldBase;
            if (last.TryGetValue(renderer, out old) && old == color &&
                lastSplitMap.TryGetValue(renderer, out oldMap) && oldMap == map &&
                lastSplitBase.TryGetValue(renderer, out oldBase) && oldBase == baseMap) return;
            renderer.GetPropertyBlock(block);
            block.SetTexture(MainTex, baseMap); block.SetTexture(EmissionMap, map); block.SetColor(Emission, color);
            renderer.SetPropertyBlock(block);
            last[renderer] = color; lastSplitMap[renderer] = map; lastSplitBase[renderer] = baseMap;
        }
    }
}
