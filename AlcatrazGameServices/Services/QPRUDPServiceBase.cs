using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QNetZ;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Alcatraz.GameServices.Services
{
	public abstract class QPRUDPServiceBase : IHostedService
	{
		private Timer _timer;
		private int _diagnosticCallbacks;
		protected ILogger _logger;
		public abstract string ServiceName { get; }
		public abstract ushort ListenPort { get; }
		public abstract uint ServerPID { get; }

		public UdpClient listener;
		public QPacketHandlerPRUDP packetHandler;

		public QPRUDPServiceBase(ILogger logger)
		{
			_logger = logger;
		}

		//----------------------------------------------------------

		protected virtual void Update()
		{
		}

		public Task StartAsync(CancellationToken cancellationToken)
        {
			_logger.LogInformation($"{ServiceName} is STARTED listening at {ListenPort}.");

			listener = new UdpClient(ListenPort);
			packetHandler = new QPacketHandlerPRUDP(listener, ServerPID, ListenPort, ServiceName);
			packetHandler.Updates.Add(() => Update());
			packetHandler.Trace("service.start", details: () => new { hosting = "hosted_timer", timer_period_ms = 1 });

			_timer = new Timer(Process, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
			return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
			_timer.Change(Timeout.Infinite, Timeout.Infinite);
			CurrentRecvTask = null;
			listener.Close();
			packetHandler.Trace("service.stop");

			_logger.LogInformation($"{ServiceName} is STOPPED.");

			await Task.Yield();
		}

		Task<UdpReceiveResult> CurrentRecvTask = null;

		protected void Process(object state)
		{
			var diagnostic = NetworkDiagnostics.Enabled;
			var started = diagnostic ? Stopwatch.GetTimestamp() : 0;
			var active = diagnostic ? Interlocked.Increment(ref _diagnosticCallbacks) : 0;
			if (active > 1)
				packetHandler.Trace("service.callback.overlap", details: () => new { active_callbacks = active });
			try
			{
				packetHandler.Update();

				// use non-blocking recieve
				if (CurrentRecvTask != null)
				{
					if (CurrentRecvTask.IsCanceled || CurrentRecvTask.IsFaulted)
					{
						packetHandler.Trace("udp.receive.fault", details: () => new
						{
							canceled = CurrentRecvTask?.IsCanceled,
							error_type = CurrentRecvTask?.Exception?.GetBaseException().GetType().Name
						});
						CurrentRecvTask = null;
					}
                    else if (CurrentRecvTask.IsCompleted)
                    {
                        var result = CurrentRecvTask.Result;
                        CurrentRecvTask = null;
                        packetHandler.ProcessPacket(result.Buffer, result.RemoteEndPoint);
                    }
                }

				if (CurrentRecvTask == null)
					CurrentRecvTask = listener.ReceiveAsync();
			}
			catch (Exception ex)
			{
				packetHandler.Trace("service.process.error", details: () => new { error_type = ex.GetType().Name });
				_logger.LogError(ex.Message + ex.StackTrace);
                CurrentRecvTask = null;
            }
			finally
			{
				if (diagnostic)
				{
					Interlocked.Decrement(ref _diagnosticCallbacks);
					var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
					if (active > 1 || elapsed >= 100)
						packetHandler.Trace("service.callback.complete", details: () => new { elapsed_ms = elapsed, active_at_entry = active });
				}
			}
		}
	}
}
