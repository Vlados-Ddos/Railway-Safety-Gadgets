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
        // The native item API shares this short description across inventory and shop tooltips.
        private static readonly string[] InventoryDescriptionsRu = {
            "Повторяет показания сигналов по выбранному маршруту.",
            "Показывает допустимую скорость для всего состава и следующее ограничение.",
            "Применяет поездной тормоз при превышении допустимой скорости или запрещённом проезде сигнала."
        };
        private static readonly string[] InventoryDescriptionsEn = {
            "Repeats signal indications along the selected route.",
            "Displays the permitted speed for the whole train and the next speed limit.",
            "Applies the train brake for overspeed or an unauthorized signal crossing."
        };
        private static readonly string[] DetailsRu = {
            "Повторяет показания подходящего сигнала по выбранному маршруту. Поворотный переключатель: 0 — нормальный режим, 1 — маневровый. Без переключателя действует нормальный режим. Семафоры учитываются по отдельной настройке; предупредительные и повторительные сигналы не выбираются. Запрещающий сигнал показывается красным; разрешённый въезд по зарезервированному маршруту до 20 км/ч — красно-жёлтым. Белый означает разрешающий маневровый сигнал либо отсутствие данных; отсутствие данных не является разрешением на проезд. Само появление красного показания не включает тормоз: защита АЛС реагирует на подтверждённый запрещённый проезд.",
            "Верхний экран показывает следующее ограничение, нижний — допустимую скорость, средний — расстояние в метрах. Понижение применяется при входе головы, повышение — только после выхода хвоста; ограничения учитываются на всём занятом составом участке. Поворотный переключатель: 0 — обычный режим, 1 — Управление тормозной кривой. Без переключателя действует обычный режим. Включённая тормозная кривая учитывает необходимые снижения впереди, даже если ближайшее из них менее строгое. Верхний экран при этом сохраняет ближайшую цель. Нижний экран округляет допустимую скорость вниз до 1 км/ч; защита использует точное значение. Расстояние округляется вниз с шагом 1/5/10/50/100 м в зависимости от скорости. Прочерки означают отсутствие достоверных данных, HI — расстояние более 9999 м. Начало активной кривой сопровождается одним звуковым сигналом; смена цифр не повторяет его. При временной потере данных сохраняется ранее подтверждённое более строгое ограничение.",
            "При превышении допустимой скорости с учётом допуска звучит короткое предупреждение и мигает красная лампа. Если превышение сохраняется до окончания заданного времени, включается полный поездной тормоз. Критическое превышение и подтверждённый запрещённый проезд сигнала вызывают немедленное торможение. Остановка перед красным сигналом не считается проездом. Поворотный переключатель: 0 — АЛС и ограничитель скорости, 1 — только АЛС. Без переключателя работают обе защиты. Отключение скоростной реакции снимает только её причину удержания, сохраняя удержание за проезд сигнала. Красная кнопка снимает удержание и звук; затем машинист отпускает тормоз рукояткой. Назначенная клавиша нажимает красную кнопку сброса с её анимацией и подчиняется настройке игры «Управление с клавиатуры». Отключение питания не снимает уже сработавшее удержание тормоза."
        };
        private static readonly string[] DetailsEn = {
            "Repeats the eligible signal on the selected route. The rotary Switch selects 0 normal / 1 shunting; normal mode applies without a Switch. Semaphores are optional; distant and repeater signals are excluded. A stop indication is red; authorized entry along a reserved route at up to 20 km/h is red-yellow. White indicates a clear shunting signal or unavailable data; missing data never grants permission to pass. Red alone does not apply the brake: signal protection requires a confirmed unauthorized crossing.",
            "The upper display shows the next speed limit, the lower display the permitted speed, and the middle display the distance in metres. A reduction applies at head entry; an increase waits for tail clearance. Limits cover the entire occupied route. The rotary Switch selects 0 normal / 1 Brake Curve Control; normal mode applies without a Switch. The curve considers necessary reductions farther ahead even when the nearest limit is less restrictive. The upper display still shows the nearest target. Permitted speed rounds down to 1 km/h on the display; protection uses the exact value. Distance rounds down in 1/5/10/50/100 m steps according to speed. Dashes mean no reliable data; HI means over 9999 m. Curve activation gives one warning sound, without repeating it for every digit change. Temporary data loss retains a previously confirmed stricter limit.",
            "Overspeed beyond the tolerance starts a short warning and a flashing red lamp. If the excess remains until the configured warning time expires, the full train brake applies. Critical overspeed and a confirmed unauthorized signal crossing brake immediately. Stopping before a red signal is not a crossing. The rotary Switch selects 0 signal and speed protection / 1 signal protection only; both apply without a Switch. Disabling speed supervision removes only its own hold reason, preserving a signal-passage hold. The red button silences and unlocks the brake; then release the handle manually. The assigned key presses the red reset button with its animation and follows the game's Keyboard Controls setting. Switching off power does not release an existing brake hold."
        };
        private static readonly string[] HelpRu = {
            "Питание и яркость. Каждый гаджет принимает переключатель режима и один отдельный источник питания или яркости. Кнопка и рычажный выключатель включают или выключают гаджет. Чередующийся контроллер задаёт яркость 0/25/50/75/100%; аналоговый контроллер плавно задаёт 0–100%. Кнопка, чередующийся и аналоговый контроллеры взаимоисключающие, но каждый работает одновременно с переключателем режима. Без источника яркости используется 100%. При 0% индикация гаснет и новые защитные вмешательства запрещены; удерживаемый тормоз сбрасывается красной кнопкой.",
            "Управление тормозной кривой. Показывает, с какой скоростью можно подойти к каждому снижению впереди, если выполнять служебное торможение. Учитываются масса всех машин, доступная сила их тормозов, запас воздуха, нагрев, юз, сохраняющаяся тяга и изменение высоты состава на пути к цели. На подъёме торможение легче, на спуске труднее. Таймер предупреждения тормозного блока не сдвигает кривую. Расчёт предполагает уже развившееся тормозное усилие: машинисту нужно учитывать его нарастание, а ожидание автоматического вмешательства не гарантирует нужную скорость у знака. Повышения не создают кривую; несколько понижений учитываются вместе. После изменения состава данные обновляются. Выключение режима возвращает обычное ограничение всего состава; сигналы в расчёте кривой не участвуют.",
            "Направление. Используется подтверждённое направление, выбранное реверсом. Откат или переключение рукоятки на ходу не разворачивает поиск. Для смены направления нужно остановиться и начать устойчивое движение в выбранную противоположную сторону. Нейтраль сохраняет подтверждённое направление.",
            "DV Signals необязателен. При его наличии АЛС получает реальные показания. Без DV Signals локомотивный светофор показывает белый, а защита за проезд сигнала не срабатывает. Ограничитель скорости, Управление тормозной кривой, торможение по превышению, крепления и контроллеры продолжают работать.",
            "Утилизация. Снимите гаджет и оставьте его в мусорном контейнере. Игра удалит его после отхода от контейнера; купленный экземпляр вернётся в доступный запас магазина без возврата денег. До удаления предмет можно забрать обратно."
        };
        private static readonly string[] HelpEn = {
            "Power and brightness. Each gadget accepts a mode Switch and one separate power or brightness controller. A Button or lever switch turns it on or off. The Alternating Controller selects 0/25/50/75/100% brightness; the Analog Controller provides continuous 0–100%. Button, Alternating Controller and Analog Controller are mutually exclusive, but each can work alongside the mode Switch. Without a brightness controller the level is 100%. At 0%, the display goes dark and new protection interventions stop; an existing brake hold still requires the red reset button.",
            "Brake Curve Control. Shows the speed from which service braking can meet each lower limit ahead. It uses every vehicle's mass, available brake force, air supply, heat, wheel slide, continuing traction and the train's change in height on the way to the target. Uphill travel aids braking; downhill travel opposes it. The brake unit's warning timer does not shift the curve. The calculation assumes developed brake force: allow for brake build-up, as waiting for automatic intervention does not guarantee reaching the sign at the target speed. Increases create no curve; successive reductions are considered together. Train data updates when the consist changes. Switching the mode off restores the normal whole-train limit; signals do not enter the curve calculation.",
            "Direction. Lookups use the confirmed direction selected by the reverser. Rollback or moving the lever while travelling does not reverse the lookup. Stop and begin sustained movement in the selected opposite direction to confirm a reversal. Neutral preserves the confirmed direction.",
            "DV Signals is optional. With it, signal protection receives real aspects. Without DV Signals, the locomotive repeater shows white and signal-passage protection stays inactive. Speed limits, Brake Curve Control, overspeed braking, Mounts and controllers continue to work.",
            "Disposal. Remove the gadget and leave it in a dumpster. The game deletes it after you move away; a purchased unit returns to available shop stock without a money refund. You can retrieve it before deletion."
        };
        internal static string Name(int i, bool russian) { return (russian ? NamesRu : NamesEn)[i]; }
        internal static string Description(int i, bool russian) { return (russian ? InventoryDescriptionsRu : InventoryDescriptionsEn)[i]; }
        internal static string DetailedDescription(int i, bool russian) { return (russian ? DetailsRu : DetailsEn)[i]; }
        internal static string[] SettingsHelp(bool russian) { return russian ? HelpRu : HelpEn; }

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

