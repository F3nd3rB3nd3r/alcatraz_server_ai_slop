using DSFServices.DDL.Models;
using QNetZ;
using QNetZ.DDL;
using System.Collections.Generic;
using System.Linq;

namespace DSFServices
{
	public static class PartySessions
	{
		public static List<PartySessionGathering> GatheringList = new List<PartySessionGathering>();

		/// <summary>
		/// Removes player from all gatherings (except new one) and adds him to new one
		/// </summary>
		/// <param name="playerInfo"></param>
		/// <param name="newGatheringId"></param>
		public static void UpdateGatheringParticipation(PlayerInfo playerInfo, uint newGatheringId)
		{
			MultiplayerDiagnostics.Participation("gathering.participation.begin", playerInfo, newGatheringId, null, null);
			// remove this participant from all gatherings
			// except new one and remove station urls associated
			var oldGatherings = GatheringList.Where(x => x.Participants.Contains(playerInfo.PID) && x.Session.m_idMyself != newGatheringId).ToArray();			
			foreach (var gathering in oldGatherings)
			{
				gathering.Urls.RemoveAll(x => PlayerEndpoint.Matches(x, playerInfo.Url));
				gathering.Participants.Remove(playerInfo.PID);
			}

			// add player to new gathering if he isn't there yet
			var newGathering = GatheringList.SingleOrDefault(x => x.Session.m_idMyself == newGatheringId);
			if(newGathering != null && !newGathering.Participants.Contains(playerInfo.PID))
				newGathering.Participants.Add(playerInfo.PID);

			// A different player may have just registered an empty gathering and
			// not yet sent Participate. Retire only records this update emptied.
			RemoveEmptyGatherings(oldGatherings, playerInfo.PID, "last_member_left");

			playerInfo.GameData().CurrentGatheringId = newGatheringId;
			MultiplayerDiagnostics.Participation("gathering.participation.complete", playerInfo, newGatheringId, null, null);
		}

		public static void CleanupOwnedEmptyGatherings(PlayerInfo playerInfo)
		{
			RemoveEmptyGatherings(GatheringList.Where(x => x.Session.m_pidOwner == playerInfo.PID),
				playerInfo.PID, "owner_dropped");
		}

		private static void RemoveEmptyGatherings(IEnumerable<PartySessionGathering> candidates, uint pid, string reason)
		{
			foreach (var gathering in candidates.ToArray())
			{
				if (gathering.Participants.Count != 0 || !GatheringList.Remove(gathering))
					continue;
				NetworkDiagnostics.Record("gathering.cleanup.removed", () => new { pid, gathering = gathering.Session.m_idMyself, reason });
				QLog.WriteLine(1, $"Auto-deleted gathering {gathering.Session.m_idMyself}");
			}
		}
	}

	public class PartySessionGathering
	{
		public PartySessionGathering()
		{
			Session = new HermesPartySession();
		}

		public PartySessionGathering(HermesPartySession session)
		{
			Session = session;
			Urls = new List<StationURL>();
			Participants = new HashSet<uint>();
		}

		public HermesPartySession Session { get; set; }
		public List<StationURL> Urls { get; set; } // host and player URLs
		public HashSet<uint> Participants { get; set; }
	}
}
