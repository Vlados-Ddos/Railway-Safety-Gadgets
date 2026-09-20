using System;
using System.Collections;
using System.Reflection;
using DV.Shops;
using HarmonyLib;

namespace RailwaySafetyGadgets
{
    internal static class ShopCompatibility
    {
        private static FieldInfo baselines;
        private static MethodInfo shopName;
        private static MethodInfo register;
        private static FieldInfo careerSettings, careerMode, careerMultiplier;
        private static bool installed;
        internal static void Install(Harmony harmony)
        {
            if (installed) return;
            if (Main.ModActive("CareerRework"))
            {
                Type career = AccessTools.TypeByName("CareerRework.Main");
                careerSettings = career == null ? null : AccessTools.Field(career, "settings");
                if (careerSettings != null)
                {
                    careerMode = AccessTools.Field(careerSettings.FieldType, "startupMode");
                    careerMultiplier = AccessTools.Field(careerSettings.FieldType, "priceMultiplierKeysGadgets");
                    if (careerMode == null || !careerMode.FieldType.IsEnum || careerMultiplier == null ||
                        careerMultiplier.FieldType != typeof(int)) careerSettings = null;
                }
                if (careerSettings == null) Main.Log(Texts.Pick("Не удалось подключить цены Career Rework. Сохранены обычные цены.",
                    "Career Rework pricing is unavailable. Standard prices are unchanged."));
            }
            if (Main.ModActive("ShopRework"))
            {
                Type type = AccessTools.TypeByName("ShopRework.ShopReworkManager");
                if (type != null)
                {
                    baselines = AccessTools.Field(type, "originalPricesByKey");
                    shopName = AccessTools.Method(type, "GetShopNameFromItem", new[] { typeof(ScanItemCashRegisterModule) });
                    register = AccessTools.Method(type, "RegisterShopItem", new[] { typeof(ScanItemCashRegisterModule) });
                }
                if (baselines == null || !typeof(IDictionary).IsAssignableFrom(baselines.FieldType) ||
                    shopName == null || shopName.ReturnType != typeof(string) || register == null)
                {
                    register = null;
                    Main.Log(Texts.Pick("Не удалось подключить скидки Shop Rework. Сохранена обычная регистрация товаров.",
                        "Shop Rework discounts are unavailable. Standard item registration is unchanged."));
                }
            }
            if (careerSettings == null && register == null) return;
            // Native InitializeData resets price from ShopItemData on every call.
            // Career Rework 1.06.0 only prices named vanilla items, not our stable
            // custom IDs. Apply its Custom multiplier once, then let Shop Rework
            // calculate discounts from that price using its own saved baselines.
            var patch = new HarmonyMethod(typeof(ShopCompatibility), nameof(Initialized))
                { after = new[] { "CareerRework" }, before = new[] { "ShopRework" } };
            harmony.Patch(AccessTools.Method(typeof(ScanItemCashRegisterModule), "InitializeData"), postfix: patch);
            installed = true;
        }
        private static void Initialized(ScanItemCashRegisterModule __instance)
        {
            if (__instance == null || __instance.sellingItemSpec == null || __instance.Data == null) return;
            foreach (var item in Main.Items)
            {
                if (__instance.sellingItemSpec.ItemPrefabName != item.ItemSpec.ItemPrefabName) continue;
                if (careerSettings != null && Main.ModActive("CareerRework"))
                {
                    var settings = careerSettings.GetValue(null);
                    if (settings != null && careerMode.GetValue(settings).ToString() == "Custom")
                    {
                        int multiplier = (int)careerMultiplier.GetValue(settings);
                        // Matches the native integer slider range. Invalid XML
                        // must not make our items free or negatively priced.
                        if (multiplier >= 1 && multiplier <= 10) __instance.Data.pricePerUnit *= multiplier;
                    }
                }
                if (register == null || !Main.ModActive("ShopRework")) return;
                string name = (string)shopName.Invoke(null, new object[] { __instance });
                if (string.IsNullOrEmpty(name) || name == "Unknown") return;
                var dictionary = (IDictionary)baselines.GetValue(null);
                if (dictionary == null) return;
                dictionary[name + "::" + __instance.name] = __instance.Data.pricePerUnit;
                register.Invoke(null, new object[] { __instance });
                return;
            }
        }
    }
}
