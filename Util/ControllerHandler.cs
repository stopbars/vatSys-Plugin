using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;

namespace BARS.Util
{
    public enum WindowType
    {
        Legacy,
        INTAS
    }

    public class ControllerHandler
    {
        // Debounce map to prevent rapid-fire user toggles causing network/state feedback loops
        private static readonly Dictionary<string, DateTime> _lastToggle = new Dictionary<string, DateTime>();

        private static readonly Logger _logger = new Logger("ControllerHandler");

        // Minimum interval between successive UI toggles of the same stopbar
        private static readonly TimeSpan _minToggleInterval = TimeSpan.FromMilliseconds(250);

        private static readonly object _toggleLock = new object();
        private static Dictionary<string, Dictionary<string, Stopbar>> _stopbars = new Dictionary<string, Dictionary<string, Stopbar>>();
        private static readonly Dictionary<string, Dictionary<string, string>> _leadOnIndex = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // Event for new stopbar registration
        public static event EventHandler<StopbarEventArgs> StopbarRegistered;

        private static IReadOnlyList<string> NormalizeLeadOnIds(IEnumerable<string> leadOnIds)
        {
            if (leadOnIds == null)
            {
                return Array.Empty<string>();
            }

            return leadOnIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string DescribeLeadOnIds(IReadOnlyCollection<string> leadOnIds)
        {
            if (leadOnIds == null || leadOnIds.Count == 0)
            {
                return "<none>";
            }

            return string.Join(", ", leadOnIds);
        }

        private static void IndexLeadOnIds(string airport, string parentBarsId, IReadOnlyList<string> leadOnIds)
        {
            if (leadOnIds == null || leadOnIds.Count == 0)
            {
                return;
            }

            if (!_leadOnIndex.TryGetValue(airport, out var index))
            {
                index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _leadOnIndex[airport] = index;
            }

            foreach (var id in leadOnIds)
            {
                index[id] = parentBarsId;
            }
        }

        public static bool IsLeadOnId(string airport, string barsId)
        {
            return _leadOnIndex.TryGetValue(airport, out var index) && index.ContainsKey(barsId);
        }

        public static event EventHandler<StopbarEventArgs> StopbarStateChanged;
        public static void NotifyStopbarStateChanged(Stopbar stopbar, WindowType windowType, bool fromNetwork = false)
        {
            StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, windowType, fromNetwork));
        }

        public static Stopbar GetStopbar(string airport, string barsId)
        {
            if (_stopbars.ContainsKey(airport) && _stopbars[airport].ContainsKey(barsId))
            {
                return _stopbars[airport][barsId];
            }

            return null;
        }

        public static List<Stopbar> GetStopbarsForAirport(string airport)
        {
            if (_stopbars.ContainsKey(airport))
            {
                return _stopbars[airport].Values.ToList();
            }

            return new List<Stopbar>();
        }

        /// <summary>
        /// Registers a stopbar in the system (legacy signature without LeadOnId)
        /// </summary>
        public static void RegisterStopbar(string airport, string displayName, string barsId, bool initialState = true, bool autoRaise = true)
        {
            RegisterStopbar(airport, displayName, barsId, (IEnumerable<string>)null, initialState, autoRaise);
        }

        /// <summary>
        /// Registers a stopbar with a single lead-on identifier (legacy signature).
        /// </summary>
        public static void RegisterStopbar(string airport, string displayName, string barsId, string leadOnId, bool initialState = true, bool autoRaise = true)
        {
            IEnumerable<string> leadOnIds = string.IsNullOrWhiteSpace(leadOnId) ? null : new[] { leadOnId };
            RegisterStopbar(airport, displayName, barsId, leadOnIds, initialState, autoRaise);
        }

        /// <summary>
        /// Registers a stopbar with zero or more lead-on identifiers.
        /// Lead-on state is defined as the logical inverse of the stopbar state.
        /// </summary>
        public static void RegisterStopbar(string airport, string displayName, string barsId, IEnumerable<string> leadOnIds, bool initialState = true, bool autoRaise = true)
        {
            var normalizedLeadOnIds = NormalizeLeadOnIds(leadOnIds);

            if (!_stopbars.ContainsKey(airport))
            {
                _stopbars[airport] = new Dictionary<string, Stopbar>();
            }

            if (!_stopbars[airport].ContainsKey(barsId))
            {
                var stopbar = new Stopbar(airport, displayName, barsId, normalizedLeadOnIds, initialState, autoRaise);
                stopbar.AutoRaiseTimer.Elapsed += (sender, e) => HandleAutoRaise(stopbar.Airport, stopbar.BARSId);
                _stopbars[airport][barsId] = stopbar;
                IndexLeadOnIds(airport, barsId, stopbar.LeadOnIds);
                _logger.Log($"Registered stopbar {barsId} for {airport} with initial state: {(initialState ? "ON" : "OFF")}, AutoRaise: {autoRaise}, LeadOns: {DescribeLeadOnIds(stopbar.LeadOnIds)}");
                StopbarRegistered?.Invoke(null, new StopbarEventArgs(stopbar, WindowType.Legacy));
                // If NetHandler is in deferred seed mode, inform it so it can seed this stopbar now
                var netHandler = NetManager.Instance.GetConnection(airport);
                if (netHandler != null)
                {
                    netHandler.NotifyStopbarRegistered(stopbar);
                }
            }
            else
            {
                // Already exists (likely from server INITIAL_STATE). Merge any newly supplied lead-ons.
                var existing = _stopbars[airport][barsId];
                bool leadOnAdded = existing.MergeLeadOnIds(normalizedLeadOnIds);
                IndexLeadOnIds(airport, barsId, existing.LeadOnIds);

                // Optionally update display name if differs (profile may have nicer name)
                if (!string.IsNullOrEmpty(displayName) && existing.DisplayName != displayName)
                {
                    existing.DisplayName = displayName;
                }

                if (leadOnAdded)
                {
                    _logger.Log($"Updated stopbar {barsId} for {airport} with additional lead-on(s): {DescribeLeadOnIds(existing.LeadOnIds)}");
                    var netHandler = NetManager.Instance.GetConnection(airport);
                    if (netHandler != null && netHandler.IsConnected())
                    {
                        _ = netHandler.UpdateStopbar(existing, true); // force leadOnState=false on first send for new lead-ons
                    }
                }
            }
        }

        /// <summary>
        /// Sets the state of a stopbar explicitly and raises the appropriate event
        /// </summary>
        /// <param name="airport">Airport ICAO code</param>
        /// <param name="barsId">Stopbar ID</param>
        /// <param name="state">Desired state (true = on, false = off)</param>
        /// <param name="windowType">Window type (Legacy or INTAS)</param>
        /// <param name="autoRaise">Whether to auto-raise the stopbar after 45 seconds if turned off</param>
        public static void SetStopbarState(string airport, string barsId, bool state, WindowType windowType, bool autoRaise = true)
        {
            var stopbar = GetStopbar(airport, barsId);
            if (stopbar != null)
            {
                if (stopbar.State != state || stopbar.AutoRaise != autoRaise)
                {
                    stopbar.State = state;
                    stopbar.AutoRaise = autoRaise;

                    _logger.Log($"Set stopbar {barsId} at {airport} to {(state ? "ON" : "OFF")}, AutoRaise: {autoRaise}");

                    HandleStopbarTimer(stopbar);

                    StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, windowType));

                    var netHandler = NetManager.Instance.GetConnection(airport);
                    if (netHandler != null)
                    {
                        _ = netHandler.UpdateStopbar(stopbar);
                    }
                }
            }
        }

        public static void ToggleStopbar(string airport, string barsId, WindowType windowType, bool autoRaise = true)
        {
            var stopbar = GetStopbar(airport, barsId);
            if (stopbar == null)
            {
                return;
            }

            var key = airport + "|" + barsId;
            lock (_toggleLock)
            {
                var now = DateTime.UtcNow;
                if (_lastToggle.TryGetValue(key, out var last) && (now - last) < _minToggleInterval)
                {
                    return; // Ignore rapid repeat
                }
                _lastToggle[key] = now;
            }

            stopbar.State = !stopbar.State;
            stopbar.AutoRaise = autoRaise;

            _logger.Log($"Toggled stopbar {barsId} at {airport} to {(stopbar.State ? "ON" : "OFF")}, AutoRaise: {autoRaise}");

            HandleStopbarTimer(stopbar);

            StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, windowType));

            var netHandler = NetManager.Instance.GetConnection(airport);
            if (netHandler != null)
            {
                _ = netHandler.UpdateStopbar(stopbar);
            }
        }

        private static void HandleAutoRaise(string airport, string barsId)
        {
            var stopbar = GetStopbar(airport, barsId);
            if (stopbar != null && !stopbar.State)
            {
                _logger.Log($"Auto-raising stopbar {barsId} at {airport}");

                stopbar.State = true;

                StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, WindowType.Legacy));
                StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, WindowType.INTAS));

                var netHandler = NetManager.Instance.GetConnection(airport);
                if (netHandler != null)
                {
                    _ = netHandler.UpdateStopbar(stopbar);
                }
            }
        }

        private static void HandleStopbarTimer(Stopbar stopbar)
        {
            stopbar.AutoRaiseTimer.Stop();

            if (!stopbar.State && stopbar.AutoRaise)
            {
                _logger.Log($"Starting auto-raise timer for stopbar {stopbar.BARSId} at {stopbar.Airport}. Will raise in 45 seconds.");
                stopbar.AutoRaiseTimer.Start();
            }
        }
    }

    public class Stopbar
    {
        private readonly List<string> _leadOnIds;

        public Stopbar(string airport, string displayName, string barsId, IEnumerable<string> leadOnIds = null, bool initialState = true, bool autoRaise = true)
        {
            Airport = airport;
            DisplayName = displayName;
            BARSId = barsId;
            State = initialState;
            AutoRaise = autoRaise;
            AutoRaiseTimer = new Timer(45000);
            AutoRaiseTimer.AutoReset = false;
            _leadOnIds = new List<string>();
            MergeLeadOnIds(leadOnIds);
        }

        public string Airport { get; set; }

        public bool AutoRaise { get; set; }

        public string BARSId { get; set; }

        /// <summary>
        /// Display name of the stopbar
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Collection of associated lead-on identifiers. Lead-on state is considered the inverse of the stopbar state.
        /// </summary>
        public IReadOnlyList<string> LeadOnIds => _leadOnIds;

        /// <summary>
        /// Adds any new lead-on identifiers to this stopbar. Returns true if at least one new identifier was added.
        /// </summary>
        public bool MergeLeadOnIds(IEnumerable<string> leadOnIds)
        {
            bool addedAny = false;
            if (leadOnIds == null)
            {
                return false;
            }

            foreach (var id in leadOnIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string normalized = id.Trim();
                bool alreadyPresent = _leadOnIds.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
                if (!alreadyPresent)
                {
                    _leadOnIds.Add(normalized);
                    addedAny = true;
                }
            }

            return addedAny;
        }

        /// <summary>
        /// Convenience helper to determine if this stopbar tracks a given lead-on identifier.
        /// </summary>
        public bool HasLeadOn(string leadOnId)
        {
            if (string.IsNullOrWhiteSpace(leadOnId))
            {
                return false;
            }

            string normalized = leadOnId.Trim();
            return _leadOnIds.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        }

        [Obsolete("Use LeadOnIds instead.")]
        public string LeadOnId
        {
            get => _leadOnIds.FirstOrDefault();
            set
            {
                _leadOnIds.Clear();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    MergeLeadOnIds(new[] { value });
                }
            }
        }

        public bool State { get; set; }

        internal Timer AutoRaiseTimer { get; set; }
    }

    public class StopbarEventArgs : EventArgs
    {
        public StopbarEventArgs(Stopbar stopbar, WindowType windowType, bool fromNetwork = false)
        {
            Stopbar = stopbar;
            WindowType = windowType;
            FromNetwork = fromNetwork;
        }

        public Stopbar Stopbar { get; private set; }
        public WindowType WindowType { get; private set; }
        public bool FromNetwork { get; private set; }
    }
}