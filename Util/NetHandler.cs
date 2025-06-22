using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BARS.Util
{
    public class NetHandler
    {
        private const int HEARTBEAT_INTERVAL = 30000;

        private const int HEARTBEAT_TIMEOUT = 60000;

        private const int SERVER_UPDATE_DELAY = 100;

        private readonly object _updateLock = new object();
        private readonly Logger logger = new Logger("NetHandler");

        private string _airport;

        private string _apiKey;

        private CancellationTokenSource _cancellationTokenSource;

        private string _controllerId;

        private System.Timers.Timer _heartbeatTimer;

        private bool _isConnected = false;

        private DateTime _lastHeartbeatReceived;

        private Dictionary<string, object> _localStopbarStates = new Dictionary<string, object>();

        private bool _processingNetworkUpdate = false;

        private ClientWebSocket _webSocket;

        public NetHandler(string connectionId = null)
        {
            _webSocket = null;
            _cancellationTokenSource = new CancellationTokenSource();
            ConnectionId = connectionId ?? Guid.NewGuid().ToString();
        }

        public delegate void ConnectionEventHandler(object sender, bool isConnected);

        public delegate void ErrorEventHandler(object sender, string errorMessage);

        public delegate void StateUpdateEventHandler(object sender, Dictionary<string, object> stopbarStates);

        public event ConnectionEventHandler OnConnectionChanged;

        public event ErrorEventHandler OnError;

        public event StateUpdateEventHandler OnStateUpdate;

        public string ConnectionId { get; private set; }

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

                string wsUrl = $"wss://v2.stopbars.com/connect?key={_apiKey}&airport={_airport}";
                Uri serverUri = new Uri(wsUrl);

                await _webSocket.ConnectAsync(serverUri, _cancellationTokenSource.Token);

                _isConnected = true;
                OnConnectionChanged?.Invoke(this, true);

                StartHeartbeat();

                _ = ReceiveMessagesAsync();

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

        public async Task Disconnect()
        {
            if (_webSocket == null)
                return;

            try
            {
                StopHeartbeat();

                if (_webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await SendPacket(new
                        {
                            type = "CLOSE"
                        });
                    }
                    catch (Exception)
                    {
                    }

                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "Client disconnecting",
                        CancellationToken.None);
                }

                _cancellationTokenSource.Cancel();

                try
                {
                    _webSocket.Dispose();
                }
                catch (Exception)
                {
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

                _webSocket = null;
                _isConnected = false;
                OnConnectionChanged?.Invoke(this, false);
            }
        }

        public Dictionary<string, object> GetAllStopbarStates()
        {
            return new Dictionary<string, object>(_localStopbarStates);
        }

        public void Initialize(string apiKey, string airport, string controllerId)
        {
            _apiKey = apiKey;
            _airport = airport;
            _controllerId = controllerId;
        }

        public bool IsConnected()
        {
            return _isConnected && _webSocket != null && _webSocket.State == WebSocketState.Open;
        }

        public async Task UpdateStopbar(Stopbar stopbar)
        {
            if (_processingNetworkUpdate)
                return;

            string objectId;
            object packet;

            lock (_updateLock)
            {
                objectId = stopbar.BARSId;
                bool networkState = ConvertStopbarStateToNetwork(stopbar);

                _localStopbarStates[objectId] = networkState;

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

            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await Task.Run(async () =>
                {
                    await SendPacket(packet);
                    logger.Log($"Sent stopbar state update for {objectId} to BARS server");
                });
            }
        }

        private bool ConvertStopbarStateToNetwork(Stopbar stopbar)
        {
            return stopbar.State;
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
                        string objectId = obj.id;
                        bool state = obj.state;
                        stopbarStates[objectId] = state;

                        ControllerHandler.RegisterStopbar(
                            _airport,
                            objectId,
                            objectId,
                            state,
                            false
                        );
                    }

                    logger.Log($"Received initial state with {stopbarStates.Count} stopbars for airport {_airport}");
                }
                else
                {
                    logger.Log($"Received empty initial state for airport {_airport}");
                }

                _localStopbarStates = stopbarStates;

                OnStateUpdate?.Invoke(this, stopbarStates);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, $"Initial state processing error: {ex.Message}");
                logger.Log($"Initial state processing error: {ex.Message}");
            }
        }

        private async void ProcessMessageAsync(string json)
        {
            try
            {
                dynamic message = JsonConvert.DeserializeObject(json);
                string messageType = message.type;

                switch (messageType)
                {
                    case "HEARTBEAT":

                        await SendPacket(new
                        {
                            type = "HEARTBEAT_ACK"
                        });
                        break;

                    case "HEARTBEAT_ACK":

                        _lastHeartbeatReceived = DateTime.Now;
                        break;

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

        private async void ProcessStateUpdate(dynamic stateUpdate)
        {
            try
            {
                string objectId = stateUpdate.data.objectId;
                bool state = stateUpdate.data.state;

                string controllerId = stateUpdate.data.controllerId;
                if (controllerId == _controllerId)
                    return;

                await Task.Delay(SERVER_UPDATE_DELAY);

                lock (_updateLock)
                {
                    _localStopbarStates[objectId] = state;

                    _processingNetworkUpdate = true;
                    try
                    {
                        ControllerHandler.SetStopbarState(
                            _airport,
                            objectId,
                            state,
                            WindowType.Legacy,
                            false
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
            }
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
                    catch { /* Suppress reconnection errors */ }
                }
            }
        }

        private async Task SendHeartbeat()
        {
            try
            {
                if ((DateTime.Now - _lastHeartbeatReceived).TotalMilliseconds > HEARTBEAT_TIMEOUT)
                {
                    logger.Log("Heartbeat timeout, reconnecting...");
                    await Disconnect();
                    await Task.Delay(1000);
                    await Connect();
                    return;
                }

                await SendPacket(new
                {
                    type = "HEARTBEAT"
                });
            }
            catch (Exception ex)
            {
                logger.Error($"Error sending heartbeat: {ex.Message}");

                try
                {
                    await Disconnect();
                    await Task.Delay(1000);
                    await Connect();
                }
                catch { /* Suppress reconnection errors */ }
            }
        }

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

        private void StartHeartbeat()
        {
            _lastHeartbeatReceived = DateTime.Now;

            _heartbeatTimer = new System.Timers.Timer(HEARTBEAT_INTERVAL);
            _heartbeatTimer.Elapsed += async (sender, e) => await SendHeartbeat();
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
        }

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