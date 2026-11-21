using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using QNetZ;

static class RetryExpiryChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [
        ("due-expired",true),("expired-not-due-control",false),("valid-due-control",false),
        ("valid-not-due-control",false),("selected-before-expiry",true),
        ("direct-expired-retry",true),("null-request-control",false),
        ("mixed-recipients",true),("multi-packet-expired",true)
    ];
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static (bool, string) Run(string kind)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var other = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var handler = new QPacketHandlerPRUDP(socket, 2, 0, "expiry-check");
        object Add(UdpClient target, uint signature, string body)
        {
            var endpoint = (IPEndPoint)target.Client.LocalEndPoint!;
            QPacket Packet(uint sig) => new()
            {
                type = QPacket.PACKETTYPE.DATA,
                flags = [QPacket.PACKETFLAG.FLAG_RELIABLE],
                m_oSourceVPort = new(0x31),
                m_oDestinationVPort = new(0x3f),
                m_uiSignature = sig,
                m_bySessionID = 1,
                uiSeqId = 1,
                payload = Encoding.ASCII.GetBytes(body),
                usesCompression = false
            };
            handler.GetType().GetMethod("CacheResponse", Flags)!.Invoke(handler, [Packet(signature), Packet(signature + 100), endpoint]);
            return ((IList)handler.GetType().GetField("CachedResponses", Flags)!.GetValue(handler)!)[^1]!;
        }
        var cache = Add(receiver, 100, "first");
        void Set(object target, string field, object? value) => target.GetType().GetField(field)!.SetValue(target, value);
        Set(cache, "DropTime", DateTime.UtcNow.AddMinutes(1));
        Set(cache, "ResendTime", DateTime.UtcNow.AddSeconds(-1));
        var expected = 0;
        if (kind is "due-expired" or "expired-not-due-control" or "direct-expired-retry" or "mixed-recipients" or "multi-packet-expired")
            Set(cache, "DropTime", DateTime.UtcNow.AddSeconds(-1));
        if (kind is "expired-not-due-control" or "valid-not-due-control")
            Set(cache, "ResendTime", DateTime.UtcNow.AddMinutes(1));
        if (kind == "null-request-control")
            Set(cache, "SrcPacket", null);
        if (kind == "mixed-recipients")
        {
            var valid = Add(other, 200, "other");
            Set(valid, "DropTime", DateTime.UtcNow.AddMinutes(1));
            Set(valid, "ResendTime", DateTime.UtcNow.AddSeconds(-1));
        }
        if (kind == "valid-due-control")
            expected = 1;
        if (kind == "multi-packet-expired")
            Add(receiver, 100, "second");
        var log = QLog.LogFunction;
        var priority = QLog.MinPriority;
        var files = QLog.EnableFileLogging;
        bool expired = false;
        try
        {
            QLog.EnableFileLogging = false;
            if (kind == "selected-before-expiry")
            {
                QLog.MinPriority = 5;
                QLog.LogFunction = (p, message, color) =>
                {
                    if (message.Contains("Retry send ", StringComparison.Ordinal) && !expired)
                    {
                        expired = true;
                        Set(cache, "DropTime", DateTime.UtcNow.AddSeconds(-1));
                    }
                };
            }
            if (kind is "direct-expired-retry" or "multi-packet-expired")
                handler.GetType().GetMethod("RetrySend", Flags)!.Invoke(handler, [cache]);
            else
                handler.Update();
        }
        finally { QLog.LogFunction = log; QLog.MinPriority = priority; QLog.EnableFileLogging = files; }
        var a = NatProbeChecks.Drain(receiver);
        var b = NatProbeChecks.Drain(other);
        var expectedOther = kind == "mixed-recipients" ? 1 : 0;
        var remaining = ((IList)handler.GetType().GetField("CachedResponses", Flags)!.GetValue(handler)!).Count;
        bool schedule = kind != "valid-not-due-control" || remaining == 1;
        bool expiryForced = kind != "selected-before-expiry" || expired;
        if (!expiryForced)
            throw new InvalidOperationException("Expiry boundary hook did not execute");
        return (a.Length == expected && b.Length == expectedOther && schedule && expiryForced,
            $"{a.Length}/{expected} primary and {b.Length}/{expectedOther} other datagrams; expiry boundary forced {expiryForced}; remaining groups {remaining}");
    }
}
