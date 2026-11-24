using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Alcatraz.Context;
using Alcatraz.Context.Entities;
using DSFServices.DDL.Models;
using DSFServices.Services;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using QNetZ;
using QNetZ.DDL;
using QNetZ.Interfaces;
using RDVServices;

// New isolated databases only. Never accepts a live database connection string.
var root = Path.GetFullPath(Path.Combine("artifacts", "leaderboard-check-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
QConfiguration.Instance = QConfiguration.MakeDevelopmentConfiguration("127.0.0.1");
QLog.LogFunction = null;
QLog.EnableFileLogging = false;
var results = new List<Observation>();
void Check(string name, bool brokenBefore, Func<Fixture, bool> check)
{
    using var fixture = new Fixture(Path.Combine(root, name + ".sqlite"));
    try
    {
        var passed = check(fixture);
        results.Add(new(name, passed, brokenBefore, null));
        Console.WriteLine($"{(passed ? "PASS" : "VIOLATION")} {name}");
    }
    catch (Exception ex)
    {
        // Only explicitly characterized production exceptions are invariant
        // failures. Unexpected fixture/SQL/serialization errors remain errors.
        if (brokenBefore && ((ex is NullReferenceException && name is "unknown-description" or "unknown-read-column" or "unknown-write-preserves-known")
            || (ex is DbUpdateException && name is "known-missing-property-insert" or "recreated-property-repeated-write")))
        {
            results.Add(new(name, false, brokenBefore, null));
            Console.WriteLine($"VIOLATION {name}: {ex.GetType().Name}");
        }
        else
        {
            results.Add(new(name, false, brokenBefore, ex.GetType().Name));
            Console.WriteLine($"ERROR {name}: {ex.GetType().Name}");
        }
    }
}

Check("legal-troubles-ascending-control", false, f =>
{
    f.LegalTroubles(); var r = f.Range(61, 1, 1, 100);
    return r.a.Select(x => x.pid).SequenceEqual([1002u, 1001u]) && r.b == 2
        && r.a.Select(x => x.scoresByBoard.Single().rank).SequenceEqual([1, 2]);
});
Check("legal-troubles-pid-results", true, f =>
{
    f.LegalTroubles(); var r = f.ByPids([new() { boardId = 61, columnId = 1 }], [1001, 1002]);
    return r.a.Count == 2 && r.a.Single(x => x.pid == 1002).scoresByBoard.Single().score == 11258
        && r.a.Single(x => x.pid == 1001).scoresByBoard.Single().rank == 2;
});
Check("legal-troubles-pid-subset-global-rank", true, f =>
{
    f.LegalTroubles(); var r = f.ByPids([new() { boardId = 61, columnId = 1 }], [1001]);
    return r.a.Count == 1 && r.a[0].pid == 1001 && r.a[0].scoresByBoard.Single().rank == 2;
});
Check("legal-troubles-read-player-rank", true, f =>
{
    f.LegalTroubles(); var r = Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats(
        [new() { boardId = 61, propertyIds = [1] }], [1001, 1002]));
    return r.Single(x => x.pid == 1001).scoresByBoard.Single().rank == 2
        && r.Single(x => x.pid == 1002).scoresByBoard.Single().rank == 1;
});
// Driver's local challenge time is int32. Its native comparison routines use
// the left operand's type for both values, so returning an integral double
// causes a floating-point bit pattern to be compared with the local integer.
Check("legal-troubles-range-wire-type", true, f =>
{
    f.LegalTroubles(); var rows = f.Range(61, 1, 1, 100).a;
    return rows.All(x => x.scoresByBoard.Single().scores.Single().value.typeValue == 1)
        && rows.Select(x => x.scoresByBoard.Single().scores.Single().value.valueInt32).SequenceEqual([11258, 12153])
        && f.Value(1001, 61, 1).typeValue == 4 && f.Value(1002, 61, 1).typeValue == 4;
});
Check("legal-troubles-pid-wire-type", true, f =>
{
    f.LegalTroubles();
    return f.ByPids([new() { boardId = 61, columnId = 1 }], [1001, 1002]).a
        .All(x => x.scoresByBoard.Single().scores.Single().value.typeValue == 1);
});
Check("legal-troubles-player-stats-wire-type", true, f =>
{
    f.LegalTroubles();
    return Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats([new() { boardId = 61, propertyIds = [1] }], [1001, 1002]))
        .All(x => x.scoresByBoard.Single().scores.Single().value.typeValue == 1);
});
Check("wire-type-preserves-fraction", false, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(11258.5));
    var value = f.Range(61, 1, 1, 100).a.Single().scoresByBoard.Single().scores.Single().value;
    return value.typeValue == 4 && value.valueDouble == 11258.5;
});
Check("wire-type-preserves-outside-int32", false, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(2147483648d)); f.Seed(1002, 61, 1, Fixture.Integer(long.MaxValue));
    var rows = f.Range(61, 1, 1, 100).a;
    return rows.Single(x => x.pid == 1001).scoresByBoard.Single().scores.Single().value.typeValue == 4
        && rows.Single(x => x.pid == 1002).scoresByBoard.Single().scores.Single().value.valueInt64 == long.MaxValue;
});
Check("wire-type-int32-boundaries", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(int.MinValue)); f.Seed(1002, 61, 1, Fixture.Integer(int.MaxValue));
    var values = f.Range(61, 1, 1, 100).a.Select(x => x.scoresByBoard.Single().scores.Single().value).ToArray();
    return values.All(x => x.typeValue == 1) && values.Select(x => x.valueInt32).SequenceEqual([int.MinValue, int.MaxValue]);
});
Check("wire-type-preserves-unranked-stat", false, f =>
{
    f.Seed(1001, 1, 1, Fixture.Number(123));
    return Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats([new() { boardId = 1, propertyIds = [1] }], [1001]))
        .Single().scoresByBoard.Single().scores.Single().value.typeValue == 4;
});
Check("wire-type-preserves-float-stat", false, f =>
{
    var desc = SeedStatistics.AllStatisticDescriptions.First(x => x.statType == VariantType.Float);
    f.Seed(1001, desc.statBoard, desc.statInBoardId, Fixture.Number(1.5));
    var value = Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats(
        [new() { boardId = desc.statBoard, propertyIds = [desc.statInBoardId] }], [1001]))
        .Single().scoresByBoard.Single().scores.Single().value;
    return value.typeValue == 4 && value.valueDouble == 1.5;
});
Check("range-global-order-before-limit", true, f =>
{
    f.LegalTroubles(); var r = f.Range(61, 1, 1, 1);
    return r.a.Count == 1 && r.a[0].pid == 1002;
});
Check("range-offset", true, f =>
{
    f.LegalTroubles();
    using (var db = DBHelper.GetDbContext()) { db.Users.Add(Fixture.User(1003)); db.SaveChanges(); }
    f.Seed(1003, 61, 1, Fixture.Number(10000));
    var r = f.Range(61, 1, 2, 1);
    return r.a.Count == 1 && r.a[0].pid == 1002 && r.a[0].scoresByBoard.Single().rank == 2;
});
Check("range-total-before-pagination", true, f =>
{
    f.LegalTroubles(); return f.Range(61, 1, 1, 1).b == 2;
});
Check("range-empty-late-page", true, f =>
{
    f.LegalTroubles(); var r = f.Range(61, 1, 20, 10); return r.a.Count == 0 && r.b == 2;
});
Check("range-fractional-score-order", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(11258.8)); f.Seed(1002, 61, 1, Fixture.Number(11258.2));
    return f.Range(61, 1, 1, 100).a[0].pid == 1002;
});
Check("range-descending-control", false, f =>
{
    f.Seed(1001, 3, 1, Fixture.Number(10)); f.Seed(1002, 3, 1, Fixture.Number(20));
    return f.Range(3, 1, 1, 100).a.Select(x => x.pid).SequenceEqual([1002u, 1001u]);
});
Check("range-large-integer-order", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Integer(long.MaxValue)); f.Seed(1002, 61, 1, Fixture.Integer(long.MaxValue - 1));
    return f.Range(61, 1, 1, 100).a[0].pid == 1002;
});
Check("range-tie-deterministic", true, f =>
{
    f.Seed(1002, 61, 1, Fixture.Number(11258)); f.Seed(1001, 61, 1, Fixture.Number(11258));
    return f.Range(61, 1, 1, 100).a.Select(x => x.pid).SequenceEqual([1001u, 1002u]);
});
Check("unknown-description", true, _ => SeedStatistics.GetStatisticBoardValueByPropertyId(100, 0) == null);
Check("known-description-control", false, _ => SeedStatistics.GetStatisticBoardValueByPropertyId(61, 1) != null);
Check("unknown-read-column", true, f => f.Range(61, 254, 0, 10).a.Count == 0);
Check("unknown-write-preserves-known", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(12153));
    f.Write(61, [new() { propertyId = 0, value = new() { typeValue = 5, valueString = "unknown-stat-value" }, writePolicy = 2 },
        new() { propertyId = 1, value = Fixture.Number(11258), writePolicy = 2 }]);
    return f.Value(1001, 61, 1).GetAsFloat() == 11258 && f.CountValues(61, 0) == 0;
});
Check("rejected-first-stat-write-does-not-add-ranked-player", true, f =>
{
    f.Seed(1002, 3, 1, Fixture.Number(4504));
    var result = f.Service.WritePlayerStats([new() { boardId = 3, statisticList =
        [new() { propertyId = 254, value = Fixture.Number(100), writePolicy = 2 }] }]);
    var rows = f.Range(3, 1, 1, 24);
    return result.Error == 0 && f.BoardCount(1001, 3) == 0 && rows.b == 1
        && rows.a.Select(x => x.pid).SequenceEqual([1002u]);
});
Check("empty-and-unknown-board-writes-do-not-create-boards", true, f =>
{
    f.Service.WritePlayerStats([
        new() { boardId = 3, statisticList = [] },
        new() { boardId = int.MaxValue, statisticList =
            [new() { propertyId = 1, value = Fixture.Number(100), writePolicy = 2 }] },
        new() { boardId = 62, statisticList =
            [new() { propertyId = 1, value = Fixture.Number(200), writePolicy = 2 }] }
    ]);
    return f.BoardCount(1001, 3) == 0 && f.BoardCount(1001, int.MaxValue) == 0
        && f.Value(1001, 62, 1).GetAsFloat() == 200;
});
Check("rejected-stat-write-preserves-existing-board-metadata", true, f =>
{
    f.Seed(1001, 3, 1, Fixture.Number(4027));
    var savedTime = new DateTime(2026, 11, 23);
    using (var db = DBHelper.GetDbContext())
    {
        var board = db.PlayerStatisticBoards.Single();
        board.LastUpdate = savedTime;
        board.Rank = 2;
        db.SaveChanges();
    }
    f.Write(3, [new() { propertyId = 1, value = null!, writePolicy = 2 }]);
    using var check = DBHelper.GetDbContext();
    var saved = check.PlayerStatisticBoards.Single();
    return saved.LastUpdate == savedTime && saved.Rank == 2 && saved.Score == 4027
        && f.Value(1001, 3, 1).GetAsFloat() == 4027;
});
Check("known-missing-property-insert", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(12153));
    using (var db = DBHelper.GetDbContext()) { db.PlayerStatisticBoardValues.RemoveRange(db.PlayerStatisticBoardValues); db.SaveChanges(); }
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(11258), writePolicy = 2 }]);
    return f.Value(1001, 61, 1).GetAsFloat() == 11258;
});
Check("write-refreshes-single-column-ranks", true, f =>
{
    f.LegalTroubles(); f.Write(61, [new() { propertyId = 1, value = Fixture.Number(11000), writePolicy = 2 }]);
    using var db = DBHelper.GetDbContext(); var rows = db.PlayerStatisticBoards.Where(x => x.BoardId == 61).ToArray();
    return rows.Single(x => x.PlayerId == 1001).Rank == 1 && rows.Single(x => x.PlayerId == 1002).Rank == 2
        && rows.Single(x => x.PlayerId == 1001).Score == 11000;
});
Check("minimum-write-policy-control", false, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(12153));
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(11258), writePolicy = 4 }]);
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(13000), writePolicy = 4 }]);
    return f.Value(1001, 61, 1).GetAsFloat() == 11258;
});
Check("maximum-write-compares-mixed-numeric-types", true, f =>
{
    f.Seed(1001, 62, 1, Fixture.Number(4442));
    f.Seed(1002, 62, 1, Fixture.Number(4200));
    f.Write(62, [new() { propertyId = 1, value = new() { typeValue = 1, valueInt32 = 4000 }, writePolicy = 3 }]);
    var afterWorse = f.Value(1001, 62, 1);
    var afterWorseRank = f.Range(62, 1, 1, 2).a[0].pid;
    f.Write(62, [new() { propertyId = 1, value = new() { typeValue = 1, valueInt32 = 4500 }, writePolicy = 3 }]);
    f.Write(62, [new() { propertyId = 1, value = Fixture.Number(4499.5), writePolicy = 3 }]);
    var afterBetter = f.Value(1001, 62, 1);
    return afterWorse.typeValue == 4 && afterWorse.valueDouble == 4442 && afterWorseRank == 1001
        && afterBetter.typeValue == 1 && afterBetter.valueInt32 == 4500;
});
Check("minimum-write-compares-mixed-numeric-types", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(11258));
    f.Write(61, [new() { propertyId = 1, value = new() { typeValue = 1, valueInt32 = 12000 }, writePolicy = 4 }]);
    var afterWorse = f.Value(1001, 61, 1);
    f.Write(61, [new() { propertyId = 1, value = new() { typeValue = 1, valueInt32 = 11000 }, writePolicy = 4 }]);
    var afterBetter = f.Value(1001, 61, 1);
    return afterWorse.typeValue == 4 && afterWorse.valueDouble == 11258
        && afterBetter.typeValue == 1 && afterBetter.valueInt32 == 11000
        && f.Range(61, 1, 1, 2).a[0].scoresByBoard.Single().score == 11000;
});
Check("mixed-maximum-write-preserves-full-int64-precision", true, f =>
{
    const long best = 9007199254740993;
    f.Seed(1001, 62, 1, Fixture.Integer(best));
    f.Write(62, [new() { propertyId = 1, value = Fixture.Number(best - 1), writePolicy = 3 }]);
    var afterWorse = f.Value(1001, 62, 1);
    f.Write(62, [new() { propertyId = 1, value = Fixture.Integer(best + 1), writePolicy = 3 }]);
    var afterBetter = f.Value(1001, 62, 1);
    return afterWorse.typeValue == 2 && afterWorse.valueInt64 == best
        && afterBetter.typeValue == 2 && afterBetter.valueInt64 == best + 1;
});
Check("mixed-numeric-leaderboard-ranks-preserve-int64-precision", true, f =>
{
    const long best = 9007199254740993;
    f.Seed(1001, 62, 1, Fixture.Number(best - 1));
    f.Seed(1002, 62, 1, Fixture.Integer(best));
    return f.Range(62, 1, 1, 2).a.Select(x => x.pid).SequenceEqual([1002u, 1001u]);
});
Check("pids-multiple-boards", true, f =>
{
    f.LegalTroubles(); f.Seed(1001, 62, 1, Fixture.Number(123));
    var r = f.ByPids([new() { boardId = 61, columnId = 1 }, new() { boardId = 62, columnId = 1 }], [1001]);
    return r.a.Count == 1 && r.a[0].scoresByBoard.Count == 2;
});
Check("pids-empty-control", false, f => f.ByPids([new() { boardId = 61, columnId = 1 }], []).a.Count == 0);
Check("range-wire-roundtrip-control", false, f =>
{
    f.LegalTroubles(); var result = f.Service.ReadStatsLeaderboardByRange(61, 1, 1, 100, 0, 0);
    using var stream = new MemoryStream(result.Response.ToBuffer());
    var decoded = DDLSerializer.ReadObject<BoardReply>(stream);
    return stream.Position == stream.Length && decoded.a.Count == 2 && decoded.b == 2
        && decoded.a[0].pid == 1002 && decoded.a[0].scoresByBoard.Single().scores.Single().value.GetAsFloat() == 11258;
});
Check("pids-wire-roundtrip", true, f =>
{
    f.LegalTroubles(); var result = f.Service.ReadStatsLeaderboardByPIDs([new() { boardId = 61, columnId = 1 }], [1001, 1002]);
    using var stream = new MemoryStream(result.Response.ToBuffer()); var decoded = DDLSerializer.ReadObject<BoardReply>(stream);
    return stream.Position == stream.Length && decoded.a.Count == 2 && decoded.b == 2;
});
Check("friends-stat-cap-control", false, f =>
{
    using (var db = DBHelper.GetDbContext())
    {
        for (uint i = 1003; i < 1020; i++) db.Users.Add(Fixture.User(i));
        db.SaveChanges();
    }
    var ids = Enumerable.Range(1001, 19).Select(x => (uint)x).ToList();
    return Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats([new() { boardId = 61, propertyIds = [1] }], ids)).Count == 16;
});
Check("zero-best-time-is-unranked", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(0)); f.Seed(1002, 61, 1, Fixture.Number(11258));
    var r = f.Range(61, 1, 1, 100);
    return r.a.Count == 1 && r.a[0].pid == 1002 && r.a[0].scoresByBoard.Single().rank == 1;
});
Check("zero-counter-is-ranked", true, f =>
{
    f.Seed(1001, 2, 3, Fixture.Number(0)); f.Seed(1002, 2, 3, Fixture.Number(5));
    var r = f.Range(2, 3, 1, 100);
    return r.a.Count == 2 && r.a[0].pid == 1001 && r.a[0].scoresByBoard.Single().rank == 1;
});
Check("recreated-property-repeated-write", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(12153));
    using (var db = DBHelper.GetDbContext()) { db.PlayerStatisticBoardValues.RemoveRange(db.PlayerStatisticBoardValues); db.SaveChanges(); }
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(11258), writePolicy = 2 },
        new() { propertyId = 1, value = Fixture.Number(11000), writePolicy = 4 }]);
    return f.CountValues(61, 1) == 1 && f.Value(1001, 61, 1).GetAsFloat() == 11000;
});

// Client online/statistics.lua (also present byte-for-byte in the installed
// ScriptsLuaScripts.fchunk) declares Max=3, Min=4. Use literal wire IDs here
// so an incorrect server enum cannot make the fixtures agree with the bug.
Check("client-min-max-policy-ids", true, _ =>
    (byte)StatisticPolicy.ReplaceIfMax == 3 && (byte)StatisticPolicy.ReplaceIfMin == 4);
Check("score-first-submission-from-default", true, f =>
{
    f.Write(62, [new() { propertyId = 1, value = Fixture.Number(4358), writePolicy = 3 }]);
    return f.Value(1001, 62, 1).GetAsFloat() == 4358;
});
Check("score-recover-existing-zero-and-rank", true, f =>
{
    f.Seed(1001, 62, 1, Fixture.Number(0)); f.Seed(1002, 62, 1, Fixture.Number(2700));
    f.Write(62, [new() { propertyId = 1, value = Fixture.Number(4358), writePolicy = 3 }]);
    var r = f.Range(62, 1, 1, 100);
    return r.a.Select(x => x.pid).SequenceEqual([1001u, 1002u]) && r.b == 2
        && r.a.Select(x => x.scoresByBoard.Single().scores.Single().value.valueInt32).SequenceEqual([4358, 2700])
        && f.ByPids([new() { boardId = 62, columnId = 1 }], [1001, 1002]).a.Count == 2;
});
Check("score-max-improves-preserves-best", true, f =>
{
    f.Seed(1001, 62, 1, Fixture.Number(4358));
    f.Write(62, [new() { propertyId = 1, value = Fixture.Number(4500), writePolicy = 3 }]);
    f.Write(62, [new() { propertyId = 1, value = Fixture.Number(2700), writePolicy = 3 }]);
    return f.Value(1001, 62, 1).GetAsFloat() == 4500;
});
Check("time-first-submission-from-default", false, f =>
{
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(12153), writePolicy = 4 }]);
    var r = f.Range(61, 1, 1, 100);
    return r.a.Count == 1 && r.a[0].scoresByBoard.Single().score == 12153;
});
Check("time-min-improves-preserves-best", true, f =>
{
    f.Seed(1001, 61, 1, Fixture.Number(12153));
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(11258), writePolicy = 4 }]);
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(13000), writePolicy = 4 }]);
    return f.Value(1001, 61, 1).GetAsFloat() == 11258;
});

Check("key-ranks-time-anchors", true, f =>
{
    f.LegalTroubles();
    var rows = Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(61, 1));
    return rows.Select(x => x.pid).SequenceEqual([1002u, 1001u])
        && rows.Select(x => x.scoresByBoard.Single().rank).SequenceEqual([1, 2])
        && rows.All(x => x.scoresByBoard.Single().scores.Single().value.typeValue == 1);
});
Check("key-ranks-score-anchors", true, f =>
{
    f.Seed(1001, 62, 1, Fixture.Number(4442)); f.Seed(1002, 62, 1, Fixture.Number(3184));
    var rows = Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(62, 1));
    return rows.Select(x => x.pid).SequenceEqual([1001u, 1002u])
        && rows.Select(x => x.scoresByBoard.Single().scores.Single().value.valueInt32).SequenceEqual([4442, 3184]);
});
Check("key-ranks-empty-unknown-unranked", false, f =>
    Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(61, 1)).Count == 0
    && Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(61, 254)).Count == 0
    && Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(1, 1)).Count == 0);
Check("key-ranks-single-row-wire-envelope", true, f =>
{
    f.Seed(1001, 62, 1, Fixture.Number(4442));
    var result = f.Service.ReadStatsLeaderboardKeyRanks(62, 1);
    using var stream = new MemoryStream(result.Response.ToBuffer());
    var rows = DDLSerializer.ReadObject<List<ScoreListRead>>(stream);
    return stream.Position == stream.Length && rows.Count == 1
        && rows[0].pid == 1001 && rows[0].scoresByBoard.Single().rank == 1;
});
Check("key-ranks-bounded-global-endpoints", true, f =>
{
    using (var db = DBHelper.GetDbContext())
    {
        for (uint i = 1003; i <= 1205; i++) db.Users.Add(Fixture.User(i));
        for (uint i = 1001; i <= 1205; i++)
        {
            var board = SeedStatistics.GeneratePlayerBoard(62, i);
            board.Values.Single(x => x.PropertyId == 1).ValueJSON = JsonConvert.SerializeObject(Fixture.Number(i));
            db.Add(board);
        }
        db.SaveChanges();
    }
    var rows = Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(62, 1));
    var ranks = rows.Select(x => x.scoresByBoard.Single().rank).ToArray();
    return rows.Count == 100 && rows[0].pid == 1205 && rows[^1].pid == 1001
        && ranks[0] == 1 && ranks[^1] == 205 && ranks.Distinct().Count() == 100
        && ranks.SequenceEqual(ranks.Order())
        && rows.All(x => x.scoresByBoard.Single().rank == 1206 - x.pid);
});

Check("nonzero-filter-is-explicitly-unsupported", true, f =>
{
    f.LegalTroubles();
    var result = f.Service.ReadStatsLeaderboardByRange(61, 1, 1, 100, 1, 10);
    return result.Error == (uint)QNetZ.Connection.ErrorCode.Core_NotImplemented
        && result.Response.ToBuffer().Length == 0
        && f.Value(1001, 61, 1).GetAsFloat() == 12153;
});
Check("nonzero-filter-value-never-falls-back", true, f =>
{
    f.LegalTroubles();
    return f.Service.ReadStatsLeaderboardByRange(61, 1, 1, 100, 0, 10).Error
            == (uint)QNetZ.Connection.ErrorCode.Core_NotImplemented
        && f.Service.ReadStatsLeaderboardByRange(61, 1, 1, 100, -1, -1).Error
            == (uint)QNetZ.Connection.ErrorCode.Core_NotImplemented
        && f.Range(61, 1, 1, 100).a.Select(x => x.pid).SequenceEqual([1002u, 1001u]);
});
Check("unsupported-filter-rmc-failure-envelope", true, f =>
{
    var result = f.Service.ReadStatsLeaderboardByRange(61, 1, 1, 100, 1, 1);
    var packet = new RMCPacket { proto = RMCProtocolId.PlayerStatsService, isRequest = false,
        callID = 7, methodID = 5, error = result.Error, response = result.Response };
    var decoded = new RMCPacket(new QPacket { payload = packet.ToBuffer() });
    return !decoded.success && decoded.callID == 7
        && decoded.error == (0x80000000u | (uint)QNetZ.Connection.ErrorCode.Core_NotImplemented);
});

Check("takedown-range-includes-all-columns", true, f =>
{
    f.Multiplayer(3);
    var rows = f.Range(3, 1, 1, 24).a;
    return rows.Select(x => x.pid).SequenceEqual([1002u, 1001u])
        && rows.All(x => x.scoresByBoard.Single().scores.Select(v => v.propertyId).SequenceEqual(new byte[] { 1, 2, 3, 4 }))
        && rows.Single(x => x.pid == 1001).scoresByBoard.Single().scores.Select(v => v.value.GetAsFloat()).SequenceEqual([4027d, 3d, 1d, 0d])
        && rows.Single(x => x.pid == 1002).scoresByBoard.Single().scores.Select(v => v.value.GetAsFloat()).SequenceEqual([4504d, 1d, 3d, 0d]);
});
Check("multiplayer-friend-world-switch-consistency", true, f =>
{
    f.Multiplayer(3);
    for (int i = 0; i < 5; i++)
    {
        var friends = f.ByPids([new() { boardId = 3, columnId = 1 }], [1001, 1002]);
        var world = f.Range(3, 1, 1, 24);
        var own = f.ByPids([new() { boardId = 3, columnId = 1 }], [1001]);
        if (friends.a.Count != 2 || world.a.Count != 2 || own.a.Single().scoresByBoard.Single().rank != 2
            || !friends.a.Concat(world.a).Concat(own.a).All(x => x.scoresByBoard.Single().scores.Count == 4)) return false;
    }
    return f.Value(1001, 3, 2).GetAsFloat() == 3 && f.Value(1002, 3, 2).GetAsFloat() == 1;
});
Check("multiplayer-ranks-only-selected-column", true, f =>
{
    f.Multiplayer(3);
    var points = f.Range(3, 1, 1, 24).a;
    var wins = f.Range(3, 2, 1, 24).a;
    return points[0].pid == 1002 && wins[0].pid == 1001
        && wins[0].scoresByBoard.Single().score == 3
        && wins[0].scoresByBoard.Single().scores.FirstOrDefault(v => v.propertyId == 1)?.value.GetAsFloat() == 4027;
});
Check("trailblazer-wire-keeps-all-statistics", true, f =>
{
    f.Multiplayer(4);
    var response = f.Service.ReadStatsLeaderboardByRange(4, 1, 1, 24, 0, 0);
    using var stream = new MemoryStream(response.Response.ToBuffer());
    var reply = DDLSerializer.ReadObject<BoardReply>(stream);
    return stream.Position == stream.Length && reply.b == 2
        && reply.a.All(x => x.scoresByBoard.Single().scores.Count == 4
            && x.scoresByBoard.Single().scores.All(v => v.value.typeValue == 1))
        && reply.a.Single(x => x.pid == 1001).scoresByBoard.Single().scores.Single(v => v.propertyId == 4).value.valueInt32 == 200;
});
Check("multiplayer-fractional-secondary-column", true, f =>
{
    f.Multiplayer(10); f.Seed(1002, 10, 4, Fixture.Number(1.3333333730697632));
    var row = f.Range(10, 1, 1, 24).a.Single(x => x.pid == 1002).scoresByBoard.Single();
    var average = row.scores.FirstOrDefault(x => x.propertyId == 4);
    return average?.value.typeValue == 4 && average.value.valueDouble == 1.3333333730697632
        && row.score == 4504 && row.rank == 1;
});
Check("multiplayer-malformed-secondary-is-isolated", true, f =>
{
    f.Multiplayer(3);
    using (var db = DBHelper.GetDbContext())
    {
        var board = db.PlayerStatisticBoards.Include(x => x.Values).Single(x => x.PlayerId == 1001 && x.BoardId == 3);
        board.Values.Single(x => x.PropertyId == 4).ValueJSON = "{broken";
        board.Values.Add(new() { PropertyId = 254, ValueJSON = JsonConvert.SerializeObject(Fixture.Number(99)) });
        db.SaveChanges();
    }
    var row = f.Range(3, 1, 1, 24).a.Single(x => x.pid == 1001).scoresByBoard.Single();
    return row.rank == 2 && row.score == 4027
        && row.scores.Select(x => x.propertyId).SequenceEqual(new byte[] { 1, 2, 3 });
});
Check("multiplayer-24-row-pagination-53-players", false, f =>
{
    using (var db = DBHelper.GetDbContext())
    {
        for (uint i = 1003; i <= 1053; i++) db.Users.Add(Fixture.User(i));
        for (uint i = 1001; i <= 1053; i++)
        {
            var board = SeedStatistics.GeneratePlayerBoard(3, i);
            foreach (var cell in board.Values)
                cell.ValueJSON = JsonConvert.SerializeObject(Fixture.Number(cell.PropertyId == 1 ? 10000 - i : i % 7));
            db.Add(board);
        }
        db.SaveChanges();
    }
    var pages = new[] { f.Range(3, 1, 1, 24), f.Range(3, 1, 25, 24), f.Range(3, 1, 49, 24) };
    var rows = pages.SelectMany(x => x.a).ToArray();
    return pages.All(x => x.b == 53) && pages.Select(x => x.a.Count).SequenceEqual([24, 24, 5])
        && rows.Select(x => x.pid).SequenceEqual(Enumerable.Range(1001, 53).Select(x => (uint)x))
        && rows.Select(x => x.scoresByBoard.Single().rank).SequenceEqual(Enumerable.Range(1, 53))
        && f.Range(3, 1, 73, 24).a.Count == 0 && f.Range(3, 1, 73, 24).b == 53;
});
Check("multiplayer-key-ranks-include-companion-stats", true, f =>
{
    f.Multiplayer(3);
    var rows = Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(3, 1));
    return rows.Count == 2 && rows.All(x => x.scoresByBoard.Single().scores.Count == 4);
});

Check("duplicate-trailblazer-range-one-row-per-player", true, f =>
{
    f.DuplicateBoards(4, 1002); f.Seed(1001, 4, 1, Fixture.Number(3000));
    var r = f.Range(4, 1, 1, 24);
    return r.b == 2 && r.a.Select(x => x.pid).SequenceEqual([1002u, 1001u])
        && r.a.Select(x => x.scoresByBoard.Single().rank).SequenceEqual([1, 2])
        && r.a[0].scoresByBoard.Single().score == 4000 && f.BoardCount(1002, 4) == 2;
});
Check("duplicate-overall-friends-one-board", true, f =>
{
    f.DuplicateBoards(12, 1001, true);
    var r = f.ByPids([new() { boardId = 12, columnId = 1 }], [1001, 1002]);
    return r.b == 1 && r.a.Count == 1 && r.a[0].scoresByBoard.Count == 1
        && r.a[0].scoresByBoard.Single().scores.Single(x => x.propertyId == 2).value.GetAsFloat() == 3
        && f.BoardCount(1001, 12) == 2;
});
Check("duplicate-key-ranks-do-not-inflate-population", true, f =>
{
    f.DuplicateBoards(4, 1002); f.Seed(1001, 4, 1, Fixture.Number(3000));
    var rows = Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(4, 1));
    return rows.Count == 2 && rows.Select(x => x.pid).Distinct().Count() == 2
        && rows.Select(x => x.scoresByBoard.Single().rank).SequenceEqual([1, 2]);
});
Check("duplicate-player-stat-read-selects-latest-write", true, f =>
{
    f.DuplicateBoards(12, 1001, true);
    var board = Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats(
        [new() { boardId = 12, propertyIds = [1, 2, 3, 4] }], [1001])).Single().scoresByBoard.Single();
    return board.scores.Single(x => x.propertyId == 1).value.GetAsFloat() == 4000
        && board.scores.Single(x => x.propertyId == 2).value.GetAsFloat() == 3 && f.BoardCount(1001, 12) == 2;
});
Check("missing-player-stat-read-does-not-create-boards", true, f =>
{
    var rows = Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats(
        [new() { boardId = 12, propertyIds = [1, 2, 3, 4] }], [1001, 1002]));
    return rows.Count == 2 && rows.All(x => x.scoresByBoard.Single().scores.Count == 4
        && x.scoresByBoard.Single().scores.All(v => v.value.GetAsFloat() == 0))
        && f.BoardCount(1001, 12) == 0 && f.BoardCount(1002, 12) == 0;
});
Check("duplicate-write-targets-latest-record", true, f =>
{
    var ids = f.DuplicateBoards(12, 1001, true);
    f.Write(12, [new() { propertyId = 1, value = Fixture.Number(5000), writePolicy = 2 }]);
    return f.RecordValue(ids.Written, 1) == 5000 && f.RecordValue(ids.Empty, 1) == 0
        && f.BoardCount(1001, 12) == 2 && f.Range(12, 1, 1, 24).a.Count == 1;
});
Check("duplicate-legitimate-newer-zero-is-preserved", true, f =>
{
    var ids = f.DuplicateBoards(4, 1001);
    using (var db = DBHelper.GetDbContext())
    {
        db.PlayerStatisticBoards.Single(x => x.Id == ids.Empty).LastUpdate = new DateTime(2026, 11, 24);
        db.SaveChanges();
    }
    var r = f.Range(4, 1, 1, 24);
    return r.a.Count == 1 && r.a[0].scoresByBoard.Single().score == 0
        && f.RecordValue(ids.Written, 1) == 4000 && f.BoardCount(1001, 4) == 2;
});
Check("duplicate-timestamp-tie-is-deterministic", true, f =>
{
    var ids = f.DuplicateBoards(4, 1001);
    using (var db = DBHelper.GetDbContext())
    {
        db.PlayerStatisticBoards.Single(x => x.Id == ids.Empty).LastUpdate = new DateTime(2026, 11, 23);
        db.PlayerStatisticBoardValues.Single(x => x.PlayerBoardId == ids.Empty && x.PropertyId == 1)
            .ValueJSON = JsonConvert.SerializeObject(Fixture.Number(5000));
        db.SaveChanges();
    }
    var r = f.Range(4, 1, 1, 24);
    return r.a.Count == 1 && r.a[0].scoresByBoard.Single().score == 4000;
});

Check("duplicate-time-write-preserves-inactive-record", false, f =>
{
    var ids = f.DuplicateBoards(61, 1001);
    using (var db = DBHelper.GetDbContext())
    {
        var inactive = db.PlayerStatisticBoards.Single(x => x.Id == ids.Written);
        inactive.Rank = 7;
        var current = db.PlayerStatisticBoards.Single(x => x.Id == ids.Empty);
        current.LastUpdate = new DateTime(2026, 11, 24); current.Score = 3000;
        db.PlayerStatisticBoardValues.Single(x => x.PlayerBoardId == ids.Empty && x.PropertyId == 1)
            .ValueJSON = JsonConvert.SerializeObject(Fixture.Number(3000));
        db.SaveChanges();
    }
    f.Write(61, [new() { propertyId = 1, value = Fixture.Number(2500), writePolicy = 4 }]);
    using var check = DBHelper.GetDbContext();
    var old = check.PlayerStatisticBoards.Single(x => x.Id == ids.Written);
    var active = check.PlayerStatisticBoards.Single(x => x.Id == ids.Empty);
    return old.Rank == 7 && old.Score == 4000 && f.RecordValue(ids.Written, 1) == 4000
        && active.Rank == 1 && active.Score == 2500;
});
Check("parallel-stat-writes-create-once-and-preserve-adds", false, f =>
{
    using var start = new ManualResetEventSlim();
    var writes = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
    {
        start.Wait();
        f.Write(3, [new() { propertyId = 1, value = Fixture.Number(1), writePolicy = 0 }]);
    })).ToArray();
    start.Set(); Task.WhenAll(writes).GetAwaiter().GetResult();
    return f.BoardCount(1001, 3) == 1 && f.Value(1001, 3, 1).GetAsFloat() == 8;
});
Check("failed-stat-write-leaves-no-empty-board", true, f =>
{
    try
    {
        f.Service.WritePlayerStats([
            new() { boardId = 62, statisticList =
                [new() { propertyId = 1, value = Fixture.Number(200), writePolicy = 2 }] },
            new() { boardId = 3, statisticList = null! }
        ]);
        return false;
    }
    catch (ArgumentNullException) { return f.BoardCount(1001, 3) == 0 && f.BoardCount(1001, 62) == 0; }
});

Check("identity-pid-account-display-roundtrip", true, f =>
{
    f.DistinctIdentities();
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    var ids = Fixture.ReadWire<Dictionary<uint, string>>(accounts.LookupUbiAccountIDsByPids([1001, 1002]));
    var names = Fixture.ReadWire<Dictionary<string, string>>(accounts.LookupUsernamesByUbiAccountIDs(ids.Values));
    return ids[1001] == "Display PC" && ids[1002] == "Display Laptop"
        && names.Count == 2 && names[ids[1001]] == "Display PC" && names[ids[1002]] == "Display Laptop";
});
Check("identity-display-account-roundtrip", true, f =>
{
    f.DistinctIdentities();
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    var ids = Fixture.ReadWire<Dictionary<string, string>>(accounts.LookupUbiAccountIDsByUsernames(["Display PC", "Display Laptop"]));
    return ids.Count == 2 && ids.TryGetValue("Display PC", out var pc) && pc == "Display PC"
        && ids.TryGetValue("Display Laptop", out var laptop) && laptop == "Display Laptop";
});
Check("identity-account-principal-roundtrip", true, f =>
{
    f.DistinctIdentities();
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    var ids = Fixture.ReadWire<Dictionary<string, uint>>(accounts.LookupPrincipalIds(["Display PC", "Display Laptop"]));
    return ids.Count == 2 && ids["Display PC"] == 1001 && ids["Display Laptop"] == 1002;
});
Check("identity-unknown-control", false, f =>
{
    f.DistinctIdentities();
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    return Fixture.Read<Dictionary<uint, string>>(accounts.LookupUbiAccountIDsByPids([9999])).Count == 0
        && Fixture.Read<Dictionary<string, string>>(accounts.LookupUsernamesByUbiAccountIDs(["missing"])).Count == 0
        && Fixture.Read<Dictionary<string, string>>(accounts.LookupUbiAccountIDsByUsernames(["missing"])).Count == 0;
});
Check("identity-leaderboard-display-name-consistency", false, f =>
{
    f.DistinctIdentities(); f.Multiplayer(4);
    var rows = f.Range(4, 1, 1, 24).a
        .Concat(f.ByPids([new() { boardId = 4, columnId = 1 }], [1001, 1002]).a)
        .Concat(Fixture.Read<List<ScoreListRead>>(f.Service.ReadPlayerStats([new() { boardId = 4, propertyIds = [1] }], [1001, 1002])))
        .Concat(Fixture.Read<List<ScoreListRead>>(f.Service.ReadStatsLeaderboardKeyRanks(4, 1))).ToArray();
    return rows.Length == 8 && rows.All(x => x.pname == (x.pid == 1001 ? "Display PC" : "Display Laptop"));
});
Check("identity-overlapping-name-account-domains", true, f =>
{
    f.DistinctIdentities();
    using (var db = DBHelper.GetDbContext())
    {
        foreach (var user in db.Users) user.Username = user.Id == 1001 ? "Display Laptop" : "Display PC";
        db.SaveChanges();
    }
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    var names = Fixture.ReadWire<Dictionary<string, string>>(accounts.LookupUsernamesByUbiAccountIDs(["Display PC"]));
    var pids = Fixture.ReadWire<Dictionary<string, uint>>(accounts.LookupPrincipalIds(["Display PC"]));
    return names.Count == 1 && names["Display PC"] == "Display PC" && pids["Display PC"] == 1001;
});
Check("identity-website-login-is-not-a-game-name", true, f =>
{
    f.DistinctIdentities();
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    return Fixture.ReadWire<Dictionary<string, uint>>(accounts.LookupPrincipalIds(["synthetic-login-pc"])).Count == 0
        && Fixture.ReadWire<Dictionary<string, string>>(accounts.LookupUsernamesByUbiAccountIDs(["synthetic-login-pc"])).Count == 0
        && Fixture.ReadWire<Dictionary<string, string>>(accounts.LookupUbiAccountIDsByUsernames(["synthetic-login-pc"])).Count == 0;
});
Check("identity-own-account-keeps-game-name-control", false, f =>
{
    f.DistinctIdentities();
    f.Service.Context.Client.PlayerInfo.Name = "Display PC";
    f.Service.Context.Client.PlayerInfo.AccountId = "Display PC";
    var accounts = new UbiAccountManagementService { Context = f.Service.Context };
    var own = Fixture.ReadWire<AccountReply>(accounts.GetAccount());
    return own.exist && own.account.m_ubiAccountId == "Display PC" && own.account.m_username == "Display PC";
});

var baseline = args.Contains("--baseline");
var report = new { scope = "New temporary SQLite databases; no live accounts/server/game", observations = results };
var outputIndex = Array.IndexOf(args, "--output");
if (outputIndex >= 0) File.WriteAllText(args[outputIndex + 1], System.Text.Json.JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count} scenarios; {results.Count(x => x.Passed)} pass; {results.Count(x => !x.Passed && x.Error == null)} violations; {results.Count(x => x.Error != null)} harness errors.");
return results.Any(x => x.Error != null || (baseline ? x.Passed == x.BrokenBefore : !x.Passed)) ? 1 : 0;

record Observation(string Name, bool Passed, bool BrokenBefore, string? Error);
class BoardReply
{
    public List<ScoreListRead> a { get; set; } = [];
    public uint b { get; set; }
}
class AccountReply
{
    public UbiAccount account { get; set; } = new();
    public bool exist { get; set; }
}
sealed class Fixture : IDisposable
{
    readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    public PlayerStatisticsService Service { get; }
    public Fixture(string path)
    {
        QConfiguration.Instance.DbConnectionString = "Data Source=" + path;
        using (var db = DBHelper.GetDbContext()) { db.Users.AddRange(User(1001), User(1002)); db.SaveChanges(); }
        var handler = new QPacketHandlerPRUDP(socket, 2, 0, "leaderboard-fixture");
        var client = handler.NewQClient(new IPEndPoint(IPAddress.Loopback, 31001));
        client.PlayerInfo = new PlayerInfo { PID = 1001, Name = "synthetic-player", Client = client };
        Service = new() { Context = new(new RMCPacket(), handler, client, new QPacket()) };
    }
    public static User User(uint id) => new() { Id = id, Username = "synthetic-" + id, PlayerNickName = "synthetic-" + id, Password = "unused-fixture-password" };
    public void DistinctIdentities()
    {
        using var db = DBHelper.GetDbContext();
        foreach (var user in db.Users)
        {
            user.Username = user.Id == 1001 ? "synthetic-login-pc" : "synthetic-login-laptop";
            user.PlayerNickName = user.Id == 1001 ? "Display PC" : "Display Laptop";
        }
        db.SaveChanges();
    }
    public static StatisticValueVariant Number(double value) => new() { typeValue = 4, valueDouble = value };
    public static StatisticValueVariant Integer(long value) => new() { typeValue = 2, valueInt64 = value };
    public (uint Written, uint Empty) DuplicateBoards(int boardId, uint pid, bool emptyFirst = false)
    {
        using var db = DBHelper.GetDbContext();
        var written = SeedStatistics.GeneratePlayerBoard(boardId, pid);
        written.LastUpdate = new DateTime(2026, 11, 23); written.Score = 4000;
        foreach (var cell in written.Values)
            cell.ValueJSON = JsonConvert.SerializeObject(Number(cell.PropertyId == 1 ? 4000 : cell.PropertyId == 2 ? 3 : cell.PropertyId == 3 ? 1 : 0));
        var empty = SeedStatistics.GeneratePlayerBoard(boardId, pid);
        if (emptyFirst) db.AddRange(empty, written); else db.AddRange(written, empty);
        db.SaveChanges(); return (written.Id, empty.Id);
    }
    public int BoardCount(uint pid, int boardId)
    {
        using var db = DBHelper.GetDbContext(); return db.PlayerStatisticBoards.Count(x => x.PlayerId == pid && x.BoardId == boardId);
    }
    public double RecordValue(uint recordId, int property)
    {
        using var db = DBHelper.GetDbContext();
        return JsonConvert.DeserializeObject<StatisticValueVariant>(db.PlayerStatisticBoardValues
            .Single(x => x.PlayerBoardId == recordId && x.PropertyId == property).ValueJSON)!.GetAsFloat();
    }
    public void LegalTroubles() { Seed(1001, 61, 1, Number(12153)); Seed(1002, 61, 1, Number(11258)); }
    public void Multiplayer(int board)
    {
        Seed(1001, board, 1, Number(4027)); Seed(1001, board, 2, Number(3));
        Seed(1001, board, 3, Number(1)); Seed(1001, board, 4, Number(board == 4 ? 200 : 0));
        Seed(1002, board, 1, Number(4504)); Seed(1002, board, 2, Number(1));
        Seed(1002, board, 3, Number(3)); Seed(1002, board, 4, Number(board == 4 ? 300 : 0));
    }
    public void Seed(uint pid, int boardId, int column, StatisticValueVariant value)
    {
        using var db = DBHelper.GetDbContext();
        var board = db.PlayerStatisticBoards.Include(x => x.Values).SingleOrDefault(x => x.PlayerId == pid && x.BoardId == boardId);
        if (board == null) { board = SeedStatistics.GeneratePlayerBoard(boardId, pid); db.Add(board); }
        var cell = board.Values.Single(x => x.PropertyId == column); cell.ValueJSON = JsonConvert.SerializeObject(value);
        board.Score = (float)value.GetAsFloat(); db.SaveChanges();
    }
    public BoardReply Range(int board, int column, int start, int rows) => Read<BoardReply>(Service.ReadStatsLeaderboardByRange(board, column, start, rows, 0, 0));
    public BoardReply ByPids(IEnumerable<LeaderboardData> boards, List<uint> pids) => Read<BoardReply>(Service.ReadStatsLeaderboardByPIDs(boards, pids));
    public void Write(int board, IEnumerable<StatisticWriteValue> values) => Service.WritePlayerStats([new() { boardId = board, statisticList = values }]);
    public StatisticValueVariant Value(uint pid, int board, int column)
    {
        using var db = DBHelper.GetDbContext();
        var cell = db.PlayerStatisticBoardValues.Single(v => v.PlayerBoard.PlayerId == pid && v.PlayerBoard.BoardId == board && v.PropertyId == column);
        return JsonConvert.DeserializeObject<StatisticValueVariant>(cell.ValueJSON)!;
    }
    public int CountValues(int board, int column)
    {
        using var db = DBHelper.GetDbContext(); return db.PlayerStatisticBoardValues.Count(v => v.PlayerBoard.BoardId == board && v.PropertyId == column);
    }
    public static T Read<T>(RMCResult result) where T : new()
    {
        var data = result.Response.GetType().GetField("objectData", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(result.Response)!;
        if (data is T typed) return typed;
        return System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(data))!;
    }
    public static T ReadWire<T>(RMCResult result) where T : class, new()
    {
        using var stream = new MemoryStream(result.Response.ToBuffer());
        var value = DDLSerializer.ReadObject<T>(stream);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing reply bytes");
        return value;
    }
    public void Dispose() => socket.Dispose();
}
