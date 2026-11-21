using System.Net;
using System.Net.Sockets;
using System.Reflection;
using DSFServices;
using DSFServices.DDL.Models;
using QNetZ;
using QNetZ.DDL;
using QNetZ.Interfaces;

static class CleanupIsolationChecks
{
    public static readonly (string Kind, bool OriginalViolation)[] Cases = [
        ("gathering-pending-other-owner",true),("gathering-pending-same-owner",true),
        ("gathering-last-member-control",false),("gathering-remaining-member-control",false),
        ("gathering-same-target-control",false),("gathering-explicit-unregister-control",false),
        ("gathering-drop-owner-pending",true),("gathering-drop-nonowner-pending",true),
        ("gathering-drop-owner-occupied-control",false),
        ("gathering-create-drop-before-participation",true),
        ("gathering-interleaving-2",true),("gathering-interleaving-5",true),("gathering-interleaving-8",true),
        ("session-pending-join",true),("session-pending-leave",true),
        ("session-unrelated-occupied-join",true),("session-unrelated-occupied-leave",true),
        ("session-move-public-control",false),("session-move-private-control",false),
        ("session-same-target-control",false),("session-key-type-control",false),
        ("session-last-member-control",false),("session-remaining-member-control",false),
        ("session-drop-owner-pending",true),("session-drop-nonowner-pending",true),
        ("session-drop-owner-occupied-control",false),("session-multiple-memberships-control",false),
        ("session-service-create-add-interleaving",true),
        ("session-create-drop-before-participation",true),
        ("session-interleaving-2",true),("session-interleaving-5",true),("session-interleaving-8",true),
        ("endpoint-gathering-same-address",true),("endpoint-session-same-address",true),
        ("endpoint-gathering-missing-port",true),("endpoint-session-missing-port",true),
        ("endpoint-gathering-invalid-port",true),("endpoint-session-invalid-port",true),
        ("endpoint-gathering-scheme-control",false),("endpoint-session-scheme-control",false),
        ("endpoint-gathering-other-address-control",false),("endpoint-session-other-address-control",false),
        ("endpoint-gathering-null-url-control",false),("endpoint-session-null-url-control",false),
        ("endpoint-gathering-own-variants-control",false),("endpoint-session-own-variants-control",false),
        ("endpoint-default-compare-control",false)
    ];

    public static (bool, string) Run(string kind)
    {
        if (kind.Contains("interleaving-") && int.TryParse(kind.Split('-')[^1], out var count))
            return Interleaving(kind.StartsWith("gathering"), count);
        if (kind.EndsWith("create-drop-before-participation"))
            return CreateAndDrop(kind.StartsWith("gathering"));
        var a = Player(101, 31001);
        var b = Player(102, 31002);
        if (kind.StartsWith("endpoint-"))
            return Endpoint(kind, a, b);
        if (kind.StartsWith("gathering-"))
        {
            var old = Gathering(1, a);
            old.Participants.Add(a.PID);
            var next = Gathering(2, b);
            next.Participants.Add(b.PID);
            var pending = Gathering(3, kind == "gathering-pending-same-owner" ? a : b);
            if (kind.Contains("pending"))
            {
                if (kind.Contains("drop-"))
                {
                    pending.Session.m_pidOwner = a.PID;
                    var othersPending = Gathering(4, b);
                    NetworkPlayers.DropPlayerInfo(kind.Contains("nonowner") ? b : a);
                    bool ok = kind.Contains("nonowner") ? PartySessions.GatheringList.Contains(pending) && !PartySessions.GatheringList.Contains(othersPending)
                        : !PartySessions.GatheringList.Contains(pending) && PartySessions.GatheringList.Contains(othersPending);
                    return (ok, "Owner drop retires only its empty creations; another owner's pending creation survives");
                }
                PartySessions.UpdateGatheringParticipation(a, 2);
                return (PartySessions.GatheringList.Contains(pending) && !PartySessions.GatheringList.Contains(old)
                    && next.Participants.SetEquals([a.PID, b.PID]), "Joining another lobby preserves an unrelated pending creation, including one by the same active owner");
            }
            // These controls do not depend on retaining unrelated pending records.
            PartySessions.GatheringList.Remove(pending);
            switch (kind)
            {
                case "gathering-last-member-control":
                    PartySessions.UpdateGatheringParticipation(a, uint.MaxValue);
                    return (!PartySessions.GatheringList.Contains(old) && PartySessions.GatheringList.Contains(next), "Last member's former lobby is deleted; occupied peer lobby survives");
                case "gathering-remaining-member-control":
                case "gathering-drop-owner-occupied-control":
                    old.Participants.Add(b.PID);
                    if (kind.Contains("drop"))
                        NetworkPlayers.DropPlayerInfo(a);
                    else
                        PartySessions.UpdateGatheringParticipation(a, uint.MaxValue);
                    return (PartySessions.GatheringList.Contains(old) && old.Participants.SetEquals([b.PID]), "Remaining member keeps the former owner's occupied lobby");
                case "gathering-same-target-control":
                    PartySessions.UpdateGatheringParticipation(a, 1);
                    PartySessions.UpdateGatheringParticipation(a, 1);
                    return (old.Participants.SetEquals([a.PID]), "Repeated same-lobby participation is idempotent");
                case "gathering-explicit-unregister-control":
                    using (var socket = new UdpClient(AddressFamily.InterNetwork))
                    {
                        var match = Service("MatchMakingService", new(socket, 2, 0, "cleanup-check"), a.Client);
                        var result = Call(match, "UnregisterGathering", 2u);
                        return (result.Response.ToBuffer().SequenceEqual(new byte[] { 1 }) && !PartySessions.GatheringList.Contains(next), "Explicit unregister still removes a pending/occupied specified gathering");
                    }
            }
        }
        if (kind == "session-service-create-add-interleaving")
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            var h = new QPacketHandlerPRUDP(socket, 2, 0, "cleanup-check");
            var service = new DSFServices.Services.GameSessionService { Context = new(new RMCPacket(), h, a.Client, new QPacket()) };
            var result = service.CreateSession(new GameSession { m_typeID = 1, m_attributes = [] });
            var bytes = result.Response.ToBuffer();
            var key = new GameSessionKey { m_typeID = BitConverter.ToUInt32(bytes, 0), m_sessionID = BitConverter.ToUInt32(bytes, 4) };
            var other = Session(99, b);
            other.PublicParticipants.Add(b.PID);
            GameSessions.UpdateSessionParticipation(b, Key(other), false);
            service.AddParticipants(key, [a.PID], []);
            var created = GameSessions.SessionList.SingleOrDefault(x => x.IsMatchingKey(key));
            return (created != null && created.PublicParticipants.Contains(a.PID), "Real CreateSession -> other player's update -> AddParticipants retains and joins the creation");
        }
        var current = Session(1, a);
        current.PublicParticipants.Add(a.PID);
        var target = Session(2, b);
        target.PublicParticipants.Add(b.PID);
        switch (kind)
        {
            case "session-pending-join":
            case "session-pending-leave":
                var pending = Session(3, b);
                GameSessions.UpdateSessionParticipation(a, kind.EndsWith("join") ? Key(target) : null!, false);
                return (GameSessions.SessionList.Contains(pending), "An unrelated empty event survives another player's join/leave");
            case "session-unrelated-occupied-join":
            case "session-unrelated-occupied-leave":
                // Same-address peers make an unrelated cleanup observable through
                // the surviving host's URL. A is not a member of this event.
                b.Client.Endpoint = new(a.Client.Endpoint.Address, b.Client.Endpoint.Port);
                target.HostURLs.Add(b.Url);
                var destination = Session(3, a);
                GameSessions.UpdateSessionParticipation(a, kind.EndsWith("join") ? Key(destination) : null!, false);
                return (target.PublicParticipants.SetEquals([b.PID]) && target.HostURLs.Count == 1, "An event A never joined keeps B's endpoint and membership");
            case "session-drop-owner-pending":
            case "session-drop-nonowner-pending":
                var owned = Session(3, a);
                var otherOwned = Session(4, b);
                NetworkPlayers.DropPlayerInfo(kind.Contains("nonowner") ? b : a);
                return (kind.Contains("nonowner") ? GameSessions.SessionList.Contains(owned) && !GameSessions.SessionList.Contains(otherOwned)
                    : !GameSessions.SessionList.Contains(owned) && GameSessions.SessionList.Contains(otherOwned), "Owner drop removes only its empty event creations");
            case "session-drop-owner-occupied-control":
            case "session-remaining-member-control":
                current.PublicParticipants.Add(b.PID);
                if (kind.Contains("drop"))
                    NetworkPlayers.DropPlayerInfo(a);
                else
                    GameSessions.UpdateSessionParticipation(a, null!, false);
                return (GameSessions.SessionList.Contains(current) && current.PublicParticipants.SetEquals([b.PID]), "Another participant keeps the former host's occupied event");
            case "session-last-member-control":
                GameSessions.UpdateSessionParticipation(a, null!, false);
                return (!GameSessions.SessionList.Contains(current) && GameSessions.SessionList.Contains(target), "Last member removes its former event only");
            case "session-move-private-control":
            case "session-move-public-control":
                if (kind.Contains("private"))
                {
                    current.PublicParticipants.Clear();
                    current.Participants.Add(a.PID);
                }
                GameSessions.UpdateSessionParticipation(a, Key(target), kind.Contains("private"));
                return (!GameSessions.SessionList.Contains(current) && (kind.Contains("private") ? target.Participants : target.PublicParticipants).Contains(a.PID)
                    && target.Attributes[(uint)GameSessionAttributeType.FilledPublicSlots] == target.PublicParticipants.Count
                    && target.Attributes[(uint)GameSessionAttributeType.FilledPrivateSlots] == target.Participants.Count, "Moving public/private participation keeps slot counts and deletes the emptied old event");
            case "session-same-target-control":
                GameSessions.UpdateSessionParticipation(a, Key(current), false);
                GameSessions.UpdateSessionParticipation(a, Key(current), false);
                return (current.PublicParticipants.SetEquals([a.PID]), "Repeated same-event participation is idempotent");
            case "session-key-type-control":
                target.Id = current.Id;
                target.TypeID = 2;
                GameSessions.UpdateSessionParticipation(a, Key(target), false);
                return (!GameSessions.SessionList.Contains(current) && target.PublicParticipants.Contains(a.PID), "Equal numeric IDs with different event types remain different keys");
            case "session-multiple-memberships-control":
                target.Participants.Add(a.PID);
                GameSessions.UpdateSessionParticipation(a, null!, false);
                return (!current.PublicParticipants.Contains(a.PID) && !target.Participants.Contains(a.PID) && target.PublicParticipants.Contains(b.PID), "Leaving clears all actual public/private memberships, retaining other players");
        }
        throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
    }

    static (bool, string) Interleaving(bool gathering, int count)
    {
        var players = Enumerable.Range(0, count).Select(i => Player((uint)(200 + i), 32000 + i)).ToArray();
        if (gathering)
        {
            var created = players.Select((p, i) => Gathering((uint)(100 + i), p)).ToArray();
            for (var i = count - 1; i >= 0; i--)
                PartySessions.UpdateGatheringParticipation(players[i], created[i].Session.m_idMyself);
            var kept = created.Where((g, i) => PartySessions.GatheringList.Contains(g) && g.Participants.SetEquals([players[i].PID])).Count();
            return (kept == count, $"{kept}/{count} interleaved create/join lobbies survive");
        }
        else
        {
            var created = players.Select((p, i) => Session((uint)(100 + i), p)).ToArray();
            for (var i = count - 1; i >= 0; i--)
                GameSessions.UpdateSessionParticipation(players[i], Key(created[i]), false);
            var kept = created.Where((s, i) => GameSessions.SessionList.Contains(s) && s.PublicParticipants.SetEquals([players[i].PID])).Count();
            return (kept == count, $"{kept}/{count} interleaved create/join events survive");
        }
    }
    static (bool, string) Endpoint(string kind, PlayerInfo a, PlayerInfo b)
    {
        b.Client.Endpoint = new(a.Client.Endpoint.Address, b.Client.Endpoint.Port);
        if (kind == "endpoint-default-compare-control")
            return (a.Url.Compare(b.Url), "Default StationURL.Compare remains address/scheme-only for unrelated callers");
        var keep = b.Url;
        var own = a.Url;
        if (kind.Contains("other-address"))
            keep.Address = "127.0.0.9";
        if (kind.Contains("scheme"))
            keep.UrlScheme = "prudps";
        if (kind.Contains("missing-port"))
            keep.Parameters.Remove("port");
        if (kind.Contains("invalid-port"))
            keep["port"] = 0;
        var variants = kind.Contains("own-variants");
        if (variants)
            keep.Address = "127.0.0.9";
        var urls = new List<StationURL> { own, keep };
        if (variants)
        {
            var second = a.Url;
            second["type"] = 3;
            urls.Add(second);
        }
        if (kind.Contains("null-url"))
            a.Client = null!;
        if (kind.Contains("gathering"))
        {
            var g = Gathering(1, b);
            g.Participants.UnionWith([a.PID, b.PID]);
            g.Urls.AddRange(urls);
            PartySessions.UpdateGatheringParticipation(a, uint.MaxValue);
            return (g.Participants.SetEquals([b.PID]) && g.Urls.Contains(keep) && g.Urls.Count == (a.Client == null ? 2 : 1), "Lobby cleanup removes identifiable own endpoint variants and preserves the peer/ambiguous endpoint");
        }
        var s = Session(1, b);
        s.PublicParticipants.UnionWith([a.PID, b.PID]);
        s.HostURLs.AddRange(urls);
        GameSessions.UpdateSessionParticipation(a, null!, false);
        return (s.PublicParticipants.SetEquals([b.PID]) && s.HostURLs.Contains(keep) && s.HostURLs.Count == (a.Client == null ? 2 : 1), "Event cleanup removes identifiable own endpoint variants and preserves the peer/ambiguous endpoint");
    }
    static (bool, string) CreateAndDrop(bool gathering)
    {
        // First game action after login: no pre-created DSF datastore. Drop must
        // still clean up the creation if Participate/AddParticipants never arrives.
        var owner = Player(201, 33001, false);
        var other = Player(202, 33002, false);
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        var h = new QPacketHandlerPRUDP(socket, 2, 0, "creation-drop-check");
        uint Create(PlayerInfo p)
        {
            if (gathering)
                return BitConverter.ToUInt32(Call(Service("MatchMakingService", h, p.Client), "RegisterGathering",
                new AnyData<HermesPartySession>(new HermesPartySession())).Response.ToBuffer());
            var result = Call(Service("GameSessionService", h, p.Client), "CreateSession", new GameSession { m_typeID = 1, m_attributes = [] });
            return BitConverter.ToUInt32(result.Response.ToBuffer(), 4);
        }
        var ownedId = Create(owner);
        var otherId = Create(other);
        NetworkPlayers.DropPlayerInfo(owner);
        bool exists(uint id) => gathering ? PartySessions.GatheringList.Any(g => g.Session.m_idMyself == id) : GameSessions.SessionList.Any(s => s.Id == id);
        return (!exists(ownedId) && exists(otherId), "First-action creation is retired when its owner drops before participating; another owner's creation survives");
    }
    static PlayerInfo Player(uint pid, int port, bool initializeData = true)
    {
        var client = new QClient(pid + 1000, new(IPAddress.Loopback, port)) { State = QClient.StateType.Active };
        var p = NetworkPlayers.CreatePlayerInfo(client);
        p.PID = pid;
        p.Name = "synthetic-cleanup";
        client.PlayerInfo = p;
        if (initializeData)
            p.GameData();
        return p;
    }
    static PartySessionGathering Gathering(uint id, PlayerInfo owner)
    {
        var g = new PartySessionGathering(new()
        {
            m_idMyself = id,
            m_pidOwner = owner.PID,
            m_pidHost = owner.PID
        });
        PartySessions.GatheringList.Add(g);
        return g;
    }
    static GameSessionData Session(uint id, PlayerInfo owner)
    {
        var s = new GameSessionData { Id = id, TypeID = 1, HostPID = owner.PID };
        GameSessions.SessionList.Add(s);
        return s;
    }
    static GameSessionKey Key(GameSessionData s) => new() { m_sessionID = s.Id, m_typeID = s.TypeID };
    static RMCServiceBase Service(string name, QPacketHandlerPRUDP h, QClient c)
    {
        var type = typeof(DSFPlayerGameData).Assembly.GetType("DSFServices.Services." + name)!;
        var service = (RMCServiceBase)Activator.CreateInstance(type, true)!;
        service.Context = new(new RMCPacket(), h, c, new QPacket());
        return service;
    }
    static RMCResult Call(object target, string name, params object[] args) => (RMCResult)target.GetType().GetMethod(name)!.Invoke(target, args)!;
}
