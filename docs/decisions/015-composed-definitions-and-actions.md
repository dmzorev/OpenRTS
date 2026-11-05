# 015. Составные определения и независимые панели действий

Дата: 2026-11-05. E3 реализован. [Проверки и стоимость](../verification/016-composed-definitions.md), [план ECS](../research/ecs-selection/migration.md).

## Рецепт и экземпляр

`EntityDefinition` — неизменяемый типизированный рецепт с ID и необязательными Health/Spatial/Presentation/Armament/Production/Build. Он не содержит текущие HP, положение, cooldown или задачи. Конкретные capabilities — HealthDefinition, SpatialDefinition, PresentationDefinition, ArmamentDefinition и ProducerDefinition. PresentationDefinition описывает семантические имена/состояния, без Model, текстур и UI.

Возможности проверяют свои параметры и зависимости при конструировании. Footprint требует неподвижности; armament требует spatial и объявленных анимаций; физические продукты требуют spatial и известных очередей; рабочие состояния/дверные анимации очереди должны существовать в semantic presentation. Ability/product IDs не пересекаются. Входные коллекции копируются и публикуются как read-only; пустые armament/production нормализуются в отсутствие возможности.

`UnitDefinition` остаётся совместимым представлением **пространственного юнита** поверх рецепта. Его прежний конструктор собирает capabilities; новый принимает готовый EntityDefinition. Плоские свойства и WriteState сохраняют действующий API. Этот адаптер пока требует Health, Spatial и Presentation, потому что горячие системы ещё работают с EntityState[]. Сам EntityDefinition таких обязательных возможностей не требует: непространственные данные можно хранить в другом Frent.World без фиктивных HP/координат.

UnitCatalog остаётся загрузчиком юнитов/зданий. `Definitions` и `Units` содержат те же типы в одинаковом ordinal-порядке: первая коллекция даёт compiled recipes, вторая — совместимый API. Универсальный EntityCatalog, PlayerWorld и пустые Player-состояния не создаются. Эти capabilities отражают реализованные механики; универсальный registry произвольных модулей не вводится заранее.

Unit storage хранит тот же EntityDefinition как immutable class-component, общий для экземпляров типа. ProducerState и прочее runtime-состояние принадлежат конкретной сущности. Создание/удаление строки не меняет рецепт; Frent handles не входят в recipe. Горячие поля и физические запросы переносятся отдельно в E4.

## Gameplay Actions и UI

Новый рекомендуемый формат:

```yaml
rifleman:
  # Health, Armaments и Presentation объявлены здесь или у родителя.
  Actions:
    Definitions:
      grenade: {Mount: grenade, GroundTarget: true, AutoCast: false}
  ActionPanel:
    Buttons:
      grenade: {Label: Граната, Hotkey: Q, Cursor: grenade, Order: 0}
```

Gameplay definition содержит ровно одно из Mount/Unit. Способности могут задавать GroundTarget/AutoCast; production — Queue/Ticks/Cost/PlacementRadius. Цена/длительность наследуются из Buildable продукта. Загрузчик подготавливает каждый concrete trait set и Buildable один раз, затем разрешает forward references; самопродукция не создаёт рекурсии. Неизвестные продукты, отсутствующий Buildable, неверные очереди и несоответствие PlaceBuilding/footprint отклоняются до создания мира.

Ключ в ActionPanel.Buttons — ссылка на игровой action ID. Здесь только Label/Icon/Hotkey/Cursor/Order. В Definitions запрещены UI-поля, в панели запрещены параметры оружия/производства. Неизвестные action refs и повторяющиеся hotkeys отклоняются до матча. Q/W/E/R/T/Y назначаются после сортировки панели по Order и ordinal ID, как раньше.

Без ActionPanel новые gameplay actions сохраняются и доступны WorldCommand/Session. Панель может показывать подмножество действий; она не определяет набор механик. Label, порядок, курсор, иконка и hotkey не входят в fingerprint и simulation state.

## Совместимость контента

Прежний `Actions.Buttons` продолжает создавать и gameplay declarations, и UI bindings. Это совместимый вход: **новый контент использует отдельные Definitions/ActionPanel**. У legacy без явного ActionPanel панель создаётся автоматически; явный ActionPanel полностью заменяет legacy bindings, но не gameplay declarations. Новый и legacy наборы разрешено объединять только при разных action IDs; дублирование одного ID отклоняется вместо скрытого выбора приоритета.

Правила наследования DeclarativeContent не меняются: пустая map `{}` сохраняет родительские значения. В новом формате удалить унаследованную панель можно через `-ActionPanel:`; Actions.Definitions при этом остаются. Чтобы очистить существующую панель и задать новую, применяется обычное remove-then-add. Для legacy автоматический fallback отключается явной пустой панелью; если она уже унаследована, сначала удаляется, затем добавляется пустая. Переход с legacy actions на новые с теми же ID также требует удалить старый Buttons-блок у соответствующего родителя/потомка.

Sandbox units/buildings переведены на новый формат с теми же игровыми параметрами и UI. Типовые индексы, default hotkeys, цены/длительности, mount IDs и recipe overrides не изменены.

## Воспроизводимость и границы

Каталог сохраняет fingerprint7, симуляция — direct7/nav11. WriteState записывает прежние канонические значения, а не layout нового объекта или ссылки Frent. Все пять исходных покадровых traces остались прежними после переноса definitions и YAML.

Blueprint не заменяет полный snapshot/rollback и не поддерживает горячее изменение правил матча. Shared class-component безопасен здесь благодаря неизменяемости; mutable классы состояния так разделять нельзя. Удобство общего рецепта не требует помещать игроков, юнитов и снаряды в один store.
