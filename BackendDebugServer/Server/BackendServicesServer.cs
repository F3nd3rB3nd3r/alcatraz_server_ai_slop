using QNetZ;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace BackendDebugServer
{
	public static class BackendServicesServer
	{
		public static readonly uint serverPID = 2;
		public static readonly object _sync = new object();
		public static bool _exit = false;
		public static UdpClient listener;
		public static ushort _skipNextNAT = 0xFFFF;
		public static QPacketHandlerPRUDP packetHandler;

		static Task<UdpReceiveResult> CurrentRecvTask = null;

		public static void Start()
		{
			_exit = false;
			new Thread(tMainThread).Start();
		}

		public static void Stop()
		{
			lock (_sync)
			{
				_exit = true;
			}
		}

		public static void tMainThread(object obj)
		{
			var listenPort = QConfiguration.Instance.BackendServiceServerPort;

			listener = new UdpClient(listenPort);
			packetHandler = new QPacketHandlerPRUDP(listener, serverPID, listenPort, "BackendServices");
			packetHandler.Updates.Add(() => NetworkPlayers.DropPlayers());
			packetHandler.Trace("service.start", details: () => new { hosting = "debug_thread" });

			WriteLog(1, $"Listening at port {listenPort}");

			while (true)
			{
				lock (_sync)
				{
					if (_exit)
						break;
				}

				try
				{
					packetHandler.Update();

					// use non-blocking recieve
					if (CurrentRecvTask != null)
					{
						if (CurrentRecvTask.IsCanceled || CurrentRecvTask.IsFaulted)
						{
							packetHandler.Trace("udp.receive.fault", details: () => new { canceled = CurrentRecvTask?.IsCanceled, error_type = CurrentRecvTask?.Exception?.GetBaseException().GetType().Name });
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

					Thread.Sleep(1);
				}
				catch (Exception ex)
				{
					packetHandler.Trace("service.process.error", details: () => new { error_type = ex.GetType().Name });
					WriteLog(1, "error - exception occured! " + ex.Message);
                    CurrentRecvTask = null;
                }
			}
			WriteLog(1, "Server stopped");

			CurrentRecvTask = null;
			listener.Close();
			packetHandler.Trace("service.stop");
		}

		public static void ProcessPacket(byte[] data, IPEndPoint ep)
		{
			packetHandler.ProcessPacket(data, ep);
		}

		private static void WriteLog(int priority, string s)
		{
			QLog.WriteLine(priority, "[BackendServices] " + s);
		}
	}
}
