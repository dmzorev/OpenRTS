# 017. E4a: SpatialState и HealthState в ECS

Дата: 2026-11-05. [Решение016](../decisions/016-spatial-health-components.md). Перенесён первый срез E4; приказы, motion state, боевые mounts и presentation пока остаются в legacy state.

## Что проверено

Current/Previous позиции и текущий HP имеют единственный источник — struct-компоненты Frent. В packed EntityState нет этих mutable полей; только временные readonly обращения к живому компоненту. WorldCommand, Published poses и Definition API не менялись. Previous обновляется закрытой batch query после команд, до расчётов; инициализация Spawn задаёт обе позиции.

Пять покадровых golden traces E0 совпали без обновления hashes. Они включают состояния/команды/события и Entity/Weapon/Projectile/Task poses для direct, navigation, combat, buildings и production. Полный прогон:191 Core и20 DeclarativeContent проверок,0ошибок. После упрощения общего fault guard дополнительно выполнен финальный целевой ECS-прогон.

Новые случаи: движущийся повреждённый actor сохраняет HP и previous/current после **разных** перестановок legacy и Frent archetype; новый actor в переработанном native слоте получает своё начальное здоровье/положение; factory после перестановки выпускает по своей позиции;80command spawns заставляют расти обе ёмкости; pinned pose переживает эти изменения и Dispose. Отдельно проверен Spawn+Move на одном тике. Событие Destroy получает позицию до удаления компонента.

Существующие проверки покрывают damage→repair→death, ally-only repair, боевую геометрию/splash, огонь в движении, относительный swept collision, изменение footprint, paused/cancelled sessions и отсутствие steady allocations при неизменной producer membership.

Финальный целевой ECS-прогон:14проверок,0ошибок. Полный Godot/headless verification прошёл: content parity; direct1/10000/10001 при15/120FPS и pacing; navigation1/10000 sequential/parallel/reverse; sprite/combat/repair presentation; RTS controls; buildings/10000poses; abilities/production UI и160-тиковый journal; невалидные входы. [Сводка запусков](017-spatial-health-ecs/checks.txt).

## Стоимость среза

Метод обычных нагрузок совпадает с [E3](016-composed-definitions.md): Release, SDK10.0.105/runtime10.0.5, macOS27.2 arm64/14logical processors, TieredCompilation=0,3последовательных запуска на1000/10000initial объектов,64warm-up/256измеряемых тиков. P95 исключает первый тик; allocations включают первый приказ, Spawn и рост ёмкости. Навигации/боя/рендера в этих нагрузках нет; memory/snapshot/hash сохраняются отдельно.

| Initial / workload | p95 E3 → E4a, мс | Δ p95 |
| --- | --- | --- |
| 1000 / units | 0.221 → 0.224 | +1.8% |
| 1000 / mixed | 1.665 → 1.697 | +1.9% |
| 1000 / production | 6.869 → 6.957 | +1.3% |
| 10000 / units | 5.293 → 5.311 | +0.3% |
| 10000 / mixed | 46.919 → 48.394 | +3.1% |
| 10000 / production | 50.342 → 51.015 | +1.3% |

Для навигации перед изменением Core сохранены E3 Release-бинарники с commit b825a8bba490e821fa1b05ef75dd1d12458cb38b. Они и новая сборка запущены одинаковым headless сценарием, с тем же контентом:1000×128тиков и10000×64тика, workers1, TieredCompilation=0, по3запуска, последовательно без параллельного test/performance прогона. Этот benchmark включает первый cold tick в p95; first tick показан отдельно. Локальный обход/геометрия и бюджеты не менялись.

| Count / ticks | p95 E3 → E4a, мс | Δ p95 | First tick E3 → E4a, мс |
| --- | --- | --- | --- |
| 1000 / 128 | 1.346 → 1.446 | +7.4% | 8.478 → 9.046 |
| 10000 / 64 | 40.836 → 41.269 | +1.1% | 204.790 → 209.535 |

Данные: [обычные нагрузки](017-spatial-health-ecs/after.json), [навигация до](017-spatial-health-ecs/nav-before.json), [после](017-spatial-health-ecs/nav-after.json), [сравнение](017-spatial-health-ecs/comparison.json). Все old/new trials каждой нагрузки имеют одинаковое финальное состояние. Обе группы проходят предварительный10% p95 gate. Это умеренная цена переходного доступа, а не доказательство ускорения навигации или устранения её cold spikes.

Во всех нагрузках runtime spatial/health больше не лежит в широком массиве; дополнительные columns и их capacity имеют собственную цену. Оставшиеся legacy readers используют Get<SpatialState>/Get<HealthState>, поэтому следующий срез должен убирать мост постепенно, а не создавать второй кэш authoritative данных. Результаты навигации64/128тиков не доказывают пропускную способность проходов или завершение движения всей толпы.

## Повторение

```sh
./tools/dev.sh test
./tools/dev.sh test --filter=ECS
./tools/dev.sh benchmark --ecs-benchmark --count=1000 --ticks=256
./tools/dev.sh benchmark --ecs-benchmark --count=10000 --ticks=256
./tools/dev.sh benchmark --navigation --count=1000 --ticks=128 --workers=1
./tools/dev.sh benchmark --navigation --count=10000 --ticks=64 --workers=1
./tools/dev.sh verify
```

Состояние benchmark не включает render/FPS. Native entity/index layouts, число jobs и время выполнения не становятся gameplay данными. Следующий срез — Orders/Motion/NavigationAgent, затем оставшиеся Combat/Presentation; завершение E4/E5 и удаление EntityState[] пока не объявлены.
