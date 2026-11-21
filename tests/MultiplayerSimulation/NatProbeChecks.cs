using System.Net;
using System.Net.Sockets;
using QNetZ;
using QNetZ.DDL;
using QNetZ.Factory;
using QNetZ.Interfaces;
using RDVServices.Services;

static class NatProbeChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [
        ("ipv6-alternative", true), ("high-bit-rvcid", true),
        ("missing-rvcid-control", false), ("zero-rvcid-control", false),
        ("dropped-target-control", false), ("preconnect-target-control", false),
        ("expired-target-control", false), ("detached-target-control", false),
        ("unregistered-target-control", false), ("other-handler-control", false),
        ("unidentified-target-control", false), ("ambiguous-rvcid-control", false),
        ("unregistered-caller-control", false), ("unidentified-caller-control", false),
        ("expired-caller-control", false), ("replaced-caller-control", false),
        ("stale-binding", true), ("same-ip-peers", true), ("two-fallback-targets", true),
        ("repeated-alternates", true), ("observed-then-alternate-control", false),
        ("exact-duplicates-control", false), ("fallback-with-exact-duplicates-control", false),
        ("endpoint-rvcid-conflict-control", false), ("exact-without-rvcid-control", false),
        ("malformed-address", true), ("malformed-port", true), ("missing-port", true),
        ("malformed-then-observed", true), ("rmc-dispatch", true), ("rmc-exact-control", false)
    ];

    public static (bool, string) Run(string kind)
    {
        using var fixture = new Fixture();
        var h = fixture.Handler;
        var caller = fixture.Caller;
        var a = fixture.A;
        var b = fixture.B;
        var expectedA = 1;
        var expectedB = 0;
        if (kind == "high-bit-rvcid")
            a.PlayerInfo.RVCID = 0xF0001234;
        var alternate = Url(a, "192.0.2.20");
        var exact = Url(a);
        var urls = new List<StationURL> { alternate };
        switch (kind)
        {
            case "ipv6-alternative":
                alternate.Address = "2001:db8::20";
                break;
            case "high-bit-rvcid":
                break;
            case "missing-rvcid-control":
                alternate.Parameters.Remove("RVCID");
                expectedA = 0;
                break;
            case "zero-rvcid-control":
                alternate["RVCID"] = 0;
                expectedA = 0;
                break;
            case "dropped-target-control":
                a.State = QClient.StateType.Dropped;
                expectedA = 0;
                break;
            case "preconnect-target-control":
                a.State = QClient.StateType.PreCONNECT;
                expectedA = 0;
                break;
            case "expired-target-control":
                a.LastPacketTime = DateTime.UtcNow.AddSeconds(-Constants.ClientTimeoutSeconds - 1);
                expectedA = 0;
                break;
            case "detached-target-control":
                a.PlayerInfo.Client = b;
                expectedA = 0;
                break;
            case "unregistered-target-control":
                NetworkPlayers.Players.Remove(a.PlayerInfo);
                expectedA = 0;
                break;
            case "other-handler-control":
                h.Clients.Remove(a);
                var otherHandler = new QPacketHandlerPRUDP(fixture.Socket, 2, 0, "other-handler");
                otherHandler.Clients.Add(a);
                expectedA = 0;
                break;
            case "unidentified-target-control":
                a.PlayerInfo.PID = 0;
                expectedA = 0;
                break;
            case "ambiguous-rvcid-control":
                b.PlayerInfo.RVCID = a.PlayerInfo.RVCID;
                expectedA = 0;
                break;
            case "unregistered-caller-control":
                NetworkPlayers.Players.Remove(caller.PlayerInfo);
                expectedA = 0;
                break;
            case "unidentified-caller-control":
                caller.PlayerInfo = null;
                expectedA = 0;
                break;
            case "expired-caller-control":
                caller.LastPacketTime = DateTime.UtcNow.AddSeconds(-Constants.ClientTimeoutSeconds - 1);
                expectedA = 0;
                break;
            case "replaced-caller-control":
                caller.PlayerInfo.Client = b;
                expectedA = 0;
                break;
            case "stale-binding":
                var old = h.NewQClient(new(IPAddress.Loopback, 31003));
                old.State = QClient.StateType.Active;
                old.PlayerInfo = a.PlayerInfo;
                // Only A owns this PlayerInfo's current back-reference.
                break;
            case "same-ip-peers":
                break; // A and B already use the same address and different ports/IDs.
            case "two-fallback-targets":
                urls.Add(Url(b, "192.0.2.21"));
                expectedB = 1;
                break;
            case "repeated-alternates":
                urls.Add(Url(a, "192.0.2.21"));
                urls.Add(Url(a, "192.0.2.22"));
                break;
            case "observed-then-alternate-control":
                urls.Insert(0, exact);
                break;
            case "exact-duplicates-control":
                urls = [exact, Url(a)];
                expectedA = 2;
                break;
            case "fallback-with-exact-duplicates-control":
                urls.Add(exact);
                urls.Add(Url(a));
                expectedA = 2;
                break;
            case "endpoint-rvcid-conflict-control":
                exact["RVCID"] = unchecked((int)b.PlayerInfo.RVCID);
                urls = [exact];
                break;
            case "exact-without-rvcid-control":
                exact.Parameters.Remove("RVCID");
                urls = [exact];
                break;
            case "malformed-address":
                alternate.Address = "not-an-ip";
                expectedA = 0;
                break;
            case "malformed-port":
                alternate["port"] = 65536;
                expectedA = 0;
                break;
            case "missing-port":
                alternate.Parameters.Remove("port");
                expectedA = 0;
                break;
            case "malformed-then-observed":
                alternate.Address = "not-an-ip";
                urls.Add(exact);
                break;
            case "rmc-dispatch":
                break;
            case "rmc-exact-control":
                urls = [exact];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        uint error = 0;
        var dispatch = kind.StartsWith("rmc-", StringComparison.Ordinal);
        try
        {
            if (dispatch)
            {
                if (RMCServiceFactory.GetServiceFactory(RMCProtocolId.NATTraversalService) == null)
                    RMCServiceFactory.RegisterService<NATTraversalService>();
                var rmc = new RMCPacket
                {
                    proto = RMCProtocolId.NATTraversalService,
                    methodID = 1,
                    callID = 1,
                    isRequest = true,
                    request = new RMCPRequestDDL<ProbeRequest>(new() { urlTargetList = urls })
                };
                var packet = Packet(caller.IDrecv, 1);
                packet.payload = rmc.ToBuffer();
                h.ProcessPacket(packet.toBuffer(), caller.Endpoint);
            }
            else
            {
                var service = new NATTraversalService { Context = new(new RMCPacket(), h, caller, Packet(caller.IDrecv, 1)) };
                error = service.RequestProbeInitiation(urls).Error;
            }
        }
        catch (Exception ex) when (kind.StartsWith("malformed-", StringComparison.Ordinal) || kind == "missing-port")
        {
            // The original parser throws on these synthetic invalid URL fields.
            // Characterize that known outcome, but do not hide unrelated failures.
            if (ex is not FormatException and not ArgumentException and not KeyNotFoundException)
                throw;
            return (false, $"Malformed target aborts the request with {ex.GetType().Name}; expected skip and remaining valid targets processed");
        }
        h.Update();
        var receivedA = Drain(fixture.ReceiverA);
        var receivedB = Drain(fixture.ReceiverB);
        var valid = receivedA.All(p => ValidProbe(p, a, caller)) && receivedB.All(p => ValidProbe(p, b, caller));
        var dispatchReturned = true;
        if (dispatch)
        {
            var replies = Drain(fixture.CallerSocket).Where(p => p.type == QPacket.PACKETTYPE.DATA && !p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK)).Select(p => new RMCPacket(p)).ToArray();
            dispatchReturned = replies.Length == 1 && replies[0].success && replies[0].callID == 1;
        }
        return (error == 0 && receivedA.Length == expectedA && receivedB.Length == expectedB && valid && dispatchReturned,
            $"{kind}: recipient A/B probes {receivedA.Length}/{receivedB.Length}, expected {expectedA}/{expectedB}; decoded caller/recipient correct {valid}; request returned {dispatchReturned && error == 0}");
    }

    static StationURL Url(QClient target, string? address = null) => new("prudp", address ?? target.Endpoint.Address.ToString(),
        new Dictionary<string, int> { ["port"] = target.Endpoint.Port, ["RVCID"] = unchecked((int)target.PlayerInfo.RVCID), ["type"] = 3 });
    sealed class ProbeRequest
    {
        public List<StationURL> urlTargetList { get; set; } = [];
    }
    static QPacket Packet(uint signature, ushort sequence) => new()
    {
        type = QPacket.PACKETTYPE.DATA,
        flags = [QPacket.PACKETFLAG.FLAG_RELIABLE],
        m_oSourceVPort = new(0x3f),
        m_oDestinationVPort = new(0x31),
        m_uiSignature = signature,
        m_bySessionID = 1,
        uiSeqId = sequence,
        payload = [],
        usesCompression = false
    };
    static QPacket[] Drain(UdpClient receiver)
    {
        var packets = new List<QPacket>();
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (receiver.Available > 0)
            packets.Add(new QPacket(receiver.Receive(ref from)));
        return packets.ToArray();
    }
    static bool ValidProbe(QPacket packet, QClient recipient, QClient caller)
    {
        var rmc = new RMCPacket(packet);
        using var body = new MemoryStream(packet.payload);
        body.Position = rmc._afterProtocolOffset;
        var station = new StationURL();
        station.Read(body);
        return packet.m_uiSignature == recipient.IDsend && rmc.isRequest && rmc.proto == RMCProtocolId.NATTraversalService
            && rmc.methodID == 2 && station.Address == caller.Endpoint.Address.ToString()
            && station["port"] == caller.Endpoint.Port && station["RVCID"] == unchecked((int)caller.PlayerInfo.RVCID);
    }
    sealed class Fixture : IDisposable
    {
        public readonly UdpClient Socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UdpClient CallerSocket = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UdpClient ReceiverA = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UdpClient ReceiverB = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly QPacketHandlerPRUDP Handler;
        public readonly QClient Caller, A, B;
        public Fixture()
        {
            Handler = new(Socket, 2, 0, "nat-probe-check");
            Caller = Add(CallerSocket, 1);
            A = Add(ReceiverA, 2);
            B = Add(ReceiverB, 3);
        }
        QClient Add(UdpClient socket, uint pid)
        {
            var client = Handler.NewQClient((IPEndPoint)socket.Client.LocalEndPoint!);
            client.State = QClient.StateType.Active;
            client.IDsend = client.IDrecv + 100;
            client.SessionID = 1;
            client.PlayerInfo = NetworkPlayers.CreatePlayerInfo(client);
            client.PlayerInfo.PID = pid;
            return client;
        }
        public void Dispose()
        {
            Socket.Dispose();
            CallerSocket.Dispose();
            ReceiverA.Dispose();
            ReceiverB.Dispose();
        }
    }
}
