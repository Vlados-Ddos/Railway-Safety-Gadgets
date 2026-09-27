using System;
using InputManager = DV.Interaction.Inputs.InputManager;
using Rewired;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    internal static class BrakeInput
    {
        private static bool armed, capturing;
        private enum BindingMessage { None, Saved, Cancelled, NotReady, Conflict }
        private static BindingMessage message;
        private static int lastGuiFrame = -100;
        private static string parsedBinding;
        private static KeyCode parsedKey;
        private static KeyCode Parse()
        {
            string binding = Main.Settings.BrakeToggleKey;
            if (String.Equals(binding, parsedBinding, StringComparison.Ordinal)) return parsedKey;
            parsedBinding = binding;
            KeyCode key;
            parsedKey = Enum.TryParse(binding, true, out key) && ValidKey(key) ? key : KeyCode.None;
            return parsedKey;
        }
        private static bool ValidKey(KeyCode key)
        { return Enum.IsDefined(typeof(KeyCode), key) && !Keyboard.IsModifierKey(key) && key < KeyCode.Mouse0 && key != KeyCode.Escape; }
        private static bool Conflict(KeyCode key)
        {
            foreach (var map in InputManager.NewPlayer.controllers.maps.GetAllMaps())
                if (map.controllerType == ControllerType.Keyboard)
                    foreach (var binding in map.AllMaps) if (binding.keyCode == key) return true;
            return false;
        }
        private static void SaveKey(KeyCode key)
        {
            Main.Settings.BrakeToggleKey = key.ToString(); Main.Settings.Save(Main.Entry);
            capturing = armed = false; message = BindingMessage.Saved;
        }
        internal static void Update()
        {
            if (capturing && (Time.frameCount - lastGuiFrame > 2 || !Application.isFocused || UnloadWatcher.isUnloading))
            { capturing = false; armed = false; }
            // Rebinding is handled by the real IMGUI key event even while UMM
            // pauses the game or detaches its gameplay keyboard.
            if (capturing || Time.frameCount - lastGuiFrame <= 1 || Main.Settings == null || !ReInput.isReady ||
                !Application.isFocused || Time.timeScale <= 0 || UnloadWatcher.isUnloading ||
                PlayerManager.Car == null || GUIUtility.keyboardControl != 0)
            { armed = false; return; }
            var keyboard = InputManager.NewPlayer.controllers.Keyboard;
            var key = Parse();
            if (keyboard == null || key == KeyCode.None || InputManager.Actions.pausedInBackground)
            { armed = false; return; }
            if (!KeyboardButtonAccess.InputAllowed) { armed = false; return; }
            if (!keyboard.GetKey(key)) { armed = true; return; }
            if (!armed || !keyboard.GetKeyDown(key)) return;
            armed = false;
            if (Conflict(key))
            {
                Main.LogOnce("brake-key-conflict-" + key, Texts.Pick("Нажатие кнопки сброса пропущено: клавиша уже назначена в игре: ", "Reset button press ignored: key is already assigned in the game: ") + key);
                return;
            }
            foreach (var service in LocoService.All)
                if (service != null && service.Car == PlayerManager.Car) service.PressBrakeButton();
        }
        internal static void DrawSettings()
        {
            if (!Application.isFocused) { capturing = false; armed = false; }
            lastGuiFrame = Time.frameCount;
            var current = Parse();
            GUILayout.Label(Texts.Pick("Клавиша красной кнопки сброса: ", "Red reset button key: ") +
                (current == KeyCode.None ? Texts.Pick("не назначена", "unbound") : current.ToString()));
            GUILayout.Label(Texts.Pick(
                "(Клавиша не работает, если управление поездом с помощью горячих клавиш\nотключено в настройках сложности игры.)",
                "(Does not work if keyboard train controls are disabled\nin the game difficulty settings.)"));
            if (!capturing)
            {
                if (GUILayout.Button(Texts.Pick("Назначить клавишу", "Assign key")))
                { capturing = true; armed = false; message = BindingMessage.None; GUIUtility.keyboardControl = 0; }
                if (current != KeyCode.None && GUILayout.Button(Texts.Pick("Сбросить привязку", "Clear binding"))) SaveKey(KeyCode.None);
            }
            else
            {
                GUILayout.Label(Texts.Pick("Нажмите клавишу. Escape — отмена.", "Press a key. Escape cancels."));
                if (GUILayout.Button(Texts.Pick("Отмена", "Cancel"))) { capturing = false; armed = false; message = BindingMessage.Cancelled; }
                var e = Event.current;
                if (capturing && e != null && e.type == EventType.KeyDown)
                {
                    var key = e.keyCode; e.Use();
                    if (key == KeyCode.Escape) { capturing = false; armed = false; message = BindingMessage.Cancelled; }
                    else if (key != KeyCode.None && ValidKey(key))
                    {
                        if (!ReInput.isReady) message = BindingMessage.NotReady;
                        else if (Conflict(key)) message = BindingMessage.Conflict;
                        else SaveKey(key);
                    }
                }
            }
            switch (message)
            {
                case BindingMessage.Saved: GUILayout.Label(Texts.Pick("Привязка сохранена.", "Binding saved.")); break;
                case BindingMessage.Cancelled: GUILayout.Label(Texts.Pick("Назначение отменено.", "Assignment cancelled.")); break;
                case BindingMessage.NotReady: GUILayout.Label(Texts.Pick("Игровая система ввода ещё не готова.", "Game input is not ready yet.")); break;
                case BindingMessage.Conflict: GUILayout.Label(Texts.Pick("Эта клавиша уже занята в игре. Выберите другую.", "This key is already assigned in the game. Choose another.")); break;
            }
        }
    }
}
