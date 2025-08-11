using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BARS.Util
{
    public class NetHandler
    {
        // Timing / protocol constants
        private const int HEARTBEAT_INTERVAL = 30000;          // 30s heartbeat interval
        private const int HEARTBEAT_TIMEOUT = 60000;           // 60s timeout before reconnect
        private const int SERVER_UPDATE_DELAY = 100;           // Small settle delay on inbound state

        private readonly object _updateLock = new object();
        private readonly Logger logger = new Logger("NetHandler");
        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1); // serialize websocket sends

        private string _airport;
        private string _apiKey;
        private string _controllerId;
        private CancellationTokenSource _cancellationTokenSource;
        private System.Timers.Timer _heartbeatTimer;
        private bool _isConnected = false;
        private DateTime _lastHeartbeatReceived;
        private ClientWebSocket _webSocket;

        // Local state cache (includes lead-on object IDs as separate entries when we send them)
        private Dictionary<string, object> _localStopbarStates = new Dictionary<string, object>();
        private bool _processingNetworkUpdate = false;
        private DateTime _lastSnapshotRequest = DateTime.MinValue;
        private readonly TimeSpan _snapshotMinInterval = TimeSpan.FromSeconds(2);
        private bool _deferredSeedMode = false; // true when initial state empty but profile not yet loaded
        private readonly Dictionary<string, PendingUpdate> _pendingLocalUpdates = new Dictionary<string, PendingUpdate>();
        private readonly TimeSpan _pendingGrace = TimeSpan.FromMilliseconds(900); // window to ignore conflicting server reversions
        private readonly TimeSpan _verificationDelay = TimeSpan.FromMilliseconds(450); // delay before post-send verification snapshot

        private class PendingUpdate
        {
            public bool State { get; set; }
            public DateTime SentAt { get; set; }
            public bool VerificationScheduled { get; set; }
            public int RetryCount { get; set; }
        }

        public NetHandler(string connectionId = null)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            ConnectionId = connectionId ?? Guid.NewGuid().ToString();
        }

        // Events
        public delegate void ConnectionEventHandler(object sender, bool isConnected);
        public delegate void ErrorEventHandler(object sender, string errorMessage);
        public delegate void StateUpdateEventHandler(object sender, Dictionary<string, object> stopbarStates);

        public event ConnectionEventHandler OnConnectionChanged;
        public event ErrorEventHandler OnError;
        public event StateUpdateEventHandler OnStateUpdate;

        public string ConnectionId { get; private set; }

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

                // Log connection
                logger.Log($"Connected to BARS server for airport {_airport}");

                return true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Connection error: {ex.Message}");
                logger.Error($"WebSocket connection error: {ex.Message}");
                _isConnected = false;
                return false;
            }
        }

        // Disconnect from the WebSocket server
        public async Task Disconnect()
        {
            if (_webSocket == null)
                return;

            try
            {
                // Stop the heartbeat timer first
                StopHeartbeat();

                if (_webSocket.State == WebSocketState.Open)
                {
                    // Only try to send close message if socket is still open
                    try
                    {
                        await SendPacket(new
                        {
                            type = "CLOSE"
                        });
                    }
                    catch (Exception)
                    {
                        // Suppress send errors during disconnect
                    }

                    // Attempt graceful closure
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "Client disconnecting",
                        CancellationToken.None);
                }

                // Cancel any ongoing operations
                _cancellationTokenSource.Cancel();

                // Clean up WebSocket
                try
                {
                    _webSocket.Dispose();
                }
                catch (Exception)
                {
                    // Suppress dispose errors
                }
                finally
                {
                    _webSocket = null;
                    _isConnected = false;
                    OnConnectionChanged?.Invoke(this, false);
                }

                logger.Log($"Disconnected from BARS server for airport {_airport}");
            }
            catch (Exception ex)
            {
                string stateMsg = _webSocket != null ? _webSocket.State.ToString() : "null";
                OnError?.Invoke(this, $"Disconnect error: {ex.Message} (Socket State: {stateMsg})");
                logger.Error($"WebSocket disconnect error: {ex.Message} (Socket State: {stateMsg})");

                // Ensure cleanup even on error
                _webSocket = null;
                _isConnected = false;
                OnConnectionChanged?.Invoke(this, false);
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

        // Update stopbar state and send to server
        public async Task UpdateStopbar(Stopbar stopbar, bool forceLeadOnStateFalse = false)
        {
            if (_processingNetworkUpdate) return;

            string objectId;
            bool networkState;
            string leadOnId;

            lock (_updateLock)
            {
                objectId = stopbar.BARSId;
                networkState = ConvertStopbarStateToNetwork(stopbar);
                leadOnId = stopbar.LeadOnId;
                _localStopbarStates[objectId] = networkState;
            }

            if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

            var packet = new
            {
                type = "STATE_UPDATE",
                airport = _airport,
                data = new { objectId, state = networkState }
            };
            await SendPacket(packet);
            logger.Log($"Sent stopbar state update for {objectId} (state={networkState}) to BARS server");

            // Track optimistic local update
            lock (_updateLock)
            {
                _pendingLocalUpdates[objectId] = new PendingUpdate
                {
                    State = networkState,
                    SentAt = DateTime.UtcNow
                };
            }

            // Schedule verification snapshot if not already scheduled for this object
            _ = Task.Run(async () =>
            {
                bool schedule;
                lock (_updateLock)
                {
                    schedule = _pendingLocalUpdates.ContainsKey(objectId) && !_pendingLocalUpdates[objectId].VerificationScheduled;
                    if (schedule) _pendingLocalUpdates[objectId].VerificationScheduled = true;
                }
                if (schedule)
                {
                    await Task.Delay(_verificationDelay);
                    await RequestStateSnapshot();
                }
            });

            // Separate packet for lead-on if applicable
            if (!string.IsNullOrEmpty(leadOnId))
            {
                bool leadOnState = forceLeadOnStateFalse ? false : !networkState; // inverse unless forcing false (initial seed / late assignment)
                lock (_updateLock)
                {
                    _localStopbarStates[leadOnId] = leadOnState;
                }
                var leadOnPacket = new
                {
                    type = "STATE_UPDATE",
                    airport = _airport,
                    data = new { objectId = leadOnId, state = leadOnState }
                };
                await SendPacket(leadOnPacket);
                logger.Log($"Sent lead-on state update for {leadOnId} (state={leadOnState}) paired with stopbar {objectId}");
                lock (_updateLock)
                {
                    _pendingLocalUpdates[leadOnId] = new PendingUpdate
                    {
                        State = leadOnState,
                        SentAt = DateTime.UtcNow
                    };
                }
            }
        }

        private bool ConvertStopbarStateToNetwork(Stopbar stopbar) => stopbar.State;

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
                        string id = obj.id;
                        bool state = obj.state;
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

        // Process state updates from other controllers
        private async void ProcessStateUpdate(dynamic stateUpdate)
        {
            try
            {
                string objectId = stateUpdate.data.objectId;
                bool state = stateUpdate.data.state;
                string controllerId = stateUpdate.data.controllerId;
                if (controllerId == _controllerId) return; // ignore own

                await Task.Delay(SERVER_UPDATE_DELAY);

                lock (_updateLock) _localStopbarStates[objectId] = state;

                var all = ControllerHandler.GetStopbarsForAirport(_airport);
                var primary = all.FirstOrDefault(sb => sb.BARSId == objectId);
                if (primary != null)
                {
                    // Check for recent optimistic local update
                    bool suppress = false;
                    bool needsRetry = false;
                    lock (_updateLock)
                    {
                        if (_pendingLocalUpdates.TryGetValue(objectId, out var pending))
                        {
                            if ((DateTime.UtcNow - pending.SentAt) < _pendingGrace)
                            {
                                if (pending.State != state)
                                {
                                    // Conflict within grace window – schedule a single retry by re-sending our state
                                    if (pending.RetryCount == 0)
                                    {
                                        pending.RetryCount++;
                                        needsRetry = true;
                                    }
                                    suppress = true; // don't override our local yet
                                }
                                else
                                {
                                    // Server echoed desired state – clear pending
                                    _pendingLocalUpdates.Remove(objectId);
                                }
                            }
                            else
                            {
                                // Grace expired; accept server as authoritative
                                _pendingLocalUpdates.Remove(objectId);
                            }
                        }
                    }
                    if (needsRetry)
                    {
                        _ = UpdateStopbar(primary); // resend
                    }
                    if (suppress) return; // ignore this revert attempt during grace

                    if (primary.State == state)
                    {
                        // No change
                        return;
                    }
                    _processingNetworkUpdate = true;
                    try
                    {
                        ControllerHandler.SetStopbarState(_airport, objectId, state, WindowType.Legacy, primary.AutoRaise);
                    }
                    finally
                    {
                        _processingNetworkUpdate = false;
                    }
                    logger.Log($"Received state update for stopbar {objectId} from controller {controllerId}");
                }
                else if (all.Any(sb => sb.LeadOnId == objectId))
                {
                    // Lead-on update: ignore (inverse derived from primary)
                    logger.Log($"Received lead-on update {objectId} (state={state}) from controller {controllerId} – ignored.");
                }
                else
                {
                    logger.Log($"Received update for unknown object {objectId} (state={state}) from controller {controllerId} – no action.");
                }

                // After any external update, request a snapshot (debounced) to repair potential rapid-tap divergence.
                _ = RequestStateSnapshot();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"State update processing error: {ex.Message}");
                logger.Error($"State update processing error: {ex.Message}");
            }
        }

        // Public method to request a snapshot (debounced)
        public async Task RequestStateSnapshot(bool force = false)
        {
            if (!IsConnected()) return;
            if (!force && (DateTime.UtcNow - _lastSnapshotRequest) < _snapshotMinInterval) return;
            _lastSnapshotRequest = DateTime.UtcNow;
            await SendPacket(new
            {
                type = "GET_STATE",
                airport = _airport,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            logger.Log("Sent GET_STATE request");
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
                        string id = obj.id;
                        bool state = obj.state;
                        serverStates[id] = state;
                    }
                }
                logger.Log($"Received STATE_SNAPSHOT with {serverStates.Count} objects (offline={offline})");

                var localStopbars = ControllerHandler.GetStopbarsForAirport(_airport);
                var primaryIds = new HashSet<string>(localStopbars.Select(s => s.BARSId));
                var leadOnIds = new HashSet<string>(localStopbars.Where(s => !string.IsNullOrEmpty(s.LeadOnId)).Select(s => s.LeadOnId));

                // Apply server authoritative states to existing primaries
                foreach (var sb in localStopbars)
                {
                    if (serverStates.TryGetValue(sb.BARSId, out object srvObj))
                    {
                        bool srvState = Convert.ToBoolean(srvObj);
                        bool skip = false;
                        lock (_updateLock)
                        {
                            if (_pendingLocalUpdates.TryGetValue(sb.BARSId, out var pending) && (DateTime.UtcNow - pending.SentAt) < _pendingGrace)
                            {
                                if (pending.State != srvState)
                                {
                                    // Conflict inside grace window – prefer local, schedule retry if not already
                                    if (pending.RetryCount == 0)
                                    {
                                        pending.RetryCount++;
                                        _ = UpdateStopbar(sb);
                                    }
                                    skip = true;
                                }
                                else
                                {
                                    _pendingLocalUpdates.Remove(sb.BARSId); // confirmed
                                }
                            }
                        }
                        if (skip) continue;
                        if (sb.State != srvState)
                        {
                            _processingNetworkUpdate = true;
                            try
                            {
                                ControllerHandler.SetStopbarState(_airport, sb.BARSId, srvState, WindowType.Legacy, sb.AutoRaise);
                            }
                            finally { _processingNetworkUpdate = false; }
                            logger.Log($"Reconciled stopbar {sb.BARSId} to server state {srvState}");
                        }
                    }
                    else
                    {
                        // Missing on server, announce local state
                        _ = UpdateStopbar(sb);
                        logger.Log($"Server missing {sb.BARSId}; pushed local state.");
                    }
                }

                // Register any new objects (ignore those that map to known lead-ons only)
                foreach (var kvp in serverStates)
                {
                    if (!primaryIds.Contains(kvp.Key) && !leadOnIds.Contains(kvp.Key))
                    {
                        ControllerHandler.RegisterStopbar(_airport, kvp.Key, kvp.Key, (bool)kvp.Value, false);
                        logger.Log($"Discovered new server object {kvp.Key}; registered locally.");
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

        // Receive and process messages from the server
        private async Task ReceiveMessagesAsync()
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (_webSocket != null && _webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await Disconnect();
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        ProcessMessageAsync(json);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Receive error: {ex.Message}");
                logger.Error($"WebSocket receive error: {ex.Message}");
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
        private async Task SendPacket(object data)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                return;

            await _sendSemaphore.WaitAsync();
            try
            {
                string json = JsonConvert.SerializeObject(data);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
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
        private void StopHeartbeat()
        {
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
        }
    }
}