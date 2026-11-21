using System;
using System.Collections.Generic;
using System.Linq;

namespace QNetZ
{
	public partial class QPacketHandlerPRUDP
	{
		// Administrative retention/resource limits, not client protocol constants.
		private const long FragmentLifetimeMilliseconds = 120000;
		private const int MaxFragmentPacketsPerOwner = 4096;
		private const int MaxFragmentPacketsPerHandler = 32768;
		private const long MaxFragmentBytesPerOwner = 32L * 1024 * 1024;
		private const long MaxFragmentBytesPerHandler = 128L * 1024 * 1024;
		private const int MaxFragmentStreamsPerOwner = 64;
		private const int MaxFragmentStreamsPerHandler = 1024;
		private const int FragmentDiagnosticSample = 32;
		private long NextFragmentSweep;
		private int FragmentPacketCount;
		private long FragmentPayloadBytes;

		// Keep arrival order alongside exact sequence and first-receipt indexes.
		private sealed class FragmentBuffer : List<QPacket>
		{
			public readonly Dictionary<ushort, QPacket> BySequence = new();
			public readonly Dictionary<QPacket, long> ReceivedAt = new();
			public long PayloadBytes;
		}

		private void AddFragment(FragmentBuffer buffer, QPacket packet, long receivedAt)
		{
			buffer.Add(packet);
			buffer.BySequence.Add(packet.uiSeqId, packet);
			buffer.ReceivedAt.Add(packet, receivedAt);
			buffer.PayloadBytes += packet.payload.Length;
			FragmentPacketCount++;
			FragmentPayloadBytes += packet.payload.Length;
		}

		private void RemoveFragments(FragmentBuffer buffer, QPacket[] packets)
		{
			foreach (var packet in packets)
			{
				buffer.Remove(packet);
				buffer.BySequence.Remove(packet.uiSeqId);
				buffer.ReceivedAt.Remove(packet);
				buffer.PayloadBytes -= packet.payload.Length;
				FragmentPacketCount--;
				FragmentPayloadBytes -= packet.payload.Length;
			}
		}

		private string FragmentCapacityRejection(QClient owner, int bytes, bool newStream)
		{
			int ownerPackets = 0, ownerStreams = 0;
			long ownerBytes = 0;
			foreach (var pair in FragmentBuffers)
			{
				if (!ReferenceEquals(pair.Key.Owner, owner)) continue;
				ownerPackets += pair.Value.Count;
				ownerStreams++;
				ownerBytes += pair.Value.PayloadBytes;
			}
			if (newStream && ownerStreams >= MaxFragmentStreamsPerOwner) return "owner_stream_limit";
			if (newStream && FragmentBuffers.Count >= MaxFragmentStreamsPerHandler) return "handler_stream_limit";
			if (ownerPackets >= MaxFragmentPacketsPerOwner) return "owner_packet_limit";
			if (FragmentPacketCount >= MaxFragmentPacketsPerHandler) return "handler_packet_limit";
			if (ownerBytes > MaxFragmentBytesPerOwner - bytes) return "owner_byte_limit";
			if (FragmentPayloadBytes > MaxFragmentBytesPerHandler - bytes) return "handler_byte_limit";
			return null;
		}

		private void ExpireFragmentBuffers(long now)
		{
			if (now < NextFragmentSweep) return;
			NextFragmentSweep = now + 1000;
			foreach (var pair in FragmentBuffers.ToArray())
			{
				var expired = pair.Value.Where(p => now - pair.Value.ReceivedAt[p] >= FragmentLifetimeMilliseconds).ToArray();
				if (expired.Length == 0) continue;
				Trace("fragment.expire", pair.Key.Owner, details: () => new
				{
					reason = "first_receipt_timeout", signature = pair.Key.Signature, session = pair.Key.Session,
					source_vport = pair.Key.Source, destination_vport = pair.Key.Destination,
					expired_packets = expired.Length, expired_bytes = expired.Sum(p => (long)p.payload.Length),
					retained_packets = pair.Value.Count - expired.Length, lifetime_ms = FragmentLifetimeMilliseconds
				});
				RemoveFragments(pair.Value, expired);
				if (pair.Value.Count == 0) FragmentBuffers.Remove(pair.Key);
			}
		}
	}
}
