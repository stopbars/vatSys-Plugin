using BARS.Util;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace BARS.Windows
{
    public class AirportMapControl : Control
    {
        private const int ANIMATION_FRAMES = 60;
        private const int ANIMATION_INTERVAL = 100;
        private const int LEAD_ON_ANIMATION_MIN_DURATION_MS = 1000;
        private const int LEAD_ON_ANIMATION_MAX_DURATION_MS = 1500;
        private const int LEAD_ON_ANIMATION_FAST_MIN_DURATION_MS = 250;
        private const int LEAD_ON_ANIMATION_POINT_DURATION_BUDGET = 40;
        private const int LEAD_ON_ANIMATION_MAX_RENDER_POINTS = 120;

        private const float DEFAULT_ANGLE_SNAP_TOLERANCE_DEG = 7.5f;

        private const float LeadOnLineWidthOff = 0.5f;
        private const float LeadOnLineWidthOn = 1.0f;
        private const float MAX_ZOOM = 10.0f;
        private const float MIN_LINE_WIDTH = 0.5f;
        private const float MIN_ZOOM = 0.1f;
        private const int STOPBAR_BASE_SIZE = 16;
        private const float STOPBAR_MAX_SLIDE = 3f;
        private const float WINDSOCK_CLEARANCE_PX = 4f;
        private const float TaxiwayLineWidth = 1.0f;

        private const int MIN_FRAME_INTERVAL_MS = 16; // ~60fps cap
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly float[] ANGLE_SNAP_TARGETS = new float[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };

        private readonly Timer _animationTimer;
        private readonly Logger _logger = new Logger("AirportMapControl");
        private readonly Timer _windSimulationTimer;
        private readonly Color BackgroundColor = Color.Black;
        private readonly Color LeadOnGreenColor = Color.FromArgb(28, 208, 40);
        private readonly Color StopbarOffColor = Color.Gray;
        private readonly Color StopbarOnColor = Color.Red;
        private readonly Color TaxiwayColor = Color.FromArgb(28, 208, 40);

        private int _animationFrame = 0;
        private int _baseWindDirection = 0;

        private int _baseWindGust = 0;
        private int _baseWindSpeed = 0;
        private int _defaultCountdownSeconds = 45;

        private Dictionary<string, List<MapElement>> _groundElements = new Dictionary<string, List<MapElement>>();

        private bool _isDragging = false;

        private bool _isVariableWind = false;

        private Point _lastMousePosition;
        private Dictionary<string, LeadOnAnimationState> _leadOnAnimations = new Dictionary<string, LeadOnAnimationState>();
        private readonly Random _leadOnAnimationRandom = new Random();
        private AirportMapData _mapData;
        private PointF _panOffset = new PointF(0, 0);
        private List<RunwayInfo> _runways;
        private float _scalingRatio = 1.0f;
        private Dictionary<string, StopbarCountdownTimer> _stopbarCountdowns = new Dictionary<string, StopbarCountdownTimer>();
        private Random _windRandom = new Random();
        private Dictionary<Windsock, WindState> _windsockStates = new Dictionary<Windsock, WindState>();
        private Dictionary<Windsock, GeoPoint> _windsockSmartPositions = new Dictionary<Windsock, GeoPoint>();
        private bool _windsockSmartPositionsInitialized = false;
        private float _zoomLevel = 1.0f;
        private readonly Dictionary<string, float> _stopbarSlideOffsetsWorld = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _expiredCountdownBuffer = new List<string>();
        private bool _stopbarSlidesInitialized = false;
        private Dictionary<string, StopbarVisual> _cachedStopbarVisuals = new Dictionary<string, StopbarVisual>(StringComparer.OrdinalIgnoreCase);
        private RectangleF _cachedVisualBounds;
        private bool _hasCachedVisualBounds = false;
        private bool _visualCacheDirty = true;
        private readonly Dictionary<Windsock, RunwayInfo> _windsockNearestRunway = new Dictionary<Windsock, RunwayInfo>();

        private DateTime _lastPaintTime = DateTime.MinValue;
        private DateTime _lastZoomTime = DateTime.MinValue;
        private const int ZOOM_SETTLE_MS = 150; // Time after last zoom to restore quality
        private bool _isPanningOrZooming = false;
        private bool _pendingInvalidate = false;
        private Pen _taxiwayPen;
        private SolidBrush _groundAprBrush;
        private SolidBrush _groundTwyBrush;
        private SolidBrush _groundBldBrush;
        private SolidBrush _groundRwyBrush;

        public AirportMapControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = BackgroundColor;
            InitializeCachedResources();

            _animationTimer = new Timer();
            _animationTimer.Interval = ANIMATION_INTERVAL;
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();

            _windSimulationTimer = new Timer();
            _windSimulationTimer.Interval = _windRandom.Next(1000, 3000);
            _windSimulationTimer.Tick += WindSimulationTimer_Tick;
            _windSimulationTimer.Start();
            this.MouseWheel += OnMouseWheel;
        }

        private void InitializeCachedResources()
        {
            _taxiwayPen = new Pen(TaxiwayColor, TaxiwayLineWidth);
            _taxiwayPen.StartCap = LineCap.Flat;
            _taxiwayPen.EndCap = LineCap.Flat;
            _taxiwayPen.LineJoin = LineJoin.Round;

            _groundAprBrush = new SolidBrush(Color.FromArgb(83, 83, 83));
            _groundTwyBrush = new SolidBrush(Color.FromArgb(63, 63, 63));
            _groundBldBrush = new SolidBrush(Color.FromArgb(100, 43, 43));
            _groundRwyBrush = new SolidBrush(Color.Black);
        }

        private void DisposeCachedResources()
        {
            _taxiwayPen?.Dispose();
            _groundAprBrush?.Dispose();
            _groundTwyBrush?.Dispose();
            _groundBldBrush?.Dispose();
            _groundRwyBrush?.Dispose();
        }

        private void ThrottledInvalidate()
        {
            var now = DateTime.Now;
            var elapsed = (now - _lastPaintTime).TotalMilliseconds;

            if (elapsed >= MIN_FRAME_INTERVAL_MS)
            {
                _pendingInvalidate = false;
                Invalidate();
            }
            else
            {
                if (!_pendingInvalidate)
                {
                    _pendingInvalidate = true;
                }
            }
        }

        public event EventHandler<StopbarClickEventArgs> StopbarClicked;

        /// <summary>
        /// Enables snapping of stopbar image rotation to nice angles (0/45/90/etc.) when visually close.
        /// </summary>
        public bool AngleSnapEnabled { get; set; } = true;

        /// <summary>
        /// Degrees within which a stopbar rotation will snap to the nearest target (from ANGLE_SNAP_TARGETS).
        /// </summary>
        public float AngleSnapToleranceDegrees { get; set; } = DEFAULT_ANGLE_SNAP_TOLERANCE_DEG;

        public bool IsDragging => _isDragging;

        public int GetDefaultCountdownDuration()
        {
            return _defaultCountdownSeconds;
        }

        public PointF GetPan()
        {
            return _panOffset;
        }

        public MapStopbar GetStopbarAtPoint(Point clickPoint)
        {
            if (_mapData?.Stopbars == null || _mapData.Stopbars.Count == 0)
                return null;

            var bounds = CalculateSquareDrawingBounds();
            var visuals = GetStopbarVisuals(bounds);
            if (visuals.Count == 0)
                return null;

            MapStopbar closestStopbar = null;
            float closestDistance = float.MaxValue;

            float baseClickDistance = 50.0f;
            float scaledClickDistance = baseClickDistance * _zoomLevel;
            PointF clickPointF = new PointF(clickPoint.X, clickPoint.Y);

            foreach (var visual in visuals.Values)
            {
                float detectionRadius = Math.Max(scaledClickDistance, visual.ImageSize);
                float distance = Distance(clickPointF, visual.ScreenPosition);

                if (distance <= detectionRadius && distance < closestDistance)
                {
                    closestDistance = distance;
                    closestStopbar = visual.Stopbar;
                }
            }

            return closestStopbar;
        }

        public string GetNearestRunwayIdentForStopbar(string barsId)
        {
            if (string.IsNullOrWhiteSpace(barsId))
            {
                return null;
            }

            var stopbars = _mapData?.Stopbars;
            if (stopbars == null || stopbars.Count == 0)
            {
                return null;
            }

            MapStopbar target = stopbars.FirstOrDefault(sb => string.Equals(sb.BarsId, barsId, StringComparison.OrdinalIgnoreCase))
                                 ?? stopbars.FirstOrDefault(sb => string.Equals(sb.DisplayName, barsId, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                return null;
            }

            var runways = _runways;
            if (runways == null || runways.Count == 0)
            {
                if (_mapData != null)
                {
                    _ = FetchRunwaysAsync(_mapData.AirportIcao);
                }
                return null;
            }

            RunwayInfo nearest = null;
            double nearestDist = double.MaxValue;
            foreach (var rwy in runways)
            {
                double d = DistancePointToSegmentMeters(target.Position, rwy.Le, rwy.He);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = rwy;
                }
            }

            if (nearest == null)
            {
                return null;
            }

            double distToLe = DistancePointToSegmentMeters(target.Position, nearest.Le, nearest.Le);
            double distToHe = DistancePointToSegmentMeters(target.Position, nearest.He, nearest.He);

            string ident = null;
            if (distToHe <= distToLe)
            {
                ident = !string.IsNullOrWhiteSpace(nearest.HeIdent) ? nearest.HeIdent : nearest.LeIdent;
            }
            else
            {
                ident = !string.IsNullOrWhiteSpace(nearest.LeIdent) ? nearest.LeIdent : nearest.HeIdent;
            }

            return string.IsNullOrWhiteSpace(ident) ? null : ident.Trim();
        }

        public float GetZoom()
        {
            return _zoomLevel;
        }

        public void LoadAirportMap(string airportIcao)
        {
            try
            {
                _mapData = AirportMapData.LoadFromXml(airportIcao);
                ResetStopbarSlides();
                InvalidateStopbarVisualCache();
                ResetWindsockSmartPositions();

                _windsockStates.Clear();
                _windsockNearestRunway.Clear();
                if (_mapData?.Windsocks != null)
                {
                    foreach (var windsock in _mapData.Windsocks)
                    {
                        _windsockStates[windsock] = new WindState(_baseWindDirection, _baseWindSpeed);
                    }
                }

                Invalidate();

                _ = FetchRunwaysAsync(airportIcao);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load airport map for {airportIcao}: {ex.Message}");
                _mapData = null;
                _windsockNearestRunway.Clear();
                Invalidate();
            }
        }

        public void LoadGroundLayout(Dictionary<string, List<MapElement>> groundElements)
        {
            _groundElements = groundElements ?? new Dictionary<string, List<MapElement>>();
            ResetWindsockSmartPositions();
            Invalidate();
        }

        public void ResetZoomAndPan()
        {
            _zoomLevel = 1.0f;
            _panOffset = new PointF(0, 0);
            InvalidateStopbarVisualCache();
            Invalidate();
        }

        public void ZoomToFitContent(float paddingRatio = 0.08f)
        {
            if (_mapData == null || Width <= 0 || Height <= 0)
                return;

            var bounds = CalculateSquareDrawingBounds();
            if (bounds.Width <= 1f || bounds.Height <= 1f)
                return;

            var points = GetContentScreenPoints(bounds);
            if (points.Count == 0)
                return;

            float minX = points.Min(p => p.X);
            float maxX = points.Max(p => p.X);
            float minY = points.Min(p => p.Y);
            float maxY = points.Max(p => p.Y);

            float contentWidth = Math.Max(1f, maxX - minX);
            float contentHeight = Math.Max(1f, maxY - minY);
            float padding = Math.Max(0f, Math.Min(0.4f, paddingRatio));
            float availableWidth = Math.Max(1f, bounds.Width * (1f - (padding * 2f)));
            float availableHeight = Math.Max(1f, bounds.Height * (1f - (padding * 2f)));

            float zoom = Math.Min(availableWidth / contentWidth, availableHeight / contentHeight);
            zoom = Math.Min(1.0f, zoom);
            _zoomLevel = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, zoom));

            float boundsCenterX = bounds.X + bounds.Width / 2f;
            float boundsCenterY = bounds.Y + bounds.Height / 2f;
            float contentCenterX = (minX + maxX) / 2f;
            float contentCenterY = (minY + maxY) / 2f;

            _panOffset = new PointF(
                -(contentCenterX - boundsCenterX) * _zoomLevel,
                -(contentCenterY - boundsCenterY) * _zoomLevel);

            InvalidateStopbarVisualCache();
            ResetWindsockSmartPositions();
            Invalidate();
        }

        public void SetDefaultCountdownDuration(int seconds)
        {
            _defaultCountdownSeconds = Math.Max(1, Math.Min(300, seconds));
        }

        public void SetMapRotation(double rotationDegreesCW)
        {
            if (_mapData == null) return;
            _mapData.Rotation = rotationDegreesCW;
            try { _mapData.RecalculateBounds(); } catch { /* ignore if not yet ready */ }
            InvalidateStopbarVisualCache();
            ResetWindsockSmartPositions();
            Invalidate();
        }

        public void SetPan(PointF panOffset)
        {
            _panOffset = panOffset;
            InvalidateStopbarVisualCache();
            Invalidate();
        }

        public void SetScalingRatio(float ratio)
        {
            _scalingRatio = ratio;
            InvalidateStopbarVisualCache();
            Invalidate();
        }

        public void SetWind(int direction, int speed)
        {
            _baseWindDirection = direction;
            _baseWindSpeed = speed;
            _baseWindGust = Math.Max(speed, _baseWindGust);
            _isVariableWind = false;

            _logger.Log($"Wind manually set to: {direction}° at {speed} knots");

            if (_mapData?.Windsocks != null)
            {
                foreach (var windsock in _mapData.Windsocks)
                {
                    _windsockStates[windsock] = new WindState(direction, speed);
                }
            }

            Invalidate();
        }

        public void SetZoom(float zoomLevel)
        {
            _zoomLevel = Math.Max(MIN_ZOOM, Math.Min(MAX_ZOOM, zoomLevel));
            InvalidateStopbarVisualCache();
            Invalidate();
        }

        public void StartStopbarCountdown(string barsId, TimeSpan duration)
        {
            if (_stopbarCountdowns.ContainsKey(barsId))
            {
                _stopbarCountdowns.Remove(barsId);
            }

            _stopbarCountdowns[barsId] = new StopbarCountdownTimer(barsId, duration);
            _logger.Log($"Started countdown timer for stopbar {barsId} with duration {duration.TotalSeconds:F0} seconds");
            _logger.Log($"Total active countdowns: {_stopbarCountdowns.Count}");

            Invalidate();
        }

        public void StartStopbarCountdown(string barsId, int seconds)
        {
            StartStopbarCountdown(barsId, TimeSpan.FromSeconds(seconds));
        }

        public void StopStopbarCountdown(string barsId)
        {
            if (_stopbarCountdowns.ContainsKey(barsId))
            {
                _stopbarCountdowns.Remove(barsId);
                _logger.Log($"Stopped countdown timer for stopbar {barsId}");
                Invalidate();
            }
        }

        public void UpdateLeadOnLight(string leadOnId, bool stopbarActive)
        {
            UpdateLeadOnLight(leadOnId, stopbarActive, DateTime.Now, GetRandomLeadOnAnimationDurationSeconds(), true);
        }

        private void UpdateLeadOnLight(string leadOnId, bool stopbarActive, DateTime animationStartTime, double animationDurationSeconds, bool invalidate)
        {
            if (_mapData != null)
            {
                _mapData.UpdateLeadOnLightColor(leadOnId, stopbarActive);

                if (!_leadOnAnimations.ContainsKey(leadOnId))
                {
                    _leadOnAnimations[leadOnId] = new LeadOnAnimationState();
                }

                var animState = _leadOnAnimations[leadOnId];
                bool currentState = !stopbarActive;
                bool previousStopbarState = !animState.PreviousState;
                if (!previousStopbarState && stopbarActive)
                {
                    var leadOn = _mapData.LeadOnLights.Find(l => l.Id == leadOnId);
                    if (leadOn != null)
                    {
                        float totalLength = CalculateLeadOnLength(leadOn);
                        bool wasAnimating = animState.IsAnimating;

                        animState.TotalLength = totalLength;
                        float startLength = Math.Max(0f, Math.Min(totalLength, animState.ProgressLength));
                        if (!wasAnimating)
                        {
                            startLength = totalLength;
                        }

                        animState.IsReverse = true;

                        if (Math.Abs(startLength) <= 0.01f)
                        {
                            animState.StartLength = 0f;
                            animState.ProgressLength = 0f;
                            animState.IsAnimating = false;
                        }
                        else
                        {
                            animState.IsAnimating = true;
                            animState.StartTime = animationStartTime;
                            animState.StartLength = startLength;
                            animState.ProgressLength = startLength;
                            animState.DurationSeconds = GetLeadOnAnimationDurationSeconds(leadOn, animationDurationSeconds);
                            _logger.Log($"Starting reverse animation for lead-on {leadOnId}, length: {animState.TotalLength:F1}m (from {startLength:F1}m), duration: {animState.DurationSeconds:F2}s");
                        }
                    }
                }
                else if (previousStopbarState && !stopbarActive)
                {
                    var leadOn = _mapData.LeadOnLights.Find(l => l.Id == leadOnId);
                    if (leadOn != null)
                    {
                        float totalLength = CalculateLeadOnLength(leadOn);
                        bool wasAnimating = animState.IsAnimating;

                        animState.TotalLength = totalLength;
                        float startLength = Math.Max(0f, Math.Min(totalLength, animState.ProgressLength));
                        if (!wasAnimating)
                        {
                            startLength = 0f;
                        }

                        animState.IsReverse = false;

                        if (Math.Abs(startLength - totalLength) <= 0.01f)
                        {
                            animState.StartLength = totalLength;
                            animState.ProgressLength = totalLength;
                            animState.IsAnimating = false;
                        }
                        else
                        {
                            animState.IsAnimating = true;
                            animState.StartTime = animationStartTime;
                            animState.StartLength = startLength;
                            animState.ProgressLength = startLength;
                            animState.DurationSeconds = GetLeadOnAnimationDurationSeconds(leadOn, animationDurationSeconds);
                            _logger.Log($"Starting forward animation for lead-on {leadOnId}, length: {animState.TotalLength:F1}m (from {startLength:F1}m), duration: {animState.DurationSeconds:F2}s");
                        }
                    }
                }

                animState.PreviousState = currentState;

                if (invalidate)
                {
                    Invalidate();
                }
            }
        }

        public void UpdateLeadOnLightsForStopbar(string barsId, bool stopbarActive)
        {
            if (_mapData == null || _mapData.Stopbars == null)
                return;

            var stopbar = _mapData.Stopbars.FirstOrDefault(s => s.BarsId == barsId);
            if (stopbar == null)
            {
                // Fallback: compute aggregated state for a lead-on with same id as barsId (if any)
                bool aggregatedActive = IsLeadOnStopbarActive(barsId);
                UpdateLeadOnLight(barsId, aggregatedActive);
                return;
            }

            if (stopbar.LeadOnIds != null && stopbar.LeadOnIds.Count > 0)
            {
                DateTime animationStartTime = DateTime.Now;
                double animationDurationSeconds = GetRandomLeadOnAnimationDurationSeconds();

                // Aggregate across all stopbars that reference each lead-on: lead-on is OFF only when all controlling stopbars are active (raised)
                foreach (string leadOnId in stopbar.LeadOnIds)
                {
                    bool aggregatedActive = IsLeadOnStopbarActive(leadOnId);
                    UpdateLeadOnLight(leadOnId, aggregatedActive, animationStartTime, animationDurationSeconds, false);
                }
                _logger.Log($"Updated {stopbar.LeadOnIds.Count} lead-on lights for stopbar {barsId}");
                Invalidate();
            }
            else
            {
                bool aggregatedActive = IsLeadOnStopbarActive(barsId);
                UpdateLeadOnLight(barsId, aggregatedActive);
            }
        }

        public void UpdateStopbarState(string barsId, bool state)
        {
            UpdateStopbarState(barsId, state, true);
        }

        public void UpdateStopbarState(string barsId, bool state, bool autoRaise)
        {
            if (_mapData == null || _mapData.Stopbars == null)
                return;

            var stopbar = _mapData.Stopbars.FirstOrDefault(s => s.BarsId == barsId);
            if (stopbar != null)
            {
                bool previous = stopbar.State;
                stopbar.State = state;

                // Manage countdowns on actual transitions only to avoid race conditions during rapid clicks
                if (previous != state)
                {
                    if (state)
                    {
                        // Raised -> cancel any active countdown
                        StopStopbarCountdown(barsId);
                    }
                    else
                    {
                        // Dropped -> only start countdown if auto-raise is enabled
                        if (autoRaise)
                        {
                            if (!_stopbarCountdowns.ContainsKey(barsId) || !_stopbarCountdowns[barsId].IsActive)
                            {
                                _logger.Log($"Starting countdown for stopbar {barsId}");
                                StartStopbarCountdown(barsId, TimeSpan.FromSeconds(_defaultCountdownSeconds));
                            }
                        }
                        else
                        {
                            // Ensure no countdown when auto-raise is disabled
                            StopStopbarCountdown(barsId);
                        }
                    }
                }

                Invalidate();
            }
        }

        public void UpdateWindFromMetar(string metarText)
        {
            if (string.IsNullOrEmpty(metarText))
                return;

            try
            {
                // Capture VRB or 3-digit dir, sustained speed, optional gust
                var windMatch = System.Text.RegularExpressions.Regex.Match(metarText, @"(VRB|\d{3})(\d{2,3})(?:G(\d{2,3}))?KT");

                if (windMatch.Success)
                {
                    string dirToken = windMatch.Groups[1].Value;
                    bool vrb = string.Equals(dirToken, "VRB", StringComparison.OrdinalIgnoreCase);
                    int direction = _baseWindDirection;
                    if (!vrb)
                    {
                        int.TryParse(dirToken, out direction);
                    }

                    int speed = 0;
                    int.TryParse(windMatch.Groups[2].Value, out speed);
                    int gust = 0;
                    if (windMatch.Groups.Count >= 4)
                    {
                        int.TryParse(windMatch.Groups[3].Value, out gust);
                    }

                    _baseWindDirection = NormalizeDegrees(direction);
                    _baseWindSpeed = Math.Max(0, speed);
                    _baseWindGust = Math.Max(_baseWindSpeed, Math.Max(0, gust));
                    _isVariableWind = vrb;

                    var gustText = gust > 0 ? $" G{gust}KT" : string.Empty;
                    var vrbText = vrb ? " VRB" : string.Empty;
                    _logger.Log($"Updated wind from METAR:{vrbText} {_baseWindDirection:000}° {_baseWindSpeed}KT{gustText}");

                    if (_mapData?.Windsocks != null)
                    {
                        foreach (var windsock in _mapData.Windsocks)
                        {
                            // preserve turbulence per windsock if it exists
                            if (_windsockStates.TryGetValue(windsock, out var prev))
                            {
                                _windsockStates[windsock] = new WindState(_baseWindDirection, _baseWindSpeed)
                                {
                                    TurbulenceFactor = prev.TurbulenceFactor,
                                    GustActive = false,
                                    GustUntil = DateTime.MinValue,
                                    GustSpeed = _baseWindGust
                                };
                            }
                            else
                            {
                                _windsockStates[windsock] = new WindState(_baseWindDirection, _baseWindSpeed)
                                {
                                    GustSpeed = _baseWindGust
                                };
                            }
                        }
                    }
                }
                else
                {
                    var calmMatch = System.Text.RegularExpressions.Regex.Match(metarText, @"00000KT");
                    if (calmMatch.Success)
                    {
                        _baseWindDirection = 0;
                        _baseWindSpeed = 0;
                        _baseWindGust = 0;
                        _isVariableWind = false;

                        _logger.Log("Wind conditions: CALM");

                        if (_mapData?.Windsocks != null)
                        {
                            foreach (var windsock in _mapData.Windsocks)
                            {
                                if (_windsockStates.TryGetValue(windsock, out var prev))
                                {
                                    _windsockStates[windsock] = new WindState(0, 0)
                                    {
                                        TurbulenceFactor = prev.TurbulenceFactor,
                                        GustActive = false,
                                        GustUntil = DateTime.MinValue,
                                        GustSpeed = 0
                                    };
                                }
                                else
                                {
                                    _windsockStates[windsock] = new WindState(0, 0);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error parsing wind from METAR: {ex.Message}");
            }

            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animationTimer?.Stop();
                _animationTimer?.Dispose();
                _windSimulationTimer?.Stop();
                _windSimulationTimer?.Dispose();
                DisposeCachedResources();
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);

            this.Focus();
            if (e.Button != MouseButtons.Middle)
            {
                var clickedStopbar = GetStopbarAtPoint(e.Location);
                if (clickedStopbar != null)
                {
                    _logger.Log($"Stopbar {clickedStopbar.BarsId} clicked with {e.Button}, current state: {clickedStopbar.State}");

                    StopbarClicked?.Invoke(this, new StopbarClickEventArgs(clickedStopbar.BarsId, e.Button));
                }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);

            if (e.Button == MouseButtons.Middle)
            {
                ResetZoomAndPan();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            this.Focus();

            if (e.Button == MouseButtons.Middle)
            {
                _isDragging = true;
                _isPanningOrZooming = true;
                _lastMousePosition = e.Location;
                this.Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            if (_isDragging)
            {
                _isDragging = false;
                _isPanningOrZooming = false;
                this.Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isDragging && e.Button == MouseButtons.Middle)
            {
                int deltaX = e.X - _lastMousePosition.X;
                int deltaY = e.Y - _lastMousePosition.Y;

                _panOffset.X += deltaX;
                _panOffset.Y += deltaY;

                _lastMousePosition = e.Location;

                InvalidateStopbarVisualCache();
                ThrottledInvalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Middle && _isDragging)
            {
                _isDragging = false;
                _isPanningOrZooming = false;
                this.Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            InvalidateStopbarVisualCache();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _lastPaintTime = DateTime.Now;

            // Use lower quality during rapid motion so large lead-on animations do not stretch past their duration.
            if (_isPanningOrZooming)
            {
                e.Graphics.SmoothingMode = SmoothingMode.HighSpeed;
                e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
            }
            else
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            }

            var squareBounds = CalculateSquareDrawingBounds();

            if (_mapData == null && _groundElements.Count > 0)
            {
                DrawGroundLayoutFallback(e.Graphics, squareBounds);
                return;
            }

            if (_mapData == null)
            {
                DrawNoMapMessage(e.Graphics);
                return;
            }

            DrawGroundLayout(e.Graphics, squareBounds);

            DrawTaxiways(e.Graphics, squareBounds);

            DrawLeadOnLights(e.Graphics, squareBounds);

            DrawWindsocks(e.Graphics, squareBounds);

            var stopbarVisuals = GetStopbarVisuals(squareBounds);

            DrawStopbars(e.Graphics, stopbarVisuals);

            DrawStopbarCountdownLabels(e.Graphics, stopbarVisuals);
        }

        private bool HasActiveLeadOnAnimations()
        {
            return _leadOnAnimations.Values.Any(a => a.IsAnimating);
        }

        private static double AngleDifferenceDegrees(double a, double b)
        {
            double d = (a - b) % 360.0;
            if (d < -180) d += 360;
            if (d > 180) d -= 360;
            return Math.Abs(d);
        }

        private static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
        {
            double φ1 = DegToRad(lat1);
            double φ2 = DegToRad(lat2);
            double Δλ = DegToRad(lon2 - lon1);
            double y = Math.Sin(Δλ) * Math.Cos(φ2);
            double x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
            double θ = Math.Atan2(y, x);
            double brng = (θ * 180.0 / Math.PI + 360.0) % 360.0;
            return brng;
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;

        private static double DeltaAngle(double current, double target)
        {
            double d = (target - current) % 360.0;
            if (d > 180.0) d -= 360.0;
            if (d < -180.0) d += 360.0;
            return d;
        }

        private static float DeltaAngleF(float current, float target)
        {
            float d = (target - current) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        private static double DistancePointToSegmentMeters(GeoPoint p, GeoPoint a, GeoPoint b)
        {
            // Project lat/lon to local tangent plane (equirectangular) around point p
            double lat0 = p.Latitude * Math.PI / 180.0;
            double mPerDegLat = 111320.0;
            double mPerDegLon = Math.Cos(lat0) * 111320.0;

            var Ax = (a.Longitude - p.Longitude) * mPerDegLon;
            var Ay = (a.Latitude - p.Latitude) * mPerDegLat;
            var Bx = (b.Longitude - p.Longitude) * mPerDegLon;
            var By = (b.Latitude - p.Latitude) * mPerDegLat;
            var Px = 0.0;
            var Py = 0.0;
            double ABx = Bx - Ax;
            double ABy = By - Ay;
            double APx = Px - Ax;
            double APy = Py - Ay;
            double ab2 = ABx * ABx + ABy * ABy;
            if (ab2 <= 1e-6)
            {
                return Math.Sqrt(APx * APx + APy * APy);
            }
            double t = (APx * ABx + APy * ABy) / ab2;
            t = Math.Max(0, Math.Min(1, t));
            double Cx = Ax + t * ABx;
            double Cy = Ay + t * ABy;
            double dx = Px - Cx;
            double dy = Py - Cy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double MoveTowards(double current, double target, double maxDelta)
        {
            double delta = target - current;
            if (Math.Abs(delta) <= maxDelta) return target;
            return current + Math.Sign(delta) * maxDelta;
        }

        private static double MoveTowardsAngle(double current, double target, double maxDelta)
        {
            double c = NormalizeDegrees((int)Math.Round(current));
            double t = NormalizeDegrees((int)Math.Round(target));
            double delta = DeltaAngle(c, t);
            if (Math.Abs(delta) <= maxDelta) return t;
            return NormalizeDegrees((int)Math.Round(c + Math.Sign(delta) * maxDelta));
        }

        private static float NormalizeAngleF(float deg)
        {
            float d = deg % 360f;
            if (d < 0f) d += 360f;
            return d;
        }

        private static int NormalizeDegrees(int deg)
        {
            int d = deg % 360;
            if (d < 0) d += 360;
            return d;
        }

        private static bool TryParseDouble(string s, out double v)
        {
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            _animationFrame = (_animationFrame + 1) % ANIMATION_FRAMES;

            bool needsRepaint = false;
            foreach (var kvp in _leadOnAnimations)
            {
                var animState = kvp.Value; if (animState.IsAnimating)
                {
                    // If there is no measurable length, finish immediately
                    if (animState.TotalLength <= 0f)
                    {
                        animState.IsAnimating = false;
                        animState.ProgressLength = 0f;
                        needsRepaint = true;
                        continue;
                    }
                    var elapsedSec = (DateTime.Now - animState.StartTime).TotalSeconds;
                    double durationSec = Math.Max(0.001, animState.DurationSeconds);
                    float animationRatio = Math.Min(1f, (float)(elapsedSec / durationSec));
                    float startLength = Math.Max(0f, Math.Min(animState.TotalLength, animState.StartLength));

                    if (animState.IsReverse)
                    {
                        float animationDelta = startLength * animationRatio;
                        float newProgress = startLength - animationDelta;
                        if (newProgress <= 0f)
                        {
                            animState.IsAnimating = false;
                            animState.ProgressLength = 0f;
                            animState.StartLength = 0f;
                        }
                        else
                        {
                            animState.ProgressLength = Math.Max(0f, newProgress);
                        }
                    }
                    else
                    {
                        float remainingLength = animState.TotalLength - startLength;
                        float animationDelta = remainingLength * animationRatio;
                        float newProgress = startLength + animationDelta;
                        if (animState.TotalLength > 0f && newProgress >= animState.TotalLength)
                        {
                            animState.IsAnimating = false;
                            animState.ProgressLength = animState.TotalLength;
                            animState.StartLength = animState.TotalLength;
                        }
                        else
                        {
                            animState.ProgressLength = Math.Min(animState.TotalLength, newProgress);
                        }
                    }

                    needsRepaint = true;
                }
            }

            _expiredCountdownBuffer.Clear();
            foreach (var kvp in _stopbarCountdowns)
            {
                if (!kvp.Value.IsActive)
                {
                    _expiredCountdownBuffer.Add(kvp.Key);
                }
            }

            if (_expiredCountdownBuffer.Count > 0)
            {
                for (int i = 0; i < _expiredCountdownBuffer.Count; i++)
                {
                    _stopbarCountdowns.Remove(_expiredCountdownBuffer[i]);
                }
                needsRepaint = true;
            }

            if (_stopbarCountdowns.Count > 0)
            {
                needsRepaint = true;
            }

            // Handle deferred invalidate from throttling
            if (_pendingInvalidate)
            {
                _pendingInvalidate = false;
                needsRepaint = true;
            }

            // Check if zooming has settled - restore quality and do final repaint
            if (_isPanningOrZooming && !_isDragging && _lastZoomTime != DateTime.MinValue)
            {
                var zoomElapsed = (DateTime.Now - _lastZoomTime).TotalMilliseconds;
                if (zoomElapsed >= ZOOM_SETTLE_MS)
                {
                    _isPanningOrZooming = false;
                    _lastZoomTime = DateTime.MinValue;
                    needsRepaint = true; // Final high-quality repaint
                }
            }

            if (needsRepaint)
            {
                Invalidate();
            }
        }

        private double GetRandomLeadOnAnimationDurationSeconds()
        {
            int durationMs = _leadOnAnimationRandom.Next(
                LEAD_ON_ANIMATION_MIN_DURATION_MS,
                LEAD_ON_ANIMATION_MAX_DURATION_MS + 1);
            return durationMs / 1000.0;
        }

        private double GetLeadOnAnimationDurationSeconds(LeadOnLight leadOn, double requestedDurationSeconds)
        {
            int pointCount = leadOn?.Line?.Points?.Count ?? 0;
            int segmentCount = Math.Max(1, pointCount - 1);

            if (segmentCount <= LEAD_ON_ANIMATION_POINT_DURATION_BUDGET)
            {
                return requestedDurationSeconds;
            }

            double scaledDuration = requestedDurationSeconds * LEAD_ON_ANIMATION_POINT_DURATION_BUDGET / segmentCount;
            return Math.Max(LEAD_ON_ANIMATION_FAST_MIN_DURATION_MS / 1000.0, scaledDuration);
        }

        private float CalculateLeadOnLength(LeadOnLight leadOn)
        {
            if (leadOn.Line.Points.Count < 2) return 0;

            float totalLength = 0;
            for (int i = 1; i < leadOn.Line.Points.Count; i++)
            {
                var point1 = leadOn.Line.Points[i - 1];
                var point2 = leadOn.Line.Points[i];

                double lat1Rad = point1.Latitude * Math.PI / 180.0;
                double lat2Rad = point2.Latitude * Math.PI / 180.0;
                double deltaLat = (point2.Latitude - point1.Latitude) * Math.PI / 180.0;
                double deltaLon = (point2.Longitude - point1.Longitude) * Math.PI / 180.0;

                double a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2) +
                          Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                          Math.Sin(deltaLon / 2) * Math.Sin(deltaLon / 2);
                double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
                double distance = 6371000 * c;

                totalLength += (float)distance;
            }

            return totalLength;
        }

        private RectangleF CalculateSquareDrawingBounds()
        {
            float minDimension = Math.Min(Width, Height);
            float centerX = Width / 2.0f;
            float centerY = Height / 2.0f;

            return new RectangleF(
                centerX - minDimension / 2.0f,
                centerY - minDimension / 2.0f,
                minDimension,
                minDimension
            );
        }

        private List<PointF> GetContentScreenPoints(RectangleF bounds)
        {
            var points = new List<PointF>();
            PointF originPan = new PointF(0, 0);

            void AddGeoPoint(GeoPoint geoPoint)
            {
                if (geoPoint == null)
                    return;

                PointF screenPoint = _mapData.GeoToScreen(geoPoint, bounds, 1.0f, originPan);
                if (!float.IsNaN(screenPoint.X) && !float.IsInfinity(screenPoint.X) &&
                    !float.IsNaN(screenPoint.Y) && !float.IsInfinity(screenPoint.Y))
                {
                    points.Add(screenPoint);
                }
            }

            if (_mapData.Taxiways?.Lines != null)
            {
                foreach (var line in _mapData.Taxiways.Lines)
                {
                    foreach (var point in line.Points)
                    {
                        AddGeoPoint(point);
                    }
                }
            }

            if (_mapData.LeadOnLights != null)
            {
                foreach (var leadOn in _mapData.LeadOnLights)
                {
                    foreach (var point in leadOn.Line.Points)
                    {
                        AddGeoPoint(point);
                    }
                }
            }

            if (_mapData.Windsocks != null)
            {
                foreach (var windsock in _mapData.Windsocks)
                {
                    AddGeoPoint(windsock.Position);
                }
            }

            if (_mapData.Stopbars != null)
            {
                foreach (var stopbar in _mapData.Stopbars)
                {
                    AddGeoPoint(stopbar.Position);
                }
            }

            foreach (var elementType in _groundElements.Values)
            {
                foreach (var element in elementType)
                {
                    foreach (var point in element.Points)
                    {
                        AddGeoPoint(new GeoPoint(point.Longitude, point.Latitude));
                    }
                }
            }

            return points;
        }

        private void InvalidateStopbarVisualCache()
        {
            _visualCacheDirty = true;
        }

        private void ResetWindsockSmartPositions()
        {
            _windsockSmartPositions.Clear();
            _windsockSmartPositionsInitialized = false;
        }

        private void RebuildWindsockRunwayCache()
        {
            _windsockNearestRunway.Clear();
            if (_runways == null || _runways.Count == 0)
                return;
            if (_mapData?.Windsocks == null || _mapData.Windsocks.Count == 0)
                return;

            foreach (var windsock in _mapData.Windsocks)
            {
                RunwayInfo nearest = null;
                double nearestDist = double.MaxValue;
                foreach (var rwy in _runways)
                {
                    double d = DistancePointToSegmentMeters(windsock.Position, rwy.Le, rwy.He);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearest = rwy;
                    }
                }

                if (nearest != null)
                {
                    _windsockNearestRunway[windsock] = nearest;
                }
            }
        }

        /// <summary>
        /// Computes crosswind and tailwind components for the windsock against the nearest runway.
        /// Also ensures the runway heading used matches the closest runway end (e.g., HE -> use LE→HE heading and HE ident, LE -> use HE→LE).
        /// </summary>
        /// <param name="windsock">The windsock to evaluate.</param>
        /// <param name="crosswind">Absolute crosswind component in knots.</param>
        /// <param name="tailwind">Tailwind component in knots (>= 0). 0 when headwind or calm.</param>
        private void ComputeWindComponentsForWindsock(Windsock windsock, out float crosswind, out float tailwind)
        {
            crosswind = 0f;
            tailwind = 0f;
            if (_runways == null || _runways.Count == 0) return;
            if (!_windsockStates.TryGetValue(windsock, out var wind)) return;

            _windsockNearestRunway.TryGetValue(windsock, out var nearest);
            if (nearest == null)
            {
                // Choose nearest runway by shortest distance to runway segment in a local tangent plane
                double nearestDist = double.MaxValue;
                foreach (var rwy in _runways)
                {
                    double d = DistancePointToSegmentMeters(windsock.Position, rwy.Le, rwy.He);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearest = rwy;
                    }
                }

                if (nearest != null)
                {
                    _windsockNearestRunway[windsock] = nearest;
                }
            }
            if (nearest == null) return;

            int windDir = NormalizeDegrees(wind.CurrentDirection);
            int windSpd = Math.Max(0, wind.CurrentSpeed);
            if (windSpd <= 0) return;

            // Pick runway direction from the nearest runway END using runway ident -> heading (e.g., "34" => 340°)
            // This reflects local conditions at each end: if you're near 34 with 160 wind, it'll show tailwind.
            double distToLE = DistancePointToSegmentMeters(windsock.Position, nearest.Le, nearest.Le);
            double distToHE = DistancePointToSegmentMeters(windsock.Position, nearest.He, nearest.He);

            int rwyHeadingDeg;
            if (distToHE <= distToLE)
            {
                if (!TryGetHeadingFromIdent(nearest.HeIdent, out rwyHeadingDeg))
                {
                    // Fallback: geometric bearing LE->HE
                    rwyHeadingDeg = (int)Math.Round(nearest.Heading);
                }
            }
            else
            {
                if (!TryGetHeadingFromIdent(nearest.LeIdent, out rwyHeadingDeg))
                {
                    // Fallback: geometric bearing HE->LE
                    rwyHeadingDeg = (int)Math.Round(BearingDegrees(nearest.He.Latitude, nearest.He.Longitude, nearest.Le.Latitude, nearest.Le.Longitude));
                }
            }

            double delta = AngleDifferenceDegrees(windDir, rwyHeadingDeg);
            double cross = Math.Abs(windSpd * Math.Sin(DegToRad(delta)));
            double head = windSpd * Math.Cos(DegToRad(delta)); // positive = headwind
            double tail = Math.Max(0.0, -head);
            crosswind = (float)cross;
            tailwind = (float)tail;
        }

        private static bool TryGetHeadingFromIdent(string ident, out int headingDeg)
        {
            headingDeg = 0;
            if (string.IsNullOrWhiteSpace(ident)) return false;
            int i = 0;
            while (i < ident.Length && char.IsDigit(ident[i])) i++;
            if (i == 0) return false;
            if (!int.TryParse(ident.Substring(0, i), out int num)) return false;
            if (num == 36) headingDeg = 0;
            else headingDeg = (num % 36) * 10;
            if (headingDeg < 0) headingDeg = (headingDeg % 360 + 360) % 360;
            else headingDeg = headingDeg % 360;
            return true;
        }

        private void DrawAnimatedLeadOn(Graphics g, Pen pen, LeadOnLight leadOn, PointF[] screenPoints, LeadOnAnimationState animState)
        {
            if (screenPoints.Length < 2) return;
            g.DrawLines(pen, screenPoints);

            if (animState.TotalLength <= 0f || animState.ProgressLength <= 0f)
            {
                return;
            }

            float scaledAnimationWidth = Math.Max(MIN_LINE_WIDTH, TaxiwayLineWidth * _zoomLevel * _scalingRatio);
            using (var animationPen = new Pen(TaxiwayColor, scaledAnimationWidth))
            {
                animationPen.StartCap = LineCap.Flat;
                animationPen.EndCap = LineCap.Flat;
                animationPen.LineJoin = LineJoin.Round;

                float totalScreenLength = 0;

                for (int i = 1; i < screenPoints.Length; i++)
                {
                    totalScreenLength += Distance(screenPoints[i - 1], screenPoints[i]);
                }

                if (totalScreenLength <= 0f) return;

                float progressRatio = Math.Min(1.0f, animState.ProgressLength / animState.TotalLength);
                float targetScreenLength = totalScreenLength * progressRatio;
                if (targetScreenLength <= 0f) return;

                float minSegmentSize = 35.0f;
                float segmentSize = Math.Max(minSegmentSize, totalScreenLength * 0.1f);

                float currentLength = 0;
                var pointsToDrawEnd = new List<PointF>();

                for (int i = screenPoints.Length - 1; i > 0; i--)
                {
                    float segmentLength = Distance(screenPoints[i - 1], screenPoints[i]);
                    if (segmentLength <= 0f)
                    {
                        continue;
                    }

                    if (currentLength + segmentLength <= targetScreenLength)
                    {
                        if (pointsToDrawEnd.Count == 0)
                        {
                            pointsToDrawEnd.Add(screenPoints[i]);
                        }
                        pointsToDrawEnd.Add(screenPoints[i - 1]);
                        currentLength += segmentLength;
                    }
                    else if (currentLength < targetScreenLength)
                    {
                        float remainingLength = targetScreenLength - currentLength;

                        float extendedLength = Math.Min(remainingLength + segmentSize, segmentLength);
                        float ratio = extendedLength / segmentLength;

                        float x = screenPoints[i].X + (screenPoints[i - 1].X - screenPoints[i].X) * ratio;
                        float y = screenPoints[i].Y + (screenPoints[i - 1].Y - screenPoints[i].Y) * ratio;

                        if (pointsToDrawEnd.Count == 0)
                        {
                            pointsToDrawEnd.Add(screenPoints[i]);
                        }
                        pointsToDrawEnd.Add(new PointF(x, y));
                        break;
                    }
                    else
                    {
                        break;
                    }
                }

                if (pointsToDrawEnd.Count >= 2)
                {
                    pointsToDrawEnd.Reverse();
                    g.DrawLines(animationPen, pointsToDrawEnd.ToArray());
                }
            }
        }

        private void DrawGroundLayout(Graphics g, RectangleF bounds)
        {
            if (_groundElements.Count == 0) return;

            DrawGroundLayoutWithSimpleProjection(g, bounds);
        }

        private void DrawGroundLayoutFallback(Graphics g, RectangleF bounds)
        {
            if (_groundElements.Count == 0) return;

            var allCoords = new List<Coordinate>();
            foreach (var elementType in _groundElements)
            {
                foreach (var element in elementType.Value)
                {
                    allCoords.AddRange(element.Points);
                }
            }

            if (allCoords.Count == 0) return;

            double minLat = allCoords.Min(c => c.Latitude);
            double maxLat = allCoords.Max(c => c.Latitude);
            double minLon = allCoords.Min(c => c.Longitude);
            double maxLon = allCoords.Max(c => c.Longitude);

            double latRange = maxLat - minLat;
            double lonRange = maxLon - minLon; if (latRange == 0 || lonRange == 0) return;

            var renderOrder = new[] { "GROUND_APR", "GROUND_TWY", "GROUND_BLD", "GROUND_RWY" };

            foreach (var elementTypeName in renderOrder)
            {
                var actualKey = _groundElements.Keys.FirstOrDefault(k =>
                    string.Equals(k, elementTypeName, StringComparison.OrdinalIgnoreCase));

                if (actualKey == null) continue;

                var elementType = new KeyValuePair<string, List<MapElement>>(actualKey, _groundElements[actualKey]);
                Color fillColor = GetGroundElementColor(elementType.Key);

                using (var brush = new SolidBrush(fillColor))
                {
                    foreach (var element in elementType.Value)
                    {
                        if (element.Points.Count >= 3)
                        {
                            try
                            {
                                var screenPoints = new PointF[element.Points.Count];

                                for (int i = 0; i < element.Points.Count; i++)
                                {
                                    float x = (float)(((element.Points[i].Longitude - minLon) / lonRange) * bounds.Width);
                                    float y = (float)(bounds.Height - ((element.Points[i].Latitude - minLat) / latRange) * bounds.Height);
                                    screenPoints[i] = new PointF(x, y);
                                }

                                if (screenPoints.Length >= 3)
                                {
                                    g.FillPolygon(brush, screenPoints);
                                }
                            }
                            catch (Exception)
                            {
                                continue;
                            }
                        }
                    }
                }
            }
        }

        private void DrawGroundLayoutWithSimpleProjection(Graphics g, RectangleF bounds)
        {
            if (_groundElements.Count == 0) return;

            if (_mapData.CenterPoint == null || _mapData.MapBounds <= 0)
            {
                DrawGroundLayoutFallback(g, bounds);
                return;
            }

            var renderOrder = new[] { "GROUND_APR", "GROUND_TWY", "GROUND_BLD", "GROUND_RWY" };

            foreach (var elementTypeName in renderOrder)
            {
                var actualKey = _groundElements.Keys.FirstOrDefault(k =>
                    string.Equals(k, elementTypeName, StringComparison.OrdinalIgnoreCase));

                if (actualKey == null) continue;

                var elementType = new KeyValuePair<string, List<MapElement>>(actualKey, _groundElements[actualKey]);
                SolidBrush brush = GetCachedBrush(elementType.Key);

                foreach (var element in elementType.Value)
                {
                    if (element.Points.Count >= 3)
                    {
                        try
                        {
                            var screenPoints = new PointF[element.Points.Count];
                            bool validPoints = true;
                            float minX = float.MaxValue, maxX = float.MinValue;
                            float minY = float.MaxValue, maxY = float.MinValue;

                            for (int i = 0; i < element.Points.Count; i++)
                            {
                                var geoPoint = new GeoPoint(element.Points[i].Longitude, element.Points[i].Latitude);
                                var screenPoint = _mapData.GeoToScreen(geoPoint, bounds, _zoomLevel, _panOffset);

                                if (float.IsInfinity(screenPoint.X) || float.IsInfinity(screenPoint.Y) ||
                                    float.IsNaN(screenPoint.X) || float.IsNaN(screenPoint.Y))
                                {
                                    validPoints = false;
                                    break;
                                }

                                screenPoints[i] = screenPoint;

                                if (screenPoint.X < minX) minX = screenPoint.X;
                                if (screenPoint.X > maxX) maxX = screenPoint.X;
                                if (screenPoint.Y < minY) minY = screenPoint.Y;
                                if (screenPoint.Y > maxY) maxY = screenPoint.Y;
                            }

                            if (validPoints && (maxX < 0 || minX > this.Width || maxY < 0 || minY > this.Height))
                            {
                                continue;
                            }

                            if (validPoints && screenPoints.Length >= 3)
                            {
                                g.FillPolygon(brush, screenPoints);
                            }
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }
                }
            }
        }

        private SolidBrush GetCachedBrush(string elementType)
        {
            switch (elementType?.ToUpperInvariant())
            {
                case "GROUND_RWY": return _groundRwyBrush;
                case "GROUND_APR": return _groundAprBrush;
                case "GROUND_TWY": return _groundTwyBrush;
                case "GROUND_BLD": return _groundBldBrush;
                default: return _groundTwyBrush;
            }
        }

        private PointF[] BuildLeadOnScreenPoints(LeadOnLight leadOn, RectangleF bounds, bool useAnimationDetailLimit)
        {
            var points = leadOn.Line.Points;
            if (!useAnimationDetailLimit || points.Count <= LEAD_ON_ANIMATION_MAX_RENDER_POINTS)
            {
                var screenPoints = new PointF[points.Count];
                for (int i = 0; i < points.Count; i++)
                {
                    screenPoints[i] = _mapData.GeoToScreen(points[i], bounds, _zoomLevel, _panOffset);
                }
                return screenPoints;
            }

            int step = Math.Max(1, (int)Math.Ceiling((points.Count - 1) / (double)(LEAD_ON_ANIMATION_MAX_RENDER_POINTS - 1)));
            var limitedPoints = new List<PointF>(LEAD_ON_ANIMATION_MAX_RENDER_POINTS);

            for (int i = 0; i < points.Count; i += step)
            {
                limitedPoints.Add(_mapData.GeoToScreen(points[i], bounds, _zoomLevel, _panOffset));
            }

            GeoPoint lastPoint = points[points.Count - 1];
            PointF lastScreenPoint = _mapData.GeoToScreen(lastPoint, bounds, _zoomLevel, _panOffset);
            if (limitedPoints.Count == 0 || Distance(limitedPoints[limitedPoints.Count - 1], lastScreenPoint) > 0.01f)
            {
                limitedPoints.Add(lastScreenPoint);
            }

            return limitedPoints.ToArray();
        }

        private void DrawLeadOnLights(Graphics g, RectangleF bounds)
        {
            foreach (var leadOn in _mapData.LeadOnLights)
            {
                if (leadOn.Line.Points.Count < 2) continue;

                var animState = _leadOnAnimations.ContainsKey(leadOn.Id) ? _leadOnAnimations[leadOn.Id] : null;

                bool isStopbarActive = IsLeadOnStopbarActive(leadOn.Id);
                float baseLineWidth;

                if (animState != null && animState.IsAnimating)
                {
                    if (animState.IsReverse)
                    {
                        baseLineWidth = LeadOnLineWidthOff;
                    }
                    else
                    {
                        baseLineWidth = LeadOnLineWidthOff;
                    }
                }
                else
                {
                    baseLineWidth = isStopbarActive ? LeadOnLineWidthOff : LeadOnLineWidthOn;
                }
                float lineWidth = Math.Max(MIN_LINE_WIDTH, baseLineWidth * _zoomLevel * _scalingRatio);

                Color drawColor = LeadOnGreenColor;
                using (var pen = new Pen(drawColor, lineWidth))
                {
                    pen.StartCap = LineCap.Flat;
                    pen.EndCap = LineCap.Flat;
                    pen.LineJoin = LineJoin.Round;

                    bool isAnimating = animState != null && animState.IsAnimating;
                    var screenPoints = BuildLeadOnScreenPoints(leadOn, bounds, isAnimating);

                    if (isAnimating)
                    {
                        DrawAnimatedLeadOn(g, pen, leadOn, screenPoints, animState);
                    }
                    else
                    {
                        if (screenPoints.Length >= 2)
                        {
                            g.DrawLines(pen, screenPoints);
                        }
                    }
                }
            }
        }

        private void DrawNoMapMessage(Graphics g)
        {
            string message = "No Map Data Available";
            using (var font = new Font("Arial", 12, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
            {
                var textSize = g.MeasureString(message, font);
                var x = (Width - textSize.Width) / 2;
                var y = (Height - textSize.Height) / 2;
                g.DrawString(message, font, brush, x, y);
            }
        }

        private void DrawStopbarCountdownLabel(Graphics g, MapStopbar stopbar, PointF screenPos, int imageSize)
        {
            if (!_stopbarCountdowns.ContainsKey(stopbar.BarsId))
                return;

            var countdown = _stopbarCountdowns[stopbar.BarsId];
            if (!countdown.IsActive)
            {
                _logger.Log($"Countdown for {stopbar.BarsId} is no longer active");
                return;
            }

            var remaining = countdown.RemainingTime;
            string timeText = $"T{remaining.Minutes}:{remaining.Seconds:D2}";

            float baseFontSize = 4f;
            float scaledFontSize = baseFontSize * _scalingRatio * _zoomLevel;

            if (scaledFontSize <= 0f || float.IsNaN(scaledFontSize) || float.IsInfinity(scaledFontSize))
            {
                scaledFontSize = 4f;
            }

            float padding = 0.5f * _scalingRatio * _zoomLevel;
            float horizontalPadding = padding + (1.0f * _scalingRatio * _zoomLevel);

            using (var font = new Font("Arial", scaledFontSize, FontStyle.Bold))
            using (var textBrush = new SolidBrush(Color.White))
            using (var backgroundBrush = new SolidBrush(Color.Black))
            {
                DrawStopbarCountdownLabel(g, stopbar, screenPos, imageSize, timeText, font, textBrush, backgroundBrush, padding, horizontalPadding);
            }
        }

        private void DrawStopbarCountdownLabel(Graphics g, MapStopbar stopbar, PointF screenPos, int imageSize, string timeText, Font font, Brush textBrush, Brush backgroundBrush, float padding, float horizontalPadding)
        {
            var textSize = g.MeasureString(timeText, font);

            float labelWidth = textSize.Width + (horizontalPadding * 2);
            float labelHeight = textSize.Height + (padding * 2);

            float labelX = screenPos.X - (imageSize / 2f) - labelWidth;
            float labelY = screenPos.Y - (imageSize / 2f);
            RectangleF labelRect = new RectangleF(labelX, labelY, labelWidth, labelHeight);

            g.FillRectangle(backgroundBrush, labelRect);
            float textX = labelX + horizontalPadding;
            float textY = labelY + padding;
            g.DrawString(timeText, font, textBrush, textX, textY);
        }

        private void DrawStopbarCountdownLabels(Graphics g, Dictionary<string, StopbarVisual> visuals)
        {
            if (_stopbarCountdowns.Count == 0 || visuals == null || visuals.Count == 0)
                return;

            float baseFontSize = 4f;
            float scaledFontSize = baseFontSize * _scalingRatio * _zoomLevel;
            if (scaledFontSize <= 0f || float.IsNaN(scaledFontSize) || float.IsInfinity(scaledFontSize))
            {
                scaledFontSize = 4f;
            }
            float padding = 0.5f * _scalingRatio * _zoomLevel;
            float horizontalPadding = padding + (1.0f * _scalingRatio * _zoomLevel);

            using (var font = new Font("Arial", scaledFontSize, FontStyle.Bold))
            using (var textBrush = new SolidBrush(Color.White))
            using (var backgroundBrush = new SolidBrush(Color.Black))
            {
                foreach (var visual in visuals.Values)
                {
                    var stopbar = visual.Stopbar;
                    if (!_stopbarCountdowns.ContainsKey(stopbar.BarsId) || !_stopbarCountdowns[stopbar.BarsId].IsActive)
                        continue;

                    PointF screenPos = visual.ScreenPosition;
                    int imageSize = Math.Max(1, (int)Math.Round(visual.ImageSize));

                    float margin = Math.Max(imageSize * 3, 200);
                    if (screenPos.X < -margin || screenPos.X > this.Width + margin ||
                        screenPos.Y < -margin || screenPos.Y > this.Height + margin)
                    {
                        continue;
                    }

                    var countdown = _stopbarCountdowns[stopbar.BarsId];
                    var remaining = countdown.RemainingTime;
                    string timeText = $"T{remaining.Minutes}:{remaining.Seconds:D2}";

                    DrawStopbarCountdownLabel(g, stopbar, screenPos, imageSize, timeText, font, textBrush, backgroundBrush, padding, horizontalPadding);
                }
            }
        }

        private Dictionary<string, StopbarVisual> BuildStopbarVisuals(RectangleF bounds)
        {
            var layout = new Dictionary<string, StopbarVisual>(StringComparer.OrdinalIgnoreCase);
            if (_mapData?.Stopbars == null || _mapData.Stopbars.Count == 0)
            {
                return layout;
            }

            EnsureStopbarSlides(bounds);

            foreach (var stopbar in _mapData.Stopbars)
            {
                PointF basePosition = _mapData.GeoToScreen(stopbar.Position, bounds, _zoomLevel, _panOffset);
                float rotation = GetStopbarRotation(stopbar);
                PointF rightUnit = CalculateRightUnitVector(rotation);

                float imageSize = STOPBAR_BASE_SIZE * _scalingRatio * _zoomLevel;
                if (imageSize <= 0f || float.IsNaN(imageSize) || float.IsInfinity(imageSize))
                {
                    imageSize = 1f;
                }

                float slideWorld = 0f;
                if (!_stopbarSlideOffsetsWorld.TryGetValue(stopbar.BarsId, out slideWorld))
                {
                    slideWorld = 0f;
                }

                double scale = GetCurrentScale(bounds);
                float slidePixels = (float)(slideWorld * scale);
                PointF screenPosition = basePosition;
                if (Math.Abs(slidePixels) > 0.01f)
                {
                    screenPosition = new PointF(
                        basePosition.X + rightUnit.X * slidePixels,
                        basePosition.Y + rightUnit.Y * slidePixels);
                }

                layout[stopbar.BarsId] = new StopbarVisual
                {
                    Stopbar = stopbar,
                    BasePosition = basePosition,
                    ScreenPosition = screenPosition,
                    Rotation = rotation,
                    ImageSize = imageSize,
                    RightUnit = rightUnit,
                    SlideOffset = slidePixels
                };
            }

            return layout;
        }

        private static bool BoundsRoughlyEqual(RectangleF a, RectangleF b)
        {
            const float tolerance = 0.5f;
            return Math.Abs(a.X - b.X) < tolerance &&
                   Math.Abs(a.Y - b.Y) < tolerance &&
                   Math.Abs(a.Width - b.Width) < tolerance &&
                   Math.Abs(a.Height - b.Height) < tolerance;
        }

        private Dictionary<string, StopbarVisual> GetStopbarVisuals(RectangleF bounds)
        {
            if (!_visualCacheDirty && _hasCachedVisualBounds && BoundsRoughlyEqual(bounds, _cachedVisualBounds))
            {
                return _cachedStopbarVisuals;
            }

            _cachedStopbarVisuals = BuildStopbarVisuals(bounds);
            _cachedVisualBounds = bounds;
            _hasCachedVisualBounds = true;
            _visualCacheDirty = false;
            return _cachedStopbarVisuals;
        }

        private double GetCurrentScale(RectangleF bounds)
        {
            if (_mapData == null)
                return 0.0;

            double mapBounds = _mapData.MapBounds;
            if (mapBounds <= 0.0)
                return 0.0;

            double effectiveWidth = Math.Max(1.0, bounds.Width);
            double scale = (effectiveWidth / mapBounds) * _zoomLevel;
            return scale;
        }

        private void EnsureStopbarSlides(RectangleF bounds)
        {
            if (_stopbarSlidesInitialized)
                return;

            InitializeStopbarSlides(bounds);
            _stopbarSlidesInitialized = true;
        }

        private void InitializeStopbarSlides(RectangleF bounds)
        {
            _stopbarSlideOffsetsWorld.Clear();

            if (_mapData?.Stopbars == null || _mapData.Stopbars.Count <= 1)
                return;

            double scale = GetCurrentScale(bounds);
            if (scale <= 0.0)
            {
                scale = 1.0;
            }

            float worldMaxSlide = (float)(STOPBAR_MAX_SLIDE / scale);

            var tempStopbars = new List<TempStopbar>();
            float imageSize = STOPBAR_BASE_SIZE * _scalingRatio * _zoomLevel;
            if (imageSize <= 0f || float.IsNaN(imageSize) || float.IsInfinity(imageSize))
            {
                imageSize = STOPBAR_BASE_SIZE;
            }

            foreach (var stopbar in _mapData.Stopbars)
            {
                PointF basePosition = _mapData.GeoToScreen(stopbar.Position, bounds, _zoomLevel, _panOffset);
                float rotation = GetStopbarRotation(stopbar);
                PointF rightUnit = CalculateRightUnitVector(rotation);

                tempStopbars.Add(new TempStopbar
                {
                    Stopbar = stopbar,
                    BasePosition = basePosition,
                    RightUnit = rightUnit
                });
            }

            for (int i = 0; i < tempStopbars.Count; i++)
            {
                for (int j = i + 1; j < tempStopbars.Count; j++)
                {
                    var a = tempStopbars[i];
                    var b = tempStopbars[j];
                    float distance = Distance(a.BasePosition, b.BasePosition);
                    if (distance > imageSize || distance <= 0.01f)
                    {
                        continue;
                    }

                    PointF diff = new PointF(b.BasePosition.X - a.BasePosition.X, b.BasePosition.Y - a.BasePosition.Y);
                    float projection = diff.X * a.RightUnit.X + diff.Y * a.RightUnit.Y;
                    float axisDistance = Math.Abs(projection);
                    float crossDistance = Math.Abs(diff.X * (-a.RightUnit.Y) + diff.Y * a.RightUnit.X);

                    if (axisDistance <= 1.0f || crossDistance >= (imageSize * 0.6f))
                    {
                        continue;
                    }

                    float direction = projection >= 0 ? -1f : 1f;

                    if (!_stopbarSlideOffsetsWorld.ContainsKey(a.Stopbar.BarsId))
                    {
                        _stopbarSlideOffsetsWorld[a.Stopbar.BarsId] = direction * worldMaxSlide;
                    }

                    if (!_stopbarSlideOffsetsWorld.ContainsKey(b.Stopbar.BarsId))
                    {
                        _stopbarSlideOffsetsWorld[b.Stopbar.BarsId] = -direction * worldMaxSlide;
                    }
                }
            }
        }

        private void ResetStopbarSlides()
        {
            _stopbarSlideOffsetsWorld.Clear();
            _stopbarSlidesInitialized = false;
        }

        private static PointF CalculateRightUnitVector(float rotationDegrees)
        {
            float radians = rotationDegrees * (float)(Math.PI / 180.0);
            float cos = (float)Math.Cos(radians);
            float sin = (float)Math.Sin(radians);
            return NormalizeVector(new PointF(cos, sin));
        }

        private static PointF NormalizeVector(PointF vector)
        {
            float length = (float)Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
            if (length <= 1e-3f)
            {
                return new PointF(1f, 0f);
            }

            return new PointF(vector.X / length, vector.Y / length);
        }

        private static float Distance(PointF a, PointF b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        private void DrawStopbarFallback(Graphics g, MapStopbar stopbar, PointF screenPos, int imageSize)
        {
            Color borderColor = stopbar.State ? Color.FromArgb(255, 0, 208) : Color.Black;
            using (var borderPen = new Pen(borderColor, 1.5f))
            {
                RectangleF borderRect = new RectangleF(
                    screenPos.X - (imageSize / 2f),
                    screenPos.Y - (imageSize / 2f),
                    imageSize,
                    imageSize
                );
                g.DrawRectangle(borderPen, Rectangle.Round(borderRect));
            }

            Color stopbarColor = stopbar.State ? StopbarOnColor : StopbarOffColor;

            using (Matrix rotationMatrix = new Matrix())
            {
                // Use a unified rotation calculation so stopbar visuals match map rotation consistently
                float adjustedHeading = GetStopbarRotation(stopbar);
                rotationMatrix.RotateAt(adjustedHeading, screenPos);

                g.Transform = rotationMatrix;

                RectangleF stopbarRect = new RectangleF(
                    screenPos.X - (imageSize / 2f),
                    screenPos.Y - (imageSize / 2f),
                    imageSize,
                    imageSize
                );

                using (SolidBrush stopbarBrush = new SolidBrush(stopbarColor))
                {
                    g.FillRectangle(stopbarBrush, stopbarRect);
                }
                g.ResetTransform();
            }

            DrawStopbarCountdownLabel(g, stopbar, screenPos, imageSize);
        }

        private void DrawStopbars(Graphics g, Dictionary<string, StopbarVisual> visuals)
        {
            if (_mapData?.Stopbars == null || visuals == null || visuals.Count == 0)
                return;

            var qualityState = g.Save();

            // Use lower quality during panning/zooming for performance
            if (_isPanningOrZooming)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.CompositingQuality = CompositingQuality.HighSpeed;
            }
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
            }
            g.CompositingMode = CompositingMode.SourceOver;

            foreach (var visual in visuals.Values)
            {
                var stopbar = visual.Stopbar;
                PointF screenPos = visual.ScreenPosition;
                int imageSize = Math.Max(1, (int)Math.Round(visual.ImageSize));

                float extraLeftSpace = 0f;
                if (_stopbarCountdowns.ContainsKey(stopbar.BarsId) && _stopbarCountdowns[stopbar.BarsId].IsActive)
                {
                    float baseFontSize = 4f;
                    float scaledFontSize = baseFontSize * _scalingRatio * _zoomLevel;
                    if (scaledFontSize <= 0f) scaledFontSize = 4f;

                    float estimatedLabelWidth = (8 * scaledFontSize) + (4 * _scalingRatio * _zoomLevel);
                    extraLeftSpace = estimatedLabelWidth;
                }

                float margin = Math.Max(imageSize * 3, 200) + extraLeftSpace;
                if (screenPos.X < -margin || screenPos.X > this.Width + margin ||
                    screenPos.Y < -margin || screenPos.Y > this.Height + margin)
                {
                    continue;
                }

                Color borderColor = stopbar.State ? Color.FromArgb(255, 0, 208) : Color.Black;
                using (var borderPen = new Pen(borderColor, 1.5f))
                {
                    RectangleF borderRect = new RectangleF(
                        screenPos.X - (imageSize / 2f),
                        screenPos.Y - (imageSize / 2f),
                        imageSize,
                        imageSize
                    );
                    g.DrawRectangle(borderPen, Rectangle.Round(borderRect));
                }

                System.Drawing.Bitmap stopbarImage = null;
                try
                {
                    stopbarImage = stopbar.State ? Properties.Resources.bar_on : Properties.Resources.bar_off;
                }
                catch (Exception)
                {
                    DrawStopbarFallback(g, stopbar, screenPos, imageSize);
                    continue;
                }

                if (stopbarImage == null)
                {
                    DrawStopbarFallback(g, stopbar, screenPos, imageSize);
                    continue;
                }

                var state = g.Save();
                float adjustedHeading = visual.Rotation;
                g.TranslateTransform(screenPos.X, screenPos.Y);
                g.RotateTransform(adjustedHeading);
                g.TranslateTransform(-imageSize / 2f, -imageSize / 2f);

                g.DrawImage(stopbarImage, 0, 0, imageSize, imageSize);
                g.Restore(state);
            }

            g.Restore(qualityState);
        }

        private void DrawTaxiways(Graphics g, RectangleF bounds)
        {
            float scaledLineWidth = Math.Max(MIN_LINE_WIDTH, TaxiwayLineWidth * _zoomLevel * _scalingRatio);

            // Update cached pen width
            _taxiwayPen.Width = scaledLineWidth;

            foreach (var line in _mapData.Taxiways.Lines)
            {
                if (line.Points.Count < 2) continue;

                var screenPoints = new PointF[line.Points.Count];
                bool anyVisible = false;
                float margin = 50f; // Include lines that are just outside viewport

                for (int i = 0; i < line.Points.Count; i++)
                {
                    screenPoints[i] = _mapData.GeoToScreen(line.Points[i], bounds, _zoomLevel, _panOffset);

                    // Check if any point is within or near the visible area
                    if (!anyVisible &&
                        screenPoints[i].X >= -margin && screenPoints[i].X <= this.Width + margin &&
                        screenPoints[i].Y >= -margin && screenPoints[i].Y <= this.Height + margin)
                    {
                        anyVisible = true;
                    }
                }

                // Skip lines that are completely offscreen
                if (!anyVisible) continue;

                if (screenPoints.Length >= 2)
                {
                    g.DrawLines(_taxiwayPen, screenPoints);
                }
            }
        }

        private void DrawWindsocks(Graphics g, RectangleF bounds)
        {
            if (_mapData?.Windsocks == null) return;
            System.Drawing.Bitmap windsockImage = null;
            try
            {
                windsockImage = Properties.Resources.windsock;
            }
            catch (Exception)
            {
                DrawWindsocksFallback(g, bounds);
                return;
            }

            if (windsockImage == null)
            {
                DrawWindsocksFallback(g, bounds);
                return;
            }

            EnsureWindsockSmartPositions(g, bounds);
            foreach (var windsock in _mapData.Windsocks)
            {
                GeoPoint drawPosition = _windsockSmartPositions.TryGetValue(windsock, out GeoPoint smartPosition)
                    ? smartPosition
                    : windsock.Position;
                var screenPoint = _mapData.GeoToScreen(drawPosition, bounds, _zoomLevel, _panOffset);

                if (_isPanningOrZooming)
                {
                    float margin = 100f;
                    if (screenPoint.X < -margin || screenPoint.X > this.Width + margin ||
                        screenPoint.Y < -margin || screenPoint.Y > this.Height + margin)
                    {
                        continue;
                    }
                }

                var windState = _windsockStates.ContainsKey(windsock) ?
                    _windsockStates[windsock] :
                    new WindState(_baseWindDirection, _baseWindSpeed);
                // Compute on-screen rotation:
                // - METAR direction is the direction the wind is FROM (true degrees)
                // - Map geometry is rotated by -Rotation in projection, so add map Rotation to keep headings consistent on screen
                // - Windsock tail should point DOWNWIND, hence +180 from the wind-from direction
                float totalRotation = GetWindsockRotation(windState.CurrentDirection);
                int baseImageSize = 12;
                int imageSize = (int)(baseImageSize * _scalingRatio * _zoomLevel);

                if (imageSize <= 0)
                {
                    imageSize = 1;
                }

                string windText = $"{windState.CurrentDirection:000} / {windState.CurrentSpeed:00}";
                float baseFontSize = 5f;
                float scaledFontSize = baseFontSize * _scalingRatio * _zoomLevel;

                if (scaledFontSize <= 0f || float.IsNaN(scaledFontSize) || float.IsInfinity(scaledFontSize))
                {
                    scaledFontSize = 1f;
                }

                using (var font = new Font("Arial", scaledFontSize, FontStyle.Bold))
                using (var textBrush = new SolidBrush(Color.White))
                {
                    SizeF textSize = g.MeasureString(windText, font);
                    float labelOffsetY = (imageSize / 2f) + (16 * _scalingRatio * _zoomLevel);

                    var state = g.Save();

                    // Use lower quality during panning/zooming
                    if (_isPanningOrZooming)
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.SmoothingMode = SmoothingMode.HighSpeed;
                        g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    }
                    else
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    }

                    // Rotate around the icon center for predictable orientation
                    g.TranslateTransform(screenPoint.X, screenPoint.Y);
                    g.RotateTransform(totalRotation);
                    g.TranslateTransform(-imageSize / 2f, -imageSize / 2f);

                    g.DrawImage(windsockImage, 0, 0, imageSize, imageSize);

                    g.Restore(state);
                    float textX = screenPoint.X - textSize.Width / 2;
                    float textY = screenPoint.Y + labelOffsetY;
                    ComputeWindComponentsForWindsock(windsock, out float crosswind, out float tailwind);
                    bool highCrosswind = crosswind > 20.0f;
                    bool tailwindAlert = tailwind >= 5.0f;
                    if (highCrosswind || tailwindAlert)
                    {
                        float paddingX = 1.0f * _scalingRatio * _zoomLevel;
                        float paddingY = 1.0f * _scalingRatio * _zoomLevel;
                        RectangleF bg = new RectangleF(
                            textX - paddingX,
                            textY - paddingY,
                            textSize.Width + (paddingX * 2),
                            textSize.Height + (paddingY * 2));
                        using (var bgBrush = new SolidBrush(Color.FromArgb(255, 117, 18)))
                        {
                            g.FillRectangle(bgBrush, bg);
                        }
                    }

                    g.DrawString(windText, font, textBrush, textX, textY);
                }
            }
        }

        private WindsockObstacles BuildWindsockObstacles(RectangleF bounds)
        {
            var obstacles = new WindsockObstacles();

            foreach (var visual in GetStopbarVisuals(bounds).Values)
            {
                obstacles.Rectangles.Add(InflateRect(RectFromCenter(visual.ScreenPosition, visual.ImageSize, visual.ImageSize), WINDSOCK_CLEARANCE_PX));
            }

            float taxiwayWidth = Math.Max(MIN_LINE_WIDTH, TaxiwayLineWidth * _zoomLevel * _scalingRatio) + WINDSOCK_CLEARANCE_PX;
            AddLineObstacles(obstacles, _mapData.Taxiways?.Lines, bounds, taxiwayWidth);

            float leadOnWidth = Math.Max(MIN_LINE_WIDTH, LeadOnLineWidthOn * _zoomLevel * _scalingRatio) + WINDSOCK_CLEARANCE_PX;
            var leadOnLines = _mapData.LeadOnLights?.Select(l => l.Line);
            AddLineObstacles(obstacles, leadOnLines, bounds, leadOnWidth);

            foreach (var runwayLayer in _groundElements.Where(kvp => string.Equals(kvp.Key, "GROUND_RWY", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var element in runwayLayer.Value)
                {
                    if (element.Points.Count < 3) continue;
                    obstacles.Polygons.Add(element.Points
                        .Select(p => _mapData.GeoToScreen(new GeoPoint(p.Longitude, p.Latitude), bounds, _zoomLevel, _panOffset))
                        .ToArray());
                }
            }

            return obstacles;
        }

        private void EnsureWindsockSmartPositions(Graphics g, RectangleF bounds)
        {
            if (_windsockSmartPositionsInitialized || _mapData?.Windsocks == null)
                return;

            _windsockSmartPositions.Clear();
            var obstacles = BuildWindsockObstacles(bounds);
            var placedWindsocks = new List<RectangleF>();

            foreach (var windsock in _mapData.Windsocks)
            {
                PointF screenPoint = _mapData.GeoToScreen(windsock.Position, bounds, _zoomLevel, _panOffset);
                var windState = _windsockStates.ContainsKey(windsock)
                    ? _windsockStates[windsock]
                    : new WindState(_baseWindDirection, _baseWindSpeed);

                int imageSize = Math.Max(1, (int)(12 * _scalingRatio * _zoomLevel));
                float fontSize = 5f * _scalingRatio * _zoomLevel;
                if (fontSize <= 0f || float.IsNaN(fontSize) || float.IsInfinity(fontSize))
                {
                    fontSize = 1f;
                }

                using (var font = new Font("Arial", fontSize, FontStyle.Bold))
                {
                    string windText = $"{windState.CurrentDirection:000} / {windState.CurrentSpeed:00}";
                    SizeF textSize = g.MeasureString(windText, font);
                    float labelOffsetY = (imageSize / 2f) + (16 * _scalingRatio * _zoomLevel);
                    RectangleF originalBounds = GetWindsockElementBounds(screenPoint, imageSize, textSize, labelOffsetY);
                    PointF smartPoint = FindSmartWindsockPosition(screenPoint, originalBounds, obstacles, placedWindsocks);
                    RectangleF smartBounds = GetWindsockElementBounds(smartPoint, imageSize, textSize, labelOffsetY);

                    _windsockSmartPositions[windsock] = _mapData.ScreenToGeo(smartPoint, bounds, _zoomLevel, _panOffset) ?? windsock.Position;
                    placedWindsocks.Add(smartBounds);
                }
            }

            _windsockSmartPositionsInitialized = true;
        }

        private void AddLineObstacles(WindsockObstacles obstacles, IEnumerable<GeoLine> lines, RectangleF bounds, float width)
        {
            if (lines == null) return;

            foreach (var line in lines)
            {
                if (line.Points.Count < 2) continue;
                PointF previous = _mapData.GeoToScreen(line.Points[0], bounds, _zoomLevel, _panOffset);
                for (int i = 1; i < line.Points.Count; i++)
                {
                    PointF current = _mapData.GeoToScreen(line.Points[i], bounds, _zoomLevel, _panOffset);
                    obstacles.Lines.Add(new LineObstacle(previous, current, width));
                    previous = current;
                }
            }
        }

        private PointF FindSmartWindsockPosition(PointF originalCenter, RectangleF originalBounds, WindsockObstacles obstacles, List<RectangleF> placedWindsocks)
        {
            if (!WindsockBoundsConflict(originalBounds, obstacles, placedWindsocks))
                return originalCenter;

            float step = Math.Max(8f, Math.Max(originalBounds.Width, originalBounds.Height) * 0.35f);
            PointF[] directions =
            {
                new PointF(1, 0), new PointF(-1, 0), new PointF(0, -1), new PointF(0, 1),
                new PointF(1, -1), new PointF(-1, -1), new PointF(1, 1), new PointF(-1, 1)
            };

            for (int ring = 1; ring <= 8; ring++)
            {
                float distance = step * ring;
                foreach (var direction in directions)
                {
                    PointF unit = NormalizeVector(direction);
                    PointF candidate = new PointF(originalCenter.X + unit.X * distance, originalCenter.Y + unit.Y * distance);
                    RectangleF candidateBounds = OffsetRect(originalBounds, candidate.X - originalCenter.X, candidate.Y - originalCenter.Y);

                    if (IsInsideClient(candidateBounds) && !WindsockBoundsConflict(candidateBounds, obstacles, placedWindsocks))
                        return candidate;
                }
            }

            return originalCenter;
        }

        private RectangleF GetWindsockElementBounds(PointF center, int imageSize, SizeF textSize, float labelOffsetY)
        {
            RectangleF icon = RectFromCenter(center, imageSize, imageSize);
            RectangleF label = new RectangleF(center.X - textSize.Width / 2f, center.Y + labelOffsetY, textSize.Width, textSize.Height);
            return InflateRect(RectangleF.Union(icon, label), WINDSOCK_CLEARANCE_PX);
        }

        private bool WindsockBoundsConflict(RectangleF bounds, WindsockObstacles obstacles, List<RectangleF> placedWindsocks)
        {
            if (placedWindsocks.Any(r => r.IntersectsWith(bounds))) return true;
            if (obstacles.Rectangles.Any(r => r.IntersectsWith(bounds))) return true;
            if (obstacles.Lines.Any(l => RectIntersectsLine(bounds, l.Start, l.End, l.Width))) return true;
            return obstacles.Polygons.Any(p => RectIntersectsPolygon(bounds, p));
        }

        private bool IsInsideClient(RectangleF bounds)
        {
            return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= Width && bounds.Bottom <= Height;
        }

        private static RectangleF RectFromCenter(PointF center, float width, float height)
        {
            return new RectangleF(center.X - width / 2f, center.Y - height / 2f, width, height);
        }

        private static RectangleF InflateRect(RectangleF rect, float amount)
        {
            rect.Inflate(amount, amount);
            return rect;
        }

        private static RectangleF OffsetRect(RectangleF rect, float dx, float dy)
        {
            rect.Offset(dx, dy);
            return rect;
        }

        private static bool RectIntersectsLine(RectangleF rect, PointF start, PointF end, float width)
        {
            RectangleF expanded = InflateRect(rect, width / 2f);
            if (expanded.Contains(start) || expanded.Contains(end)) return true;

            PointF topLeft = new PointF(expanded.Left, expanded.Top);
            PointF topRight = new PointF(expanded.Right, expanded.Top);
            PointF bottomRight = new PointF(expanded.Right, expanded.Bottom);
            PointF bottomLeft = new PointF(expanded.Left, expanded.Bottom);
            return SegmentsIntersect(start, end, topLeft, topRight) ||
                   SegmentsIntersect(start, end, topRight, bottomRight) ||
                   SegmentsIntersect(start, end, bottomRight, bottomLeft) ||
                   SegmentsIntersect(start, end, bottomLeft, topLeft);
        }

        private static bool RectIntersectsPolygon(RectangleF rect, PointF[] polygon)
        {
            if (polygon == null || polygon.Length < 3) return false;

            PointF[] corners =
            {
                new PointF(rect.Left, rect.Top), new PointF(rect.Right, rect.Top),
                new PointF(rect.Right, rect.Bottom), new PointF(rect.Left, rect.Bottom)
            };

            if (corners.Any(c => PointInPolygon(c, polygon))) return true;
            if (polygon.Any(p => rect.Contains(p))) return true;

            for (int i = 0; i < polygon.Length; i++)
            {
                PointF a = polygon[i];
                PointF b = polygon[(i + 1) % polygon.Length];
                for (int c = 0; c < corners.Length; c++)
                {
                    if (SegmentsIntersect(a, b, corners[c], corners[(c + 1) % corners.Length])) return true;
                }
            }

            return false;
        }

        private static bool PointInPolygon(PointF point, PointF[] polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                bool crosses = ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)) &&
                    point.X < (polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X;
                if (crosses) inside = !inside;
            }
            return inside;
        }

        private static bool SegmentsIntersect(PointF a, PointF b, PointF c, PointF d)
        {
            float d1 = Cross(a, b, c);
            float d2 = Cross(a, b, d);
            float d3 = Cross(c, d, a);
            float d4 = Cross(c, d, b);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static float Cross(PointF a, PointF b, PointF c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        private void DrawWindsocksFallback(Graphics g, RectangleF bounds)
        {
            using (var brush = new SolidBrush(Color.Orange))
            using (var pen = new Pen(Color.DarkOrange, 1.0f))
            {
                foreach (var windsock in _mapData.Windsocks)
                {
                    var screenPoint = _mapData.GeoToScreen(windsock.Position, bounds, _zoomLevel, _panOffset);

                    float triangleBase = 8.0f;
                    float triangleHeight = 12.0f;

                    PointF[] trianglePoints = new PointF[]
                    {
                        new PointF(screenPoint.X, screenPoint.Y - triangleHeight / 2),
                        new PointF(screenPoint.X - triangleBase / 2, screenPoint.Y + triangleHeight / 2),
                        new PointF(screenPoint.X + triangleBase / 2, screenPoint.Y + triangleHeight / 2)
                    };

                    g.FillPolygon(brush, trianglePoints);

                    g.DrawPolygon(pen, trianglePoints);
                }
            }
        }

        private async Task FetchRunwaysAsync(string icao)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(icao)) return;
                // v2.stopbars.com provides runway endpoints for computing headings and proximity
                var url = $"https://v2.stopbars.com/airports?icao={Uri.EscapeDataString(icao)}";
                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                var api = JsonConvert.DeserializeObject<AirportApiResponse>(json);
                if (api?.runways == null || api.runways.Count == 0)
                {
                    _runways = null;
                    return;
                }

                var list = new List<RunwayInfo>();
                foreach (var rwy in api.runways)
                {
                    if (!TryParseDouble(rwy.le_latitude_deg, out double leLat) ||
                        !TryParseDouble(rwy.le_longitude_deg, out double leLon) ||
                        !TryParseDouble(rwy.he_latitude_deg, out double heLat) ||
                        !TryParseDouble(rwy.he_longitude_deg, out double heLon))
                    {
                        continue;
                    }
                    var le = new GeoPoint(leLon, leLat);
                    var he = new GeoPoint(heLon, heLat);
                    double heading = BearingDegrees(leLat, leLon, heLat, heLon);
                    list.Add(new RunwayInfo
                    {
                        LeIdent = rwy.le_ident,
                        HeIdent = rwy.he_ident,
                        Le = le,
                        He = he,
                        Heading = heading
                    });
                }

                _runways = list.Count > 0 ? list : null;
                if (_runways != null && _runways.Count > 0)
                {
                    RebuildWindsockRunwayCache();

                    if (IsHandleCreated)
                    {
                        BeginInvoke((Action)(Invalidate));
                    }
                    else
                    {
                        Invalidate();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Failed to fetch runways for {icao}: {ex.Message}");
                _runways = null;
            }
        }

        private Color GetGroundElementColor(string elementType)
        {
            switch (elementType?.ToUpperInvariant())
            {
                case "GROUND_RWY":
                    return Color.Black;

                case "GROUND_APR":
                    return Color.FromArgb(83, 83, 83);

                case "GROUND_TWY":
                    return Color.FromArgb(63, 63, 63);

                case "GROUND_BLD":
                    return Color.FromArgb(100, 43, 43);

                default:
                    return Color.FromArgb(50, 50, 50);
            }
        }

        // Computes the correct on-screen rotation for a stopbar sprite, compensating for map rotation
        // and aligning the sprite perpendicular to the taxiway/stopbar heading.
        private float GetStopbarRotation(MapStopbar stopbar)
        {
            if (_mapData == null)
                return 0f;

            // World geometry is rotated by -Rotation in projection, so subtract map rotation
            float angle = (float)(stopbar.Heading + _mapData.Rotation - 90f);

            // Normalize to [0, 360)
            angle %= 360f;
            if (angle < 0f) angle += 360f;

            // Stopbar sprite is drawn perpendicular to taxiway heading
            angle -= 90f;

            // Normalize again after adjustment
            angle %= 360f;
            if (angle < 0f) angle += 360f;

            // Apply visual snapping to tidy up nearly-horizontal/vertical/diagonal bars on screen
            angle = MaybeSnapAngle(angle);
            return angle;
        }

        // Computes the correct on-screen rotation for a windsock sprite.
        // Contract:
        // - Input: windFromDeg (0..359), the direction wind is blowing FROM (meteorological convention, true degrees)
        // - Output: clockwise degrees to rotate the sprite on screen so that the windsock tail points DOWNWIND
        // Notes:
        // - Map projection rotates world geometry by -Rotation (clockwise on screen),
        //   so we ADD map Rotation here to maintain absolute heading on the rotated map.
        // - We add +180 so the tail (not the mouth) points in the wind-to direction; this matches typical windsock sprites drawn pointing to +X at 0°.
        private float GetWindsockRotation(int windFromDeg)
        {
            if (_mapData == null) return 0f;
            float angle = NormalizeDegrees(windFromDeg);
            angle += (float)_mapData.Rotation; // compensate for map rotation
            angle += 180f; // tail downwind
            angle %= 360f;
            if (angle < 0f) angle += 360f;
            return angle;
        }

        private bool IsLeadOnStopbarActive(string leadOnId)
        {
            if (_mapData?.Stopbars == null) return false;
            var controlling = _mapData.Stopbars
                .Where(s => s.LeadOnIds != null && s.LeadOnIds.Contains(leadOnId))
                .ToList();

            if (controlling.Count == 0)
            {
                return false;
            }

            return controlling.All(s => s.State);
        }

        // Returns the input angle snapped to nearest target if within tolerance; otherwise returns original angle.
        private float MaybeSnapAngle(float angleDeg)
        {
            if (!AngleSnapEnabled) return angleDeg;
            float a = NormalizeAngleF(angleDeg);
            float tol = Math.Max(0f, AngleSnapToleranceDegrees);
            float best = a;
            float bestDiff = tol + 0.001f; // require strictly within tolerance
            for (int i = 0; i < ANGLE_SNAP_TARGETS.Length; i++)
            {
                float t = ANGLE_SNAP_TARGETS[i];
                float d = Math.Abs(DeltaAngleF(a, t));
                if (d < bestDiff)
                {
                    best = t;
                    bestDiff = d;
                }
            }
            return best;
        }

        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            float oldZoom = _zoomLevel;

            var bounds = CalculateSquareDrawingBounds();

            float mouseX = e.X - bounds.X;
            float mouseY = e.Y - bounds.Y;

            float preZoomX = (mouseX - bounds.Width / 2 - _panOffset.X) / oldZoom;
            float preZoomY = (mouseY - bounds.Height / 2 - _panOffset.Y) / oldZoom;
            const float ZOOM_FACTOR = 1.15f;

            if (e.Delta > 0)
            {
                _zoomLevel = Math.Min(_zoomLevel * ZOOM_FACTOR, MAX_ZOOM);
            }
            else
            {
                _zoomLevel = Math.Max(_zoomLevel / ZOOM_FACTOR, MIN_ZOOM);
            }

            if (Math.Abs(_zoomLevel - oldZoom) > 0.001f)
            {
                float postZoomX = preZoomX * _zoomLevel;
                float postZoomY = preZoomY * _zoomLevel;

                _panOffset.X = mouseX - bounds.Width / 2 - postZoomX;
                _panOffset.Y = mouseY - bounds.Height / 2 - postZoomY;

                // Set interaction flag during rapid zooming
                _isPanningOrZooming = true;
                _lastZoomTime = DateTime.Now;
                InvalidateStopbarVisualCache();
                ThrottledInvalidate();
            }
        }

        private void WindSimulationTimer_Tick(object sender, EventArgs e)
        {
            int baseDir = _baseWindDirection;
            int baseSpd = Math.Max(0, _baseWindSpeed);
            int baseGust = Math.Max(baseSpd, _baseWindGust);
            bool vrb = _isVariableWind;

            foreach (var kvp in _windsockStates)
            {
                var windsock = kvp.Key;
                var s = kvp.Value;
                if (s.TurbulenceFactor <= 0f)
                {
                    s.TurbulenceFactor = 0.7f + (float)_windRandom.NextDouble() * 0.8f; // 0.7 .. 1.5
                }
                float speedScale = baseSpd <= 1 ? 0.15f : Math.Min(1f, baseSpd / 20f); // 0..1
                float turb = s.TurbulenceFactor;
                int dirTarget = baseDir;
                int dirWander = vrb ? _windRandom.Next(-60, 61) : _windRandom.Next(-12, 13);
                dirWander = (int)(dirWander * turb);
                dirTarget = NormalizeDegrees(dirTarget + dirWander);

                float dirStep = (vrb ? 10f : 4f) * speedScale * turb; // deg per tick toward target
                if (baseSpd == 0) dirStep = 0; // calm, no movement

                int newDir = (int)Math.Round(MoveTowardsAngle(s.CurrentDirection, dirTarget, dirStep));
                newDir = NormalizeDegrees(newDir);

                DateTime now = DateTime.Now;
                if (baseGust > baseSpd)
                {
                    if (!s.GustActive || now >= s.GustUntil)
                    {
                        float gustGap = baseGust - baseSpd;
                        float p = Math.Min(0.25f, 0.05f + (gustGap / 30f) * 0.15f); // 5%..25%
                        if (_windRandom.NextDouble() < p)
                        {
                            s.GustActive = true;
                            int durMs = _windRandom.Next(2000, 8000); // 2..8s bursts
                            s.GustUntil = now.AddMilliseconds(durMs);
                            s.GustSpeed = baseGust + _windRandom.Next(-1, 2);
                        }
                    }
                }
                else
                {
                    s.GustActive = false;
                }

                int speedTarget = baseSpd;
                if (s.GustActive && now < s.GustUntil)
                {
                    speedTarget = Math.Max(speedTarget, s.GustSpeed);
                }
                float baseStep = 0.6f + 1.2f * speedScale; // 0.6..1.8
                if (s.GustActive) baseStep *= 1.8f; // accelerate during gust
                baseStep *= turb;
                int jitter = _windRandom.Next(-1, 2);
                int newSpd = (int)Math.Round(MoveTowards(s.CurrentSpeed, speedTarget, baseStep)) + jitter;
                newSpd = Math.Max(0, newSpd);

                s.CurrentDirection = newDir;
                s.CurrentSpeed = newSpd;
                s.LastUpdate = now;
            }

            int minIvl = (vrb || _baseWindGust > _baseWindSpeed) ? 400 : 900;
            int maxIvl = (vrb || _baseWindGust > _baseWindSpeed) ? 1100 : 2200;
            _windSimulationTimer.Interval = _windRandom.Next(minIvl, maxIvl);

            if (_windsockStates.Count > 0)
            {
                Invalidate();
            }
        }

        public class StopbarClickEventArgs : EventArgs
        {
            public StopbarClickEventArgs(string barsId, MouseButtons button)
            {
                BarsId = barsId;
                Button = button;
            }

            public string BarsId { get; private set; }
            public MouseButtons Button { get; private set; }
        }

        private class AirportApiResponse
        {
            public string icao { get; set; }
            public double latitude { get; set; }
            public double longitude { get; set; }
            public string name { get; set; }
            public List<RunwayApi> runways { get; set; }
        }

        private class RunwayApi
        {
            public string airport_icao { get; set; }
            public string he_ident { get; set; }
            public string he_latitude_deg { get; set; }
            public string he_longitude_deg { get; set; }
            public int id { get; set; }
            public string le_ident { get; set; }
            public string le_latitude_deg { get; set; }
            public string le_longitude_deg { get; set; }
            public string length_ft { get; set; }
            public string width_ft { get; set; }
        }

        private class TempStopbar
        {
            public MapStopbar Stopbar { get; set; }
            public PointF BasePosition { get; set; }
            public PointF RightUnit { get; set; }
        }

        private class StopbarVisual
        {
            public MapStopbar Stopbar { get; set; }
            public PointF BasePosition { get; set; }
            public PointF ScreenPosition { get; set; }
            public float Rotation { get; set; }
            public float ImageSize { get; set; }
            public PointF RightUnit { get; set; }
            public float SlideOffset { get; set; }
        }

        private class WindsockObstacles
        {
            public List<RectangleF> Rectangles { get; } = new List<RectangleF>();
            public List<LineObstacle> Lines { get; } = new List<LineObstacle>();
            public List<PointF[]> Polygons { get; } = new List<PointF[]>();
        }

        private class LineObstacle
        {
            public LineObstacle(PointF start, PointF end, float width)
            {
                Start = start;
                End = end;
                Width = width;
            }

            public PointF Start { get; }
            public PointF End { get; }
            public float Width { get; }
        }

        private class RunwayInfo
        {
            public GeoPoint He { get; set; }
            public double Heading { get; set; }
            public string HeIdent { get; set; }
            public GeoPoint Le { get; set; }
            public string LeIdent { get; set; }
        }
    }

    public class LeadOnAnimationState
    {
        public LeadOnAnimationState()
        {
            IsAnimating = false;
            StartTime = DateTime.Now;
            TotalLength = 0;
            ProgressLength = 0;
            StartLength = 0;
            PreviousState = false;
            IsReverse = false;
            DurationSeconds = 1.0;
        }

        public double DurationSeconds { get; set; }
        public bool IsAnimating { get; set; }
        public bool IsReverse { get; set; }
        public bool PreviousState { get; set; }
        public float ProgressLength { get; set; }
        public float StartLength { get; set; }
        public DateTime StartTime { get; set; }
        public float TotalLength { get; set; }
    }

    public class StopbarCountdownTimer
    {
        public StopbarCountdownTimer(string barsId, TimeSpan duration)
        {
            BarsId = barsId;
            StartTime = DateTime.Now;
            CountdownDuration = duration;
        }

        public string BarsId { get; set; }
        public TimeSpan CountdownDuration { get; set; }
        public bool IsActive => DateTime.Now < StartTime.Add(CountdownDuration);
        public TimeSpan RemainingTime => IsActive ? StartTime.Add(CountdownDuration) - DateTime.Now : TimeSpan.Zero;
        public DateTime StartTime { get; set; }
    }

    public class WindState
    {
        public WindState(int direction, int speed)
        {
            CurrentDirection = direction;
            CurrentSpeed = speed;
            LastUpdate = DateTime.Now;
            TurbulenceFactor = 0f; // lazily randomized on first tick
            GustActive = false;
            GustUntil = DateTime.MinValue;
            GustSpeed = speed;
        }

        public int CurrentDirection { get; set; }
        public int CurrentSpeed { get; set; }

        public bool GustActive { get; set; }

        public int GustSpeed { get; set; }
        public DateTime GustUntil { get; set; }
        public DateTime LastUpdate { get; set; }

        public float TurbulenceFactor { get; set; }
    }
}