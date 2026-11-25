using System.Net;
using System.Net.Sockets;
using Alcatraz.Context.Entities;
using DSFServices.DDL.Models;
using DSFServices.Services;
using QNetZ;
using QNetZ.DDL;
using QNetZ.Factory;
using RDVServices;

static class FriendsBlockListChecks
{
    public static int Run()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "friends-check-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        QConfiguration.Instance.DbConnectionString = "Data Source=" + Path.Combine(directory, "friends.sqlite");
        QLog.MinPriority = 0;

        const uint ownerId = 1001, targetId = 1002, requesterId = 1003;
        using (var db = DBHelper.GetDbContext())
        {
            db.Users.AddRange(User(ownerId, "Owner"), User(targetId, "Target"), User(requesterId, "Requester"));
            db.UserRelationships.AddRange(
                new() { User1Id = targetId, User2Id = ownerId, ByRelationShip = 1 },
                new() { User1Id = requesterId, User2Id = ownerId, ByRelationShip = 3, Details = 42 });
            db.SaveChanges();
        }

        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var ownerSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var targetSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var handler = new QPacketHandlerPRUDP(server, 2, 0, "friends-check");
        RMCServiceFactory.RegisterService<FriendsService>();
        RMCServiceFactory.RegisterService<PlayerStatisticsService>();

        const RMCProtocolId friends = RMCProtocolId.FriendsService;
        var owner = Connect(ownerSocket, ownerId);
        var target = Connect(targetSocket, targetId);
        ushort nextCall = 0;

        QClient Connect(UdpClient receiver, uint pid)
        {
            var client = handler.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
            client.State = QClient.StateType.Active;
            client.SessionID = 1;
            client.IDsend = pid;
            client.PlayerInfo = new PlayerInfo { PID = pid, Client = client };
            return client;
        }

        MemoryStream Call<T>(UdpClient receiver, QClient client, RMCProtocolId protocol, uint method, T args,
            Action<QPacket[]>? inspectPackets = null)
            where T : class
        {
            var call = ++nextCall;
            var packet = new QPacket
            {
                type = QPacket.PACKETTYPE.DATA,
                flags = [QPacket.PACKETFLAG.FLAG_RELIABLE, QPacket.PACKETFLAG.FLAG_NEED_ACK],
                m_oSourceVPort = new(0x3f),
                m_oDestinationVPort = new(0x31),
                m_uiSignature = client.IDrecv,
                m_bySessionID = client.SessionID,
                uiSeqId = call,
                usesCompression = false
            };
            packet.payload = new RMCPacket
            {
                proto = protocol, methodID = method, callID = call, isRequest = true,
                request = new RMCPRequestDDL<T>(args)
            }.ToBuffer();
            handler.ProcessPacket(packet.toBuffer(), client.Endpoint);
            handler.Update();
            var packets = Drain(receiver);
            inspectPackets?.Invoke(packets);
            var reply = packets
                .Where(x => x.type == QPacket.PACKETTYPE.DATA && !x.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
                .Select(x => (Packet: x, Rmc: new RMCPacket(x)))
                .SingleOrDefault(x => !x.Rmc.isRequest && x.Rmc.callID == call);
            Require(reply.Rmc is { success: true } && reply.Rmc.proto == protocol && reply.Rmc.methodID == method,
                $"Protocol {protocol}, method {method} returned a matching success envelope");
            return new MemoryStream(reply.Packet!.payload) { Position = reply.Rmc!._afterProtocolOffset };
        }

        bool Change<T>(UdpClient receiver, QClient client, uint method, T args,
            Action<QPacket[]>? inspectPackets = null) where T : class
        {
            using var body = Call(receiver, client, friends, method, args, inspectPackets);
            var result = Helper.ReadBool(body);
            Require(body.Position == body.Length, "Relationship mutation returns one boolean");
            return result;
        }

        List<FriendData> Blocks(UdpClient receiver, QClient client, byte relation = 3, bool reversed = false)
        {
            using var body = Call(receiver, client, friends, 12, new { relation, reversed });
            var blocks = DDLSerializer.ReadObject<List<FriendData>>(body);
            Require(body.Position == body.Length, "Block list returns a list without a wrapper");
            return blocks;
        }

        Require(Blocks(ownerSocket, owner).Count == 0, "Legacy pending friendships stay out of the block list");
        Require(Change(ownerSocket, owner, 8, new { name = "Target", details = 17u }), "Block by game nickname");

        using (var body = Call(ownerSocket, owner, RMCProtocolId.PlayerStatsService, 5,
                   new { board = 62, column = 1, start = 1, rows = 24, filter = 0, value = 0 }))
            Require(body.Length - body.Position == 8 && Helper.ReadU32(body) == 0 && Helper.ReadU32(body) == 0,
                "A leaderboard read completes on the same connection immediately after blocking");

        var block = Blocks(ownerSocket, owner).Single();
        Require(block.m_pid == targetId && block.m_strName == "Target" && block.m_byRelationship == 3 &&
            block.m_uiDetails == 17 && block.m_strStatus == "", "Block list returns the saved target and wire fields");
        Require(Blocks(targetSocket, target).Count == 0 && Blocks(targetSocket, target, reversed: true).Count == 0 &&
            Blocks(ownerSocket, owner, relation: 1).Count == 0, "Only the owner sees blocks in the block list");
        using (var body = Call(ownerSocket, owner, friends, 13, new { offset = 0, count = 16 }))
        {
            var relationships = DDLSerializer.ReadObject<RelationshipsResult>(body);
            Require(relationships.uiTotalCount == 1 && relationships.lstRelationshipsList.Single().m_pid == requesterId,
                "The block replaced the friendship and preserved the unrelated pending request");
        }

        Require(!Change(targetSocket, target, 9, new { player = ownerId }) &&
            !Change(targetSocket, target, 5, new { player = ownerId }) &&
            !Change(targetSocket, target, 6, new { player = ownerId }) &&
            !Change(targetSocket, target, 2, new { name = "Owner", details = 0u, message = "" }),
            "The blocked player cannot clear or bypass another player's block through friend requests");
        Require(!Change(ownerSocket, owner, 7, new { player = ownerId, details = 0u }) &&
            !Change(ownerSocket, owner, 7, new { player = 2000u, details = 0u }) &&
            !Change(ownerSocket, owner, 8, new { name = "Owner", details = 0u }) &&
            !Change(ownerSocket, owner, 8, new { name = "login-Target", details = 0u }),
            "Invalid targets, self-blocks, and website logins get an explicit false response");

        Require(Change(ownerSocket, owner, 7, new { player = targetId, details = 18u }) &&
            Blocks(ownerSocket, owner).Single().m_uiDetails == 18, "A repeat block updates the existing entry");
        Require(Change(targetSocket, target, 7, new { player = ownerId, details = 0u }) &&
            Change(ownerSocket, owner, 9, new { player = targetId }) &&
            Blocks(ownerSocket, owner).Count == 0 && Blocks(targetSocket, target).Single().m_pid == ownerId,
            "Unblocking removes only the caller's block when both players blocked each other");
        Require(Change(targetSocket, target, 9, new { player = ownerId }) &&
            Change(targetSocket, target, 2, new { name = "Owner", details = 0u, message = "" }),
            "Friend requests work again after both players unblock");

        Require(!Change(targetSocket, target, 5, new { player = ownerId }),
            "The sender cannot accept an outgoing request");
        Require(!Change(targetSocket, target, 6, new { player = ownerId }),
            "The sender cannot decline an outgoing request");
        Require(Change(ownerSocket, owner, 6, new { player = targetId }), "Decline the incoming request");
        Require(FriendEvents(Drain(targetSocket), 3).ToArray() is
            [{ m_pidSource: ownerId, m_uiParam1: ownerId }],
            "The requester receives a decline notification from the recipient");
        Require(!Change(ownerSocket, owner, 6, new { player = targetId }) &&
            !FriendEvents(Drain(targetSocket), 3).Any(),
            "Declining a missing request returns false without a notification");
        Require(!Change(ownerSocket, owner, 5, new { player = targetId }),
            "Accepting a missing request returns false");
        Require(Change(targetSocket, target, 2, new { name = "Owner", details = 0u, message = "" }),
            "The requester can send a new request after a decline");

        NotificationEvent[] accepterEvents = [];
        Require(Change(ownerSocket, owner, 5, new { player = targetId },
            packets => accepterEvents = FriendEvents(packets, 1).ToArray()),
            "Accept the incoming request");
        var requesterEvents = FriendEvents(Drain(targetSocket), 1).ToArray();
        Require(accepterEvents is [{ m_pidSource: ownerId, m_uiParam1: targetId }],
            "The accepter receives the acceptance notification with the requester as the other player");
        Require(requesterEvents is [{ m_pidSource: ownerId, m_uiParam1: ownerId }],
            "The requester receives the acceptance notification with the accepter as the other player");

        Require(Change(ownerSocket, owner, 5, new { player = targetId },
            packets => Require(!FriendEvents(packets, 1).Any(), "Retrying acceptance does not notify the accepter again")) &&
            !FriendEvents(Drain(targetSocket), 1).Any(),
            "Retrying acceptance succeeds without notifying the requester again");
        Require(!Change(targetSocket, target, 5, new { player = ownerId }),
            "The requester cannot accept an already accepted friendship");
        Require(!Change(ownerSocket, owner, 6, new { player = targetId }) &&
            !Change(targetSocket, target, 6, new { player = ownerId }),
            "Neither player can decline an accepted friendship");
        using (var body = Call(targetSocket, target, friends, 13, new { offset = 0, count = 16 }))
        {
            var relationships = DDLSerializer.ReadObject<RelationshipsResult>(body);
            Require(relationships.lstRelationshipsList.Single() is { m_pid: ownerId, m_byRelationship: 1 },
                "The requester can still fetch the friendship after invalid decline attempts");
        }
        Require(Change(ownerSocket, owner, 5, new { player = requesterId }),
            "The accepter can confirm a request while the requester is offline");
        using (var db = DBHelper.GetDbContext())
            Require(db.UserRelationships.Single(x => x.User1Id == requesterId && x.User2Id == ownerId).ByRelationShip == 1,
                "The offline requester can fetch the friendship on their next login");

        const uint incomingId = 1299, outgoingId = 1202;
        var offlineFriendIds = Enumerable.Range(1100, 13).Select(pid => (uint)pid).ToArray();
        var additionalFriendIds = Enumerable.Range(1203, 5).Select(pid => (uint)pid).ToArray();
        using (var db = DBHelper.GetDbContext())
        {
            db.Users.AddRange(offlineFriendIds.Select(pid => User(pid, $"Friend {pid}")));
            db.Users.AddRange(User(incomingId, "Incoming"), User(outgoingId, "Outgoing"));
            db.UserRelationships.AddRange(offlineFriendIds.Select(pid => new UserRelationship
            {
                User1Id = ownerId, User2Id = pid, ByRelationShip = 1
            }));
            db.UserRelationships.Add(new UserRelationship
            {
                User1Id = incomingId, User2Id = ownerId, ByRelationShip = 3
            });
            db.SaveChanges();
        }

        RelationshipsResult ReadOwnerRelationships()
        {
            using var body = Call(ownerSocket, owner, friends, 13, new { offset = 0, count = 20 });
            var reply = DDLSerializer.ReadObject<RelationshipsResult>(body);
            Require(body.Position == body.Length, "Relationship list consumes the full reply body");
            return reply;
        }

        var visibleIds = offlineFriendIds.Append(targetId).Append(requesterId).Append(incomingId).ToHashSet();
        var atLimit = ReadOwnerRelationships();
        Require(atLimit.uiTotalCount == 16 && visibleIds.SetEquals(atLimit.lstRelationshipsList.Select(x => x.m_pid)),
            "All 16 visible relationships are returned before sending an outgoing request");
        Require(Change(ownerSocket, owner, 2, new { name = "Outgoing", details = 0u, message = "" }),
            "Send a request while the recipient is offline");
        var withOutgoing = ReadOwnerRelationships();
        Require(withOutgoing.uiTotalCount == 17 && withOutgoing.lstRelationshipsList.Count() == 16 &&
            visibleIds.SetEquals(withOutgoing.lstRelationshipsList.Select(x => x.m_pid)) &&
            withOutgoing.lstRelationshipsList.Single(x => x.m_pid == incomingId).m_byRelationship == 3,
            "An outgoing pending request does not trigger filtering of 16 visible relationships");

        using (var db = DBHelper.GetDbContext())
        {
            db.Users.AddRange(additionalFriendIds.Select(pid => User(pid, $"Friend {pid}")));
            db.UserRelationships.AddRange(additionalFriendIds.Select(pid => new UserRelationship
            {
                User1Id = ownerId, User2Id = pid, ByRelationShip = 1
            }));
            db.SaveChanges();
        }

        var acceptedIds = offlineFriendIds.Concat(additionalFriendIds).Append(targetId).Append(requesterId).Order().ToArray();
        var fullPage = ReadOwnerRelationships();
        Require(fullPage.uiTotalCount == 22 &&
            fullPage.lstRelationshipsList.Select(x => x.m_pid).SequenceEqual(acceptedIds.Take(19).Prepend(incomingId)) &&
            fullPage.lstRelationshipsList.First() is { m_byRelationship: 3, m_byStatus: 0 },
            "The 20-entry page reserves a slot for the incoming request when all friends are offline");

        var online = new PlayerInfo { PID = acceptedIds[^1] };
        NetworkPlayers.Players.Add(online);
        try
        {
            var withOnline = ReadOwnerRelationships().lstRelationshipsList.ToArray();
            Require(withOnline.Select(x => x.m_pid).SequenceEqual(acceptedIds.Take(18).Prepend(online.PID).Prepend(incomingId)) &&
                withOnline[1].m_byStatus == 1,
                "An online friend comes after the incoming request and before offline friends");
        }
        finally
        {
            NetworkPlayers.Players.Remove(online);
        }

        Require(Change(ownerSocket, owner, 5, new { player = incomingId }), "Accept the request from the full page");
        var afterAcceptance = ReadOwnerRelationships();
        Require(afterAcceptance.uiTotalCount == 22 &&
            afterAcceptance.lstRelationshipsList.Select(x => x.m_pid).SequenceEqual(acceptedIds),
            "Accepting the request frees its priority slot and returns 20 accepted friends");
        using (var db = DBHelper.GetDbContext())
            Require(db.UserRelationships.Single(x => x.User1Id == incomingId && x.User2Id == ownerId).ByRelationShip == 1 &&
                db.UserRelationships.Count(x => (x.User1Id == ownerId || x.User2Id == ownerId) && x.ByRelationShip == 1) == 21,
                "All 21 accepted friendships stay saved even when the friend who just accepted falls off the page");

        Console.WriteLine("Friends block-list wire checks passed.");
        return 0;
    }

    private static QPacket[] Drain(UdpClient receiver)
    {
        var packets = new List<QPacket>();
        var sender = new IPEndPoint(IPAddress.Any, 0);
        while (receiver.Available > 0)
            packets.Add(new QPacket(receiver.Receive(ref sender)));
        return packets.ToArray();
    }

    private static IEnumerable<NotificationEvent> FriendEvents(IEnumerable<QPacket> packets, uint action)
    {
        foreach (var packet in packets.Where(p => p.type == QPacket.PACKETTYPE.DATA &&
                 !p.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK)))
        {
            var rmc = new RMCPacket(packet);
            if (!rmc.isRequest || rmc.proto != RMCProtocolId.NotificationEventManager || rmc.methodID != 1)
                continue;
            using var body = new MemoryStream(packet.payload) { Position = rmc._afterProtocolOffset };
            var notice = DDLSerializer.ReadObject<NotificationEvent>(body);
            if (notice.m_uiType == (uint)NotificationEventsType.FriendEvent * 1000 && notice.m_uiParam2 == action)
                yield return notice;
        }
    }

    private static User User(uint pid, string nickname) =>
        new() { Id = pid, Username = "login-" + nickname, PlayerNickName = nickname, Password = "fixture" };

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
