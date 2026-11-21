using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using QNetZ.DDL;

namespace QNetZ
{
    // Separate from QLog: no arbitrary RMC arguments, payloads, tickets, or keys.
    // Callers pass only purpose-built metadata snapshots. Failures never escape.
    public static class NetworkDiagnostics
    {
        private static readonly object Sync = new object();
        private static Session current;
        private static int environmentChecked;
        public static bool Enabled => Volatile.Read(ref current) != null;

        public static object Station(StationURL url) => url == null ? null : new
        {
            scheme = url.UrlScheme, address = url.Address,
            port = url.Parameters.TryGetValue("port", out var port) ? (int?)port : null,
            rvcid = url.Parameters.TryGetValue("RVCID", out var rvcid) ? (int?)rvcid : null,
            type = url.Parameters.TryGetValue("type", out var type) ? (int?)type : null
        };

        public static void StartFromEnvironment()
        {
            if (Interlocked.Exchange(ref environmentChecked, 1) != 0) return;
            var directory = Environment.GetEnvironmentVariable("ALCATRAZ_NETWORK_DIAGNOSTICS");
            if (string.IsNullOrWhiteSpace(directory)) return;
            try { Start(directory); }
            catch { /* Diagnostics must not prevent a server from starting. */ }
        }

        public static void Start(string directory, int capacity = 4096, long maxFileBytes = 8 * 1024 * 1024, int retainedFiles = 4)
        {
            lock (Sync)
            {
                if (current != null) throw new InvalidOperationException("Diagnostics already running");
                if (capacity < 1 || maxFileBytes < 1024 || retainedFiles < 1) throw new ArgumentOutOfRangeException();
                current = new Session(Path.GetFullPath(directory), capacity, maxFileBytes, retainedFiles);
            }
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Record("diagnostics.started", () => new
            {
                assembly_version = typeof(NetworkDiagnostics).Assembly.GetName().Version?.ToString(),
                client_timeout_seconds = Constants.ClientTimeoutSeconds,
                resend_seconds = Constants.PacketResendTimeSeconds,
                capacity, max_file_bytes = maxFileBytes, retained_files = retainedFiles
            });
        }

        // The lambda is skipped entirely when disabled and invoked immediately
        // when enabled, before mutable protocol state can change on another turn.
        public static void Record(string eventName, Func<object> snapshot)
        {
            var session = Volatile.Read(ref current);
            if (session == null) return;
            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    schema = 1, utc = DateTimeOffset.UtcNow.ToString("O"),
                    elapsed_ticks = Stopwatch.GetTimestamp() - session.Started,
                    tick_frequency = Stopwatch.Frequency, run = session.Run,
                    process = Environment.ProcessId, thread = Environment.CurrentManagedThreadId,
                    number = Interlocked.Increment(ref session.Sequence),
                    dropped = Interlocked.Read(ref session.Dropped),
                    failures = Interlocked.Read(ref session.Failures),
                    @event = eventName, data = snapshot()
                });
                // Bound both queue length and individual record size.
                if (line.Length > 32768 || !session.Queue.Writer.TryWrite(line))
                    Interlocked.Increment(ref session.Dropped);
            }
            catch { Interlocked.Increment(ref session.Failures); }
        }

        public static async Task StopAsync()
        {
            Session session;
            lock (Sync) { session = current; current = null; }
            if (session == null) return;
            session.Queue.Writer.TryComplete();
            await session.Worker.ConfigureAwait(false);
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }

        private static void OnProcessExit(object sender, EventArgs args)
        {
            try { StopAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
        }

        private sealed class Session
        {
            public readonly string Run = Guid.NewGuid().ToString("N");
            public readonly long Started = Stopwatch.GetTimestamp();
            public readonly Channel<string> Queue;
            public readonly Task Worker;
            public long Sequence, Dropped, Failures;

            public Session(string directory, int capacity, long maxBytes, int retainedFiles)
            {
                Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
                {
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
                });
                Worker = Task.Run(async () =>
                {
                    StreamWriter writer = null;
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var index = 0;
                        long bytes = 0;
                        async Task Persist(string line)
                        {
                            var length = Encoding.UTF8.GetByteCount(line) + 1;
                            if (length > maxBytes) { Interlocked.Increment(ref Dropped); return; }
                            if (writer == null || bytes + length > maxBytes)
                            {
                                if (writer != null) await writer.DisposeAsync();
                                var path = Path.Combine(directory, $"network-{Run}-{index++ % retainedFiles}.jsonl");
                                writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
                                { AutoFlush = true, NewLine = "\n" };
                                bytes = 0;
                            }
                            await writer.WriteLineAsync(line);
                            bytes += length;
                        }
                        await foreach (var line in Queue.Reader.ReadAllAsync()) await Persist(line);
                        if (writer != null)
                            await Persist(JsonSerializer.Serialize(new
                            {
                                schema = 1, utc = DateTimeOffset.UtcNow.ToString("O"), run = Run,
                                @event = "diagnostics.stopped", dropped = Dropped, failures = Failures
                            }));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref Failures);
                        // One payload-free notice even if the configured path is unwritable.
                        Console.Error.WriteLine($"Network diagnostics writer stopped ({ex.GetType().Name}).");
                        Queue.Writer.TryComplete();
                    }
                    finally
                    {
                        if (writer != null)
                        {
                            try { await writer.DisposeAsync(); } catch { Interlocked.Increment(ref Failures); }
                        }
                    }
                });
            }
        }
    }
}
