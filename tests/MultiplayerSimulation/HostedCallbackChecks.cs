using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Alcatraz.GameServices.Services;
using Microsoft.Extensions.Logging;
using QNetZ;

// Manual ticks call the linked production hosted-service implementation. No timer,
// web host or database is started. Blocking gates force the relevant interleavings.
static class HostedCallbackChecks
{
    static readonly FieldInfo ReceiveTaskField = typeof(QPRUDPServiceBase).GetField("CurrentRecvTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static IList Caches(QPacketHandlerPRUDP handler) => (IList)typeof(QPacketHandlerPRUDP)
        .GetField("CachedResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
    static object Field(object target, string name) => target.GetType().GetField(name)!.GetValue(target)!;
    static void SetField(object target, string name, object value) => target.GetType().GetField(name)!.SetValue(target, value);
    static void Wait(Task task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Controlled callback did not finish");
    }
    static Task Start(Action action) => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    static QPacket Response(QClient client) => new()
    {
        type = QPacket.PACKETTYPE.DATA,
        flags = [QPacket.PACKETFLAG.FLAG_RELIABLE],
        m_oSourceVPort = new(0x31),
        m_oDestinationVPort = new(0x3f),
        m_uiSignature = client.IDsend,
        m_bySessionID = 1,
        uiSeqId = 1,
        payload = [1, 2, 3],
        usesCompression = false
    };
    static object Seed(Service service, QClient client, QPacket response)
    {
        var request = Response(client);
        request.m_uiSignature = client.IDrecv;
        typeof(QPacketHandlerPRUDP).GetMethod("CacheResponse", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service.packetHandler, [request, response, client.Endpoint]);
        return Caches(service.packetHandler)[0]!;
    }
    static int Drain(UdpClient receiver)
    {
        var count = 0;
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (receiver.Available > 0)
        {
            receiver.Receive(ref from);
            count++;
        }
        return count;
    }

    public static (bool, string) ReliableReentry()
    {
        using var service = new Service();
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = service.packetHandler.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
        client.IDsend = 2001;
        var cache = Seed(service, client, Response(client));
        var previousLog = QLog.LogFunction;
        var previousPriority = QLog.MinPriority;
        var previousFiles = QLog.EnableFileLogging;
        var reentered = false;
        try
        {
            QLog.EnableFileLogging = false;
            QLog.MinPriority = 5;
            // RetrySend logs after selecting the due cache and before advancing
            // its deadline. Reentry forces a second selection at that exact point;
            // the ResponseList monitor is reentrant, so the unfixed path sends twice.
            QLog.LogFunction = (_, message, _) =>
            {
                if (reentered || !message.Contains("Retry send ", StringComparison.Ordinal))
                    return;
                reentered = true;
                service.Tick();
            };
            service.Tick();
        }
        finally { QLog.LogFunction = previousLog; QLog.MinPriority = previousPriority; QLog.EnableFileLogging = previousFiles; }
        var first = Drain(receiver);
        service.Tick();
        var beforeDue = Drain(receiver);
        SetField(cache, "ResendTime", DateTime.UtcNow.AddSeconds(-1));
        service.Tick();
        var retry = Drain(receiver);
        var sendCount = (int)Field(((IList)Field(cache, "ResponseList"))[0]!, "ReSendCount");
        return (reentered && first == 1 && beforeDue == 0 && retry == 1 && sendCount == 2 && service.Errors.Count == 0,
            $"Forced retry-selection reentry: initial/not-due/due datagrams {first}/{beforeDue}/{retry}; total sends {sendCount}; errors {service.Errors.Count}");
    }

    public static (bool, string) ReceiveOwnership()
    {
        using var service = new Service();
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var client = service.packetHandler.NewQClient((IPEndPoint)receiver.Client.LocalEndPoint!);
        client.IDsend = 2001;
        var response = Response(client);
        var cache = Seed(service, client, response);
        SetField(cache, "ResendTime", DateTime.UtcNow.AddHours(1));
        var ack = Response(client);
        ack.flags = [QPacket.PACKETFLAG.FLAG_ACK];
        ack.m_uiSignature = client.IDrecv;
        ack.m_oSourceVPort = response.m_oDestinationVPort;
        ack.m_oDestinationVPort = response.m_oSourceVPort;
        var ready = Task.FromResult(new UdpReceiveResult(ack.toBuffer(), client.Endpoint));
        ReceiveTaskField.SetValue(service, ready);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var updates = 0;
        service.packetHandler.Updates.Add(() =>
        {
            if (Interlocked.Increment(ref updates) != 1)
                return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Owner release not signaled");
        });
        var owner = Start(service.Tick);
        var untouched = false;
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Owner did not enter update");
            // The owner remains inside Update while eight real competing threads
            // attempt both Update and consumption/replacement of its ready ACK task.
            var contenders = Enumerable.Range(0, 8).Select(_ => Start(service.Tick)).ToArray();
            Wait(Task.WhenAll(contenders));
            untouched = Volatile.Read(ref updates) == 1 && Caches(service.packetHandler).Count == 1
                && ReferenceEquals(ReceiveTaskField.GetValue(service), ready);
        }
        finally { release.Set(); Wait(owner); }
        var pending = (Task<UdpReceiveResult>?)ReceiveTaskField.GetValue(service);
        var consumedOnce = Caches(service.packetHandler).Count == 0 && pending != null && !pending.IsCompleted;
        service.Tick();
        return (untouched && consumedOnce && updates == 2 && ReferenceEquals(pending, ReceiveTaskField.GetValue(service)) && service.Errors.Count == 0,
            $"Eight competing callbacks leave owner's update/ready ACK untouched: {untouched}; ACK consumed and single receive retained: {consumedOnce}; updates after next tick {updates}; errors {service.Errors.Count}");
    }

    public static (bool, string) IndependentServices()
    {
        using var first = new Service();
        using var second = new Service();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var firstUpdates = 0;
        var secondUpdates = 0;
        first.packetHandler.Updates.Add(() =>
        {
            Interlocked.Increment(ref firstUpdates);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Independent service release not signaled");
        });
        second.packetHandler.Updates.Add(() => Interlocked.Increment(ref secondUpdates));
        var owner = Start(first.Tick);
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("First service did not enter");
            Wait(Start(second.Tick));
        }
        finally { release.Set(); Wait(owner); }
        return (firstUpdates == 1 && secondUpdates == 1 && first.Errors.Count == 0 && second.Errors.Count == 0,
            $"Separate service progresses while first is held: updates {firstUpdates}/{secondUpdates}");
    }

    public static (bool, string) ErrorRecovery()
    {
        using var service = new Service();
        var updates = 0;
        service.packetHandler.Updates.Add(() =>
        {
            if (++updates == 1)
                throw new InvalidOperationException("Synthetic update failure");
        });
        service.Tick();
        service.Tick();
        return (updates == 2 && service.Errors.Count == 1 && ReceiveTaskField.GetValue(service) != null,
            $"First update throws; subsequent tick progresses: updates {updates}, logged errors {service.Errors.Count}");
    }

    sealed class Service : QPRUDPServiceBase, IDisposable
    {
        public readonly ErrorLogger Errors;
        public Service() : this(new ErrorLogger()) { }
        Service(ErrorLogger errors) : base(errors)
        {
            Errors = errors;
            listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            packetHandler = new QPacketHandlerPRUDP(listener, ServerPID, ListenPort, "hosted-callback-test");
        }
        public override string ServiceName => "hosted-callback-test";
        public override ushort ListenPort => 0;
        public override uint ServerPID => 2;
        public void Tick() => Process(null!);
        public void Dispose() => listener.Dispose();
    }
    sealed class ErrorLogger : ILogger
    {
        int count;
        public int Count => Volatile.Read(ref count);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Interlocked.Increment(ref count);
        }
    }
}
