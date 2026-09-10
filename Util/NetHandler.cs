using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BARS.Util
{
    public class NetHandler
    {
        // Timing / protocol constants
        private const int HEARTBEAT_INTERVAL = 30000;          // 30s heartbeat interval

        private const int HEARTBEAT_TIMEOUT = 60000;           // 60s timeout before reconnect
        private const int SERVER_UPDATE_DELAY = 100;           // Small settle delay on inbound state
        private const int STOPBAR_CROSSING_RAISE_DELAY_MS = 10000; // Delay before raising after crossing

        // Track pending delayed raises triggered by STOPBAR_CROSSING to avoid duplicates
        private readonly Dictionary<string, CancellationTokenSource> _pendingCrossingRaises = new Dictionary<string, CancellationTokenSource>();

        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _snapshotMinInterval = TimeSpan.FromMilliseconds(600);
        private readonly TimeSpan _snapshotBurstDelay = TimeSpan.FromMilliseconds(1200);
        private readonly object _updateLock = new object();

        private readonly Logger logger = new Logger("NetHandler");
        // serialize websocket sends

        private string _airport;
        private string _apiKey;
        private CancellationTokenSource _cancellationTokenSource;
        private string _controllerId;
        private bool _deferredSeedMode = false;
        private System.Timers.Timer _heartbeatTimer;
        private bool _isConnected = false;
        private DateTime _lastHeartbeatReceived;
        private long _lastOnlinePilotsTimestamp;
        private DateTime _lastSnapshotRequest = DateTime.MinValue;
        private CancellationTokenSource _snapshotBurstCts;

        // Local state cache (includes lead-on object IDs as separate entries when we send them)
        private Dictionary<string, object> _localStopbarStates = new Dictionary<string, object>();

        private readonly HashSet<string> _networkEchoSuppression = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ClientWebSocket _webSocket;

        public NetHandler(string connectionId = null)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            ConnectionId = connectionId ?? Guid.NewGuid().ToString();
        }

        // Events
        public delegate void ConnectionEventHandler(object sender, bool isConnected);

        public delegate void ErrorEventHandler(object sender, string errorMessage);

        public delegate void StateUpdateEventHandler(object sender, Dictionary<string, object> stopbarStates);

        public delegate void StopbarViolationEventHandler(object sender, Stopbar stopbar, string controllerId);

        public event ConnectionEventHandler OnConnectionChanged;

        public event ErrorEventHandler OnError;

        public event StateUpdateEventHandler OnStateUpdate;

        public event StopbarViolationEventHandler OnStopbarViolation;

        public string ConnectionId { get; private set; }
        public string Airport => _airport;
        public string ControllerId => _controllerId;

        /// <summary>
        /// Apply a new API key to this connection. If currently connected, will gracefully
        /// reconnect using the new key. If not connected, just updates the key for the next connect.
        /// </summary>
        public async Task ApplyNewApiKey(string newApiKey)
        {
            if (string.Equals(_apiKey, newApiKey, StringComparison.Ordinal))
            {
                return;
            }

            // If key is empty, update stored value but don't attempt to reconnect
            if (string.IsNullOrWhiteSpace(newApiKey))
            {
                _apiKey = newApiKey;
                return;
            }

            _apiKey = newApiKey;

            // Reconnect if we have an active socket or were previously connected
            bool shouldReconnect = _webSocket != null;
            if (shouldReconnect)
            {
                try
                {
                    await Disconnect();
                    await Task.Delay(250); // short pause to allow cleanup
                    await Connect();
                }
                catch (Exception ex)
                {
                    logger.Error($"API key apply reconnect failed for airport {_airport}: {ex.Message}");
                }
            }
        }

        // Connect to the WebSocket server
        public async Task<bool> Connect()
        {
            if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_airport))
            {
                OnError?.Invoke(this, "API Key and Airport are required to connect");
                return false;
            }

            if (vatsys.Network.IsConnected == false)
            {
                OnError?.Invoke(this, "Network connection is required to connect to BARS");
                return false;
            }

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();
                _lastOnlinePilotsTimestamp = 0;

                // Create connection URL with parameters
                string wsUrl = $"wss://v2.stopbars.com/connect?key={_apiKey}&airport={_airport}";
                Uri serverUri = new Uri(wsUrl);

                // Connect to the server
                await _webSocket.ConnectAsync(serverUri, _cancellationTokenSource.Token);

                // Start listening for messages
                _isConnected = true;
                OnConnectionChanged?.Invoke(this, true);

                // Setup and start heartbeat timer
                StartHeartbeat();

                // Start the message receiving loop
                _ = ReceiveMessagesAsync();

                if (Properties.Settings.Default.ShowBARSPilots)
                {
                    _ = RequestOnlinePilots();
                }

                // Log connection
                logger.Log($"Connected to BARS server for airport {_airport}");

                return true;
            }
            catch (WebSocketException wse)
            {
                var status = TryParseHttpStatusFromException(wse);
                string friendly = BuildFriendlyConnectError(status);
                if (status.HasValue)
                {
                    OnError?.Invoke(this, friendly);
                    logger.Error($"WebSocket handshake failed (HTTP {status}): {friendly}");
                }
                else
                {
                    OnError?.Invoke(this, $"Connection error: {wse.Message}");
                    logger.Error($"WebSocket connection error: {wse.Message}");
                }
                _isConnected = false;
                return false;
            }
            catch (Exception ex)
            {
                // Fallback generic error
                OnError?.Invoke(this, $"Connection error: {ex.Message}");
                logger.Error($"WebSocket connection error: {ex.Message}");
                _isConnected = false;
                return false;
            }
        }

        // Disconnect from the WebSocket server
        public async Task Disconnect()
        {
            if (PilotPresenceStore.ClearAirport(_airport))
            {
                vatsys.MMI.RequestRedraw(false, false, false);
            }

            var socket = _webSocket;
            if (socket == null)
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    OnConnectionChanged?.Invoke(this, false);
                }
                return;
            }

            var cts = _cancellationTokenSource;
            var heartbeatTimer = _heartbeatTimer;

            if (ReferenceEquals(_webSocket, socket))
            {
                _webSocket = null;
            }

            try
            {
                // Stop the heartbeat timer first
                StopHeartbeat(heartbeatTimer);
                CancelPendingSnapshot();

                bool socketOpen = false;
                try
                {
                    socketOpen = socket.State == WebSocketState.Open;
                }
                catch (ObjectDisposedException)
                {
                    socketOpen = false;
                }

                if (socketOpen)
                {
                    // Only try to send close message if socket is still open
                    try
                    {
                        await SendPacket(new
                        {
                            type = "CLOSE"
                        }, socket, CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // Suppress send errors during disconnect
                    }

                    try
                    {
                        // Attempt graceful closure
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                            "Client disconnecting",
                            CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // Suppress close errors during disconnect
                    }
                }

                // Cancel any ongoing operations
                try
                {
                    cts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Suppress cancellation errors during cleanup
                }

                logger.Log($"Disconnected from BARS server for airport {_airport}");
            }
            catch (Exception ex)
            {
                string stateMsg;
                try
                {
                    stateMsg = socket.State.ToString();
                }
                catch (ObjectDisposedException)
                {
                    stateMsg = "disposed";
                }

                OnError?.Invoke(this, $"Disconnect error: {ex.Message} (Socket State: {stateMsg})");
                logger.Error($"WebSocket disconnect error: {ex.Message} (Socket State: {stateMsg})");
            }
            finally
            {
                if (ReferenceEquals(_cancellationTokenSource, cts))
                {
                    _cancellationTokenSource = null;
                }

                try
                {
                    cts?.Dispose();
                }
                catch (Exception)
                {
                    // Suppress dispose errors during cleanup
                }

                _isConnected = false;
                OnConnectionChanged?.Invoke(this, false);

                try
                {
                    socket.Dispose();
                }
                catch (Exception)
                {
                    // Suppress dispose errors during cleanup
                }
            }
        }

        // Get all current stopbar states
        public Dictionary<string, object> GetAllStopbarStates()
        {
            return new Dictionary<string, object>(_localStopbarStates);
        }

        // Initialize connection parameters
        public void Initialize(string apiKey, string airport, string controllerId)
        {
            _apiKey = apiKey;
            _airport = airport;
            _controllerId = controllerId;
        }

        // Check if connected to the server
        public bool IsConnected()
        {
            return _isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open;
        }

        public Task RequestOnlinePilots()
        {
            if (!Properties.Settings.Default.ShowBARSPilots || !IsConnected())
            {
                return Task.CompletedTask;
            }

            return SendPacket(new
            {
                type = "GET_ONLINE_PILOTS"
            });
        }

        /// <summary>
        /// Called by ControllerHandler when a stopbar gets registered. If we deferred seeding because
        /// the profile wasn't loaded at connection time, seed each newly registered stopbar now.
        /// </summary>
        /// <param name="stopbar">The newly registered stopbar.</param>
        public void NotifyStopbarRegistered(Stopbar stopbar)
        {
            if (!_deferredSeedMode) return;
            // Seed this stopbar (and its lead-on) with lead-on forced FALSE
            _ = UpdateStopbar(stopbar, true);
        }

        // Public method to request a snapshot (debounced)
        public async Task RequestStateSnapshot(bool force = false)
        {
            if (!IsConnected())
            {
                logger.Log("Skipped GET_STATE request because connection is down");
                return;
            }
            if (!force && (DateTime.UtcNow - _lastSnapshotRequest) < _snapshotMinInterval)
            {
                logger.Log("Skipped GET_STATE request due to debounce window");
                return;
            }
            _lastSnapshotRequest = DateTime.UtcNow;
            await SendPacket(new
            {
                type = "GET_STATE",
                airport = _airport,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            logger.Log("Sent GET_STATE request");
        }

        private void ScheduleSnapshotReconciliation()
        {
            if (!IsConnected())
            {
                return;
            }

            CancellationTokenSource previous;
            CancellationTokenSource pending;
            lock (_updateLock)
            {
                previous = _snapshotBurstCts;
                pending = new CancellationTokenSource();
                _snapshotBurstCts = pending;
            }

            if (previous != null)
            {
                try
                {
                    previous.Cancel();
                }
                catch { }
                finally
                {
                    previous.Dispose();
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_snapshotBurstDelay, pending.Token);
                    await RequestStateSnapshot(force: true);
                }
                catch (TaskCanceledException)
                {
                    // Swallow cancellations caused by newer requests.
                }
                catch (ObjectDisposedException)
                {
                    // Timer was disposed before delay completed; ignore.
                }
                catch (Exception ex)
                {
                    logger.Error($"Snapshot reconcile request failed: {ex.Message}");
                }
            });
        }

        private void CancelPendingSnapshot()
        {
            CancellationTokenSource pending;
            lock (_updateLock)
            {
                pending = _snapshotBurstCts;
                _snapshotBurstCts = null;
            }

            if (pending == null)
            {
                return;
            }

            try
            {
                pending.Cancel();
            }
            catch { }
            finally
            {
                pending.Dispose();
            }
        }

        // Update stopbar state and send to server (batched into a single packet for primary + lead-ons)
        public async Task UpdateStopbar(Stopbar stopbar, bool forceLeadOnStateFalse = false)
        {
            string objectId;
            bool networkState;
            List<string> leadOnIds;

            // Collect updates and cache them locally under lock
            lock (_updateLock)
            {
                objectId = stopbar.BARSId;
                if (_networkEchoSuppression.Contains(objectId))
                {
                    logger.Log($"Skipping outbound state for {objectId} because network echo suppression is active");
                    return;
                }

                networkState = ConvertStopbarStateToNetwork(stopbar);
                leadOnIds = stopbar.LeadOnIds?.ToList() ?? new List<string>();
                _localStopbarStates[objectId] = networkState;
            }

            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                var socketState = _webSocket == null ? "null" : _webSocket.State.ToString();
                logger.Log($"Skipping outbound state for {objectId} because socket state is {socketState}");
                return;
            }

            // Build a single batch payload: primary + lead-ons (inverse unless forced false)
            var updates = new List<StateUpdateDto>
            {
                new StateUpdateDto { objectId = objectId, state = networkState }
            };

            if (leadOnIds.Count > 0)
            {
                foreach (string leadOnId in leadOnIds)
                {
                    bool leadOnState = forceLeadOnStateFalse ? false : !networkState;
                    lock (_updateLock)
                    {
                        _localStopbarStates[leadOnId] = leadOnState;
                    }
                    updates.Add(new StateUpdateDto { objectId = leadOnId, state = leadOnState });
                }
            }

            var packet = new
            {
                type = "MULTI_STATE_UPDATE",
                airport = _airport,
                data = updates
            };

            await SendPacket(packet);
            logger.Log($"Sent batched state update ({updates.Count} object(s)) for {objectId} to BARS server");
        }

        // DTO used for batching outbound state updates
        private sealed class StateUpdateDto
        {
            public string objectId { get; set; }
            public bool state { get; set; }
        }

        private string BuildFriendlyConnectError(int? httpStatus)
        {
            if (!httpStatus.HasValue)
            {
                return "Unable to connect to the server.";
            }
            switch (httpStatus.Value)
            {
                case 400:
                    return $"Invalid ICAO code '{_airport}'.";

                case 401:
                    return "API Key is invalid.";

                case 403:
                    return "Not connected to VATSIM.";

                default:
                    return $"Connection failed (HTTP {httpStatus}).";
            }
        }

        internal static bool TryReadNetworkState(object value, out bool state)
        {
            state = false;

            if (value == null)
            {
                return false;
            }

            if (value is bool boolValue)
            {
                state = boolValue;
                return true;
            }

            if (value is JToken token)
            {
                return TryReadNetworkState(token, out state);
            }

            if (value is string stringValue)
            {
                return TryReadBooleanString(stringValue, out state);
            }

            if (value is IConvertible convertible)
            {
                try
                {
                    state = convertible.ToBoolean(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryReadNetworkState(JToken token, out bool state)
        {
            state = false;

            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                state = token.Value<bool>();
                return true;
            }

            if (token.Type == JTokenType.Integer)
            {
                state = token.Value<long>() != 0;
                return true;
            }

            if (token.Type == JTokenType.String)
            {
                return TryReadBooleanString(token.Value<string>(), out state);
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                foreach (string propertyName in new[] { "state", "value", "active", "enabled" })
                {
                    if (obj.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken nestedToken) &&
                        TryReadNetworkState(nestedToken, out state))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadBooleanString(string value, out bool state)
        {
            state = false;

            if (bool.TryParse(value, out state))
            {
                return true;
            }

            if (long.TryParse(value, out long numericValue))
            {
                state = numericValue != 0;
                return true;
            }

            return false;
        }

        private static bool TryReadNetworkObject(object value, out string id, out bool state)
        {
            id = null;
            state = false;

            if (!(value is JToken token))
            {
                return false;
            }

            if (token is JProperty property)
            {
                id = property.Name;
                return TryReadNetworkState(property.Value, out state);
            }

            if (!(token is JObject obj))
            {
                return false;
            }

            id = (string)(obj["id"] ?? obj["objectId"]);
            JToken stateToken = obj["state"] ?? obj["value"];
            return !string.IsNullOrWhiteSpace(id) && TryReadNetworkState(stateToken, out state);
        }

        private bool ConvertStopbarStateToNetwork(Stopbar stopbar) => stopbar.State;
        private void SetStopbarStateFromNetwork(string airport, string barsId, bool state, bool autoRaise)
        {
            var stopbar = ControllerHandler.GetStopbar(airport, barsId);
            if (stopbar != null && stopbar.State != state)
            {
                using (BeginNetworkEchoSuppression(stopbar.BARSId))
                {
                    stopbar.State = state;
                    stopbar.AutoRaise = autoRaise;

                    logger.Log($"Network update: Set stopbar {barsId} at {airport} to {(state ? "ON" : "OFF")}, broadcasting to all windows");

                    // Broadcast to ALL window types (Legacy and INTAS)
                    ControllerHandler.NotifyStopbarStateChanged(stopbar, WindowType.Legacy, true);
                    ControllerHandler.NotifyStopbarStateChanged(stopbar, WindowType.INTAS, true);
                }
            }
        }

        private void ProcessInitialState(dynamic initialState)
        {
            try
            {
                string connectionType = initialState.data.connectionType;
                logger.Log($"Connected as: {connectionType}");

                Dictionary<string, object> stopbarStates = new Dictionary<string, object>();
                if (initialState.data.objects != null && initialState.data.objects.Count > 0)
                {
                    foreach (var obj in initialState.data.objects)
                    {
                        if (!TryReadNetworkObject(obj, out string id, out bool state))
                        {
                            logger.Log("Skipping malformed initial state object");
                            continue;
                        }

                        stopbarStates[id] = state;
                        ControllerHandler.RegisterStopbar(_airport, id, id, state, false);
                    }
                    logger.Log($"Received initial state with {stopbarStates.Count} stopbars for airport {_airport}");
                }
                else
                {
                    logger.Log($"Received empty initial state for airport {_airport}");
                    var locals = ControllerHandler.GetStopbarsForAirport(_airport);
                    if (locals.Any())
                    {
                        // Profile already loaded before we connected – seed immediately
                        logger.Log($"Profile already loaded; seeding {locals.Count} local stopbars now (lead-ons forced FALSE).");
                        Task.Run(async () =>
                        {
                            foreach (var sb in locals)
                            {
                                try
                                {
                                    await UpdateStopbar(sb, true); // force lead-on FALSE on first send
                                }
                                catch (Exception ex)
                                {
                                    logger.Error($"Error seeding {sb.BARSId}: {ex.Message}");
                                }
                            }
                        });
                    }
                    else
                    {
                        _deferredSeedMode = true;
                        logger.Log("Initial state empty and no local stopbars yet; deferring seeding until stopbars are registered.");
                    }
                }

                _localStopbarStates = stopbarStates; // for non-empty path
                OnStateUpdate?.Invoke(this, stopbarStates);
                // Proactively request a fresh snapshot to ensure parity (debounced)
                _ = RequestStateSnapshot();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Initial state processing error: {ex.Message}");
                logger.Error($"Initial state processing error: {ex.Message}");
            }
        }

        // Process received WebSocket messages
        private async void ProcessMessageAsync(string json)
        {
            try
            {
                // Ensure asynchronous context (avoid analyzer warning about lacking awaits)
                await Task.Yield();
                dynamic message = JsonConvert.DeserializeObject(json);
                string messageType = message.type;

                switch (messageType)
                {
                    case "HEARTBEAT":
                        // Server heartbeat: just record receipt (server does not expect ACK)
                        _lastHeartbeatReceived = DateTime.Now;
                        break;

                    // Rest of the cases remain the same
                    case "INITIAL_STATE":
                        ProcessInitialState(message);
                        break;

                    case "STATE_UPDATE":
                        ProcessStateUpdate(message);
                        break;

                    case "MULTI_STATE_UPDATE":
                        ProcessMultiStateUpdate(message);
                        break;

                    case "STATE_SNAPSHOT":
                        ProcessStateSnapshot(message);
                        break;

                    case "CONTROLLER_CONNECT":
                        string controllerId = message.data.controllerId;
                        logger.Log($"Controller {controllerId} connected to airport {_airport}");
                        break;

                    case "CONTROLLER_DISCONNECT":
                        string disconnectedId = message.data.controllerId;
                        logger.Log($"Controller {disconnectedId} disconnected from airport {_airport}");
                        break;

                    case "ONLINE_PILOTS":
                        ProcessOnlinePilots(message as JObject);
                        break;

                    case "STOPBAR_CROSSING":
                        {
                            string objectId = message.data.objectId;
                            string crossingPilotId = message.data.controllerId;

                            logger.Log($"Received STOPBAR_CROSSING for {objectId} (by pilot {crossingPilotId}) at airport {_airport}");

                            // Find the stopbar in our local registry
                            var stopbar = ControllerHandler.GetStopbar(_airport, objectId);
                            if (stopbar == null)
                            {
                                logger.Log($"STOPBAR_CROSSING for unknown stopbar {objectId} – no action taken.");
                                break;
                            }

                            // If the stopbar is already up, nothing to do (also cancel any pending delayed raise)
                            if (stopbar.State)
                            {
                                CancellationTokenSource existingCts = null;
                                lock (_updateLock)
                                {
                                    if (_pendingCrossingRaises.TryGetValue(objectId, out existingCts))
                                    {
                                        _pendingCrossingRaises.Remove(objectId);
                                    }
                                }
                                if (existingCts != null)
                                {
                                    try { existingCts.Cancel(); } catch { }
                                    try { existingCts.Dispose(); } catch { }
                                }

                                logger.Log($"STOPBAR_CROSSING violation detected for {objectId}; raised stopbar crossed by {crossingPilotId}. Raising OnStopbarViolation.");
                                try
                                {
                                    OnStopbarViolation?.Invoke(this, stopbar, crossingPilotId);
                                }
                                catch (Exception ex)
                                {
                                    logger.Error($"STOPBAR violation event handler error for {objectId}: {ex.Message}");
                                }
                                break;
                            }

                            // Schedule a delayed raise if not already pending
                            bool schedule = false;
                            CancellationTokenSource cts;
                            lock (_updateLock)
                            {
                                if (_pendingCrossingRaises.ContainsKey(objectId))
                                {
                                    logger.Log($"STOPBAR_CROSSING for {objectId} – delayed raise already scheduled; ignoring duplicate event.");
                                    schedule = false;
                                    cts = null;
                                }
                                else
                                {
                                    cts = new CancellationTokenSource();
                                    _pendingCrossingRaises[objectId] = cts;
                                    schedule = true;
                                }
                            }

                            if (schedule)
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await Task.Delay(STOPBAR_CROSSING_RAISE_DELAY_MS, cts.Token);
                                        if (cts.IsCancellationRequested) return;

                                        // Re-fetch and verify it still needs raising
                                        var sb = ControllerHandler.GetStopbar(_airport, objectId);
                                        if (sb != null && !sb.State)
                                        {
                                            sb.State = true;
                                            ControllerHandler.NotifyStopbarStateChanged(sb, WindowType.Legacy);
                                            ControllerHandler.NotifyStopbarStateChanged(sb, WindowType.INTAS);
                                            logger.Log($"Auto-raised stopbar {objectId} after STOPBAR_CROSSING delay.");

                                            // Send to network server
                                            var netHandler = NetManager.Instance.GetConnection(_airport);
                                            if (netHandler != null && netHandler.IsConnected())
                                            {
                                                _ = netHandler.UpdateStopbar(sb);
                                            }
                                        }
                                    }
                                    catch (TaskCanceledException) { }
                                    catch (Exception ex)
                                    {
                                        logger.Error($"Error performing delayed raise for {objectId}: {ex.Message}");
                                    }
                                    finally
                                    {
                                        lock (_updateLock)
                                        {
                                            if (_pendingCrossingRaises.TryGetValue(objectId, out var toDispose) && toDispose == cts)
                                            {
                                                _pendingCrossingRaises.Remove(objectId);
                                            }
                                        }
                                        try { cts.Dispose(); } catch { }
                                    }
                                });
                                logger.Log($"Scheduled delayed raise for {objectId} in {STOPBAR_CROSSING_RAISE_DELAY_MS} ms due to STOPBAR_CROSSING.");
                            }
                            break;
                        }

                    case "ERROR":
                        string errorMsg = message.data.message;
                        OnError?.Invoke(this, $"Server error: {errorMsg}");
                        logger.Log($"Server error: {errorMsg}");
                        break;
                }
                // Any non-heartbeat message still indicates activity; update last seen (except heartbeat already updated above)
                if (messageType != "HEARTBEAT")
                {
                    _lastHeartbeatReceived = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Message processing error: {ex.Message}");
                logger.Log($"WebSocket message processing error: {ex.Message}");
            }
        }

        private void ProcessStateSnapshot(dynamic snapshot)
        {
            try
            {
                Dictionary<string, object> serverStates = new Dictionary<string, object>();
                bool offline = false;
                try { offline = snapshot.data.offline; } catch { }
                if (snapshot.data.objects != null)
                {
                    foreach (var obj in snapshot.data.objects)
                    {
                        if (!TryReadNetworkObject(obj, out string id, out bool state))
                        {
                            logger.Log("Skipping malformed STATE_SNAPSHOT object");
                            continue;
                        }

                        serverStates[id] = state;
                    }
                }
                logger.Log($"Received STATE_SNAPSHOT with {serverStates.Count} objects (offline={offline})");

                var localStopbars = ControllerHandler.GetStopbarsForAirport(_airport);
                var primaryIds = new HashSet<string>(localStopbars.Select(s => s.BARSId));
                var leadOnIds = new HashSet<string>(
                    localStopbars.SelectMany(s => s.LeadOnIds ?? Array.Empty<string>()),
                    StringComparer.OrdinalIgnoreCase);

                // Apply server authoritative states to existing primaries
                foreach (var sb in localStopbars)
                {
                    bool isLeadOn = ControllerHandler.IsLeadOnId(_airport, sb.BARSId);
                    if (isLeadOn)
                    {
                        if (serverStates.TryGetValue(sb.BARSId, out object leadStateObj))
                        {
                            if (TryReadNetworkState(leadStateObj, out bool leadState) && sb.State != leadState)
                            {
                                SetStopbarStateFromNetwork(_airport, sb.BARSId, leadState, sb.AutoRaise);
                                logger.Log($"Snapshot aligned lead-on {sb.BARSId} to server state {leadState}");
                            }
                        }
                        continue;
                    }

                    if (serverStates.TryGetValue(sb.BARSId, out object srvObj))
                    {
                        if (TryReadNetworkState(srvObj, out bool srvState) && sb.State != srvState)
                        {
                            SetStopbarStateFromNetwork(_airport, sb.BARSId, srvState, sb.AutoRaise);
                            logger.Log($"Reconciled stopbar {sb.BARSId} to server state {srvState}");
                        }
                    }
                    else
                    {
                        // Missing on server, announce local state
                        if (!ControllerHandler.IsLeadOnId(_airport, sb.BARSId))
                        {
                            _ = UpdateStopbar(sb);
                            logger.Log($"Server missing {sb.BARSId}; pushed local state.");
                        }
                    }
                }

                // Register any new objects (ignore those that map to known lead-ons only)
                foreach (var kvp in serverStates)
                {
                    if (!primaryIds.Contains(kvp.Key) && !leadOnIds.Contains(kvp.Key))
                    {
                        if (TryReadNetworkState(kvp.Value, out bool state))
                        {
                            ControllerHandler.RegisterStopbar(_airport, kvp.Key, kvp.Key, state, false);
                            logger.Log($"Discovered new server object {kvp.Key}; registered locally.");
                        }
                    }
                }

                // Update cache
                lock (_updateLock)
                {
                    foreach (var kvp in serverStates)
                    {
                        _localStopbarStates[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"State snapshot processing error: {ex.Message}");
                logger.Error($"State snapshot processing error: {ex.Message}");
            }
        }

        private void ProcessOnlinePilots(JObject message)
        {
            if (message == null || !Properties.Settings.Default.ShowBARSPilots)
            {
                return;
            }

            string responseAirport = message.Value<string>("airport");
            if (string.IsNullOrWhiteSpace(responseAirport) ||
                !string.Equals(responseAirport, _airport, StringComparison.OrdinalIgnoreCase))
            {
                logger.Log($"Ignored ONLINE_PILOTS response for unexpected airport '{responseAirport}'.");
                return;
            }

            JToken pilotsToken = message["data"]?["pilots"];
            if (!(pilotsToken is JArray pilots))
            {
                logger.Log("Ignored malformed ONLINE_PILOTS response without a pilots array.");
                return;
            }

            long responseTimestamp = message.Value<long?>("timestamp") ?? 0;
            if (responseTimestamp > 0 && responseTimestamp < _lastOnlinePilotsTimestamp)
            {
                logger.Log($"Ignored stale ONLINE_PILOTS response for {_airport}.");
                return;
            }

            var callsigns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken pilot in pilots)
            {
                string callsign = pilot?["callsign"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(callsign))
                {
                    callsigns.Add(callsign.Trim());
                }
            }

            if (responseTimestamp > 0)
            {
                _lastOnlinePilotsTimestamp = responseTimestamp;
            }

            if (PilotPresenceStore.UpdateAirport(_airport, callsigns))
            {
                vatsys.MMI.RequestRedraw(false, false, false);
            }
            logger.Log($"Received {callsigns.Count} online BARS pilot(s) for {_airport}.");
        }

        // Process state updates from other controllers
        private async void ProcessStateUpdate(dynamic stateUpdate)
        {
            try
            {
                string objectId = stateUpdate.data.objectId;
                if (!TryReadNetworkState((stateUpdate as JToken)?["data"]?["state"], out bool state))
                {
                    logger.Log($"Skipping malformed STATE_UPDATE for {objectId}");
                    return;
                }

                string controllerId = stateUpdate.data.controllerId;
                if (controllerId == _controllerId) return; // ignore own

                await Task.Delay(SERVER_UPDATE_DELAY);

                lock (_updateLock) _localStopbarStates[objectId] = state;

                if (ControllerHandler.IsLeadOnId(_airport, objectId))
                {
                    var leadOnStopbar = ControllerHandler.GetStopbar(_airport, objectId);
                    if (leadOnStopbar != null)
                    {
                        SetStopbarStateFromNetwork(_airport, objectId, state, leadOnStopbar.AutoRaise);
                        logger.Log($"Applied lead-on state update for {objectId} from controller {controllerId}");
                    }
                    return;
                }

                var all = ControllerHandler.GetStopbarsForAirport(_airport);
                var primary = all.FirstOrDefault(sb => sb.BARSId == objectId);
                if (primary != null)
                {
                    if (primary.State == state)
                    {
                        return;
                    }
                    SetStopbarStateFromNetwork(_airport, objectId, state, primary.AutoRaise);
                    logger.Log($"Received state update for stopbar {objectId} from controller {controllerId}");
                }
                else if (all.Any(sb => sb.LeadOnIds != null && sb.LeadOnIds.Any(id => string.Equals(id, objectId, StringComparison.OrdinalIgnoreCase))))
                {
                    // Lead-on update: ignore (inverse derived from primary)
                    logger.Log($"Received lead-on update {objectId} (state={state}) from controller {controllerId} – ignored (legacy path).");
                }
                else
                {
                    logger.Log($"Received update for unknown object {objectId} (state={state}) from controller {controllerId} – no action.");
                }

                // After burst of external activity, queue a reconciliation snapshot so we only fetch once the dust settles.
                ScheduleSnapshotReconciliation();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"State update processing error: {ex.Message}");
                logger.Error($"State update processing error: {ex.Message}");
            }
        }

        // Process batched state updates from other controllers
        private async void ProcessMultiStateUpdate(dynamic batchUpdate)
        {
            try
            {
                // Extract controllerId if present (either alongside data or nested inside)
                string controllerId = null;
                try { controllerId = (string)((JToken)batchUpdate)["controllerId"]; } catch { }
                try { controllerId = controllerId ?? (string)((JToken)batchUpdate["data"])?.Value<string>("controllerId"); } catch { }

                // Small settle delay to align with single update path
                await Task.Delay(SERVER_UPDATE_DELAY);

                var dataToken = (batchUpdate as JToken)?["data"];
                if (dataToken == null)
                {
                    logger.Error("Multi state update processing error: data payload missing");
                    return;
                }

                // Determine the collection of update entries
                IEnumerable<JToken> entries = null;
                if (dataToken.Type == JTokenType.Array)
                {
                    entries = dataToken.Children();
                }
                else if (dataToken.Type == JTokenType.Object)
                {
                    var updatesToken = dataToken["updates"] ?? dataToken["objects"] ?? dataToken["items"];
                    if (updatesToken != null && updatesToken.Type == JTokenType.Array)
                    {
                        entries = updatesToken.Children();
                    }
                    else
                    {
                        // Treat object properties as key/value pairs objectId->state
                        entries = dataToken.Children();
                    }
                }

                if (entries == null)
                {
                    logger.Error("Multi state update processing error: data payload not iterable");
                    return;
                }

                foreach (var token in entries)
                {
                    string objectId = null;
                    bool? state = null;

                    if (token is JProperty prop)
                    {
                        objectId = prop.Name;
                        state = TryReadNetworkState(prop.Value, out bool parsedState) ? parsedState : (bool?)null;
                    }
                    else if (token is JObject obj)
                    {
                        objectId = (string)(obj["objectId"] ?? obj["id"]);
                        JToken stateToken = obj["state"] ?? obj["value"];
                        if (TryReadNetworkState(stateToken, out bool parsedState))
                        {
                            state = parsedState;
                        }
                    }

                    if (string.IsNullOrEmpty(objectId) || state == null)
                    {
                        logger.Log("Skipping malformed MULTI_STATE_UPDATE entry");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(controllerId) && controllerId == _controllerId)
                    {
                        continue; // ignore own echo
                    }

                    lock (_updateLock) _localStopbarStates[objectId] = state.Value;

                    if (ControllerHandler.IsLeadOnId(_airport, objectId))
                    {
                        var leadOnStopbar = ControllerHandler.GetStopbar(_airport, objectId);
                        if (leadOnStopbar != null)
                        {
                            SetStopbarStateFromNetwork(_airport, objectId, state.Value, leadOnStopbar.AutoRaise);
                            logger.Log($"Applied lead-on state update for {objectId} from controller {controllerId}");
                        }
                        continue;
                    }

                    var all = ControllerHandler.GetStopbarsForAirport(_airport);
                    bool anyLocals = all.Count > 0;
                    var primary = all.FirstOrDefault(sb => sb.BARSId == objectId);
                    if (primary != null)
                    {
                        if (primary.State == state.Value)
                        {
                            continue;
                        }
                        SetStopbarStateFromNetwork(_airport, objectId, state.Value, primary.AutoRaise);
                        logger.Log($"Received batched state update for stopbar {objectId} from controller {controllerId}");
                    }
                    else if (all.Any(sb => sb.LeadOnIds != null && sb.LeadOnIds.Any(id => string.Equals(id, objectId, StringComparison.OrdinalIgnoreCase))))
                    {
                        logger.Log($"Received batched lead-on update {objectId} (state={state.Value}) from controller {controllerId} – ignored (legacy path).");
                    }
                    else
                    {
                        if (anyLocals)
                        {
                            logger.Log($"Received batched update for unknown object {objectId} (state={state.Value}) from controller {controllerId} – no action.");
                        }
                    }
                }

                // After burst of external activity, queue a reconciliation snapshot
                ScheduleSnapshotReconciliation();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Multi state update processing error: {ex.Message}");
                logger.Error($"Multi state update processing error: {ex.Message}");
            }
        }

        // Receive and process messages from the server
        private async Task ReceiveMessagesAsync()
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (_webSocket != null && _webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    StringBuilder messageBuilder = null;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await Disconnect();
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            if (messageBuilder == null)
                            {
                                messageBuilder = new StringBuilder();
                            }
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                        // Ignore non-text messages once drained to EndOfMessage
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text && messageBuilder != null)
                    {
                        ProcessMessageAsync(messageBuilder.ToString());
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Receive error: {ex.Message}");
                logger.Error($"WebSocket receive error: {ex.Message}");
                if (PilotPresenceStore.ClearAirport(_airport))
                {
                    vatsys.MMI.RequestRedraw(false, false, false);
                }
                if (_isConnected)
                {
                    _isConnected = false;
                    OnConnectionChanged?.Invoke(this, false);
                    try
                    {
                        await Task.Delay(3000);
                        await Connect();
                    }
                    catch { }
                }
            }
        }

        // Send a heartbeat to the server
        private async Task SendHeartbeat()
        {
            try
            {
                // Check if server is responsive
                if ((DateTime.Now - _lastHeartbeatReceived).TotalMilliseconds > HEARTBEAT_TIMEOUT)
                {
                    logger.Log("Heartbeat timeout, reconnecting...");
                    await Disconnect();
                    await Task.Delay(1000); // Wait a bit before reconnecting
                    await Connect();
                    return;
                }

                // Send heartbeat
                await SendPacket(new
                {
                    type = "HEARTBEAT"
                });

                if (Properties.Settings.Default.ShowBARSPilots)
                {
                    await RequestOnlinePilots();
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Error sending heartbeat: {ex.Message}");
                // Try to reconnect on heartbeat failure
                try
                {
                    await Disconnect();
                    await Task.Delay(1000);
                    await Connect();
                }
                catch { /* Suppress reconnection errors */ }
            }
        }

        // Send a packet to the server
        private async Task SendPacket(object data, ClientWebSocket socketOverride = null, CancellationToken? cancellationTokenOverride = null)
        {
            var socket = socketOverride ?? _webSocket;
            try
            {
                if (socket == null || socket.State != WebSocketState.Open)
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            await _sendSemaphore.WaitAsync();
            try
            {
                string json = JsonConvert.SerializeObject(data);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                CancellationToken token;
                if (cancellationTokenOverride.HasValue)
                {
                    token = cancellationTokenOverride.Value;
                }
                else if (_cancellationTokenSource != null)
                {
                    token = _cancellationTokenSource.Token;
                }
                else
                {
                    token = CancellationToken.None;
                }

                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Send error: {ex.Message}");
                logger.Error($"WebSocket send error: {ex.Message}");
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        // Start the heartbeat timer
        private void StartHeartbeat()
        {
            _lastHeartbeatReceived = DateTime.Now;

            _heartbeatTimer = new System.Timers.Timer(HEARTBEAT_INTERVAL);
            _heartbeatTimer.Elapsed += async (sender, e) => await SendHeartbeat();
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
        }

        // Stop the heartbeat timer
        private void StopHeartbeat(System.Timers.Timer expectedTimer = null)
        {
            if (expectedTimer != null)
            {
                try
                {
                    expectedTimer.Stop();
                    expectedTimer.Dispose();
                }
                catch (Exception)
                {
                    // Suppress timer disposal errors during cleanup
                }

                if (ReferenceEquals(_heartbeatTimer, expectedTimer))
                {
                    _heartbeatTimer = null;
                }

                return;
            }

            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
        }

        // Extract HTTP status code from handshake-related exceptions where available
        private int? TryParseHttpStatusFromException(Exception ex)
        {
            if (ex == null) return null;

            // Common message patterns seen from ClientWebSocket handshake failures
            // e.g., "The server returned status code '401' when status code '101' was expected."
            // or    "The remote server returned an error: (400) Bad Request."
            var msg = ex.Message ?? string.Empty;
            var m1 = Regex.Match(msg, @"status code '\s*(\d{3})\s*'", RegexOptions.IgnoreCase);
            if (m1.Success && int.TryParse(m1.Groups[1].Value, out int code1)) return code1;

            var m2 = Regex.Match(msg, @"\((\d{3})\)\s*[A-Za-z ]+", RegexOptions.IgnoreCase);
            if (m2.Success && int.TryParse(m2.Groups[1].Value, out int code2)) return code2;

            // Recurse into inner exceptions to try to find a status code
            return TryParseHttpStatusFromException(ex.InnerException);
        }

        // true when initial state empty but profile not yet loaded
        // delay before post-send verification snapshot
        private IDisposable BeginNetworkEchoSuppression(string objectId) => new NetworkEchoSuppression(this, objectId);

        private sealed class NetworkEchoSuppression : IDisposable
        {
            private readonly NetHandler _handler;
            private readonly string _objectId;
            private bool _disposed;

            public NetworkEchoSuppression(NetHandler handler, string objectId)
            {
                _handler = handler;
                _objectId = objectId;
                lock (_handler._updateLock)
                {
                    _handler._networkEchoSuppression.Add(objectId);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                lock (_handler._updateLock)
                {
                    _handler._networkEchoSuppression.Remove(_objectId);
                }
                _disposed = true;
            }
        }
    }
}
