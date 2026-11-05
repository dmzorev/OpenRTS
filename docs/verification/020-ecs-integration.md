# 020. Завершение ECS-интеграции

Дата: 2026-11-05. [Решение019](../decisions/019-ecs-integration.md). Существующие unit механики используют Frent без EntityState, live views или per-ID route dictionary. Общие навигационные структуры, projectile pool и адаптерные ресурсы остаются специализированными.

## Что проверяется

Пять исходных покадровых golden traces сохранены без изменения ожидаемых hashes: direct, navigation, combat, buildings и production, с командами/событиями/Entity/Weapon/Projectile/Task poses. Форматы catalog7/direct7/nav11 прежние. Целевой ECS-прогон после перехода на native handles, component routes и domain spatial IDs:19проверок,0ошибок.

Новый интеграционный сценарий смешивает два archetypes: producers и обычные units. Во время полёта снаряда удаляется producer, создаются80объектов, растут native/handle/spatial ёмкости. Spatial query имеет иной порядок slots, чем handle list; cannon обязан попасть в физически ближайший producer, сохранив здоровье дальней цели. После удаления перехватившего объекта стрелок движется на его место и завершает точный маршрут. Это проверяет domain IDs кандидатов, повторное использование мест, SetEnd и Route после перестановок.

Существующие случаи дополнительно проверяют local detours при разном packed removal и росте, active FIFO/cooldown/burst/autocast/animation/owner, pinned frames после Dispose, same-exit production commits, combat death перед выпуском, forward/reverse workers, bounded queues, terrain changes и crowded destination completion. Производные body slots/generation не хешируются; borrowed BodyToken не хранится в компонентах или кадрах.

Финальный полный прогон:195 Core и20 DeclarativeContent проверок,0ошибок. Включены длительные crowded1000 и10000dense сценарии. Godot/headless verification прошёл: direct1/10000/10001 при15/120FPS и pacing, navigation1/10000 sequential/parallel/reverse, спрайты/combat/repair, RTS controls, здания/10000poses, abilities/production/rally/placement и160-тиковый journal, невалидные входы. [Сводка запусков](020-ecs-integration/checks.txt).

## Измерения

Baseline — сохранённая E4c Release assembly commit `af6d328f5457e25df98d9ce7ad415e6dfa2e6371`. Среда: SDK10.0.105/runtime10.0.5, macOS27.2 arm64,14logical processors, TieredCompilation=0. По3парных trial: before→after в нечётных, after→before в чётном, последовательно внутри каждой нагрузки, без одновременного test/Godot.

Units/mixed/production:1000/10000initial actors,64warm-up/256измеряемых тиков, p95 без первого тика. Initialization/retained memory/allocations/snapshot/hash отдельно. Spawn и рост включены в step allocations. Navigation:1000×128/10000×64, workers1, p95 включает cold tick; стенд не завершает всю толпу за это окно. Combat samples используют идентичную Release test assembly с old/new Core,8warm/16измеряемых тиков на10000stationary actors; это total time16тиков, не p95. Hash выполняется после окна.

Первое сравнение выявило рост малого navigation p95 сверх10%. Фазы Step дополнительно измерены на временных копиях обоих Core; profiling code не добавлен в репозиторий. Основная добавка пришлась на боевую фазу: вооружённые Owner0 всё равно делали spatial acquisition, хотя ValidTarget запрещает им атаковать/ремонтировать. Новый ранний отказ убирает только этот перебор. Дополнительно горячий шаг использует уже полученные refs/UnitDefinition и один BodyToken для collision/SetEnd, без повторного разрешения тех же компонентов.

## Результат окончательной сборки

| Initial / workload | p95 E4c → E5, мс | Изменение |
| --- | --- | --- |
| 1000 / units | 0.286 → 0.276 | -3.6% |
| 1000 / mixed | 2.056 → 2.082 | +1.3% |
| 1000 / production | 8.069 → 8.515 | +5.5% |
| 10000 / units | 6.384 → 6.423 | +0.6% |
| 10000 / mixed | 56.955 → 57.108 | +0.3% |
| 10000 / production | 59.678 → 61.161 | +2.5% |

| Navigation count / ticks | p95 E4c → E5, мс | Изменение | First tick E4c → E5, мс |
| --- | --- | --- | --- |
| 1000 / 128 | 1.748 → 0.634 | -63.7% | 9.787 → 7.071 |
| 10000 / 64 | 48.581 → 10.660 | -78.1% | 245.483 → 122.389 |

| Combat / 10000 actors | Total16тиков E4c → E5, мс | Изменение | Allocations в окне |
| --- | --- | --- | --- |
| Hitscan,52500выстрелов | 93.010 → 99.331 | +6.8% | 0 → 0байт |
| 10000летящих ракет | 133.069 → 143.773 | +8.0% | 0 → 0байт |

Все old/new trials каждой нагрузки имеют одинаковый State. Предварительный10%gate по p95 Step пройден: максимальный рост обычных нагрузок5.5%. Боевые total samples выросли6.8%/8.0%; это отдельная цена новых identity/domain lookups, не p95. Нельзя переносить эти цифры на плотный бой с движением/массовыми взрывами.

Большое снижение navigation времени связано с устранением бесполезного neutral acquisition, не с новым глобальным pathfinder. Историческая строка workload «no combat» описывала отсутствие выстрелов/урона, но не отсутствие вызовов оружейной фазы. Смешанный состав движущихся типов сохранён. Cold first tick10000по-прежнему примерно0.12секунды, завершение всей толпы за64тика не заявляется; N4 и throughput остаются отдельными задачами.

Retained managed memory после initialization units1000:1050064→1125888байт; units10000:9956824→10529056. Readonly identity column и ёмкости отдельных columns имеют стоимость, хотя миграционная структура удалена. У навигации добавлены собственный ID→body-slot mapping, cohort и tokens; actor-route dictionary удалён. Измерение памяти обычных нагрузок не заменяет полный working-set профиль навигации.

CopyPoses units10000:0.429→0.411мс; hash4.140→3.757мс. Это одиночные samples вне Step, не отдельные устойчивые benchmarks. Step allocations units1000:7640→8560байт, units10000:43640→44560за256тиков — одинаковая ограниченная добавка920байт, не per-entity/per-tick рост. Прогретый production membership и оба combat samples по-прежнему не имеют steady allocations. Spawn/capacity changes в mixed/production измерены отдельно в raw data.

Данные: [до](020-ecs-integration/before.json), [после и source/assembly SHA](020-ecs-integration/after.json), [navigation до](020-ecs-integration/nav-before.json), [после](020-ecs-integration/nav-after.json), [combat до](020-ecs-integration/combat-before.json), [после](020-ecs-integration/combat-after.json), [сравнение](020-ecs-integration/comparison.json). Абсолютные времена локальных запусков меняются с условиями; gate — предварительный критерий сопоставимых нагрузок, не обещание10% точности любых измерений.

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

Для измерений выше установлен `DOTNET_TieredCompilation=0`. Combat samples: Release executable OpenRTS.Tests с одним аргументом фильтра `--filter=10000 active shooters` либо `--filter=10000 live projectiles`. При old/new сравнении публичный Core API должен оставаться совместимым; исполняемая test assembly одинакова на обеих сторонах.

Это завершение миграции существующих механизмов, а не гарантия любой армии10000+ на25Гц. N4/clearance/глобальная инвалидация, реальные заторы, массовые взрывы, экономика/Player и конкретные заменяемые системы — отдельные задачи. Случаи, числа и ограничения фиксируются здесь, исторические отчёты прежних срезов не переписываются.
