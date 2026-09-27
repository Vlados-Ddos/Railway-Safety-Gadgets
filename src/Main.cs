using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using custom_item_mod;
using DV.CabControls.Spec;
using DV.Customization;
using DV.Customization.Gadgets;
using DV.Interaction;
using DV.Shops;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace RailwaySafetyGadgets
{
    public sealed class Settings : UnityModManager.ModSettings
    {
        public const float SignalScale = .50f;
        public const float SpeedScale = .15f;
        public const float BrakeScale = .25f;
        // Legacy serialized/API field retained. Signal selection is now controlled
        // by the native rotary switch; an old saved value cannot mix both modes.
        public bool IncludeShuntingSignals = false;
        public bool IncludeOldSignals = false;
        public bool UseSpeedLimiter = true;
        public float OverspeedToleranceKmh = 5;
        public float CriticalOverspeedKmh = 10;
        public float SpeedWarningSeconds = 10;
        public bool EnableRouteCurve = true;
        public string BrakeToggleKey = "None";
        // Migration-only input; individual volumes replace this field on save.
        public float WarningVolume = .35f;
        public float SignalVolume = -1, SpeedVolume = -1, BrakeVolume = -1;
        public bool ShouldSerializeWarningVolume() { return false; }
        public float Volume(DeviceKind kind)
        {
            float selected = kind == DeviceKind.Signal ? SignalVolume : kind == DeviceKind.Speed ? SpeedVolume : BrakeVolume;
            return (float)ProtectionPolicy.Clamp(selected, 0, 1, .35);
        }
        public void Normalize()
        {
            var p = ProtectionPolicy.Create(OverspeedToleranceKmh, CriticalOverspeedKmh, SpeedWarningSeconds);
            OverspeedToleranceKmh = (float)p.ToleranceKmh; CriticalOverspeedKmh = (float)p.CriticalKmh;
            SpeedWarningSeconds = (float)p.WarningSeconds;
            float legacy = (float)ProtectionPolicy.Clamp(WarningVolume, 0, 1, .35);
            SignalVolume = NormalizeVolume(SignalVolume, legacy);
            SpeedVolume = NormalizeVolume(SpeedVolume, legacy);
            BrakeVolume = NormalizeVolume(BrakeVolume, legacy);
        }
        private static float NormalizeVolume(float value, float legacy)
        { return value == -1 ? legacy : (float)ProtectionPolicy.Clamp(value, 0, 1, .35); }
        public override void Save(UnityModManager.ModEntry entry) { Normalize(); Save(this, entry); }
    }

    public static class Main
    {
        internal static UnityModManager.ModEntry Entry;
        internal static Settings Settings;
        internal static string ModPath;
        internal static GameObject Staging;
        internal static readonly List<CustomItem> Items = new List<CustomItem>();
        private static readonly HashSet<string> Errors = new HashSet<string>();
        private static bool integrations;
        internal static Harmony Harmony;
        private static float nextIntegration;
        public static bool IncludeShuntingSignals { get { return Settings != null && Settings.IncludeShuntingSignals; } }
        public static bool IncludeOldSignals { get { return Settings != null && Settings.IncludeOldSignals; } }
        public static bool UseSpeedLimiter { get { return Settings == null || Settings.UseSpeedLimiter; } }
        // The permitted-speed display is intentionally fixed at 1 km/h. The
        // former 1/5/10 km/h preference was removed from both the UI and save
        // model; old serialized values therefore cannot affect calculations.
        public static int DisplayStep { get { return 1; } }
        public static bool RouteCurveEnabled { get { return Settings != null && Settings.EnableRouteCurve; } }
        public static ProtectionPolicy Protection { get { return Settings == null ? ProtectionPolicy.Create(5, 10, 10) :
            ProtectionPolicy.Create(Settings.OverspeedToleranceKmh, Settings.CriticalOverspeedKmh, Settings.SpeedWarningSeconds); } }

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Entry = entry; ModPath = entry.Path;
            Settings = UnityModManager.ModSettings.Load<Settings>(entry) ?? new Settings();
            Settings.Normalize();
            entry.OnGUI = OnGUI;
            entry.OnSaveGUI = e => Settings.Save(e);
            entry.OnUpdate = OnUpdate;
            try
            {
                if (!ModActive("custom_item_mod")) { Log(Texts.Pick("Custom Item Mod недоступен. Предметы не добавлены.", "Custom Item Mod is unavailable. Items were not added.")); return false; }
                Staging = new GameObject("RailwaySafetyGadgets_Prefabs");
                Staging.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(Staging);
                Harmony = new Harmony("denis.railway-safety-gadgets");
                PlacementSetup.Install(Harmony);
                EmergencyBrakeLock.Install(Harmony);
                Texts.Install(Harmony);
                ControllerPort.Install(Harmony);
                GadgetItemLifetime.Install(Harmony);
                CustomGadgetBaseMap.RegisterGadgetImplementation(typeof(GadgetAuthoring), typeof(SafetyGadget), ConfigureGadget);
                Harmony.Patch(AccessTools.Method(typeof(ItemModsFinder), "InitializeItems"), postfix: new HarmonyMethod(typeof(ItemRegistration), nameof(ItemRegistration.Register)));
                return true;
            }
            catch (Exception ex) { ErrorOnce("load", ex); return false; }
        }

        private static void OnUpdate(UnityModManager.ModEntry entry, float dt)
        {
            // Release track event subscriptions even when every display was removed
            // before the world unload and no LocoService remains to query the route.
            if (UnloadWatcher.isUnloading)
            {
                SpeedSigns.ClearSession();
                // The adapter is process-lived; no active locomotive may remain
                // to refresh it after the last gadget was removed from the world.
                try { LocoService.Signals?.Refresh(); }
                catch (Exception ex) { ErrorOnce("signals-unload", ex); }
                return;
            }
            try { BrakeInput.Update(); }
            catch (Exception ex) { ErrorOnce("brake-input", ex); }
            if (!integrations)
            {
                integrations = true;
                try { SpeedSigns.InstallPatches(Harmony); }
                catch (Exception ex) { ErrorOnce("speed-integration-setup", ex); }
                try { ShopCompatibility.Install(Harmony); }
                catch (Exception ex) { ErrorOnce("shop-integration-setup", ex); }
            }
            if (LocoService.Signals != null || Time.unscaledTime < nextIntegration) return;
            nextIntegration = Time.unscaledTime + 5;
            if (!ModActive("DVSignals") && !ModActive("wiz.signals")) return;
            try
            {
                var assembly = Assembly.LoadFrom(Path.Combine(ModPath, "RailwaySafetyGadgets.Signals.dll"));
                LocoService.Signals = (ISignalSource)Activator.CreateInstance(assembly.GetType("RailwaySafetyGadgets.SignalsIntegration.SignalsAdapter", true));
                Log(Texts.Pick("Подключено к DV Signals.", "Connected to DV Signals."));
            }
            catch (Exception ex) { ErrorOnce("signals-adapter", ex); }
        }

        internal static bool ModActive(string id)
        {
            return UnityModManager.modEntries.Any(e => e.Active && string.Equals(e.Info.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        internal static void ConfigureGadget(custom_item_components.GadgetBase source, ref GadgetBase target)
        {
            ((SafetyGadget)target).ConfigureKind(((GadgetAuthoring)source).kind);
            var requirements = new TrainCarCustomization.TrainCarCustomizerBase.TrainCarRequirements
            {
                trainCarPresence = TrainCarCustomization.TrainCarCustomizerBase.CustomizerTrainCarRequirements.RequireInterior,
                baseControls = true, cabin = true, electricsFuse = true,
                simPorts = new TrainCarCustomization.TrainCarCustomizerBase.TrainCarRequirements.PortRequirement[0]
            };
            Set(target, "requirements", requirements);
            Set(target, "requireSoldering", true);
            Set(target, "vrIgnoreColliders", new Collider[0]);
        }

        internal static void Set(object instance, string field, object value)
        {
            var info = AccessTools.Field(instance.GetType(), field);
            if (info == null) throw new MissingFieldException(instance.GetType().FullName, field);
            info.SetValue(instance, value);
        }

        public static void Log(string message) { if (Entry != null) Entry.Logger.Log(message); }
        public static void LogOnce(string key, string message) { if (Errors.Add(key)) Log(message); }
        internal static void ErrorOnce(string key, Exception ex)
        {
            if (Errors.Add(key)) Entry.Logger.Error(Texts.Pick("Ошибка мода", "Mod error") + " [" + key + "]: " + ex);
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            GUILayout.Label(Texts.Pick("Настройки", "Options"));
            bool oldSignals = GUILayout.Toggle(Settings.IncludeOldSignals, Texts.Pick("АЛС: учитывать старые сигналы (семафоры)", "Cab signal: include old signals (semaphores)"));
            GUILayout.Label(Texts.Pick("Учитывать семафоры в нормальном режиме.", "Include semaphores in normal mode."));
            if (oldSignals != Settings.IncludeOldSignals)
            {
                Settings.IncludeOldSignals = oldSignals;
                foreach (var service in LocoService.All) if (service != null) service.SignalSelectionChanged();
            }
            Settings.OverspeedToleranceKmh = Slider(Texts.Pick("Допуск превышения, км/ч", "Overspeed tolerance, km/h"), Settings.OverspeedToleranceKmh, 0, 15);
            Settings.CriticalOverspeedKmh = Slider(Texts.Pick("Критическое превышение над допустимой скоростью, км/ч", "Critical excess above permitted speed, km/h"),
                Settings.CriticalOverspeedKmh, Settings.OverspeedToleranceKmh + 1, 30);
            Settings.SpeedWarningSeconds = Slider(Texts.Pick("Предупреждение, секунд", "Warning time, seconds"), Settings.SpeedWarningSeconds, 1, 30);
            Settings.Normalize();
            BrakeInput.DrawSettings();
            GUILayout.Label(Texts.Pick("Красная кнопка и назначенная ей клавиша подтверждают/сбрасывают торможение.",
                "The red button and its assigned key acknowledge/reset braking."));
            Settings.SignalVolume = VolumeSlider(Texts.Name(0, Texts.Russian), Settings.SignalVolume);
            Settings.SpeedVolume = VolumeSlider(Texts.Name(1, Texts.Russian), Settings.SpeedVolume);
            Settings.BrakeVolume = VolumeSlider(Texts.Name(2, Texts.Russian), Settings.BrakeVolume);
        }
        private static float VolumeSlider(string name, float value)
        {
            GUILayout.Label(name + Texts.Pick(": громкость ", ": volume ") + Mathf.RoundToInt(value * 100) + "%");
            return GUILayout.HorizontalSlider(value, 0, 1);
        }

        private static float Slider(string label, float value, float minimum, float maximum)
        {
            GUILayout.Label(label + ": " + value.ToString("0"));
            return Mathf.Round(GUILayout.HorizontalSlider(value, minimum, maximum));
        }
    }

    internal static class ItemRegistration
    {
        private static GameObject registrationRoot;
        internal static readonly string[] Keys = { "rsg_locomotive_signal_v1", "rsg_speed_limit_indicator_v1", "rsg_automatic_brake_unit_v1" };
        internal static readonly int[] Prices = { 25000, 20000, 35000 };
        internal static void Register()
        {
            try
            {
                NativeAssets.Load();
                Main.Items.Clear();
                if (registrationRoot != null) UnityEngine.Object.Destroy(registrationRoot);
                registrationRoot = new GameObject("RSG_registration"); registrationRoot.SetActive(false);
                registrationRoot.transform.SetParent(Main.Staging.transform, false);
                string[] kinds = { "signal", "speed", "brake" };
                float[] scales = { Settings.SignalScale, Settings.SpeedScale, Settings.BrakeScale };
                for (int i = 0; i < kinds.Length; i++)
                {
                    string kind = kinds[i]; float scale = scales[i];
                    Vector3 size = ModelAssets.Size(kind) * scale;
                    var source = New(Keys[i]);
                    var rb = source.AddComponent<Rigidbody>(); rb.mass = i == 0 ? 3f : 1.5f;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    var itemVisual = ModelAssets.Create(kind, scale, source.transform);
                    ModelAssets.CenterModel(itemVisual, size);
                    var collider = source.AddComponent<BoxCollider>(); collider.center = Vector3.zero; collider.size = size;
                    PlacementSetup.HandAnchors(source, new Bounds(collider.center, size), (DeviceKind)i);
                    var gadget = New(Keys[i] + "_gadget");
                    var mountedVisual = ModelAssets.Create(kind, scale, gadget.transform);
                    ModelAssets.CenterModel(mountedVisual, size);
                    var gc = gadget.AddComponent<BoxCollider>(); gc.center = collider.center; gc.size = size;
                    var authoring = gadget.AddComponent<GadgetAuthoring>();
                    authoring.kind = (DeviceKind)i; authoring.boundsCenter = collider.center; authoring.boundsSize = size;
                    authoring.highlightMeshes = ModelAssets.CreatePreview(kind, scale, gadget.transform, false).GetComponentsInChildren<MeshFilter>(true);
                    authoring.removalMethod = custom_item_components.GadgetBase.GadgetRemovalMethod.Remover;
                    authoring.requiredMountPoints = 2;
                    if (i == 2)
                    {
                        AddButton(mountedVisual);
                        var cover = mountedVisual.GetComponentsInChildren<MeshFilter>(true).First(m => m.name == "Front_Cover_With_Printed_Face");
                        float front = (cover.transform.localPosition.z + cover.sharedMesh.bounds.min.z) * scale - size.z / 2;
                        float rear = size.z / 2;
                        gc.center = new Vector3(0, 0, (front + rear) / 2);
                        gc.size = new Vector3(size.x, size.y, rear - front);
                    }
                    source.AddComponent<custom_item_components.GadgetItem>().gadget = authoring;
                    var shelf = New(Keys[i] + "_shelf");
                    var shelfVisual = ModelAssets.Create(kind, scale, shelf.transform);
                    ModelAssets.CenterModel(shelfVisual, size);
                    // Explicit shelf source has no gadget/item/button authoring markers.
                    Vector3 shelfBounds = PlacementSetup.LayOnShelf(shelfVisual, size, (DeviceKind)i);
                    shelf.SetActive(true);
                    var info = new CustomItemInfo { Name = Keys[i], Description = Texts.Description(i, Texts.Russian), Price = Prices[i], Amount = 20,
                        ShelfBounds = shelfBounds, PreviewRotation = new Vector3(90, 0, 0) };
                    // Custom Item Mod adds the native ShopRestocker. Let the real
                    // dumpster/RespawnOnDrop and purchase/save marker own disposal.
                    var item = new CustomItem(info, source, ModelAssets.Icon(kind, false), ModelAssets.Icon(kind, true), shelf, immuneToDumpster: false);
                    item.ShopData.shelfItem.height = shelfBounds.z;
                    var nativeGadgetItem = item.ItemPrefab.GetComponent<DV.Customization.Gadgets.GadgetItem>();
                    // Same rear-face mounting rule as the native flat gadgets.
                    Main.Set(nativeGadgetItem, "onlyPlaceInOneAxis", true);
                    Main.Set(nativeGadgetItem, "placingAxis", Vector3.back);
                    Main.Set(nativeGadgetItem, "placingUp", Vector3.up);
                    item.Name = Texts.Name(i, Texts.Russian); // IDs/localization keys were generated from the permanent ASCII key.
                    item.ItemPrefab.transform.parent.SetParent(registrationRoot.transform, false);
                    var spec = item.ItemPrefab.GetComponent<Item>();
                    spec.rigidbodyMass = i == 0 ? 3f : 1.5f;
                    spec.precisionGrab = false;
                    spec.itemCollisionSoundCategory = ItemCollisionSoundCategory.Generic;
                    item.ItemSpec.PreviewBounds = new Bounds(collider.center, size);
                    item.ItemSpec.PreviewPrefab = ModelAssets.CreatePreview(kind, scale, registrationRoot.transform, true);
                    item.ItemPrefab.SetActive(true);
                    gadget.SetActive(true);
                    ItemModsFinder.CustomItems.Add(item); Main.Items.Add(item);
                    Main.Log(Texts.Pick("Добавлен предмет: ", "Item added: ") + item.Name + " | " + item.ItemSpec.ItemPrefabName + " | $" + item.ShopData.basePrice);
                }
            }
            catch (Exception ex) { Main.ErrorOnce("item-registration", ex); }
        }

        private static GameObject New(string name)
        {
            var go = new GameObject(name); go.SetActive(false); go.transform.SetParent(registrationRoot.transform, false); return go;
        }
        private static void AddButton(GameObject visual)
        {
            var transform = visual.GetComponentsInChildren<Transform>(true).First(t => t.name == "Large_Red_Button_Cap");
            var go = transform.gameObject; go.SetActive(false);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            var collider = go.AddComponent<BoxCollider>(); collider.center = mesh.bounds.center; collider.size = mesh.bounds.size;
            var button = go.AddComponent<Button>(); button.colliderGameObjects = new[] { go };
            button.press = NativeAssets.Button;
            button.pushLocalOffset = new Vector3(0, 0, .012f);
            button.createRigidbody = false; button.useJoints = false;
            button.handPosesOverride = new InteractionHandPoses();
            var highlight = go.AddComponent<HighlightTag>(); highlight.renderers = new List<Renderer> { go.GetComponent<Renderer>() };
            go.SetActive(true);
        }
    }
}
