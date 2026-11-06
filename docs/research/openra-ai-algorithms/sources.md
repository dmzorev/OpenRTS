# Источники, метод и ограничения

## Версия и метод

5 ноября 2026 года прочитаны локальные файлы чистой копии OpenRA с `git rev-parse HEAD = f3f3f2cdf9a81d9ec6a805defea158d464e1548b`; `origin` указывает на `https://github.com/OpenRA/OpenRA.git`. Гиперссылки ниже закреплены этим commit. Строки исходника и конфигурация изучены локально, а не проверены загрузкой GitHub на каждый URL. Движок, боты и тесты не запускались; замеры не выполнялись. Предыдущий обзор [архитектуры RTS AI](../rts-ai-architecture/README.md) служит контекстом, но его ссылки на изменяемую ветку не используются как доказательство деталей здесь.

## Просмотренные опорные файлы

- [ModularBot.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/Player/ModularBot.cs) — активация, циклы модулей и очередь приказов.
- [UnitBuilderBotModule.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/UnitBuilderBotModule.cs) — состав армии и внешние запросы.
- [BaseBuilderBotModule.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/BaseBuilderBotModule.cs), [BaseBuilderQueueManager.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/BotModuleLogic/BaseBuilderQueueManager.cs) — планирование очередей, выбор и размещение построек; только обозначенные в тексте ветви.
- [HarvesterBotModule.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/HarvesterBotModule.cs) — сканы, запрос сборщика и поиск ресурса; перераспределение по карте не развёрнуто.
- [SquadManagerBotModule.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/SquadManagerBotModule.cs), [Squad.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/Squads/Squad.cs), [StateMachine.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/Squads/StateMachine.cs) — группы, цели, планирование и переходы.
- [GroundStates.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/Squads/States/GroundStates.cs), [StateBase.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/Squads/States/StateBase.cs), [AttackOrFleeFuzzy.cs](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/OpenRA.Mods.Common/Traits/BotModules/Squads/AttackOrFleeFuzzy.cs) — наземная тактика и входы fuzzy-системы.
- [RA ai.yaml](https://github.com/OpenRA/OpenRA/blob/f3f3f2cdf9a81d9ec6a805defea158d464e1548b/mods/ra/rules/ai.yaml) — примеры типов ботов, условий включения и параметров. Конечная конфигурация зависит от загрузки правил, наследования и карты, которые целиком не проверялись.

## Непокрытые вопросы

Не описаны полноценно авиация, защита и её конечный автомат, распределение сборщиков по секторам, карта ресурсов, ремонт, захват, супероружие, развёртывание и расширение базы, полная логика поиска места под здания, навигация, микроконтроль оружия, кампанийные скрипты и особенности других модов. Сериализация и загрузка изучены выборочно; полноценной гарантии восстановления состояния и совпадения последовательности после загрузки не дано. Порядок входов из индексов/коллекций и поведение сторонней fuzzy-библиотеки не установлены там, где это специально не отмечено.

Записи охватывают выбранные ветви, а не полную семантику всех функций, которые они вызывают. Нет оценки силы бота, производительности или сравнения с OpenRTS. Конкретные границы выборок, цены, таймеры и случайность не следует переносить в проект без самостоятельной постановки задачи, проверки лицензий и испытаний.
