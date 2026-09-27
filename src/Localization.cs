using System;
using System.Reflection;
using DV.Shops;
using HarmonyLib;
using I2.Loc;
using UnityEngine;

namespace RailwaySafetyGadgets
{
    internal static class Texts
    {
        private static bool installed;
        // The public getters call InitializeIfNeeded -> I2PrefReroute.Get,
        // which can run before UserManager.CurrentUser exists (observed log).
        // These verified backing fields are read-only snapshots of the language
        // already selected by I2. Missing/uninitialized data safely selects EN.
        private static readonly FieldInfo LanguageCode = typeof(LocalizationManager).GetField("mLanguageCode", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo LanguageName = typeof(LocalizationManager).GetField("mCurrentLanguage", BindingFlags.Static | BindingFlags.NonPublic);
        internal static bool IsRussian(string language)
        {
            return !string.IsNullOrEmpty(language) && (language.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                language.StartsWith("ru-", StringComparison.OrdinalIgnoreCase) || language.StartsWith("Russian", StringComparison.OrdinalIgnoreCase));
        }
        internal static bool Russian
        {
            get
            {
                string code = LanguageCode?.GetValue(null) as string;
                return !string.IsNullOrEmpty(code) ? IsRussian(code) : IsRussian(LanguageName?.GetValue(null) as string);
            }
        }
        internal static string Pick(string ru, string en) { return Russian ? ru : en; }
        internal static readonly string[] NamesRu = { "Локомотивный светофор", "Ограничитель скорости", "Тормозной блок" };
        internal static readonly string[] NamesEn = { "Locomotive Signal Repeater", "Speed Limiter", "Automatic Brake Unit" };
        internal static readonly string[] DescriptionsRu = {
            "Повторяет показания подходящего сигнала по выбранному маршруту. Запрещающий сигнал — красный; подтверждённый въезд по резервации до 20 км/ч — красно-жёлтый. Белый означает разрешающий маневровый сигнал либо отсутствие данных; отсутствие данных не считается разрешением. Обычный поворотный переключатель: 0 — нормальный режим, 1 — маневровый; питание не меняется. Без него действует нормальный режим. Старые семафоры учитываются по настройке; предупредительные и повторительные сигналы исключены. Внешняя кнопка и рычажный выключатель управляют питанием. Чередующийся контроллер задаёт яркость 0/25/50/75/100%.",
            "Показывает скорости в км/ч: следующее ограничение сверху, допустимую скорость снизу. Средний экран показывает метры до той же цели: понижение — до входа головы, повышение — до выхода хвоста. Расстояние округляется вниз с шагом 1/5/10/50/100 м по скорости поезда. ---- означает отсутствие актуальной цели; HI — более 9999 м. Допустимая скорость отображается с фиксированным шагом 1 км/ч. При временной потере данных сохраняется ранее подтверждённое более строгое ограничение. В обычном режиме показывается ограничение всего состава. Учитывает направление движения и выбранный путь через стрелки. Работает и без видимых знаков. Начало активной тормозной кривой сопровождается одним сигналом; изменения цифр не повторяют звук. Прочерки означают, что ограничение неизвестно. Обычный поворотный переключатель: 0 — обычный режим, 1 — расчёт тормозной кривой. В обоих режимах учитывается весь состав. Без переключателя действует обычный режим. Кнопка и рычажный выключатель управляют питанием. Чередующийся контроллер задаёт яркость 0/25/50/75/100%.",
            "Красный АЛС показывается заранее; полный поездной тормоз включается немедленно только при подтверждённом неразрешённом проезде сигнала. Остановка перед красным не вызывает это торможение. Незначительное превышение допустимой скорости с учётом допуска вызывает короткий сигнал, красная лампа мигает; серьёзное превышение тормозит немедленно. Допуск, критический порог и время предупреждения настраиваются. После торможения лампа горит постоянно. Красная кнопка снимает удержание и звук; затем отпустите тормоз рукояткой. Один проезд не срабатывает повторно, даже если красный сохраняется. Поворотный переключатель: 0 — АЛС + ограничитель скорости, 1 — только АЛС. Без него включены обе защиты. Отключение скоростной реакции снимает только её причину удержания, сохраняя удержание за проезд сигнала; тормозную рукоятку отпускает машинист. Кнопка и рычажный выключатель управляют питанием блока. Чередующийся контроллер задаёт яркость 0/25/50/75/100%; положение 0 запрещает новые вмешательства и гасит индикацию, сохраняя уже сработавший тормоз. Назначенная клавиша нажимает красную кнопку сброса, в том числе при подключённом переключателе. Отключение блока не снимает уже сработавшее удержание тормоза." };
        internal static readonly string[] DescriptionsEn = {
            "Repeats the eligible signal on the selected route. Stop is red; confirmed reserved entry at up to 20 km/h is red-yellow. White indicates shunting clear or unavailable data; unavailable data is not permission. The standard rotary switch selects 0 normal / 1 shunting without changing power. Without it, normal mode applies. Old semaphores are optional; distant and repeater signals are excluded. An external button or lever switch controls power. An alternating controller sets 0/25/50/75/100% brightness.",
            "Shows speeds in km/h: the next restriction above and permitted speed below. The middle display shows metres to the same restriction: head entry for a lower limit, tail clearance for a higher limit. Distance rounds down in 1/5/10/50/100 m steps according to train speed. ---- means no known target; HI means over 9999 m. Permitted speed uses a fixed 1 km/h display step. If data is temporarily unavailable, a previously confirmed stricter limit is retained. Normal mode shows the whole-train limit. Follows your direction of travel and the selected route through switches. Works without visible signs. An active braking curve starts with one warning; changing digits do not repeat the sound. Dashes mean the limit is unknown. The standard rotary switch selects 0 normal / 1 Braking Curve Control. Both modes supervise the whole train. Without the rotary, normal mode applies. A button or lever switch controls power. An alternating controller sets 0/25/50/75/100% brightness.",
            "A red cab indication is shown in advance; the full train brake applies immediately only after a confirmed unauthorized signal crossing. Stopping before red does not trigger this intervention. Minor excess above the permitted speed plus tolerance gives one short warning and a flashing red lamp; critical excess brakes immediately. Tolerance, critical threshold and warning time are configurable. After braking the lamp stays on. The red button silences and unlocks the brake; then release it with the handle. One crossing does not retrigger while red remains. The rotary switch selects 0 cab signal + speed limiter / 1 cab signal only. Without it both protections apply. Disabling speed supervision removes only its own hold reason, preserving a signal-passage hold; the driver releases the brake handle. A button or lever switch controls unit power. An alternating controller sets 0/25/50/75/100% brightness; position 0 disables new interventions and extinguishes the lamps while preserving a latched brake. The assigned key presses the red reset button, including when a switch is wired. Disabling the unit does not release an existing brake latch." };
        internal static string Name(int i, bool russian) { return (russian ? NamesRu : NamesEn)[i]; }
        internal static string Description(int i, bool russian)
        {
            return (russian ? DescriptionsRu : DescriptionsEn)[i] + (russian
                ? " Для утилизации снимите гаджет и оставьте его в мусорном контейнере. Штатное удаление происходит после отхода от контейнера; купленный экземпляр возвращается в доступный запас магазина без возврата денег. До удаления предмет можно забрать обратно."
                : " To dispose of the gadget, remove it and leave it in a dumpster. The game deletes it after you move away; a purchased unit returns to available shop stock without a money refund. You can retrieve it before deletion.");
        }

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
            LocalizedPanels.Refresh(Russian);
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
