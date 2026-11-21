using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using QNetZ;
using QNetZ.Factory;
using RDVServices.Services;

static class TransportEdgeChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = BuildCases().ToArray();
    static IEnumerable<(string, bool)> BuildCases()
    {
        yield return ("wrap-two", true);
        yield return ("wrap-three", true);
        yield return ("wrap-before-boundary-control", false);
        yield return ("zero-start-control", false);
        yield return ("overlap-forward", true);
        yield return ("overlap-reverse", true);
        yield return ("overlap-wrap", true);
        yield return ("incomplete-keeps-other-prefix", true);
        yield return ("standalone-keeps-future-prefix", true);
        foreach (var order in Permutations([0, 1, 2]))
            yield return ("three-message-finals-" + string.Concat(order), true);
        foreach (var order in Permutations([0, 1, 2]))
            yield return ("wire-three-" + string.Concat(order), order[0] == 2);
        foreach (var order in Permutations([0, 1, 2, 3]))
            yield return ("wire-two-" + string.Concat(order), string.Concat(order) is not ("0123" or "2301"));
        yield return ("wire-wrap-two", true);
        yield return ("wire-wrap-three", true);
        foreach (var count in new[] { 2, 5, 8 })
            yield return ("wire-interleaving-wrap-" + count, true);
        yield return ("wire-duplicate-prefix-control", false);
        yield return ("wire-lost-final-retry-control", false);
        yield return ("wire-complete-reversal-characterization", false);
        foreach (var name in new[]{"empty","short-size","declared-long","declared-short","short-request-header",
            "partial-success","partial-error","short-extended-header"})
            yield return ("frame-" + name, true);
        foreach (var name in new[] { "success", "error", "extended" })
            yield return ("frame-" + name + "-control", false);
    }
    static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length == 0)
        {
            yield return [];
            yield break;
        }
        for (var i = 0; i < items.Length; i++)
            foreach (var tail in Permutations(items.Where((_, j) => j != i).ToArray()))
                yield return new[] { items[i] }.Concat(tail).ToArray();
    }
    public static (bool, string) Run(string kind)
    {
        if (kind.StartsWith("frame-"))
            return Frame(kind);
        if (kind.StartsWith("wire-interleaving-wrap-"))
            return MultiClient(int.Parse(kind.Split('-')[^1]));
        if (kind.StartsWith("wire-"))
            return Wire(kind);
        using var f = new Fixture();
        var h = f.Handler;
        var c = f.Clients[0];
        var defrag = typeof(QPacketHandlerPRUDP).GetMethod("Defrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Feed(QPacket p) => (bool)defrag.Invoke(h, [c, p])!;
        QPacket P(ushort seq, byte part, string text) => Packet(c, seq, part, Encoding.ASCII.GetBytes(text), false);
        string Body(QPacket p) => Encoding.ASCII.GetString(p.payload);
        if (kind.StartsWith("wrap-") || kind == "zero-start-control")
        {
            ushort first = kind == "wrap-before-boundary-control" ? (ushort)65534 : kind == "zero-start-control" ? (ushort)0 : (ushort)65535;
            var three = kind == "wrap-three";
            if (three)
                first = 65534;
            var waiting = !Feed(P(first, 1, "A"));
            if (three)
                waiting &= !Feed(P(unchecked((ushort)(first + 1)), 2, "B"));
            var last = P(unchecked((ushort)(first + (three ? 2 : 1))), 0, "C");
            var complete = Feed(last);
            return (waiting && complete && Body(last) == (three ? "ABC" : "AC") && Pending(h) == 0,
                $"Start {first}; complete {complete}; body {Body(last)}; pending fragments {Pending(h)}");
        }
        if (kind == "incomplete-keeps-other-prefix")
        {
            Feed(P(10, 1, "A"));
            Feed(P(20, 1, "B"));
            var rejected = !Feed(P(12, 0, "bad-tail"));
            var kept = Pending(h);
            var last = P(21, 0, "b");
            var complete = Feed(last);
            return (rejected && kept == 1 && complete && Body(last) == "Bb" && Pending(h) == 0,
                $"Incomplete A rejected {rejected}; B prefix retained {kept}/1; B body {Body(last)}");
        }
        if (kind == "standalone-keeps-future-prefix")
        {
            Feed(P(11, 1, "B"));
            var earlier = P(10, 0, "standalone");
            var firstOk = Feed(earlier) && Body(earlier) == "standalone";
            var kept = Pending(h);
            var final = P(12, 0, "b");
            var secondOk = Feed(final) && Body(final) == "Bb";
            return (firstOk && kept == 1 && secondOk && Pending(h) == 0, $"Earlier standalone correct {firstOk}; future prefix retained {kept}/1; later multipart correct {secondOk}");
        }
        if (kind.StartsWith("three-message-finals-"))
        {
            for (var i = 0; i < 3; i++)
                Feed(P((ushort)(10 + 2 * i), 1, $"{i}:"));
            var correct = 0;
            foreach (var digit in kind.Split('-')[^1])
            {
                var i = digit - '0';
                var last = P((ushort)(11 + 2 * i), 0, "end");
                if (Feed(last) && Body(last) == $"{i}:end")
                    correct++;
            }
            return (correct == 3 && Pending(h) == 0, $"{correct}/3 bodies correct with final order {kind.Split('-')[^1]}; pending {Pending(h)}");
        }
        ushort a = kind == "overlap-wrap" ? (ushort)65534 : (ushort)10;
        ushort b = unchecked((ushort)(a + 2));
        Feed(P(a, 1, "A"));
        Feed(P(b, 1, "B"));
        var finals = kind == "overlap-forward" ? new[] { P(unchecked((ushort)(a + 1)), 0, "a"), P(unchecked((ushort)(b + 1)), 0, "b") }
            : new[] { P(unchecked((ushort)(b + 1)), 0, "b"), P(unchecked((ushort)(a + 1)), 0, "a") };
        var bodies = new List<string>();
        foreach (var last in finals)
        if (Feed(last))
            bodies.Add(Body(last));
        return (bodies.Count == 2 && bodies.Contains("Aa") && bodies.Contains("Bb") && Pending(h) == 0,
            $"Overlapping bodies [{string.Join(',', bodies)}]; expected Aa/Bb; pending {Pending(h)}");
    }
    static (bool, string) Wire(string kind)
    {
        using var f = new Fixture();
        var c = f.Clients[0];
        QPacket[] packets;
        int[] order;
        uint[] calls;
        if (kind.StartsWith("wire-two-"))
        {
            packets = Message(c, 10, 100, 2).Concat(Message(c, 12, 101, 2)).ToArray();
            order = kind.Split('-')[^1].Select(x => x - '0').ToArray();
            calls = [100, 101];
        }
        else if (kind.StartsWith("wire-three-"))
        {
            packets = Message(c, 10, 100, 3);
            order = kind.Split('-')[^1].Select(x => x - '0').ToArray();
            calls = [100];
        }
        else if (kind == "wire-complete-reversal-characterization")
        {
            packets = [Packet(c, 10, 0, Body(100)), Packet(c, 11, 0, Body(101))];
            order = [1, 0];
            calls = [100, 101];
        }
        else
        {
            var count = kind == "wire-wrap-three" ? 3 : 2;
            var seq = kind == "wire-wrap-three" ? (ushort)65534 : kind == "wire-wrap-two" ? (ushort)65535 : (ushort)10;
            packets = Message(c, seq, 100, count);
            calls = [100];
            order = kind == "wire-duplicate-prefix-control" ? [0, 0, 1] : kind == "wire-lost-final-retry-control" ? [0] : Enumerable.Range(0, count).ToArray();
        }
        foreach (var i in order)
            f.Send(0, packets[i]);
        // Model a bounded reliable sender: resend only requests/fragments without
        // transport ACKs. A falsely ACKed tail is never replayed by this sender.
        for (var round = 0; round < 3 && !calls.All(f.Replies[0].Contains); round++)
            foreach (var p in packets)
            if (!f.Acked[0].Contains(p.uiSeqId))
                f.Send(0, p);
        var valid = calls.All(f.Replies[0].Contains) && f.UnexpectedReplies == 0;
        return (valid && Pending(f.Handler) == 0,
            $"Expected calls [{string.Join(',', calls)}], replies [{string.Join(',', f.Replies[0])}]; ACKed request sequences [{string.Join(',', f.Acked[0])}]; pending {Pending(f.Handler)}; unexpected replies {f.UnexpectedReplies}");
    }
    static (bool, string) MultiClient(int count)
    {
        using var f = new Fixture(count);
        var tails = new QPacket[count];
        for (var i = 0; i < count; i++)
        {
            var p = Message(f.Clients[i], 65535, (uint)(100 + i), 2);
            f.Send(i, p[0]);
            tails[i] = p[1];
        }
        for (var i = count - 1; i >= 0; i--)
            f.Send(i, tails[i]);
        var correct = Enumerable.Range(0, count).Count(i => f.Replies[i].Contains((uint)(100 + i)));
        return (correct == count && Pending(f.Handler) == 0 && f.UnexpectedReplies == 0, $"{correct}/{count} clients receive their wrapped fragmented RPC reply; pending {Pending(f.Handler)}");
    }
    static (bool, string) Frame(string kind)
    {
        using var f = new Fixture();
        var c = f.Clients[0];
        byte[] body = Body(100);
        if (kind == "frame-empty")
            body = [];
        else if (kind == "frame-short-size")
            body = [1, 2, 3];
        else if (kind == "frame-declared-long")
            BitConverter.GetBytes((uint)body.Length + 100).CopyTo(body, 0);
        else if (kind == "frame-declared-short")
            BitConverter.GetBytes(0u).CopyTo(body, 0);
        else if (kind == "frame-short-request-header")
        {
            body = body[..12];
            BitConverter.GetBytes(8u).CopyTo(body, 0);
        }
        else if (kind.Contains("success"))
            body = new RMCPacket { proto = RMCProtocolId.NATTraversalService, isRequest = false, callID = 200, methodID = 2, response = new RMCPResponseEmpty() }.ToBuffer();
        else if (kind.Contains("error"))
            body = new RMCPacket { proto = RMCProtocolId.NATTraversalService, isRequest = false, callID = 200, error = 0x80010001 }.ToBuffer();
        else if (kind.Contains("extended"))
        {
            body = new byte[kind.Contains("short") ? 14 : 15];
            BitConverter.GetBytes((uint)body.Length - 4).CopyTo(body, 0);
            body[4] = 0xff;
            body[5] = 0x80;
            body[6] = 1;
            BitConverter.GetBytes(200u).CopyTo(body, 7);
            body[11] = 1;
        }
        if (kind.StartsWith("frame-partial-"))
            body = body[..10];
        var p = Packet(c, 10, 0, body);
        var control = kind.EndsWith("control");
        if (!control)
            p.m_bySessionID = 7;
        f.Send(0, p);
        var acknowledged = f.Acked[0].Contains(10);
        return (control ? acknowledged : !acknowledged && f.Replies[0].Count == 0 && c.SessionID == 1 && c.SeqCounter == 0,
            $"{kind}: request ACK {acknowledged}; expected ACK {control}; replies {f.Replies[0].Count}; connection session {c.SessionID}");
    }
    static byte[] Body(uint call) => new RMCPacket
    {
        proto = RMCProtocolId.NATTraversalService,
        methodID = 4,
        callID = call,
        isRequest = true,
        request = new RMCPRequestDDL<ScalarRequest>(new() { cid = 9000 + call, result = false })
    }.ToBuffer();
    static QPacket[] Message(QClient c, ushort first, uint call, int parts)
    {
        var bytes = Body(call);
        if (bytes.Length != 18)
            throw new InvalidOperationException("Bounded scalar fixture changed");
        var pieces = parts == 2 ? new[] { bytes[..13], bytes[13..] } : new[] { bytes[..6], bytes[6..12], bytes[12..] };
        return pieces.Select((p, i) => Packet(c, unchecked((ushort)(first + i)), i == pieces.Length - 1 ? (byte)0 : (byte)(i + 1), p)).ToArray();
    }
    static QPacket Packet(QClient c, ushort seq, byte part, byte[] body, bool ack = true) => new()
    {
        type = QPacket.PACKETTYPE.DATA,
        flags = ack ? [QPacket.PACKETFLAG.FLAG_RELIABLE, QPacket.PACKETFLAG.FLAG_NEED_ACK] : [QPacket.PACKETFLAG.FLAG_RELIABLE],
        m_oSourceVPort = new(0x3f),
        m_oDestinationVPort = new(0x31),
        m_uiSignature = c.IDrecv,
        m_bySessionID = 1,
        uiSeqId = seq,
        m_byPartNumber = part,
        payload = body,
        usesCompression = false
    };
    static int Pending(QPacketHandlerPRUDP h) => ((IDictionary)typeof(QPacketHandlerPRUDP).GetField("FragmentBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(h)!).Values.Cast<IList>().Sum(x => x.Count);
    sealed class ScalarRequest
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
        readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0)); readonly UdpClient[] receivers;
        public readonly QPacketHandlerPRUDP Handler; public readonly QClient[] Clients;
        public readonly HashSet<ushort>[] Acked; public readonly HashSet<uint>[] Replies; public int UnexpectedReplies;
        public Fixture(int count = 1)
        {
            if (RMCServiceFactory.GetServiceFactory(RMCProtocolId.NATTraversalService) == null)
                RMCServiceFactory.RegisterService<NATTraversalService>();
            Handler = new(socket, 2, 0, "transport-edge-check");
            receivers = Enumerable.Range(0, count).Select(_ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))).ToArray();
            Clients = receivers.Select((r, i) => { var c = Handler.NewQClient((IPEndPoint)r.Client.LocalEndPoint!); c.State = QClient.StateType.Active; c.IDsend = (uint)(2000 + i); c.SessionID = 1; return c; }).ToArray();
            Acked = receivers.Select(_ => new HashSet<ushort>()).ToArray();
            Replies = receivers.Select(_ => new HashSet<uint>()).ToArray();
        }
        public void Send(int index, QPacket p)
        {
            Handler.ProcessPacket(p.toBuffer(), Clients[index].Endpoint);
            Handler.Update();
            Collect(index);
        }
        void Collect(int index)
        {
            foreach (var p in NatProbeChecks.Drain(receivers[index]))
            {
                if (p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
                {
                    Acked[index].Add(p.uiSeqId);
                    continue;
                }
                if (p.type != QPacket.PACKETTYPE.DATA)
                    continue;
                var rmc = new RMCPacket(p);
                if (!rmc.isRequest && rmc.success && rmc.proto == RMCProtocolId.NATTraversalService && rmc.methodID == 4)
                    Replies[index].Add(rmc.callID);
                else
                    UnexpectedReplies++;
                Handler.ProcessPacket(new QPacket
                {
                    type = QPacket.PACKETTYPE.DATA,
                    flags = [QPacket.PACKETFLAG.FLAG_ACK],
                    m_uiSignature = Clients[index].IDrecv,
                    m_bySessionID = p.m_bySessionID,
                    uiSeqId = p.uiSeqId,
                    m_oSourceVPort = p.m_oDestinationVPort,
                    m_oDestinationVPort = p.m_oSourceVPort,
                    payload = [],
                    usesCompression = false
                }.toBuffer(), Clients[index].Endpoint);
            }
        }
        public void Dispose()
        {
            socket.Dispose();
            foreach (var r in receivers)
                r.Dispose();
        }
    }
}
