using System;
using System.IO;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Network;

public partial class Sandbox
{
    private Label? _replayLabel;
    private HSlider? _replayTimeline;
    private Button? _replayPause;
    private long _replayGeneration;
    private bool _replayDragging;
    private string? _openReplayPath, _replayMapPath, _replaySetPath;
    private bool _recordPlaytest;
    private string? _localJournalPath;

    private static string NewJournalPath()
    {
        string directory = ProjectSettings.GlobalizePath("user://network");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.journal");
    }

    private void BindLocalRecording(LocalMapPlaytest receipt)
    {
        var map = _mapMatch!.Map;
        var set = receipt.ReadStartingSet(_manifest);
        if (receipt.Slots.Length == 0) throw new ArgumentException("Для записи нужен хотя бы один активный игрок.");
        string[] slots = map.Slots.ToArray();
        int seatCount = receipt.Slots.Length > MatchSetupOptions.LegacySeatCount ? MapScenario.MaximumPlayers : MatchSetupOptions.LegacySeatCount;
        var seats = Enumerable.Range(0, seatCount).Select(i => i < receipt.Slots.Length
            ? new MatchSeat(SlotController.Human, Array.IndexOf(slots, receipt.Slots[i]))
            : new MatchSeat(SlotController.None, 0)).ToArray();
        _configuredOptions = new MatchSetupOptions(map.Id, set?.Id ?? "", (int)OpenRTS.Core.Visibility.FogMode.Visible,
            1, receipt.TickRate, seats, receipt.SpeedPerTick, Local: true).Bind(map, set);
        _configuredOptions.RequireIdentity();
    }

    private LocalReplayRecorder CreateLocalRecorder()
    {
        var options = _configuredOptions!;
        _localJournalPath = NewJournalPath();
        return new(_localJournalPath, options.Describe(_installedCatalog.Summary.Fingerprint, _mapMatch!.Seed),
            options.Serialize(), options.Seats.Select(seat => seat.Controller).ToArray());
    }

    private void SaveLocalReplay()
    {
        if (_session?.Recorder is not { } recorder || _session.Completion.IsFaulted || _session.Completion.IsCanceled)
            throw new InvalidOperationException("Запись матча прервана.");
        var report = recorder.Complete();
        GD.Print("OPENRTS_LOCAL_JOURNAL:" + _localJournalPath);
        GD.Print("OPENRTS_LOCAL_REPLAY_HASH:" + report.StateHash);
    }

    private void BeginReplay(string path)
    {
        if (_session is not null) return;
        GameMap? replayArtifact = null;
        try
        {
            using var input = File.OpenRead(path); var info = StarJournal.ReadInfo(input);
            var options = MatchSetupOptions.ReadReplay(info, _installedCatalog.Summary.Fingerprint);
            GameMap map;
            if (_replayMapPath is { } artifact)
                replayArtifact = map = SceneMapBundle.IsPath(artifact)
                    ? LoadSceneMap(artifact, _installedCatalog) : GameMap.Load(ReadMapSource(artifact), _installedCatalog);
            else if (!_setupMaps.TryGetValue(options.Map, out map!))
                throw new InvalidDataException($"Не установлена версия карты '{options.Map}' с отпечатком {options.MapFingerprint}.");
            StartingSet? set = null;
            if (_replaySetPath is { } startSet) set = StartingSet.Load(ReadMapSource(startSet));
            else if (options.Mode.Length != 0 && !_setupSets.TryGetValue(options.Mode, out set))
                throw new InvalidDataException($"Не установлена версия стартового набора '{options.Mode}' с отпечатком {options.StartingSetFingerprint}.");
            var match = options.Prepare(map, set);
            if (match.Seed != info.Descriptor.Seed) throw new InvalidDataException("Seed повтора не совпадает с фабрикой карты.");
            _configuredOptions = options; _mapMatch = match;
            _networkMode = 0; _fogViewer = 0; _constructionViewer = 0; _opponentProfile = null; _opponent = null;
            _matchFogMode = (OpenRTS.Core.Visibility.FogMode)_configuredOptions.Fog;
            _count = match.UnitCount; _paused = true; _playbackSpeed = 1;
            _session = new SandboxSession(new ReplaySession(path, actual =>
            {
                if (actual.Descriptor != info.Descriptor || actual.Recipe != info.Recipe || actual.InitialHash != info.InitialHash ||
                    actual.InitialState.Match != info.InitialState.Match) throw new InvalidDataException("Файл повтора изменился.");
                return options.CreateWorld(match, _workers, restrict: true);
            }));
            _matchSetup!.SetLaunching(true, "Проверка и загрузка повтора…");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or DeclarativeContent.ContentException or System.Text.Json.JsonException)
        {
            if (replayArtifact is not null) _sceneMaps.Remove(replayArtifact);
            _matchSetup!.SetLaunching(false, "Не удалось открыть повтор: " + error.Message);
            if (_verifyReplayPath is not null) { GD.PushError(error.Message); GetTree().Quit(1); }
        }
    }
    private void AddReplayControls()
    {
        if (_session?.Replay is not { } replay) return;
        var layer = new CanvasLayer { Layer = 21 }; AddChild(layer);
        var panel = new PanelContainer { Position = new(12, 10), Theme = TerminalTheme() }; layer.AddChild(panel);
        _rtsPanels.Add(panel);
        panel.AddThemeStyleboxOverride("panel", TerminalBox(new("#101712ee"), new("#697657"), 8));
        var box = new VBoxContainer(); panel.AddChild(box);
        _replayLabel = new Label(); box.AddChild(_replayLabel);
        var controls = new HBoxContainer(); box.AddChild(controls);
        _replayPause = new Button { Name = "ReplayPause", Text = "Воспроизвести" };
        _replayPause.Pressed += () => replay.SetPlayback(replay.PlaybackSpeed, !replay.IsPaused); controls.AddChild(_replayPause);
        var restart = new Button { Name = "ReplayRestart", Text = "С начала" };
        restart.Pressed += () => replay.Seek(0); controls.AddChild(restart);
        var step = new Button { Name = "ReplayStep", Text = "+1 тик" }; step.Pressed += replay.StepOnce; controls.AddChild(step);
        var speed = new OptionButton { Name = "ReplaySpeed" }; foreach (string value in new[] { "0.25×", "0.5×", "1×", "2×", "4×", "8×" }) speed.AddItem(value); speed.Select(2);
        speed.ItemSelected += value => replay.SetPlayback(new double[] { .25, .5, 1, 2, 4, 8 }[value], replay.IsPaused); controls.AddChild(speed);
        var viewer = new OptionButton { Name = "ReplayViewer" }; viewer.AddItem("Весь мир", 0);
        foreach (var seat in replay.Info!.InitialState.Slots.Where(s => s.Controller != SlotController.None)) viewer.AddItem("Игрок " + seat.Player, (int)seat.Player);
        viewer.ItemSelected += value => { _fogViewer = (uint)viewer.GetItemId((int)value); _constructionViewer = _fogViewer; ResetReplayPresentation(); _focusStartingBase = _fogViewer != 0; }; controls.AddChild(viewer);
        _replayTimeline = new HSlider { Name = "ReplayTimeline", MinValue = 0, MaxValue = replay.Info.EndTick, Step = 1, CustomMinimumSize = new(390, 20) };
        _replayTimeline.DragStarted += () => _replayDragging = true;
        _replayTimeline.DragEnded += changed => { _replayDragging = false; if (changed) replay.Seek((long)_replayTimeline.Value); }; box.AddChild(_replayTimeline);
        var seek = new Button { Name = "ReplaySeek", Text = "Перейти к выбранному тику" }; seek.Pressed += () => replay.Seek((long)_replayTimeline.Value); box.AddChild(seek);
        _replayGeneration = replay.Generation;
    }
    private void ResetReplayPresentation()
    {
        _selection.Reset(); _panelDirty = true; _panelTick = -1; _visibleProjectsTick = -1;
        _combatEffects?.Clear(); _sprites?.ClearWrecks(); _rtsOverlay?.RadarContacts.Clear(); _fogView?.ResetTimeline();
        _productionPlacement = null; _sitePlacement = null; _armedAction = null; _armedCustom = null;
    }
    private void UpdateReplayControls()
    {
        if (_session?.Replay is not { } replay) return;
        _paused = replay.IsPaused; _playbackSpeed = replay.PlaybackSpeed;
        if (_replayGeneration != replay.Generation) { _replayGeneration = replay.Generation; ResetReplayPresentation(); }
        if (_replayLabel is not null) _replayLabel.Text = $"Повтор · {replay.Position} / {replay.Info!.EndTick} · {replay.Status}";
        if (_replayPause is not null) _replayPause.Text = replay.IsPaused ? "Воспроизвести" : "Пауза";
        if (_replayTimeline is not null && !_replayDragging && !_replayTimeline.HasFocus()) _replayTimeline.SetValueNoSignal(replay.Position);
    }
    private bool HandleReplayKey(Key key)
    {
        if (_session?.Replay is not { } replay) return false;
        if (key == Key.P) replay.SetPlayback(replay.PlaybackSpeed, !replay.IsPaused);
        else if (key == Key.N) replay.StepOnce();
        else if (key is Key.Equal or Key.Plus or Key.KpAdd or Key.Minus or Key.KpSubtract)
            replay.SetPlayback(Math.Clamp(replay.PlaybackSpeed * (key is Key.Minus or Key.KpSubtract ? .5 : 2), .125, 8), replay.IsPaused);
        else if (key is Key.Escape or Key.F10 or Key.F11 or Key.F12 or Key.F8 or Key.F9 or Key.F7) return false;
        return true;
    }
}
