using System.Net;
using System.Net.Sockets;
using DSFServices;
using DSFServices.DDL.Models;
using QNetZ;
using QNetZ.DDL;
using RDVServices.Services;

static class ConnectAssociationChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [
        ("retry-identical",true),("retry-many",true),("retry-challenge-change",true),("retry-peer-signature-change",true),
        ("initial-bind-control",false),("challenge-wrap-control",false),("missing-player-control",false),
        ("unregistered-player-control",false),("empty-connect-control",false),("retry-preserves-membership",true),
        ("rebind-current-player",true),("retry-after-rebind",true),("retired-alias-account-switch",true),
        ("stale-alias-account-switch",true),("account-switch-control",false),("account-switch-bound-target",true),
        ("late-old-connect",true),("rebind-old-timeout-control",false),("retry-nat-routing",true),
        ("wire-interleaving-2",true),("wire-interleaving-5",true),("wire-interleaving-8",true)
    ];
    public static (bool, string) Run(string kind)
    {
        if (kind.StartsWith("wire-interleaving-", StringComparison.Ordinal))
            return Interleaved(int.Parse(kind.Split('-')[2]));
        using var f = new Fixture(2);
        var q = f.Clients[0];
        var next = f.Clients[1];
        var a = f.Players[0];
        var b = f.Players[1];
        if (kind == "missing-player-control")
        {
            var ack = f.Connect(q, 999999, expectMissing: true);
            return (ack && q.PlayerInfo == null && NetworkPlayers.Players.Count == 2 && f.Probe(a).Drops == 0, "Unknown player keeps existing registrations and returns the existing empty ACK payload");
        }
        var challenge = kind == "challenge-wrap-control" ? uint.MaxValue : 123u;
        var initial = f.Connect(q, a.PID, challenge);
        var rvcid = a.RVCID;
        var firstReply = f.LastReply.ToArray();
        if (!initial)
            throw new Exception("Initial synthetic CONNECT failed");
        switch (kind)
        {
            case "initial-bind-control":
            case "challenge-wrap-control":
                return (f.Current(a, q) && f.Probe(a).Drops == 0, "Initial binding and existing challenge increment are retained");
            case "missing-player-control":
                throw new InvalidOperationException();
            case "unregistered-player-control":
                NetworkPlayers.Players.Remove(a);
                return (f.Connect(q, a.PID, expectMissing: true) && !NetworkPlayers.Players.Contains(a) && ReferenceEquals(q.PlayerInfo, a) && f.Probe(a).Drops == 0,
                    "CONNECT does not resurrect a removed registration or introduce new authentication policy");
            case "empty-connect-control":
                return (f.Connect(q, a.PID, empty: true) && f.Current(a, q) && f.Probe(a).Drops == 0, "Empty CONNECT ACK behavior preserves current association");
            case "account-switch-control":
                var switched = f.Connect(q, b.PID);
                return (switched && !NetworkPlayers.Players.Contains(a) && f.Current(b, q) && f.Probe(a).Drops == 1 && f.Probe(b).Drops == 0,
                    "Genuine account replacement drops only the current old player exactly once");
            case "account-switch-bound-target":
                var bound = f.Connect(next, b.PID);
                var replaced = f.Connect(q, b.PID);
                return (bound && replaced && !NetworkPlayers.Players.Contains(a) && f.Current(b, q) && next.PlayerInfo == null && f.Probe(a).Drops == 1 && f.Probe(b).Drops == 0,
                    "Account switch to an already-bound target retires that target's old alias without dropping it");
            case "stale-alias-account-switch":
                // Model a legacy inconsistent link directly: old transport still
                // points at A, but A and the new transport point at each other.
                next.PlayerInfo = a;
                a.Client = next;
                var staleSwitched = f.Connect(q, b.PID);
                return (staleSwitched && f.Current(a, next) && f.Current(b, q) && f.Probe(a).Drops == 0 && f.Probe(b).Drops == 0,
                    "Switching through a stale alias does not drop the player currently owned by another transport");
            case "rebind-current-player":
            case "retry-after-rebind":
            case "retired-alias-account-switch":
            case "late-old-connect":
            case "rebind-old-timeout-control":
                var moved = f.Connect(next, a.PID);
                if (kind == "rebind-old-timeout-control")
                {
                    q.State = QClient.StateType.Dropped;
                    q.LastPacketTime = DateTime.UtcNow.AddSeconds(-Constants.ClientTimeoutSeconds - 1);
                    f.Handler.Update();
                    NetworkPlayers.DropPlayers();
                    return (moved && f.Current(a, next) && f.Probe(a).Drops == 0, "Timing out an old transport retains the player's current transport");
                }
                if (kind == "retry-after-rebind")
                    moved &= f.Connect(next, a.PID);
                if (kind == "retired-alias-account-switch")
                {
                    moved &= f.Connect(q, b.PID);
                    return (moved && f.Current(a, next) && f.Current(b, q) && f.Probe(a).Drops == 0 && f.Probe(b).Drops == 0,
                        "Old transport can switch accounts without dropping its former player on the new transport");
                }
                if (kind == "late-old-connect")
                {
                    moved &= f.Connect(q, a.PID);
                    return (moved && f.Current(a, q) && next.PlayerInfo == null && f.Probe(a).Drops == 0,
                        "Last accepted CONNECT still selects the current owner; no new replay/freshness rejection policy");
                }
                return (moved && f.Current(a, next) && q.PlayerInfo == null && ReferenceEquals(f.Handler.GetQClientByClientPID(a.PID), next) && f.Probe(a).Drops == 0,
                    "Rebinding preserves registration/RVCID/data and leaves only the new transport associated");
            case "retry-preserves-membership":
                var party = new PartySessionGathering(new()
                {
                    m_idMyself = 77,
                    m_pidHost = a.PID
                });
                party.Participants.Add(a.PID);
                party.Urls.Add(a.Url);
                PartySessions.GatheringList.Add(party);
                var session = new GameSessionData { Id = 88, TypeID = 1, HostPID = a.PID };
                session.PublicParticipants.Add(a.PID);
                session.HostURLs.Add(a.Url);
                GameSessions.SessionList.Add(session);
                a.GameData().CurrentGatheringId = 77;
                a.GameData().CurrentSession = new()
                {
                    m_sessionID = 88,
                    m_typeID = 1
                };
                var retry = f.Connect(q, a.PID);
                return (retry && f.Current(a, q) && party.Participants.Contains(a.PID) && PartySessions.GatheringList.Contains(party)
                    && session.PublicParticipants.Contains(a.PID) && GameSessions.SessionList.Contains(session) && a.GameData().CurrentGatheringId == 77
                    && a.GameData().CurrentSession?.m_sessionID == 88 && f.Probe(a).Drops == 0,
                    "Retry preserves actual party/game-session membership and does not invoke player cleanup");
            case "retry-nat-routing":
                var other = f.Connect(next, b.PID);
                var retried = f.Connect(q, a.PID);
                var service = new NATTraversalService { Context = new(new RMCPacket(), f.Handler, next, new QPacket()) };
                var result = service.RequestProbeInitiation([new StationURL("prudp", "192.0.2.20", new Dictionary<string, int> { ["port"] = q.Endpoint.Port, ["RVCID"] = unchecked((int)a.RVCID) })]);
                f.Handler.Update();
                var messages = NatProbeChecks.Drain(f.Receiver(q));
                return (other && retried && result.Error == 0 && f.Current(a, q) && messages.Length == 1 && NatProbeChecks.ValidProbe(messages[0], q, next),
                    $"After retry, RVCID fallback delivers {messages.Length}/1 probe to the registered current player");
            case "retry-identical":
            case "retry-many":
            case "retry-challenge-change":
            case "retry-peer-signature-change":
                var replies = true;
                for (var i = 0; i < (kind == "retry-many" ? 8 : 1); i++)
                    replies &= f.Connect(q, a.PID, kind == "retry-challenge-change" ? 456u : challenge,
                        peerSignature: kind == "retry-peer-signature-change" ? q.IDsend + 1 : null);
                var bytesSame = kind != "retry-identical" || firstReply.SequenceEqual(f.LastReply);
                return (replies && bytesSame && f.Current(a, q) && a.RVCID == rvcid && f.Probe(a).Drops == 0,
                    $"{kind}: reply correct {replies}; byte-identical retry when applicable {bytesSame}; registered/current {f.Current(a, q)}; cleanup calls {f.Probe(a).Drops}");
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
    static (bool, string) Interleaved(int count)
    {
        using var f = new Fixture(count);
        var ok = true;
        for (var i = 0; i < count; i++)
            ok &= f.Connect(f.Clients[i], f.Players[i].PID);
        for (var i = count - 1; i >= 0; i--)
            ok &= f.Connect(f.Clients[i], f.Players[i].PID);
        var kept = f.Players.Where((p, i) => f.Current(p, f.Clients[i]) && f.Probe(p).Drops == 0).Count();
        return (ok && kept == count && NetworkPlayers.Players.Count == count, $"{kept}/{count} interleaved clients retain their unique registrations after repeated wire CONNECTs");
    }
    internal static byte[] Payload(uint pid, uint challenge = 123)
    {
        using var plain = new MemoryStream();
        Helper.WriteU32(plain, pid);
        Helper.WriteU32(plain, 99);
        Helper.WriteU32(plain, challenge);
        var encrypted = Helper.Encrypt(Constants.SessionKey, plain.ToArray());
        using var bytes = new MemoryStream();
        Helper.WriteU32(bytes, 0);
        Helper.WriteU32(bytes, (uint)encrypted.Length + 16);
        bytes.Write(encrypted);
        return bytes.ToArray(); // Synthetic empty ticket; production ticket validation is currently a stub.
    }
    sealed class Fixture : IDisposable
    {
        readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly Dictionary<QClient, UdpClient> receivers = [];
        public readonly QPacketHandlerPRUDP Handler;
        public readonly QClient[] Clients;
        public readonly PlayerInfo[] Players;
        public byte[] LastReply = [];
        public Fixture(int count)
        {
            Handler = new(socket, 2, 0, "connect-association-check");
            Clients = Enumerable.Range(0, count).Select(i =>
            {
                var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var client = Handler.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
                receivers.Add(client, receiver);
                return client;
            }).ToArray();
            Players = Enumerable.Range(0, count).Select(i =>
            {
                var pid = (uint)(101 + i);
                var login = new QClient(0xAA000000 + pid, new(IPAddress.Loopback, 40000 + i));
                var p = NetworkPlayers.CreatePlayerInfo(login);
                p.PID = pid;
                p.Name = "synthetic-connect";
                p.GetData<ConnectCleanupProbe>();
                return p;
            }).ToArray();
        }
        public ConnectCleanupProbe Probe(PlayerInfo player) => player.GetData<ConnectCleanupProbe>();
        public UdpClient Receiver(QClient client) => receivers[client];
        public bool Current(PlayerInfo player, QClient client) => NetworkPlayers.Players.Count(p => ReferenceEquals(p, player)) == 1
            && ReferenceEquals(player.Client, client) && ReferenceEquals(client.PlayerInfo, player) && player.RVCID == Probe(player).OriginalRvcid;
        public bool Connect(QClient client, uint pid, uint challenge = 123, uint? peerSignature = null, bool empty = false, bool expectMissing = false)
        {
            var peer = peerSignature ?? (0xCA000000u + client.IDrecv);
            var p = new QPacket
            {
                type = QPacket.PACKETTYPE.CONNECT,
                flags = [QPacket.PACKETFLAG.FLAG_NEED_ACK],
                m_oSourceVPort = new(0x3f),
                m_oDestinationVPort = new(0x31),
                m_uiSignature = client.IDrecv,
                m_uiConnectionSignature = peer,
                m_bySessionID = 17,
                uiSeqId = 1,
                payload = empty ? [] : Payload(pid, challenge),
                usesCompression = false
            };
            Handler.ProcessPacket(p.toBuffer(), client.Endpoint);
            var packets = NatProbeChecks.Drain(receivers[client]);
            if (packets.Length != 1)
                return false;
            var reply = packets[0];
            LastReply = reply.toBuffer();
            if (reply.type != QPacket.PACKETTYPE.CONNECT || !reply.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK) || reply.m_uiSignature != peer
                || reply.uiSeqId != 1 || reply.m_bySessionID != 17 || reply.m_oSourceVPort.toByte() != 0x31 || reply.m_oDestinationVPort.toByte() != 0x3f)
                return false;
            if (empty || expectMissing)
                return reply.payload.Length == 0;
            return reply.payload.Length == 8 && BitConverter.ToUInt32(reply.payload, 0) == 4 && BitConverter.ToUInt32(reply.payload, 4) == unchecked(challenge + 1);
        }
        public void Dispose()
        {
            socket.Dispose();
            foreach (var receiver in receivers.Values)
                receiver.Dispose();
        }
    }
}

public sealed class ConnectCleanupProbe : IPlayerDataStore
{
    public readonly uint OriginalRvcid;
    public int Drops;
    public ConnectCleanupProbe(PlayerInfo owner)
    {
        OriginalRvcid = owner.RVCID;
    }
    public void OnDropped()
    {
        Drops++;
    }
}
