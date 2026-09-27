using System;
using DV.CabControls;
using DV.Shops;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    // Owns only the paired gadget root. The native item, dumpster, storage and
    // ShopRestocker retain sole ownership of deletion and stock accounting.
    internal sealed class GadgetItemLifetime : MonoBehaviour
    {
        private SafetyGadget gadget;

        internal void Bind(SafetyGadget value) { gadget = value; }

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ItemDumpster), "IsValidDumpsterItem"),
                prefix: new HarmonyMethod(typeof(GadgetItemLifetime), nameof(CanRegister)));
            harmony.Patch(AccessTools.Method(typeof(ItemDumpster), "UnregisterItem"),
                postfix: new HarmonyMethod(typeof(GadgetItemLifetime), nameof(RestoreDistance)));
        }

        private static void RestoreDistance(ItemBase itemBase)
        {
            if (UnloadWatcher.isUnloading || itemBase == null || itemBase.GetComponent<GadgetItemLifetime>() == null) return;
            var respawner = itemBase.GetComponent<RespawnOnDrop>();
            // Native unregister restores maxDistance directly, leaving its
            // squared cache at 75m. Use the native setter to restore both.
            if (respawner != null) respawner.SetMaxDistance(respawner.maxDistance);
        }

        private static bool CanRegister(ItemBase itemBase, ref bool __result)
        {
            var lifetime = itemBase == null ? null : itemBase.GetComponent<GadgetItemLifetime>();
            if (lifetime == null) return true;
            var storage = SingletonBehaviour<StorageController>.Instance;
            // Native RegisterItem dereferences RespawnOnDrop and world storage.
            // Refuse an incomplete own item before it changes ownership/storage.
            // Installed gadgets must first follow normal removal permissions.
            if (UnloadWatcher.isUnloading || lifetime.gadget == null || lifetime.gadget.IsLinked ||
                storage == null || storage.StorageWorld == null || SingletonBehaviour<GlobalShopController>.Instance == null ||
                itemBase.GetComponent<RespawnOnDrop>() == null || itemBase.GetComponent<ShopRestocker>() == null)
            { __result = false; return false; }
            return true;
        }

        private void OnDestroy()
        {
            var owned = gadget; gadget = null;
            // During scene teardown Unity destroys both scene roots. Do not
            // invoke customization/world services after their teardown began.
            if (owned == null || UnloadWatcher.isUnloading) return;
            try
            {
                if (owned.IsLinked) owned.Unlink();
                else owned.wiring.UnwireAll();
            }
            catch (Exception ex) { Main.ErrorOnce("disposed-gadget-unlink", ex); }
            finally
            {
                // Native Unlink calls SafetyGadget.OnBeforeUnlinked and removes
                // only this unit from LocoService; other brake causes remain.
                UnityEngine.Object.Destroy(owned.gameObject);
            }
        }
    }
}
