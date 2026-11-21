using QNetZ.DDL;

namespace DSFServices
{
	internal static class PlayerEndpoint
	{
		// Cleanup must not infer ownership from a shared address alone. Leave
		// incomplete URLs intact, and ignore type flags on the same endpoint.
		internal static bool Matches(StationURL candidate, StationURL current)
		{
			return candidate?.Parameters != null && current?.Parameters != null
				&& candidate.Compare(current)
				&& candidate.Parameters.TryGetValue("port", out var candidatePort)
				&& current.Parameters.TryGetValue("port", out var currentPort)
				&& candidatePort > 0 && candidatePort <= ushort.MaxValue
				&& candidatePort == currentPort;
		}
	}
}
