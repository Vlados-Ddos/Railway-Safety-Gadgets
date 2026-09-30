using System;
using System.Collections.Generic;
using DV.Customization.Gadgets;
using DV.Customization.Gadgets.Implementations;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    // Native port registration, handshake, Wire/Unwire, callbacks and UID save
    // links remain authoritative. Filtering lives in CanLinkTo, shared by all.
    internal sealed class ControllerPort : GadgetWiringModule.WireLinkPort<GadgetSwitch>
    {
        private GadgetWiringModule.WireLinkPort linked;
        private readonly bool mode;
        internal ControllerPort(SafetyGadget owner, Action<GadgetSwitch> onWired, Action<GadgetSwitch> onUnwired, bool mode = false)
            : base(owner, false, onWired, onUnwired) { this.mode = mode; }
        internal static bool Accepts(GadgetSwitch controller)
        {
            if (controller == null || controller.GadgetItem == null) return false;
            var spec = controller.GadgetItem.GetComponent<InventoryItemSpec>();
            if (spec == null || string.IsNullOrEmpty(spec.ItemPrefabName)) return false;
            if (controller is AlternatingController) return spec.ItemPrefabName == NativeAssets.AlternatingPrefabId;
            if (spec.ItemPrefabName == NativeAssets.AnalogPrefabId) return true;
            return spec.ItemPrefabName == NativeAssets.ButtonPrefabId || spec.ItemPrefabName == NativeAssets.SwitchPrefabId ||
                spec.ItemPrefabName == NativeAssets.RotarySwitchPrefabId;
        }
        internal static float BrightnessOf(GadgetSwitch controller, GadgetBase recipient, bool analog)
        {
            if (controller == null) return 1f;
            if (analog)
            {
                // Preserve the native float output and power gating. Native
                // range comparisons clamp infinities but allow NaN through.
                float value = controller.OutputValueOf(recipient);
                return float.IsNaN(value) ? 0f : Mathf.Clamp01(value);
            }
            if (controller is AlternatingController alternating)
            {
                // Native OutputValueOf rotates through subscribers on a timer.
                // Only our receiver interprets the actual five-position selector
                // as a stable dimmer; native controllers/other recipients are untouched.
                if (!alternating.PowerState || alternating.IntervalCount != 5) return 0f;
                int position = alternating.SelectedInterval;
                return position >= 0 && position <= 4 ? position * .25f : 0f;
            }
            // A standard controller is strictly OFF/ON, never an analogue dimmer.
            return controller.OutputValueOf(recipient) > 0f ? 1f : 0f;
        }
        internal static bool IsAnalogController(GadgetSwitch controller)
        {
            if (controller == null || controller is AlternatingController || controller.GadgetItem == null) return false;
            var spec = controller.GadgetItem.GetComponent<InventoryItemSpec>();
            return spec != null && spec.ItemPrefabName == NativeAssets.AnalogPrefabId;
        }
        internal static bool IsCabModeSwitch(GadgetSwitch controller)
        {
            if (controller == null || controller is AlternatingController || controller.GadgetItem == null) return false;
            var spec = controller.GadgetItem.GetComponent<InventoryItemSpec>();
            return spec != null && spec.ItemPrefabName == NativeAssets.RotarySwitchPrefabId;
        }
        protected override bool CanBeLinked { get { return linked == null; } }
        protected override bool CanLinkTo(GadgetWiringModule.WireLinkPort port)
        { return base.CanLinkTo(port) && Accepts(port.owner as GadgetSwitch) && IsCabModeSwitch(port.owner as GadgetSwitch) == mode; }
        protected override bool IsLinkedTo(GadgetWiringModule.WireLinkPort port) { return port != null && linked == port; }
        protected override void Add(GadgetWiringModule.WireLinkPort port) { linked = port; }
        protected override void Remove(GadgetWiringModule.WireLinkPort port) { if (linked == port) linked = null; }
        public override void GetLinks(List<GadgetWiringModule.WireLinkPort> destination) { if (linked != null) destination.Add(linked); }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GadgetBase), nameof(GadgetBase.SaveDataLoaded)),
                prefix: new HarmonyMethod(typeof(ControllerPort), nameof(FilterSavedLinks)));
        }
        private static void FilterSavedLinks(GadgetBase __instance, ref JObject src)
        {
            if (!(__instance is SafetyGadget) && !(__instance is GadgetSwitch)) return;
            // Native load uses the same compatibility test, but logs an error
            // for old invalid links. Drop only these links, on either saved end.
            if (__instance.Custom == null || !(src?["links"] is JArray links)) return;
            JArray retained = null;
            bool removedIncompatible = false;
            for (int i = 0; i < links.Count; i++)
            {
                int? uid = (int?)links[i];
                bool reject = false;
                if (uid.HasValue && __instance.Custom.TryGetCustomizerByUID(uid.Value, out var other))
                {
                    if (__instance is SafetyGadget && other is GadgetSwitch sw) reject = !Accepts(sw);
                    else if (other is SafetyGadget && __instance is GadgetSwitch controller) reject = !Accepts(controller);
                    removedIncompatible |= reject;
                    // Both endpoints can carry the native saved UID. Once the
                    // first restored it, the second must not wire or notify twice.
                    if (!reject && (__instance is SafetyGadget || other is SafetyGadget) && other is GadgetBase gadget &&
                        __instance.TryGetCompatiblePorts(gadget, out var a, out var b) && GadgetWiringModule.WireLinkPort.AreWired(a, b)) reject = true;
                }
                if (reject && retained == null)
                {
                    retained = new JArray();
                    for (int j = 0; j < i; j++) retained.Add(links[j].DeepClone());
                }
                if (!reject && retained != null) retained.Add(links[i].DeepClone());
            }
            if (retained == null) return;
            src = (JObject)src.DeepClone(); src["links"] = retained;
            if (removedIncompatible) Main.LogOnce("unsupported-controller-links", Texts.Pick(
                "Удалено сохранённое несовместимое соединение гаджета. Подключите кнопку, переключатель, аналоговый или чередующийся контроллер яркости.",
                "Removed an incompatible saved gadget connection. Connect a button, switch, analog or alternating brightness controller."));
        }
    }
}
