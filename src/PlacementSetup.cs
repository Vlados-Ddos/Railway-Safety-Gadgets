using System;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Items;
using HarmonyLib;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    internal static class PlacementSetup
    {
        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Mount), nameof(Mount.Accepts)), prefix: new HarmonyMethod(typeof(PlacementSetup), nameof(Accepts)));
            harmony.Patch(AccessTools.Method(typeof(Mount), nameof(Mount.GetAttachmentPositions)), prefix: new HarmonyMethod(typeof(PlacementSetup), nameof(Positions)));
            harmony.Patch(AccessTools.Method(typeof(GadgetBase), nameof(GadgetBase.CanBeRemovedUsingMethod)), postfix: new HarmonyMethod(typeof(PlacementSetup), nameof(Removable)));
        }

        private static void Removable(GadgetBase __instance, ref bool __result)
        {
            // A player cannot bypass the reset button by removing the latched unit.
            // Native lifecycle removal opts out via SafetyGadget.ForceRemove.
            if (__instance is SafetyGadget safety && safety.kind == DeviceKind.Brake && safety.Tripped && !safety.ForceRemovalInProgress) __result = false;
        }

        internal static bool Allowed(Mount mount, SafetyGadget gadget)
        {
            if (mount == null || mount.ThisGadget == null) return false;
            var owner = mount.ThisGadget;
            var car = owner.TrainCar;
            if (car == null || !car.IsLoco || owner.Controls == null) return false;
            bool interior = car.interior != null && mount.transform.IsChildOf(car.interior);
            if (car.loadedInterior != null) interior |= mount.transform.IsChildOf(car.loadedInterior.transform);
            if (!interior) return false;
            if (gadget.kind == DeviceKind.Brake)
            {
                var spec = owner.GadgetItem == null ? null : owner.GadgetItem.GetComponent<InventoryItemSpec>();
                // Compare the real native inventory ID, never a guessed name or size.
                if (spec == null || string.IsNullOrEmpty(NativeAssets.SmallMountPrefabId)) return false;
                if (string.Equals(spec.ItemPrefabName, NativeAssets.SmallMountPrefabId, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static bool Accepts(Mount __instance, GadgetBase gadget, ref bool __result)
        {
            if (!(gadget is SafetyGadget safety) || Allowed(__instance, safety)) return true;
            __result = false; return false;
        }
        private static bool Positions(Mount __instance, GadgetBase gadget, ref AttachmentPosition[] __result)
        {
            if (!(gadget is SafetyGadget safety) || Allowed(__instance, safety)) return true;
            __result = new AttachmentPosition[0]; return false;
        }

        internal static void HandAnchors(GameObject source, Bounds bounds, DeviceKind kind)
        {
            // Native non-VR offset mechanism; position the model centre, not its
            // bottom/front export origin, relative to the player's item anchor.
            var hand = source.AddComponent<FovBasedNonVRGrabAnchor>();
            // The stock ATS uses the rig's neutral item orientation.
            hand.localRotation = Vector3.zero;
            // Per-device hand translation only. Keep the native FOV compensation;
            // the tall signal and wide brake unit need different edge clearance.
            Vector3 centerOffset = kind switch
            {
                DeviceKind.Signal => new Vector3(.180f, .020f, .025f),
                DeviceKind.Speed => new Vector3(.140f, -.025f, -.005f),
                DeviceKind.Brake => new Vector3(.130f, -.020f, .060f),
                _ => new Vector3(0, -.015f, .12f)
            };
            hand.localPosition = centerOffset - Quaternion.Euler(hand.localRotation) * bounds.center;
            hand.fovRotationXOffset = AnimationCurve.Linear(30, 0, 120, 0);
            hand.fovDistanceOffset = AnimationCurve.Linear(30, .25f, 120, 0);

            // The stock ATS has side grips suitable for all three rigid enclosures.
            var reference = NativeAssets.Prefab("automatictrainstop");
            var referenceLocal = reference.GetComponent<BoxCollider>();
            foreach (string name in new[] { "[left anchor]", "[right anchor]" })
            {
                var original = reference.transform.Find(name);
                if (original == null) throw new InvalidOperationException("Native grip missing: " + name);
                var anchor = new GameObject(name).transform; anchor.SetParent(source.transform, false);
                Vector3 relative = original.localPosition - referenceLocal.center;
                Vector3 ratios = new Vector3(relative.x / referenceLocal.size.x, relative.y / referenceLocal.size.y, relative.z / referenceLocal.size.z);
                anchor.localPosition = bounds.center + Vector3.Scale(ratios, bounds.size);
                anchor.localRotation = original.localRotation;
            }
        }

        internal static Vector3 LayOnShelf(GameObject visual, Vector3 size, DeviceKind kind)
        {
            // ShelfItem's origin is at the FRONT edge. Its entire volume is in
            // negative Z, not centred on Z=0. Put the tall signal across the
            // shelf so its 0.442 height is not used as the shelf depth (0.387).
            bool across = kind == DeviceKind.Signal;
            float width = across ? size.y : size.x;
            float depth = across ? size.x : size.y;
            Quaternion rotation = Quaternion.Euler(90, across ? 90 : 180, 0);
            visual.transform.localRotation = rotation;
            Vector3 center = new Vector3(0, size.z / 2, -depth / 2 - .01f);
            visual.transform.localPosition = center - rotation * ModelAssets.ExportCenter(size);
            return new Vector3(width + .02f, depth + .02f, size.z);
        }
    }
}
