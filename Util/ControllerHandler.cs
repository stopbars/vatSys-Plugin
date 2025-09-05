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

        // Event for new stopbar registration
        public static event EventHandler<StopbarEventArgs> StopbarRegistered;

        public static event EventHandler<StopbarEventArgs> StopbarStateChanged;

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
            RegisterStopbar(airport, displayName, barsId, null, initialState, autoRaise);
        }

        /// <summary>
        /// Registers a stopbar with an optional associated lead-on light.
        /// Lead-on state is defined as the logical inverse of the stopbar state.
        /// </summary>
        public static void RegisterStopbar(string airport, string displayName, string barsId, string leadOnId, bool initialState = true, bool autoRaise = true)
        {
            if (!_stopbars.ContainsKey(airport))
            {
                _stopbars[airport] = new Dictionary<string, Stopbar>();
            }

            if (!_stopbars[airport].ContainsKey(barsId))
            {
                var stopbar = new Stopbar(airport, displayName, barsId, leadOnId, initialState, autoRaise);
                stopbar.AutoRaiseTimer.Elapsed += (sender, e) => HandleAutoRaise(stopbar.Airport, stopbar.BARSId);
                _stopbars[airport][barsId] = stopbar;
                _logger.Log($"Registered stopbar {barsId} for {airport} with initial state: {(initialState ? "ON" : "OFF")}, AutoRaise: {autoRaise}, LeadOn: {(!string.IsNullOrEmpty(leadOnId) ? leadOnId : "<none>")}");
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
                // Already exists (likely from server INITIAL_STATE). If we now have a LeadOnId from profile and it wasn't set, update it.
                var existing = _stopbars[airport][barsId];
                bool leadOnAdded = false;
                if (!string.IsNullOrEmpty(leadOnId) && string.IsNullOrEmpty(existing.LeadOnId))
                {
                    existing.LeadOnId = leadOnId;
                    leadOnAdded = true;
                    _logger.Log($"Updated stopbar {barsId} for {airport} with late LeadOnId: {leadOnId} (profile loaded after initial registration).");
                }
                // Optionally update display name if differs (profile may have nicer name)
                if (!string.IsNullOrEmpty(displayName) && existing.DisplayName != displayName)
                {
                    existing.DisplayName = displayName;
                }
                // If we added a lead-on id, push a fresh state update so server now knows inverse pair.
                if (leadOnAdded)
                {
                    var netHandler = NetManager.Instance.GetConnection(airport);
                    if (netHandler != null && netHandler.IsConnected())
                    {
                        _ = netHandler.UpdateStopbar(existing, true); // force leadOnState=false on first send
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
            if (stopbar != null)
            {
                var key = airport + "|" + barsId;
                lock (_toggleLock)
                {
                    if (_lastToggle.TryGetValue(key, out var last) && (DateTime.UtcNow - last) < _minToggleInterval)
                    {
                        return; // Ignore rapid repeat
                    }
                    _lastToggle[key] = DateTime.UtcNow;
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
        public Stopbar(string airport, string displayName, string barsId, string leadOnId = null, bool initialState = true, bool autoRaise = true)
        {
            Airport = airport;
            DisplayName = displayName;
            BARSId = barsId;
            LeadOnId = leadOnId;
            State = initialState;
            AutoRaise = autoRaise;
            AutoRaiseTimer = new Timer(45000);
            AutoRaiseTimer.AutoReset = false;
        }

        public string Airport { get; set; }

        public bool AutoRaise { get; set; }

        public string BARSId { get; set; }

        /// <summary>
        /// Display name of the stopbar
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Optional lead-on identifier. Lead-on is considered ON when stopbar is OFF.
        /// </summary>
        public string LeadOnId { get; set; }

        public bool State { get; set; }

        internal Timer AutoRaiseTimer { get; set; }
    }

    public class StopbarEventArgs : EventArgs
    {
        public StopbarEventArgs(Stopbar stopbar, WindowType windowType)
        {
            Stopbar = stopbar;
            WindowType = windowType;
        }

        public Stopbar Stopbar { get; private set; }
        public WindowType WindowType { get; private set; }
    }
}