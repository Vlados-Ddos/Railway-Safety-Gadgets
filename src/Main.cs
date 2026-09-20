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
        public bool IncludeShuntingSignals = false;
        public bool UseSpeedLimiter = true;
        public float WarningVolume = .35f;
        public override void Save(UnityModManager.ModEntry entry) { Save(this, entry); }
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
        public static bool UseSpeedLimiter { get { return Settings == null || Settings.UseSpeedLimiter; } }

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Entry = entry; ModPath = entry.Path;
            Settings = UnityModManager.ModSettings.Load<Settings>(entry) ?? new Settings();
            Settings.WarningVolume = float.IsNaN(Settings.WarningVolume) || float.IsInfinity(Settings.WarningVolume)
                ? .35f : Mathf.Clamp01(Settings.WarningVolume);
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
            if (UnloadWatcher.isUnloading) { SpeedSigns.ClearSession(); return; }
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
            ((SafetyGadget)target).kind = ((GadgetAuthoring)source).kind;
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
            if (Errors.Add(key)) Entry.Logger.Error(key + ": " + ex);
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            GUILayout.Label(Texts.Pick("Настройки", "Options"));
            bool shunting = GUILayout.Toggle(Settings.IncludeShuntingSignals, Texts.Pick("Локомотивный светофор: учитывать маневровые сигналы", "Locomotive Signal Repeater: include shunting signals"));
            if (shunting != Settings.IncludeShuntingSignals)
            {
                Settings.IncludeShuntingSignals = shunting;
                foreach (var service in LocoService.All) if (service != null) service.DeviceStateChanged();
            }
            Settings.UseSpeedLimiter = GUILayout.Toggle(Settings.UseSpeedLimiter,
                Texts.Pick("Тормозной блок: использовать Ограничитель скорости", "Automatic Brake Unit: use Speed Limiter"));
            GUILayout.Label(Texts.Pick("Громкость предупреждений: ", "Warning volume: ") + Mathf.RoundToInt(Settings.WarningVolume * 100) + "%");
            Settings.WarningVolume = GUILayout.HorizontalSlider(Settings.WarningVolume, 0, 1);
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
                    var item = new CustomItem(info, source, ModelAssets.Icon(kind, false), ModelAssets.Icon(kind, true), shelf);
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
