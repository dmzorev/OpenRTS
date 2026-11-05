# 016. E3: compiled definitions и независимые UI Actions

Дата: 2026-11-05. [Решение015](../decisions/015-composed-definitions-and-actions.md), [оставшиеся этапы](../research/ecs-selection/migration.md).

## Реализация

Core: immutable EntityDefinition и пять typed capabilities; UnitDefinition — совместимый пространственный view. Каждая ECS entity юнита/здания получает ссылку на общий recipe. Текущие HP/движение/оружейное состояние не перенесены в definitions; mutable ProducerState остаётся отдельным компонентом.

Content: Actions.Definitions компилируется независимо от ActionPanel.Buttons. Старый Actions.Buttons поддерживается. Traits/Buildable подготавливаются один раз на concrete definition; default build parameters и ссылки разрешаются до матча. UnitCatalog.Definitions даёт рецепты в том же порядке, что Units. Sandbox переведён на новый YAML; UI/backend команд не потребовали изменения API.

Непространственная recipe проверена в независимом Frent store без Health/Spatial/Presentation. Это проверка состава данных, не реализация Player или универсального Spawn в SimulationWorld: существующий unit frontend по-прежнему требует пространственные capabilities.

## Проверки

Полный прогон:189 Core и20 DeclarativeContent проверок,0ошибок. Затем целевой Definitions-прогон:12 проверок, включая9новых,0ошибок. Целевой прогон уточняет remove/inheritance и раннюю проверку несовместимого production target после полного прогона.

Покрыты независимые gameplay/UI declarations и прежний формат; default/override build values; скрытое применение grenade и production через WorldCommand; отсутствие влияния UI на fingerprint; shared recipes с разными runtime queues; защита входных коллекций от последующей мутации; неизвестные refs и misplaced fields; mount/queue/presentation dependencies; удаление унаследованной панели и обычное поведение `{}`; дублирование action IDs между legacy/new; default hotkey collision.

Все5 golden traces из E0 — каждый тик состояния, команд, событий и опубликованных Entity/Weapon/Projectile/Task poses — совпали после изменения definitions и миграции YAML. Ожидаемые hashes не обновлялись. Форматы catalog7/direct7/nav11 сохранены.

Полный Godot/headless verification прошёл: content parity; direct1/10000/10001 при15/120FPS; pacing; navigation1/10000 sequential/parallel/reverse; zoom/playback; sprite/combat/repair presentation; RTS selection/commands; здания и10000поз; custom actions/production UI;160-тиковый production journal; невалидные входы. [Сводка запусков](016-composed-definitions/checks.txt).

## Измерения

Сравнение с итогом E0–E2 из [015/after.json](015-ecs-production/after.json). Тот же Release-стенд, SDK10.0.105/runtime10.0.5, macOS27.2 arm64/14logical processors, TieredCompilation=0. По3 последовательных запуска для1000/10000initial объектов;64warm-up тика на отдельном мире,256измеряемых. Измерения выполнялись после завершения полного/целевого тестового прогона, до запуска Godot verification.

Нагрузки `units`, `mixed`, `production` и их ограничения описаны в [предыдущем отчёте](015-ecs-production.md). P95 исключает первый тик; allocations включают реальные Spawn и расширение ёмкости. Нет terrain navigation, боя и рендера. Старые данные не пересчитывались под новую реализацию.

| Initial count / workload | Step p95, мс E0–E2 → E3 | Δ p95 |
| --- | --- | --- |
| 1000 / units | 0.217 → 0.221 | +1.6% |
| 1000 / mixed | 1.650 → 1.665 | +0.9% |
| 1000 / production | 6.773 → 6.869 | +1.4% |
| 10000 / units | 5.253 → 5.293 | +0.8% |
| 10000 / mixed | 47.346 → 46.919 | -0.9% |
| 10000 / production | 49.878 → 50.342 | +0.9% |

[Новые исходные результаты и hashes файлов](016-composed-definitions/after.json), [сравнение медиан](016-composed-definitions/comparison.json). Во всех6нагрузках все old/new trials имеют одинаковый финальный StateHash. Рост p95 остаётся в пределах предварительного10% gate; отсутствие заметного изменения Step не является обещанием производительности будущих E4 систем.

Новый recipe общий для типа; на сущность добавляется ECS reference column, а не копия всех capabilities. Горячие свойства UnitDefinition пока читаются через совместимый view. Стоимость памяти, создания, CopyPoses и Report сохранена в JSON отдельно. Эти данные не доказывают масштабирование плотного боя или готовность всех10000+ сценариев на25Гц.

## Повторение

```sh
./tools/dev.sh test
./tools/dev.sh test --filter=Definitions
./tools/dev.sh test --filter='ECS baseline'
./tools/dev.sh benchmark --ecs-benchmark --count=1000 --ticks=256
./tools/dev.sh benchmark --ecs-benchmark --count=10000 --ticks=256
./tools/dev.sh verify
```

Следующий этап — E4: по одному срезу переносить Spatial/Health, Orders/Motion/NavigationAgent, Combat/Presentation в ECS, сохраняя копии кадров, stable IDs и явные границы структурных изменений. Универсальный Player backend и модульный scripting не добавлялись.
