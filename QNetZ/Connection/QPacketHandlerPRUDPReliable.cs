using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace QNetZ
{
	class QPacketState
	{
		public QPacketState(QPacket p)
		{
			Packet = p;
			ReSendCount = 0;
		}
		public QPacket Packet;
		public int ReSendCount;
	}

	class QReliableResponse
	{
		public QReliableResponse(QPacket srcPacket, IPEndPoint endpoint)
		{
			SrcPacket = srcPacket;
			ResponseList = new List<QPacketState>();
			DropTime = DateTime.UtcNow.AddSeconds(18);
			ResendTime = DateTime.UtcNow;
			Endpoint = endpoint;
		}

		public QPacket SrcPacket;
		public List<QPacketState> ResponseList;

		public DateTime DropTime;       // if ACKs not recieved, in this time it will be dropped
		public DateTime ResendTime;
		public IPEndPoint Endpoint;     // client endpoint
	}

	//-----------------------------------------------------

	public partial class QPacketHandlerPRUDP
	{
		bool Defrag(QClient client, QPacket packet)
		{
			if (packet.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
				return true;

			if (!packet.flags.Contains(QPacket.PACKETFLAG.FLAG_RELIABLE))
				return true;

			// A handler serves several peers and virtual streams. Keep pending
			// packets with the transport object and the complete incoming stream.
			var key = (client, packet.m_uiSignature, packet.m_bySessionID,
				packet.m_oSourceVPort?.toByte(), packet.m_oDestinationVPort?.toByte());
			if (!FragmentBuffers.TryGetValue(key, out var accumulated))
			{
				accumulated = new List<QPacket>();
				FragmentBuffers.Add(key, accumulated);
			}
			Trace("fragment.receive", client, packet, details: () => new
			{
				buffer_scope = "connection_session_stream", pending_streams = FragmentBuffers.Count,
				buffer = accumulated.Select(x => new {
					signature = x.m_uiSignature, session = x.m_bySessionID, sequence = x.uiSeqId, part = x.m_byPartNumber,
					source_vport = x.m_oSourceVPort?.toByte(), destination_vport = x.m_oDestinationVPort?.toByte()
				}).ToArray()
			});

			if (!accumulated.Any(x =>
				 x.uiSeqId == packet.uiSeqId &&
				 x.checkSum == packet.checkSum &&
				 x.m_byPartNumber == packet.m_byPartNumber &&
				 x.checkSum == packet.checkSum &&
				 x.m_bySessionID == packet.m_bySessionID &&
				 x.m_uiSignature == packet.m_uiSignature
				))
			{
				accumulated.Add(packet);
			}

			if (packet.m_byPartNumber != 0)
			{
				Trace("fragment.wait", client, packet);
				// add and don't process
				return false;
			}

			// got last fragment, assemble
			var orderedFragments = accumulated.OrderBy(x => x.uiSeqId);
			int numPackets = 0;
			foreach (var fragPacket in orderedFragments)
			{
				numPackets++;
				if (fragPacket.m_byPartNumber == 0)
					break;
			}

			var fragments = orderedFragments.Take(numPackets).ToArray();
			Trace("fragment.selection", client, packet, details: () => new
			{
				selected = fragments.Select(x => new {
					signature = x.m_uiSignature, session = x.m_bySessionID, sequence = x.uiSeqId, part = x.m_byPartNumber,
					source_vport = x.m_oSourceVPort?.toByte(), destination_vport = x.m_oDestinationVPort?.toByte()
				}).ToArray(),
				cleared_count = accumulated.Count, retained_streams = FragmentBuffers.Count - 1
			});

			// Preserve the existing within-stream clearing/validation behavior,
			// including rejection, without discarding another peer or stream.
			FragmentBuffers.Remove(key);

			if (numPackets > 1)
			{
				// validate algorightm above
				ushort seqId = fragments.First().uiSeqId;
				int nfrag = 1;
				foreach (var fragPacket in fragments)
				{
					//if(fragPacket.uiSeqId != seqId)
					//{
					//	QLog.WriteLine(1, "ERROR : packet sequence mismatch!");
					//	//return false;
					//}

					if (fragments.Length == nfrag && fragPacket.m_byPartNumber != 0)
					{
						Trace("fragment.reject", client, packet, details: () => new { reason = "missing_final_part" });
						QLog.WriteLine(1, "ERROR : packet sequence does not end with 0 - call a programmer!");
						return false;
					}

					if (!(fragPacket.m_byPartNumber == 0 && fragments.Length == nfrag))
					{
						if (fragPacket.m_byPartNumber != nfrag)
						{
							Trace("fragment.reject", client, packet, details: () => new { reason = "insufficient_parts", expected_part = nfrag });
							QLog.WriteLine(1, "ERROR : insufficient packet fragments - call a programmer!");
							return false;
						}
					}

					seqId++;
					nfrag++;
				}

				// acks are required for each packet
				foreach (var fragPacket in fragments)
				{
					if (fragPacket.flags.Contains(QPacket.PACKETFLAG.FLAG_NEED_ACK))
						SendACK(fragPacket, client);
				}

				var fullPacketData = new MemoryStream();
				foreach (var fragPacket in fragments)
					fullPacketData.Write(fragPacket.payload, 0, fragPacket.payload.Length);

				// replace packet payload with defragmented data
				packet.payload = fullPacketData.ToArray();
				packet.payloadSize = (ushort)fullPacketData.Length;

				QLog.WriteLine(10, $"Defragmented sequence of {numPackets} packets !\n");
			}

			Trace("fragment.complete", client, packet, details: () => new { fragments = numPackets });
			return true;
		}

		private void DropFragmentBuffers(QClient client, string reason)
		{
			foreach (var key in FragmentBuffers.Keys.Where(key => ReferenceEquals(key.Owner, client)).ToArray())
			{
				Trace("fragment.drop", client, details: () => new
				{
					reason, signature = key.Signature, session = key.Session,
					source_vport = key.Source, destination_vport = key.Destination,
					pending = FragmentBuffers[key].Count
				});
				FragmentBuffers.Remove(key);
			}
		}

		// acknowledges packet
		private static bool MatchesAck(QReliableResponse cache, QPacket response, QClient client, QPacket ack)
		{
			// Incoming signatures identify the server-side connection; outgoing
			// signatures identify its peer. They must not be compared directly.
			return cache.Endpoint.Equals(client.Endpoint) &&
				response.m_uiSignature == client.IDsend &&
				response.m_bySessionID == ack.m_bySessionID &&
				response.uiSeqId == ack.uiSeqId &&
				response.m_oSourceVPort?.Equals(ack.m_oDestinationVPort) == true &&
				response.m_oDestinationVPort?.Equals(ack.m_oSourceVPort) == true;
		}

		void OnGotAck(QClient client, QPacket ackPacket)
		{
			lock (CachedResponses)
			{
				Trace("ack.matches", client, ackPacket, details: () => new
				{
					matches = CachedResponses.SelectMany(cr => cr.ResponseList
						.Where(x => MatchesAck(cr, x.Packet, client, ackPacket))
						.Select(x => new { destination = cr.Endpoint.ToString(), response_signature = x.Packet.m_uiSignature, sequence = x.Packet.uiSeqId })).ToArray()
				});
				foreach (var cr in CachedResponses)
				{
					cr.ResponseList.RemoveAll(x =>
						MatchesAck(cr, x.Packet, client, ackPacket));
				}

				CachedResponses.RemoveAll(x => !x.ResponseList.Any());
			}
		}

		// returns response cache list by request packet
		QReliableResponse GetCachedResponseByRequestPacket(QPacket packet, IPEndPoint endpoint)
		{
			if (packet == null)
				return null;

			if (packet.m_oSourceVPort == null)
			{
				QLog.WriteLine(1, "GetCachedResponseByRequestPacket - invalid packet SRC VPORT!\n");
				return null;
			}

			if (packet.m_oDestinationVPort == null)
			{
				QLog.WriteLine(1, "GetCachedResponseByRequestPacket - invalid packet DEST VPORT!\n");
				return null;
			}

			lock (CachedResponses)
			{
				// delete all invalid messages
				CachedResponses.RemoveAll(x =>
					x.SrcPacket == null ||
					x.SrcPacket.m_oSourceVPort == null ||
					x.SrcPacket.m_oDestinationVPort == null);

				return CachedResponses.FirstOrDefault(cr =>
						cr.Endpoint.Equals(endpoint) &&
						cr.SrcPacket.type == packet.type &&
						cr.SrcPacket.m_uiSignature == packet.m_uiSignature &&
						cr.SrcPacket.m_oSourceVPort.type == packet.m_oSourceVPort.type &&
						cr.SrcPacket.m_oSourceVPort.port == packet.m_oSourceVPort.port &&
						cr.SrcPacket.m_oDestinationVPort.type == packet.m_oDestinationVPort.type &&
						cr.SrcPacket.m_oDestinationVPort.port == packet.m_oDestinationVPort.port &&
						cr.SrcPacket.uiSeqId == packet.uiSeqId &&
						cr.SrcPacket.checkSum == packet.checkSum);
			}
		}

		// Caches the response which is going to be sent
		bool CacheResponse(QPacket requestPacket, QPacket responsePacket, IPEndPoint ep)
		{
			// only DATA can be reliable
			if (responsePacket.type != QPacket.PACKETTYPE.DATA)
				return false;

			// don't cache non-reliable packets
			if (!responsePacket.flags.Contains(QPacket.PACKETFLAG.FLAG_RELIABLE))
				return false;

			if (responsePacket.flags.Contains(QPacket.PACKETFLAG.FLAG_ACK))
				return false;

			// Server-initiated RMC calls use synthetic request headers shared by
			// several clients. Never reuse another recipient's destination/cache.
			var cache = GetCachedResponseByRequestPacket(requestPacket, ep);
			if (cache == null)
			{
				lock (CachedResponses)
				{
					cache = new QReliableResponse(requestPacket, ep);
					CachedResponses.Add(cache);
				}
			}
			else
			{
				QLog.WriteLine(10, "[QPacketReliable] Found cached request");
			}

			lock (cache.ResponseList)
			{
				cache.ResponseList.Add(new QPacketState(responsePacket));
				Trace("cache.enqueue", packet: responsePacket, endpoint: ep, details: () => new
				{
					cached_destination = cache.Endpoint.ToString(), destination_matches = cache.Endpoint.Equals(ep),
					request_signature = requestPacket?.m_uiSignature, request_session = requestPacket?.m_bySessionID,
					request_sequence = requestPacket?.uiSeqId, expires_utc = cache.DropTime, pending = cache.ResponseList.Count
				});
			}
			return true;
		}

		private void RetrySend(QReliableResponse cache)
		{
			int minResendTimes = 0;
			lock(cache.ResponseList)
            {
                QLog.WriteLine(5, $"Retry send {cache.ResponseList.Count} reliable packets...");
                foreach (var crp in cache.ResponseList)
				{
					var data = crp.Packet.toBuffer();
					Trace("cache.transmit", packet: crp.Packet, endpoint: cache.Endpoint, details: () => new
					{
						prior_send_count = crp.ReSendCount, expires_utc = cache.DropTime, due_utc = cache.ResendTime,
						expired = DateTime.UtcNow >= cache.DropTime
					});
					SendDatagram(data, cache.Endpoint, crp.Packet, "reliable");

					crp.ReSendCount++;
					minResendTimes = Math.Min(minResendTimes, crp.ReSendCount);
				}
			}
			cache.ResendTime = DateTime.UtcNow.AddMilliseconds(Constants.PacketResendTimeSeconds * 1000 + minResendTimes * 250);
		}

		private void CheckResendPackets()
		{
			QReliableResponse[] reliableResend;
			lock (CachedResponses)
			{
				reliableResend = CachedResponses.Where(x => x.SrcPacket != null && DateTime.UtcNow >= x.ResendTime).ToArray();
				if (NetworkDiagnostics.Enabled)
					foreach (var expiring in CachedResponses.Where(x => x.SrcPacket == null || DateTime.UtcNow >= x.DropTime))
						Trace("cache.expire", endpoint: expiring.Endpoint, details: () => new { pending = expiring.ResponseList.Count, expires_utc = expiring.DropTime });
				int droppedPackets = CachedResponses.RemoveAll(x => x.SrcPacket == null || DateTime.UtcNow >= x.DropTime);

				if(droppedPackets > 0)
					QLog.WriteLine(5, $"Dropped {droppedPackets} reliable packets");
            }
			foreach (var crp in reliableResend)
				RetrySend(crp);
		}
	}
}
