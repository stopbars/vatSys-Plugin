using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BARS.Util
{
    public class NetManager
    {
        private static NetManager _instance;
        private static readonly object _lock = new object();
        private readonly Dictionary<string, NetHandler> _connections;
        private string _apiKey;

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

        public bool IsAirportConnected(string airport)
        {
            return _connections.TryGetValue(airport, out NetHandler handler) && handler.IsConnected();
        }
    }
}