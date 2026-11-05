# Изолированные проверки ECS

Дата: 2026-11-05. **Это проверки API/совместимости, не RTS benchmark и не готовая миграция.** Собственный код probes — под MIT репозитория; библиотеки восстанавливаются из NuGet, исходники и бинарники зависимостей здесь не vendored. Основные csproj/solution не изменены.

Среда: macOS/arm64, .NET SDK10.0.105, net10.0, Godot4.7.2.stable.mono.official.ed1daf0bf. Console probes — Release; Godot — Debug/headless. Фиксированные версии и package hashes — в соседних packages.lock.json и [package evidence](../package-evidence.json).

| Проверка | Результат |
| --- | --- |
| Friflo3.6:10000 rows, ref query, managed state, explicit buffer/playback, nonspatial player, удалённый producer, stale handle | PASS |
| Friflo:1/4 external workers, обратный creation order, сортировка по domain ID | PASS, hash4946d6de00077ad4 |
| EasyEcs4.7:10000 rows, ref query, managed field, применение изменений после прохода, stale handle | PASS; analyzer EECS031 предупреждает о поштучном создании без Reserve |
| Frent0.9.1-beta:10000 rows, ref query, managed field, deferred Add до выхода из query, stale handle | PASS |
| Frent: nonspatial player, managed producer state, отсутствие удалённого producer в query,1/4 external workers, обратный creation order | PASS, hash4946d6de00077ad4 |
| Godot4.7.2/net10 + Frent0.9.1-beta, выполнение query из C# сцены | PASS, process exit0 |

Слово workers означает настройку MaxDegreeOfParallelism внешнего Parallel.For, а не доказанное число одновременно работавших CPU. Workers не трогают ECS, получают скопированные строки и возвращают результаты; owner сортирует по собственному ID и применяет. Совпадение hash проверяет этот конкретный протокол на простой целочисленной арифметике за4итерации. Полный бой, RNG, маршруты, разные ОС и journal OpenRTS здесь не моделируются.

Идентичные hashes Friflo/Frent относятся только к одинаковым Identity/X/Y probes, не к взаимозаменяемости библиотек. Friflo устанавливает LGPL runtime packages; это исследовательская ветка, не выбранная зависимость Core. Package lock отражает фактический restore на net10, а не все возможные TFM библиотеки.

## Повторный запуск

Из корня репозитория:

```sh
dotnet restore docs/research/ecs-selection/probe/frent/Probe.csproj --locked-mode
dotnet run --project docs/research/ecs-selection/probe/frent/Probe.csproj -c Release --no-restore
dotnet restore docs/research/ecs-selection/probe/easy/Probe.csproj --locked-mode
dotnet run --project docs/research/ecs-selection/probe/easy/Probe.csproj -c Release --no-restore
dotnet restore docs/research/ecs-selection/probe/friflo/Probe.csproj --locked-mode
dotnet run --project docs/research/ecs-selection/probe/friflo/Probe.csproj -c Release --no-restore
dotnet restore docs/research/ecs-selection/probe/godot/GodotProbe.csproj --locked-mode
dotnet build docs/research/ecs-selection/probe/godot/GodotProbe.csproj --no-restore
```

Затем запустить установленный Godot .NET:

```sh
"${OPENRTS_GODOT:-/Users/dmzorev/projects/games/Godot_mono.app/Contents/MacOS/Godot}" --headless --path docs/research/ecs-selection/probe/godot
```

Ожидается `PASS: Godot4.7.2/net10 Frent0.9.1-beta query` и exit0. Первый restore требует доступа к NuGet. `.godot`, bin/obj игнорируются Git. Эти проекты не подключены к обычному `tools/dev.sh test`; их наличие не добавляет ECS зависимости рабочему движку.

Программы намеренно создают временные массивы/списки и не измеряют elapsed/allocations. Они не являются рекомендуемым размещением буферов горячей системы. Полный baseline и измерение parity-пилота — E0/E2 из [плана](../migration.md).
