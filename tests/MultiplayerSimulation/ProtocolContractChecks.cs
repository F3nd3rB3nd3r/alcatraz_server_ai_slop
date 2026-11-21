using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using DSFServices;
using DSFServices.DDL.Models;
using QNetZ;
using QNetZ.DDL;
using QNetZ.Interfaces;

// Safety controls for observed flows, not a claim that the exploratory invariants
// in Program.cs have become established client contracts.
static class ProtocolContractChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [("host-url-replacement-control",false),("repeated-host-urls-control",false),
        ("late-old-cancel-preserves-new-control",false),("cancel-notifies-peer-control",false),
        ("complete-rmc-gap-characterization",false),("known-missing-middle-retry",true),("known-missing-middle-ack-guard",true)];
    public static (bool, string) Run(string kind)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiverA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiverB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var h = new QPacketHandlerPRUDP(socket, 2, 0, "contract-control");
        PlayerInfo Player(uint pid, UdpClient receiver)
        {
            var c = h.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
            c.State = QClient.StateType.Active;
            c.IDsend = 2000 + pid;
            c.SessionID = 1;
            var p = NetworkPlayers.CreatePlayerInfo(c);
            p.PID = pid;
            p.Name = "synthetic-contract";
            c.PlayerInfo = p;
            return p;
        }
        var a = Player(101, receiverA);
        var b = Player(102, receiverB);
        var type = typeof(DSFPlayerGameData).Assembly.GetType("DSFServices.Services.MatchMakingService")!;
        var service = (RMCServiceBase)Activator.CreateInstance(type, true)!;
        void Caller(PlayerInfo p) => service.Context = new(new RMCPacket(), h, p.Client, new QPacket());
        RMCResult Call(string method, params object[] args) => (RMCResult)type.GetMethods()
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(service, args)!;
        Caller(a);
        var old = new PartySessionGathering(new()
        {
            m_idMyself = 1,
            m_pidOwner = a.PID,
            m_pidHost = a.PID
        });
        old.Participants.UnionWith([a.PID, b.PID]);
        PartySessions.GatheringList.Add(old);
        if (kind.Contains("urls") || kind.Contains("url-replacement"))
        {
            var first = new[] { a.Url };
            Call("RegisterLocalURLs", 1u, first);
            var owner = kind.StartsWith("host-url") ? b : a;
            Caller(owner);
            Call("UpdateSessionHost", 1u);
            var plain = owner.Url;
            var advertised = owner.Url;
            advertised["type"] = 3;
            var current = new[] { plain, advertised };
            Call("RegisterLocalURLs", 1u, current);
            if (kind.StartsWith("repeated"))
                Call("RegisterLocalURLs", 1u, current);
            var returned = Call("GetSessionURLs", 1u).Response.ToBuffer();
            var expected = new RMCPResponseDDL<List<StationURL>>(current.ToList()).ToBuffer();
            return (returned.SequenceEqual(expected) && old.Participants.SetEquals([a.PID, b.PID]),
                "Host URL replacement/re-registration returns exactly the current host's submitted alternatives without changing participants");
        }
        if (kind.StartsWith("late-old-cancel"))
        {
            var next = new PartySessionGathering(new()
            {
                m_idMyself = 2,
                m_pidOwner = a.PID,
                m_pidHost = a.PID
            });
            PartySessions.GatheringList.Add(next);
            Call("Participate", 2u, "");
            var response = Call("CancelParticipation", 1u, "");
            return (response.Response.ToBuffer().SequenceEqual(new byte[] { 1 }) && next.Participants.SetEquals([a.PID])
                && PartySessions.GatheringList.Contains(next) && a.GameData().CurrentGatheringId == 2 && old.Participants.SetEquals([b.PID]),
                "A late cancellation naming the old gathering preserves the already-joined new gathering and current reference");
        }
        if (kind.StartsWith("cancel-notifies"))
        {
            Call("CancelParticipation", 1u, "");
            h.Update();
            var packets = NatProbeChecks.Drain(receiverB);
            var notices = packets.Where(p => p.type == QPacket.PACKETTYPE.DATA && !p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK)).ToArray();
            bool valid = false;
            if (notices.Length == 1)
            {
                var p = notices[0];
                var rmc = new RMCPacket(p);
                var offset = (int)rmc._afterProtocolOffset;
                valid = rmc.proto == RMCProtocolId.NotificationEventManager && rmc.isRequest
                    && BitConverter.ToUInt32(p.payload, offset) == a.PID && BitConverter.ToUInt32(p.payload, offset + 4) == 3002
                    && BitConverter.ToUInt32(p.payload, offset + 8) == 1;
            }
            return (valid && NatProbeChecks.Drain(receiverA).Length == 0, "Cancellation sends the existing departure notification to the other member only");
        }
        var defrag = typeof(QPacketHandlerPRUDP).GetMethod("Defrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool Feed(QPacket p) => (bool)defrag.Invoke(h, [a.Client, p])!;
        QPacket Fragment(ushort sequence, byte part, string text) => new()
        {
            type = QPacket.PACKETTYPE.DATA,
            flags = [QPacket.PACKETFLAG.FLAG_RELIABLE],
            m_oSourceVPort = new(0x3f),
            m_oDestinationVPort = new(0x31),
            m_uiSignature = a.Client.IDrecv,
            m_bySessionID = 1,
            uiSeqId = sequence,
            m_byPartNumber = part,
            payload = Encoding.ASCII.GetBytes(text),
            usesCompression = false
        };
        if (kind == "complete-rmc-gap-characterization")
        {
            var body = new RMCPacket
            {
                isRequest = true,
                proto = RMCProtocolId.NATTraversalService,
                methodID = 4,
                callID = 1,
                request = new RMCPRequestDDL<object>(new { cid = 42u, result = false })
            }.ToBuffer();
            var first = Fragment(1, 0, "");
            first.payload = body;
            Feed(first);
            var next = Fragment(3, 0, "");
            next.payload = body;
            return (Feed(next) && next.payload.SequenceEqual(body), "Characterization: a sequence gap alone does not identify a fragment; the following part-zero body can be a complete RMC message");
        }
        var prefix = Fragment(1, 1, "A");
        var final = Fragment(3, 0, "C");
        var checkAcks = kind.EndsWith("ack-guard");
        if (checkAcks)
        {
            prefix.flags.Add(QPacket.PACKETFLAG.FLAG_NEED_ACK);
            final.flags.Add(QPacket.PACKETFLAG.FLAG_NEED_ACK);
        }
        var waited = !Feed(prefix);
        var incompleteDelivered = Feed(final);
        var prematureAcks = NatProbeChecks.Drain(receiverA).Length;
        var retryFirst = !Feed(Fragment(1, 1, "A"));
        var retryMiddle = !Feed(Fragment(2, 2, "B"));
        var retryFinal = Fragment(3, 0, "C");
        var retryOk = retryFirst && retryMiddle && Feed(retryFinal) && Encoding.ASCII.GetString(retryFinal.payload) == "ABC";
        return (waited && !incompleteDelivered && prematureAcks == 0 && retryOk,
            $"Known incomplete multi-part body delivered {incompleteDelivered}; premature fragment ACKs {prematureAcks}; full ordered retry works {retryOk}. No global sequence gate is assumed");
    }
}
