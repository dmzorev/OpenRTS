# 016. Первый срез горячего состояния: Spatial и Health

Дата: 2026-11-05. Реализован E4a; E4 в целом ещё не завершён. [Проверки и измерения](../verification/017-spatial-health-ecs.md), [план](../research/ecs-selection/migration.md).

## Источник состояния

`SpatialState` содержит Current Position и Previous, `HealthState` — текущий HP. Это обычные struct-компоненты Frent unit storage. Неизменяемые Body/Locomotor/Footprint/MaxHealth/Armor остаются в compiled definitions. Инициализация компонентов выполняется при Spawn в той же операции создания entity, включая производителей.

Из EntityState[] удалены mutable поля Position, Previous и Health. На время переноса связанных систем у EntityState есть **только readonly getters** к соответствующему компоненту. Они не содержат кэша/второй копии, не дают записать локальный struct copy и не меняют компоненты. Такое чтение возвращает живое состояние; копия EntityState больше не является снимком его spatial/health. Если значение нужно после Destroy, оно копируется до удаления entity.

Реальные записи выполняются явно: непосредственное движение, общий групповой сдвиг и MoveTo изменяют SpatialState; damage/repair изменяют HealthState. Это ограниченный миграционный мост, а не постоянный actor API или универсальный ECS facade. Orders/Motion/NavigationAgent/Combat/Presentation и mapping packed индексов ещё предстоит перенести.

## Фазы, refs и жизненный цикл

После команд закрытая batch query копирует Position→Previous у всех SpatialState. Порядок строк здесь не влияет на результат: каждая операция независима. Затем, как прежде, выполняются приказы, навигация, движение, бой, производство и presentation. Ошибка подготовки previous теперь попадает в общий faulted-step guard; частичный тик не публикуется.

Компонентные refs в горячих циклах живут только в участке без structural changes и не передаются в jobs. Прямое движение, подготовка motion index, presentation, damage и CopyPoses берут один spatial/health доступ на нужный объект. До Spawn/Destroy и до ожидания job-пакета такие refs заканчиваются. Навигационные workers по-прежнему обрабатывают согласованные карты/запросы и не получают Frent refs.

Уничтожение сначала создаёт value-событие с позицией/типом/владельцем живой entity, затем удаляет её из Frent и packed storage. Повторное использование native слота не переиспользует доменный EntityId. Archetype packed removal и legacy packed removal могут переставлять разные объекты — внутренний handle сохраняет связь с компонентами, а _indices обновляет legacy mapping.

MotionIndex и FrameExchange остаются специализированными структурами. Копии в индексе — derived данные начала/конца движения; опубликованные poses — immutable снимок. Они не являются вторым authoritative spatial/health store. UI не получает новые компоненты или ссылки на них.

## Каноническое состояние и ограничения

WriteState/Report сохраняют прежние значения и порядок по доменным IDs; layout компонентов, slots и native world IDs не сериализуются. Форматы catalog7/direct7/nav11 и golden traces сохранены.

Health/Spatial нужны всем текущим юнитам/зданиям, поэтому присутствуют во всех archetypes unit storage. Другие домены не обязаны иметь эти компоненты или использовать этот store. Непространственный Player не добавлен. Пока остаются readonly обращения из legacy систем; стоимость измерена отдельно на прямом движении/производстве и реальном навигационном сценарии. Принятие этого среза не означает, что обещана любая армия10000+ на25Гц.
