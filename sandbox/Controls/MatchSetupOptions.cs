using System;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;
using OpenRTS.Network;
using OpenRTS.Samples;

public sealed record MatchSeat(SlotController Controller, int Start, uint Team = 0);
public sealed record MatchSetupOptions(string Map, string Mode, int Fog, int Delay, int TickRate, MatchSeat[] Seats,
    int SpeedPerTick = 128, int FogHoldTicks = 0, string? MapFingerprint = null, string? StartingSetFingerprint = null,
    string? EffectiveContentFingerprint = null, bool Local = false)
{
    public const string Rules = "openrts-lobby-v4";
    public const int LegacySeatCount = 4;

    public static bool SupportsSeatCount(int count) => count is LegacySeatCount or MapScenario.MaximumPlayers;

    public MatchSetupOptions Bind(GameMap map, StartingSet? set) => this with
    {
        MapFingerprint = map.GameplayFingerprint, StartingSetFingerprint = set?.Fingerprint,
        EffectiveContentFingerprint = map.Catalog.Summary.Fingerprint
    };

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public MatchDescriptor Describe(string content, ulong seed) =>
        new(Rules, content, Digest(Serialize()), seed, TickRate: TickRate, InputDelay: Delay, EndTick: 0);

    public static MatchSetupOptions ReadReplay(ReplayInfo info, string content)
    {
        if (info.Descriptor.Rules is not ("openrts-lobby-v2" or "openrts-lobby-v3" or Rules) || info.Descriptor.ContentHash != content)
            throw new InvalidDataException("Правила или версия контента повтора не совпадают с установленными.");
        var options = Parse(info.Recipe);
        if (info.Descriptor.Rules == Rules)
        {
            options.RequireIdentity();
            if (info.Descriptor.ScenarioHash != Digest(info.Recipe) || info.Descriptor.TickRate != options.TickRate ||
                info.Descriptor.InputDelay != options.Delay)
                throw new InvalidDataException("Параметры повтора не совпадают с описанием матча.");
        }
        else if (options.Seats.Length != LegacySeatCount || options.Local || options.MapFingerprint is not null ||
            options.EffectiveContentFingerprint is not null || options.StartingSetFingerprint is not null)
            throw new InvalidDataException("Старая версия повтора не поддерживает идентификаторы карт B6.");
        return options;
    }

    public void RequireIdentity()
    {
        static bool Valid(string? digest) => digest is { Length: 64 } && digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (!Valid(MapFingerprint) || !Valid(EffectiveContentFingerprint) ||
            (Mode.Length == 0 ? !Local || StartingSetFingerprint is not null : !Valid(StartingSetFingerprint)))
            throw new InvalidDataException("В параметрах матча отсутствует точная версия карты, стартового набора или правил.");
    }

    public string[] PlayerSlots(GameMap map) =>
        Seats.Take(map.Slots.Count).Select(s => map.Slots[s.Start]).ToArray();

    public MapMatch Prepare(GameMap map, StartingSet? set)
    {
        if (map.Id != Map || MapFingerprint is not null && map.GameplayFingerprint != MapFingerprint)
            throw new InvalidDataException($"Не установлена версия карты '{Map}' с отпечатком {MapFingerprint}.");
        if (EffectiveContentFingerprint is not null && map.Catalog.Summary.Fingerprint != EffectiveContentFingerprint)
            throw new InvalidDataException("Правила карты не совпадают с параметрами матча.");
        if ((set?.Id ?? "") != Mode || MapFingerprint is not null && set?.Fingerprint != StartingSetFingerprint)
            throw new InvalidDataException($"Не установлена версия стартового набора '{Mode}' с отпечатком {StartingSetFingerprint}.");
        int capacity = Math.Min(map.Slots.Count, Seats.Length);
        if (Seats.Take(capacity).Any(seat => seat.Start >= map.Slots.Count) ||
            Seats.Skip(capacity).Any(seat => seat.Controller != SlotController.None) ||
            (Local || set?.Id != "supply") && Seats.Any(seat => seat.Controller == SlotController.Ai))
            throw new ArgumentException("Недоступные места или ИИ для этой карты/режима.");
        string[] slots = PlayerSlots(map);
        return MapScenario.Prepare(map, set, slots, Seats.Take(slots.Length).Select(s => s.Controller != SlotController.None).ToArray());
    }

    public SimulationWorld CreateWorld(MapMatch map, int workers, bool restrict)
    {
        var fog = (FogMode)Fog;
        var world = map.CreateWorld(new GameplayMechanics
        {
            FogCellSize = fog == FogMode.Visible ? null : 1024, FogMode = fog, FogVisibilityHoldTicks = FogHoldTicks
        }, speedPerTick: SpeedPerTick, workers: workers, restrictPlayerCommands: restrict, tickRate: TickRate);
        try
        {
            world.SetInitialTeams(Seats.Select((s, i) => (s, i)).Where(p => p.s.Controller != SlotController.None)
                .ToDictionary(p => (uint)p.i + 1, p => p.s.Team));
            return world;
        }
        catch { world.Dispose(); throw; }
    }
    public System.Collections.Generic.IReadOnlySet<uint> Opponents(uint player) => Seats.Select((s, i) => (s, i)).Where(p => p.s.Controller != SlotController.None && p.i + 1 != player &&
        (Seats[player - 1].Team == 0 || Seats[player - 1].Team != p.s.Team)).Select(p => (uint)p.i + 1).ToHashSet();
    public string Serialize() => JsonSerializer.Serialize(this);
    public static MatchSetupOptions Parse(string data)
    {
        var options = JsonSerializer.Deserialize<MatchSetupOptions>(data) ?? throw new ArgumentException("Нет настроек матча.");
        if (string.IsNullOrWhiteSpace(options.Map) || options.Mode is null ||
            (options.Mode.Length != 0 ? string.IsNullOrWhiteSpace(options.Mode) : !options.Local) || options.Fog is < 0 or > 2 ||
            options.Delay is < 1 or > 16 || (options.Local ? options.TickRate is < 1 or > 1000 : options.TickRate is not (15 or 25 or 40)) ||
            options.Seats is null || !SupportsSeatCount(options.Seats.Length) ||
            options.Seats.Any(s => s is null || !Enum.IsDefined(s.Controller) || s.Start is < 0 or >= MapCompiler.MaximumSlots || s.Team > MapScenario.MaximumPlayers) ||
            options.Seats[0].Controller != SlotController.Human || options.SpeedPerTick is < 1 or > 1024 || options.FogHoldTicks is < 0 or > 250)
            throw new ArgumentException("Неверные настройки матча.");
        return options;
    }
}
