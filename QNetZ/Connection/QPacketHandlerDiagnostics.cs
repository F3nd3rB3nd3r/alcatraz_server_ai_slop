using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace QNetZ
{
    public partial class QPacketHandlerPRUDP
    {
        private readonly string diagnosticHandlerId = Guid.NewGuid().ToString("N");

        public void Trace(string eventName, QClient client = null, QPacket packet = null,
            IPEndPoint endpoint = null, Func<object> details = null)
        {
            NetworkDiagnostics.Record(eventName, () => new
            {
                handler = diagnosticHandlerId, service = SourceName, listen_port = Port,
                connection = client?.IDrecv, send_signature = client?.IDsend,
                pid = client?.PlayerInfo?.PID, rvcid = client?.PlayerInfo?.RVCID,
                state = client?.State.ToString(), stored_endpoint = client?.Endpoint?.ToString(),
                observed_endpoint = endpoint?.ToString(),
                packet = packet == null ? null : new
                {
                    type = packet.type.ToString(), flags = packet.flags?.Select(x => (int)x).ToArray(),
                    signature = packet.m_uiSignature, session = packet.m_bySessionID,
                    sequence = packet.uiSeqId, part = packet.m_byPartNumber,
                    source_vport = packet.m_oSourceVPort?.toByte(), destination_vport = packet.m_oDestinationVPort?.toByte(),
                    payload_length = packet.payload?.Length, wire_length = packet.realSize
                },
                details = details?.Invoke()
            });
        }

        private void SendDatagram(byte[] data, IPEndPoint endpoint, QPacket packet, string reason)
        {
            Trace("udp.send.attempt", packet: packet, endpoint: endpoint, details: () => new { reason, bytes = data.Length });
            try
            {
                var sent = UDP.Send(data, data.Length, endpoint);
                Trace("udp.send.complete", packet: packet, endpoint: endpoint, details: () => new { reason, bytes = sent });
            }
            catch (SocketException ex)
            {
                Trace("udp.send.error", packet: packet, endpoint: endpoint, details: () => new { reason, socket_error = (int)ex.SocketErrorCode });
                throw;
            }
        }
    }
}
