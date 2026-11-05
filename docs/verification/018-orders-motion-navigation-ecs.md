# 018. E4b: Orders, Motion и NavigationAgent в ECS

Дата: 2026-11-05. [Решение017](../decisions/017-orders-motion-navigation-components.md). Перенесён второй срез E4; Combat/Presentation и окончательный mapping/legacy cleanup остаются впереди.

## Поведение и жизненный цикл

OrderState, MotionState и NavigationAgentState имеют единственную mutable копию в Frent. В legacy EntityState остались временные ref-return обращения; эти структуры не являются snapshot и не переживают native Delete. FIFO и локальный массив принадлежат одному actor. Queries не выполняют structural changes, refs не пересекают ожидание навигационных jobs.

Пять исходных golden traces сохранились без изменения ожидаемых hashes: каждый тик сравнивает state, команды, события и Entity/Weapon/Projectile/Task poses в direct, navigation, combat, buildings и production. Форматы catalog7/direct7/nav11 прежние. Sequential/parallel/reverse порядок jobs, корректировка целей, активация queued waypoints, таймаут уступания, динамические footprints и поведение боевых приказов проверяются существующими сценариями.

Новая проверка сравнивает actor с неизменённым контрольным миром после удаления чужого queued actor и80Spawn на одном тике. Legacy/native removal переставляет разные объекты, обе ёмкости растут. Проверяются активный приказ, FIFO, дробный остаток скорости333/1024, движения в двух archetypes и отсутствие у новых actors унаследованных приказов/движения. Оба surviving actor завершают собственные маршруты.

Проверка opposing detours сохранена для прежней узкой карты и дополнена большой картой с40Spawn во время движения. В двух мирах удаляются одинаковые actors в обратном порядке; сравниваются hashes каждого тика, безопасность интерполяции и завершение маршрута. Проверка подтверждает, что локальный поиск действительно выполнялся. В первом полном прогоне новая проверка ошибочно смотрела сбрасываемый per-tick счётчик только в конце; исправлена на накопление за сценарий, после чего оба варианта прошли в целевом ECS-прогоне.

Финальный полный прогон:193 Core и20 DeclarativeContent проверок,0ошибок. Целевой ECS-прогон:17проверок,0ошибок. Длительный crowded1000 сценарий завершил движение и заполнил всю целевую область. Полный Godot/headless verification также прошёл: content parity; direct1/10000/10001 при15/120FPS и pacing; navigation1/10000 sequential/parallel/reverse; presentation/combat/repair, управление, здания/10000poses, abilities/production/rally/placement и160-тиковый journal; невалидные входы. [Результаты запусков](018-orders-motion-navigation-ecs/checks.txt).

## Стоимость

Перед изменением сохранены E4a Release binaries из commit `c857b0a207084dbdd5796917ab1c5906006e4429`. Старый и новый executable запущены с одинаковым контентом: SDK10.0.105/runtime10.0.5, macOS27.2 arm64,14logical processors, TieredCompilation=0. По3последовательных trial; тесты/Godot не выполнялись одновременно с измерениями. В таблицах — медианы trial.

Обычные нагрузки:1000/10000initial actors,64warm-up/256измеряемых тиков, без navigation/combat/render. P95 исключает первый тик. Mixed содержит5%производителей, production —100%; реальное производство увеличивает число actors. Allocations включают первый приказ, Spawn и рост ёмкостей.

| Initial / workload | p95 E4a → E4b, мс | Изменение |
| --- | --- | --- |
| 1000 / units | 0.222 → 0.233 | +4.9% |
| 1000 / mixed | 1.682 → 1.768 | +5.1% |
| 1000 / production | 6.859 → 6.985 | +1.8% |
| 10000 / units | 5.290 → 5.555 | +5.0% |
| 10000 / mixed | 47.971 → 48.869 | +1.9% |
| 10000 / production | 50.141 → 51.487 | +2.7% |

Навигация: одинаковый compact-group сценарий,1000×128/10000×64тиков, workers1. Здесь p95 включает cold tick; он также показан отдельно. Геометрия, цели, бюджеты и алгоритмы не менялись.

| Count / ticks | p95 E4a → E4b, мс | Изменение | First tick E4a → E4b, мс |
| --- | --- | --- | --- |
| 1000 / 128 | 1.407 → 1.482 | +5.3% | 8.958 → 9.247 |
| 10000 / 64 | 40.647 → 42.385 | +4.3% | 205.676 → 211.130 |

Во всех old/new trials одной нагрузки финальный State совпал. Предварительный10%p95 gate пройден. Это цена текущей интеграции, а не ускорение навигации. Эти64/128тиков не доказывают завершение движения всей толпы или пропускную способность проходов; полный crowded1000 сценарий проверяет завершение отдельно.

Раздельные columns и их capacity увеличили retained managed memory после инициализации: units1000 —801472→993760байт; units10000 —7748816→8995992байт. Эти direct worlds не выделяют local path массивы. В navigation мире отдельный массив16точек на actor добавляет заголовок массива вместо прежнего общего capacity-буфера; потери и преимущества этой схемы не следует сводить только к размеру полей struct.

Snapshot и hash измерены отдельно. Для units10000 CopyPoses вырос0.212→0.341мс: он разрешает ещё три компонента через переходный bridge. Это заметный относительный рост при небольшом абсолютном времени. Hash3.858→3.836мс. На более длинных mixed/production workloads выигрыш меньшего legacy массива при canonical копировании/сортировке может перекрывать дополнительные обращения, но отдельные короткие snapshot/hash samples не являются самостоятельным устойчивым performance benchmark.

В units step allocations5208→5992байт на1000 и41208→41992на10000 за256тиков: добавилась ограниченная инициализация query, а не выделение на каждую entity/тик. Проверка неизменной producer membership после прогрева по-прежнему контролирует отсутствие steady tick allocations. У mixed/production allocations меняются из-за роста разных columns и количества Spawn; все сырые значения сохранены.

Данные: [до](018-orders-motion-navigation-ecs/before.json), [после и SHA исходников](018-orders-motion-navigation-ecs/after.json), [навигация до](018-orders-motion-navigation-ecs/nav-before.json), [после](018-orders-motion-navigation-ecs/nav-after.json), [сравнение](018-orders-motion-navigation-ecs/comparison.json). Для units/mixed/production приведены также initialization, retained memory, allocations, snapshot/hash. Это измерение выбранных сценариев, без обещания любой армии10000+ на25Гц.

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

Для замеров выше установлен `DOTNET_TieredCompilation=0`. Следующий срез — Combat/Presentation; domain mapping для MotionIndex и удаление оставшегося EntityState ещё не выполнены. N4 и оптимизация заторов остаются отдельными задачами.
