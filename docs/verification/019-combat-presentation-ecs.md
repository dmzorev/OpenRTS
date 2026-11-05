# 019. E4c: Combat, Presentation и Ownership в ECS

Дата: 2026-11-05. [Решение018](../decisions/018-combat-presentation-components.md). Mount state, сводный cooldown, семантическое представление и Owner перенесены в Frent. Mapping и оставшаяся навигационная интеграция ещё не завершены.

## Корректность

Финальный полный прогон:194 Core и20 DeclarativeContent проверок,0ошибок. Пять покадровых golden traces сохранены без изменения ожидаемых hashes: direct, navigation, combat, buildings и production, включая команды, события и Entity/Weapon/Projectile/Task poses. Форматы catalog7/direct7/nav11 прежние.

Новый lifecycle-сценарий сравнивает два одинаковых мира. У одного удаляется actor, затем80Spawn заставляют расти legacy/native storage. Frent и legacy packed removal перемещают разных actors. Сравнение продолжает60тиков: активная серия, windup/cooldown, autocast, направления, animation override, visual states, owner и здоровье целей сохраняются. У новых actors собственные default autocast/idle/owner/states, нет чужого cooldown, цели или burst. Отключение autocast одного бойца не влияет на второго. Закреплённый frame с EntityPose и WeaponPose переживает рост, перестановки и Dispose.

Существующие проверки покрывают simultaneous damage/repair/death, projectile после смерти стрелка, collision/splash/размеры, стрельбу на ходу, независимые mounts, производство/двери, framed leases, безопасную отмену, последовательный/параллельный поиск и завершение crowded1000. После прогрева при неизменной producer membership нет steady tick allocations.

Полный Godot/headless verification прошёл: контент; direct1/10000/10001 при15/120FPS и pacing; navigation1/10000 sequential/parallel/reverse; sprite/combat/repair presentation; RTS controls; buildings/10000poses; abilities/production/rally/placement и160-тиковый journal; явные ошибки невалидных входов. [Сводка запусков](019-combat-presentation-ecs/checks.txt).

## Метод измерений

Baseline — сохранённые E4b Release binaries commit `9bc273b16d1b677fdd4e400b46d6fc20cdc2fc02`. Новая сборка использует тот же контент и настройки: SDK10.0.105/runtime10.0.5, macOS27.2 arm64,14logical processors, TieredCompilation=0. Тесты и Godot не выполняются одновременно с измерениями.

Обычные нагрузки:1000/10000initial actors,64warm-up/256измеряемых тиков, units/mixed/production без навигации/боя/рендера. P95 исключает первый тик. Allocations включают первый приказ, реальный Spawn и рост ёмкостей. Navigation — прежний compact-group сценарий1000×128/10000×64, workers1; его p95 включает cold tick, который также сохраняется отдельно.

Боевые нагрузки используют существующие проверки WeaponChecks в одной и той же Release test assembly с прежней либо новой Core assembly. Их SHA записаны рядом с результатами. Каждая имеет8warm-up/16измеряемых тиков:10000stationary actors в разнесённых парах двух владельцев; hitscan либо10000долгоживущих ракет. Hash считается после измеряемого окна, не входит во время Step. Это суммарное время16тиков, **не p95**; оно дополняет обычные и навигационные p95, не заменяет их.

Первый перенос выявил избыточные обращения к новым компонентам у безоружных actors. Per-mount фаза теперь пропускает неизменяемые определения без mounts; обработка приказов откладывает доступ к представлению до проверки применимого оружия/режима. Последующие измерения с раздельными сериями «все before, затем все after» заметно сдвигались целиком, поэтому окончательное сравнение использует парные запуски: before→after в нечётных trial, after→before в чётном, внутри каждой нагрузки. По3trial, в таблицах — медианы. Это снижает влияние последовательного изменения условий; точность10% всех локальных измерений не обещается.

Парное сравнение также обнаружило отдельную цену legacy component views на малой нагрузке прямого движения. Финальный UpdatePresentation переведён на закрытый5-компонентный запрос; idle/move IDs подготовлены один раз в immutable PresentationDefinition. Порядок строк безопасен: позиции всех actors уже рассчитаны, а изменения представления относятся только к своей entity. Целевой ECS-прогон после этого изменения сохранил все18проверок и пять golden traces.

## Результаты окончательной сборки

| Initial / workload | p95 E4b → E4c, мс | Изменение |
| --- | --- | --- |
| 1000 / units | 0.274 → 0.301 | +9.6% |
| 1000 / mixed | 2.094 → 2.003 | -4.3% |
| 1000 / production | 8.387 → 8.310 | -0.9% |
| 10000 / units | 6.948 → 6.860 | -1.3% |
| 10000 / mixed | 60.439 → 59.155 | -2.1% |
| 10000 / production | 65.297 → 65.274 | 0.0% |

| Navigation count / ticks | p95 E4b → E4c, мс | Изменение | Cold first tick E4b → E4c, мс |
| --- | --- | --- | --- |
| 1000 / 128 | 2.226 → 1.829 | -17.8% | 10.924 → 10.267 |
| 10000 / 64 | 52.893 → 49.631 | -6.2% | 237.985 → 247.036 |

| Combat / 10000 actors | Всего16тиков E4b → E4c, мс | Изменение | Allocations в окне |
| --- | --- | --- | --- |
| Hitscan,52500выстрелов | 87.806 → 94.580 | +7.7% | 0 → 0байт |
| 10000летящих ракет | 140.656 → 139.287 | -1.0% | 0 → 0байт |

Во всех old/new trials каждой нагрузки финальный State одинаков. Предварительный10%gate по p95 Step пройден; малая units-нагрузка близка к границе (+9.6%), поэтому при следующем срезе её нужно продолжать измерять. Боевой sample сравнивает суммарное время16тиков, а не p95. Снижение навигационного p95 в этом запуске не доказывает универсального ускорения pathfinding: алгоритмы/бюджеты не менялись, холодный пик10000остаётся примерно0.24секунды. Нагрузка64/128тиков не доказывает завершение движения всей толпы.

Цена хранения и публикации измерена отдельно. Retained managed memory после инициализации units1000:993760→1050064байт; units10000:8995992→9956824байт. Дополнительные columns и их capacity имеют стоимость, несмотря на уменьшение legacy массива. CopyPoses10000:0.344→0.407мс; hash4.381→4.668мс. Короткие snapshot/hash samples чувствительны к условиям запуска и не заменяют отдельного устойчивого профиля публикации.

Step allocations units1000:5992→7640байт, units10000:41992→43640за256тиков. Одинаковая добавка1648байт относится к ограниченной регистрации новых queries в мире, а не к выделению на каждого actor/тик. Проверки прогретого неизменного production membership и оба боевых sample по-прежнему дают0steady allocations. Mixed/production включают реальные Spawn, рост разных columns и первый приказ; их allocations сохранены полностью.

Данные: [до](019-combat-presentation-ecs/before.json), [после, SHA исходников и assemblies](019-combat-presentation-ecs/after.json), [навигация до](019-combat-presentation-ecs/nav-before.json), [после](019-combat-presentation-ecs/nav-after.json), [бой до](019-combat-presentation-ecs/combat-before.json), [после](019-combat-presentation-ecs/combat-after.json), [сравнение](019-combat-presentation-ecs/comparison.json). Таблицы описывают выбранные нагрузки и конкретную среду, а не любую армию10000+ на25Гц.

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

Release combat samples: build `tests/OpenRTS.Tests/OpenRTS.Tests.csproj -c Release`, затем запуск executable с `--filter=10000 active shooters` или `--filter=10000 live projectiles` (значение фильтра передаётся одним аргументом). Для чисел этого отчёта установлен `DOTNET_TieredCompilation=0`. При сравнении прежнего Core public API должен оставаться совместимым; новая test assembly здесь побайтово одинакова для обеих сторон.

Следующий этап — замена legacy mapping/live views, MotionIndex и per-ID group routes. ECS не устраняет cold navigation spikes и заторы автоматически; результаты разнесённых stationary пар не обещают такую же скорость плотной движущейся армии, массовых взрывов или Godot с10000эффектов.
