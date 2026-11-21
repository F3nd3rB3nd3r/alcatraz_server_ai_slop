using System.Collections.Generic;
using System.Linq;

namespace QNetZ
{
	public static class NetworkPlayers
	{
		public static uint RVCIDCounter = 0xBB98E;

		public static readonly List<PlayerInfo> Players = new List<PlayerInfo>();

		public static PlayerInfo GetPlayerInfoByPID(uint pid)
		{
			return Players.SingleOrDefault(pl => pl.PID == pid);
		}

		public static PlayerInfo GetPlayerInfoByUsername(string userName)
		{
			return Players.SingleOrDefault(pl => pl.Name == userName);
		}

		public static PlayerInfo CreatePlayerInfo(QClient connection)
		{
			var plInfo = new PlayerInfo();

			plInfo.Client = connection;
			plInfo.PID = 0;
			plInfo.RVCID = RVCIDCounter++;

			Players.Add(plInfo);
			NetworkDiagnostics.Record("player.created", () => new { connection = connection?.IDrecv, endpoint = connection?.Endpoint?.ToString(), rvcid = plInfo.RVCID, count = Players.Count });

			return plInfo;
		}

		public static void PurgeAllPlayers()
		{
			Players.Clear();
		}

		public static void DropPlayerInfo(PlayerInfo playerInfo)
		{
			NetworkDiagnostics.Record("player.drop.explicit", () => new { pid = playerInfo.PID, rvcid = playerInfo.RVCID, connection = playerInfo.Client?.IDrecv });
			QLog.WriteLine(1, $"dropping player: {playerInfo.Name}");

			playerInfo.OnDropped();
			Players.Remove(playerInfo);
			NetworkDiagnostics.Record("player.drop.complete", () => new { pid = playerInfo.PID, count = Players.Count });
		}

		public static void DropPlayers()
		{
			Players.RemoveAll(playerInfo => {
				if(playerInfo.Client != null)
				{
					if (playerInfo.Client.State != QClient.StateType.Dropped)
						return false;

					if (playerInfo.Client.TimeSinceLastPacket < Constants.ClientTimeoutSeconds)
						return false;
				}

				QLog.WriteLine(1, $"Auto-Dropping player: {playerInfo.Name}");
				NetworkDiagnostics.Record("player.drop.timeout", () => new
				{
					pid = playerInfo.PID, rvcid = playerInfo.RVCID, connection = playerInfo.Client?.IDrecv,
					state = playerInfo.Client?.State.ToString(), idle_seconds = playerInfo.Client?.TimeSinceLastPacket
				});
				playerInfo.OnDropped();
				return true;
			});
		}
	}
}
