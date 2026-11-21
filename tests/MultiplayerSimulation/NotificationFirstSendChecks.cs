using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using QNetZ;
using QNetZ.DDL;

static class NotificationFirstSendChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [
        ("while-earlier-call-pending",true),("after-partial-ack",true),("fresh-call-after-old-expiry",true),
        ("fragmented-call-while-pending",true),("two-calls-before-update-control",false),
        ("one-fragmented-call-control",false),("other-recipient-control",false),("earlier-retry-deadline-control",false)
    ];
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static (bool, string) Run(string kind)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var other = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var h = new QPacketHandlerPRUDP(socket, 2, 0, "first-send-check");
        var a = h.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
        a.IDsend = 2001;
        a.SessionID = 1;
        var b = h.NewQClient((IPEndPoint)other.Client.LocalEndPoint!);
        b.IDsend = 2002;
        b.SessionID = 1;
        IList Caches() => (IList)typeof(QPacketHandlerPRUDP).GetField("CachedResponses", Flags)!.GetValue(h)!;
        void Set(object cache, string field, object value) => cache.GetType().GetField(field)!.SetValue(cache, value);
        DateTime Get(object cache, string field) => (DateTime)cache.GetType().GetField(field)!.GetValue(cache)!;
        void Call(QClient c, bool big = false) => RMC.SendRMCCall(h, c, RMCProtocolId.NATTraversalService, 2,
            new RMCPRequestDDL<StationURL>(new("prudp", big ? new string('x', 3000) : "127.0.0.1", new Dictionary<string, int> { ["port"] = 32001 })));
        void Ack(QPacket packet)
        {
            h.ProcessPacket(new QPacket
            {
                type = QPacket.PACKETTYPE.DATA,
                flags = [QPacket.PACKETFLAG.FLAG_ACK],
                m_oSourceVPort = packet.m_oDestinationVPort,
                m_oDestinationVPort = packet.m_oSourceVPort,
                m_uiSignature = a.IDrecv,
                m_bySessionID = packet.m_bySessionID,
                uiSeqId = packet.uiSeqId,
                payload = [],
                usesCompression = false
            }.toBuffer(), a.Endpoint);
        }
        if (kind == "two-calls-before-update-control")
        {
            Call(a);
            Call(a);
            h.Update();
            var got = NatProbeChecks.Drain(receiver);
            return (got.Length == 2 && got.Select(p => p.uiSeqId).Distinct().Count() == 2, "Two queued calls still deliver distinct packets in one update");
        }
        if (kind == "one-fragmented-call-control")
        {
            Call(a, true);
            h.Update();
            var got = NatProbeChecks.Drain(receiver);
            return (got.Length > 1 && got[^1].m_byPartNumber == 0 && Caches().Count == 1, "Fragments of one server call keep one common cache and complete body");
        }
        Call(a);
        if (kind == "after-partial-ack")
            Call(a);
        h.Update();
        var initial = NatProbeChecks.Drain(receiver);
        if (initial.Length != (kind == "after-partial-ack" ? 2 : 1))
            throw new InvalidOperationException("Initial notification fixture failed");
        if (kind == "after-partial-ack")
            Ack(initial[0]);
        var earlier = Caches()[0]!;
        var due = DateTime.UtcNow.AddMinutes(1);
        Set(earlier, "ResendTime", due);
        if (kind == "fresh-call-after-old-expiry")
            Set(earlier, "DropTime", DateTime.UtcNow.AddSeconds(-1));
        if (kind == "earlier-retry-deadline-control")
        {
            h.Update();
            var early = NatProbeChecks.Drain(receiver);
            Set(earlier, "ResendTime", DateTime.UtcNow.AddSeconds(-1));
            h.Update();
            var retry = NatProbeChecks.Drain(receiver);
            return (early.Length == 0 && retry.Length == 1 && initial[0].toBuffer().SequenceEqual(retry[0].toBuffer()), "An existing call does not resend before due and retains byte-identical retry behavior");
        }
        var target = kind == "other-recipient-control" ? b : a;
        var fragmented = kind == "fragmented-call-while-pending";
        Call(target, fragmented);
        var call = target.CallCounterRMC;
        h.Update();
        var primary = NatProbeChecks.Drain(receiver);
        var secondary = NatProbeChecks.Drain(other);
        var gotNew = target == a ? primary : secondary;
        var noExtra = target == a ? secondary.Length == 0 : primary.Length == 0;
        bool payload = false;
        if (gotNew.Length > 0)
        {
            var sorted = gotNew.OrderBy(p => p.uiSeqId).ToArray();
            var merged = new QPacket { payload = sorted.SelectMany(p => p.payload).ToArray() };
            var rmc = new RMCPacket(merged);
            payload = rmc.callID == call && rmc.proto == RMCProtocolId.NATTraversalService && rmc.methodID == 2
                && sorted.All(p => p.m_uiSignature == target.IDsend) && sorted[^1].m_byPartNumber == 0;
        }
        bool correctCount = fragmented ? gotNew.Length > 1 : gotNew.Length == 1;
        return (correctCount && payload && noExtra && Get(earlier, "ResendTime") == due,
            $"New call first-send datagrams {gotNew.Length}; correct payload {payload}; old retry deadline preserved {Get(earlier, "ResendTime") == due}; no unrelated sends {noExtra}");
    }
}
