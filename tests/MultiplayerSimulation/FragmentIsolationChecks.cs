using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using QNetZ;
using QNetZ.Factory;
using RDVServices.Services;

static class FragmentIsolationChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [
        ("interleaving-2", true), ("interleaving-5", true), ("interleaving-8", true),
        ("singleton-preserves-other-owner", true), ("session-isolation", true),
        ("source-port-isolation", true), ("destination-port-isolation", true), ("stream-type-isolation", true),
        ("connection-object-isolation", true), ("duplicate-prefix-control", false), ("ordered-three-parts-control", false),
        ("ack-recipient-isolation", true), ("ack-partial-timing-control", false), ("ack-unreliable-bypass-control", false),
        ("rejection-preserves-other-owner", true), ("dropped-owner-cleanup", true), ("inactive-owner-cleanup", true),
        ("reconnect-generation", true), ("rmc-dispatch-2", true), ("rmc-dispatch-5", true), ("rmc-dispatch-8", true)
    ];
    static readonly MethodInfo Defrag = typeof(QPacketHandlerPRUDP).GetMethod("Defrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static bool Feed(QPacketHandlerPRUDP handler, QClient client, QPacket packet) => (bool)Defrag.Invoke(handler, [client, packet])!;
    static QPacket Fragment(QClient client, ushort sequence, byte part, string body, byte session = 1, byte source = 0x3f, byte destination = 0x31, bool ack = false) => new()
    {
        type = QPacket.PACKETTYPE.DATA,
        flags = ack ? [QPacket.PACKETFLAG.FLAG_RELIABLE, QPacket.PACKETFLAG.FLAG_NEED_ACK] : [QPacket.PACKETFLAG.FLAG_RELIABLE],
        m_uiSignature = client.IDrecv,
        m_bySessionID = session,
        uiSeqId = sequence,
        m_byPartNumber = part,
        m_oSourceVPort = new(source),
        m_oDestinationVPort = new(destination),
        payload = Encoding.ASCII.GetBytes(body),
        usesCompression = false
    };
    static string Body(QPacket packet) => Encoding.ASCII.GetString(packet.payload);
    static int Pending(QPacketHandlerPRUDP handler)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(QPacketHandlerPRUDP).GetField("FragmentBuffers", flags) is { } field)
            return ((IDictionary)field.GetValue(handler)!).Values.Cast<IList>().Sum(list => list.Count);
        return ((IList)typeof(QPacketHandlerPRUDP).GetField("AccumulatedPackets", flags)!.GetValue(handler)!).Count;
    }

    public static (bool, string) Run(string kind, string? payloadSentinel = null)
    {
        if (kind.StartsWith("interleaving-", StringComparison.Ordinal))
            return Interleaving(int.Parse(kind.Split('-')[1]));
        if (kind.StartsWith("rmc-dispatch-", StringComparison.Ordinal))
            return Dispatch(int.Parse(kind.Split('-')[2]));
        using var f = new Fixture(2);
        var a = f.Clients[0];
        var b = f.Clients[1];
        var h = f.Handler;
        var prefix = payloadSentinel ?? "prefix";
        if (kind == "duplicate-prefix-control" || kind == "ordered-three-parts-control")
        {
            var first = Fragment(a, 10, 1, prefix);
            var wait = !Feed(h, a, first);
            if (kind == "duplicate-prefix-control")
                wait &= !Feed(h, a, Fragment(a, 10, 1, prefix));
            else
                wait &= !Feed(h, a, Fragment(a, 11, 2, "middle"));
            var final = Fragment(a, kind == "duplicate-prefix-control" ? (ushort)11 : (ushort)12, 0, "end");
            var expected = prefix + (kind == "duplicate-prefix-control" ? "" : "middle") + "end";
            return (wait && Feed(h, a, final) && Body(final) == expected && Pending(h) == 0, $"{kind}: correct complete body and no pending fragments required");
        }
        if (kind == "ack-partial-timing-control")
        {
            var waiting = !Feed(h, a, Fragment(a, 10, 1, prefix, ack: true));
            var before = NatProbeChecks.Drain(f.Receivers[0]);
            var final = Fragment(a, 11, 0, "end", ack: true);
            var complete = Feed(h, a, final);
            var acks = NatProbeChecks.Drain(f.Receivers[0]);
            return (waiting && before.Length == 0 && complete && Body(final) == prefix + "end" && ValidAcks(acks, a, [10, 11]),
                $"Partial/final ACK datagrams {before.Length}/{acks.Length}; existing deferred ACK timing retained");
        }
        if (kind == "ack-unreliable-bypass-control")
        {
            var waiting = !Feed(h, a, Fragment(a, 10, 1, prefix));
            var ack = Fragment(b, 11, 0, "");
            ack.flags = [QPacket.PACKETFLAG.FLAG_ACK, QPacket.PACKETFLAG.FLAG_RELIABLE];
            var plain = Fragment(b, 12, 0, "plain");
            plain.flags = [];
            var bypass = Feed(h, b, ack) && Feed(h, b, plain);
            var final = Fragment(a, 11, 0, "end");
            return (waiting && bypass && Feed(h, a, final) && Body(final) == prefix + "end" && Pending(h) == 0,
                "ACK and unreliable DATA leave pending reliable fragments intact");
        }
        if (kind is "dropped-owner-cleanup" or "inactive-owner-cleanup")
        {
            Feed(h, a, Fragment(a, 10, 1, prefix));
            Feed(h, b, Fragment(b, 10, 1, "other"));
            if (kind == "dropped-owner-cleanup")
                a.State = QClient.StateType.Dropped;
            else
                a.LastPacketTime = DateTime.UtcNow.AddSeconds(-Constants.ClientTimeoutSeconds - 1);
            h.Update();
            var retained = Pending(h);
            var final = Fragment(b, 11, 0, "end");
            var complete = Feed(h, b, final);
            return (retained == 1 && complete && Body(final) == "otherend" && Pending(h) == 0,
                $"Dropped owner removed; surviving pending packets {retained}/1; surviving message correct {complete && Body(final) == "otherend"}");
        }
        if (kind == "reconnect-generation")
        {
            Feed(h, a, Fragment(a, 10, 1, "old"));
            var disconnect = Fragment(a, 12, 0, "");
            disconnect.type = QPacket.PACKETTYPE.DISCONNECT;
            h.ProcessPacket(disconnect.toBuffer(), a.Endpoint);
            NatProbeChecks.Drain(f.Receivers[0]);
            h.Update();
            var replacement = h.NewQClient(a.Endpoint);
            replacement.IDsend = a.IDsend;
            replacement.State = QClient.StateType.Active;
            var waiting = !Feed(h, replacement, Fragment(replacement, 10, 1, prefix));
            var final = Fragment(replacement, 11, 0, "end");
            var complete = Feed(h, replacement, final);
            return (waiting && complete && Body(final) == prefix + "end" && Pending(h) == 0,
                $"Same endpoint after disconnect: fresh message correct {complete && Body(final) == prefix + "end"}; pending {Pending(h)}");
        }
        if (kind == "rejection-preserves-other-owner")
        {
            Feed(h, a, Fragment(a, 10, 1, prefix));
            Feed(h, b, Fragment(b, 20, 3, "bad"));
            var rejected = !Feed(h, b, Fragment(b, 21, 0, "tail"));
            var final = Fragment(a, 11, 0, "end");
            var complete = Feed(h, a, final);
            return (rejected && complete && Body(final) == prefix + "end" && Pending(h) == 0,
                $"Invalid other-owner parts rejected {rejected}; unrelated message preserved {complete && Body(final) == prefix + "end"}");
        }

        var wantsAck = kind == "ack-recipient-isolation";
        var firstPart = Fragment(a, 10, 1, prefix, ack: wantsAck);
        var waitFirst = !Feed(h, a, firstPart);
        var other = Fragment(b, 11, 0, "single", ack: wantsAck);
        QClient otherOwner = b;
        switch (kind)
        {
            case "singleton-preserves-other-owner":
            case "ack-recipient-isolation":
                break;
            case "session-isolation":
                otherOwner = a;
                other = Fragment(a, 11, 0, "single", session: 2);
                break;
            case "source-port-isolation":
                otherOwner = a;
                other = Fragment(a, 11, 0, "single", source: 0x3e);
                break;
            case "destination-port-isolation":
                otherOwner = a;
                other = Fragment(a, 11, 0, "single", destination: 0x32);
                break;
            case "stream-type-isolation":
                otherOwner = a;
                other = Fragment(a, 11, 0, "single", source: 0x1f, destination: 0x11);
                break;
            case "connection-object-isolation":
                // Same wire identifiers/endpoint, distinct transport object. This
                // directly checks ownership even if a signature is reused later.
                otherOwner = new QClient(a.IDrecv, a.Endpoint) { IDsend = a.IDsend };
                other = Fragment(otherOwner, 11, 0, "single");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var otherComplete = Feed(h, otherOwner, other);
        var otherAcks = NatProbeChecks.Drain(f.Receivers[1]);
        var last = Fragment(a, 12, 0, "end", ack: wantsAck);
        var firstComplete = Feed(h, a, last);
        var firstAcks = NatProbeChecks.Drain(f.Receivers[0]);
        var passed = waitFirst && otherComplete && Body(other) == "single" && firstComplete && Body(last) == prefix + "end" && Pending(h) == 0;
        if (wantsAck)
            passed &= otherAcks.Length == 0 && ValidAcks(firstAcks, a, [10, 12]);
        return (passed, $"{kind}: other body isolated {otherComplete && Body(other) == "single"}; first body preserved {firstComplete && Body(last) == prefix + "end"}; ACK A/B {firstAcks.Length}/{otherAcks.Length}");
    }

    static (bool, string) Interleaving(int count)
    {
        using var f = new Fixture(count);
        var waiting = true;
        var completed = 0;
        for (var i = 0; i < count; i++)
            waiting &= !Feed(f.Handler, f.Clients[i], Fragment(f.Clients[i], 10, 1, $"{i}:prefix"));
        for (var i = count - 1; i >= 0; i--)
        {
            var final = Fragment(f.Clients[i], 11, 0, ":end");
            if (Feed(f.Handler, f.Clients[i], final) && Body(final) == $"{i}:prefix:end")
                completed++;
        }
        return (waiting && completed == count && Pending(f.Handler) == 0, $"{completed}/{count} interleaved clients receive only their own complete body, reverse completion order");
    }
    static (bool, string) Dispatch(int count)
    {
        using var f = new Fixture(count);
        if (RMCServiceFactory.GetServiceFactory(RMCProtocolId.NATTraversalService) == null)
            RMCServiceFactory.RegisterService<NATTraversalService>();
        var tails = new QPacket[count];
        for (var i = 0; i < count; i++)
        {
            var bytes = new RMCPacket
            {
                proto = RMCProtocolId.NATTraversalService,
                methodID = 4,
                callID = (uint)(100 + i),
                isRequest = true,
                request = new RMCPRequestDDL<ReportRequest>(new() { cid = (uint)(9000 + i), result = false })
            }.ToBuffer();
            if (bytes.Length != 18)
                throw new Exception("Scalar RMC fixture length changed");
            var first = Fragment(f.Clients[i], 10, 1, "", ack: true);
            first.payload = bytes[..13];
            tails[i] = Fragment(f.Clients[i], 11, 0, "", ack: true);
            tails[i].payload = bytes[13..];
            f.Handler.ProcessPacket(first.toBuffer(), f.Clients[i].Endpoint);
        }
        // Scalar-only method and five-byte tails keep the broken baseline bounded:
        // isolated tails parse as non-request headers, never attacker-sized lists.
        for (var i = count - 1; i >= 0; i--)
            f.Handler.ProcessPacket(tails[i].toBuffer(), f.Clients[i].Endpoint);
        f.Handler.Update();
        var correct = 0;
        for (var i = 0; i < count; i++)
        {
            var replies = NatProbeChecks.Drain(f.Receivers[i]).Where(p => p.type == QPacket.PACKETTYPE.DATA && !p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK)).ToArray();
            if (replies.Length != 1 || replies[0].m_uiSignature != f.Clients[i].IDsend)
                continue;
            var rmc = new RMCPacket(replies[0]);
            if (!rmc.isRequest && rmc.success && rmc.proto == RMCProtocolId.NATTraversalService && rmc.methodID == 4 && rmc.callID == (uint)(100 + i))
                correct++;
        }
        return (correct == count && Pending(f.Handler) == 0, $"{correct}/{count} clients receive their own successful RMC response after interleaved wire fragments");
    }
    static bool ValidAcks(QPacket[] packets, QClient client, ushort[] expected) => packets.Length == expected.Length
        && packets.All(p => p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK) && p.m_uiSignature == client.IDsend
            && p.m_bySessionID == 1 && p.m_oSourceVPort.toByte() == 0x31 && p.m_oDestinationVPort.toByte() == 0x3f)
        && packets.Select(p => p.uiSeqId).Order().SequenceEqual(expected.Order());
    sealed class ReportRequest
    {
        public uint cid
        {
            get; set;
        }
        public bool result
        {
            get; set;
        }
    }
    sealed class Fixture : IDisposable
    {
        readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly List<UdpClient> Receivers = [];
        public readonly QPacketHandlerPRUDP Handler;
        public readonly QClient[] Clients;
        public Fixture(int count)
        {
            Handler = new(socket, 2, 0, "fragment-isolation-check");
            Clients = Enumerable.Range(0, count).Select(i =>
            {
                var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                Receivers.Add(receiver);
                var client = Handler.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
                client.IDsend = (uint)(10000 + i);
                client.State = QClient.StateType.Active;
                return client;
            }).ToArray();
        }
        public void Dispose()
        {
            socket.Dispose();
            foreach (var receiver in Receivers)
                receiver.Dispose();
        }
    }
}
