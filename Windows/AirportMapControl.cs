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

        // Angle snapping for stopbar sprites (visual nicety)
        private const float DEFAULT_ANGLE_SNAP_TOLERANCE_DEG = 7.5f;

        private const float LeadOnLineWidthOff = 0.5f;
        private const float LeadOnLineWidthOn = 1.0f;
        private const float MAX_ZOOM = 10.0f;
        private const float MIN_LINE_WIDTH = 0.5f;
        private const float MIN_ZOOM = 0.1f;
        private const int STOPBAR_BASE_SIZE = 16;
        private const float TaxiwayLineWidth = 1.0f;
        private static readonly HttpClient _httpClient = new HttpClient();

        // snap when within this many degrees of a target
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

        // parsed from METAR Gxx
        private bool _isVariableWind = false; // METAR VRB indicator

        private Point _lastMousePosition;
        private Dictionary<string, LeadOnAnimationState> _leadOnAnimations = new Dictionary<string, LeadOnAnimationState>();
        private AirportMapData _mapData;
        private PointF _panOffset = new PointF(0, 0);
        private List<RunwayInfo> _runways;
        private float _scalingRatio = 1.0f;
        private Dictionary<string, StopbarCountdownTimer> _stopbarCountdowns = new Dictionary<string, StopbarCountdownTimer>();
        private Random _windRandom = new Random();
        private Dictionary<Windsock, WindState> _windsockStates = new Dictionary<Windsock, WindState>();
        private float _zoomLevel = 1.0f;

        public AirportMapControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.DoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            BackColor = BackgroundColor;

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
            MapStopbar closestStopbar = null;
            float closestDistance = float.MaxValue;

            float baseClickDistance = 50.0f;
            float scaledClickDistance = baseClickDistance * _zoomLevel;

            foreach (var stopbar in _mapData.Stopbars)
            {
                PointF screenPos = _mapData.GeoToScreen(stopbar.Position, bounds, _zoomLevel, _panOffset);

                float distanceX = clickPoint.X - screenPos.X;
                float distanceY = clickPoint.Y - screenPos.Y;
                float distance = (float)Math.Sqrt((distanceX * distanceX) + (distanceY * distanceY));

                if (distance <= scaledClickDistance && distance < closestDistance)
                {
                    closestDistance = distance;
                    closestStopbar = stopbar;
                }
            }

            return closestStopbar;
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

                _windsockStates.Clear();
                if (_mapData?.Windsocks != null)
                {
                    foreach (var windsock in _mapData.Windsocks)
                    {
                        _windsockStates[windsock] = new WindState(_baseWindDirection, _baseWindSpeed);
                    }
                }

                Invalidate();

                // Fire-and-forget fetch of runway geometry used for crosswind checks
                _ = FetchRunwaysAsync(airportIcao);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load airport map for {airportIcao}: {ex.Message}");
                _mapData = null;
                Invalidate();
            }
        }

        public void LoadGroundLayout(Dictionary<string, List<MapElement>> groundElements)
        {
            _groundElements = groundElements ?? new Dictionary<string, List<MapElement>>();
            Invalidate();
        }

        public void ResetZoomAndPan()
        {
            _zoomLevel = 1.0f;
            _panOffset = new PointF(0, 0);
            Invalidate();
        }

        public void SetDefaultCountdownDuration(int seconds)
        {
            _defaultCountdownSeconds = Math.Max(1, Math.Min(300, seconds));
        }

        // Allows overriding the map rotation (clockwise degrees) using external sources (e.g., ASMGCS file)
        public void SetMapRotation(double rotationDegreesCW)
        {
            if (_mapData == null) return;
            _mapData.Rotation = rotationDegreesCW;
            try { _mapData.RecalculateBounds(); } catch { /* ignore if not yet ready */ }
            Invalidate();
        }

        public void SetPan(PointF panOffset)
        {
            _panOffset = panOffset;
            Invalidate();
        }

        public void SetScalingRatio(float ratio)
        {
            _scalingRatio = ratio;
            Invalidate();
        }

        public void SetWind(int direction, int speed)
        {
            _baseWindDirection = direction;
            _baseWindSpeed = speed;
            _baseWindGust = Math.Max(speed, _baseWindGust); // keep gust >= speed; leave as-is if previously higher
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
                        animState.IsAnimating = true;
                        animState.StartTime = DateTime.Now;
                        animState.TotalLength = CalculateLeadOnLength(leadOn);
                        animState.ProgressLength = animState.TotalLength;
                        animState.IsReverse = true;
                        _logger.Log($"Starting reverse animation for lead-on {leadOnId}, length: {animState.TotalLength:F1}m");
                    }
                }
                else if (previousStopbarState && !stopbarActive)
                {
                    var leadOn = _mapData.LeadOnLights.Find(l => l.Id == leadOnId);
                    if (leadOn != null)
                    {
                        animState.IsAnimating = true;
                        animState.StartTime = DateTime.Now;
                        animState.TotalLength = CalculateLeadOnLength(leadOn);
                        animState.ProgressLength = 0;
                        animState.IsReverse = false;
                        _logger.Log($"Starting forward animation for lead-on {leadOnId}, length: {animState.TotalLength:F1}m");
                    }
                }

                animState.PreviousState = currentState;

                Invalidate();
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
                // Aggregate across all stopbars that reference each lead-on: lead-on is OFF only when all controlling stopbars are active (raised)
                foreach (string leadOnId in stopbar.LeadOnIds)
                {
                    bool aggregatedActive = IsLeadOnStopbarActive(leadOnId);
                    UpdateLeadOnLight(leadOnId, aggregatedActive);
                }
                _logger.Log($"Updated {stopbar.LeadOnIds.Count} lead-on lights for stopbar {barsId}");
            }
            else
            {
                bool aggregatedActive = IsLeadOnStopbarActive(barsId);
                UpdateLeadOnLight(barsId, aggregatedActive);
            }
        }

        // Backward-compat wrapper: assumes autoRaise when not specified
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

            // Ensure UI updates to reflect new wind and potential crosswind highlighting
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

                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button == MouseButtons.Middle && _isDragging)
            {
                _isDragging = false;
                this.Cursor = Cursors.Default;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

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
            DrawStopbars(e.Graphics, squareBounds);

            DrawStopbarCountdownLabels(e.Graphics, squareBounds);
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
            var Px = 0.0; // by definition of local frame centered at p
            var Py = 0.0;

            // Vector operations in 2D
            double ABx = Bx - Ax;
            double ABy = By - Ay;
            double APx = Px - Ax;
            double APy = Py - Ay;
            double ab2 = ABx * ABx + ABy * ABy;
            if (ab2 <= 1e-6)
            {
                // Degenerate segment – return distance to A
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
            foreach (var kvp in _leadOnAnimations.ToList())
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
                    // Ensure the animation completes in at most 5 seconds.
                    // Use a baseline speed for short lead-ons and scale up speed for longer ones so duration <= 5s.
                    var elapsedSec = (DateTime.Now - animState.StartTime).TotalSeconds;
                    const float BASE_SPEED_MPS = 75.0f; // meters per second for typical short segments
                    const double MAX_DURATION_SEC = 5.0; // hard cap
                    float animationSpeed = BASE_SPEED_MPS;
                    if (animState.TotalLength > 0f)
                    {
                        // If the baseline would take longer than 5s, increase speed so it finishes in 5s.
                        float speedForCap = animState.TotalLength / (float)MAX_DURATION_SEC;
                        animationSpeed = Math.Max(BASE_SPEED_MPS, speedForCap);
                    }

                    if (animState.IsReverse)
                    {
                        animState.ProgressLength = animState.TotalLength - (float)(elapsedSec * animationSpeed);
                        if (animState.ProgressLength <= 0f)
                        {
                            animState.IsAnimating = false;
                            animState.ProgressLength = 0f;
                        }
                    }
                    else
                    {
                        animState.ProgressLength = (float)(elapsedSec * animationSpeed);
                        if (animState.TotalLength > 0f && animState.ProgressLength >= animState.TotalLength)
                        {
                            animState.IsAnimating = false;
                            animState.ProgressLength = animState.TotalLength;
                        }
                    }

                    needsRepaint = true;
                }
            }

            var expiredCountdowns = _stopbarCountdowns.Where(kvp => !kvp.Value.IsActive).ToList();
            foreach (var expired in expiredCountdowns)
            {
                _stopbarCountdowns.Remove(expired.Key);
                needsRepaint = true;
            }

            if (_stopbarCountdowns.Count > 0)
            {
                needsRepaint = true;
            }

            if (needsRepaint)
            {
                Invalidate();
            }
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

            // Choose nearest runway by shortest distance to runway segment in a local tangent plane
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
            // Extract leading digits (handles 16, 34L, 05, etc.)
            int i = 0;
            while (i < ident.Length && char.IsDigit(ident[i])) i++;
            if (i == 0) return false;
            if (!int.TryParse(ident.Substring(0, i), out int num)) return false;
            // Map runway number to degrees. 36 => 360 => treat as 0° to keep 0..359.
            if (num == 36) headingDeg = 0;
            else headingDeg = (num % 36) * 10;
            // Normalize just in case
            if (headingDeg < 0) headingDeg = (headingDeg % 360 + 360) % 360;
            else headingDeg = headingDeg % 360;
            return true;
        }

        private void DrawAnimatedLeadOn(Graphics g, Pen pen, LeadOnLight leadOn, PointF[] screenPoints, LeadOnAnimationState animState)
        {
            if (screenPoints.Length < 2) return;
            g.DrawLines(pen, screenPoints);
            float scaledAnimationWidth = Math.Max(MIN_LINE_WIDTH, TaxiwayLineWidth * _zoomLevel * _scalingRatio);
            using (var animationPen = new Pen(TaxiwayColor, scaledAnimationWidth))
            {
                animationPen.StartCap = LineCap.Flat;
                animationPen.EndCap = LineCap.Flat;
                animationPen.LineJoin = LineJoin.Round;

                float totalScreenLength = 0;
                var segmentLengths = new List<float>();

                for (int i = 1; i < screenPoints.Length; i++)
                {
                    float segmentLength = (float)Math.Sqrt(
                        Math.Pow(screenPoints[i].X - screenPoints[i - 1].X, 2) +
                        Math.Pow(screenPoints[i].Y - screenPoints[i - 1].Y, 2)
                    );
                    segmentLengths.Add(segmentLength);
                    totalScreenLength += segmentLength;
                }

                if (totalScreenLength == 0) return;

                float progressRatio = Math.Min(1.0f, animState.ProgressLength / animState.TotalLength);
                float targetScreenLength = totalScreenLength * progressRatio;

                float minSegmentSize = 35.0f;
                float segmentSize = Math.Max(minSegmentSize, totalScreenLength * 0.1f);

                float currentLength = 0;
                var pointsToDrawEnd = new List<PointF>();

                for (int i = screenPoints.Length - 1; i > 0; i--)
                {
                    float segmentLength = segmentLengths[i - 1];

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
                                bool validPoints = true; for (int i = 0; i < element.Points.Count; i++)
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

                    var screenPoints = new PointF[leadOn.Line.Points.Count];
                    for (int i = 0; i < leadOn.Line.Points.Count; i++)
                    {
                        screenPoints[i] = _mapData.GeoToScreen(leadOn.Line.Points[i], bounds, _zoomLevel, _panOffset);
                    }

                    if (animState != null && animState.IsAnimating)
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

            using (var font = new Font("Arial", scaledFontSize, FontStyle.Bold))
            using (var textBrush = new SolidBrush(Color.White))
            using (var backgroundBrush = new SolidBrush(Color.Black))
            {
                var textSize = g.MeasureString(timeText, font);

                float padding = 0.5f * _scalingRatio * _zoomLevel;

                float horizontalPadding = padding + (1.0f * _scalingRatio * _zoomLevel);
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
        }

        private void DrawStopbarCountdownLabels(Graphics g, RectangleF bounds)
        {
            if (_mapData?.Stopbars == null || _stopbarCountdowns.Count == 0)
                return;

            foreach (var stopbar in _mapData.Stopbars)
            {
                if (!_stopbarCountdowns.ContainsKey(stopbar.BarsId) || !_stopbarCountdowns[stopbar.BarsId].IsActive)
                    continue;

                PointF screenPos = _mapData.GeoToScreen(stopbar.Position, bounds, _zoomLevel, _panOffset);

                int imageSize = (int)(STOPBAR_BASE_SIZE * _scalingRatio * _zoomLevel);

                if (imageSize <= 0)
                {
                    imageSize = 1;
                }

                float margin = Math.Max(imageSize * 3, 200);
                if (screenPos.X < -margin || screenPos.X > this.Width + margin ||
                    screenPos.Y < -margin || screenPos.Y > this.Height + margin)
                {
                    continue;
                }

                DrawStopbarCountdownLabel(g, stopbar, screenPos, imageSize);
            }
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

        private void DrawStopbars(Graphics g, RectangleF bounds)
        {
            if (_mapData?.Stopbars == null)
                return; foreach (var stopbar in _mapData.Stopbars)
            {
                PointF screenPos = _mapData.GeoToScreen(stopbar.Position, bounds, _zoomLevel, _panOffset);
                int imageSize = (int)(STOPBAR_BASE_SIZE * _scalingRatio * _zoomLevel);

                if (imageSize <= 0)
                {
                    imageSize = 1;
                }
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
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.CompositingMode = CompositingMode.SourceOver;

                // Translate to stopbar position and rotate using unified helper (accounts for map rotation and sprite orientation)
                float adjustedHeading = GetStopbarRotation(stopbar);
                g.TranslateTransform(screenPos.X, screenPos.Y);
                g.RotateTransform(adjustedHeading);
                g.TranslateTransform(-imageSize / 2f, -imageSize / 2f);

                g.DrawImage(stopbarImage, 0, 0, imageSize, imageSize);
                g.Restore(state);
            }
        }

        private void DrawTaxiways(Graphics g, RectangleF bounds)
        {
            float scaledLineWidth = Math.Max(MIN_LINE_WIDTH, TaxiwayLineWidth * _zoomLevel * _scalingRatio);
            using (var pen = new Pen(TaxiwayColor, scaledLineWidth))
            {
                pen.StartCap = LineCap.Flat;
                pen.EndCap = LineCap.Flat;
                pen.LineJoin = LineJoin.Round;

                foreach (var line in _mapData.Taxiways.Lines)
                {
                    if (line.Points.Count < 2) continue;

                    var screenPoints = new PointF[line.Points.Count];
                    for (int i = 0; i < line.Points.Count; i++)
                    {
                        screenPoints[i] = _mapData.GeoToScreen(line.Points[i], bounds, _zoomLevel, _panOffset);
                    }

                    if (screenPoints.Length >= 2)
                    {
                        g.DrawLines(pen, screenPoints);
                    }
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
            foreach (var windsock in _mapData.Windsocks)
            {
                var screenPoint = _mapData.GeoToScreen(windsock.Position, bounds, _zoomLevel, _panOffset);

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

                var state = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // Rotate around the icon center for predictable orientation
                g.TranslateTransform(screenPoint.X, screenPoint.Y);
                g.RotateTransform(totalRotation);
                g.TranslateTransform(-imageSize / 2f, -imageSize / 2f);

                g.DrawImage(windsockImage, 0, 0, imageSize, imageSize);

                g.Restore(state);
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
                    var textSize = g.MeasureString(windText, font);
                    float textX = screenPoint.X - textSize.Width / 2;
                    float textY = screenPoint.Y + (imageSize / 2) + (16 * _scalingRatio * _zoomLevel);

                    // Compute wind components and draw orange background for high crosswind or tailwind
                    ComputeWindComponentsForWindsock(windsock, out float crosswind, out float tailwind);
                    bool highCrosswind = crosswind > 25.0f;
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

        // ---------------- Runway/crosswind helpers ----------------
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
                // Trigger repaint when runway data arrives
                if (_runways != null && _runways.Count > 0)
                {
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

            // Find all stopbars that control this lead-on
            var controlling = _mapData.Stopbars
                .Where(s => s.LeadOnIds != null && s.LeadOnIds.Contains(leadOnId))
                .ToList();

            if (controlling.Count == 0)
            {
                // If no controlling stopbars explicitly reference this lead-on, default to not active
                // so the lead-on will render as ON (common fallback behaviour in this UI)
                return false;
            }

            // Lead-on is considered "stopbar active" only if ALL controlling stopbars are active (raised)
            // This makes the lead-on behave like an OR gate: any dropped stopbar keeps the lead-on ON.
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

                Invalidate();
            }
        }

        private void WindSimulationTimer_Tick(object sender, EventArgs e)
        {
            // Determine global conditions
            int baseDir = _baseWindDirection;
            int baseSpd = Math.Max(0, _baseWindSpeed);
            int baseGust = Math.Max(baseSpd, _baseWindGust);
            bool vrb = _isVariableWind;

            foreach (var windsock in _windsockStates.Keys.ToList())
            {
                var s = _windsockStates[windsock];

                // Initialize turbulence factor if default
                if (s.TurbulenceFactor <= 0f)
                {
                    s.TurbulenceFactor = 0.7f + (float)_windRandom.NextDouble() * 0.8f; // 0.7 .. 1.5
                }

                // Scale of variation based on speed (less at low speeds)
                float speedScale = baseSpd <= 1 ? 0.15f : Math.Min(1f, baseSpd / 20f); // 0..1
                float turb = s.TurbulenceFactor;

                // Direction target and step
                int dirTarget = baseDir;
                int dirWander = vrb ? _windRandom.Next(-60, 61) : _windRandom.Next(-12, 13);
                dirWander = (int)(dirWander * turb);
                dirTarget = NormalizeDegrees(dirTarget + dirWander);

                float dirStep = (vrb ? 10f : 4f) * speedScale * turb; // deg per tick toward target
                if (baseSpd == 0) dirStep = 0; // calm, no movement

                int newDir = (int)Math.Round(MoveTowardsAngle(s.CurrentDirection, dirTarget, dirStep));
                newDir = NormalizeDegrees(newDir);

                // Gust logic: transient push towards gust speed
                DateTime now = DateTime.Now;
                if (baseGust > baseSpd)
                {
                    // chance to (re)trigger a gust event when none active
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

                // Speed step depends on turbulence and whether gust is active
                float baseStep = 0.6f + 1.2f * speedScale; // 0.6..1.8
                if (s.GustActive) baseStep *= 1.8f; // accelerate during gust
                baseStep *= turb;

                // Add small random micro-jitter
                int jitter = _windRandom.Next(-1, 2);
                int newSpd = (int)Math.Round(MoveTowards(s.CurrentSpeed, speedTarget, baseStep)) + jitter;
                newSpd = Math.Max(0, newSpd);

                s.CurrentDirection = newDir;
                s.CurrentSpeed = newSpd;
                s.LastUpdate = now;

                _windsockStates[windsock] = s;
            }

            // Adjust timer interval dynamically: faster during variable/gusty conditions
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
            PreviousState = false;
            IsReverse = false;
        }

        public bool IsAnimating { get; set; }
        public bool IsReverse { get; set; }
        public bool PreviousState { get; set; }
        public float ProgressLength { get; set; }
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

        // Gust event state for this windsock
        public bool GustActive { get; set; }

        public int GustSpeed { get; set; }
        public DateTime GustUntil { get; set; }
        public DateTime LastUpdate { get; set; }

        // Per-windsock variability
        public float TurbulenceFactor { get; set; }
    }
}