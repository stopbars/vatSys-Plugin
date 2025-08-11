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
        private static Dictionary<string, Dictionary<string, Stopbar>> _stopbars = new Dictionary<string, Dictionary<string, Stopbar>>();

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
        public Stopbar(string airport, string displayName, string barsId, bool initialState = true, bool autoRaise = true)
        {
            Airport = airport;
            DisplayName = displayName;
            BARSId = barsId;
            State = initialState;
            AutoRaise = autoRaise;
            AutoRaiseTimer = new Timer(45000);
            AutoRaiseTimer.AutoReset = false;
        }

        public string Airport { get; set; }

        public bool AutoRaise { get; set; }

        public string BARSId { get; set; }

        public string DisplayName { get; set; }

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