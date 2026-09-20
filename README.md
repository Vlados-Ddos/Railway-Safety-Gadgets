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

- Three purchasable gadgets with physical controls, individual icons, and cab mounting.
- Route and signal readings that follow the direction of travel and the selected switch route.
- Cab signal indications and current and upcoming speed limits integrated with the Automatic Brake Unit.
- Native mounting, electrical connection, power, audio, and saved gadget state.
- Russian and English localization, with automatic language selection.
- Settings for shunting signals, speed supervision, and warning volume.

## Gadgets

The images below are the project's existing inventory renders. Printed legends are part of the original models.

### Locomotive Signal Repeater
<img width="384" height="384" alt="image" src="https://github.com/user-attachments/assets/18cd1ef1-c989-404e-b55d-1996f4f3712e" />

Displays cab signal indications using DV Signals block conditions and the selected route.

- GREEN, YELLOW, YELLOW-RED, RED, and WHITE indications.
- Additional confirmed indications: RED + FLASHING YELLOW and GREEN + FLASHING YELLOW. Both physical faces display the same state.
- Forward/reverse route selection, own-consist exclusion, and optional shunting signals.
- WHITE indicates unavailable cab signal information; power loss extinguishes the lamps.

A permitted RED + FLASHING YELLOW indication does not itself trigger braking. Confirmed RED from an occupied current block still takes priority over a reservation.

### Speed Limiter
<img width="384" height="384" alt="image" src="https://github.com/user-attachments/assets/c5aaf37b-aa83-4e6d-b9bd-08a59e44c948" />

Displays the next speed limit at the top and the current limit at the bottom, in km/h. These are route limits, not a display of the locomotive's actual speed.

- Follows travel direction and the selected branch, including reverse movement.
- Uses native speed profiles, or Double Track's provider when active.
- Can determine limits without visible signs; unknown limits appear as dashes.
- An arrow and sound warn when the next limit is lower.
- Supplies the CURRENT limit for the Automatic Brake Unit's optional speed supervision.

### Automatic Brake Unit
<img width="384" height="384" alt="image" src="https://github.com/user-attachments/assets/a07f6b29-132c-4716-b6a0-e7be2125269a" />

Applies the native train brake in response to cab RED or sustained overspeed, when the corresponding operational gadgets are fitted to the same locomotive.

- Cab RED applies full braking immediately, without a ten-second warning.
- Speed strictly above CURRENT starts a 10-second audible warning and flashing red lamp.
- Returning to the permitted speed cancels the warning; continued overspeed applies full braking and a steady red lamp. Equality is not overspeed; NEXT is not enforced.
- The red button silences the alarm and unlocks the brake; release it with the native handle.
- An acknowledged continuous RED does not retrigger; a later new RED can trigger again.
- Speed supervision can be disabled independently of cab RED protection.

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

- Три доступных для покупки устройства с физическими органами управления, собственными значками и возможностью установки в кабине.
- Показания маршрута и сигналов учитывают направление движения и выбранный маршрут на стрелочном переводе.
- Показания локомотивной сигнализации и текущие/предстоящие ограничения скорости интегрированы с блоком автоматического торможения.
- Штатные крепления и электрические соединения, питание, звуковые эффекты и сохранение состояния устройств.
- Локализация на русском и английском языках с автоматическим выбором языка.
- Настройки маневровых сигналов, контроля скорости и громкости предупреждений.

## Устройства

Ниже представлены исходные изображения устройств из инвентаря проекта. Надписи на моделях являются частью оригинального оформления.

### Locomotive Signal Repeater — Повторитель локомотивной сигнализации
<img width="384" height="384" alt="image" src="https://github.com/user-attachments/assets/bd9cc541-335a-474d-b25e-be9617f4d402" />

Отображает показания локомотивной сигнализации на основе условий блоков DV Signals и выбранного маршрута.

- Показания GREEN (зелёный), YELLOW (жёлтый), YELLOW-RED (жёлтый-красный), RED (красный) и WHITE (белый).
- Дополнительные подтверждённые показания: RED + FLASHING YELLOW (красный + мигающий жёлтый) и GREEN + FLASHING YELLOW (зелёный + мигающий жёлтый). Обе стороны устройства отображают одно и то же показание.
- Выбор маршрута при движении вперёд и назад, исключение собственного поезда из проверки и возможность отображения маневровых сигналов.
- WHITE означает, что информация о показании для кабины недоступна; при отключении питания лампы гаснут.

Разрешающее показание RED + FLASHING YELLOW само по себе не вызывает торможения. Подтверждённый RED от занятого текущего блока по-прежнему имеет приоритет над резервированием маршрута.

### Speed Limiter — Ограничитель скорости
<img width="384" height="384" alt="image" src="https://github.com/user-attachments/assets/c5a2c04b-6712-4704-88c3-e7c375d0c6f9" />

Отображает следующее ограничение скорости сверху, а текущее — снизу. Значения указаны в км/ч. Это ограничения скорости на маршруте, а не фактическая скорость локомотива.

- Учитывает направление движения и выбранную ветвь маршрута, в том числе при движении задним ходом.
- Использует штатные профили скорости или данные Double Track, если этот мод активен.
- Может определять ограничения даже при отсутствии видимых знаков; неизвестные значения отображаются прочерками.
- Стрелка и звуковой сигнал предупреждают о снижении следующего ограничения скорости.
- Передаёт значение CURRENT (текущее ограничение) в систему контроля скорости блока автоматического торможения, если она включена.

### Automatic Brake Unit — Блок автоматического торможения
<img width="384" height="384" alt="image" src="https://github.com/user-attachments/assets/39d7befe-6b72-4128-92e1-bb8ad2e37924" />

Задействует штатный поездной тормоз при красном показании локомотивной сигнализации (RED) или длительном превышении скорости, если соответствующие устройства установлены на том же локомотиве.

- Показание RED в кабине немедленно включает полное торможение — без десятисекундного предупреждения.
- Если скорость строго выше CURRENT (текущего ограничения), запускается 10-секундное звуковое предупреждение и начинает мигать красная лампа.
- Возврат к разрешённой скорости отменяет предупреждение. Если превышение продолжается, включается полное торможение, а красная лампа горит постоянно. Скорость, равная ограничению, не считается превышением; NEXT (следующее ограничение) не контролируется.
- Красная кнопка отключает звуковой сигнал и разблокирует тормоз; для растормаживания используйте штатную рукоятку.
- Подтверждённый непрерывный сигнал RED не вызывает повторного срабатывания. Новый сигнал RED может снова активировать торможение.
- Контроль скорости можно отключить независимо от защиты по красному показанию локомотивной сигнализации.

## Совместимость

- [Career Rework](https://www.nexusmods.com/derailvalley/mods/1153): полная совместимость.
- [Shop Rework](https://www.nexusmods.com/derailvalley/mods/1200): полная совместимость.
- [Double Track](https://www.nexusmods.com/derailvalley/mods/1487): полная совместимость.
- Мультиплеер: пока не поддерживается. Совместимость планируется добавить в одном из будущих обновлений после реализации синхронизации предметов и устройств в мультиплеерном моде.
