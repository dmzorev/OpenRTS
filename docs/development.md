# Сборка и проверки OpenRTS

## Окружение

Установите .NET SDK 10.0.105 или более новый patch той же feature band и Godot 4.7.2 .NET. Скрипт для macOS и Linux использует Bash, для Windows нужен PowerShell 7.3 или новее (`pwsh`). Для сквозных проверок и команд сборки карт нужен Python 3.9+. Для `verify-wan --netem` нужен Docker с Linux-контейнерами. Первый запуск восстанавливает зависимости из NuGet. [Версия SDK](../global.json) · [Версия Godot SDK](../sandbox/OpenRTS.Sandbox.csproj).

Оба скрипта сначала используют `OPENRTS_GODOT`, затем ищут Godot в `PATH` под именами `godot-mono`, `godot4-mono`, `godot` или `godot4`. На macOS скрипт также ищет соседний `Godot_mono.app` и приложения Godot в `/Applications` и `~/Applications`. На Windows скрипт использует консольный `*_console.exe`, если он лежит рядом с указанным `.exe`. Для установки по другому адресу задайте `OPENRTS_GODOT` полным путём к исполняемому файлу.

При работе с локальным NuGet feed задайте `OPENRTS_NUGET_SOURCE`. Godot SDK должен быть доступен SDK resolver через NuGet или локальный кэш `.packages`.

## Команды разработки

```sh
./tools/dev.sh build
./tools/dev.sh test
./tools/dev.sh headless --mod=sandbox/game.yaml
./tools/dev.sh verify
./tools/dev.sh editor
./tools/dev.sh run
```

В PowerShell на Windows используются те же команды и аргументы.

```powershell
$env:OPENRTS_GODOT = 'C:\Tools\Godot\Godot.exe'
pwsh -File .\tools\dev.ps1 build
pwsh -File .\tools\dev.ps1 test
pwsh -File .\tools\dev.ps1 run --buildings
pwsh -File .\tools\dev.ps1 scene-map-pack 'C:\Temp\openrts.pck'
```

- `test` собирает решение и запускает три исполняемых проекта проверок. Используйте эту команду для запуска набора целиком.
- `verify` проверяет импорт библиотеки, контент, движение, навигацию и воспроизводимость в двух хостах. `run` и `verify` импортируют ресурсы Godot после сборки.
- `headless` запускает симуляцию без Godot. `run` открывает игровой sandbox, а `editor` — его проект в Godot. [Полигоны и сценарии](sandbox.md) · [Проверка сборки карт](map-authoring.md#сборка-локальный-запуск-и-повтор).

## Сравнение прогонов и замеры

```sh
./tools/dev.sh headless --simulate --count=10000 --ticks=64
./tools/dev.sh run --verify-simulation --count=10000 --ticks=64
./tools/dev.sh run --navigation --workers=4
./tools/dev.sh headless --navigation --workers=1 --count=10000 --ticks=64
./tools/dev.sh headless --production --ticks=160
./tools/dev.sh run --verify-actions
./tools/dev.sh benchmark --count=10000 --ticks=128
./tools/dev.sh benchmark --navigation --workers=4 --count=10000 --ticks=128
./tools/dev.sh benchmark --size-benchmark --count=10000 --ticks=128 --workers=1
```

`benchmark` собирает Release headless. Основной замер охватывает `World.Step` без публикации кадров и графики. Команда устанавливает `DOTNET_TieredCompilation=0`, чтобы исключить смену уровня JIT в коротком опыте. [Отчёты о проверках и замерах](verification/).

## Игровой манифест и ресурсы

Манифест [sandbox/game.yaml](../sandbox/game.yaml) подключает отдельные файлы и рекурсивные YAML-директории. Оба хоста принимают `--mod=path`. Настройки запуска карт и меню матча находятся в [sandbox/content/settings](../sandbox/content/settings/). [Контракт манифеста](decisions/035-game-manifest.md) · [План параметров](research/game-configuration/README.md).

Игровые определения лежат в каталогах [units](../sandbox/content/units/) и [weapons](../sandbox/content/weapons/). Представление настраивается через [models](../sandbox/content/models/) и [effects.yaml](../sandbox/content/effects.yaml). Ресурсы SpriteFrames и Texture2D находятся в [sandbox/resources](../sandbox/resources/). [Семантика представления](decisions/007-presentation.md) · [Источники графики](assets/README.md).
