# OpenRTS.Network

NET1–NET5, включая WAN pipeline и multiplayer lifecycle: прямой клиентский lockstep, .NET10 / MIT. Зависимости Network → Gameplay → Core; Godot/Content не требуются, новых NuGet пакетов нет.

После аудита NET1–NET5 исправлены пункты1–4: ошибочный peer изолируется без завершения здоровых клиентов (настоящий desync остаётся fatal), параметры rejoin не зависят от удалённого экрана, ticket ограничен сроком lease, default reader принимает длинные журналы в прежней байтовой квоте. [Контракт045](../../docs/decisions/045-network-audit-hardening.md), [проверки045](../../docs/verification/045-network-audit.md), [закрытые пункты5–8: контракт046](../../docs/decisions/046-network-directory-diagnostics.md). `./tools/dev.sh verify-network --reconnect` разрывает TCP и проверяет настоящую кнопку возврата, новую эпоху и дальнейший replay.

`DirectMatch.Run` принимает подключённый TcpClient, player1/2, immutable MatchDescriptor, фабрику tick-zero SimulationWorld, callback ввода на будущий тик и stream журнала. Run блокирует поток-владелец до EndTick либо согласованного Leave в live режиме; сокет и мир освобождаются им, журналом владеет вызывающий код. Cancel и deadline прерывают сетевое ожидание. Команды передаются pipeline; checkpoints сверяются периодически, финальный hash обязателен; `DesyncException.Tick` обозначает первый проверенный расходящийся тик. `MatchJournal.Replay` воспроизводит завершённый журнал с исходной фабрикой и проверяет все hashes.

Поддержаны обычные RTS player intents, включая способности, производство, стройку, транспорт, upgrade/transform и авиацию; дополнительные поля имеют явный wire format3 (player intent IDs/encoding сохранены из format2). Stamp назначает сетевой owner, а не offline Session.Submit. Неподдержанные поля/команды отвергаются. Детали текущего протокола: [решение038](../../docs/decisions/038-net2-live-direct-matches.md); исходный NET1 — [решение037](../../docs/decisions/037-net1-direct-lockstep.md).

## Игра через Godot (NET3)

Откройте **Multiplayer**. Хост нажимает **Создать игру**, задаёт название и при желании пароль. Гость выбирает игру и нажимает **Войти** или **Смотреть**. Для прямого поиска достаточно IP или имени одного узла и кнопки **Соединить**. В лобби каждый игрок нажимает **Готов**, хост запускает матч. Слот назначается на хосте. Адрес матча, порт и TLS-отпечаток клиент получает из объявления, затем проверяет сертификат, версию и игровой контент.


`StarSession` обслуживает2–8сторон (sandbox до4), full-world client simulation, pinned TLS и клиент-хост как координатор. В supply доступны начальные AI slots и takeover отключённых сторон на общей паузе. Только хост формирует команды ИИ, клиенты применяют общий журнал. В меню доступны чат и общая Pause/Resume; локальное открытие меню не ставит мир на паузу. Leave гостя переводит его слот в None и ставит оставшихся на общую паузу; можно продолжить без нового ввода этой стороны или включить ИИ. Leave/Stop хоста завершает матч для всех. Потеря хоста прерывает игру. Уже принятые приказы выбывшей стороны не отменяются и армия не уничтожается.

Журналы находятся в Godot `user://network`. Новый `StarJournal.Replay` v3 проверяет команды, control revisions и каждый world hash; журналы оставшихся peers побайтно одинаковы. Он не совместим со старым `MatchJournal` v3. Runtime snapshots и rejoin описаны ниже; host migration, изменяемая дипломатия/shared vision и surrender с поражением остаются продолжением. Зрители не входят в барьер тика. [Контракт040](../../docs/decisions/040-net3-multiplayer-lifecycle.md), [проверки](../../docs/verification/040-net3.md).

`./tools/dev.sh verify-network` — два реальных headless Godot процесса. `./tools/dev.sh verify-network --net3 --spectator` — четыре процесса (три игрока и зритель), четвёртая сторона под ИИ, Ready/Start, команды/production, локальное меню, общая пауза, Stop и replay. `--visible --output=/tmp/openrts-network-ui` сохраняет оконные скриншоты. `dotnet run --project tests/OpenRTS.Network.Tests -- --net3` — отдельные многопользовательские проверки, включая8human и асимметричный WAN.

NET2 двухсторонний pipeline сохранён в DirectMatch/DirectSession для диагностических потребителей. [Контракт039](../../docs/decisions/039-net2-wan-pipeline.md), [измерения](../../docs/verification/039-net2-wan.md): D тиков вперёд, checkpoints каждые H, очереди по32frames и bounded verification lag. Звезда NET3 переиспользует транспортные границы, но отправляет итоговые записи всех сторон и проверяет hash каждого тика на клиентах; численные WAN-результаты DirectSession не являются её benchmark.

`./tools/dev.sh verify-wan --output=/tmp/wan-model.json` — задержки, jitter, модель recovery после loss, bandwidth и slow clients. `./tools/dev.sh verify-wan --netem --output=/tmp/wan-kernel` — два TLS процесса и настоящие kernel drops в одноразовом Linux-контейнере (нужен Docker/OrbStack; test-only image iproute2). Сетевые правила хоста не меняются, контейнер удаляется. `--quick` сокращает real matrix до одного профиля. Оба peer-журнала проверяются offline. `DirectSession.Statistics` даёт immutable локальные метрики; они не входят в игровой hash.

## NET4.1: повторы и восстановление через историю

Новые StarJournalv3 содержат полный MatchDescriptor и game-owned recipe. `StarJournal.ReadInfo` читает пролог, `Inspect` проверяет структуру целиком, `Replay` проверяет World hash каждого тика. Старый StarJournalv2 читается через Replay только с явно переданными descriptor/factory. Новые файлы sandbox открываются из главного меню → Повторы: Play/Pause, шаг, скорость, seek и POV; управление игровыми сторонами запрещено.

`ReplayCheckpoint.Capture(journal, tick, output)` сохраняет ограниченный префикс завершённого журнала, `Restore(input, factory)` возвращает RecoveredReplay с живым World. Caller владеет Dispose и продолжает работу на том же потоке. Это история O(K), не runtime snapshot; AI policy/соединения не восстанавливаются. По умолчанию64MiB/1000000тиков/15с catch-up. [Контракт042](../../docs/decisions/042-net4-replay-recovery.md), [проверки](../../docs/verification/042-net4.md).

`./tools/dev.sh verify-replay --visible --output=/tmp/openrts-replay` записывает настоящий TLS-матч и проверяет его проигрывание через Godot UI. `dotnet run --project tests/OpenRTS.Network.Tests -- --net4` проверяет recovery/перезапуск/повреждения. Runtime schemas и сетевой reconnect реализованы в NET4.2/4.3 ниже.

## NET4.2/4.3: runtime snapshot и возврат

`SimulationWorld.CaptureSnapshot()` сохраняет состояние на K; `SimulationWorld.RestoreSnapshot(bytes, factory, cancellationToken)` возвращает новый проверенный мир на том же owner. Caller владеет Dispose. Native handles/Frent storage не сохраняются. Фабрика должна создавать тот же исходный сценарий. Stateful extensions реализуют `IRuntimeSnapshotMechanic`; без reader сохранение явно отвергается.

`StarSession.ResumeCredential` содержит секрет Match/Player/Token/Epoch. Его можно передать новому guest через параметр `resumeCredential`. Токен хранится в памяти; его нельзя публиковать или добавлять в журнал. Хост передаёт snapshot и префикс общей истории chunks по32KiB, клиент проверяет их и возвращается на K. Новый ввод начинается с K+D, согласованные sequence numbers продолжаются. Возврат завершается на общей паузе, затем участник нажимает Resume. В sandbox после ошибки сети доступно «Вернуться в матч».

Поздний spectator (player0) может присоединиться на уже установленной паузе; анонимный запрос во время Playing отвергается. Для анализа матчей без остановки потребуется отдельный observer catch-up NET6/NET7. Пределы: snapshot64MiB, префикс64MiB/1000000тиков, transfer15с, restore15с, admission30с. Потеря хоста не мигрирует матч; snapshot World не включает внешнюю память AI policy. Replay seek пока остаётся от0.

[Контракт043](../../docs/decisions/043-net4-runtime-snapshot-rejoin.md) · [Проверки043](../../docs/verification/043-net4-runtime-rejoin.md).

## NET5: поиск матча и допуск

При открытии главного меню клиент отвечает на поиск в LAN. UDP 17772 служит для обнаружения активных клиентов по IPv4, TCP 17772 — для получения списка и регистрации игр. Первый экземпляр приложения на машине обслуживает TCP-каталог. Другой экземпляр может зарегистрировать свою игру у него. Матч использует отдельное TLS-соединение, по умолчанию TCP 17771.

В [network.yaml](../../sandbox/network.yaml) задаются адреса поиска и публикации. Можно положить собственный файл в Godot `user://network.yaml`. Пустой список подходит для игры в одной LAN. При обновлении клиент опрашивает настроенные источники и введённый адрес. Для IP или имени сначала проверяется клиентский каталог, затем HTTPS-сервис на 17773. Явный HTTPS URL задаёт адрес полноценного каталога. Для локальной отладки на loopback также разрешён HTTP. За reverse proxy можно задать `AdvertiseHost`. При обычной регистрации сервис сообщает исходный адрес соединения. Доступность объявленного игрового порта зависит от маршрута, брандмауэра и при необходимости настройки NAT.

Все новые матчи публичны. Список может содержать запароленные игры. В обычном режиме StarProtocol 6 клиент по выбранному объявлению сразу соединяется с хостом. Хост проверяет пароль при входе игрока и зрителя, на своём потоке выделяет свободное место и выдаёт игроку секрет возврата. Допуск действует на этом соединении. Каталог хранит только объявление и срок регистрации. Программный режим admissionPolicy с подписанным билетом сохраняется для явных интеграций NET5. Физический LAN-сегмент является границей доверия для первичного объявления отпечатка. Для подтверждения источника объявления в интернете используется HTTPS-каталог.

У LAN-регистрации срок 12 секунд. Клиент отдаёт также найденные объявления с остатком срока, повторную пересылку копий ограничивает одним переходом. У самостоятельного сервиса lease 30 секунд. Публикация в каждый настроенный источник идёт вне цикла симуляции. Недоступность источника после поиска не мешает войти в ещё доступную игру.

`./tools/dev.sh verify-directory` проверяет создание и вход из каталога. `./tools/dev.sh verify-network --lan-address=<IPv4 этой машины>` проверяет адресный поиск и вход через настоящий диалог пароля двумя процессами Godot. Для проверки между устройствами откройте LAN на двух компьютерах с одной версией, сначала войдите через **Найти в LAN**, затем повторите через IP хоста. Пустой список при установленной связи, недоступный узел и отказ во входе означают разные состояния.

## Запуск конечного headless-стенда

Сначала `./tools/dev.sh build`. Из корня репозитория в двух терминалах:

```sh
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --host=127.0.0.1 --port=17771 --journal=/tmp/net1-host.log
dotnet run --project tests/OpenRTS.Network.Tests --no-build -- --join=127.0.0.1 --port=17771 --journal=/tmp/net1-join.log
```

Для LAN хост указывает свой адрес интерфейса в `--host`, второй участник — этот адрес в `--join`. Стенд создаёт два юнита разных игроков, выдаёт обоим движение на тике2, выполняет20тиков с разным количеством workers, печатает итоговый hash и автоматически проверяет offline replay. `--port=0` у host выбирает свободный порт и печатает `LISTEN port`. Самостоятельный запуск без параметров выполняет сетевые проверки; они также включены в `./tools/dev.sh test`.

Основная версия по умолчанию — прямой клиентский lockstep по подходу OpenRA/0 A.D./Recoil. Внешний сервер необязателен: поиск игр и роль зрителя для статистики/анализа матчей, баланса и признаков читерства, без влияния на tick barrier. Безопасный режим с доверенным сервером — последняя дополнительная опция NET8/NET9, выключенная по умолчанию и выбираемая при создании игры. Live NET2 использует pinned TLS без обязательного аккаунта; конечный NET1 diagnostic harness сохраняет plain TCP. NET4.1–4.3 реализованы в пределах [контракта043](../../docs/decisions/043-net4-runtime-snapshot-rejoin.md). Дипломатия в ходе матча и игровое поражение требуют дополнительных правил Gameplay. Фиксированные альянсы и лобби описаны в [041](../../docs/decisions/041-game-shell-and-lobby.md).

- [Исследование](../../docs/research/networking/README.md)
- [Референсы](../../docs/research/networking/references.md)
- [Целевые алгоритмы](../../docs/research/networking/design.md)
- [Приоритеты](../../docs/research/networking/roadmap.md)
- [Проверки NET1](../../docs/verification/037-net1.md)

После аудита046 каталог/билет передают полное число мест2..8; StarSession берёт его из catalogue grant, а прямой вход/rejoin сохраняют явный playerCount. Новые лобби Godot вмещают до восьми сторон. Браузер подключается к лобби на четыре или восемь мест. Confirm очереди ограничены1024 и продолжаются после reservation_missing. Waiting сообщает тик/участников/время, RTT измеряется nonce Ping/Pong и не записывается в журнал. Protocol5 требует обновить host/client/catalogue вместе; старые журналы читаются. `verify-network --net3 --spectator --slow-client-ms=1800` проверяет menu barrier при медленном UI; `verify-directory` проверяет вход в лобби на восемь мест и отключённые кнопки для лобби на два места. [Проверки046](../../docs/verification/046-network-directory-diagnostics.md).
