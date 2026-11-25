using Alcatraz.Context;
using Alcatraz.DTO.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

if (args.Length != 1) throw new ArgumentException("Specify a new, empty directory under artifacts.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new ArgumentException("Use a new test directory to avoid modifying existing data.");
Directory.CreateDirectory(root);
MainDbContext Open(string name) => new(MainDbContext.OnContextBuilding(new DbContextOptionsBuilder<MainDbContext>(),
    DBType.SQLite, $"Data Source={Path.Combine(root, name + ".sqlite")}").Options);

using (var fresh = Open("fresh"))
{
    fresh.Database.Migrate();
    fresh.SaveChanges(); // Same ordering as the hosted server's Startup.Configure.
    if (fresh.Users.Any() || fresh.Database.GetPendingMigrations().Any()) throw new Exception("Fresh migration failed.");
    Console.WriteLine("PASS fresh empty database reaches latest schema and supports current User queries.");
}

string firstHash;
using (var legacy = Open("legacy"))
{
    legacy.GetService<IMigrator>().Migrate("20220705160130_AddPlayerUnlocks");
    legacy.Database.ExecuteSqlInterpolated($"INSERT INTO Users (Id, Username, PlayerNickName, Password, RewardFlags) VALUES ({42}, {"legacy_fixture"}, {"LegacyFixture"}, {"synthetic-old-password"}, {7})");
    legacy.Database.Migrate();
    legacy.SaveChanges();
    legacy.ChangeTracker.Clear();
    var user = legacy.Users.Single();
    if (user.Id != 42 || user.Username != "legacy_fixture" || user.PlayerNickName != "LegacyFixture" ||
        user.RewardFlags != 7 || !SecurePasswordHasher.Verify("42-synthetic-old-password", user.Password))
        throw new Exception("Legacy password or unrelated user data was changed incorrectly.");
    firstHash = user.Password;
    Console.WriteLine("PASS legacy password converted and persisted; ID, username, nickname and rewards preserved.");
}
using (var reopened = Open("legacy"))
{
    reopened.Database.Migrate();
    reopened.SaveChanges();
    var user = reopened.Users.Single();
    if (user.Password != firstHash || !SecurePasswordHasher.Verify("42-synthetic-old-password", user.Password))
        throw new Exception("Repeated startup changed the password again.");
    Console.WriteLine("PASS reopening and repeating migrations does not hash an existing password again.");
}
return 0;
