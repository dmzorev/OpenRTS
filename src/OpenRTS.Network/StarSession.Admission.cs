using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace OpenRTS.Network;

public sealed partial class StarSession
{
    public const int ProtocolVersion = StarProtocol.Version;
    private readonly string _password;
    internal bool HostAdmission => _admissionGate is null;
    public bool PasswordRequired => _password.Length != 0;
    private byte[] PasswordProof(byte[] challenge)
    {
        byte[] message = [.. Convert.FromHexString(Fingerprint), .. challenge];
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(_password), message);
    }
    private void CheckPassword(HelloData hello, PeerPipeline pipe)
    {
        if (!PasswordRequired || hello.Resume is not null) return;
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        pipe.Send([StarProtocol.PasswordChallenge, .. challenge]);
        var proof = pipe.Read(TimeSpan.FromSeconds(5));
        if (proof.Length != 33 || proof[0] != StarProtocol.PasswordProof ||
            !CryptographicOperations.FixedTimeEquals(PasswordProof(challenge), proof.AsSpan(1)))
            throw new InvalidDataException("Неверный пароль матча.");
    }
    private readonly MatchAdmissionGate? _admissionGate;
    private readonly MatchJoinGrant? _joinGrant;
    private readonly DirectoryIdentity? _accountIdentity;
    private readonly MatchEndpoint[]? _connectionEndpoints;
    private readonly ConcurrentQueue<MatchAdmissionReceipt> _admitted = new();
    internal const int MaximumAdmissionReceipts = 1024;
    public bool TryReadAdmission(out MatchAdmissionReceipt receipt) => _admitted.TryDequeue(out receipt!);
    private void Admitted(Peer peer)
    {
        if (peer.Admission is { } receipt) { _admitted.Enqueue(receipt); while (_admitted.Count > MaximumAdmissionReceipts) _admitted.TryDequeue(out _); }
    }
    private sealed record HelloData(uint Player, int Count, string Digest, string Hash, string Rules, string Content,
        MatchResumeCredential? Resume, string? Ticket);
    private static HelloData ReadHello(byte[] frame) => Wire.Unpack(frame, r => {
        if (r.ReadByte() != StarProtocol.Hello || r.ReadInt32() != StarProtocol.Version) throw new InvalidDataException("Несовместимая версия сетевого протокола.");
        uint player = r.ReadUInt32(); int count = r.ReadInt32();
        string digest = Wire.Text(r), hash = Wire.Text(r), rules = Wire.Text(r), content = Wire.Text(r);
        byte flag = r.ReadByte(); if (flag > 1) throw new InvalidDataException("Invalid credential flag.");
        MatchResumeCredential? resume = null;
        if (flag != 0) { int size = r.ReadInt32(); if (size is < 1 or > 256) throw new InvalidDataException("Credential size limit."); resume = ReadCredential(r.ReadBytes(size)); }
        int length = r.ReadUInt16(); if (length > 8192) throw new InvalidDataException("Admission ticket size limit.");
        byte[] bytes = r.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
        string? ticket = length == 0 ? null : new UTF8Encoding(false, true).GetString(bytes);
        if (resume is not null && ticket is not null) throw new InvalidDataException("Resume и новый допуск нельзя совмещать.");
        return new HelloData(player, count, digest, hash, rules, content, resume, ticket);
    });
    private void ProveAdmission(HelloData hello, PeerPipeline pipe)
    {
        if (hello.Ticket is null)
        {
            if (_admissionGate is not null && hello.Resume is null) throw new InvalidDataException("Для этой игры требуется билет каталога или приглашение.");
            return;
        }
        if (_admissionGate is null) throw new InvalidDataException("Хост не принимает билеты этого каталога.");
        var claims = _admissionGate.Inspect(hello.Ticket);
        if (claims.Match != State.Match || claims.Player != hello.Player || claims.PlayerCount != hello.Count || claims.PlayerCount != State.Slots.Count)
            throw new InvalidDataException("Билет выдан для другого матча, роли или числа сторон.");
        string nonce = DirectoryIdentity.Secret();
        pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.AdmissionChallenge); Wire.Text(w, nonce); }));
        string signature = Wire.Unpack(pipe.Read(TimeSpan.FromSeconds(5)), r => {
            if (r.ReadByte() != StarProtocol.AdmissionProof) throw new InvalidDataException("Не получено подтверждение билета."); return Wire.Text(r);
        });
        _admissionGate.CheckProof(hello.Ticket, nonce, signature);
    }
    private static void RejectPeer(PeerPipeline? pipe, string reason)
    {
        if (pipe is null) return;
        try { pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.Rejected); Wire.Text(w, reason.Length > 200 ? "Подключение отклонено." : reason); })); pipe.Flush(TimeSpan.FromMilliseconds(100)); }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidDataException) { }
    }
    private sealed record ConnectedPeer(TcpClient Socket, SslStream Tls) : IDisposable
    { public void Dispose() { Tls.Dispose(); Socket.Dispose(); } }
    private ConnectedPeer ConnectClient(string address, int port)
    {
        var endpoints = _connectionEndpoints ?? [new(MatchEndpointKind.Direct, address, port)];
        var failures = new List<string>();
        foreach (var endpoint in endpoints.OrderBy(e => e.Kind))
        {
            _stop.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref _status, endpoint.Kind == MatchEndpointKind.Direct ? "Прямое подключение…" : "Подключение через relay…");
            var socket = new TcpClient { NoDelay = true }; SslStream? tls = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(_joinGrant is null ? 30 : 5));
                socket.ConnectAsync(endpoint.Address, endpoint.Port, deadline.Token).AsTask().GetAwaiter().GetResult();
                tls = new SslStream(socket.GetStream(), false, (_, remote, _, _) => remote is not null &&
                    Convert.ToHexString(SHA256.HashData(remote.GetRawCertData())) == Fingerprint);
                Authenticate(tls, null, deadline.Token).GetAwaiter().GetResult(); return new(socket, tls);
            }
            catch (Exception e) when (!_stop.IsCancellationRequested && e is SocketException or IOException or AuthenticationException or OperationCanceledException)
            {
                string reason = e is AuthenticationException ? "TLS или отпечаток хоста не прошёл проверку" :
                    e is OperationCanceledException ? "истёк срок подключения" : e is SocketException network ? network.SocketErrorCode.ToString() : "соединение прервано";
                failures.Add($"{endpoint.Kind}: {endpoint.Address}:{endpoint.Port} ({reason})"); tls?.Dispose(); socket.Dispose();
            }
            catch { tls?.Dispose(); socket.Dispose(); throw; }
        }
        throw new IOException("Хост недоступен по указанным endpoints: " + string.Join("; ", failures) + ". Проверьте адрес, порт, NAT и настройку relay.");
    }
}
