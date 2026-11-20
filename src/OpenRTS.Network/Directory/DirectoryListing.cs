namespace OpenRTS.Network;

/// <summary>Independent catalogue heartbeat. Failure cannot enter the simulation's barrier.</summary>
public sealed class DirectoryListing : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private string _status = "Публикация в каталоге…";
    private DirectoryLease? _lease;
    public string Status => Volatile.Read(ref _status);
    public string? Invite => Volatile.Read(ref _lease)?.Invite;
    public DirectoryListing(MatchDirectoryClient client, StarSession host, string title, string? publicAddress, int publicPort,
        Func<string> map, bool privateGame = false, MatchEndpoint? relay = null, string? peerAddress = null)
    {
        if (!host.IsHost) throw new ArgumentException("A player-host is required.");
        if (privateGame) throw new ArgumentException("Скрытые игры больше не публикуются.");
        if (publicAddress is not null) DirectoryValidation.Endpoint(new(MatchEndpointKind.Direct, publicAddress, publicPort));
        if (relay is not null) DirectoryValidation.Endpoint(relay);
        DirectoryValidation.Text(title, 64, "название");
        _worker = Task.Run(async () => {
            var receipts = new Queue<MatchAdmissionReceipt>();
            try
            {
                while (!_stop.IsCancellationRequested && !host.Completion.IsCompleted && host.State.Phase != MatchPhase.Finished)
                {
                    try
                    {
                        if (peerAddress is not null && _lease is null && LanGames.Advertisement(host, publicPort, title, map) is { } entry)
                        {
                            try
                            {
                                await LanRegistry.RegisterAsync(peerAddress, entry, _stop.Token);
                                Volatile.Write(ref _status, "Игра опубликована на " + peerAddress);
                                await Task.Delay(TimeSpan.FromSeconds(4), _stop.Token);
                                continue;
                            }
                            catch (Exception e) when (!_stop.IsCancellationRequested && e is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException) { }
                        }
                        publicAddress ??= await client.GetHostAddressAsync(_stop.Token);
                        MatchEndpoint[] endpoints = relay is null ? [new(MatchEndpointKind.Direct, publicAddress, publicPort)] :
                            [new(MatchEndpointKind.Direct, publicAddress, publicPort), relay];
                        var state = host.State;
                        var game = new MatchAdvertisement(state.Match, state.Revision, StarSession.ProtocolVersion, state.Slots.Count, host.Descriptor.Rules, host.Descriptor.ContentHash,
                            title, map(), host.Fingerprint, state.Phase, state.Slots.Where(s => s.Controller == SlotController.Human && !s.Connected).Select(s => s.Player).ToArray(),
                            state.Slots.Count(s => s.Connected), state.Spectators, endpoints, privateGame, PasswordRequired: host.PasswordRequired, HostAdmission: host.HostAdmission);
                        if (_lease is null) Volatile.Write(ref _lease, await client.RegisterAsync(game, _stop.Token));
                        else await client.HeartbeatAsync(_lease, game, _stop.Token);
                        while (receipts.Count < StarSession.MaximumAdmissionReceipts && host.TryReadAdmission(out var receipt)) receipts.Enqueue(receipt);
                        while (receipts.TryPeek(out var receipt))
                        {
                            try { await client.ConfirmAsync(_lease!, receipt, _stop.Token); }
                            catch (MatchDirectoryException e) when (e.Code == "reservation_missing") { /* This offline admission no longer has a catalogue reservation. */ }
                            receipts.Dequeue();
                        }
                        Volatile.Write(ref _status, "Игра опубликована в каталоге");
                    }
                    catch (MatchDirectoryException e) when (e.Code is "lease_invalid" or "match_expired") { Volatile.Write(ref _lease, null); Volatile.Write(ref _status, "Регистрация истекла; повторная публикация…"); }
                    catch (Exception e) when (e is HttpRequestException or MatchDirectoryException or IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
                    { if (_stop.IsCancellationRequested) break; Volatile.Write(ref _status, "Каталог недоступен; прямой матч продолжается. " + e.Message); }
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                if (_lease is { } lease)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await client.CloseAsync(lease, deadline.Token); } catch (Exception e) when (e is HttpRequestException or MatchDirectoryException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
        });
    }
    public void Stop() => _stop.Cancel();
    public void Dispose()
    {
        _stop.Cancel();
        if (!_worker.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Catalogue publisher did not stop.");
        _stop.Dispose();
    }
}
