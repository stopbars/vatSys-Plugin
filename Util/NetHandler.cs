using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Threading;
using Newtonsoft.Json;
using System.Timers;
using System.Net;
using System.IO;

namespace BARS.Util
{
    public class NetHandler
    {
        private readonly Logger logger = new Logger("NetHandler");

        // Constants matching the server configuration
        private const int HEARTBEAT_INTERVAL = 30000; // 30 seconds (matching server)
        private const int HEARTBEAT_TIMEOUT = 60000;  // 60 seconds (matching server)
        private const int SERVER_UPDATE_DELAY = 100; // 100ms delay for server updates
        
        // Unique identifier for this connection
        public string ConnectionId { get; private set; }
        
        // WebSocket client
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private System.Timers.Timer _heartbeatTimer;
        private DateTime _lastHeartbeatReceived;
        private bool _isConnected = false;
        private string _apiKey;
        private string _airport;
        private string _controllerId;
        private readonly object _updateLock = new object();

        // Event handlers
        public delegate void ConnectionEventHandler(object sender, bool isConnected);
        public delegate void StateUpdateEventHandler(object sender, Dictionary<string, object> stopbarStates);
        public delegate void ErrorEventHandler(object sender, string errorMessage);

        public event ConnectionEventHandler OnConnectionChanged;
        public event StateUpdateEventHandler OnStateUpdate;
        public event ErrorEventHandler OnError;

        // Track local stopbar states
        private Dictionary<string, object> _localStopbarStates = new Dictionary<string, object>();

        // Track if a state change is coming from the network
        private bool _processingNetworkUpdate = false;

        // Constructor with connection ID
        public NetHandler(string connectionId = null)
        {
            _webSocket = null;
            _cancellationTokenSource = new CancellationTokenSource();
            ConnectionId = connectionId ?? Guid.NewGuid().ToString();
        }

        // Initialize connection parameters
        public void Initialize(string apiKey, string airport, string controllerId)
        {
            _apiKey = apiKey;
            _airport = airport;
            _controllerId = controllerId;
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
        }

        // Add state conversion helper
        private bool ConvertStopbarStateToNetwork(Stopbar stopbar)
        {
            return stopbar.State;
        }

        // Update stopbar state and send to server
        public async Task UpdateStopbar(Stopbar stopbar)
        {
            // Skip sending updates back to network if we're processing a network update
            if (_processingNetworkUpdate)
                return;

            string objectId;
            object packet;

            lock (_updateLock)
            {
                objectId = stopbar.BARSId;
                bool networkState = ConvertStopbarStateToNetwork(stopbar);

                // Update local state
                _localStopbarStates[objectId] = networkState;

                // Create packet inside lock but send it after
                packet = new
                {
                    type = "STATE_UPDATE",
                    airport = _airport,
                    data = new
                    {
                        objectId,
                        state = networkState
                    }
                };
            }

            // Send state update to server outside the lock
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await Task.Run(async () =>
                {
                    await SendPacket(packet);
                    logger.Log($"Sent stopbar state update for {objectId} to BARS server");
                });
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
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);

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
            catch (OperationCanceledException)
            {
                // Normal cancellation, do nothing
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Receive error: {ex.Message}");
                logger.Error($"WebSocket receive error: {ex.Message}");

                // Try to reconnect
                if (_isConnected)
                {
                    _isConnected = false;
                    OnConnectionChanged?.Invoke(this, false);
                    
                    try
                    {
                        await Task.Delay(3000); // Wait before reconnecting
                        await Connect();
                    }
                    catch { /* Suppress reconnection errors */ }
                }
            }
        }

        // Process received WebSocket messages
        private async void ProcessMessageAsync(string json)
        {
            try
            {
                dynamic message = JsonConvert.DeserializeObject(json);
                string messageType = message.type;

                switch (messageType)
                {
                    case "HEARTBEAT":
                        // Server sent a heartbeat, acknowledge it
                        await SendPacket(new
                        {
                            type = "HEARTBEAT_ACK"
                        });
                        break;

                    case "HEARTBEAT_ACK":
                        // Server acknowledged our heartbeat
                        _lastHeartbeatReceived = DateTime.Now;
                        break;

                    // Rest of the cases remain the same
                    case "INITIAL_STATE":
                        ProcessInitialState(message);
                        break;

                    case "STATE_UPDATE":
                        ProcessStateUpdate(message);
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
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Message processing error: {ex.Message}");
                logger.Log($"WebSocket message processing error: {ex.Message}");
            }
        }


        // Process initial state received from server
        private void ProcessInitialState(dynamic initialState)
        {
            try
            {
                // Extract connection type
                string connectionType = initialState.data.connectionType;
                logger.Log($"Connected as: {connectionType}");

                // Process received objects
                Dictionary<string, object> stopbarStates = new Dictionary<string, object>();
                
                if (initialState.data.objects != null && initialState.data.objects.Count > 0)
                {
                    foreach (var obj in initialState.data.objects)
                    {
                        string objectId = obj.id;
                        bool state = obj.state;
                        stopbarStates[objectId] = state;

                        // Register or update the stopbar with just the state
                        ControllerHandler.RegisterStopbar(
                            _airport,
                            objectId, // Using ID as display name since we don't receive it
                            objectId,
                            state,
                            false // Default autoRaise to false since we don't receive it
                        );
                    }
                    
                    logger.Log($"Received initial state with {stopbarStates.Count} stopbars for airport {_airport}");
                }
                else
                {
                    logger.Log($"Received empty initial state for airport {_airport}");
                }

                // Update the local state
                _localStopbarStates = stopbarStates;

                // Notify listeners
                OnStateUpdate?.Invoke(this, stopbarStates);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Initial state processing error: {ex.Message}");
                logger.Log($"Initial state processing error: {ex.Message}");
            }
        }

        // Process state updates from other controllers
        private async void ProcessStateUpdate(dynamic stateUpdate)
        {
            try
            {
                // Extract object details
                string objectId = stateUpdate.data.objectId;
                bool state = stateUpdate.data.state;
                
                // Skip if it's our own update
                string controllerId = stateUpdate.data.controllerId;
                if (controllerId == _controllerId)
                    return;

                // Add a small delay to let any pending local updates complete
                await Task.Delay(SERVER_UPDATE_DELAY);

                lock (_updateLock)
                {
                    // Update local state
                    _localStopbarStates[objectId] = state;
                    
                    // Set flag before updating controller and reset after
                    _processingNetworkUpdate = true;
                    try
                    {
                        // Update the ControllerHandler with just the state
                        // Use server state as authoritative
                        ControllerHandler.SetStopbarState(
                            _airport,
                            objectId,
                            state,
                            WindowType.Legacy, // Use Legacy as default for network updates
                            false // Default autoRaise to false since we don't receive it
                        );
                    }
                    finally
                    {
                        _processingNetworkUpdate = false;
                    }
                    
                    logger.Log($"Received state update for stopbar {objectId} from controller {controllerId}");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"State update processing error: {ex.Message}");
                logger.Log($"State update processing error: {ex.Message}");
            }
        }

        // Check if connected to the server
        public bool IsConnected()
        {
            return _isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open;
        }

        // Get all current stopbar states
        public Dictionary<string, object> GetAllStopbarStates()
        {
            return new Dictionary<string, object>(_localStopbarStates);
        }
    }
}
