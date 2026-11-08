# Референсы для разработки OpenRTS

Референсы используются для исследования архитектурных идей. Разрешение переносить код определяется лицензией и происхождением конкретного файла. Заявленная проектом производительность не заменяет наши измерения.

| Система | Источник | Вопрос для исследования |
| --- | --- | --- |
| Граница симуляции и представления | [C&C Remastered DLLInterface](https://github.com/electronicarts/CnC_Remastered_Collection/blob/master/REDALERT/DLLInterface.cpp) | Какие команды, состояния и события нужны внешнему хосту |
| Публикация кадров | [FA-Remaster LiveSim](https://github.com/invictus-1/FA-Remaster/blob/main/render/live_sim.h) | Кто владеет данными между тиками и кадрами отображения |
| Детерминированная параллельность | [FA-Remaster multicore](https://github.com/invictus-1/FA-Remaster/blob/main/docs/multicore.md) | Как отделить независимое вычисление от применения результатов |
| Компоненты и редкие обновления | [Generals UpdateModule](https://github.com/electronicarts/CnC_Generals_Zero_Hour/blob/main/GeneralsMD/Code/GameEngine/Include/GameLogic/Module/UpdateModule.h) | Где достаточно планировщика пробуждения по тикам |
| Навигация и группы | [Permafrost navigation API](https://github.com/eduard-permyakov/permafrost-engine/blob/master/src/navigation/public/nav.h) | Как разделить маршруты, локальное движение и прибытие группы |
| Игровое и графическое время | [Warzone gtime](https://github.com/Warzone2100/warzone2100/blob/master/lib/gamelib/gtime.h) | Как интерполировать представление без влияния на игровой шаг |
| Фоновый поиск пути | [Warzone fpath](https://github.com/Warzone2100/warzone2100/blob/master/src/fpath.cpp) | Когда результат фоновой задачи становится частью мира |
| Полномочия игровой и локальной логики | [Recoil LuaHandleSynced](https://github.com/beyond-all-reason/RecoilEngine/blob/master/rts/Lua/LuaHandleSynced.h) | Как предотвратить влияние локальных эффектов на симуляцию |
| Независимая C# симуляция | [OpenZ ZWorld](https://github.com/playopenz/OpenZ/blob/main/source/Assets/OpenZ/Scripts/Sim/ZWorld.cs) | Как игровой frontend использует обычные C# объекты |
| Полнота сохранения | [0 A.D. Simulation2](https://github.com/0ad/0ad/blob/master/source/simulation2/Simulation2.cpp#L359) | Как сравнить восстановленный мир с непрерывным запуском |
| Ограничения потоков Godot | [Godot thread-safe APIs](https://docs.godotengine.org/en/stable/tutorials/performance/thread_safe_apis.html) | Какие действия выполняет основной поток, какие можно делегировать |
| Код и отдельные CC ассеты | [Creative Commons FAQ](https://creativecommons.org/faq/#if-i-create-a-collection-that-includes-a-work-offered-under-a-cc-license-which-licenses-may-i-choose-for-the-collection) | Как сохранить отдельные лицензии и условия NC, ND и SA |

Переиспользована библиотека из `/Users/dmzorev/projects/games/hyperfleets/libs/DeclarativeContent`. Её README, LICENSE, исходники и тесты перенесены вместе. [Манифест импорта](vendor/declarative-content.json) содержит исходный commit и SHA-256 файлов. Библиотека не предоставляет игровой цикл или готовые RTS правила.

OpenRTSv1 находится в `/Users/dmzorev/projects/games/OpenRTSv1`. Он служит источником опыта разделения систем. Его план совместимости с OpenRA не является планом нового MIT проекта.

[Исследование авиации и вертолётов](research/aviation/README.md) сравнивает OpenRA, Zero Hour, Recoil и Warzone 2100 по закреплённым исходникам. Первая реализация в Gameplay и её ограничения описаны в [решении 032](decisions/032-aviation-and-target-categories.md); исследовательский план включает также последующие этапы.
