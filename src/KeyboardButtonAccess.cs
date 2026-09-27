using DV;
using DV.CabControls;
using DV.Common;
using DV.HUD;
using DV.KeyboardInput;
using DV.Utils;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    // Uses the game's actual reach policy at the reset button's position.
    // No action registration, input polling, or subscriptions are added here.
    internal sealed class KeyboardButtonAccess : AKeyboardInput
    {
        public override bool FixedUpdateTick { get { return false; } }
        public override void SetupActions(InteriorControlsManager manager) { }
        public override void Tick(float deltaTime) { }

        internal static bool InputAllowed
        {
            get
            {
                var parameters = Globals.G.GameParams;
                if (parameters == null || !parameters.KeyboardDrivingAllowed ||
                    !GameFeatureFlags.IsAllowed(GameFeatureFlags.Flag.KeyboardDriving)) return false;
                var focus = SingletonBehaviour<InputFocusManager>.Instance;
                return focus == null || !focus.hasKeyboardFocus;
            }
        }

        internal static bool CanPress(ButtonBase button, ref KeyboardButtonAccess access)
        {
            if (!InputAllowed || button == null || !button.isActiveAndEnabled || !button.InteractionAllowed) return false;
            if (access == null)
            {
                access = button.GetComponent<KeyboardButtonAccess>();
                if (access == null) access = button.gameObject.AddComponent<KeyboardButtonAccess>();
            }
            // PlayerCanReach implements the installed game's XZ/Y range,
            // anywhere-on-vehicle option and joystick/custom-controller exception.
            if (PlayerManager.PlayerCamera == null && PlayerManager.PlayerTransform == null) return false;
            return access.PlayerCanReach();
        }
    }
}
