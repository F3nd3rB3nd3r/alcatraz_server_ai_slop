using System.Text.Json;
using QNetZ;
using QNetZ.DDL;

// Scalar serialization only: no database, sockets, server or account records.
var results = new List<Observation>();
void Check(string name, Func<bool> check)
{
    try
    {
        bool passed = check(); results.Add(new(name, passed, null));
        Console.WriteLine($"{(passed ? "PASS" : "VIOLATION")} {name}");
    }
    catch (Exception ex)
    {
        results.Add(new(name, false, ex.GetType().Name));
        Console.WriteLine($"ERROR {name}: {ex.GetType().Name}");
    }
}
static DateTime Read(byte[] bytes) => Helper.ReadDateTime(new MemoryStream(bytes));
static byte[] Write(DateTime value)
{
    using var stream = new MemoryStream(); Helper.WriteDateTime(stream, value); return stream.ToArray();
}

Check("u64-distinct-upper-half", () =>
    Helper.ReadU64(new MemoryStream(Convert.FromHexString("EFCDAB8967452301"))) == 0x0123456789ABCDEFUL);
Check("u64-full-width-boundaries", () => new[]
{
    0UL, 1UL, uint.MaxValue, 0x100000000UL, 0x7FFFFFFFFFFFFFFFUL, 0x8000000000000000UL, ulong.MaxValue
}.All(value =>
{
    using var stream = new MemoryStream(); Helper.WriteU64(stream, value); stream.Position = 0;
    return stream.Length == 8 && Helper.ReadU64(stream) == value;
}));
Check("i64-ddl-preserves-signed-high-bits", () =>
{
    var envelope = new IntegerEnvelope { Value = long.MinValue + 0x12345678 };
    using var stream = new MemoryStream(); DDLSerializer.WriteObject(envelope, stream); stream.Position = 0;
    return stream.Length == 8 && DDLSerializer.ReadObject<IntegerEnvelope>(stream).Value == envelope.Value
        && stream.Position == stream.Length;
});

// Independent historical wire value 135003992596; not produced by our writer.
var golden = Convert.FromHexString("1472DD6E1F000000");
var goldenDate = new DateTime(2011, 11, 14, 23, 8, 20);
Check("date-read-historical-vector", () => Read(golden) == goldenDate);
Check("date-write-historical-vector", () => Write(goldenDate).SequenceEqual(golden));
Check("date-all-hours-from-wire", () => Enumerable.Range(0, 24).All(hour =>
{
    ulong wire = (135003992596UL & ~(31UL << 12)) | ((ulong)hour << 12);
    return Read(BitConverter.GetBytes(wire)) == new DateTime(2011, 11, 14, hour, 8, 20);
}));
Check("date-calendar-boundaries", () => new[]
{
    new DateTime(1900, 1, 1), new DateTime(2000, 2, 29, 23, 59, 59),
    new DateTime(2024, 2, 29, 7, 6, 5), new DateTime(2026, 11, 23, 22, 39, 17),
    new DateTime(2038, 1, 19, 3, 14, 7), new DateTime(2047, 12, 31, 23, 59, 59)
}.All(date => Read(Write(date)) == date));
Check("date-full-year-field", () => new[]
{
    new DateTime(2048, 1, 1, 1, 2, 3), new DateTime(2400, 2, 29, 12, 34, 56),
    new DateTime(9999, 12, 31, 23, 59, 59)
}.All(date => Read(Write(date)) == date));
Check("date-seconds-precision-and-kind", () =>
{
    var utc = DateTime.SpecifyKind(goldenDate, DateTimeKind.Utc).AddMilliseconds(789);
    var local = DateTime.SpecifyKind(goldenDate, DateTimeKind.Local);
    return Write(utc).SequenceEqual(golden) && Write(local).SequenceEqual(golden)
        && Read(golden).Kind == DateTimeKind.Unspecified;
});
Check("date-invalid-fields-preserve-fallback", () => new[]
{
    0UL, 1UL, (2026UL << 26) | (15UL << 22) | (1UL << 17),
    (2026UL << 26) | (1UL << 22) | (1UL << 17) | (63UL << 6)
}.All(wire => Read(BitConverter.GetBytes(wire)) == new DateTime(1900, 1, 1)));
Check("date-oversized-year-does-not-wrap", () =>
{
    // A cast before validation must not turn this invalid 38-bit year into 2011.
    ulong wire = 135003992596UL | (1UL << 58);
    return Read(BitConverter.GetBytes(wire)) == new DateTime(1900, 1, 1);
});
Check("date-ddl-envelope-width", () =>
{
    var envelope = new DateEnvelope { Before = 0x12345678, Date = goldenDate, After = 0xCAFEBABE };
    using var stream = new MemoryStream(); DDLSerializer.WriteObject(envelope, stream);
    var bytes = stream.ToArray(); stream.Position = 0;
    var decoded = DDLSerializer.ReadObject<DateEnvelope>(stream);
    return bytes.Length == 16 && bytes.Skip(4).Take(8).SequenceEqual(golden)
        && stream.Position == stream.Length && decoded.Before == envelope.Before
        && decoded.Date == goldenDate && decoded.After == envelope.After;
});

int index = Array.IndexOf(args, "--output");
if (index >= 0) File.WriteAllText(args[index + 1], JsonSerializer.Serialize(new
{ scope = "Scalar wire serialization only; no live state", observations = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count} checks; {results.Count(x => x.Passed)} pass; {results.Count(x => !x.Passed && x.Error == null)} violations; {results.Count(x => x.Error != null)} errors.");
return results.All(x => x.Passed) ? 0 : 1;

record Observation(string Name, bool Passed, string? Error);
class DateEnvelope
{
    public uint Before { get; set; }
    public DateTime Date { get; set; }
    public uint After { get; set; }
}
class IntegerEnvelope
{
    public long Value { get; set; }
}
