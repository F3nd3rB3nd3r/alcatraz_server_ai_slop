using System.Linq;
using QNetZ;
using DSFServices.DDL.Models;

namespace DSFServices
{
    public static class MultiplayerDiagnostics
    {
        // Read existing registries only. Do not call GameData(), which can create
        // a data store, or serialize any service arguments or free-text fields.
        public static object Snapshot() => new
        {
            player_count = NetworkPlayers.Players.Count,
            gathering_count = PartySessions.GatheringList.Count,
            session_count = GameSessions.SessionList.Count,
            truncated = PartySessions.GatheringList.Count > 64 || GameSessions.SessionList.Count > 64,
            gatherings = PartySessions.GatheringList.Take(64).Select(x => new
            {
                id = x.Session.m_idMyself, owner = x.Session.m_pidOwner, host = x.Session.m_pidHost,
                state = x.Session.m_uiState, participants = x.Participants.Take(64).ToArray(),
                urls = x.Urls.Take(32).Select(NetworkDiagnostics.Station).ToArray()
            }).ToArray(),
            sessions = GameSessions.SessionList.Take(64).Select(x => new
            {
                id = x.Id, type = x.TypeID, host = x.HostPID,
                public_slots = Attribute(x, GameSessionAttributeType.PublicSlots),
                private_slots = Attribute(x, GameSessionAttributeType.PrivateSlots),
                filled_public_slots = Attribute(x, GameSessionAttributeType.FilledPublicSlots),
                filled_private_slots = Attribute(x, GameSessionAttributeType.FilledPrivateSlots),
                private_participants = x.Participants.Take(64).ToArray(),
                public_participants = x.PublicParticipants.Take(64).ToArray(),
                urls = x.HostURLs.Take(32).Select(NetworkDiagnostics.Station).ToArray()
            }).ToArray()
        };

        private static uint? Attribute(GameSessionData session, GameSessionAttributeType type) =>
            session.Attributes.TryGetValue((uint)type, out var value) ? (uint?)value : null;

        public static void Participation(string eventName, PlayerInfo player, uint? gathering, uint? session, uint? type)
        {
            NetworkDiagnostics.Record(eventName, () => new
            {
                pid = player.PID, rvcid = player.RVCID, connection = player.Client?.IDrecv,
                gathering, session, session_type = type, state = Snapshot()
            });
        }
    }
}
