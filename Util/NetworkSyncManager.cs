using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BARS.Util
{
    public class NetworkSyncManager : IDisposable
    {
        private static readonly Lazy<NetworkSyncManager> _lazy = new Lazy<NetworkSyncManager>(() => new NetworkSyncManager());
        private static readonly Logger _logger = new Logger("NetworkSyncManager");
        private readonly TimeSpan _auditInterval = TimeSpan.FromSeconds(20);
        private readonly Timer _auditTimer;
        private readonly ConcurrentDictionary<string, DateTime> _lastToggle = new ConcurrentDictionary<string, DateTime>();
        private readonly TimeSpan _minToggleInterval = TimeSpan.FromMilliseconds(300);

        // anti-spam
        private int _auditRunning = 0;

        private NetworkSyncManager()
        {
            ControllerHandler.StopbarStateChanged += OnStopbarStateChanged;
            ControllerHandler.StopbarRegistered += OnStopbarRegistered;
            _auditTimer = new Timer(async _ => await AuditAsync(), null, _auditInterval, _auditInterval);
        }

        public static NetworkSyncManager Instance => _lazy.Value;

        public void Dispose()
        {
            _auditTimer?.Dispose();
            ControllerHandler.StopbarStateChanged -= OnStopbarStateChanged;
            ControllerHandler.StopbarRegistered -= OnStopbarRegistered;
        }

        private async Task AuditAsync()
        {
            if (Interlocked.Exchange(ref _auditRunning, 1) == 1) return;
            try
            {
                // For each connected airport, compare local stopbars vs network tracked state
                foreach (var airport in BARS.ControlledAirports.ToList())
                {
                    var net = NetManager.Instance.GetConnection(airport);
                    if (net == null || !net.IsConnected()) continue;
                    var local = ControllerHandler.GetStopbarsForAirport(airport);
                    var network = net.GetAllStopbarStates();

                    foreach (var sb in local)
                    {
                        if (network.TryGetValue(sb.BARSId, out object objState))
                        {
                            bool networkState = Convert.ToBoolean(objState);
                            if (networkState != sb.State)
                            {
                                _logger.Log($"Audit desync detected {sb.BARSId} airport {airport}: local={sb.State} server={networkState}. Repairing...");
                                // Decide repair strategy: trust server (authoritative)
                                ControllerHandler.SetStopbarState(airport, sb.BARSId, networkState, WindowType.Legacy, sb.AutoRaise);
                            }
                        }
                        else
                        {
                            _logger.Log($"Audit: stopbar {sb.BARSId} missing on server list for {airport}; pushing local state.");
                            await net.UpdateStopbar(sb);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Audit error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _auditRunning, 0);
            }
        }

        private void OnStopbarRegistered(object sender, StopbarEventArgs e)
        {
            // Ensure inverse invariant is documented
            if (!string.IsNullOrEmpty(e.Stopbar.LeadOnId))
            {
                _logger.Log($"Stopbar {e.Stopbar.BARSId} registered with lead-on {e.Stopbar.LeadOnId} (inverse invariant).");
            }
        }

        private void OnStopbarStateChanged(object sender, StopbarEventArgs e)
        {
            var stopbar = e.Stopbar;
            string key = stopbar.Airport + "|" + stopbar.BARSId;
            DateTime now = DateTime.UtcNow;

            // Anti rapid toggle: if too soon, revert and ignore
            if (_lastToggle.TryGetValue(key, out DateTime last) && (now - last) < _minToggleInterval)
            {
                _logger.Log($"Debouncing rapid toggle for {stopbar.BARSId} at {stopbar.Airport}");
                return; // We allow UI change, network layer already guards duplicate sends via timestamp diff
            }
            _lastToggle[key] = now;

            // Lead-on invariant is handled in NetHandler (inverse) when sending; ensure we log for trace
            if (!string.IsNullOrEmpty(stopbar.LeadOnId))
            {
                _logger.Log($"Invariant: LeadOn {stopbar.LeadOnId} considered {(stopbar.State ? "OFF" : "ON")} because Stopbar {stopbar.BARSId} is {(stopbar.State ? "ON" : "OFF")}");
            }
        }
    }
}