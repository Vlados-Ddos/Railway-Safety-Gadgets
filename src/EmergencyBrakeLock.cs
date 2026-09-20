using System;
using System.Collections.Generic;
using DV.CabControls;
using DV.CabControls.NonVR;
using DV.HUD;
using DV.Simulation.Controllers;
using HarmonyLib;
using LocoSim.Implementations;

namespace RailwaySafetyGadgets
{
    // Owns only this locomotive's train-brake input. Native blockers and other
    // controls are never replaced. No frame loop writes a brake value.
    internal sealed class EmergencyBrakeLock : IDisposable
    {
        private static readonly HashSet<OverridableBaseControl> Brakes = new HashSet<OverridableBaseControl>();
        private static readonly HashSet<Port> Ports = new HashSet<Port>();
        private static readonly Dictionary<ControlImplBase, EmergencyBrakeLock> Handles = new Dictionary<ControlImplBase, EmergencyBrakeLock>();
        private BrakeControl brake;
        private Port port;
        private ControlImplBase handle;
        private bool nativeInteractionAllowed, nativeBlocked, changingHandle;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Port), nameof(Port.ExternalValueUpdate)), prefix: Patch(nameof(AllowPortWrite)));
            harmony.Patch(AccessTools.Method(typeof(OverridableBaseControl), nameof(OverridableBaseControl.Move)), prefix: Patch(nameof(AllowMove)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(OverridableBaseControl), nameof(OverridableBaseControl.IsControlBlocked)), postfix: Patch(nameof(ControlBlocked)));
            harmony.Patch(AccessTools.PropertySetter(typeof(ControlImplBase), nameof(ControlImplBase.InteractionAllowed)), prefix: Patch(nameof(InteractionRequested)));
            harmony.Patch(AccessTools.Method(typeof(ControlImplBase), nameof(ControlImplBase.SetValue)), prefix: Patch(nameof(AllowHandleValue)));
            harmony.Patch(AccessTools.Method(typeof(ControlImplBase), "RequestValueUpdate"), prefix: Patch(nameof(AllowHandleValue)));
            // The shipped train-brake control derives from LeverBase. Its native
            // BlockControl sets joint drag; InteractionAllowed also stops VR grabs.
            harmony.Patch(AccessTools.Method(typeof(LeverBase), nameof(LeverBase.BlockControl)), prefix: Patch(nameof(BlockRequested)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(LeverNonVR), nameof(LeverNonVR.BlockControl)), prefix: Patch(nameof(NonVRBlockRequested)));
        }

        private static HarmonyMethod Patch(string name) { return new HarmonyMethod(typeof(EmergencyBrakeLock), name); }
        private static bool AllowPortWrite(Port __instance, float newValue) { return !Ports.Contains(__instance) || newValue >= 1f; }
        private static bool AllowMove(OverridableBaseControl __instance) { return !Brakes.Contains(__instance); }
        private static void ControlBlocked(OverridableBaseControl __instance, ref bool __result) { if (Brakes.Contains(__instance)) __result = true; }
        private static bool AllowHandleValue(ControlImplBase __instance, float newValue) { return !Handles.ContainsKey(__instance) || newValue >= 1f; }
        private static void InteractionRequested(ControlImplBase __instance, ref bool value)
        {
            EmergencyBrakeLock owner;
            if (!Handles.TryGetValue(__instance, out owner) || owner.changingHandle) return;
            owner.nativeInteractionAllowed = value;
            value = false;
        }
        private static void BlockRequested(LeverBase __instance, ref bool setBlock)
        {
            // NonVR's override also blocks its grab handler and scrolling. Patch
            // that override before its body; do not record its nested base call.
            if (__instance is LeverNonVR) return;
            RememberBlock(__instance, ref setBlock);
        }
        private static void NonVRBlockRequested(LeverNonVR __instance, ref bool setBlock) { RememberBlock(__instance, ref setBlock); }
        private static void RememberBlock(ControlImplBase __instance, ref bool setBlock)
        {
            EmergencyBrakeLock owner;
            if (!Handles.TryGetValue(__instance, out owner) || owner.changingHandle) return;
            owner.nativeBlocked = setBlock;
            setBlock = true;
        }

        internal void Hold(TrainCar car, BrakeControl control)
        {
            if (control == null) return;
            if (brake != control)
            {
                Dispose();
                port = (Port)AccessTools.Field(typeof(OverridableBaseControl), "controlPort").GetValue(control);
                if (port == null) return; // Simulation not initialized yet; retry binding, not braking.
                brake = control;
                // Native Set/MUOverride normally uses this exact external input.
                // Set once before blocking; native BrakeControl updates pneumatics.
                port.ExternalValueUpdate(1f);
                Ports.Add(port); Brakes.Add(brake);
            }
            ControlImplBase next = null;
            if (car.loadedInterior != null)
            {
                var manager = car.loadedInterior.GetComponent<InteriorControlsManager>();
                InteriorControlsManager.ControlReference reference;
                if (manager != null && manager.TryGetControl(InteriorControlsManager.ControlType.TrainBrake, out reference)) next = reference.controlImplBase;
            }
            if (ReferenceEquals(next, handle)) return;
            ReleaseHandle();
            if (next == null) return;
            handle = next;
            nativeInteractionAllowed = handle.InteractionAllowed;
            nativeBlocked = brake.controlBlocker != null && brake.controlBlocker.isBlocked;
            if (handle.IsGrabbed()) handle.ForceEndInteraction();
            Handles[handle] = this;
            changingHandle = true;
            try { handle.InteractionAllowed = false; handle.BlockControl(true); }
            finally { changingHandle = false; }
        }

        private void ReleaseHandle()
        {
            if (ReferenceEquals(handle, null)) return;
            Handles.Remove(handle);
            if (handle != null)
            {
                handle.BlockControl(nativeBlocked);
                handle.InteractionAllowed = nativeInteractionAllowed;
            }
            handle = null;
        }
        public void Dispose()
        {
            ReleaseHandle();
            if (!ReferenceEquals(brake, null)) Brakes.Remove(brake);
            if (port != null) Ports.Remove(port);
            brake = null; port = null;
            // Leave the native brake at full application; the driver releases it.
        }
    }
}
