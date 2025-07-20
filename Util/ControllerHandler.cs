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
        private static readonly Logger _logger = new Logger("ControllerHandler");
        private static NetHandler _netHandler;
        private static Dictionary<string, Dictionary<string, Stopbar>> _stopbars = new Dictionary<string, Dictionary<string, Stopbar>>();

        // Event for stopbar state changes
        public static event EventHandler<StopbarEventArgs> StopbarStateChanged;

        /// <summary>
        /// Gets a stopbar by airport and BARS ID
        /// </summary>
        public static Stopbar GetStopbar(string airport, string barsId)
        {
            if (_stopbars.ContainsKey(airport) && _stopbars[airport].ContainsKey(barsId))
            {
                return _stopbars[airport][barsId];
            }

            return null;
        }

        /// <summary>
        /// Gets all stopbars for a specific airport
        /// </summary>
        public static List<Stopbar> GetStopbarsForAirport(string airport)
        {
            if (_stopbars.ContainsKey(airport))
            {
                return _stopbars[airport].Values.ToList();
            }

            return new List<Stopbar>();
        }

        /// <summary>
        /// Registers a stopbar in the system
        /// </summary>
        public static void RegisterStopbar(string airport, string displayName, string barsId, bool initialState = true, bool autoRaise = true)
        {
            if (!_stopbars.ContainsKey(airport))
            {
                _stopbars[airport] = new Dictionary<string, Stopbar>();
            }

            if (!_stopbars[airport].ContainsKey(barsId))
            {
                var stopbar = new Stopbar(airport, displayName, barsId, initialState, autoRaise);
                stopbar.AutoRaiseTimer.Elapsed += (sender, e) => HandleAutoRaise(stopbar.Airport, stopbar.BARSId);
                _stopbars[airport][barsId] = stopbar;
                _logger.Log($"Registered stopbar {barsId} for {airport} with initial state: {(initialState ? "ON" : "OFF")}, AutoRaise: {autoRaise}");
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

                    // Handle auto-raise timer
                    HandleStopbarTimer(stopbar);

                    // Raise the event
                    StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, windowType));

                    // Send update to network via NetManager
                    var netHandler = NetManager.Instance.GetConnection(airport);
                    if (netHandler != null)
                    {
                        _ = netHandler.UpdateStopbar(stopbar);
                    }
                }
            }
        }

        /// <summary>
        /// Toggles the state of a stopbar and raises the appropriate event
        /// </summary>
        /// <param name="airport">Airport ICAO code</param>
        /// <param name="barsId">Stopbar ID</param>
        /// <param name="windowType">Window type (Legacy or INTAS)</param>
        /// <param name="autoRaise">Whether to auto-raise the stopbar after 45 seconds if turned off</param>
        public static void ToggleStopbar(string airport, string barsId, WindowType windowType, bool autoRaise = true)
        {
            var stopbar = GetStopbar(airport, barsId);
            if (stopbar != null)
            {
                // Toggle the state
                stopbar.State = !stopbar.State;
                stopbar.AutoRaise = autoRaise;

                _logger.Log($"Toggled stopbar {barsId} at {airport} to {(stopbar.State ? "ON" : "OFF")}, AutoRaise: {autoRaise}");

                // Handle auto-raise timer
                HandleStopbarTimer(stopbar);

                // Raise the event
                StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, windowType));

                // Send update to network via NetManager
                var netHandler = NetManager.Instance.GetConnection(airport);
                if (netHandler != null)
                {
                    _ = netHandler.UpdateStopbar(stopbar);
                }
            }
        }

        /// <summary>
        /// Auto-raise handler when timer elapses
        /// </summary>
        private static void HandleAutoRaise(string airport, string barsId)
        {
            var stopbar = GetStopbar(airport, barsId);
            if (stopbar != null && !stopbar.State)
            {
                _logger.Log($"Auto-raising stopbar {barsId} at {airport}");

                // Set state to ON (true)
                stopbar.State = true;

                // Raise events for both window types to ensure all windows update
                StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, WindowType.Legacy));
                StopbarStateChanged?.Invoke(null, new StopbarEventArgs(stopbar, WindowType.INTAS));

                // Send update to network via NetManager
                var netHandler = NetManager.Instance.GetConnection(airport);
                if (netHandler != null)
                {
                    _ = netHandler.UpdateStopbar(stopbar);
                }
            }
        }

        /// <summary>
        /// Handles the auto-raise timer for a stopbar
        /// </summary>
        private static void HandleStopbarTimer(Stopbar stopbar)
        {
            // If the timer is already running, stop it first (reset the timer)
            stopbar.AutoRaiseTimer.Stop();

            // Only start timer if the stopbar is OFF (false) and AutoRaise is enabled
            if (!stopbar.State && stopbar.AutoRaise)
            {
                _logger.Log($"Starting auto-raise timer for stopbar {stopbar.BARSId} at {stopbar.Airport}. Will raise in 45 seconds.");
                stopbar.AutoRaiseTimer.Start();
            }
        }
    }

    /// <summary>
    /// Represents a stopbar in the BARS system
    /// </summary>
    public class Stopbar
    {
        public Stopbar(string airport, string displayName, string barsId, bool initialState = true, bool autoRaise = true)
        {
            Airport = airport;
            DisplayName = displayName;
            BARSId = barsId;
            State = initialState;
            AutoRaise = autoRaise;
            AutoRaiseTimer = new Timer(45000); // 45 second timer
            AutoRaiseTimer.AutoReset = false;
        }

        /// <summary>
        /// Airport ICAO code
        /// </summary>
        public string Airport { get; set; }

        /// <summary>
        /// Whether this stopbar should auto-raise after being lowered
        /// </summary>
        public bool AutoRaise { get; set; }

        /// <summary>
        /// Unique identifier for the stopbar
        /// </summary>
        public string BARSId { get; set; }

        /// <summary>
        /// Display name of the stopbar
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Current state of the stopbar (true = on, false = off)
        /// </summary>
        public bool State { get; set; }

        // Timer for auto-raise functionality
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