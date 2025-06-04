using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BARS.Util
{
    public class NetManager
    {
        private static NetManager _instance;
        private readonly Dictionary<string, NetHandler> _connections;
        private string _apiKey;

        public static NetManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new NetManager();
                }
                return _instance;
            }
        }

        private NetManager()
        {
            _connections = new Dictionary<string, NetHandler>();
        }

        public void Initialize(string apiKey)
        {
            _apiKey = apiKey;
        }

        public async Task<NetHandler> ConnectAirport(string airport, string controllerId)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                throw new InvalidOperationException("NetManager must be initialized with an API key first");
            }

            // Check if connection already exists
            if (_connections.TryGetValue(airport, out NetHandler existingHandler))
            {
                if (existingHandler.IsConnected())
                {
                    return existingHandler;
                }
                else
                {
                    // Remove disconnected handler
                    await DisconnectAirport(airport);
                }
            }

            // Create new connection
            var handler = new NetHandler(airport);
            handler.Initialize(_apiKey, airport, controllerId);
            
            bool connected = await handler.Connect();
            if (connected)
            {
                _connections[airport] = handler;
                return handler;
            }

            return null;
        }

        public async Task DisconnectAirport(string airport)
        {
            if (_connections.TryGetValue(airport, out NetHandler handler))
            {
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

        public NetHandler GetConnection(string airport)
        {
            _connections.TryGetValue(airport, out NetHandler handler);
            return handler;
        }

        public bool IsAirportConnected(string airport)
        {
            return _connections.TryGetValue(airport, out NetHandler handler) && handler.IsConnected();
        }

        public IEnumerable<string> GetConnectedAirports()
        {
            return _connections.Keys;
        }
    }
}