using System;
using DeclarativeContent;
using Godot;
using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

public partial class Sandbox
{
    private MatchSetupScreen? _matchSetup;
    private bool _startedFromSetup, _verifyMatchSetup;
    private bool _verifyAiMatch;
    private string? _aiProfileId;
    private SkirmishAiProfile? _opponentProfile;
    private SkirmishAi? _opponent;
    private string? _opponentSlot;

    private void OpenMatchSetup(UnitCatalog catalog)
    {
        GameMap Map(string path) => GameMap.Load(ReadMapSource(path), catalog);
        StartingSet Set(string path) => StartingSet.Load(ReadMapSource(path));
        (string Title, string Description, GameMap Map)[] maps =
            [
                ("Долина", "Открытая долина с дорогами, озером и четырьмя месторождениями.", Map(MapScenario.DefaultMap)),
                ("Перекрёсток", "Городские кварталы и вокзал у пересечения дорог.", Map(MapScenario.CrossroadsMap)),
                ("Береговая станция", "Два посёлка разделены рекой. Дороги сходятся к двум переправам.", Map(MapScenario.RiversideMap))
            ];
        (string Title, StartingSet Set)[] modes =
            [
                ("Снабжение · центр, инженер и грузовик", Set(MapScenario.DefaultStartSet)),
                ("Пехотный старт · площадка и два бойца", Set(MapScenario.InfantryStartSet))
            ];
        _matchSetup = new();
        AddChild(_matchSetup);
        _matchSetup.Build(TerminalTheme(), catalog.Locomotion!, maps, modes);
        _matchSetup.Launch += BeginMatchFromSetup;
        if (_verifyAiMatch) SelectAiVerificationMatch();
        else if (_verifyMatchSetup) SelectVerificationMatch();
    }

    private void BeginMatchFromSetup(GameMap map, StartingSet set, string[] slots, bool aiOpponent)
    {
        if (_session is not null) return;
        try
        {
            _mapMatch = MapScenario.Prepare(map, set, slots);
            _opponentProfile = aiOpponent ? SkirmishAiCatalog.Load(ReadMapSource("content/skirmish-ai.yaml"), _catalog!, _aiProfileId ?? SkirmishAiScenario.ProfileId) : null;
            _opponentSlot = aiOpponent ? slots[1] : null;
            _count = _mapMatch.UnitCount;
            _matchSetup!.SetLaunching(true, "Подготовка поля боя…");
            StartSession();
        }
        catch (Exception error) when (error is ContentException or ArgumentException or System.IO.IOException)
        {
            MatchSetupFailed(error, canRetry: true);
        }
    }

    private void UpdateMatchSetup()
    {
        if (_session is null) return;
        if (_session.Completion.IsCompleted)
        {
            try { _session.Completion.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                MatchSetupFailed(error, canRetry: true);
                return;
            }
        }

        using var frame = _session.Frames.AcquireLatest();
        if (frame is null) return;
        try
        {
            _startedFromSetup = true;
            BuildBattleScene(_catalog!);
            _focusStartingBase = true;
            _matchSetup!.Hide();
            _matchSetup.QueueFree();
            _matchSetup = null;
            _session.SetPlayback(_playbackSpeed, _paused || _verifyMatchSetup);
        }
        catch (Exception error)
        {
            MatchSetupFailed(error, canRetry: false);
        }
    }

    private void MatchSetupFailed(Exception error, bool canRetry)
    {
        _session?.Dispose();
        _session = null;
        _mapMatch = null;
        _opponentProfile = null;
        _opponent = null;
        _opponentSlot = null;
        if (_verifyMatchSetup || !canRetry) GD.PushError(error.ToString());
        if (_verifyMatchSetup) GetTree().Quit(1);
        else _matchSetup?.SetLaunching(!canRetry, "Матч не запущен. " + error.Message);
    }
}
