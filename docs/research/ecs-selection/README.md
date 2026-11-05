# Выбор ECS для OpenRTS

Дата: 2026-11-05. **Выбран Frent 0.9.1-beta, data-only компоненты и явно вызываемые query-системы. E0–E3 выполнены; [E3 definitions/actions](../../verification/016-composed-definitions.md). Также реализованы [E4a Spatial/Health](../../verification/017-spatial-health-ecs.md) и [E4b Orders/Motion/NavigationAgent](../../verification/018-orders-motion-navigation-ecs.md). Реализован [E4c Combat/Presentation/Ownership](../../verification/019-combat-presentation-ecs.md); mapping и завершение E4/E5 ещё впереди.** Исследовательские probes изолированы от основных csproj/solution; Core подключает только выбранный Frent.

Этот выбор заменяет прежнюю рекомендацию сначала строить собственные специализированные stores из [исследования Actor/traits](../actor-composition/README.md). Разделение ответственности из него сохраняется; готовая библиотека теперь берёт на себя ECS storage, membership, queries и внутренние handles.

- [Целевая архитектура и роль Actor](architecture.md).
- [Обязательные работы, опции и критерии перехода](migration.md).
- [Воспроизводимые проверки и их пределы](probe/README.md).
- [Первичные источники](sources.json), [фактические метаданные пакетов и SHA-256](package-evidence.json).
- [Архитектурное решение014](../../decisions/014-ecs-foundation.md).

## Основание выбора

Тысячи объектов со сложной логикой оправдывают готовую инфраструктуру данных и выборок. Однако само число10000 не доказывает преимущества одной библиотеки: текущий плотный массив тоже последователен в памяти. Выигрыш ожидаем от выборок только нужных возможностей, меньших горячих компонентных наборов, отсутствия своих membership/query механизмов и более ясных границ записи. Навигация, broad phase и выбор целей остаются отдельными алгоритмами.

Критерии здесь: соответствие явным фазам OpenRTS, работа с существующими численными типами и переменными состояниями, разрешительные зависимости, контролируемые структурные изменения, изоляция Godot и цена сопровождения. Авторские speed charts не используются как рейтинг производительности нашей RTS.

| Критерий | EasyEcs4.7.0 | Friflo.Engine.ECS3.6.0 | Frent0.9.1-beta |
| --- | --- | --- | --- |
| Поставка для нашего net10.0 | net10, generator/analyzer; Nino4.0.0-rc.6 | Используется net8 сборка; JSON dependencies | Собственная net10 сборка, bundled generator; runtime dependencies отсутствуют |
| Лицензии восстановленного набора | EasyEcs и Nino — MIT | ECS — MIT, Fliox/Burst/Annotation1.0.4 — LGPL-3.0-only | MIT; нет транзитивного runtime набора на net10 |
| Отдельные системы над данными | Да: queries/jobs и явно вызываемые группы | Да: queries/jobs, Systems необязательны | Да: queries, struct actions, chunks; EC callbacks необязательны |
| Struct с managed полями | Да | Да | Да; также допускаются class-компоненты |
| Структурные изменения | Немедленные на owner thread; нет command buffer в4.7 | Explicit buffer, но playback группирует операции | Во время query автоматически отложены; применение при выходе из последней enumeration/update |
| Параллельность | Row jobs и read/write schedule | Run/RunParallel jobs | Встроенный parallel Update относится к EC; для выбранного query-подхода сначала сохраняем свои jobs |
| Стабильность выбранной базы | Stable package, но быстрый churn4.x и RC dependency | Stable3.6; есть4.0 preview | Явно beta; версия и обновления требуют контроля |
| Итог для OpenRTS | Сильный резервный кандидат | Не выбран из-за copyleft-зависимостей стандартной поставки | **Выбран для миграции** |

Основания таблицы: [EasyEcs4.7 csproj](https://github.com/JasonXuDeveloper/EasyEcs/blob/b01f49bcb6149bb9bc38495a79af39c2793665e8/src/EasyEcs.Core/EasyEcs.Core.csproj), [Friflo3.6 csproj](https://github.com/friflo/Friflo.Engine.ECS/blob/c326ceb2076d7d14c0ae74df0e661c62aedd8d24/src/ECS/Friflo.Engine.ECS.csproj), [Frent csproj](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/Frent.csproj). Реальные NuGet архивы и их nuspec дополнительно проверены локально; декларация MIT у верхнего пакета не заменяет проверку зависимостей.

## Почему Frent

Frent предоставляет нужный низкоуровневый ECS без обязательной собственной модели тика. `Query<T...>()`, ref iteration и struct `IAction` можно вызывать прямо из наших фаз; `World.Update()` не требуется. Это позволяет оставить доменные правила порядка в OpenRTS. [Query-системы](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/docs/docs/systems.md), [typed queries](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/Systems/Query.typed.cs).

Плюс — небольшая поставка без runtime dependencies для net10 и возможность постепенно перенести текущие массивы/очереди, не переписывая сразу их внутреннюю структуру. Это преимущество интеграции, **не доказательство большей скорости относительно EasyEcs**. Собственный генератор Frent всё равно нужен и входит в пакет. [Установка](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/docs/docs/installation.md).

Принятый компромисс: **Frent остаётся beta**. Не называем его более зрелым, чем EasyEcs или Friflo. Версия фиксируется точным диапазоном и lock file; новый release не подтягивается автоматически. Поведение ключевых APIs закрепляется проверками OpenRTS. Если пилот обнаружит существенный дефект refs/deferred operations или неприемлемые накладные расходы, возврат к выбору происходит до массовой миграции, с EasyEcs как первым резервом. [Предупреждение автора о beta](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/README.md#L13).

## Почему сейчас не другие два

**Friflo** технически хорошо сочетается с нашим подходом: независимые queries, jobs с последовательным/параллельным режимом, явно применяемые buffers. Базовый локальный probe прошёл. Но восстановленный3.6.0 действительно включает LGPL-3.0-only JSON-пакеты; у4.0.0-preview.2 Fliox также остаётся зависимостью. Это не означает автоматического перелицензирования OpenRTS в GPL и не означает запрета коммерческого использования. Для нашей цели разрешительной основы выбираем вариант без этой дополнительной лицензионной политики, вместо сопровождения собственного Friflo fork. [Точная preview-поставка](https://www.nuget.org/packages/Friflo.Engine.ECS/4.0.0-preview.2), [лицензия Fliox](https://github.com/friflo/Friflo.Json.Fliox/blob/6ac89217ad3ea66f11abfd756a0af323d63315ff/LICENSE).

**EasyEcs** — реальная альтернатива: SoA, unmanaged/managed columns, generated jobs и read/write schedule. На нашемSDK его probe проходит. Но4.7 содержит обязательную RC-версию Nino; latest master уже отличается от релиза, в том числе API систем и обещаниями интеграции. Stable FAQ ещё называет Godot непроверенной целью. Это не TFM-блокер и не доказательство ошибки, но повышает цену верификации. Значительная часть встроенного schedule/сохранений нам сейчас не нужна: сохраняем собственные фазы и канонический state contract. [Threading4.7](https://github.com/JasonXuDeveloper/EasyEcs/blob/b01f49bcb6149bb9bc38495a79af39c2793665e8/docs/content/guide/threading.mdx), [stable FAQ](https://github.com/JasonXuDeveloper/EasyEcs/blob/b01f49bcb6149bb9bc38495a79af39c2793665e8/docs/content/faq.mdx).

Другие ECS сейчас не добавляют обязательной ветки исследования: выбранный кандидат прошёл проверку API и текущего Godot. Если критерии пилота не выполнятся, расширяем сравнение, а не строим собственный универсальный ECS. Из прежнего исследования остаётся Arch как возможная следующая альтернатива, без утверждений о его актуальной пакетной поставке или превосходстве.

## Проверенные ограничения Frent

1. **Порядок query не является игровым порядком.** Внутренние handles переиспользуются; сохраняем свой монотонный EntityId и tie-break по нему. [Entity](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/Entity.cs), [удаление и generation](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/World.structural.cs).
2. **Deferred buffer не является FIFO журналом.** Команды группируются по типу, некоторые списки извлекаются LIFO. В игровых query собираем предложения и применяем их снаружи в установленном порядке; случайный выход из enumeration не должен запускать игровую транзакцию. [CommandBuffer](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/Core/CommandBuffer.cs), [structural changes](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/docs/docs/structural-changes.md).
3. **Hybrid storage — опция с ценой.** Sparse уменьшает churn сигнатур, но выборки становятся менее последовательными; chunk enumeration для sparse-query запрещена. По умолчанию используем обычные struct-компоненты и значения/флаги для частых состояний. [ISparseComponent](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/Components/ISparseComponent.cs), [ChunkQueryEnumerator](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/Frent/Systems/Enumerators/ChunkQueryEnumerator.cs).
4. **Default Update не задаёт наш порядок.** Не используем его и per-component callbacks для authoritative игровой логики. [Update filters](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/docs/docs/filter-update.md).
5. **Managed references разрешены, но не изолированы автоматически.** Запись через общий List/array не становится безопасной из-за ECS. Definition refs могут быть общими только как immutable; изменяемые очереди принадлежат одной сущности/системе. Это наш контракт владения.

Лимиты выбранной версии:127components/entity,65535component types/archetypes. Не проектируем игру вблизи этих пределов и не создаём архетип по каждому уникальному значению cooldown. [Ограничения](https://github.com/itsBuggingMe/Frent/blob/e9901ea8979d410afb881666033bd99dbfe57687/docs/docs/limitations.md).

## Что доказано, а что ещё нет

Запущены три отдельные программы с фиксированными пакетами. Frent дополнительно проверен в headless Godot4.7.2/net10.0 и на совпадение канонического результата10000 записей при1/4workers и обратном порядке создания. Результаты и команды — [здесь](probe/README.md).

Это проверка совместимости и конкретных семантических свойств. Она не измеряет сложную RTS, не доказывает ускорение World.Step, lockstep между ОС или отсутствие beta-дефектов. До переноса горячих систем обязательны baseline и пилот на существующих сценариях движения, боя, production и публикации кадров.
