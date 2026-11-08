# Как авиация устроена в исследованных проектах

Основа исследования на 9 ноября 2026 года. Все описания ниже — результаты статического чтения кода. Примеры являются ручными трассами, а не результатами запуска.

## Версии и метод

| Проект | Закреплённая версия | Что изучено |
| --- | --- | --- |
| OpenRTS | `3ae082fcdc83e68b7266016152f82329a157c643` | Текущий локальный код, рабочее дерево перед исследованием чистое |
| OpenRA | `9636b63eaee62fcb60ef2e896e7985bdf2f1ee68` | Локальный checkout без изменений, Aircraft и activities, правила CnC и RA |
| Zero Hour | `0a05454d8574207440a5fb15241b98ad0b435590` | Публичный репозиторий EA, ветка main, реализация `GeneralsMD` |
| RecoilEngine | `c9d0299f7a1e0054fc2466aeb01c809aa00e2945` | Ветка master, MoveTypes и воздушные команды |
| Warzone 2100 | `253e370c2af78228a822df6afa82b6261dc94656` | Ветка master, actions, orders, droid и rearm pad |

SHA удалённых версий получены через GitHub API дерева репозитория, деревья возвращены без усечения. Выбранные файлы прочитаны из raw GitHub по этим SHA, без клонирования полных репозиториев. Временные копии находятся вне проекта в `/tmp/openrts-aircraft-research`. Внешние исходники в OpenRTS не добавлялись. Ссылки ниже закреплены на SHA, чтобы последующее изменение ветки не меняло смысл отчёта.

## OpenRA

### Устройство и реальные настройки игр

[`AircraftInfo` и `Aircraft`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/Air/Aircraft.cs#L35) задают позицию, направление, крейсерскую высоту, вертикальную скорость, поворот, посадку и резервацию. Aircraft реализует движение независимо от наземного `Mobile`. Приказ Move создаёт `Fly`, а не наземный поиск маршрута. `CanHover`, `CanSlide`, `VTOL` и поведение idle независимы.

Конкретные определения меняют ожидаемый игровой цикл.

- [CnC `^Helicopter`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/mods/cnc/rules/defaults.yaml#L279) включает зависание, боковое движение и VTOL. `airborne` переключает Ground/Vehicle на Air. Площадка `hpad` допускает ремонт.
- [CnC `HELI` и `ORCA`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/mods/cnc/rules/aircraft.yaml#L57) получают AmmoPool и `ReloadAmmoPool`. У [ReloadAmmoPool.Tick](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/ReloadAmmoPool.cs#L78) нет обязательной проверки нахождения на базе. При активном trait пополнение происходит по таймеру. Это не модель обязательного возврата после каждого боезапаса.
- В [RA aircraft.yaml](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/mods/ra/rules/aircraft.yaml#L94) MIG имеет `Rearmable` с `afld`, а HELI — с `hpad`. Общие возможности движка не означают одинаковую настройку модов.

**Применимость.** Брать разделение возможностей и команд. Правила боезапаса выбирать отдельно для OpenRTS.CnC. Не объявлять поведение текущего OpenRA CnC точным воспроизведением оригинальной игры.

### Полёт к точке и минимальный разворот

Источник — [`Fly.Tick`, `FlyTick`, `VerticalTakeOffOrLandTick`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Activities/Air/Fly.cs#L76).

1. Посаженный аппарат сначала получает дочерний TakeOff. Отмена полёта тоже учитывает текущую высоту и необходимость вернуть аппарат в допустимое состояние.
2. Цель пересчитывается с учётом видимости. При исчезновении доступен сохранённый последний известный пункт. Проверяется попадание в требуемое кольцо дальности.
3. Направление меняется с ограничением TurnSpeed. `CanSlide` позволяет независимо направить горизонтальный сдвиг, остальные аппараты двигаются вдоль текущего facing.
4. Вертикальная поправка обычного полёта зависит от высоты над terrain и ограничена допустимым углом набора. Отдельное вертикальное движение использует AltitudeVelocity.
5. При возможном перелёте через близкую цель slider/VTOL точно доводит X/Y, обычный самолёт может закончить подлёт без остановки в точке.
6. Для аппарата без бокового движения проверяется минимальный круг поворота. Если цель внутри недостижимого круга, аппарат продолжает лететь, чтобы получить возможность зайти на неё. Это устраняет часть бесконечных вращений около слишком близкой цели.

[`FlyIdle`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Activities/Air/FlyIdle.cs#L39) оставляет зависающий аппарат на месте либо продолжает движение с постоянным поворотом. Движение самолёта продолжается при отсутствии активной цели.

**Ручная трасса.** Цель непосредственно сбоку внутри минимального круга разворота → обычный самолёт сначала пролетает дальше → затем снова получает возможность повернуть к цели. Вертолёт с `CanSlide` может переместиться прямо к точке. Отличие обусловлено контроллером, а не спрайтом.

### Раздвигание и посадка

[`Aircraft.GetRepulsionForce`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/Air/Aircraft.cs#L471) ищет соседей в круге и применяет раздвигание только на крейсерской высоте. Кандидаты должны быть Repulsable с той же настроенной CruiseAltitude. В совпадающей точке выбирается направление через SharedRandom. У аппарата без бокового движения отбрасывается сила, направленная против движения. Рядом с зарезервированной базой раздвигание подавляется.

Это игровой способ разделить спрайты, а не строгий решатель непрерывных 3D-коллизий. Равенство настроенной высоты не равно проверке физических объёмов. Для OpenRTS нужен собственный детерминированный порядок соседей, а правила попадания должны работать независимо от мягкого раздвигания.

[`Land.Tick`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Activities/Air/Land.cs#L218) после захода проверяет клетки посадки и очистки. Если они заняты, ставит holding pattern и уведомляет препятствующих акторов. При разрешённой посадке добавляет влияние в ActorMap до финального снижения. VTOL снижается вертикально, обычный аппарат использует заход с движением вперёд. [`CanLand`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/Air/Aircraft.cs#L669) проверяет границы, акторов и допустимый тип грунта. При стыковке с актором terrain type игнорируется, но проверки остальных препятствий остаются.

**Применимость.** Подход к посадке и фактическое касание — разные этапы. Для OpenRTS предпочтительны точные тела и допустимый объём, существующие клетки OpenRA не переносить как обязательную дискретизацию.

### Возврат, очередь и боевой заход

[`ReturnToBase`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Activities/Air/ReturnToBase.cs#L43) выбирает живой свой совместимый Reservable. Сначала ищет свободный. Если все заняты, зависающий аппарат ждёт поблизости, самолёт периодически повторяет проверку в полёте. При отсутствии базы предусмотрен выход в idle, чтобы не зациклить вызывающую activity. Для нужного обслуживания резервируется база, затем выполняется посадка и Resupply.

[`Reservable`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Traits/Buildings/Reservable.cs#L23) хранит одну резервацию, допускает уступаемую резервацию свежесозданного аппарата и освобождает её при уничтожении, продаже или смене владельца базы. Это один ресурс посадки, не полноценное разделение ангара и полосы.

[`FlyAttack`](https://github.com/OpenRA/OpenRA/blob/9636b63eaee62fcb60ef2e896e7985bdf2f1ee68/OpenRA.Mods.Common/Activities/Air/FlyAttack.cs#L140) при отсутствии доступных для цели вооружений и наличии Rearmable может поставить ReturnToBase. `AbortOnResupply` определяет отмену дальнейших действий либо возврат в родительскую activity. Для AttackMove эта ветвь заканчивает атаку без такого обслуживания. Hover разворачивается и удерживается в пределах дальности. Обычный независающий аппарат проходит цель и выходит за радиус. Strafe задаёт дополнительный вылет для нового захода.

**Применимость.** Нужны явно описанные правила возобновления приказа. Даже в одном движке AttackMove и прямая Attack могут обслуживаться по-разному. Использовать это как пример политики, а не молчаливый контракт OpenRTS.

## Command & Conquer Generals — Zero Hour

Изучена реализация `GeneralsMD`. Применимость конкретных INI-параметров к каждой выпущенной модели самолёта не проверялась, поэтому выводы относятся к возможностям кода.

### Locomotor и управление высотой

[`Locomotor.cpp`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Locomotor.cpp#L953) выбирает движение по appearance, включая Wings, Hover и Thrust, и отдельно обновляет Z. Это более широкая классификация, чем «вертолёт/самолёт»: Hover не следует считать названием только вертолётов.

[`maintainCurrentPositionWings`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Locomotor.cpp#L2512) продолжает полёт с минимальной скоростью по окружности вокруг удерживаемой позиции. `maintainCurrentPositionHover` тормозит горизонтальное движение, ожидая нулевую минимальную скорость. Ожидание не означает одинаковую физическую операцию для разных locomotor.

[`handleBehaviorZ`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Locomotor.cpp#L2220) различает высоту относительно поверхности, абсолютную высоту, немедленную установку высоты и сглаженное управление подъёмной силой. Есть варианты относительно зданий и самого высокого слоя. `PRECISE_Z_POS` переключает цель на заданный Z, что важно при посадке.

**Применимость.** Независимая вертикальная политика полезна. Физические силы, float-арифметику и набор всех режимов переносить не требуется. OpenRTS сначала достаточно ограниченных целочисленных скоростей и явной посадочной отметки.

### Парковка и полоса — разные ресурсы

Источники — [`reserveSpace`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Behavior/ParkingPlaceBehavior.cpp#L333), [`reserveRunway`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Behavior/ParkingPlaceBehavior.cpp#L477) и [`JetTakeoffOrLandingState`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Update/AIUpdate/JetAIUpdate.cpp#L705).

1. Для самолёта находится существующее либо свободное парковочное место. В нём сохраняется ID аппарата. Возвращаются координаты парковки, подхода, полосы и ангара.
2. Резервировать полосу можно только при наличии места. Полоса содержит текущий ID пользователя и отдельного следующего ожидающего взлёта. Это не произвольная FIFO-очередь всей авиабазы.
3. Самолёт ожидает разрешения, рулит, проходит паузу и взлетает. Для посадки имеются подход, ожидание полосы, посадка, руление и ориентация парковки.
4. У состояния захода есть заданные точки и точный Z. Вертолётная ветвь использует отдельный [`HeliTakeoffOrLandingState`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Update/AIUpdate/JetAIUpdate.cpp#L943).
5. Выбор JetAIStateMachine или HeliAIStateMachine зависит от `NeedsRunway`. `KeepsParkingSpaceWhenAirborne` — отдельный параметр. Домашняя вместимость и разовая возможность обслужиться различимы.

**Ручная трасса.** У двух самолётов есть парковочные места одной базы → первый получает полосу → второй может иметь место и продолжать ждать разрешения на вылет. Одной резервации «эта база занята» недостаточно, чтобы воспроизвести это поведение.

### Боезапас и потеря базы

[`JetAIUpdate::isOutOfSpecialReloadAmmo`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Update/AIUpdate/JetAIUpdate.cpp#L106) проверяет именно оружие с `RETURN_TO_BASE_TO_RELOAD`. Это не любое оружие и не физический бак топлива. [`JetOrHeliReloadAmmoState`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Update/AIUpdate/JetAIUpdate.cpp#L1413) определяет длительность и постепенно восстанавливает обоймы. Механика откладывает часть команд до окончания reload.

При потерянной базе [`JetOrHeliCirclingDeadAirfieldState`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Update/AIUpdate/JetAIUpdate.cpp#L315) удерживает аппарат в полёте, периодически ищет другой аэродром и может наносить процентный урон через `OutOfAmmoDamagePerSecond`. Звук low fuel в этой ветви сам по себе не доказывает существование расходуемого топлива.

[`killAllParkedUnits`](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/0a05454d8574207440a5fb15241b98ad0b435590/GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Behavior/ParkingPlaceBehavior.cpp#L638) при смерти базы убивает связанные аппараты с исключением находящихся выше terrain и не выполняющих взлёт/посадку. Следовательно, «смерть базы просто снимает резервацию» не описывает Zero Hour.

**Применимость.** Заимствовать понятие независимых ресурсов и явные отказы. Урон без базы, обязательную привязку, запрет прерывания перезарядки и гибель припаркованных аппаратов оставить политиками игры.

## RecoilEngine

### Два контроллера и общий жизненный цикл

[`MoveTypeFactory::GetMoveType`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/MoveTypeFactory.cpp#L23) создаёт наземный контроллер с MoveDef либо воздушный без MoveDef. Воздушные делятся на Strafe и Hover, причём Hover используется также строителями, транспортами и gunship. Общая основа — AAirMoveType.

[`CHoverAirMoveType::SetState`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/HoverAirMoveType.cpp#L172) различает Landed, Takeoff, Flying, Hovering, Landing, Crashing. Переходы меняют физические флаги, видимость с воздуха и связанные состояния. Crashing не переводится обратно в нормальный полёт этой функцией.

Название Strafe не означает обязательный разбег. [`CStrafeAirMoveType::UpdateTakeOff`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/StrafeAirMoveType.cpp#L901) добавляет вертикальное ускорение и затем движение вперёд. Это дополнительное основание разделять способ полёта и способ старта.

### Высота, избегание и столкновения

[`AAirMoveType`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/AAirMoveType.cpp#L58) использует высоту грунта, воды и сглаженную высотную сетку. Выбор зависит от свойств и текущего действия. Возле погрузки/выгрузки и в переходных фазах сглаженная поверхность может отключаться. Это не единый постоянный Z для всех действий.

[`CheckForCollision`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/AAirMoveType.cpp#L248) через QuadField ищет угрозы впереди и ближайших соседей. Это предупреждение для контроллера. Отдельный [`CHoverAirMoveType::HandleCollisions`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/HoverAirMoveType.cpp#L1071) проверяет фактическое пересечение по 3D-расстоянию и радиусам, исправляет позиции и скорости с учётом массы. Для погрузки и выгрузки есть специальные исключения.

В Strafe полёт содержит скорость, векторы ориентации, тягу, сопротивление, управление рулями, высоту и атаку. В прочитанной ветви [`UpdateFlying`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/StrafeAirMoveType.cpp#L808) часть аргументов предупреждения столкновения передаётся как `false && ...`. Нельзя заключать, что каждое рассчитанное предупреждение обязательно включает полноценный вертикальный манёвр уклонения. Есть отдельная поправка горизонтального направления и обработка пересечений.

**Применимость.** Отделить запрос соседей, желаемое движение и проверку фактического положения. Полный набор аэродинамических параметров для спрайтового CnC избыточен. Исправление пересечений после шага нельзя считать доказательством swept-гарантий OpenRTS.

### Посадка и завершение Move

[`AAirMoveType::LandAt`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/AAirMoveType.cpp#L201) задаёт место снижения и отмечает его в ground blocking map. [`CanLandAt`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/MoveTypes/HoverAirMoveType.cpp#L1019) в обычной ветви проверяет границы, воду и клетки footprint на блокирующие объекты. ForceHeading даёт отдельное исключение. Это сочетание воздушного движения с наземной занятостью в фазе посадки.

[`CAirCAI::ExecuteMove`](https://github.com/beyond-all-reason/RecoilEngine/blob/c9d0299f7a1e0054fc2466aeb01c809aa00e2945/rts/Sim/Units/CommandAI/AirCAI.cpp#L260) завершает Move самолёта при близости текущей или предсказанной позиции к цели. Порог учитывает turnRadius. Самолёт не обязан остановиться в точном X/Y. AirCAI относится к Strafe, а не ко всей авиации.

**Применимость.** Общий приказ Move может иметь разный критерий прибытия. Текущий точный контракт наземного Move OpenRTS сохраняется, воздушный режим следует публиковать явно.

В прочитанных MoveTypes не обнаружен полный аэродромный цикл Zero Hour. Правила ремонта и обслуживания конкретных игр Recoil могут находиться в Lua. Старые описания Spring AirBaseHandler не приняты за факт о текущей версии Recoil. Игровой Lua BAR и Zero-K здесь не исследовался.

## Warzone 2100

Этот дополнительный референс выбран ради законченного цикла «боевой заход → ожидание → площадка → боезапас», а не аэродинамики.

[`DACTION_VTOLATTACK`](https://github.com/Warzone2100/warzone2100/blob/253e370c2af78228a822df6afa82b6261dc94656/src/action.cpp#L1298) проверяет цель и боезапас, стреляет при видимости, дальности и наведении, строит следующие участки захода. При пустом боезапасе аппарат получает moveToRearm. Нахождение в пределах дальности не означает остановку на месте.

[`findNearestReArmPad`](https://github.com/Warzone2100/warzone2100/blob/253e370c2af78228a822df6afa82b6261dc94656/src/structure.cpp#L6944) учитывает заданную цель, расстояние и доступность площадок. [`REF_REARM_PAD`](https://github.com/Warzone2100/warzone2100/blob/253e370c2af78228a822df6afa82b6261dc94656/src/structure.cpp#L3422) выбирает ожидающий свой аппарат с более ранним actionStarted. При отсутствии своего кандидата рассматривает союзников. В прочитанной ветви равные actionStarted не имеют дополнительного явного сравнения по ID, поэтому стабильность равенства нельзя переносить на OpenRTS без собственного правила.

Площадка сама переводит выбранный аппарат из ожидания в движение к точке обслуживания. [`Обновление обслуживания`](https://github.com/Warzone2100/warzone2100/blob/253e370c2af78228a822df6afa82b6261dc94656/src/structure.cpp#L3710) начинается после фактической остановки, выдаёт боезапас по накопленному времени и массе, ремонтирует, очищает назначение при смерти или смене действия. Полное вооружение и ремонт освобождают площадку. [`DACTION_WAITFORREARM`](https://github.com/Warzone2100/warzone2100/blob/253e370c2af78228a822df6afa82b6261dc94656/src/action.cpp#L2259) обрабатывает потерю базы и повторный поиск.

**Ручная трасса.** Три аппарата ждут одну площадку → площадка выбирает одного → остальные остаются в ожидании → обслуживание начинается после его прибытия → готовность освобождает назначение. Процессом владеют и аппарат, и база через согласованные состояния. В OpenRTS такое согласование нужно оформлять проверяемой резервацией, чтобы две стороны не начали обслуживать разные пары.

## Что уже есть и чего нет в OpenRTS

| Участок текущего кода | Факт | Следствие |
| --- | --- | --- |
| [WorldPosition](../../../src/OpenRTS.Core/SimulationTypes.cs), [SpatialState](../../../src/OpenRTS.Core/Simulation/SpatialState.cs) | Есть X/Y и независимый Elevation, Previous и Position | Формат координаты пригоден как основа, готового воздушного движения нет |
| [StepTowards](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.cs#L551) | Шаг рассчитывается по X/Y, на промежуточных шагах Elevation сохраняется | Нельзя использовать его как готовое движение воздушной ракеты или аппаратного снижения |
| [MotionIndex](../../../src/OpenRTS.Gameplay/Navigation/MotionIndex.cs), [индексация мира](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.Indexing.cs) | Бины и проверки тел наземные, в индекс включаются объекты на карте | Авиацию нужно отделить от наземной блокировки, сохранив доступность для поиска целей |
| [ObjectGeometry](../../../src/OpenRTS.Gameplay/Geometry/ObjectGeometry.cs) | Физические границы X/Y, Elevation переносится в точки, но не входит в расстояние | Высота не меняет дальность и не разделяет объекты при попадании |
| [ValidTarget/InWeaponRange](../../../src/OpenRTS.Gameplay/Combat/SimulationWorld.Combat.cs) | Допуск по владельцу, броне, состоянию, дальность и LoS по наземной геометрии | Нет общего допуска Ground/Air и высотного LoS |
| [Projectiles](../../../src/OpenRTS.Gameplay/Combat/SimulationWorld.Projectiles.cs) | Сегмент и относительное движение проверяются в X/Y, интерполяция сохраняет начальный Elevation, арка гранаты строится при публикации | Высокая визуальная арка не доказывает физический пролёт над препятствием |
| [ICombatTargetRule](../../../src/OpenRTS.Gameplay/Combat/ICombatTargetRule.cs) | Ранжирование уже допустимых автоматических целей | Не заменяет общий фильтр допустимости ручной атаки, перехвата и AoE |
| [Update](../../../src/OpenRTS.Gameplay/Simulation/SimulationWorld.cs#L158) | После приказов идёт движение, затем оружие, стройка/экономика/производство, представление | Воздушное движение и смена физического режима должны завершаться до боя |
| [SpriteWorldView](../../../src/OpenRTS.Godot/Rendering/SpriteWorldView.cs#L179) | Elevation интерполируется в представлении | Нужны проверка проекции, теней, глубины и выделения, но не отдельный Godot-узел на каждый аппарат |
| [Граница транспорта](../../decisions/026-transport-construction-boundaries.md) | Пассажиры и жизненный цикл принадлежат Transport | Сохранять владельца пассажирского состояния, согласовать выгрузку с фактической посадкой |

Вывод о доработках OpenRTS относится к закреплённому commit. Численных выводов о производительности или готовности авиации из существующих наземных benchmark нет.
