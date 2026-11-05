# 018. Combat, Presentation и Ownership в ECS

Дата: 2026-11-05. Реализован E4c после [приказов и движения](017-orders-motion-navigation-components.md). [Проверки и измерения](../verification/019-combat-presentation-ecs.md), [план миграции](../research/ecs-selection/migration.md). Mapping и завершение E4/E5 остаются отдельным следующим шагом.

## Компоненты и определения

`CombatState` владеет массивом WeaponState и сводным WeaponReadyTick. Каждый mount сохраняет Target/ChargingTarget, ReadyTick/FireAtTick, BurstRemaining, AutoCast, текущий/предыдущий Facing и AnimationPlayback. Сам WeaponDefinition, настройки mount и AbilityDefinition остаются неизменяемыми compiled данными. Массив создаётся для конкретного actor; непустые mutable массивы не разделяются и не клонируются между actors. Пустые массивы не содержат изменяемых элементов.

`PresentationState` содержит семантические body/weapon направления и предыдущие направления, два playback, явные visual states, body animation override и наведение AimPoint/AimEntity/HasAim. Здесь нет Godot, Texture, Model ID или YAML. Рабочее состояние производителя по-прежнему накладывается при публикации, не записывается обратно в VisualStates. Связь с адаптерными ресурсами остаётся вне симуляции.

`OwnershipState` хранит Owner отдельно: он используется боем, производством, движением и представлением. Это не Player actor и не новая дипломатия. Прежние правила neutral0 / разные ненулевые владельцы сохранены.

EntityState больше не содержит перечисленных runtime полей. В нём остались только domain ID, UnitType и native handle с временными live component views. Это миграционный mapping, а не actor-мегакомпонент. Каждое mutable поле имеет единственный источник в Frent. Копия mapping не является снимком компонентов и невалидна после удаления native entity.

## Фазы и время жизни

Spawn создаёт сразу10компонентов обычного unit actor или11с ProducerState. Закреплённый Frent0.9.1-beta поддерживает typed Create до16компонентов; дополнительный structural Add/промежуточная entity не нужны. Инициализация направлений, idle playback, owner, states и default autocast сохраняет прежние значения. Полностью созданная entity регистрируется в mapping и получает domain ID в прежнем порядке.

После команд закрытый ECS-запрос PresentationState + CombatState сохраняет предыдущие направления; spatial previous обновляется прежним отдельным запросом. Операции независимы по actors, поэтому порядок archetype rows здесь не влияет на результат. WeaponCount читает CombatState через query. Публикация WeaponPose разрешает компоненты один раз на actor, затем проходит mounts в прежнем порядке.

Бой сохраняет стабильный порядок domain IDs: acquisition, windup/burst/cooldown, залп, применение damage, repair выжившим и удаление погибших. Структурное удаление выполняется после участков с компонентными refs. Production спавнит после боя вне открытых queries. Ссылка на ProducerState как обычный managed object может переживать Spawn; **ref на его ECS column** или любой другой component ref — не может. Навигационные workers по-прежнему не получают mutable Frent refs.

Безоружные определения пропускают per-mount фазу до получения боевых refs: mounts неизменяемы, их сводный ReadyTick остаётся начальным0. Общие projectile/damage/death проходы при этом выполняются. Обработка приказов получает представление только после проверки наличия применимого оружия и режима; обычный Move не требует этих дополнительных обращений.

Presentation обновляется после движения всех actors, боя и производства. Направление корпуса отражает реальный сдвиг; наведение по ID читает актуальную позицию цели. AnimationPlayed остаётся отдельным событием. При Destroy/Remove owner и position копируются в событие до native Delete. Снаряды хранят собственные значения source/owner/mount и переживают стрелка; их compact pool не переносится в ECS.

Сам UpdatePresentation — закрытый запрос PresentationState + CombatState + MotionState + SpatialState + EntityDefinition. Он пишет только собственные направления/playback/mount facings и читает уже рассчитанные позиции целей; поэтому порядок archetype rows здесь независим от результата. В нём нет Spawn/Delete, RNG или выдачи событий. Immutable PresentationDefinition разрешает idle/move indices один раз в конструкторе; unit frontend сохраняет прежний fallback move→idle для неподвижных определений. Перебор имён анимаций не выполняется на каждого actor/тик.

## Внешние контракты и остаток миграции

WorldCommand/Session, definitions/YAML, EntityPose/WeaponPose/ProjectilePose и frame leases не меняются. События и published poses содержат копии; native handles и массивы WeaponState наружу не выдаются. Native slots, component order и query layout не хешируются. Форматы catalog7/direct7/nav11 и пять golden traces сохранены.

MotionIndex пока использует packed indices; per-ID group routes и общие навигационные структуры остаются вне компонентов. Следующий этап убирает временный EntityState mapping/его views и завершает навигационную интеграцию с domain IDs. Производительность каждого переноса проверяется отдельно; ECS сам по себе не устраняет цену broad phase, навигации или плотных заторов. Новые универсальные Actor/IEcsWorld/scheduler абстракции не вводятся.
