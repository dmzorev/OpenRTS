# Набор графики полигона

## 3D-полигон

В [AssetShowcase.tscn](../../sandbox3d/AssetShowcase.tscn) собраны двенадцать типов зданий и четыре юнита. Для просмотра можно открыть сцену через `./tools/dev.sh editor3d res://AssetShowcase.tscn` и запустить её клавишей F6. Размеры и повороты экземпляров в витрине заданы для сравнения силуэтов.

Центр снабжения, бараки и завод изготовлены в Blender по приложенным концептам. Редактируемые сцены, предпросмотр и скрипты сборки находятся в соседнем репозитории в `Assets/buildings/supply-center` и `Assets/buildings/production-buildings`. [Центр снабжения](../../sandbox3d/assets/supply-center/supply-center.glb) · [Бараки](../../sandbox3d/assets/production-buildings/barracks.glb) · [Завод](../../sandbox3d/assets/production-buildings/factory.glb)

| Роль | Ресурс в `sandbox3d/assets` | Происхождение |
| --- | --- | --- |
| Электростанция, радар, кран, вертолётная площадка | `infrastructure/*.glb` | Модели OpenRTS по концептам из соседнего репозитория `Assets/buildings/base-infrastructure-concepts`. Исходники Blender и рецепты в `Assets/buildings/infrastructure`. |
| Шахта | `infrastructure/mine.glb`, `infrastructure/mine-upgraded.glb` | Общая площадка 3×4, открытый копёр у нейтральной шахты и высокая надстройка с погрузчиком после улучшения. Исходник Blender и рецепт в `GameAssets/buildings/infrastructure/mine`. |
| Нефтяная вышка | `infrastructure/oil-derrick.glb` | Решётчатая мачта с площадками обслуживания, нефтехранилище и насосный блок на основании 3×3. Исходник Blender и рецепт в `GameAssets/buildings/infrastructure/oil-derrick`. |
| Аэродром | `infrastructure/airfield.glb` | Модель OpenRTS с компоновкой по референсу USA Airfield из C&C Generals. Исходник Blender в `Assets/buildings/infrastructure/airfield`. |
| Истребитель | `vehicles/fighter.glb` | Оригинальный самолёт со стреловидным крылом, двумя килями и отдельным мешем шасси. Исходник Blender и рецепт в `GameAssets/vehicles/fighter`. |
| Конвертоплан поддержки | `vehicles/tiltrotor.glb` | Кольцевые винты, носовой автомат и сквозной десантный отсек на четыре места. Исходник Blender в `GameAssets/vehicles/tiltrotor-family`. |
| Ракетный конвертоплан | `vehicles/missile-tiltrotor.glb` | Узкая кабина, закрытые борта и два ракетных блока на коротких крыльях. Исходник Blender в `GameAssets/vehicles/tiltrotor-family`. |
| Тяжёлый воздушный транспорт | `vehicles/heavy-air-transport.glb` | Четыре поворотные мотогондолы с кольцевыми винтами, отдельная кормовая аппарель и трюм под технику. Исходник Blender в `GameAssets/vehicles/heavy-air-transport`. |
| Бункер | `infrastructure/bunker.glb` | Шестиугольный корпус OpenRTS с одним узлом `TurretMount`. Исходник Blender в `Assets/buildings/infrastructure/bunker`. |
| Стелс-генератор и генератор щита | `infrastructure/stealth-generator.glb`, `infrastructure/shield-generator.glb` | Модели OpenRTS по концептам инфраструктуры. Исходники Blender в `Assets/buildings/infrastructure`. |
| Нефтепереработка | `kenney/refinery.glb` | `building-c` из [Kenney City Kit (Industrial) 2.0](https://kenney.nl/assets/city-kit-industrial). GLB использует общий `Textures/colormap.png`. |
| Средний танк | `vehicles/MediumTank.tscn` | Корпус и башня OpenRTS в отдельных GLB. Исходник Blender и рецепт в соседнем репозитории `Assets/vehicles/medium-tank`. |
| Зенитно-противопехотный танк | `vehicles/MixedTank.tscn` | Шасси среднего танка и новая башня с четырьмя стволами, отдельным ракетным блоком и радаром. Исходник Blender, рецепт и предпросмотр в `GameAssets/vehicles/mixed-tank`. |
| Гусеничный БТР | `vehicles/TrackedAPC.tscn` | Корпус и башня OpenRTS в отдельных GLB. Исходник Blender и рецепт в соседнем репозитории `Assets/vehicles/tracked-apc`. |
| Багги-разведчик MRAP | `vehicles/RadarBuggy.tscn` | Шасси 4×4 с кормовым радаром и отдельный пулемётный модуль. Исходник Blender и рецепт в `GameAssets/vehicles/radar-buggy`. |
| Грузовик 6×6 | `vehicles/EngineeringTruck.tscn`, `SupplyTruckEmpty.tscn`, `SupplyTruckLoaded.tscn`, `SalvoTruck.tscn`, `CruiseTruck.tscn` | Общее шасси с анимацией колёс и пять сменных модулей. Сборки доступны в `AssetShowcase.tscn`. Исходник Blender и рецепт в соседнем репозитории `Assets/vehicles/military-truck`. |
| Колёсная САУ и мобильное ПВО | `vehicles/WheeledArtillery.tscn`, `vehicles/WheeledAirDefense.tscn` | Оригинальное шасси 6×6, две поворотные башни и отдельная головка радара. Исходник Blender и рецепт в `Assets/vehicles/wheeled-artillery`. |
| Инженерная машина | `quaternius/engineer.glb` | `Tank4.glb` из того же зеркала. В Blender 5.3 удалён меш `Tank_Gun`, применён масштаб оставшегося `Tank_Turret` и переэкспортированы четыре клипа гусениц. [Исходник Blender](sources/sandbox3d-engineer.blend) · [Папка оригинальных .blend](https://drive.google.com/drive/folders/1MsH24X-roZs9c_UILBbchk2Ue5cMeE9A). |
| Пехотинец | `quaternius/Infantry.tscn`, `quaternius/swat-armored.glb` | Бронированный SWAT на базе Quaternius Ultimate Modular Men (CC0). Восемь клипов перенесены из Mixamo, Universal Animation Library (CC0) и исходной модели. В игровом сценарии используются стойка и бег с винтовкой у корпуса. [Происхождение и контрольные суммы](swat-infantry.json) |
| Снайпер | `quaternius/Sniper.tscn`, `quaternius/sniper.glb` | Бронированный снайпер с боковой оптикой, спинной плитой и длинной винтовкой. Восемь клипов на исходном риге доступны в витрине. [Происхождение и контрольные суммы](sniper-infantry.json) |
| Тяжёлый гранатомётчик | `quaternius/Rocketeer.tscn`, `quaternius/rocketeer.glb` | Стойка, бег на месте и выстрел. Труба закреплена на грудной кости и движется вместе с плечом и кистями. [Происхождение и контрольные суммы](rocketeer-infantry.json) |

Модели Kenney и Quaternius распространяются по CC0-1.0. Уведомления авторов лежат рядом с GLB в [каталоге Kenney](../../sandbox3d/assets/kenney/LICENSE.txt) и [каталоге Quaternius](../../sandbox3d/assets/quaternius/LICENSE.txt). Для инженерной машины проверена геометрия с однотонными материалами. В исходном кузове и гусеницах есть раздельные вершины на швах и скрытые пары граней, UV-развёрток нет.

### SHA-256 исходников

| Файл | SHA-256 |
| --- | --- |
| `kenney_city-kit-industrial_2.0.zip` | `5b381164e5760f3830a2dbee43b972deee38b2a695d091b56e238ab2910c96d2` |
| `Universal_Animation_LibraryStandard.zip` | `18ff1a7215f4852b320203e8aaf02a1578b5c8eef9027fbaedfcedc7b85a3ac2` |
| `Tank2.glb` | `d46acaeb563f401bfb389bd7708342f97d0cd62f9b2c6dc88f8537be8c85ed80` |
| `Tank3.glb` | `1763818d11cb5cef32dcbe9cf52af108710310c034591e9bdf4555847e73e979` |
| `Tank4.glb` | `dd99da632fd89cae38aa6bc4af4179364bb592f87fac3e2abac4c60af55f2f1a` |
| `pistol.glb` | `4622ab2909aa0f4e88b74a13f52f9e28183a6ff5fca5896fc7e98d44008f2148` |

В [сцене гражданского квартала](../../sandbox3d/CivilianShowcase.tscn) показаны семь зданий в обычном, гарнизонном и повреждённом состояниях. Откройте сцену в Godot и запустите её клавишей F6. Исходники Blender и рецепт находятся в `GameAssets/buildings/civilian`.

## 2D-полигон

У тяжёлого транспорта 32 изометрических ракурса для каждого положения аппарели и две фазы винтов в полёте. Аппарель опущена на земле и закрыта в воздухе. Вид сверху использует ту же модель и оба состояния. [Наземный атлас тяжёлого транспорта](../../sandbox/assets/units/isometric/heavy-air-transport.png) · [Полётный атлас тяжёлого транспорта](../../sandbox/assets/units/isometric/heavy-air-transport-airborne.png)

У обоих конвертопланов по 32 ракурса на земле и в полёте. Кольца наклоняются вперёд, винты используют две фазы вращения. У транспорта `tiltrotor` на земле открыт десантный отсек, а эффект выстрела выходит из автомата под носом. У `missile-tiltrotor` эффект запуска привязан к правому блоку. [Транспорт поддержки](../../sandbox/assets/units/isometric/tiltrotor.png) · [Ракетная версия](../../sandbox/assets/units/isometric/missile-tiltrotor.png)

У `fighter` по 32 изометрических ракурса с выпущенным и убранным шасси. Состояние `airborne` переключает атласы с общей опорной точкой. Запуск ракеты начинается из точки под фюзеляжем и учитывает курс и высоту. [Наземные ракурсы](../../sandbox/assets/units/isometric/fighter.png) · [Полётные ракурсы](../../sandbox/assets/units/isometric/fighter-airborne.png)

У автоматчика в изометрии десять атласов по восемь направлений. Кадры сняты с общей опорной точкой, а панели перекрашиваются шейдером по владельцу. Трассер начинается у ствола первого кадра выстрела и следует направлению оружия. В атласе `throw-windup` граната в кисти. С первого кадра `throw-release` граната летит по дуге из точки выпуска соответствующего ракурса. Источник Blender находится в соседнем репозитории `GameAssets/characters/quaternius-infantry-v001`. [SpriteFrames](../../sandbox/resources/units/isometric/swat-rifleman.tres) · [Манифест с параметрами экспорта](swat-infantry.json)

Воспроизведение из корня OpenRTS с Blender и Python с Pillow:

```sh
work=$(mktemp -d)
../GameAssets/tools/blender.sh -P "$PWD/tools/render_swat_art.py" -- \
  "$PWD/../GameAssets/characters/quaternius-infantry-v001/swat-armored.blend" "$work" \
  "$PWD/../GameAssets/characters/quaternius-infantry-v001/Swat-comparison.blend"
python3 tools/pack_swat_art.py "$work"
```

У снайпера восемь направлений для каждой из восьми анимаций. В матче используются стойка, бег с винтовкой у корпуса, прицеливание во время подготовки и выстрел. Трассер выходит из дула. Наплечники, броневоротник и спинная плита принимают цвет владельца. [SpriteFrames](../../sandbox/resources/units/isometric/sniper.tres) · [Манифест](sniper-infantry.json)

Для сборки снайпера используется тот же пайплайн с флагом `--sniper`.

```sh
work=$(mktemp -d)
../GameAssets/tools/blender.sh -P "$PWD/tools/render_swat_art.py" -- \
  "$PWD/../GameAssets/characters/sniper-armored-v001/sniper-armored.blend" "$work" --sniper
python3 tools/pack_swat_art.py "$work" --sniper
```

У гранатомётчика четыре последовательности по восемь направлений. Подготовка и выстрел используют один Blender Action, разделённый маркером `Launch`. Точка запуска измерена на первом кадре выстрела. Наплечники и спинная плита принимают цвет владельца. [SpriteFrames](../../sandbox/resources/units/isometric/rocketeer.tres) · [Манифест](rocketeer-infantry.json)

```sh
work=$(mktemp -d)
../GameAssets/tools/blender.sh -P "$PWD/tools/render_swat_art.py" -- \
  "$PWD/../GameAssets/characters/rocketeer-animated-v001/rocketeer-animated.blend" "$work" --rocketeer
python3 tools/pack_swat_art.py "$work" --rocketeer
```

У `radar-buggy` по 32 изометрических ракурса корпуса и пулемётной турели. Колёса имеют две фазы движения. Турель использует направление оружейного канала `main`, а эффект выстрела привязан к дульной точке. [Атлас корпуса](../../sandbox/assets/units/isometric/radar-buggy-hull.png) · [Атлас турели](../../sandbox/assets/units/isometric/radar-buggy-turret.png)

У среднего танка в изометрии по 32 ракурса корпуса и башни, две фазы гусениц в движении и командные панели. Поле `MuzzleLocal` задаёт точку вспышки в единицах сцены относительно опорной точки. Она поворачивается вместе с направлением текущего кадра башни. [Корпус](../../sandbox/assets/units/isometric/medium-tank-hull.png) · [Башня](../../sandbox/assets/units/isometric/medium-tank-turret.png)

У `tank-mixed` корпус среднего танка и три отдельных атласа по 32 ракурса. Пушечный блок и ракетная надстройка ориентируются по своим оружейным каналам. Каждому каналу соответствует собственная точка выстрела. Радар вращается вокруг крепления на пушечной башне. Модели содержат 4 040, 1 824 и 540 треугольников соответственно. Проверка геометрии пройдена с допустимыми плоскими многоугольниками и скрытыми стыками деталей. [Изометрические модели](../../sandbox/content/mods/isometric/models.yaml)

Гусеничный БТР использует корпус с двумя фазами гусениц, общую точку крепления и три взаимозаменяемые башни. Корпус и каждый модуль имеют по 32 направления. Переднее крепление поворачивается вместе с корпусом, башня следует направлению наведения. [Атлас корпуса](../../sandbox/assets/units/isometric/tracked-apc-hull.png) · [Пулемётная башня](../../sandbox/assets/units/isometric/tracked-apc-turret.png) · [Ракетная башня](../../sandbox/assets/units/isometric/tracked-apc-missiles.png) · [Пушечная башня](../../sandbox/assets/units/isometric/tracked-apc-cannon.png)

Модульный грузовик использует общее шасси для строителя, снабжения, MLRS и Tomahawk. У всех деталей 32 направления, у шасси две фазы движения колёс. Кабина и палуба имеют отдельные спрайты для перекрытия задних модулей. Кузов переключается по состоянию `loaded`. Поворот крана следует за стройплощадкой или ремонтом, установки вращаются по направлению оружейного крепления. `FarLayer` задаёт слой детали, когда её точка крепления находится за корпусом относительно камеры. Для готовой ракеты Tomahawk `MuzzleLocal` задаёт опору хвоста на направляющей. [Изометрические модели](../../sandbox/content/mods/isometric/models.yaml)

Колёсная САУ и мобильное ПВО используют одно шасси, поворотные оружейные модули и отдельный радар ПВО. У корпуса, кабины, обеих башен и радара по 32 направления. Точка радара следует за поворотом ракетной установки, сам радар вращается вокруг неё независимо. В изометрическом моде обе машины доступны в очереди завода. [Изометрические модели](../../sandbox/content/mods/isometric/models.yaml)

У шахты две изометрические модели по четыре четверть-оборота. Состояние `owned` заменяет открытый копёр улучшенной шахтой с высокой надстройкой, конвейером и цветом владельца. Обе версии используют одну опорную точку в центре площадки 3×4. [Обычная шахта](../../sandbox/assets/buildings/isometric/mine.png) · [Улучшенная шахта](../../sandbox/assets/buildings/isometric/mine-upgraded.png)

Нефтяная вышка использует четыре ракурса с опорой на центр площадки 3×3. Панели на оголовке, резервуаре и насосном блоке принимают цвет владельца. [Атлас вышки](../../sandbox/assets/buildings/isometric/oil-derrick.png)

Гражданские здания в изометрии имеют четыре ракурса и пять состояний корпуса. Гарнизон отображается мешками и полосами цвета владельца. При повреждении пропадает часть верхнего угла здания, при критическом повреждении обрушение захватывает ещё один этаж. Все состояния сохраняют общую опорную точку и размеры участка. [Модели квартала](../../sandbox/content/mods/isometric/models.yaml)

Изометрические электростанция, радар, кран, вертолётная площадка и аэродром имеют по четыре ракурса корпуса. Кран и радар используют общее основание. Антенна и стрела крана используют отдельные атласы по 16 направлений. Радар вращается во времени симуляции. Для крана адаптер берёт направление из цели приказа строительства или из ремонтного крепления и переключает кадры подъёма крюка. [Определения моделей](../../sandbox/content/mods/isometric/models.yaml)

Изометрический бункер имеет четыре ракурса корпуса. Пушечное и комбинированное улучшения устанавливают один поворотный модуль в центре крыши. У каждого модуля 16 направлений. [Атлас корпуса](../../sandbox/assets/buildings/isometric/bunker.png)

У обоих генераторов четыре ракурса. Излучатель стелс-генератора использует отдельный атлас на 16 направлений и ускоряется по мере развёртывания. Генератор щита размещается как пассивное здание 4 × 4. [Атлас стелс-генератора](../../sandbox/assets/buildings/isometric/stealth-generator.png) · [Атлас генератора щита](../../sandbox/assets/buildings/isometric/shield-generator.png)

В изометрическом моде центр снабжения, бараки и завод используют по четыре поворота и кадры ворот, отрендеренные из моделей Blender. Завод имеет отдельный набор кадров после тяжёлого улучшения. Розовые панели перекрашиваются шейдером по владельцу. Графика подключается через `ModelOverrides` при запуске `./tools/dev.sh run --mod=res://isometric-game.yaml`. [Атлас центра снабжения](../../sandbox/assets/buildings/supply-center.png) · [Атлас бараков](../../sandbox/assets/buildings/isometric/barracks.png) · [Атлас завода](../../sandbox/assets/buildings/isometric/factory.png) · [Определения моделей](../../sandbox/content/mods/isometric/models.yaml)

Корпуса, башни и боеприпасы полигона непрямого огня подготовлены из оригинальных атласов ImageGen. [Промпты и SHA-256](indirect-fire-art.json).

16 ноября 2026 добавлены инженерный конвертоплан, конвертоплан снабжения и тяжёлый четырёхроторный транспорт. [Промпты, источники и SHA-256](air-support-art.json) · [Техническая нормализация через Godot](../../tools/prepare_air_support_art.gd). Встроенный imagegen создаёт RGBA-источники, нормализация сохраняет изображение и альфу, укладывает их в 1024×1024 и создаёт SpriteFrames. Роторы используют существующий отдельный анимируемый слой.

Сгенерирован 4 ноября 2026 года встроенным `image_gen`, затем скопирован в проект. Внешние игровые ассеты не заимствовались. [Промпты, происхождение и SHA-256](generated-art.json).

- `sandbox/assets/units`: семь RGBA PNG — два пехотинца, корпус БТР, две его башни, корпус/башня танка. Вид сверху; рисунок поворачивается адаптером. Для ракетной башни используется первый вариант: уточняющая правка перспективы не прошла фильтр сервиса.
- `sandbox/assets/terrain`: семь непрозрачных PNG — road, grass, mud, water, forest, swamp, mountains. Пока один рисунок на тип, без переходных тайлов между типами.
- `sandbox/resources/units`: SpriteFrames ресурсы с последовательностями stand/travel/shoot. У пехоты travel содержит четыре кадра, stand/shoot — нейтральный кадр того же цикла. У техники пока по одному рисунку; выстрел дополняется процедурными эффектами.
- `sandbox/resources/terrain`: Texture2D/AtlasTexture ресурсы. `.png.import` фиксирует mipmaps для масштаба камеры; `.godot` в репозиторий не входит.

Первоначальные исходники 1254×1254. Дополнительные листы ходьбы 2172×724 сохранены в `sources`; игровые атласы `*-walk.png` — 2048×512. [Промпты и SHA-256 ходьбы](infantry-walk.json), [скрипт нормализации](../../tools/prepare_infantry_walk.py), [предпросмотр исходной окраски](infantry-walk-preview.gif). Встроенный image_gen использован для генерации и правок; скрипт выполняет техническую нарезку/выравнивание, без рисования новых поз. Альфа — прозрачность, magenta-панели RGB — командная окраска. Нормированные pivots и размеры в models.yaml учитывают, что центр вращения башни не совпадает с центром canvas. Ресурсы можно заменить самостоятельно; Unit и симуляция от файлов картинок не зависят.

Стандартный `tools/dev.sh run` импортирует ресурсы перед запуском, поэтому свежему checkout не нужен заранее подготовленный импорт-кэш. Headless сценарий `--showcase` читает units.yaml и terrain.yaml, без графических ресурсов.

После замечания о мигании ракетчика добавлен целый лист `sources/rocketeer-walk-exposure-final.png`, подготовленный встроенным image_gen; прежние источники сохранены. Его использует скрипт нормализации. История промптов/замены и текущие SHA-256 находятся в infantry-walk.json. [Сравнение и проверка яркости](../verification/010-terrain-locomotors.md). С N1 headless showcase также загружает соседний terrain.yaml: графика по-прежнему не нужна.

На этапе оружия добавлены инженерная машина, комбинированная пушечно-ракетная башня и основание ДОТа. [Промпты/исходники/SHA-256](weapon-stage-art.json), [техническая нормализация](../../tools/prepare_weapon_art.py). Источники встроенного imagegen сохранены в sources; игровые RGBA нормированы в 1254×1254 с сохранением пропорций и альфы. Базовая гранатомётная пехота временно использует рисунок автоматчика. Headless загружает также weapons.yaml; effects.yaml остаётся визуальным.


N2 добавляет `assets/buildings`: семь зданий, отдельный радар, общие руины, деревья/камни/скалу/обломки, трёхкадровые полосы двери и сварки. [Промпты, исходники, история открытых проёмов и SHA-256](buildings-art.json), [воспроизводимая техническая нормализация](../../tools/prepare_building_art.py) (Pillow). Статические рисунки — RGBA1024² с единым alpha-bbox и fit928 без увеличения; полосы —1536×512 с общей геометрией кадров. Размер в мире задают Model объявления отдельно от физических footprint. Состояния повреждения пока tint, работы — pulse/сварка, разрушения — общий отдельный рисунок. Встроенные двери исходных зданий заменены на открытый проём, поверх которого адаптер рисует закрывающий/открывающий слой. Art не добавляет внешних лицензированных игровых ассетов.
