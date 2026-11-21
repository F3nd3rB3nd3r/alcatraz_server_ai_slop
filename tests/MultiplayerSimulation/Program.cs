using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DSFServices;
using DSFServices.DDL.Models;
using QNetZ;
using QNetZ.DDL;
using QNetZ.Interfaces;

// Calls production implementations, including private transport paths via reflection.
// No database, server listener, real authentication, or game client is required.
QConfiguration.Instance = QConfiguration.MakeDevelopmentConfiguration("127.0.0.1");
QLog.LogFunction = null;
if (args.Contains("--verify-diagnostics")) return await DiagnosticsChecks.Run();
var filterIndex = Array.IndexOf(args, "--filter");
if (filterIndex >= 0 && (filterIndex + 1 >= args.Length || args[filterIndex + 1].StartsWith("--")))
    throw new ArgumentException("--filter requires comma-separated scenario name prefixes");
var filters = filterIndex < 0 ? Array.Empty<string>() : args[filterIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (filterIndex >= 0 && filters.Length == 0) throw new ArgumentException("Empty scenario filter");
var results = new List<Observation>();
foreach (var count in new[] { 2, 5, 8 })
{
    Check($"ack-isolation-{count}", true, () => AckIsolation(count, false));
    Check($"ack-distinct-sequence-control-{count}", false, () => AckIsolation(count, true));
}
Check("ack-wrong-session-control", false, () => AckGuard("session"));
Check("ack-wrong-sequence-control", false, () => AckGuard("sequence"));
Check("ack-wrong-stream", true, () => AckGuard("stream"));
Check("ack-unknown-connection-control", false, () => AckGuard("unknown"));
Check("ack-stale-response-signature", true, () => AckGuard("signature"));
Check("ack-other-recipient", true, () => AckGuard("recipient"));
Check("ack-duplicate-control", false, DuplicateAcks);
Check("ack-stream-isolation", true, StreamAcks);
Check("interleaved-fragment-ownership", true, InterleavedFragments);
Check("ordered-fragment-control", false, OrderedFragments);
Check("final-fragment-arrives-first", true, FinalFragmentFirst);
Check("notification-cache-recipient-isolation", true, NotificationOwners);
Check("notification-wire-recipient-isolation", true, () => NotificationWire(2));
Check("notification-wire-recipient-isolation-5", true, () => NotificationWire(5));
Check("notification-wire-recipient-isolation-8", true, () => NotificationWire(8));
Check("notification-same-recipient-control", false, SameRecipientNotifications);
Check("notification-same-port-different-address", true, SamePortRecipients);
Check("cache-replay-recipient-control", false, ReplayRecipient);
Check("lost-ack-retry-control", false, RetryControl);
Check("expired-cache-late-send", true, ExpiredCache);
Check("duplicate-connect-registry", true, DuplicateConnect);
Check("cancel-party-membership", true, CancelParty);
Check("url-registration-order", true, UrlRegistration);
Check("same-ip-different-port-cleanup", true, () => EndpointCleanup(true));
Check("different-ip-cleanup-control", false, () => EndpointCleanup(false));
Check("unrelated-empty-session", true, EmptySession);
Check("gathering-create-migrate-invite", true, () => CreatedGatheringInvite(true));
Check("gathering-create-invite-control", false, () => CreatedGatheringInvite(false));
Check("disconnect-cleanup-grace-control", false, CleanupGrace);
Check("hosted-callback-reentry-reliable-send", true, HostedCallbackChecks.ReliableReentry);
Check("hosted-callback-overlap-receive-owner", true, HostedCallbackChecks.ReceiveOwnership);
Check("hosted-callback-independent-services-control", false, HostedCallbackChecks.IndependentServices);
Check("hosted-callback-error-recovery-control", false, HostedCallbackChecks.ErrorRecovery);
Check("nat-probe-observed-endpoint-control", false, () => NatProbeLookup("observed"));
Check("nat-probe-alternate-address", true, () => NatProbeLookup("address"));
Check("nat-probe-alternate-port", true, () => NatProbeLookup("port"));
Check("nat-probe-alternate-then-observed-control", false, () => NatProbeLookup("both"));
Check("nat-probe-unknown-target-control", false, () => NatProbeLookup("unknown"));
foreach (var (kind, originalViolation) in NatProbeChecks.Cases)
    Check("nat-probe-" + kind, originalViolation, () => NatProbeChecks.Run(kind));
foreach (var (kind, originalViolation) in FragmentIsolationChecks.Cases)
    Check("fragment-" + kind, originalViolation, () => FragmentIsolationChecks.Run(kind));
foreach (var (kind, originalViolation) in ConnectAssociationChecks.Cases)
    Check("connect-" + kind, originalViolation, () => ConnectAssociationChecks.Run(kind));
foreach (var (kind, originalViolation) in CleanupIsolationChecks.Cases)
    Check("cleanup-" + kind, originalViolation, () => CleanupIsolationChecks.Run(kind));
foreach (var (kind, originalViolation) in RetryExpiryChecks.Cases)
    Check("expiry-" + kind, originalViolation, () => RetryExpiryChecks.Run(kind));
if (results.Count == 0) throw new ArgumentException("No scenarios matched --filter");

var baseline = args.Contains("--baseline");
var outputIndex = Array.IndexOf(args, "--output");
if (outputIndex >= 0 && outputIndex + 1 >= args.Length)
    throw new ArgumentException("--output requires a JSON path");
var report = new
{
    utc = DateTimeOffset.UtcNow,
    mode = baseline ? "baseline-characterization" : "correctness",
    scope = "Production backend methods with synthetic inputs; not Driver.exe or NAT emulation",
    observations = results
};
if (outputIndex >= 0)
{
    var output = Path.GetFullPath(args[outputIndex + 1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
foreach (var r in results)
    Console.WriteLine($"{(r.Error != null ? "ERROR" : r.InvariantPassed ? "PASS" : "VIOLATION"),-10} {r.Name}: {r.Detail}");
Console.WriteLine($"{results.Count} scenarios; {results.Count(x => !x.InvariantPassed && x.Error == null)} invariant violations; {results.Count(x => x.Error != null)} harness errors.");
if (baseline)
    foreach (var changed in results.Where(x => x.Error == null && x.InvariantPassed == x.ExpectedBaselineViolation))
        Console.WriteLine($"BASELINE CHANGED: {changed.Name} no longer matches the original source behavior.");
await NetworkDiagnostics.StopAsync();
// Baseline mode checks the documented broken behavior and controls. Default mode
// is the future regression gate: every invariant must pass, regardless of baseline.
return results.Any(x => x.Error != null || (baseline ? x.InvariantPassed == x.ExpectedBaselineViolation : !x.InvariantPassed)) ? 1 : 0;

void Check(string name, bool expectedViolation, Func<(bool Passed, string Detail)> test)
{
    if (filters.Length > 0 && !filters.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
        return;
    NetworkPlayers.Players.Clear();
    GameSessions.SessionList.Clear();
    PartySessions.GatheringList.Clear();
    try
    {
        var (passed, detail) = test();
        results.Add(new(name, passed, expectedViolation, detail, null));
    }
    catch (Exception ex)
    {
        var cause = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
        results.Add(new(name, false, expectedViolation, "Scenario could not complete", cause!.ToString()));
        Console.Error.WriteLine($"{name}: {cause}");
    }
}

static object? Invoke(object target, string method, params object[] values) =>
    target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, values);
static IList Caches(QPacketHandlerPRUDP handler) =>
    (IList)typeof(QPacketHandlerPRUDP).GetField("CachedResponses", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(handler)!;
static object Field(object target, string name) => target.GetType().GetField(name)!.GetValue(target)!;
static QPacket Packet(uint signature, ushort seq, byte part = 0, string text = "") => new()
{
    type = QPacket.PACKETTYPE.DATA,
    flags = [QPacket.PACKETFLAG.FLAG_RELIABLE],
    m_oSourceVPort = new(0x31),
    m_oDestinationVPort = new(0x3f),
    m_uiSignature = signature,
    m_bySessionID = 1,
    uiSeqId = seq,
    m_byPartNumber = part,
    payload = Encoding.ASCII.GetBytes(text),
    usesCompression = false
};
static QPacketHandlerPRUDP Handler(UdpClient socket) => new(socket, 2, 0, "simulation");
static QPacket ClientAck(QClient client, QPacket response)
{
    var ack = Packet(client.IDrecv, response.uiSeqId);
    ack.flags = [QPacket.PACKETFLAG.FLAG_ACK];
    ack.m_bySessionID = response.m_bySessionID;
    ack.m_oSourceVPort = response.m_oDestinationVPort;
    ack.m_oDestinationVPort = response.m_oSourceVPort;
    return ack;
}
static QClient Client(QPacketHandlerPRUDP h, int port) => h.NewQClient(new(IPAddress.Loopback, port));
static PlayerInfo Player(uint pid, int port, string ip = "127.0.0.1")
{
    var c = new QClient(pid + 100, new(IPAddress.Parse(ip), port)) { State = QClient.StateType.Active };
    var p = NetworkPlayers.CreatePlayerInfo(c);
    p.PID = pid;
    p.Name = $"synthetic-{pid}";
    c.PlayerInfo = p;
    return p;
}
static RMCServiceBase MatchMaking(QPacketHandlerPRUDP h, QClient c)
{
    var type = typeof(DSFPlayerGameData).Assembly.GetType("DSFServices.Services.MatchMakingService")!;
    var service = (RMCServiceBase)Activator.CreateInstance(type, nonPublic: true)!;
    service.Context = new(new RMCPacket(), h, c, Packet(c.IDrecv, 1));
    return service;
}

static (bool, string) NatProbeLookup(string kind)
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var caller = Client(h, 31001);
    var target = Client(h, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    foreach (var c in new[] { caller, target })
    {
        c.State = QClient.StateType.Active;
        c.IDsend = c.IDrecv + 100;
        c.SessionID = 1;
        c.PlayerInfo = NetworkPlayers.CreatePlayerInfo(c);
        c.PlayerInfo.PID = ReferenceEquals(c, caller) ? 1u : 2u;
    }
    StationURL Url(bool alternate) => new("prudp", alternate && kind != "port" ? "192.0.2.20" : target.Endpoint.Address.ToString(),
        new Dictionary<string, int>
        {
            ["port"] = alternate && kind == "port" ? (target.Endpoint.Port == 65535 ? 65534 : target.Endpoint.Port + 1) : target.Endpoint.Port,
            ["RVCID"] = (int)(kind == "unknown" ? target.PlayerInfo.RVCID + 999 : target.PlayerInfo.RVCID),
            ["type"] = 3
        });
    var service = new RDVServices.Services.NATTraversalService
    {
        Context = new(new RMCPacket(), h, caller, Packet(caller.IDrecv, 1))
    };
    var urls = kind == "both" ? new[] { Url(true), Url(false) } : new[] { Url(kind != "observed") };
    var result = service.RequestProbeInitiation(urls);
    h.Update();
    var received = Drain(receiver).Select(x => new QPacket(x)).ToArray();
    var expected = kind == "unknown" ? 0 : 1;
    var valid = received.All(p =>
    {
        var rmc = new RMCPacket(p);
        using var body = new MemoryStream(p.payload);
        body.Position = rmc._afterProtocolOffset;
        var station = new StationURL();
        station.Read(body);
        return p.m_uiSignature == target.IDsend && rmc.isRequest && rmc.proto == RMCProtocolId.NATTraversalService
            && rmc.methodID == 2 && station.Address == caller.Endpoint.Address.ToString()
            && station["port"] == caller.Endpoint.Port && station["RVCID"] == (int)caller.PlayerInfo.RVCID;
    });
    return (result.Error == 0 && received.Length == expected && valid,
        $"Target {kind}: {received.Length} InitiateProbe datagrams, expected {expected}; caller URL/header valid: {valid}; request error: {result.Error}");
}

static (bool, string) AckIsolation(int count, bool distinct)
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var clients = Enumerable.Range(0, count).Select(i => Client(h, 31000 + i)).ToArray();
    for (var i = 0; i < count; i++)
    {
        clients[i].IDsend = (uint)(1000 + i);
        Invoke(h, "CacheResponse", Packet(clients[i].IDrecv, 1), Packet(clients[i].IDsend, (ushort)(distinct ? i + 1 : 1)), clients[i].Endpoint);
    }
    var ack = ClientAck(clients[0], Packet(clients[0].IDsend, 1));
    // Exercise the normal DATA ACK dispatch and wire parser, not just OnGotAck.
    h.ProcessPacket(ack.toBuffer(), clients[0].Endpoint);
    var remaining = Caches(h).Count;
    return (remaining == count - 1, $"After client 1 ACK: {remaining} caches remain; expected {count - 1}");
}
static (bool, string) AckGuard(string kind)
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = Client(h, 31001);
    var b = Client(h, 31002);
    a.IDsend = 2001;
    b.IDsend = 2002;
    var response = Packet(a.IDsend, 5);
    Invoke(h, "CacheResponse", Packet(a.IDrecv, 9), response, a.Endpoint);
    var ack = ClientAck(kind == "recipient" ? b : a, response);
    switch (kind)
    {
        case "session":
            ack.m_bySessionID++;
            break;
        case "sequence":
            ack.uiSeqId++;
            break;
        case "stream":
            ack.m_oSourceVPort = new(0x3e);
            break;
        case "unknown":
            ack.m_uiSignature = 0xFFFFFFFF;
            break;
        case "signature":
            a.IDsend++;
            break; // Cached response belongs to the previous peer signature.
    }
    h.ProcessPacket(ack.toBuffer(), kind == "recipient" ? b.Endpoint : a.Endpoint);
    return (Caches(h).Count == 1, $"Nonmatching ACK ({kind}) leaves {Caches(h).Count} response groups; expected 1");
}
static (bool, string) DuplicateAcks()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var c = Client(h, 31001);
    c.IDsend = 2001;
    var first = Packet(c.IDsend, 1);
    var second = Packet(c.IDsend, 2);
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 9), first, c.Endpoint);
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 9), second, c.Endpoint);
    var ack = ClientAck(c, first).toBuffer();
    h.ProcessPacket(ack, c.Endpoint);
    h.ProcessPacket(ack, c.Endpoint);
    var pending = Caches(h).Cast<object>().SelectMany(x => ((IList)Field(x, "ResponseList")).Cast<object>()).ToArray();
    var keptSecond = pending.Length == 1 && ((QPacket)Field(pending[0], "Packet")).uiSeqId == 2;
    h.ProcessPacket(ClientAck(c, second).toBuffer(), c.Endpoint);
    return (keptSecond && Caches(h).Count == 0, $"Duplicate ACK preserves the next sequence: {keptSecond}; final ACK leaves {Caches(h).Count} groups");
}
static (bool, string) StreamAcks()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var c = Client(h, 31001);
    c.IDsend = 2001;
    var a = Packet(c.IDsend, 1);
    var b = Packet(c.IDsend, 1);
    b.m_oDestinationVPort = new(0x3e);
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 9), a, c.Endpoint);
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 9), b, c.Endpoint);
    h.ProcessPacket(ClientAck(c, a).toBuffer(), c.Endpoint);
    var pending = Caches(h).Cast<object>().SelectMany(x => ((IList)Field(x, "ResponseList")).Cast<object>()).ToArray();
    var isolated = pending.Length == 1 && ((QPacket)Field(pending[0], "Packet")).m_oDestinationVPort.toByte() == 0x3e;
    h.ProcessPacket(ClientAck(c, b).toBuffer(), c.Endpoint);
    return (isolated && Caches(h).Count == 0, $"Equal session/sequence on different streams isolated: {isolated}; final stream ACK leaves {Caches(h).Count} groups");
}
static (bool, string) InterleavedFragments()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = Client(h, 31001);
    var b = Client(h, 31002);
    Invoke(h, "Defrag", a, Packet(a.IDrecv, 1, 1, "A"));
    var packet = Packet(b.IDrecv, 2, 0, "B");
    var delivered = (bool)Invoke(h, "Defrag", b, packet)!;
    var value = Encoding.ASCII.GetString(packet.payload);
    return (delivered && value == "B", $"Client B assembled '{value}'; expected only 'B'");
}
static (bool, string) OrderedFragments()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = Client(h, 31001);
    var waiting = !(bool)Invoke(h, "Defrag", a, Packet(a.IDrecv, 1, 1, "A"))!;
    var packet = Packet(a.IDrecv, 2, 0, "B");
    var delivered = (bool)Invoke(h, "Defrag", a, packet)!;
    return (waiting && delivered && Encoding.ASCII.GetString(packet.payload) == "AB", "Ordered two-fragment message assembles as AB");
}
static (bool, string) FinalFragmentFirst()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = Client(h, 31001);
    // Warm up sequence 1, then deliberately omit sequence 2 and deliver final 3.
    Invoke(h, "Defrag", a, Packet(a.IDrecv, 1, 0, "previous"));
    var delivered = (bool)Invoke(h, "Defrag", a, Packet(a.IDrecv, 3, 0, "tail"))!;
    return (!delivered, $"Final sequence 3 delivered with missing sequence 2: {delivered}; protocol ordering needs client validation");
}
static (bool, string) NotificationOwners()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = Client(h, 31001);
    var b = Client(h, 31002);
    a.IDsend = 2001;
    b.IDsend = 2002;
    a.SessionID = b.SessionID = 1;
    RMC.SendRMCCall(h, a, RMCProtocolId.NATTraversalService, 2, new RMCPRequestDDL<StationURL>(new("prudp:/address=127.0.0.1;port=32001")));
    RMC.SendRMCCall(h, b, RMCProtocolId.NATTraversalService, 2, new RMCPRequestDDL<StationURL>(new("prudp:/address=127.0.0.1;port=32002")));
    var owners = Caches(h).Cast<object>().Select(x => (IPEndPoint)Field(x, "Endpoint")).ToArray();
    return (owners.Length == 2 && owners.Contains(a.Endpoint) && owners.Contains(b.Endpoint), $"Two recipients produced {owners.Length} cache groups; recipient B has a group: {owners.Contains(b.Endpoint)}");
}
static (bool, string) DuplicateConnect()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var c = Client(h, 31001);
    var player = Player(1, 32001);
    using var plain = new MemoryStream();
    Helper.WriteU32(plain, player.PID);
    Helper.WriteU32(plain, 99);
    Helper.WriteU32(plain, 123);
    var encrypted = Helper.Encrypt(Constants.SessionKey, plain.ToArray());
    using var payload = new MemoryStream();
    Helper.WriteU32(payload, 0); // Synthetic empty ticket: current ticket parser is a stub.
    Helper.WriteU32(payload, (uint)encrypted.Length + 16);
    payload.Write(encrypted);
    var packet = Packet(c.IDrecv, 1);
    packet.payload = payload.ToArray();
    var first = (byte[])Invoke(h, "MakeConnectPayload", c, packet)!;
    if (first.Length != 8 || !NetworkPlayers.Players.Contains(player))
        throw new Exception("Initial association did not complete");
    Invoke(h, "MakeConnectPayload", c, packet);
    return (NetworkPlayers.Players.Contains(player), $"After repeated CONNECT: global registry {NetworkPlayers.Players.Count}, connection still references player: {ReferenceEquals(c.PlayerInfo, player)}");
}
static List<byte[]> Drain(UdpClient receiver)
{
    var packets = new List<byte[]>();
    while (packets.Count < 16 && receiver.Client.Poll(100_000, SelectMode.SelectRead))
    {
        IPEndPoint remote = new(IPAddress.Any, 0);
        packets.Add(receiver.Receive(ref remote));
    }
    return packets;
}
static (bool, string) NotificationWire(int count)
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var receivers = new List<UdpClient>();
    try
    {
        var h = Handler(socket);
        var clients = Enumerable.Range(0, count).Select(i =>
        {
            var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            receivers.Add(receiver);
            var client = Client(h, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
            client.IDsend = (uint)(2001 + i);
            client.SessionID = 1;
            return client;
        }).ToArray();
        foreach (var c in clients)
            RMC.SendRMCCall(h, c, RMCProtocolId.NATTraversalService, 2,
                new RMCPRequestDDL<StationURL>(new("prudp:/address=127.0.0.1;port=32001")));
        h.Update();
        var received = receivers.Select(x => Drain(x).Select(data => new QPacket(data).m_uiSignature).ToArray()).ToArray();
        return (received.Select((packets, i) => packets.SequenceEqual([clients[i].IDsend])).All(x => x),
            $"Actual datagrams per receiver: {string.Join('/', received.Select(x => x.Length))}; each of {count} recipients must get exactly its own notification");
    }
    finally { foreach (var receiver in receivers) receiver.Dispose(); }
}
static (bool, string) SameRecipientNotifications()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var c = Client(h, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    c.IDsend = 2001;
    c.SessionID = 1;
    for (var i = 0; i < 2; i++)
        RMC.SendRMCCall(h, c, RMCProtocolId.NATTraversalService, 2,
            new RMCPRequestDDL<StationURL>(new("prudp:/address=127.0.0.1;port=32001")));
    h.Update();
    var received = Drain(receiver).Select(x => new QPacket(x)).ToArray();
    return (received.Length == 2 && received.All(x => x.m_uiSignature == c.IDsend) && received.Select(x => x.uiSeqId).Distinct().Count() == 2,
        $"Same recipient received {received.Length} notifications with {received.Select(x => x.uiSeqId).Distinct().Count()} distinct sequences");
}
static (bool, string) SamePortRecipients()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = h.NewQClient(new(IPAddress.Parse("127.0.0.1"), 31001));
    var b = h.NewQClient(new(IPAddress.Parse("127.0.0.2"), 31001));
    a.IDsend = 2001;
    b.IDsend = 2002;
    a.SessionID = b.SessionID = 1;
    foreach (var c in new[] { a, b })
        RMC.SendRMCCall(h, c, RMCProtocolId.NATTraversalService, 2,
            new RMCPRequestDDL<StationURL>(new("prudp:/address=127.0.0.1;port=32001")));
    return (Caches(h).Count == 2, $"Same port on two addresses produced {Caches(h).Count} recipient cache groups; expected 2");
}
static (bool, string) RetryControl()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var c = Client(h, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    c.IDsend = 2001;
    var response = Packet(c.IDsend, 1, 0, "reply");
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 1), response, c.Endpoint);
    h.Update();
    var initial = Drain(receiver);
    var cache = Caches(h)[0]!;
    cache.GetType().GetField("ResendTime")!.SetValue(cache, DateTime.UtcNow.AddSeconds(-1));
    h.Update();
    var retried = Drain(receiver);
    var ack = ClientAck(c, response);
    h.ProcessPacket(ack.toBuffer(), c.Endpoint);
    h.Update();
    var afterAck = Drain(receiver);
    return (initial.Count == 1 && retried.Count == 1 && initial[0].SequenceEqual(retried[0]) && afterAck.Count == 0 && Caches(h).Count == 0,
        $"Initial/retried/post-ACK datagrams: {initial.Count}/{retried.Count}/{afterAck.Count}; retries are forced due without sleeping");
}
static (bool, string) ReplayRecipient()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiverA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiverB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var a = Client(h, ((IPEndPoint)receiverA.Client.LocalEndPoint!).Port);
    var b = Client(h, ((IPEndPoint)receiverB.Client.LocalEndPoint!).Port);
    a.IDsend = 2001;
    b.IDsend = 2002;
    var requestA = Packet(a.IDrecv, 9, 0, "synthetic request");
    requestA.m_oSourceVPort = new(0x3f);
    requestA.m_oDestinationVPort = new(0x31);
    var requestB = Packet(b.IDrecv, 9, 0, "synthetic request");
    requestB.m_oSourceVPort = new(0x3f);
    requestB.m_oDestinationVPort = new(0x31);
    var wireA = requestA.toBuffer();
    requestB.toBuffer();
    Invoke(h, "CacheResponse", requestA, Packet(a.IDsend, 1, 0, "reply A"), a.Endpoint);
    Invoke(h, "CacheResponse", requestB, Packet(b.IDsend, 1, 0, "reply B"), b.Endpoint);
    h.Update();
    var firstA = Drain(receiverA);
    var firstB = Drain(receiverB);
    h.ProcessPacket(wireA, a.Endpoint);
    var replayA = Drain(receiverA).Select(x => new QPacket(x)).ToArray();
    var replayB = Drain(receiverB);
    return (firstA.Count == 1 && firstB.Count == 1 && replayA.Length == 2 && replayB.Count == 0
            && replayA.Count(x => x.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK)) == 1
            && replayA.All(x => x.m_uiSignature == a.IDsend) && Caches(h).Count == 2,
        $"Repeated A request: {replayA.Length} A datagrams (ACK + cached reply), {replayB.Count} B datagrams, {Caches(h).Count} pending groups");
}
static (bool, string) ExpiredCache()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var c = Client(h, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 1), Packet(2001, 1, 0, "reply"), c.Endpoint);
    var cache = Caches(h)[0]!;
    cache.GetType().GetField("DropTime")!.SetValue(cache, DateTime.UtcNow.AddSeconds(-1));
    cache.GetType().GetField("ResendTime")!.SetValue(cache, DateTime.UtcNow.AddSeconds(-1));
    h.Update();
    var received = Drain(receiver);
    return (received.Count == 0, $"Datagrams sent after cache expiry: {received.Count}; remaining cache groups: {Caches(h).Count}");
}
static (bool, string) CancelParty()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var p = Player(1, 31001);
    var party = new PartySessionGathering(new()
    {
        m_idMyself = 1
    });
    PartySessions.GatheringList.Add(party);
    PartySessions.UpdateGatheringParticipation(p, 1);
    Invoke(MatchMaking(h, p.Client), "CancelParticipation", 1u, "");
    return (!party.Participants.Contains(p.PID), $"PID remains in party after cancel: {party.Participants.Contains(p.PID)}");
}
static (bool, string) UrlRegistration()
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var a = Player(1, 31001);
    var b = Player(2, 31002);
    var party = new PartySessionGathering(new()
    {
        m_idMyself = 1
    });
    PartySessions.GatheringList.Add(party);
    Invoke(MatchMaking(h, a.Client), "RegisterLocalURLs", 1u, new[] { a.Url });
    Invoke(MatchMaking(h, b.Client), "RegisterLocalURLs", 1u, new[] { b.Url });
    return (party.Urls.Count == 2, $"Two per-player registrations leave {party.Urls.Count} URL; last caller port {party.Urls.Single()["port"]}. Client submission semantics unverified");
}
static (bool, string) EndpointCleanup(bool sameIp)
{
    var a = Player(1, 31001);
    var b = Player(2, 31002, sameIp ? "127.0.0.1" : "127.0.0.2");
    var session = new GameSessionData { Id = 1, TypeID = 1, HostPID = b.PID };
    session.PublicParticipants.UnionWith([a.PID, b.PID]);
    session.HostURLs.Add(b.Url);
    GameSessions.SessionList.Add(session);
    GameSessions.UpdateSessionParticipation(a, null!, false);
    return (session.HostURLs.Count == 1 && session.PublicParticipants.SetEquals([b.PID]), $"Surviving host URLs: {session.HostURLs.Count}; expected 1; same public IP: {sameIp}");
}
static (bool, string) EmptySession()
{
    var p = Player(1, 31001);
    var pending = new GameSessionData { Id = 1, TypeID = 1 };
    var joined = new GameSessionData { Id = 2, TypeID = 1 };
    GameSessions.SessionList.AddRange([pending, joined]);
    GameSessions.UpdateSessionParticipation(p, new()
    {
        m_sessionID = 2,
        m_typeID = 1
    }, false);
    return (GameSessions.SessionList.Contains(pending), $"Unrelated newly created session survives another join: {GameSessions.SessionList.Contains(pending)}");
}
static (bool, string) CreatedGatheringInvite(bool migrateOtherPlayer)
{
    // Reproduce the observed create -> another player's migration -> participate
    // -> invite sequence, without changing the implementation or requiring a game.
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiverA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiverB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var a = Player(1001, ((IPEndPoint)receiverA.Client.LocalEndPoint!).Port);
    var b = Player(1002, ((IPEndPoint)receiverB.Client.LocalEndPoint!).Port);
    h.Clients.AddRange([a.Client, b.Client]);
    var old = new PartySessionGathering(new()
    {
        m_idMyself = 100,
        m_pidOwner = a.PID,
        m_pidHost = a.PID
    });
    old.Participants.Add(a.PID);
    var other = new PartySessionGathering(new()
    {
        m_idMyself = 101,
        m_pidOwner = b.PID,
        m_pidHost = b.PID
    });
    other.Participants.Add(b.PID);
    PartySessions.GatheringList.AddRange([old, other]);
    a.GameData().CurrentGatheringId = 100;
    b.GameData().CurrentGatheringId = 101;
    var match = MatchMaking(h, a.Client);
    var created = (RMCResult)Invoke(match, "RegisterGathering", new AnyData<HermesPartySession>(new HermesPartySession()))!;
    var pendingId = BitConverter.ToUInt32(created.Response.ToBuffer());
    if (migrateOtherPlayer)
    {
        var type = typeof(DSFPlayerGameData).Assembly.GetType("DSFServices.Services.PartyService")!;
        var party = (RMCServiceBase)Activator.CreateInstance(type, nonPublic: true)!;
        party.Context = new(new RMCPacket(), h, b.Client, Packet(b.Client.IDrecv, 1));
        // The captured late request names the former shared lobby, while B
        // already belongs to its new lobby. Keep that same ordering here.
        Invoke(party, "MigrateTo", b.PID, 100u, 101u);
    }
    var participated = (RMCResult)Invoke(match, "Participate", pendingId, "synthetic")!;
    var invited = (RMCResult)Invoke(match, "Invite", pendingId, new List<uint> { b.PID }, "synthetic")!;
    h.Update();
    var notices = NatProbeChecks.Drain(receiverB);
    var exists = PartySessions.GatheringList.Any(g => g.Session.m_idMyself == pendingId && g.Participants.Contains(a.PID));
    var accepted = participated.Response.ToBuffer().SequenceEqual(new byte[] { 1 });
    var sent = invited.Response.ToBuffer().SequenceEqual(new byte[] { 1 });
    return (exists && accepted && sent && notices.Length == 1,
        $"Pending gathering retained/joined {exists}; Participate result {accepted}; Invite result {sent}; recipient datagrams {notices.Length}/1; unrelated migration {migrateOtherPlayer}");
}

static (bool, string) CleanupGrace()
{
    var p = Player(1, 31001);
    p.Client.State = QClient.StateType.Dropped;
    NetworkPlayers.DropPlayers();
    var retained = NetworkPlayers.Players.Contains(p);
    p.Client.LastPacketTime = DateTime.UtcNow.AddSeconds(-Constants.ClientTimeoutSeconds - 1);
    NetworkPlayers.DropPlayers();
    return (retained && !NetworkPlayers.Players.Contains(p) && p.Client.PlayerInfo == null, "Dropped player retained before threshold, removed after threshold; no sleeping required");
}

record Observation(string Name, bool InvariantPassed, bool ExpectedBaselineViolation, string Detail, string? Error);
