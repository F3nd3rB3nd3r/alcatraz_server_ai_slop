using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using QNetZ;

// Bounded synthetic reassembly checks; no live backend, authentication or DDL fuzzing.
static class FragmentHardeningChecks
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    public static readonly (string Kind, bool OriginalViolation)[] Cases =
    [
        ("expiry-update-active-owner", true), ("expiry-ingress", true),
        ("expiry-keeps-fresh-same-stream", true), ("expiry-isolates-owner", true),
        ("expiry-duplicate-does-not-refresh", true), ("expiry-boundary", true),
        ("conflict-payload", true), ("conflict-part", true), ("conflict-final", true),
        ("conflict-repeat-bounded", true), ("duplicate-checksum-is-not-identity", true),
        ("duplicate-identical-control", false), ("checksum-collision-control", false),
        ("limit-owner-packets", true), ("limit-handler-packets", true),
        ("limit-owner-bytes", true), ("limit-handler-bytes", true),
        ("limit-owner-streams", true), ("limit-handler-streams", true),
        ("full-buffer-final-drains", true), ("full-buffer-standalone-control", false),
        ("capacity-reclaimed-completion", false), ("capacity-reclaimed-rejection", false),
        ("capacity-reclaimed-drop", false), ("capacity-reclaimed-expiry", true),
        ("maximum-representable-message-control", false), ("wire-conflict-recovery", true),
        ("ack-unreliable-bypass-control", false)
    ];

    // Only small cases run under diagnostic logging, avoiding deliberate mass traffic.
    public static readonly string[] DiagnosticCases =
    ["expiry-update-active-owner", "expiry-keeps-fresh-same-stream", "conflict-payload",
     "conflict-part", "conflict-final", "duplicate-checksum-is-not-identity", "wire-conflict-recovery",
     "limit-owner-streams", "maximum-representable-message-control"];

    public static (bool, string) Run(string kind, string abandonedPayload = "old")
    {
        using var f = new Fixture(kind.Contains("handler") ? 17 : 2);
        var h = f.Handler;
        var a = f.Clients[0];
        if (kind.StartsWith("expiry-"))
        {
            f.Feed(a, Packet(a, 10, 1, abandonedPayload));
            var oldTick = Environment.TickCount64 - (kind == "expiry-duplicate-does-not-refresh" ? 60000 : Lifetime + 1000);
            Age(h, oldTick);
            if (kind == "expiry-keeps-fresh-same-stream")
                f.Feed(a, Packet(a, 20, 1, "fresh"));
            if (kind == "expiry-isolates-owner")
                f.Feed(f.Clients[1], Packet(f.Clients[1], 20, 1, "fresh"));
            if (kind == "expiry-duplicate-does-not-refresh")
                f.Feed(a, Packet(a, 10, 1, abandonedPayload));
            if (kind == "expiry-boundary")
            {
                Sweep(h, oldTick + Lifetime - 1);
                var before = Pending(h);
                Sweep(h, oldTick + Lifetime);
                var after = Pending(h);
                return (before == 1 && after == 0, $"Before expiry {before}/1; at deadline {after}/0");
            }
            if (kind == "expiry-ingress")
            {
                ForceSweep(h);
                var final = Packet(a, 11, 0, "tail");
                var result = f.Feed(a, final);
                return (result && Text(final) == "tail" && Pending(h) == 0,
                    $"Expired prefix not combined on ingress: body {Text(final)}; pending {Pending(h)}");
            }
            if (kind == "expiry-duplicate-does-not-refresh")
                Sweep(h, oldTick + Lifetime);
            else
            {
                ForceSweep(h);
                h.Update();
            }
            if (kind is "expiry-keeps-fresh-same-stream" or "expiry-isolates-owner")
            {
                var owner = kind == "expiry-isolates-owner" ? f.Clients[1] : a;
                var freshCount = Pending(h);
                var final = Packet(owner, 21, 0, "tail");
                var result = f.Feed(owner, final);
                return (freshCount == 1 && result && Text(final) == "freshtail" && Pending(h) == 0,
                    $"Fresh prefix retained {freshCount}/1; correct complete body {Text(final) == "freshtail"}");
            }
            return (Pending(h) == 0 && h.Clients.Contains(a),
                $"Active owner survives {h.Clients.Contains(a)}; expired pending {Pending(h)}/0");
        }
        if (kind.StartsWith("conflict-") || kind.StartsWith("duplicate-") || kind == "checksum-collision-control")
        {
            var original = Packet(a, 10, 1, "first");
            original.checkSum = 1;
            f.Feed(a, original);
            var incoming = Packet(a, 10, kind == "conflict-part" ? (byte)2 : kind == "conflict-final" ? (byte)0 : (byte)1,
                kind.Contains("payload") || kind == "conflict-repeat-bounded" || kind == "checksum-collision-control" ? "other" : "first");
            incoming.checkSum = kind is "duplicate-identical-control" or "checksum-collision-control" ? (byte)1 : (byte)2;
            var earlyComplete = f.Feed(a, incoming);
            if (kind == "conflict-repeat-bounded")
                for (var i = 0; i < 32; i++)
                {
                    incoming = Packet(a, 10, 1, "different-" + i);
                    incoming.checkSum = (byte)(i + 3);
                    f.Feed(a, incoming);
                }
            var retained = Pending(h);
            var premature = NatProbeChecks.Drain(f.Receivers[0]).Length;
            var last = Packet(a, 11, 0, "tail");
            var complete = f.Feed(a, last);
            return (!earlyComplete && retained == 1 && premature == 0 && complete && Text(last) == "firsttail" && Pending(h) == 0,
                $"First fragment retained {retained}/1; premature packets {premature}; original body completes {complete && Text(last) == "firsttail"}");
        }
        if (kind.StartsWith("limit-"))
        {
            var bytes = kind.EndsWith("bytes");
            var streams = kind.EndsWith("streams");
            var global = kind.Contains("handler");
            var limit = Limit(global ? "Handler" : "Owner", streams ? "Streams" : bytes ? "Bytes" : "Packets");
            var body = bytes ? new byte[60000] : new byte[1];
            var perOwner = Limit("Owner", streams ? "Streams" : bytes ? "Bytes" : "Packets");
            int accepted = 0;
            long used = 0;
            int owner = 0;
            int seq = 0;
            while (used + (bytes ? body.Length : 1) <= limit)
            {
                if (global && (streams ? seq >= perOwner : bytes ? (long)(seq + 1) * body.Length > perOwner : seq >= perOwner))
                {
                    owner++;
                    seq = 0;
                }
                var p = Packet(f.Clients[owner], (ushort)seq, 1, body);
                if (streams)
                    p.m_bySessionID = (byte)seq;
                f.Feed(f.Clients[owner], p);
                accepted++;
                seq++;
                used += bytes ? body.Length : 1;
            }
            var before = Pending(h);
            var extra = Packet(f.Clients[global ? 16 : 0], (ushort)(global ? 60000 : seq), 1, body);
            if (streams)
                extra.m_bySessionID = 200;
            f.Feed(f.Clients[global ? 16 : 0], extra);
            var after = Pending(h);
            var noAck = f.Receivers.All(r => NatProbeChecks.Drain(r).Length == 0);
            return (before == accepted && after == before && noAck,
                $"{kind} reservation {used}/{limit}; accepted {before}/{accepted}; after overflow {after}; no ACK {noAck}");
        }
        if (kind == "maximum-representable-message-control")
        {
            for (var i = 0; i < 255; i++)
                f.Feed(a, Packet(a, unchecked((ushort)(65500 + i)), (byte)(i + 1), "x"));
            var final = Packet(a, unchecked((ushort)(65500 + 255)), 0, "z");
            var complete = f.Feed(a, final);
            return (complete && Text(final) == new string('x', 255) + "z" && Pending(h) == 0,
                $"All 255 prefix IDs plus final across wrap complete {complete}; length {final.payload.Length}/256");
        }
        if (kind == "wire-conflict-recovery")
        {
            // Real serialization/checksum/dispatch, bounded scalar RMC and loopback ACKs.
            if (QNetZ.Factory.RMCServiceFactory.GetServiceFactory(RMCProtocolId.NATTraversalService) == null)
                QNetZ.Factory.RMCServiceFactory.RegisterService<RDVServices.Services.NATTraversalService>();
            var body = new RMCPacket
            {
                proto = RMCProtocolId.NATTraversalService,
                isRequest = true,
                methodID = 4,
                callID = 301,
                request = new RMCPRequestDDL<Scalar>(new() { cid = 9301, result = false })
            }.ToBuffer();
            var prefix = Packet(a, 10, 1, body[..13]);
            var conflicting = Packet(a, 10, 1, body[..13]);
            conflicting.payload[5] ^= 1;
            h.ProcessPacket(prefix.toBuffer(), a.Endpoint);
            h.ProcessPacket(conflicting.toBuffer(), a.Endpoint);
            var premature = NatProbeChecks.Drain(f.Receivers[0]).Length;
            h.ProcessPacket(Packet(a, 11, 0, body[13..]).toBuffer(), a.Endpoint);
            h.Update();
            var packets = NatProbeChecks.Drain(f.Receivers[0]);
            var correct = packets.Where(p => p.type == QPacket.PACKETTYPE.DATA && !p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
                .Select(p => new RMCPacket(p)).Count(r => r.success && r.callID == 301 && r.methodID == 4);
            return (premature == 0 && correct == 1 && Pending(h) == 0, $"Premature ACKs {premature}; correct original RPC replies {correct}/1");
        }
        if (kind == "ack-unreliable-bypass-control")
        {
            f.Feed(a, Packet(a, 10, 1, "first"));
            var ack = Packet(a, 10, 0, "");
            ack.flags = [QPacket.PACKETFLAG.FLAG_RELIABLE, QPacket.PACKETFLAG.FLAG_ACK];
            var plain = Packet(a, 10, 0, "");
            plain.flags = [];
            var bypass = f.Feed(a, ack) && f.Feed(a, plain);
            var final = Packet(a, 11, 0, "tail");
            return (bypass && f.Feed(a, final) && Text(final) == "firsttail", "ACK and unreliable bypass preserve pending reliable data");
        }
        // Fill a single owner to its packet budget; final markers must still drain it.
        var cap = (int)Limit("Owner", "Packets");
        for (var i = 0; i < cap; i++)
            f.Feed(a, Packet(a, (ushort)(i * 2), 1, "x"));
        if (kind == "full-buffer-standalone-control")
        {
            var standalone = Packet(a, 65530, 0, "single");
            return (f.Feed(a, standalone) && Text(standalone) == "single" && Pending(h) == cap,
                "A complete standalone does not require persistent buffer capacity");
        }
        if (kind == "full-buffer-final-drains")
        {
            f.Feed(a, Packet(a, 50000, 1, "overflow"));
            var final = Packet(a, 1, 0, "tail");
            var complete = f.Feed(a, final);
            return (complete && Text(final) == "xtail" && Pending(h) == cap - 1,
                $"Final drains full buffer {complete}; pending {Pending(h)}/{cap - 1}");
        }
        if (kind == "capacity-reclaimed-completion")
            f.Feed(a, Packet(a, 1, 0, "tail"));
        else if (kind == "capacity-reclaimed-rejection")
            f.Feed(a, Packet(a, 65535, 0, "tail")); // Last prefix is too far: use malformed group below.
        else if (kind == "capacity-reclaimed-drop")
        {
            a.State = QClient.StateType.Dropped;
            h.Update();
            a = f.Clients[1];
        }
        else if (kind == "capacity-reclaimed-expiry")
        {
            Age(h, Environment.TickCount64 - Lifetime - 1000);
            ForceSweep(h);
            h.Update();
        }
        else
            throw new ArgumentException(kind);
        if (kind == "capacity-reclaimed-rejection")
        {
            // Reject a known missing-middle selection at the end of the filled stream.
            var bad = Packet(a, (ushort)(cap * 2 - 2 + 2), 0, "bad");
            f.Feed(a, bad);
        }
        var remaining = Pending(h);
        var admitted = Packet(a, 50000, 1, "new");
        f.Feed(a, admitted);
        var accounting = Accounting(h);
        return (remaining < cap && Pending(h) == remaining + 1 && accounting,
            $"Reclaimed capacity: retained {remaining} < {cap}; new admitted {Pending(h) == remaining + 1}; accounting {accounting}");
    }

    static readonly MethodInfo Defrag = typeof(QPacketHandlerPRUDP).GetMethod("Defrag", Private)!;
    static IDictionary Buffers(QPacketHandlerPRUDP h) => (IDictionary)typeof(QPacketHandlerPRUDP).GetField("FragmentBuffers", Private)!.GetValue(h)!;
    static int Pending(QPacketHandlerPRUDP h) => Buffers(h).Values.Cast<IList>().Sum(x => x.Count);
    static string Text(QPacket p) => Encoding.ASCII.GetString(p.payload);
    const long Lifetime = 120000;
    static void Age(QPacketHandlerPRUDP h, long tick)
    {
        foreach (var b in Buffers(h).Values)
            if (b!.GetType().GetField("ReceivedAt", Private | BindingFlags.Public)?.GetValue(b) is IDictionary times)
                foreach (var p in times.Keys.Cast<object>().ToArray())
                    times[p] = tick;
    }
    static void ForceSweep(QPacketHandlerPRUDP h) => typeof(QPacketHandlerPRUDP).GetField("NextFragmentSweep", Private)?.SetValue(h, 0L);
    static void Sweep(QPacketHandlerPRUDP h, long now)
    {
        ForceSweep(h);
        typeof(QPacketHandlerPRUDP).GetMethod("ExpireFragmentBuffers", Private)?.Invoke(h, [now]);
    }
    static long Limit(string scope, string resource)
    {
        var name = "MaxFragment" + resource + "Per" + scope;
        return Convert.ToInt64(typeof(QPacketHandlerPRUDP).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()
            ?? (resource == "Packets" ? scope == "Owner" ? 4096L : 32768L : resource == "Bytes" ? scope == "Owner" ? 32L << 20 : 128L << 20 : scope == "Owner" ? 64L : 1024L));
    }
    static bool Accounting(QPacketHandlerPRUDP h)
    {
        var packets = typeof(QPacketHandlerPRUDP).GetField("FragmentPacketCount", Private);
        var bytes = typeof(QPacketHandlerPRUDP).GetField("FragmentPayloadBytes", Private);
        return packets == null && bytes == null || packets != null && bytes != null && (int)packets.GetValue(h)! == Pending(h)
            && (long)bytes.GetValue(h)! == Buffers(h).Values.Cast<IList>().SelectMany(x => x.Cast<QPacket>()).Sum(x => (long)x.payload.Length);
    }
    static QPacket Packet(QClient c, ushort seq, byte part, string body) => Packet(c, seq, part, Encoding.ASCII.GetBytes(body));
    static QPacket Packet(QClient c, ushort seq, byte part, byte[] body) => new()
    {
        type = QPacket.PACKETTYPE.DATA,
        flags = [QPacket.PACKETFLAG.FLAG_RELIABLE, QPacket.PACKETFLAG.FLAG_NEED_ACK],
        m_oSourceVPort = new(0x3f),
        m_oDestinationVPort = new(0x31),
        m_uiSignature = c.IDrecv,
        m_bySessionID = 1,
        uiSeqId = seq,
        m_byPartNumber = part,
        payload = body,
        usesCompression = false
    };
    sealed class Scalar
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
        public readonly UdpClient[] Receivers;
        public readonly QPacketHandlerPRUDP Handler;
        public readonly QClient[] Clients;
        public Fixture(int count)
        {
            Handler = new(socket, 2, 0, "fragment-hardening-check");
            Receivers = Enumerable.Range(0, count).Select(_ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))).ToArray();
            Clients = Receivers.Select((r, i) => { var c = Handler.NewQClient((IPEndPoint)r.Client.LocalEndPoint!); c.IDsend = (uint)(3000 + i); c.State = QClient.StateType.Active; return c; }).ToArray();
        }
        public bool Feed(QClient c, QPacket p) => (bool)Defrag.Invoke(Handler, [c, p])!;
        public void Dispose()
        {
            socket.Dispose();
            foreach (var r in Receivers)
                r.Dispose();
        }
    }
}
