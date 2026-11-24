using Alcatraz.Context;
using Alcatraz.Context.Entities;
using DSFServices.DDL.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DSFServices.Services
{
	// A rank belongs to a requested column. Never rank a page-sized subset or
	// sum different columns (times, wins, losses) into a synthetic leaderboard.
	internal static class LeaderboardReader
	{
		internal sealed class Entry
		{
			public PlayerStatisticsBoard Board;
			public string Name;
			public StatisticValueVariant SortValue;
			public StatisticReadValueByBoard Result;
		}

		// One logical board per player. Keep legacy copies intact, but select
		// the latest recorded write consistently; never prefer a higher score
		// over a legitimate newer reset to zero. Break timestamp ties by ID.
		internal static PlayerStatisticsBoard LatestBoard(IEnumerable<PlayerStatisticsBoard> copies) =>
			copies.OrderByDescending(x => x.LastUpdate).ThenBy(x => x.Id).FirstOrDefault();

		internal static int? SingleRankedColumn(int boardId)
		{
			var columns = SeedStatistics.AllStatisticDescriptions.Where(x => x.statBoard == boardId
				&& x.statRankingOrder != RankingOrder.NotRanked).ToArray();
			return columns.Length == 1 ? columns[0].statInBoardId : (int?)null;
		}

		internal static StatisticValueVariant ValueForRead(int boardId, int columnId, StatisticValueVariant value)
		{
			var desc = SeedStatistics.AllStatisticDescriptions.FirstOrDefault(x => x.statBoard == boardId && x.statInBoardId == columnId);
			if (desc?.statType != VariantType.int32 || desc.statRankingOrder == RankingOrder.NotRanked
				|| value == null || value.typeValue < (byte)VariantType.int64 || value.typeValue > (byte)VariantType.Double)
				return value;
			var number = value.GetAsFloat();
			// Driver compares a remote result with its local int32 best time
			// without converting mixed numeric types. Honor the ranked column's
			// declared type when conversion is exact; never rewrite stored data
			// or truncate a fractional/out-of-range value to force compatibility.
			return double.IsFinite(number) && number >= int.MinValue && number <= int.MaxValue && Math.Truncate(number) == number
				? new StatisticValueVariant { typeValue = (byte)VariantType.int32, valueInt32 = (int)number }
				: value;
		}

		internal static List<Entry> Read(MainDbContext db, int boardId, int columnId)
		{
			var desc = SeedStatistics.AllStatisticDescriptions.FirstOrDefault(x => x.statBoard == boardId && x.statInBoardId == columnId);
			if (desc == null || desc.statRankingOrder == RankingOrder.NotRanked) return new List<Entry>();
			var boards = db.PlayerStatisticBoards.Include(x => x.Values).Where(x => x.BoardId == boardId).ToArray();
			var ids = boards.Select(x => x.PlayerId).Distinct().ToArray();
			var names = db.Users.Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.PlayerNickName }).ToDictionary(x => x.Id, x => x.PlayerNickName);
			var entries = new List<Entry>();
			foreach (var board in boards.GroupBy(x => x.PlayerId).Select(x => LatestBoard(x)))
			{
				if (!names.TryGetValue(board.PlayerId, out var name)) continue;
				var cell = board.Values.FirstOrDefault(x => x.PropertyId == columnId);
				if (cell == null || string.IsNullOrWhiteSpace(cell.ValueJSON)) continue;
				StatisticValueVariant value;
				try { value = JsonConvert.DeserializeObject<StatisticValueVariant>(cell.ValueJSON); }
				catch (JsonException) { continue; }
				if (value == null || value.typeValue < (byte)VariantType.int32 || value.typeValue > (byte)VariantType.Double) continue;
				var number = value.GetAsFloat();
				// A default zero best-time/minimum cell means no attempt. Zero
				// counters (for example losses) remain valid leaderboard values.
				if ((number == 0 && desc.statWritePolicy == StatisticPolicy.ReplaceIfMin)
					|| !double.IsFinite(number) || !float.IsFinite((float)number)) continue;
				// The requested column selects the ranking, not the response's
				// display columns. Multiplayer screens also need wins/losses/etc.
				var values = ReadValues(boardId, board.Values);
				if (!values.Any(x => x.propertyId == columnId)) continue;
				entries.Add(new Entry
				{
					Board = board, Name = name, SortValue = value,
					Result = new StatisticReadValueByBoard
					{
						boardId = boardId, lastUpdate = board.LastUpdate, score = (float)number,
						scores = values
					}
				});
			}
			entries.Sort((a, b) =>
			{
				int comparison = a.SortValue.CompareNumeric(b.SortValue);
				if (desc.statRankingOrder == RankingOrder.Descending) comparison = -comparison;
				return comparison != 0 ? comparison : a.Board.PlayerId.CompareTo(b.Board.PlayerId);
			});
			for (int i = 0; i < entries.Count; i++) entries[i].Result.rank = i + 1;
			return entries;
		}

		private static List<StatisticReadValue> ReadValues(int boardId, IEnumerable<PlayerStatisticsBoardValue> cells)
		{
			var known = SeedStatistics.AllStatisticDescriptions.Where(x => x.statBoard == boardId)
				.Select(x => x.statInBoardId).ToHashSet();
			var values = new List<StatisticReadValue>();
			foreach (var cell in cells.Where(x => known.Contains(x.PropertyId)).GroupBy(x => x.PropertyId)
				.Select(x => x.First()).OrderBy(x => x.PropertyId))
			{
				if (string.IsNullOrWhiteSpace(cell.ValueJSON)) continue;
				try
				{
					var value = JsonConvert.DeserializeObject<StatisticValueVariant>(cell.ValueJSON);
					if (value == null || value.typeValue == 0 || value.typeValue > (byte)VariantType.String) continue;
					if (value.typeValue <= (byte)VariantType.Double
						&& (!double.IsFinite(value.GetAsFloat()) || !float.IsFinite((float)value.GetAsFloat()))) continue;
					values.Add(new StatisticReadValue
					{
						propertyId = (byte)cell.PropertyId, value = ValueForRead(boardId, cell.PropertyId, value),
						rankingCriterionIndex = (byte)cell.RankingCriterionIndex,
						sliceScore = JsonConvert.DeserializeObject<StatisticValueVariant>(cell.SliceScoreJSON ?? "null") ?? new StatisticValueVariant(),
						scoreLostForNextSlice = JsonConvert.DeserializeObject<StatisticValueVariant>(cell.ScoreLostForNextSliceJSON ?? "null") ?? new StatisticValueVariant()
					});
				}
				catch (JsonException) { /* A bad companion cell must not hide a valid result. */ }
			}
			return values;
		}

		// Driver brackets a local result with these ordered rank/value anchors
		// and interpolates its estimated rank. Sampling density is server policy;
		// keep the wire list bounded and preserve the population's two endpoints.
		internal static List<Entry> KeyRanks(List<Entry> ranked)
		{
			const int maximum = 100;
			if (ranked.Count <= maximum) return ranked;
			return Enumerable.Range(0, maximum)
				.Select(i => ranked[(int)((long)i * (ranked.Count - 1) / (maximum - 1))]).ToList();
		}

		internal static void RefreshSingleColumnRanks(MainDbContext db, int boardId)
		{
			var column = SingleRankedColumn(boardId);
			if (!column.HasValue) return;
			var ranked = Read(db, boardId, column.Value).ToDictionary(x => x.Board.Id);
			foreach (var board in db.PlayerStatisticBoards.Where(x => x.BoardId == boardId).ToArray()
				.GroupBy(x => x.PlayerId).Select(x => LatestBoard(x)))
			{
				board.Rank = ranked.TryGetValue(board.Id, out var entry) ? entry.Result.rank : 0;
				board.Score = entry?.Result.score ?? 0;
			}
		}
	}
}
