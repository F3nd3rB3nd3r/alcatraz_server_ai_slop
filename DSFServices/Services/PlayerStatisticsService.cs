using Alcatraz.Context.Entities;
using Alcatraz.Context;
using DSFServices.DDL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Newtonsoft.Json;
using QNetZ;
using QNetZ.Attributes;
using QNetZ.DDL;
using QNetZ.Interfaces;
using RDVServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DSFServices.Services
{
	[RMCService(RMCProtocolId.PlayerStatsService)]
	public class PlayerStatisticsService : RMCServiceBase
	{
		// This backend process must not race check/create or lose concurrent
		// Add/Sub updates. Reads below never persist generated default boards.
		private static readonly object StatisticsWriteSync = new object();
		//public static List<StatisticsBoard> StatisticsBoards = new List<StatisticsBoard>();

		[RMCMethod(1)]
		public void PostTwitterMessage(string message)
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(2)]
		public RMCResult WritePlayerStats(IEnumerable<StatisticWriteWithBoard> playerStats)
		{
			lock (StatisticsWriteSync)
				return WritePlayerStatsLocked(playerStats);
		}

		private RMCResult WritePlayerStatsLocked(IEnumerable<StatisticWriteWithBoard> playerStats)
		{
			var playerId = Context.Client.PlayerInfo.PID;

			using (var db = DBHelper.GetDbContext())
			using (var transaction = db.Database.BeginTransaction())
			{
				var changedBoards = new HashSet<int>();
				foreach (var writeBoard in playerStats)
				{
					var submittedStats = writeBoard.statisticList;
					ArgumentNullException.ThrowIfNull(submittedStats);
					var writes = new List<(StatisticWriteValue Stat, StatisticDesc Description)>();
					foreach (var writeStat in submittedStats)
					{
						var description = SeedStatistics.AllStatisticDescriptions.FirstOrDefault(x => x.statBoard == writeBoard.boardId && x.statInBoardId == writeStat.propertyId);
						if (description == null || writeStat.value == null)
							continue;
						writes.Add((writeStat, description));
					}
					// Only a board with a usable write should enter the leaderboard or receive a new timestamp.
					if (writes.Count == 0) continue;

					var playerBoard = LeaderboardReader.LatestBoard(db.PlayerStatisticBoards
						.Where(x => x.PlayerId == playerId && x.BoardId == writeBoard.boardId));

					if (playerBoard == null)
					{
						playerBoard = SeedStatistics.GeneratePlayerBoard(writeBoard.boardId, playerId);
						db.Add(playerBoard);
						db.SaveChanges();
					}

					// avoid Client evaluation simply by converting it into int array
					var propertyIdsOnly = writes.Select(write => (int)write.Stat.propertyId).ToArray();

					var existingBoardValues = db.PlayerStatisticBoardValues
						.Where(x => propertyIdsOnly.Contains(x.PropertyId) && x.PlayerBoardId == playerBoard.Id)
						.ToList();

					float newBoardScore = 0.0f;

					foreach (var (writeStat, description) in writes)
					{
						var existingVariantJSON = existingBoardValues.FirstOrDefault(x => x.PropertyId == writeStat.propertyId);

						if(existingVariantJSON != null)
						{
							var valueToUpdate = new StatisticsBoardValue()
							{
								value = JsonConvert.DeserializeObject<StatisticValueVariant>(existingVariantJSON.ValueJSON),
								rankingCriterionIndex = (byte)existingVariantJSON.RankingCriterionIndex,
								scoreLostForNextSlice = JsonConvert.DeserializeObject<StatisticValueVariant>(existingVariantJSON.ScoreLostForNextSliceJSON),
								sliceScore = JsonConvert.DeserializeObject<StatisticValueVariant>(existingVariantJSON.SliceScoreJSON)
							};

							// A generated best-time zero means no attempt, not a time
							// that can beat every positive submission. Initialize it
							// before applying the client's minimum policy thereafter.
							var policy = (StatisticPolicy)writeStat.writePolicy;
							bool firstTime = policy == StatisticPolicy.ReplaceIfMin
								&& description.statWritePolicy == StatisticPolicy.ReplaceIfMin
								&& description.statRankingOrder != RankingOrder.NotRanked
								&& valueToUpdate.value.GetAsFloat() == 0 && writeStat.value.GetAsFloat() > 0;
							if (valueToUpdate.UpdateValueWithPolicy(writeStat.value, firstTime ? StatisticPolicy.Overwrite : policy))
							{
								QLog.WriteLine(2, $"player {Context.Client.PlayerInfo.Name} {description.statName} ({writeBoard.boardId}:{writeStat.propertyId}) changed to '{writeStat.value.ToString()}' with policy {(StatisticPolicy)writeStat.writePolicy}");
							}

							newBoardScore += (float)valueToUpdate.value.GetAsFloat();

							// put back the values
							existingVariantJSON.RankingCriterionIndex = valueToUpdate.rankingCriterionIndex;
							existingVariantJSON.ValueJSON = JsonConvert.SerializeObject(valueToUpdate.value);
							existingVariantJSON.ScoreLostForNextSliceJSON = JsonConvert.SerializeObject(valueToUpdate.scoreLostForNextSlice);
							existingVariantJSON.SliceScoreJSON = JsonConvert.SerializeObject(valueToUpdate.sliceScore);

							continue;
						}

						QLog.WriteLine(1, $"WARNING: stats property id ({writeBoard.boardId}:{writeStat.propertyId}) was not found, creating new one...");

						var newBoardValue = new StatisticsBoardValue(description) { value = writeStat.value };
						var newVariantJSON = new PlayerStatisticsBoardValue
						{
							PlayerBoardId = playerBoard.Id,
							PropertyId = writeStat.propertyId,
							RankingCriterionIndex = newBoardValue.rankingCriterionIndex,
							ValueJSON = JsonConvert.SerializeObject(newBoardValue.value),
							ScoreLostForNextSliceJSON = JsonConvert.SerializeObject(newBoardValue.scoreLostForNextSlice),
							SliceScoreJSON = JsonConvert.SerializeObject(newBoardValue.sliceScore)
						};

						db.PlayerStatisticBoardValues.Add(newVariantJSON);
						existingBoardValues.Add(newVariantJSON);
					}

					playerBoard.LastUpdate = DateTime.UtcNow;
					playerBoard.Score = newBoardScore;

					changedBoards.Add(writeBoard.boardId);
				}
				db.SaveChanges();
				foreach (var boardId in changedBoards) LeaderboardReader.RefreshSingleColumnRanks(db, boardId);
				db.SaveChanges();
				transaction.Commit();
			}

			return Error(0);
		}

		[RMCMethod(3)]
		public void WritePlayerStatsWithFriendsComparison(IEnumerable<StatisticWriteWithBoard> playerStats, List<uint> playerPIDs)
		{
			UNIMPLEMENTED();
		}

		[RMCMethod(4)]
		public RMCResult ReadPlayerStats(IEnumerable<StatisticData> data, List<uint> playerIds)
		{
			var playerStats = new List<ScoreListRead>();

			int i = 0;
			var rankings = new Dictionary<int, List<LeaderboardReader.Entry>>();
			const int MAX_STAT_PLAYERS_OR_FRIENDS_LIST_WILL_CRASH = 16;

			foreach (var playerId in playerIds)
			{
				// Until we fix the game executable this remains here
				if (++i > MAX_STAT_PLAYERS_OR_FRIENDS_LIST_WILL_CRASH)
					break;

				var player = DBHelper.GetUserByPID(playerId);

				if (player == null)
					continue;

				var scoreListRead = new ScoreListRead()
				{
					pid = playerId,
					pname = player.PlayerNickName
				};
				playerStats.Add(scoreListRead);

				using (var db = DBHelper.GetDbContext())
				{
					foreach (var statReqData in data)
					{
						var playerBoard = LeaderboardReader.LatestBoard(db.PlayerStatisticBoards
							.Include(x => x.Values)
							.Where(x => x.PlayerId == playerId && x.BoardId == statReqData.boardId));

						// Return defaults for an unattempted board without storing
						// an empty copy that can race a real statistics write.
						if (playerBoard == null)
						{
							playerBoard = SeedStatistics.GeneratePlayerBoard(statReqData.boardId, playerId);
						}

						var readBoardValue = new StatisticReadValueByBoard()
						{
							boardId = playerBoard.BoardId,
							lastUpdate = playerBoard.LastUpdate,
							rank = playerBoard.Rank,
							score = playerBoard.Score,
						};

						readBoardValue.scores = playerBoard.Values.Where(x => statReqData.propertyIds.Contains(x.PropertyId)).Select(x => new StatisticReadValue()
						{
							propertyId = (byte)x.PropertyId,
							value = LeaderboardReader.ValueForRead(playerBoard.BoardId, x.PropertyId, JsonConvert.DeserializeObject<StatisticValueVariant>(x.ValueJSON)),
							rankingCriterionIndex = (byte)x.RankingCriterionIndex,
							scoreLostForNextSlice = JsonConvert.DeserializeObject<StatisticValueVariant>(x.ScoreLostForNextSliceJSON),
							sliceScore = JsonConvert.DeserializeObject<StatisticValueVariant>(x.SliceScoreJSON)
						}).ToArray();

						var primaryColumn = LeaderboardReader.SingleRankedColumn(playerBoard.BoardId);
						if (primaryColumn.HasValue)
						{
							if (!rankings.TryGetValue(playerBoard.BoardId, out var ranked))
								rankings[playerBoard.BoardId] = ranked = LeaderboardReader.Read(db, playerBoard.BoardId, primaryColumn.Value);
							var current = ranked.FirstOrDefault(x => x.Board.PlayerId == playerId);
							readBoardValue.rank = current?.Result.rank ?? 0;
							readBoardValue.score = current?.Result.score ?? 0;
						}
						scoreListRead.scoresByBoard.Add(readBoardValue);
					}
				}
			}

			return Result(playerStats);
		}

		// Retain the existing helper type for source compatibility. The unused
		// embedded sample is no longer decoded on each leaderboard request.
		public class ScoreListResultTemp
		{
			public List<ScoreListRead> list { get; set; }
			public uint playersTotal { get; set; }
		}

		[RMCMethod(5)]
		public RMCResult ReadStatsLeaderboardByRange(int boardId, int columnId, int rankStart, int numRows, int filterId, int filterValue)
		{
			// Only the unfiltered (0,0) contract is established. Returning a
			// successful global page for an unknown filter misrepresents its scope.
			if (filterId != 0 || filterValue != 0)
				return Error((uint)QNetZ.Connection.ErrorCode.Core_NotImplemented);
			List<ScoreListRead> rows;
			uint total;
			using (var db = DBHelper.GetDbContext())
			{
				var ranked = LeaderboardReader.Read(db, boardId, columnId);
				total = (uint)ranked.Count;
				// Observed requests and legacy replies use one-based ranks.
				rows = ranked.Skip(Math.Max(1, rankStart) - 1).Take(Math.Max(0, numRows)).Select(x => new ScoreListRead
				{ pid = x.Board.PlayerId, pname = x.Name, scoresByBoard = new List<StatisticReadValueByBoard> { x.Result } }).ToList();
			}
			return Result(new { a = rows, b = total });
		}

		[RMCMethod(8)]
		public RMCResult ReadStatsLeaderboardByPIDs(IEnumerable<LeaderboardData> dataList, List<uint> playerPIDs)
		{
			var requested = (dataList ?? Enumerable.Empty<LeaderboardData>()).ToArray();
			var pids = new HashSet<uint>(playerPIDs ?? new List<uint>());
			var players = new Dictionary<uint, ScoreListRead>();
			uint total = 0;
			using (var db = DBHelper.GetDbContext())
			{
				foreach (var data in requested)
				{
					// Calculate ranks against the full population, then select PIDs.
					foreach (var entry in LeaderboardReader.Read(db, data.boardId, data.columnId).Where(x => pids.Contains(x.Board.PlayerId)))
					{
						if (!players.TryGetValue(entry.Board.PlayerId, out var player))
							players[entry.Board.PlayerId] = player = new ScoreListRead { pid = entry.Board.PlayerId, pname = entry.Name };
						player.scoresByBoard.Add(entry.Result);
						total++;
					}
				}
			}
			var rows = players.Values.OrderBy(x => x.scoresByBoard.First().rank).ThenBy(x => x.pid).ToList();
			return Result(new { a = rows, b = total });
		}

		[RMCMethod(10)]
		public RMCResult ReadStatsLeaderboardKeyRanks(int boardId, int columnId)
		{
			List<ScoreListRead> playerStats;
			using (var db = DBHelper.GetDbContext())
			{
				var ranked = LeaderboardReader.Read(db, boardId, columnId);
				playerStats = LeaderboardReader.KeyRanks(ranked).Select(x => new ScoreListRead
				{ pid = x.Board.PlayerId, pname = x.Name, scoresByBoard = new List<StatisticReadValueByBoard> { x.Result } }).ToList();
			}
			return Result(playerStats);
		}


	}
}
