using System;
using System.IO;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Network;
using OpenRTS.AI;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private int _networkMode;
    private MatchResumeCredential? _networkResume;
    private string _networkAddress = "127.0.0.1", _networkPin = "";
    private int _networkPort = 17771, _networkDelay = 6;
    private uint _networkPlayer = 2;
    private int _networkCapacity = MapScenario.MaximumPlayers;
    private uint[] _networkAiPlayers = [];
    private Label? _networkLabel;
    private Label? _networkChatLabel;
    private readonly System.Collections.Generic.Queue<string> _networkMessages = new();
    private static string NetworkStatusText(StarSession network)
    {
        var latencies = network.Latencies;
        return network.Status + (latencies.Count == 0 ? " · RTT —" : $" · RTT{(latencies.Count > 1 ? " max" : "")} {latencies.Max(p => p.RoundTripMilliseconds):0} мс");
    }
    private void UpdateNetworkChat(StarSession network)
    {
        while (network.TryReadChat(out var message))
        {
            if (_verifyNetwork && message.Text == "verify-menu-seen") { _networkMenuSeen.Add(message.Player); continue; }
            if (_verifyNetwork && _networkVerifyReconnect && message.Player == 2 && message.Text == "verify-rejoined") { _networkReconnectConfirmed = true; continue; }
            if (_verifyNetwork && _networkVerifyNet3 && message.Text == "verify-pause-seen") { _networkPauseSeen.Add(message.Player); continue; }
            _networkMessages.Enqueue($"{(message.Player == 0 ? "Зритель" : message.Player.ToString())}: {message.Text}");
            while (_networkMessages.Count > 4) _networkMessages.Dequeue();
        }
        if (_networkChatLabel is not null) _networkChatLabel.Text = string.Join("\n", _networkMessages);
    }
    private LanGames? _networkAdvertiser;
    private LanRegistry? _lanRegistry;
    private readonly List<(MatchDirectoryClient Client, DirectoryListing Publication)> _publications = [];
    private void ClosePublications()
    {
        foreach (var item in _publications) item.Publication.Stop();
        foreach (var (client, publication) in _publications) { publication.Dispose(); client.Dispose(); }
        _publications.Clear();
    }
    private sealed record NetworkLobby(string Name, string? PublicAddress, string Password,
        (string Url, string? Peer)[] Sources, MatchEndpoint[]? Endpoints, bool RequestSeat)
    {
        public override string ToString() => Name;
    }
    private Func<LanGame?>? _advertisement;
    private NetworkLobby? _networkLobby;
    private void StartNetworkSession(MapMatch map)
    {
        var path = NewJournalPath();
        var ownerOptions = _configuredOptions!; var ownerMap = map;
        var policies = new System.Collections.Generic.Dictionary<uint, (SkirmishAi Ai, long Tick, WorldCommand[] Commands)>();
        MatchDescriptor Descriptor(MatchSetupOptions options, MapMatch match) => options.Describe(_installedCatalog.Summary.Fingerprint, match.Seed);
        (MatchDescriptor, SimulationWorld) Prepare(string data)
        {
            var options = MatchSetupOptions.Parse(data);
            options.RequireIdentity();
            if (options.Local) throw new InvalidDataException("Локальная запись не является сетевым матчем.");
            if (options.Seats.Length != _networkCapacity) throw new InvalidDataException("Число мест не совпадает с вместимостью сетевого лобби.");
            if (data != options.Serialize()) throw new InvalidDataException("Неверное кодирование параметров матча.");
            var preparedMap = PrepareMap(options);
            var world = CreateConfiguredWorld(preparedMap, options);
            ownerOptions = options; ownerMap = preparedMap; policies.Clear();
            return (Descriptor(options, preparedMap), world);
        }
        System.Collections.Generic.IReadOnlyList<WorldCommand> Plan(SimulationWorld world, uint player)
        {
            policies.TryGetValue(player, out var previous);
            if (previous.Ai is not null && previous.Tick == world.Tick) return previous.Commands;
            var ai = previous.Ai is not null && previous.Tick + 1 == world.Tick ? previous.Ai :
                SkirmishAiScenario.Create(ownerMap.Map.Catalog, _opponentProfile!, ownerMap.Map, player,
                    ownerOptions.PlayerSlots(ownerMap.Map), initialTick: world.Tick, opponents: ownerOptions.Opponents(player));
            var commands = ai.Plan(world); policies[player] = (ai, world.Tick, commands); return commands;
        }
        if (_matchSetup is { } setup)
            _networkLobby = new(setup.NetworkGameName, setup.AdvertiseHost, setup.MatchPassword,
                setup.NetworkMode == 1 ? setup.PublicationSources() : [], setup.SelectedEndpoints, setup.RequestSeat);
        var lobby = _networkLobby ?? throw new InvalidOperationException("Нет параметров подключения к матчу.");
        bool host = _networkMode == 1;
        var network = new StarSession(host, _networkAddress, _networkPort, _networkPin, Descriptor(ownerOptions, map),
            () => CreateConfiguredWorld(map, ownerOptions), path, playerCount: _networkCapacity, player: _networkPlayer,
            aiPlayers: host ? _networkAiPlayers : null, planAi: Plan, lobbySettings: ownerOptions.Serialize(), prepareSettings: Prepare,
            closedPlayers: host ? ownerOptions.Seats.Select((s, i) => (s, i)).Where(p => p.s.Controller == SlotController.None).Select(p => (uint)p.i + 1).ToArray() : null,
            resumeCredential: _networkResume,
            endpoints: lobby.Endpoints, password: lobby.Password, requestSeat: _networkResume is null && lobby.RequestSeat);
        _session = new SandboxSession(network);
        ClosePublications();
        string Map() => MatchSetupOptions.Parse(network.LobbySettings).Map;
        System.Threading.Volatile.Write(ref _advertisement, network.IsHost ? () => LanGames.Advertisement(network, _networkPort, lobby.Name, Map) : null);
        if (network.IsHost)
        {
            var sources = lobby.Sources.AsEnumerable();
            if (_lanRegistry is null) sources = sources.Append(("http://127.0.0.1:17773", "127.0.0.1"));
            foreach (var (url, peer) in sources.Distinct())
            {
                var client = new MatchDirectoryClient(url, "Игрок");
                var listing = new DirectoryListing(client, network, lobby.Name, lobby.PublicAddress, _networkPort, Map, peerAddress: peer);
                _publications.Add((client, listing));
            }
        }
    }
    private SimulationWorld CreateNetworkWorld(MapMatch map) => CreateConfiguredWorld(map, _configuredOptions!);
    private void ShowNetworkFailure(string reason, string heading = "Соединение прервано")
    {
        var layer = new CanvasLayer { Layer = 40 }; AddChild(layer);
        var backdrop = new ColorRect { Color = new(0, 0, 0, 0.85f), Theme = TerminalTheme() };
        layer.AddChild(backdrop); backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer(); backdrop.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new VBoxContainer { CustomMinimumSize = new(460, 0) }; center.AddChild(panel);
        panel.AddChild(new Label { Text = heading, HorizontalAlignment = HorizontalAlignment.Center });
        panel.AddChild(new Label { Text = reason, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        if (_networkResume is not null && _mapMatch is not null && _catalog is not null)
        {
            var reconnect = new Button { Text = "Вернуться в матч" };
            reconnect.Pressed += () => {
                layer.QueueFree(); _networkMode = 2;
                try { StartSession(); } catch (Exception error) { ShowNetworkFailure(error.Message); }
            };
            panel.AddChild(reconnect);
            VerifyReconnectButton(reconnect);
        }
        var retry = new Button { Text = "В главное меню" };
        retry.Pressed += () => GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene); panel.AddChild(retry);
        var quit = new Button { Text = "Выйти" }; quit.Pressed += () => GetTree().Quit(); panel.AddChild(quit);
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }
    private void AddNetworkStatus()
    {
        if (_networkMode == 0 || _networkLabel is not null) return;
        var layer = new CanvasLayer { Layer = 20 }; AddChild(layer);
        _networkLabel = new Label { Position = new(18, 8), Text = "Прямой матч · TLS", MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_networkLabel);
    }
}
