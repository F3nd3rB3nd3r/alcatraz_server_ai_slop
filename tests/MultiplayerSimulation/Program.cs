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
var results = new List<Observation>();
foreach (var count in new[] { 2, 5, 8 })
{
    Check($"ack-isolation-{count}", true, () => AckIsolation(count, false));
    Check($"ack-distinct-sequence-control-{count}", false, () => AckIsolation(count, true));
}
Check("interleaved-fragment-ownership", true, InterleavedFragments);
Check("ordered-fragment-control", false, OrderedFragments);
Check("final-fragment-arrives-first", true, FinalFragmentFirst);
Check("notification-cache-recipient-isolation", true, NotificationOwners);
Check("notification-wire-recipient-isolation", true, NotificationWire);
Check("lost-ack-retry-control", false, RetryControl);
Check("expired-cache-late-send", true, ExpiredCache);
Check("duplicate-connect-registry", true, DuplicateConnect);
Check("cancel-party-membership", true, CancelParty);
Check("url-registration-order", true, UrlRegistration);
Check("same-ip-different-port-cleanup", true, () => EndpointCleanup(true));
Check("different-ip-cleanup-control", false, () => EndpointCleanup(false));
Check("unrelated-empty-session", true, EmptySession);
Check("disconnect-cleanup-grace-control", false, CleanupGrace);

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
await NetworkDiagnostics.StopAsync();
// Baseline mode checks the documented broken behavior and controls. Default mode
// is the future regression gate: every invariant must pass, regardless of baseline.
return results.Any(x => x.Error != null || (baseline ? x.InvariantPassed == x.ExpectedBaselineViolation : !x.InvariantPassed)) ? 1 : 0;

void Check(string name, bool expectedViolation, Func<(bool Passed, string Detail)> test)
{
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

static (bool, string) AckIsolation(int count, bool distinct)
{
    using var socket = new UdpClient(AddressFamily.InterNetwork);
    var h = Handler(socket);
    var clients = Enumerable.Range(0, count).Select(i => Client(h, 31000 + i)).ToArray();
    for (var i = 0; i < count; i++)
        Invoke(h, "CacheResponse", Packet(clients[i].IDrecv, 1), Packet((uint)(1000 + i), (ushort)(distinct ? i + 1 : 1)), clients[i].Endpoint);
    var ack = Packet(clients[0].IDrecv, 1);
    ack.flags = [QPacket.PACKETFLAG.FLAG_ACK];
    // Exercise the normal DATA ACK dispatch and wire parser, not just OnGotAck.
    h.ProcessPacket(ack.toBuffer(), clients[0].Endpoint);
    var remaining = Caches(h).Count;
    return (remaining == count - 1, $"After client 1 ACK: {remaining} caches remain; expected {count - 1}");
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
static (bool, string) NotificationWire()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiverA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiverB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var a = Client(h, ((IPEndPoint)receiverA.Client.LocalEndPoint!).Port);
    var b = Client(h, ((IPEndPoint)receiverB.Client.LocalEndPoint!).Port);
    a.IDsend = 2001;
    b.IDsend = 2002;
    a.SessionID = b.SessionID = 1;
    foreach (var c in new[] { a, b })
        RMC.SendRMCCall(h, c, RMCProtocolId.NATTraversalService, 2,
            new RMCPRequestDDL<StationURL>(new("prudp:/address=127.0.0.1;port=32001")));
    h.Update();
    var receivedA = Drain(receiverA).Select(x => new QPacket(x).m_uiSignature).ToArray();
    var receivedB = Drain(receiverB).Select(x => new QPacket(x).m_uiSignature).ToArray();
    return (receivedA.SequenceEqual([a.IDsend]) && receivedB.SequenceEqual([b.IDsend]),
        $"Actual loopback datagrams: A={receivedA.Length}, B={receivedB.Length}; A received packet addressed to B: {receivedA.Contains(b.IDsend)}");
}
static (bool, string) RetryControl()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var h = Handler(socket);
    var c = Client(h, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    Invoke(h, "CacheResponse", Packet(c.IDrecv, 1), Packet(2001, 1, 0, "reply"), c.Endpoint);
    h.Update();
    var initial = Drain(receiver);
    var cache = Caches(h)[0]!;
    cache.GetType().GetField("ResendTime")!.SetValue(cache, DateTime.UtcNow.AddSeconds(-1));
    h.Update();
    var retried = Drain(receiver);
    var ack = Packet(c.IDrecv, 1);
    ack.flags = [QPacket.PACKETFLAG.FLAG_ACK];
    h.ProcessPacket(ack.toBuffer(), c.Endpoint);
    h.Update();
    var afterAck = Drain(receiver);
    return (initial.Count == 1 && retried.Count == 1 && initial[0].SequenceEqual(retried[0]) && afterAck.Count == 0 && Caches(h).Count == 0,
        $"Initial/retried/post-ACK datagrams: {initial.Count}/{retried.Count}/{afterAck.Count}; retries are forced due without sleeping");
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
