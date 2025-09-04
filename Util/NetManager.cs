using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BARS.Util
{
    public class NetManager
    {
        private static NetManager _instance;
        private static readonly object _lock = new object();
        private readonly Dictionary<string, NetHandler> _connections;
        private readonly Dictionary<string, CancellationTokenSource> _disconnectGrace = new Dictionary<string, CancellationTokenSource>();
        private readonly Dictionary<string, NetHandler.ConnectionEventHandler> _connChangedSubscriptions = new Dictionary<string, NetHandler.ConnectionEventHandler>();
        private const int DISCONNECT_GRACE_MS = 10000; // 10 seconds grace on disconnect
        private readonly Logger _logger = new Logger("NetManager");
        private string _apiKey;
        private CancellationTokenSource _apiUpdateDebounceCts;

        private NetManager()
        {
            _connections = new Dictionary<string, NetHandler>();
        }

        public static NetManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    if (_instance == null)
                    {
                        _instance = new NetManager();
                    }
                }
                return _instance;
            }
        }

        public async Task<NetHandler> ConnectAirport(string airport, string controllerId)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                throw new InvalidOperationException("NetManager must be initialized with an API key first");
            }

            if (_connections.TryGetValue(airport, out NetHandler existingHandler))
            {
                if (existingHandler.IsConnected())
                {
                    return existingHandler;
                }
                else
                {
                    await DisconnectAirport(airport);
                }
            }

            var handler = new NetHandler(airport);
            handler.Initialize(_apiKey, airport, controllerId);

            bool connected = await handler.Connect();
            if (connected)
            {
                _connections[airport] = handler;

                // Subscribe to connection change events to implement a disconnect grace period
                if (_connChangedSubscriptions.ContainsKey(airport))
                {
                    // Clean any previous subscription just in case
                    try { handler.OnConnectionChanged -= _connChangedSubscriptions[airport]; } catch { }
                    _connChangedSubscriptions.Remove(airport);
                }
                NetHandler.ConnectionEventHandler del = (s, isConnected) =>
                {
                    _ = HandleConnectionChangedAsync(airport, isConnected);
                };
                handler.OnConnectionChanged += del;
                _connChangedSubscriptions[airport] = del;
                return handler;
            }

            return null;
        }

        public async Task DisconnectAirport(string airport)
        {
            if (_connections.TryGetValue(airport, out NetHandler handler))
            {
                // Cancel any pending disconnect grace timers and unsubscribe events
                if (_disconnectGrace.TryGetValue(airport, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                    _disconnectGrace.Remove(airport);
                }
                if (_connChangedSubscriptions.TryGetValue(airport, out var del))
                {
                    try { handler.OnConnectionChanged -= del; } catch { }
                    _connChangedSubscriptions.Remove(airport);
                }
                await handler.Disconnect();
                _connections.Remove(airport);
            }
        }

        public async Task DisconnectAll()
        {
            foreach (var handler in _connections.Values)
            {
                await handler.Disconnect();
            }
            _connections.Clear();
        }

        public IEnumerable<string> GetConnectedAirports()
        {
            return _connections.Keys;
        }

        public NetHandler GetConnection(string airport)
        {
            _connections.TryGetValue(airport, out NetHandler handler);
            return handler;
        }

        public void Initialize(string apiKey)
        {
            _apiKey = apiKey;
        }

        /// <summary>
        /// Update the API key at runtime and (debounced) reinitialize existing connections to use it
        /// without requiring a full application restart.
        /// </summary>
        public Task UpdateApiKeyAsync(string newApiKey, int debounceMs = 600)
        {
            // If unchanged, nothing to do
            if (string.Equals(_apiKey, newApiKey, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            _apiKey = newApiKey;

            // Debounce reconnection so we don't reconnect on every keystroke
            _apiUpdateDebounceCts?.Cancel();
            _apiUpdateDebounceCts = new CancellationTokenSource();
            var token = _apiUpdateDebounceCts.Token;

            return Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(debounceMs, token);
                }
                catch (TaskCanceledException)
                {
                    return; // superseded by a newer key update
                }

                // Snapshot current handlers to avoid collection modification issues
                List<NetHandler> handlers;
                lock (_lock)
                {
                    handlers = new List<NetHandler>(_connections.Values);
                }

                var tasks = new List<Task>();
                foreach (var handler in handlers)
                {
                    // Apply the new key and reconnect if needed
                    tasks.Add(handler.ApplyNewApiKey(newApiKey));
                }
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    // Best-effort; individual handlers log their own errors
                }
            }, token);
        }

        public bool IsAirportConnected(string airport)
        {
            return _connections.TryGetValue(airport, out NetHandler handler) && handler.IsConnected();
        }

        private async Task HandleConnectionChangedAsync(string airport, bool isConnected)
        {
            if (isConnected)
            {
                // Cancel any pending grace timer for this airport
                if (_disconnectGrace.TryGetValue(airport, out var cts))
                {
                    _logger.Log($"Reconnect detected for {airport}; cancelling disconnect grace timer.");
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                    _disconnectGrace.Remove(airport);
                }
                return;
            }

            // Disconnected: start or reset a grace timer
            if (_disconnectGrace.TryGetValue(airport, out var existing))
            {
                try { existing.Cancel(); } catch { }
                existing.Dispose();
                _disconnectGrace.Remove(airport);
            }

            var ctsNew = new CancellationTokenSource();
            _disconnectGrace[airport] = ctsNew;
            _logger.Log($"Connection lost for {airport}; starting {DISCONNECT_GRACE_MS / 1000}s grace period before cleanup.");

            try
            {
                await Task.Delay(DISCONNECT_GRACE_MS, ctsNew.Token);
            }
            catch (TaskCanceledException)
            {
                // Reconnected during grace
                return;
            }

            // Timer elapsed — verify still disconnected before cleanup
            bool stillDisconnected = !_connections.TryGetValue(airport, out var handler) || handler == null || !handler.IsConnected();
            if (!stillDisconnected)
            {
                // Reconnected just in time
                if (_disconnectGrace.TryGetValue(airport, out var cts2))
                {
                    cts2.Dispose();
                    _disconnectGrace.Remove(airport);
                }
                return;
            }

            _logger.Log($"Grace period elapsed for {airport} and still disconnected; removing airport and closing windows.");
            try
            {
                // Ensure UI-bound operations run on the GUI thread to avoid cross-thread control access
                vatsys.MMI.InvokeOnGUI(() =>
                {
                    // Fire and forget
                    _ = BARS.RemoveAirport(airport);
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Error scheduling post-grace cleanup for {airport}: {ex.Message}");
            }
            finally
            {
                if (_disconnectGrace.TryGetValue(airport, out var cts3))
                {
                    cts3.Dispose();
                    _disconnectGrace.Remove(airport);
                }
            }
        }
    }
}