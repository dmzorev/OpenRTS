# NET5: поиск матча и допуск

Для обычных игр действует StarProtocol 6 с выдачей места хостом. Ниже описан первоначальный сервис с отдельным issuer. [Актуальный сценарий](../../../src/OpenRTS.Network/README.md#net5-поиск-матча-и-допуск). [Контракт044](../../decisions/044-net5-match-directory-admission.md), [проверки](../../verification/044-net5-directory.md), [roadmap](roadmap.md).

Цель — найти совместимый матч и получить конкретную роль, сохранив прямой клиентский lockstep и необязательность сервера. Реализация: ASP.NET Core CLI `OpenRTS.MatchDirectory`, bounded in-memory catalogue, HTTP client/publisher, Gate внутри StarSession и Godot browser. Новых NuGet-зависимостей нет; HTTP отсутствует в Core/Gameplay и в tick barrier.

Provider первой версии удостоверяет владение P-256 ключом. Challenge30с → подпись → session1час → Join reservation/request ID → подписанный ticket30с → fresh TLS challenge и proof-of-possession → одноразовый admission на owner → receipt/heartbeat. Имя не удостоверяется; ключ Godot живёт в памяти приложения. Постоянная identity/социальные providers — отдельное продолжение.

TTL регистрации30с продлевается heartbeat5с. Private invite не показывается в публичном списке; опубликованные матчи не уходят в обычную LAN-рекламу. Atomic Join учитывает pending/confirmed до новой host revision; повтор request ID не выдаёт второй билет. Cancel прекращает запрос, но удерживает место до expiry выданного автономного capability, максимум30с. Это уточняет исходную идею немедленного освобождения по отказу.

Аудит045: expiry ticket ограничен текущим lease expiry. Close сразу скрывает матч и закрывает доступ через каталог, но удерживает его ID до истечения неиспользованных offline билетов; повторная регистрация до этой границы отвергается. Это исключает второй действующий билет на то же место при удалении записи. Проверены TTL на29/31с и явный Close с публичным/приватным доступом. [Контракт045](../../decisions/045-network-audit-hardening.md), [проверки045](../../verification/045-network-audit.md).

Direct endpoint пробуется первым, затем настроенный оператором TCP relay, с одним host TLS pin. Автоматических NAT/UPnP, reverse tunnels и внешнего relay provider нет. Их эксплуатационный выбор не должен происходить незаметно. Сервис маркирует матч peer-hosted и не принимает «доверенный результат» на основании P2P отчёта.

Критерии проверены: гонка за последнее место, request idempotency, истёкшая session/challenge/ticket, чужие lease/role/match/account proof, spent nonce, private invite, stale heartbeat, malformed/oversized requests, rate limits, confirm без изменения revision. Реальные HTTP/TLS процессы проверяют fallback через byte relay, отказ каталога при продолжающемся матче и совпадение replay. Два Godot процесса проходят native UI публикации/поиска/входа. Полный набор NET1–NET4 продолжает проходить.

Пределы: только ephemeral key accounts и in-memory directory; restart не сохраняет reservations. Не проверены публичный HTTPS deployment, физический WAN/NAT и большая нагрузка. Долговечные аккаунты/leases, внешний login provider, NAT backend и операционные лимиты требуют отдельных измерений/решений. Trusted filtered simulation остаётся последним optional NET8/NET9.

Аудит046: Confirm после reservation_missing продолжает очередь, transient ошибки повторяются; capacity включена в metadata/ticket v2 и bootstrap. Проверены2/4/8мест с AI/closed и spectator, подмена grant и несовместимые native кнопки sandbox. StarProtocol5 на этапе аудита несовместим с4; обновляются обе стороны и каталог. [Контракт046](../../decisions/046-network-directory-diagnostics.md), [проверки046](../../verification/046-network-directory-diagnostics.md).
