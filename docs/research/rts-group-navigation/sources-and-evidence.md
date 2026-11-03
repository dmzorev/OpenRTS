# Источники, выводы и пределы доказательств

Дата исследования — 3 ноября 2026 года. Функции исследовательского brief, вариантов, рекомендаций и методики объединены в [основном отчёте](README.md), чтобы не дублировать их пустыми документами. Здесь — реестр реально изученных материалов и связь существенных выводов с ними.

## Реестр

### S001

[Dave Pottinger, Implementing Coordinated Movement, 1999](https://www.gamedeveloper.com/programming/implementing-coordinated-movement). Прочитан авторский материал: общий темп/путь/прибытие, относительные места, изменение формы и высокоуровневый/локальный поиск. Это инженерное описание AoE, не измерение OpenRTS и не доказательство для 10000 участников.

### S002

[Craig Reynolds, Steering Behaviors For Autonomous Characters, 1999](https://www.red3d.com/cwr/steer/gdc99/), [Leader Following](https://www.red3d.com/cwr/steer/LeaderFollow.html), [Path Following](https://www.red3d.com/cwr/steer/PathFollow.html). Прочитаны слои поведения, flocking, следование и отделение steering от pathfinding. Рассматриваются непрерывные модели движения, а не наши клеточные резервации.

### S003

[Stephen Bjore, Game AI Pro, глава 21](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter21_Techniques_for_Formation_Movement_Using_Steering_Circles.pdf). Изучены доступные авторские разделы, особенно §21.3: Column/Band и цели точек формации. Сохранение формации описано как отдельный вариант; оно не выбрано для обычного MoveGroup.

### S004

[Elijah Emerson, Game AI Pro, глава 23](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf). Прочитаны §23.3–23.8 и §23.11–23.12: секторы, порталы, поля, кэш, динамика. Чужое распределение фонового времени по миллисекундам не перенесено в игровые правила.

### S005

[Optimal Reciprocal Collision Avoidance, авторская страница](https://gamma.cs.unc.edu/ORCA/). Проверена постановка и заявленные авторами достаточные условия в модели ORCA. Полный PDF не получен для детального разбора доказательства. Гарантия не приписывается нашему fixed-point коду.

### S006

[Treuille, Cooper, Popović, Continuum Crowds, SIGGRAPH 2006](https://grail.cs.washington.edu/projects/continuum-crowds/continuum-crowds.pdf). Прочитаны вводная и модельные части §1–3. Учитываются потоки и плотность; это другая степень сохранения индивидуальных намерений.

### S007

[David Silver, Cooperative Pathfinding, AIIDE 2005](https://ojs.aaai.org/index.php/AIIDE/article/view/18726), [PDF издателя](https://ocs.aaai.org/Papers/AIIDE/2005/AIIDE05-020.pdf). Полный PDF не был доступен для повторного чтения; использованы доступные аннотация и индексированные вводные фрагменты. Детали реализации WHCA* не считаются проверенными этим исследованием.

### S008

[Botea, Müller, Schaeffer, HPA* — авторская публикация](https://webdocs.cs.ualberta.ca/~mmueller/ps/hpastar.pdf), [авторская страница исследований](https://webdocs.cs.ualberta.ca/~games/pathfind/). Доступ ограничен аннотацией/библиографией; полный PDF на авторском сервере не загрузился. Используется только общее устройство кластерной абстракции, а не неподтверждённые детали.

### S009

Recoil, commit `d93c4daccd7c0f044714974554e9842b70736aa7`: [SelectedUnitsAI.cpp](https://github.com/beyond-all-reason/RecoilEngine/blob/d93c4daccd7c0f044714974554e9842b70736aa7/rts/Game/SelectedUnitsAI.cpp#L211), [QTPFS PathManager](https://github.com/beyond-all-reason/RecoilEngine/blob/d93c4daccd7c0f044714974554e9842b70736aa7/rts/Sim/Path/QTPFS/PathManager.cpp#L1160), [GroundMoveType](https://github.com/beyond-all-reason/RecoilEngine/blob/d93c4daccd7c0f044714974554e9842b70736aa7/rts/Sim/MoveTypes/GroundMoveType.cpp#L1900). Прочитаны функции групповых приказов, разделения маршрутов и локального движения; не весь движок.

### S010

0 A.D., commit `61a3b9507d974084e6badb88a0826bd89a6d5b8b`: [Formation.js](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/binaries/data/mods/public/simulation/components/Formation.js#L650), [motion system](https://github.com/0ad/0ad/blob/61a3b9507d974084e6badb88a0826bd89a6d5b8b/source/simulation2/components/CCmpUnitMotion_System.cpp#L400). Прочитаны controller, назначение, скорость, фазы и соседская сетка. Чужие pushing и float не выдаются за безопасные после прямого переноса.

### S011

Permafrost, commit `083a99cace3eb047e7a3ccb7dc6b22eb78e3967f`: [formation.c](https://github.com/eduard-permyakov/permafrost-engine/blob/083a99cace3eb047e7a3ccb7dc6b22eb78e3967f/src/game/formation.c#L2320), [movement.c](https://github.com/eduard-permyakov/permafrost-engine/blob/083a99cace3eb047e7a3ccb7dc6b22eb78e3967f/src/game/movement.c#L6159), [nav.c](https://github.com/eduard-permyakov/permafrost-engine/blob/083a99cace3eb047e7a3ccb7dc6b22eb78e3967f/src/navigation/nav.c#L2266). Изучены формации/Hungarian, leading/drag, соседские скорости, chunks/portals и кэш. Subformations этой версии группируют по типам; это не доказательство пространственной кластеризации армий.

### S012

Собственные реализации и эксперименты OpenRTS: [GroupDestinations.cs](../../../src/OpenRTS.Core/GroupDestinations.cs), [групповое движение](../../../src/OpenRTS.Core/SimulationWorld.GroupMovement.cs), [проверки](../../../tests/OpenRTS.Tests/GroupPlanningChecks.cs), [сырые измерения](measurements.json), [верификация](../../verification/006-compact-group-navigation.md). Именно эти сценарии подтверждают свойства нашего кода; источники S001–S011 сами их не доказывают.

## Связь выводов с доказательствами

| Вывод | Основание | Ограничение / что могло бы опровергнуть |
| --- | --- | --- |
| C001: маршрут, назначение мест и контакты требуют разных слоёв | S001, S004, S009–S011 | Разделение само по себе не даёт качественного движения; проверяется S012 |
| C002: обычный приказ собирает толпу, не сохраняет offsets | Уточнение пользователя и тест компактного сбора S012 | Сохранение offsets потребовало бы иной продуктовой семантики |
| C003: boids/ORCA не заменяют назначение и глобальный поиск | S002, S005 | Полное доказательство ORCA не разбиралось; наша модель иная |
| C004: порталы и повторное использование полезны независимо от формаций | S004, S008–S011 | HPA* доступен ограниченно; оптимальность/скорость нашей версии ими не установлены |
| C005: пространственное разделение O(N log² N) избегает глобального N³ matching | Анализ собственного кода S012; контраст S011 | Не гарантирует оптимальную сумму путей; качество оценивается отдельно |
| C006: одинаковый кинематический сдвиг допускает общую фиксацию соседних переходов | Геометрия постоянных относительных векторов и S012 | Не распространяется на произвольные повороты/встречные траектории |
| C007: ранняя остановка на своей цели может отменять вынужденное уступание | Регрессионное воспроизведение и исправление S012 | Проверка должна подтверждать выход в карман и возвращение, не только число команд |
| C008: быстрый тик не доказывает завершения очереди | Длительные профили S012 | Нужны число дошедших, ошибки и завершение, а не только p95 |
| C009: темп исполнения можно отделить от логического шага | PlaybackChecks и сравнение хешей S012 | Это не проверка межплатформенного lockstep и сетевого протокола |

## Аудит и оставшиеся пробелы

Референсные движки не запускались. Полный текст S005/S007/S008 имеет указанные ограничения. Межплатформенный determinism, реальные игровые footprint, разные скорости, динамические большие армии и общая разрешимость MAPF не доказаны. Пространственные когорты по устойчивым ролям, секторная иерархия и непрерывный avoidance не реализованы лишь на основании этого обзора.

Приоритет продолжения определяется измерениями: цена массового приказа, качество личных обходов, использование проходов и отсутствие многотысячного ожидания. Сохранение формации не является критерием готовности обычного приказа.
