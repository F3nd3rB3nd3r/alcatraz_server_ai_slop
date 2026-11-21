using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DSFServices;
using QNetZ;

static class DiagnosticsChecks
{
    public static async Task<int> Run()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "diagnostics-check-" + Guid.NewGuid().ToString("N")));
        var failures = new List<string>();
        void Assert(bool condition, string name)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
            if (!condition)
                failures.Add(name);
        }

        var evaluated = false;
        NetworkDiagnostics.Record("disabled", () => { evaluated = true; throw new Exception(); });
        Assert(!evaluated && !NetworkDiagnostics.Enabled, "disabled diagnostics never evaluate snapshots");

        var directory = Path.Combine(root, "events");
        NetworkDiagnostics.Start(directory);
        ServiceFactoryDSF.RegisterDSFServices();
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var h = new QPacketHandlerPRUDP(socket, 2, 0, "diagnostic-check");
        var client = h.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
        const string secret = "AUTHENTICATION_SENTINEL_DO_NOT_LOG";
        var packet = new QPacket
        {
            type = QPacket.PACKETTYPE.DATA,
            flags = [QPacket.PACKETFLAG.FLAG_RELIABLE],
            m_oSourceVPort = new(0x31),
            m_oDestinationVPort = new(0x3f),
            m_uiSignature = client.IDrecv,
            m_bySessionID = 1,
            uiSeqId = 1,
            payload = Encoding.UTF8.GetBytes(secret),
            usesCompression = false
        };
        h.Trace("privacy.probe", client, packet);
        NetworkDiagnostics.Record("snapshot.error", () => throw new InvalidOperationException(secret));
        NetworkDiagnostics.Record("after.snapshot.error", () => new { ok = true });
        var rmc = new RMCPacket
        {
            proto = RMCProtocolId.NATTraversalService,
            methodID = 4,
            callID = 1,
            isRequest = true,
            request = new RMCPRequestDDL<object>(new { cid = 42u, result = false })
        };
        packet.payload = rmc.ToBuffer();
        h.ProcessPacket(packet.toBuffer(), client.Endpoint);
        h.Update();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 32; i++)
                NetworkDiagnostics.Record("concurrent.writer", () => new { worker, item = i });
        })));
        // Large events must be counted and dropped, not produce unbounded files.
        NetworkDiagnostics.Record("oversize", () => new { padding = new string('x', 40000) });
        await NetworkDiagnostics.StopAsync();
        var lines = Directory.GetFiles(directory, "*.jsonl").SelectMany(File.ReadAllLines).ToArray();
        var records = lines.Select(x => JsonDocument.Parse(x)).ToArray();
        bool Has(string name) => records.Any(x => x.RootElement.GetProperty("event").GetString() == name);
        Assert(lines.All(x => !x.Contains(secret)), "packet payload and exception message excluded");
        Assert(Has("udp.receive") && Has("packet.receive") && Has("udp.send.complete"), "wire receive and actual send metadata recorded");
        Assert(Has("rmc.request.begin") && Has("rmc.request.returned") && Has("nat.result"), "RMC execution and NAT result correlated");
        Assert(Has("rmc.unimplemented"), "unimplemented service path explicit");
        Assert(Has("after.snapshot.error"), "snapshot exception contained");
        var last = records.Single(x => x.RootElement.GetProperty("event").GetString() == "diagnostics.stopped").RootElement;
        Assert(last.GetProperty("dropped").GetInt64() >= 1 && last.GetProperty("failures").GetInt64() >= 1, "oversize drop and snapshot failure counted");
        var events = records.Where(x => x.RootElement.TryGetProperty("number", out _)).Select(x => x.RootElement).ToArray();
        Assert(events.All(x => DateTimeOffset.Parse(x.GetProperty("utc").GetString()!).Offset == TimeSpan.Zero && x.GetProperty("tick_frequency").GetInt64() > 0), "UTC and monotonic clocks present");
        Assert(events.Select(x => x.GetProperty("number").GetInt64()).Distinct().Count() == events.Length, "event numbers unique");
        foreach (var record in records)
            record.Dispose();

        var rotation = Path.Combine(root, "rotation");
        NetworkDiagnostics.Start(rotation, maxFileBytes: 1024, retainedFiles: 2);
        for (var i = 0; i < 50; i++)
            NetworkDiagnostics.Record("rotation", () => new { index = i, padding = new string('x', 200) });
        await NetworkDiagnostics.StopAsync();
        var files = Directory.GetFiles(rotation, "*.jsonl").Select(x => new FileInfo(x)).ToArray();
        Assert(files.Length <= 2 && files.All(x => x.Length <= 1024), "rotation respects file count and byte limits");
        Assert(files.SelectMany(x => File.ReadAllLines(x.FullName)).All(x => { using var doc = JsonDocument.Parse(x); return doc.RootElement.ValueKind == JsonValueKind.Object; }), "rotated JSONL records complete");

        var occupied = Path.Combine(root, "occupied");
        File.WriteAllText(occupied, "Synthetic file preventing directory creation");
        NetworkDiagnostics.Start(occupied);
        NetworkDiagnostics.Record("writer.failure", () => new { test = true });
        await NetworkDiagnostics.StopAsync();
        Assert(!NetworkDiagnostics.Enabled, "writer I/O failure contained and shutdown completes");
        Console.WriteLine($"Diagnostics checks: {failures.Count} failures. Synthetic artifacts: {root}");
        return failures.Count == 0 ? 0 : 1;
    }
}
