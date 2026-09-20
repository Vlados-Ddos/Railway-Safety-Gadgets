using System;
using DV.Shops;
using HarmonyLib;
using I2.Loc;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    internal static class Texts
    {
        private static bool installed;
        internal static bool IsRussian(string language)
        {
            return !string.IsNullOrEmpty(language) && (language.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) || language.StartsWith("Russian", StringComparison.OrdinalIgnoreCase));
        }
        internal static bool Russian { get { return IsRussian(LocalizationManager.CurrentLanguageCode) || IsRussian(LocalizationManager.CurrentLanguage); } }
        internal static string Pick(string ru, string en) { return Russian ? ru : en; }
        internal static readonly string[] NamesRu = { "Локомотивный светофор", "Ограничитель скорости", "Тормозной блок" };
        internal static readonly string[] NamesEn = { "Locomotive Signal Repeater", "Speed Limiter", "Automatic Brake Unit" };
        internal static readonly string[] DescriptionsRu = {
            "Показывает состояние блоков по выбранному маршруту. Жёлто-красный — впереди запрещающий сигнал или занятый блок. Красный — текущий блок занят. Белый — нет сигнала АЛС.",
            "Показывает ограничения скорости в км/ч: следующее сверху, текущее снизу. Учитывает направление движения и выбранный путь через стрелки. Работает и без видимых знаков. Прочерки означают, что ограничение неизвестно.",
            "При красном сигнале АЛС немедленно включает полный поездной тормоз и блокирует рукоятку. Если скорость выше текущего лимита работающего Ограничителя скорости в том же локомотиве, звучит предупреждение и мигает красная лампа. Снизьте скорость за 10 секунд, иначе включится тормоз, а лампа загорится постоянно. Контроль скорости можно отключить в настройках. Красная кнопка выключает предупреждение и звук, снимает удержание тормоза. После этого отпустите тормоз рукояткой. Один непрерывный красный сигнал повторно не срабатывает. Если скорость всё ещё выше лимита, начнутся новые 10 секунд предупреждения." };
        internal static readonly string[] DescriptionsEn = {
            "Shows block conditions along the selected route. Yellow-red means a stop signal or occupied block ahead. Red means the current block is occupied. White means no cab signal is being received.",
            "Shows speed limits in km/h: next at the top, current at the bottom. Follows your direction of travel and the selected route through switches. Works without visible signs. Dashes mean the limit is unknown.",
            "A red cab signal immediately applies the full train brake and locks the handle. Exceeding the current limit from a working Speed Limiter in the same locomotive triggers a warning sound and a flashing red lamp. Slow down within 10 seconds to avoid braking. If you are still over the limit, the brake applies and the lamp stays on. Speed checks can be disabled in settings. The red button silences the warning and unlocks the brake. Then release the brake with the handle. The same continuous red signal will not trigger braking again. If you are still over the speed limit, a new 10-second warning begins." };
        internal static string Name(int i, bool russian) { return (russian ? NamesRu : NamesEn)[i]; }
        internal static string Description(int i, bool russian) { return (russian ? DescriptionsRu : DescriptionsEn)[i]; }

        internal static void Install(Harmony harmony)
        {
            if (installed) return;
            // Custom Item Mod supplies the permanent terms. A postfix translates
            // only our six terms, including requests with overrideLanguage.
            harmony.Patch(AccessTools.Method(typeof(LocalizationManager), nameof(LocalizationManager.GetTranslation)),
                postfix: new HarmonyMethod(typeof(Texts), nameof(Translated)) { priority = Priority.Last });
            LocalizationManager.OnLocalizeEvent += Refresh;
            installed = true;
        }
        private static void Translated(string Term, string overrideLanguage, ref string __result)
        {
            if (string.IsNullOrEmpty(Term)) return;
            bool russian = string.IsNullOrEmpty(overrideLanguage) ? Russian : IsRussian(overrideLanguage);
            for (int i = 0; i < Main.Items.Count; i++)
            {
                var spec = Main.Items[i].ItemSpec;
                if (Term == spec.localizationKeyName) { __result = Name(i, russian); return; }
                if (Term == spec.localizationKeyDescription) { __result = Description(i, russian); return; }
            }
        }
        internal static void Refresh()
        {
            for (int i = 0; i < Main.Items.Count; i++)
            {
                Main.Items[i].Name = Name(i, Russian);
                Main.Items[i].Description = Description(i, Russian);
            }
            // The native shelf labels cache strings rather than I2 Localize.
            if (UnloadWatcher.isUnloading || Main.Items.Count == 0) return;
            foreach (var module in UnityEngine.Object.FindObjectsOfType<ScanItemCashRegisterModule>())
                foreach (var item in Main.Items)
                    if (module.sellingItemSpec == item.ItemSpec && module.Data != null)
                    {
                        module.Data.resourceName = item.Name;
                        module.UpdateTexts();
                        break;
                    }
        }
    }
}
