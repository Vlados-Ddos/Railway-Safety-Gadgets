# Railway Safety Gadgets

## Description

Railway Safety Gadgets adds three functional cab devices to Derail Valley: a Locomotive Signal Repeater, a Speed Limiter, and an Automatic Brake Unit.

The devices can be purchased from regular shops and mounted inside a locomotive cab. Used together, they provide signal indications, route speed limit information, and automatic braking.

### Support

Enjoy my Derail Valley mods? You can support my work on Ko-fi!

[**Support me on Ko-fi**](https://ko-fi.com/7vlad7)

## Installation

- Install Unity Mod Manager.
- Install [Custom Item Mod](https://www.nexusmods.com/derailvalley/mods/891), [DV Signals](https://www.nexusmods.com/derailvalley/mods/1636), and its dependency, [DV Language Helper](https://www.nexusmods.com/derailvalley/mods/823).

## Main Features

- Three purchasable cab gadgets with physical controls, individual icons, native mounting, wiring, power and saved state.
- Route, signal and speed readings follow the confirmed direction selected by the locomotive reverser and the selected switch route, including reverse movement and long consists.
- The Locomotive Signal Repeater, Speed Limiter and Automatic Brake Unit share the same current route data, while signal protection and speed protection remain separate causes.
- DV Signals is optional. When it is available, the signal repeater uses its real aspects, reservations and block state; without it, ALS shows WHITE and does not create signal braking. Speed limits, Brake Curve Control, overspeed protection and the other gadgets continue to work.
- Whole-train speed supervision accounts for the relevant end of the consist: a reduction applies when the head enters the slower zone, while an increase applies after the tail clears it.
- The permitted-speed display uses a fixed 1 km/h step. The distance display is separate and uses speed-dependent metre bands, with hysteresis, dashes for unknown data and HI for values beyond four digits.
- Physical reset buttons, assigned keys, native switches and alternating controllers use the same interaction paths as the game; keyboard reset follows the game's keyboard-control and reach permissions.
- Russian and English localization is selected automatically, including separate panel textures and gadget descriptions. Each device has its own warning volume and brightness control.
- Native shop, save, retrieval and disposal behavior is preserved. Removing a purchased gadget and leaving it in the native dumpster returns stock only after the normal game disposal process; it does not refund money.

## Gadgets

The images below are the project's existing inventory renders. Printed legends are part of the original models.

### Locomotive Signal Repeater
<img width="384" height="384" alt="icon" src="https://github.com/user-attachments/assets/e8216232-a6be-4efb-b223-14c4c606a1ed" />

Displays the cab indication supplied by the active signal provider and the selected route.

- GREEN, YELLOW, YELLOW-RED, RED, GREEN-YELLOW and WHITE indications, plus confirmed flashing combinations where the native aspect supports them. Both physical faces show the same state.
- A button or lever switch wired to the repeater controls its power. The two-position rotary switch selects normal or shunting mode. The alternating controller sets brightness from 0% to 100%; it does not change the signal mode. Old semaphore signals can be included from the mod setting and are disabled by default.
- The confirmed reverser direction is kept through rollback and handle changes while moving; a new direction is accepted only after the train has stopped and departed that way.
- The device ignores the player's own consist when checking occupied blocks. Reservations and restricted entry are read from the selected route and do not turn a simple red lamp into a false permission.
- WHITE means that no reliable cab-signal data is available. It is non-protective when DV Signals is absent or unavailable, and power loss extinguishes the lamps.

RED shown ahead of a signal does not by itself apply the train brake. Protection requires the measured locomotive-end crossing of a prohibited signal in the confirmed direction. A permitted YELLOW-RED entry remains distinct from a prohibited RED.

### Speed Limiter
<img width="384" height="384" alt="icon" src="https://github.com/user-attachments/assets/14694dbb-c565-4a5d-89d5-3eef6257c254" />

Displays the next whole-train speed restriction above, the distance to that same target on the middle display, and the current permitted speed below. These are route limits, not the locomotive's actual speed.

- Follows the confirmed reverser direction and selected branch, including reverse movement, rollback and direction changes after a stop.
- Uses native speed profiles and the active Double Track provider when available. It can determine limits without visible signs; unknown values appear as dashes.
- A lower restriction is paired with the train-head entry point. A higher restriction is paired with the last-carriage clearance point, so long consists remain protected until the whole train is clear.
- A button or lever switch wired to the limiter controls its power. The rotary switch selects normal whole-train supervision or Brake Curve Control. The alternating controller sets brightness from 0% to 100%; at 0% the limiter is inactive. The curve uses precise Brake Curve Control data, current speed, target distance, gradient and train length; only the displayed numbers are rounded.
- The permitted-speed row is always displayed in 1 km/h steps. The distance readout uses 1/5/10/50/100 m floor bands according to train speed and does not change braking calculations.
- A lower upcoming restriction produces one warning and the existing red reduction arrow. Equal or higher limits do not create a false reduction warning.

### Automatic Brake Unit
<img width="599" height="484" alt="brake" src="https://github.com/user-attachments/assets/3915e6db-7852-4b8f-af1f-5e84758ab0f3" />

Applies the native train-brake lock when the corresponding protection cause is confirmed on the same locomotive.

- A button or lever switch wired to the unit controls its power. The native rotary switch selects combined protection from cab-signal passage and the Speed Limiter, or cab-signal protection only. Switching modes removes only the speed cause; an active signal cause remains latched.
- A measured prohibited RED crossing applies full braking immediately. Approaching a RED, stopping on its plane, rolling back or changing the aspect without crossing does not create a false intervention.
- Speed above the exact permitted value starts a 10-second audible warning and flashing red lamp. Critical excess can apply braking immediately; continued ordinary excess applies braking after the warning. Equality is safe, and the NEXT restriction is not enforced as the current limit.
- If the driver is already applying effective train or dynamic braking, the warning may receive a short, verified extension; critical excess does not wait.
- The red button and its assigned key call the physical button action, including the native press animation and sound. The button acknowledges the incident and unlocks the brake; the driver releases the train brake with the native handle.
- The built-in red button and its assigned key acknowledge the brake incident, silence its alarm and unlock the brake; they do not release the native train-brake handle. The alternating controller sets brightness from 0% to 100%; at 0% it prevents new protection interventions and extinguishes the lamps while an existing brake hold remains. Signal-passage and speed-excess causes are saved independently. Acknowledging one cause does not clear the other, and a persistent speed excess can warn again after recovery.

### Controls, mounting and saved items

- Buttons and lever switches control device power through native wiring. The alternating controller sets 0/25/50/75/100% brightness.

## Compatibility

- [Career Rework](https://www.nexusmods.com/derailvalley/mods/1153): Fully compatible.
- [Shop Rework](https://www.nexusmods.com/derailvalley/mods/1200): Fully compatible.
- [Double Track](https://www.nexusmods.com/derailvalley/mods/1487): Fully compatible.
- Multiplayer: Not currently supported. Compatibility is planned for a future update once item and gadget synchronization is implemented in the multiplayer mod.

---

# Railway Safety Gadgets — Русский

## Описание

Railway Safety Gadgets добавляет в Derail Valley три функциональных устройства для кабины локомотива: повторитель локомотивной сигнализации (Locomotive Signal Repeater), ограничитель скорости (Speed Limiter) и блок автоматического торможения (Automatic Brake Unit).

Устройства можно приобрести в обычных магазинах и установить в кабине локомотива. При совместном использовании они отображают показания локомотивной сигнализации и маршрутные ограничения скорости, а также обеспечивают автоматическое торможение.

### Поддержка

Нравятся мои моды для Derail Valley? Вы можете поддержать мою работу на Ko-fi!

[**Поддержать меня на Ko-fi**](https://ko-fi.com/7vlad7)

## Установка

- Установите Unity Mod Manager.
- Установите [Custom Item Mod](https://www.nexusmods.com/derailvalley/mods/891), [DV Signals](https://www.nexusmods.com/derailvalley/mods/1636), а также необходимую для него зависимость — [DV Language Helper](https://www.nexusmods.com/derailvalley/mods/823).

## Основные возможности

- Три доступных для покупки устройства кабины с физическими органами управления, собственными значками, штатной установкой, подключением, питанием и сохранением состояния.
- Показания маршрута, сигналов и ограничений скорости используют подтверждённое направление выбранного реверса и выбранную стрелочную ветвь, в том числе при движении назад и с длинным составом.
- Повторитель локомотивной сигнализации, ограничитель скорости и блок автоматического торможения используют одни и те же данные текущего маршрута, но причины торможения по сигналу и по скорости остаются независимыми.
- DV Signals является необязательным. При наличии мода повторитель использует реальные аспекты, резервирование и состояние блоков; без него АЛС показывает БЕЛЫЙ и не вызывает торможение по сигналу. Ограничитель скорости, система тормозной кривой, защита от превышения и остальные устройства продолжают работать.
- Ограничения рассчитываются для всего состава: при снижении применяется момент входа головы в более медленную зону, а при повышении — момент выхода последнего вагона из неё.
- Допустимая скорость отображается с фиксированным шагом 1 км/ч. Расстояние выводится отдельно, с динамическими шагами в метрах, гистерезисом, прочерком при неизвестных данных и HI при переполнении четырёх цифр.
- Физические кнопки, назначенные клавиши, штатные переключатели и чередующийся переключатель используют те же пути взаимодействия, что и игра; клавиатурный сброс подчиняется настройкам управления и проверки доступности игры.
- Русская и английская локализация выбираются автоматически, включая отдельные текстуры панелей и описания устройств. Для каждого устройства доступны отдельные громкость предупреждений и управление яркостью.
- Штатные покупка, сохранение, возврат и утилизация сохранены. После снятия купленного устройства его помещение в штатный контейнер возвращает единицу товара только после обычной процедуры игры; деньги не возвращаются.

## Устройства

Ниже представлены исходные изображения устройств из инвентаря проекта. Надписи на моделях являются частью оригинального оформления.

### Locomotive Signal Repeater — Повторитель локомотивной сигнализации
<img width="384" height="384" alt="icon" src="https://github.com/user-attachments/assets/2f82a368-528c-4f95-ad6e-5055e0bee806" />

Отображает показание кабины, полученное от активного источника сигналов для выбранного маршрута.

- Показания «ЗЕЛЁНЫЙ», «ЖЁЛТЫЙ», «ЖЁЛТО-КРАСНЫЙ», «КРАСНЫЙ», «ЗЕЛЁНО-ЖЁЛТЫЙ» и «БЕЛЫЙ», а также подтверждённые мигающие комбинации, если их поддерживает штатный аспект. Обе стороны устройства показывают одно состояние.
- Внешняя кнопка или рычажный переключатель, подключённые к повторителю, управляют его питанием. Двухпозиционный поворотный переключатель выбирает нормальный или маневровый режим. Чередующийся переключатель задаёт яркость от 0% до 100% и не меняет режим сигнализации. Учёт старых семафоров включается настройкой мода и по умолчанию выключен.
- Подтверждённое направление реверса сохраняется при откате и изменении рукоятки во время движения; новое направление принимается только после остановки и начала движения в эту сторону.
- Собственный состав исключается из проверки занятости блоков. Резервирование и ограниченный въезд читаются по выбранному маршруту и не превращают один красный огонь в ложное разрешение.
- Показание «БЕЛЫЙ» означает отсутствие надёжных данных о показании. При отсутствии или недоступности DV Signals оно не является защитным состоянием, а при отключении питания лампы гаснут.

Показание «КРАСНЫЙ», отображаемое перед светофором, само по себе не включает тормоз. Защита требует фактического пересечения запрещающего сигнала концом локомотива в подтверждённом направлении. Разрешающий вход «ЖЁЛТО-КРАСНЫЙ» остаётся отдельным от запрещающего показания «КРАСНЫЙ».

### Speed Limiter — Ограничитель скорости
<img width="384" height="384" alt="icon" src="https://github.com/user-attachments/assets/5c469cbb-38f3-4db7-a805-1710a0cc367b" />

Сверху показывает следующее ограничение всего состава, на среднем дисплее — расстояние до той же цели, а снизу — текущую допустимую скорость. Это маршрутные ограничения, а не фактическая скорость локомотива.

- Учитывает подтверждённое направление реверса и выбранную ветвь маршрута, включая движение назад, откат и смену направления после остановки.
- Использует штатные профили скорости и активный источник Double Track, если он доступен. Ограничение может определяться без видимого знака; неизвестные данные показываются прочерком.
- Для понижения учитывается вход головы состава в медленную зону. Для повышения учитывается выход последнего вагона, поэтому длинный состав не освобождается раньше времени.
- Внешняя кнопка или рычажный переключатель, подключённые к ограничителю, управляют его питанием. Поворотный переключатель выбирает обычный режим или Brake Curve Control. Чередующийся переключатель задаёт яркость от 0% до 100%; при 0% ограничитель не работает. Кривая использует точные данные Brake Curve Control, скорость, расстояние до цели, уклон и длину состава; округляются только цифры на дисплее.
- Нижняя строка допустимой скорости всегда использует шаг 1 км/ч. Расстояние округляется вниз отдельными шагами 1/5/10/50/100 м в зависимости от скорости и не влияет на тормозной расчёт.
- При следующем снижении ограничения один раз включаются предупреждение и существующая красная стрелка. Равное или более высокое ограничение не создаёт ложного предупреждения.

### Automatic Brake Unit — Блок автоматического торможения
<img width="599" height="484" alt="brake" src="https://github.com/user-attachments/assets/09307a7c-2d7a-4541-a2f9-e477633de87b" />

Включает штатную блокировку поездного тормоза после подтверждения соответствующей причины на том же локомотиве.

- Внешняя кнопка или рычажный переключатель, подключённые к блоку, управляют его питанием. Поворотный переключатель выбирает совместную защиту по пересечению запрещающего сигнала и по ограничителю скорости либо только защиту по сигналу. Смена режима снимает только причину по скорости; активная причина по сигналу сохраняется.
- После фактического пересечения запрещающего показания «КРАСНЫЙ» включается полное торможение. Приближение к показанию «КРАСНЫЙ», остановка на его плоскости, откат или смена аспекта без пересечения не вызывают ложного срабатывания.
- Превышение точной допустимой скорости запускает десятисекундное звуковое предупреждение и мигание красной лампы. Критическое превышение может включить тормоз сразу; обычное продолжающееся превышение — после предупреждения. Равенство безопасно, СЛЕДУЮЩЕЕ не используется как текущее ограничение.
- Если машинист уже эффективно использует поездной или динамический тормоз, предупреждение может получить короткое подтверждённое продление; при критическом превышении ожидания нет.
- Красная кнопка и назначенная клавиша вызывают физическое нажатие кнопки, включая штатную анимацию и звук. Кнопка подтверждает событие и снимает блокировку; поездной тормоз отпускается штатной рукояткой.
- Встроенная красная кнопка и назначенная клавиша подтверждают тормозное событие, отключают сигнал тревоги и снимают блокировку; они не отпускают штатную тормозную рукоятку. Чередующийся переключатель задаёт яркость от 0% до 100%; при 0% новые защитные вмешательства запрещены и лампы гаснут, но уже сработавшее удержание сохраняется. Причины по сигналу и по превышению сохраняются раздельно. Подтверждение одной причины не снимает другую, а повторное превышение после восстановления может снова запустить предупреждение.

### Управление, установка и сохранение устройств

- Кнопки и рычажные переключатели управляют питанием через штатное подключение. Чередующийся переключатель задаёт яркость 0/25/50/75/100%.

## Совместимость

- [Career Rework](https://www.nexusmods.com/derailvalley/mods/1153): полная совместимость.
- [Shop Rework](https://www.nexusmods.com/derailvalley/mods/1200): полная совместимость.
- [Double Track](https://www.nexusmods.com/derailvalley/mods/1487): полная совместимость.
- Мультиплеер: пока не поддерживается. Совместимость планируется добавить в одном из будущих обновлений после реализации синхронизации предметов и устройств в мультиплеерном моде.
