using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace BARS.Util
{
    internal static class PilotPresenceStore
    {
        private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(90);
        private static readonly ConcurrentDictionary<string, AirportPilotSnapshot> Snapshots =
            new ConcurrentDictionary<string, AirportPilotSnapshot>(StringComparer.OrdinalIgnoreCase);

        public static bool UpdateAirport(string airport, IEnumerable<string> callsigns)
        {
            if (string.IsNullOrWhiteSpace(airport))
            {
                return false;
            }

            var normalizedCallsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (callsigns != null)
            {
                foreach (string callsign in callsigns)
                {
                    string normalized = NormalizeCallsign(callsign);
                    if (normalized != null)
                    {
                        normalizedCallsigns.Add(normalized);
                    }
                }
            }

            string key = airport.Trim().ToUpperInvariant();
            var updatedSnapshot = new AirportPilotSnapshot(normalizedCallsigns, DateTime.UtcNow);
            while (true)
            {
                if (!Snapshots.TryGetValue(key, out AirportPilotSnapshot existingSnapshot))
                {
                    if (Snapshots.TryAdd(key, updatedSnapshot))
                    {
                        return normalizedCallsigns.Count > 0;
                    }

                    continue;
                }

                bool changed = !existingSnapshot.Callsigns.SetEquals(normalizedCallsigns) ||
                               (updatedSnapshot.UpdatedAtUtc - existingSnapshot.UpdatedAtUtc) > PresenceTtl;
                if (Snapshots.TryUpdate(key, updatedSnapshot, existingSnapshot))
                {
                    return changed;
                }
            }
        }

        public static bool IsOnline(string callsign)
        {
            string normalized = NormalizeCallsign(callsign);
            if (normalized == null)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            foreach (AirportPilotSnapshot snapshot in Snapshots.Values)
            {
                if ((now - snapshot.UpdatedAtUtc) <= PresenceTtl && snapshot.Callsigns.Contains(normalized))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool ClearAirport(string airport)
        {
            if (!string.IsNullOrWhiteSpace(airport))
            {
                return Snapshots.TryRemove(airport.Trim().ToUpperInvariant(), out _);
            }

            return false;
        }

        public static bool ClearAll()
        {
            bool changed = !Snapshots.IsEmpty;
            Snapshots.Clear();
            return changed;
        }

        private static string NormalizeCallsign(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign))
            {
                return null;
            }

            return callsign.Trim().ToUpperInvariant();
        }

        private sealed class AirportPilotSnapshot
        {
            public AirportPilotSnapshot(HashSet<string> callsigns, DateTime updatedAtUtc)
            {
                Callsigns = callsigns;
                UpdatedAtUtc = updatedAtUtc;
            }

            public HashSet<string> Callsigns { get; }
            public DateTime UpdatedAtUtc { get; }
        }
    }
}
