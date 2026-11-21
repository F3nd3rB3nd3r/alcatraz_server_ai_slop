using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DSFServices;
using QNetZ;
using QNetZ.DDL;

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
        var malformedFrame = new QPacket
        {
            type = QPacket.PACKETTYPE.DATA,
            flags = [QPacket.PACKETFLAG.FLAG_RELIABLE, QPacket.PACKETFLAG.FLAG_NEED_ACK],
            m_oSourceVPort = new(0x3f),
            m_oDestinationVPort = new(0x31),
            m_uiSignature = client.IDrecv,
            m_bySessionID = 1,
            uiSeqId = 99,
            payload = Encoding.UTF8.GetBytes(secret),
            usesCompression = false
        };
        h.ProcessPacket(malformedFrame.toBuffer(), client.Endpoint);
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
        Assert(HostedCallbackChecks.ReceiveOwnership().Item1, "hosted callback retains receive ownership with diagnostics enabled");
        Assert(HostedCallbackChecks.ReliableReentry().Item1, "hosted callback prevents duplicate selection with diagnostics enabled");
        var natChecks = new[] { "same-ip-peers", "ambiguous-rvcid-control", "fallback-with-exact-duplicates-control",
            "repeated-alternates", "endpoint-rvcid-conflict-control", "malformed-then-observed", "unidentified-caller-control" }
            .Select(NatProbeChecks.Run).ToArray();
        Assert(natChecks.All(x => x.Item1), "NAT fallback, rejection and exact-route controls pass with diagnostics enabled");
        var malformedNat = new RDVServices.Services.NATTraversalService { Context = new(new RMCPacket(), h, client, packet) };
        malformedNat.RequestProbeInitiation([new StationURL("prudp", secret, new Dictionary<string, int> { ["port"] = 3074 })]);
        var fragmentChecks = new[] { "session-isolation", "ack-recipient-isolation", "dropped-owner-cleanup", "inactive-owner-cleanup" }
            .Select(kind => FragmentIsolationChecks.Run(kind, secret)).Append(FragmentIsolationChecks.Run("rmc-dispatch-2")).ToArray();
        Assert(fragmentChecks.All(x => x.Item1), "fragment isolation, transport cleanup and wire dispatch pass with diagnostics enabled");
        var connectChecks = ConnectAssociationChecks.Cases.Select(item =>
        {
            NetworkPlayers.Players.Clear();
            GameSessions.SessionList.Clear();
            PartySessions.GatheringList.Clear();
            return ConnectAssociationChecks.Run(item.Kind);
        }).ToArray();
        Assert(connectChecks.All(x => x.Item1), "CONNECT retries, rebinds and account replacement pass with diagnostics enabled");
        var cleanupChecks = CleanupIsolationChecks.Cases.Select(item =>
        {
            NetworkPlayers.Players.Clear();
            GameSessions.SessionList.Clear();
            PartySessions.GatheringList.Clear();
            return CleanupIsolationChecks.Run(item.Kind);
        }).ToArray();
        Assert(cleanupChecks.All(x => x.Item1), "lobby/event/endpoint cleanup and owner disconnect pass with diagnostics enabled");
        Assert(RetryExpiryChecks.Cases.Select(item => RetryExpiryChecks.Run(item.Kind)).All(x => x.Item1),
            "retry expiry, live deadline boundary and valid-send controls pass with diagnostics enabled");
        Assert(ProtocolContractChecks.Cases.Select(item =>
        {
            NetworkPlayers.Players.Clear();
            GameSessions.SessionList.Clear();
            PartySessions.GatheringList.Clear();
            return ProtocolContractChecks.Run(item.Kind);
        }).All(x => x.Item1), "host URL, late cancellation and multipart-integrity controls pass with diagnostics enabled");
        Assert(NotificationFirstSendChecks.Cases.Select(item => NotificationFirstSendChecks.Run(item.Kind)).All(x => x.Item1),
            "independent first sends, fragmentation and unchanged old retry schedules pass with diagnostics enabled");
        Assert(TransportEdgeChecks.Cases.Select(item => TransportEdgeChecks.Run(item.Kind)).All(x => x.Item1),
            "wrap, overlapping messages, all arrival permutations and selective retry pass with diagnostics enabled");
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
        Assert(lines.All(x => !x.Contains(secret)), "packet payload, exception message and malformed target text excluded");
        Assert(Has("udp.receive") && Has("packet.receive") && Has("udp.send.complete"), "wire receive and actual send metadata recorded");
        Assert(Has("rmc.request.begin") && Has("rmc.request.returned") && Has("nat.result"), "RMC execution and NAT result correlated");
        Assert(Has("rmc.unimplemented"), "unimplemented service path explicit");
        var skipped = records.Where(x => x.RootElement.GetProperty("event").GetString() == "service.callback.skipped").ToArray();
        Assert(skipped.Length == 9 && skipped.All(x => x.RootElement.GetProperty("data").GetProperty("details").GetProperty("reason").GetString() == "busy")
            && !Has("service.callback.overlap"), "nine prevented callback entries recorded as busy skips, with no executing overlap");
        var lookups = records.Where(x => x.RootElement.GetProperty("event").GetString() == "nat.probe.lookup")
            .Select(x => x.RootElement.GetProperty("data").GetProperty("details")).ToArray();
        Assert(lookups.Any(x => x.GetProperty("resolution").GetString() == "rvcid" && x.GetProperty("probe_queued").GetBoolean())
            && lookups.Any(x => x.GetProperty("reason").GetString() == "ambiguous_rvcid" && x.GetProperty("eligible_candidates").GetInt32() == 2 && !x.GetProperty("probe_queued").GetBoolean())
            && lookups.Any(x => x.GetProperty("resolution").GetString() == "endpoint" && x.GetProperty("endpoint_rvcid_conflict").ValueKind == JsonValueKind.True),
            "NAT fallback selection, ambiguity and endpoint/ID disagreement recorded");
        Assert(new[] { "endpoint_available", "duplicate_target" }.All(reason => lookups.Any(x => x.GetProperty("suppression").GetString() == reason && !x.GetProperty("probe_queued").GetBoolean()))
            && new[] { "invalid_endpoint", "caller_not_current" }.All(reason => lookups.Any(x => x.GetProperty("reason").GetString() == reason && !x.GetProperty("probe_queued").GetBoolean())),
            "NAT duplicate suppression, invalid target and stale caller reasons recorded");
        var selections = records.Where(x => x.RootElement.GetProperty("event").GetString() == "fragment.selection")
            .Select(x => x.RootElement.GetProperty("data")).ToArray();
        Assert(selections.All(x => x.GetProperty("details").GetProperty("selected").EnumerateArray().All(fragment =>
                new[] { "signature", "session", "source_vport", "destination_vport" }.All(key => fragment.GetProperty(key).GetRawText() == x.GetProperty("packet").GetProperty(key).GetRawText())))
            && selections.Any(x => x.GetProperty("details").GetProperty("retained_streams").GetInt32() > 0),
            "fragment selection stays within incoming stream and records unrelated retained buffers");
        var fragmentDrops = records.Where(x => x.RootElement.GetProperty("event").GetString() == "fragment.drop")
            .Select(x => x.RootElement.GetProperty("data").GetProperty("details")).ToArray();
        Assert(new[] { "disconnect", "inactivity" }.All(reason => fragmentDrops.Any(x => x.GetProperty("reason").GetString() == reason && x.GetProperty("pending").GetInt32() == 1)),
            "fragment discard reasons and pending counts recorded for dropped transports");
        var associations = records.Where(x => x.RootElement.GetProperty("event").GetString() == "connection.association.complete")
            .Select(x => x.RootElement.GetProperty("data").GetProperty("details")).ToArray();
        Assert(new[] { "initial", "repeat", "rebind", "replace" }.All(operation => associations.Any(x => x.GetProperty("operation").GetString() == operation))
            && new[] { "none", "drop_current", "detach_stale" }.All(action => associations.Any(x => x.GetProperty("previous_player_action").GetString() == action))
            && associations.Any(x => x.GetProperty("detached_previous_connection").ValueKind == JsonValueKind.Number)
            && associations.All(x => x.GetProperty("registry_present").GetBoolean()),
            "CONNECT operation, cleanup ownership, retired alias and registry presence recorded");
        Assert(associations.Length > 0 && associations.All(x => x.EnumerateObject().Select(p => p.Name).Order()
            .SequenceEqual(new[] { "operation", "previous_player_action", "detached_previous_connection", "registry_present" }.Order())),
            "CONNECT completion details restricted to classification, numeric connection ID and registry presence");
        Assert(new[] { "gathering", "session" }.All(kind =>
        {
            var removals = records.Where(x => x.RootElement.GetProperty("event").GetString() == kind + ".cleanup.removed")
                .Select(x => x.RootElement.GetProperty("data")).ToArray();
            var fields = kind == "gathering" ? new[] { "pid", "gathering", "reason" } : new[] { "pid", "session", "session_type", "reason" };
            return new[] { "last_member_left", "owner_dropped" }.All(reason => removals.Any(x => x.GetProperty("reason").GetString() == reason))
                && removals.All(x => x.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()));
        }), "scoped cleanup removals record owner, record key and allowlisted reason only");
        var retrySkips = records.Where(x => x.RootElement.GetProperty("event").GetString() == "cache.retry.skipped")
            .Select(x => x.RootElement.GetProperty("data").GetProperty("details")).ToArray();
        Assert(retrySkips.Length >= 2 && retrySkips.All(x => x.GetProperty("reason").GetString() == "expired")
            && Has("cache.expire"), "expired retry skips and scheduled cache retirement are explicit");
        Assert(records.Any(x => x.RootElement.GetProperty("event").GetString() == "fragment.reject"
            && x.RootElement.GetProperty("data").GetProperty("details").GetProperty("reason").GetString() == "sequence_gap"
            && x.RootElement.GetProperty("data").GetProperty("details").GetProperty("expected_sequence").GetInt32() == 2
            && x.RootElement.GetProperty("data").GetProperty("details").GetProperty("received_sequence").GetInt32() == 3),
            "known multipart sequence gap identifies expected and received sequence without payload");
        var cacheOrigins = records.Where(x => x.RootElement.GetProperty("event").GetString() == "cache.enqueue")
            .Select(x => x.RootElement.GetProperty("data").GetProperty("details").GetProperty("cache_origin").GetString()).ToArray();
        Assert(new[] { "server_call", "client_request" }.All(cacheOrigins.Contains)
            && lines.All(x => !x.Contains("ResponseCacheIdentity", StringComparison.Ordinal)),
            "cache origin classification is recorded without internal identity objects");
        var frameRejections = records.Where(x => x.RootElement.GetProperty("event").GetString() == "rmc.frame.reject")
            .Select(x => x.RootElement.GetProperty("data").GetProperty("details")).ToArray();
        Assert(new[] { "short_header", "length_mismatch" }.All(reason => frameRejections.Any(x => x.GetProperty("reason").GetString() == reason))
            && frameRejections.All(x => x.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "reason" })),
            "invalid RMC frames log classification only, without untrusted header words or payload");
        Assert(records.Any(x => x.RootElement.GetProperty("event").GetString() == "fragment.selection"
            && x.RootElement.GetProperty("data").GetProperty("details").GetProperty("retained_packets").GetInt32() > 0),
            "fragment completion records retained messages in the same stream");
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
