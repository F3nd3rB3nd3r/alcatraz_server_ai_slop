using Alcatraz.Context.Entities;
using Alcatraz.Context;
using DSFServices.DDL.Models;
using Microsoft.EntityFrameworkCore;
using QNetZ;
using QNetZ.Attributes;
using QNetZ.DDL;
using QNetZ.Interfaces;
using RDVServices;
using System.Collections.Generic;
using System.Linq;

namespace DSFServices.Services
{
	/// <summary>
	/// User friends service
	/// </summary>
	[RMCService(RMCProtocolId.FriendsService)]
	public class FriendsService : RMCServiceBase
	{
		private const uint Friendship = 1;
		private const uint PendingFriendship = 3;
		// The PC queries blocks with wire value 3, which existing databases use for pending friendships.
		private const uint StoredBlock = 4;
		private const byte BlockListRelationship = 3;
		private const int RelationshipPageLimit = 20;
		private static readonly object RelationshipWrites = new();

		[RMCMethod(1)]
		public void AddFriend()
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(2)]
		public RMCResult AddFriendByName(string strPlayerName, uint uiDetails, string strMessage)
		{
			var myUserPid = Context.Client.PlayerInfo.PID;
			uint targetPid;

			lock (RelationshipWrites)
			{
				using var db = DBHelper.GetDbContext();
				var foundUser = db.Users
					.AsNoTracking()
					.Where(x => x.Id != myUserPid)
					.FirstOrDefault(x => x.PlayerNickName == strPlayerName);
				if (foundUser == null)
					return Result(new { retVal = false });

				targetPid = foundUser.Id;
				var hasRelationship = db.UserRelationships.Any(x =>
					x.User1Id == myUserPid && x.User2Id == targetPid ||
					x.User1Id == targetPid && x.User2Id == myUserPid);
				if (hasRelationship)
					return Result(new { retVal = false });

				db.UserRelationships.Add(new UserRelationship
				{
					Details = uiDetails,
					User1Id = myUserPid,
					User2Id = targetPid,
					ByRelationShip = PendingFriendship
				});
				db.SaveChanges();
			}

			var qClient = Context.Handler.GetQClientByClientPID(targetPid);
			if (qClient != null)
			{
				var notification = new NotificationEvent(NotificationEventsType.FriendEvent, 0)
				{
					m_pidSource = myUserPid,
					m_uiParam1 = myUserPid,
					m_uiParam2 = 2,
					m_strParam = strMessage
				};
				NotificationQueue.SendNotification(Context.Handler, qClient, notification);
			}
			return Result(new { retVal = true });
		}

		[RMCMethod(3)]
		public void AddFriendWithDetails()
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(4)]
		public void AddFriendByNameWithDetails()
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(5)]
		public RMCResult AcceptFriendship(uint uiPlayer)
		{
			var myUserPid = Context.Client.PlayerInfo?.PID ?? 0;
			if (myUserPid == 0 || uiPlayer == 0 || uiPlayer == myUserPid)
				return Result(new { retVal = false });

			lock (RelationshipWrites)
			{
				using var db = DBHelper.GetDbContext();
				var relationship = db.UserRelationships.Find(uiPlayer, myUserPid);
				if (relationship?.ByRelationShip != PendingFriendship)
					return Result(new { retVal = relationship?.ByRelationShip == Friendship });

				relationship.ByRelationShip = Friendship;
				db.SaveChanges();
			}

			SendFriendshipAccepted(Context.Client, myUserPid, uiPlayer);
			var requester = Context.Handler.GetQClientByClientPID(uiPlayer);
			if (requester?.State == QClient.StateType.Active)
				SendFriendshipAccepted(requester, myUserPid, myUserPid);
			return Result(new { retVal = true });
		}

		private void SendFriendshipAccepted(QClient recipient, uint acceptedBy, uint otherPlayer)
		{
			var notification = new NotificationEvent(NotificationEventsType.FriendEvent, 0)
			{
				m_pidSource = acceptedBy,
				m_uiParam1 = otherPlayer,
				m_uiParam2 = 1
			};
			NotificationQueue.SendNotification(Context.Handler, recipient, notification);
		}

		[RMCMethod(6)]
		public RMCResult DeclineFriendship(uint uiPlayer)
		{
			var myUserPid = Context.Client.PlayerInfo?.PID ?? 0;
			if (myUserPid == 0 || uiPlayer == 0 || uiPlayer == myUserPid)
				return Result(new { retVal = false });

			lock (RelationshipWrites)
			{
				using var db = DBHelper.GetDbContext();
				var relationship = db.UserRelationships.Find(uiPlayer, myUserPid);
				if (relationship?.ByRelationShip != PendingFriendship)
					return Result(new { retVal = false });

				db.UserRelationships.Remove(relationship);
				db.SaveChanges();
			}

			// send notification
			var notification = new NotificationEvent(NotificationEventsType.FriendEvent, 0)
			{
				m_pidSource = myUserPid,
				m_uiParam1 = myUserPid,       // i'm just guessing
				m_uiParam2 = 3
			};

			// send to proper client
			var qClient = Context.Handler.GetQClientByClientPID(uiPlayer);

			if (qClient != null)
			{
				NotificationQueue.SendNotification(Context.Handler, qClient, notification);
			}

			return Result(new { retVal = true });
		}

		[RMCMethod(7)]
		public RMCResult BlackList(uint uiPlayer, uint uiDetails)
		{
			var owner = Context.Client.PlayerInfo?.PID ?? 0;
			if (owner == 0 || uiPlayer == 0 || uiPlayer == owner)
				return Result(new { retVal = false });

			lock (RelationshipWrites)
			{
				using var db = DBHelper.GetDbContext();
				if (!db.Users.Any(x => x.Id == uiPlayer))
					return Result(new { retVal = false });

				StoreBlock(db, owner, uiPlayer, uiDetails);
			}
			return Result(new { retVal = true });
		}

		[RMCMethod(8)]
		public RMCResult BlackListByName(string strPlayerName, uint uiDetails)
		{
			var owner = Context.Client.PlayerInfo?.PID ?? 0;
			if (owner == 0 || string.IsNullOrEmpty(strPlayerName))
				return Result(new { retVal = false });

			lock (RelationshipWrites)
			{
				using var db = DBHelper.GetDbContext();
				var target = db.Users.Where(x => x.PlayerNickName == strPlayerName)
					.Select(x => x.Id).FirstOrDefault();
				if (target == 0 || target == owner)
					return Result(new { retVal = false });

				StoreBlock(db, owner, target, uiDetails);
			}
			return Result(new { retVal = true });
		}

		private static void StoreBlock(MainDbContext db, uint owner, uint target, uint details)
		{
			var pair = db.UserRelationships.Where(x => x.User1Id == owner && x.User2Id == target ||
				x.User1Id == target && x.User2Id == owner).ToArray();
			var outgoing = pair.FirstOrDefault(x => x.User1Id == owner);
			if (outgoing == null)
			{
				outgoing = new UserRelationship { User1Id = owner, User2Id = target };
				db.UserRelationships.Add(outgoing);
			}

			outgoing.ByRelationShip = StoredBlock;
			outgoing.Details = details;
			// A block ends a friendship or request. A block in the other direction stays in place.
			db.UserRelationships.RemoveRange(pair.Where(x => x.User1Id == target && x.ByRelationShip != StoredBlock));
			db.SaveChanges();
		}

		[RMCMethod(9)]
		public RMCResult ClearRelationship(uint uiPlayer)
		{
			var myUserPid = Context.Client.PlayerInfo.PID;

			lock (RelationshipWrites)
			{
				using var db = DBHelper.GetDbContext();
				var relationship = db.UserRelationships.FirstOrDefault(x =>
					(x.User1Id == myUserPid && x.User2Id == uiPlayer) ||
					(x.User1Id == uiPlayer && x.User2Id == myUserPid && x.ByRelationShip != StoredBlock));
				if (relationship == null)
					return Result(new { retVal = false });

				db.UserRelationships.Remove(relationship);
				db.SaveChanges();
			}

			return Result(new { retVal = true });
		}

		[RMCMethod(10)]
		public void UpdateDetails()
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(11)]
		public void GetList()
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(12)]
		public RMCResult GetDetailedList(byte byRelationship, bool bReversed)
		{
			// The PC block-list screen requests (3, false). Keep blocks private to their owner.
			var owner = Context.Client.PlayerInfo?.PID ?? 0;
			if (owner == 0 || byRelationship != BlockListRelationship || bReversed)
				return Result(new List<FriendData>());

			using var db = DBHelper.GetDbContext();
			var blocks = db.UserRelationships.AsNoTracking()
				.Where(x => x.User1Id == owner && x.ByRelationShip == StoredBlock)
				.OrderBy(x => x.User2Id)
				.Select(x => new FriendData
				{
					m_pid = x.User2Id,
					m_strName = x.User2.PlayerNickName,
					m_byRelationship = BlockListRelationship,
					m_uiDetails = x.Details,
					m_strStatus = ""
				}).ToList();
			return Result(blocks);
		}

		[RMCMethod(13)]
		public RMCResult GetRelationships(int offset, int size)
		{
			var onlinePlayerIds = NetworkPlayers.Players.ToArray().Select(x => x.PID).ToArray();
			var myUserPid = Context.Client.PlayerInfo.PID;
			using var db = DBHelper.GetDbContext();
			var relations = db.UserRelationships.AsNoTracking()
				.Where(x => (x.User1Id == myUserPid || x.User2Id == myUserPid) && x.ByRelationShip != StoredBlock);

			var total = (uint)relations.Count();
			// The retail menu reads one 20-row page. Keep incoming invitations ahead of friends.
			var page = relations
				.Where(x => !(x.ByRelationShip == PendingFriendship && x.User1Id == myUserPid))
				.Select(x => new
				{
					Pid = x.User1Id == myUserPid ? x.User2Id : x.User1Id,
					Name = x.User1Id == myUserPid ? x.User2.PlayerNickName : x.User1.PlayerNickName,
					x.ByRelationShip,
					x.Details
				})
				.Select(x => new { x.Pid, x.Name, x.ByRelationShip, x.Details, Online = onlinePlayerIds.Contains(x.Pid) })
				.OrderByDescending(x => x.ByRelationShip == PendingFriendship)
				.ThenByDescending(x => x.ByRelationShip != PendingFriendship && x.Online)
				.ThenBy(x => x.Pid)
				.Take(RelationshipPageLimit)
				.AsEnumerable()
				.Select(x => new RelationshipData
				{
					m_pid = x.Pid,
					m_strName = x.Name,
					m_byStatus = (byte)(x.Online ? 1 : 0),
					m_uiDetails = x.Details,
					m_byRelationship = (byte)x.ByRelationShip
				}).ToArray();
			return Result(new RelationshipsResult { uiTotalCount = total, lstRelationshipsList = page });
		}
	}
}
