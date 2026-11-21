using QNetZ;
using QNetZ.Attributes;
using QNetZ.DDL;
using QNetZ.Interfaces;
using RDVServices.DDL.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace RDVServices.Services
{
	[RMCService(RMCProtocolId.NATTraversalService)]
	public class NATTraversalService : RMCServiceBase
	{
		[RMCMethod(1)]
		public RMCResult RequestProbeInitiation(IEnumerable<StationURL> urlTargetList)
		{
			// urlTargetList contains all player urls (basicmcnally given by MatchMakingService.GetSessionURLs)
			// Server sends InitiateProbe to all players in that url with those URLs
			// Then clients communicate with each other...
			// Resolve the whole list first so an advertised alternate never adds a
			// fallback probe when an exact endpoint already covers the same peer.
			var targets = (urlTargetList ?? Array.Empty<StationURL>())
				.Select(url => (Url: url, Lookup: ResolveProbeTarget(url))).ToArray();
			var exactTargets = new HashSet<QClient>(targets
				.Where(x => x.Lookup.Resolution == "endpoint").Select(x => x.Lookup.Client));
			var fallbackTargets = new HashSet<QClient>();
			foreach (var target in targets)
			{
				var lookup = target.Lookup;
				var qclient = lookup.Client;
				string suppression = null;
				if (lookup.Resolution == "rvcid")
				{
					if (exactTargets.Contains(qclient)) suppression = "endpoint_available";
					else if (!fallbackTargets.Add(qclient)) suppression = "duplicate_target";
				}
				var queueProbe = qclient != null && suppression == null;
				var hasRvcid = target.Url?.Parameters?.TryGetValue("RVCID", out _) == true;
				bool? rvcidConflict = hasRvcid && qclient?.PlayerInfo != null
					? unchecked((uint)target.Url.Parameters["RVCID"]) != qclient.PlayerInfo.RVCID : (bool?)null;

				Context.Handler.Trace("nat.probe.lookup", Context.Client, endpoint: lookup.Endpoint, details: () => new
				{
					// Invalid address text is untrusted; retain the rejection reason
					// without logging arbitrary strings from malformed fields.
					target = lookup.Endpoint == null ? null : NetworkDiagnostics.Station(target.Url), found = qclient != null,
					target_connection = qclient?.IDrecv, target_pid = qclient?.PlayerInfo?.PID,
					target_rvcid = qclient?.PlayerInfo?.RVCID,
					resolution = lookup.Resolution, reason = lookup.Reason, eligible_candidates = lookup.Candidates,
					probe_queued = queueProbe, suppression, endpoint_rvcid_conflict = rvcidConflict
				});

				if (queueProbe)
				{
					var thisClient = Context.Client;
					var thisClientURL = new StationURL(
						"prudp",
						thisClient.Endpoint.Address.ToString(), 
						new Dictionary<string, int>() {
							{ "port", thisClient.Endpoint.Port },
							{ "RVCID", (int)thisClient.PlayerInfo.RVCID }
						});

					SendRMCCall(qclient, RMCProtocolId.NATTraversalService, 2, thisClientURL);
				}
			}

			return Error(0);
		}

		private bool IsCurrentProbeClient(QClient client)
		{
			var player = client?.PlayerInfo;
			return player != null && player.PID != 0 &&
				client.State == QClient.StateType.Active &&
				client.TimeSinceLastPacket <= Constants.ClientTimeoutSeconds &&
				ReferenceEquals(player.Client, client) &&
				Context.Handler.Clients.Contains(client) && NetworkPlayers.Players.Contains(player);
		}

		private (IPEndPoint Endpoint, QClient Client, string Resolution, string Reason, int Candidates) ResolveProbeTarget(StationURL url)
		{
			if (url?.Parameters == null || !url.Parameters.TryGetValue("port", out var port) ||
				port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort || !IPAddress.TryParse(url.Address, out var address))
				return (null, null, "none", "invalid_endpoint", 0);

			var endpoint = new IPEndPoint(address, port);
			var exact = Context.Handler.GetQClientByEndPoint(endpoint);
			if (exact != null)
				return (endpoint, exact, "endpoint", "endpoint_match", 0);

			if (!url.Parameters.TryGetValue("RVCID", out var rawRvcid) || rawRvcid == 0)
				return (endpoint, null, "none", "missing_or_zero_rvcid", 0);
			if (!IsCurrentProbeClient(Context.Client))
				return (endpoint, null, "none", "caller_not_current", 0);

			// StationURL stores integer parameters as signed values; RVCIDs use
			// all 32 bits. Only a current binding on this handler may be selected.
			var rvcid = unchecked((uint)rawRvcid);
			var candidates = Context.Handler.Clients.Where(client =>
				client?.PlayerInfo?.RVCID == rvcid && IsCurrentProbeClient(client)).ToArray();
			if (candidates.Length != 1)
				return (endpoint, null, "none", candidates.Length == 0 ? "no_current_rvcid_match" : "ambiguous_rvcid", candidates.Length);
			return (endpoint, candidates[0], "rvcid", "rvcid_match", 1);
		}

		[RMCMethod(2)] 
		public RMCResult InitiateProbe(StationURL urlStationToProbe)
		{
			UNIMPLEMENTED();
			return Error(0);
		}

		[RMCMethod(3)] 
		public RMCResult RequestProbeInitiationExt(IEnumerable<StationURL> urlTargetList, StationURL urlStationToProbe)
		{
			UNIMPLEMENTED();
			return Error(0);
		}

		[RMCMethod(4)] 
		public RMCResult ReportNATTraversalResult(uint cid, bool result)
		{
			Context.Handler.Trace("nat.result", Context.Client, details: () => new { cid, result });
			UNIMPLEMENTED();
			return Error(0);
		}

		[RMCMethod(5)] 
		public RMCResult ReportNATProperties(uint natmapping, uint natfiltering, uint rtt)
		{
			UNIMPLEMENTED();
			return Error(0);
		}

		[RMCMethod(6)] 
		public RMCResult GetRelaySignatureKey()
		{
			UNIMPLEMENTED();
			return Result(new RelaySignatureKey());
		}

		[RMCMethod(7)] 
		public RMCResult ReportNATTraversalResultDetail(uint cid, bool result, int detail, uint rtt)
		{
			Context.Handler.Trace("nat.result.detail", Context.Client, details: () => new { cid, result, detail, rtt });
			UNIMPLEMENTED();
			return Error(0);
		}

	}
}
