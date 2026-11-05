# 019. Завершение ECS-интеграции: identity, routes и spatial index

Дата: 2026-11-05. Продолжает [Combat/Presentation](018-combat-presentation-components.md). [Проверки и измерения](../verification/020-ecs-integration.md).

## Игровое состояние и производные индексы

EntityState удалён. UnitIdentity — readonly struct-компонент с domain EntityId и ordinal UnitType. Он задаётся при Spawn; native ID, generation, archetype row и world ID Frent не становятся игровыми идентификаторами. Другие поля читаются/изменяются непосредственно через конкретные компоненты, без миграционных live views или универсального Actor/IEcsWorld facade.

`_unitHandles` — плотный производный список Frent.Entity, `_indices` связывает domain ID с местом в этом списке. Это индекс живого unit store для существующих проходов, не второй runtime store. В списке нет HP, позиции, Owner, приказов или идентичности поверх native handle. Порядок списка не совпадает обязательно с archetype rows и не определяет игровые tie-break/RNG. При удалении переставляется только handle и исправляется ID mapping; компоненты переносит Frent. Локальные копии readonly UnitIdentity допустимы через Spawn, component refs — нет.

Проходы, которым нужен канонический порядок, используют прежние списки domain ID/отсортированные индексы. Для независимой арифметики сохраняются закрытые queries. Публикация и canonical hash получают значения непосредственно из компонентов; native layout и производные индексы не сериализуются.

## Личный маршрут принадлежит агенту

NavigationAgentState.Route хранит NavigationRoute: точки, revision, cursor, текущий waypoint/признак его наличия и производный cost prefix. Старый Dictionary<EntityId,GroupRoute> удалён. Маршрут имеет одного владельца, как FIFO, mount array и local path. SharedRouteTree, weighted fields, terrain/components и поисковый scratch остаются специализированными общими структурами.

Отмена/замена цели, уступание и локальный обход снимают Route у своего агента. Native Delete освобождает связь с маршрутом без отдельной очистки словаря и без чтения удалённой entity. Map revision по-прежнему инвалидирует старые routes. Их каноническая сериализация остаётся в прежнем месте и порядке по domain ID: points/cursor/revision/waypoint сохраняются, cost prefix не хешируется.

## MotionIndex не использует места игровых handles

MotionIndex строится закрытым запросом UnitIdentity + SpatialState + EntityDefinition. Внутри он владеет своими плотными body slots и EntityId→slot mapping. Add/SetEnd/ClearMove/ClearPlacement и внешние кандидаты используют domain IDs. Owner убран из Add: индекс его и раньше не хранил и не использовал. Бой/снаряды разрешают возвращённый ID через игровой mapping, не интерпретируют body slot как место unit handle или native row.

Buckets/extents/дедупликация по seen stamp, относительное swept движение Start→End, padding maximumStep, формы/размеры и tie-break по минимальному ID сохранены. Пространственный индекс не становится authoritative хранилищем позиции или геометрии: это снимок начала движения с последовательно принятыми End. Spawn добавляет тело до обработки следующего производителя; смерти и изменения команд обновляют индекс на прежних барьерах.

Общий сдвиг передаёт список EntityId; MotionIndex сам отмечает cohort в своих slots. Игровой bool-массив cohort больше не используется как адресация body rows. Локальный поиск один раз разрешает ID в BodyToken; принятый обычный шаг использует тот же token для ClearMove и SetEnd. Это отдельный тип с index/generation, не преобразуемый из игрового int index. Он действителен до Clear/rebuild; debug assertion проверяет generation. Tokens не сохраняются в компонентах/кадрах и не пересекают структурные изменения или ожидание jobs.

При профилировании обнаружен лишний перебор кандидатов у вооружённых Owner=0 в навигационном стенде. AcquireWeaponTarget теперь возвращает отсутствие цели до пространственного запроса: ValidTarget и раньше запрещал любую атаку/ремонт такого источника. Windup/burst/анимации обновляются прежней фазой; меняется только бесполезный поиск кандидатов.

## Границы завершения

Сохраняются explicit simulation phases, Frent data-only storage и запрет World.Update. Команды/Session, определения/YAML, frames/events и форматы catalog7/direct7/nav11 не меняются. FrameLease не получает native handles или component refs. Раздельные stores для других доменов допустимы; Player/экономика не создаются пустыми, projectile pool остаётся прежним.

Это завершает перенос unit runtime state и удаление миграционных stores/views, а не разработку всей RTS. Дальнейшие сменные системы вводятся под конкретные механики. N4, стоимость холодного clearance/инвалидации, пропускная способность толпы, экономика и новые виды приказов остаются отдельными задачами. Завершение принимается по parity, полным проверкам и измерениям, а не по числу новых компонентов.
