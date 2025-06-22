using BARS.Util;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace BARS.Windows
{
    public class AirportMapControl : Control
    {
        private const int ANIMATION_FRAMES = 60;
        private const int ANIMATION_INTERVAL = 100;
        private const float LeadOnLineWidthOff = 0.5f;
        private const float LeadOnLineWidthOn = 1.0f;
        private const float MAX_ZOOM = 10.0f;
        private const float MIN_LINE_WIDTH = 0.5f;
        private const float MIN_ZOOM = 0.1f;
        private const int STOPBAR_BASE_SIZE = 20;
        private const float TaxiwayLineWidth = 1.0f;

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

        private int _baseWindSpeed = 0;

        private int _defaultCountdownSeconds = 45;

        private Dictionary<string, List<MapElement>> _groundElements = new Dictionary<string, List<MapElement>>();
        private bool _isDragging = false;
        private Point _lastMousePosition;
        private Dictionary<string, LeadOnAnimationState> _leadOnAnimations = new Dictionary<string, LeadOnAnimationState>();
        private AirportMapData _mapData;
        private PointF _panOffset = new PointF(0, 0);
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
                UpdateLeadOnLight(barsId, stopbarActive);
                return;
            }

            if (stopbar.LeadOnIds != null && stopbar.LeadOnIds.Count > 0)
            {
                foreach (string leadOnId in stopbar.LeadOnIds)
                {
                    UpdateLeadOnLight(leadOnId, stopbarActive);
                }
                _logger.Log($"Updated {stopbar.LeadOnIds.Count} lead-on lights for stopbar {barsId}");
            }
            else
            {
                UpdateLeadOnLight(barsId, stopbarActive);
            }
        }

        public void UpdateStopbarState(string barsId, bool state)
        {
            if (_mapData == null || _mapData.Stopbars == null)
                return;

            var stopbar = _mapData.Stopbars.FirstOrDefault(s => s.BarsId == barsId);
            if (stopbar != null)
            {
                stopbar.State = state;

                if (state)
                {
                    StopStopbarCountdown(barsId);
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
                var windMatch = System.Text.RegularExpressions.Regex.Match(metarText, @"(\d{3})(\d{2,3})(?:G\d{2,3})?KT");

                if (windMatch.Success)
                {
                    if (int.TryParse(windMatch.Groups[1].Value, out int direction) &&
                        int.TryParse(windMatch.Groups[2].Value, out int speed))
                    {
                        _baseWindDirection = direction;
                        _baseWindSpeed = speed;

                        _logger.Log($"Updated wind from METAR: {direction}° at {speed} knots");

                        if (_mapData?.Windsocks != null)
                        {
                            foreach (var windsock in _mapData.Windsocks)
                            {
                                _windsockStates[windsock] = new WindState(direction, speed);
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

                        _logger.Log("Wind conditions: CALM");

                        if (_mapData?.Windsocks != null)
                        {
                            foreach (var windsock in _mapData.Windsocks)
                            {
                                _windsockStates[windsock] = new WindState(0, 0);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error parsing wind from METAR: {ex.Message}");
            }
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

                    if (e.Button == MouseButtons.Left)
                    {
                        _logger.Log($"Starting countdown for stopbar {clickedStopbar.BarsId}");
                        StartStopbarCountdown(clickedStopbar.BarsId, TimeSpan.FromSeconds(_defaultCountdownSeconds));
                    }

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

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            _animationFrame = (_animationFrame + 1) % ANIMATION_FRAMES;

            bool needsRepaint = false;
            foreach (var kvp in _leadOnAnimations.ToList())
            {
                var animState = kvp.Value; if (animState.IsAnimating)
                {
                    var elapsed = (DateTime.Now - animState.StartTime).TotalMilliseconds;
                    var animationSpeed = 75.0f;

                    if (animState.IsReverse)
                    {
                        animState.ProgressLength = animState.TotalLength - (float)(elapsed / 1000.0 * animationSpeed);

                        if (animState.ProgressLength <= 0)
                        {
                            animState.IsAnimating = false;
                            animState.ProgressLength = 0;
                        }
                    }
                    else
                    {
                        animState.ProgressLength = (float)(elapsed / 1000.0 * animationSpeed);

                        if (animState.ProgressLength >= animState.TotalLength)
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
                float adjustedHeading = (float)(stopbar.Heading - _mapData.Rotation);

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

                g.TranslateTransform(screenPos.X, screenPos.Y);
                g.RotateTransform((float)stopbar.Heading + (float)_mapData.Rotation + 180f);
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
                    new WindState(_baseWindDirection, _baseWindSpeed); float windAngle = windState.CurrentDirection;
                float mapRotation = (float)_mapData.Rotation; float totalRotation = windAngle + mapRotation + 180f;
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

                float pivotX = screenPoint.X - imageSize / 2f;
                float pivotY = screenPoint.Y + imageSize / 2f;

                g.TranslateTransform(pivotX, pivotY);
                g.RotateTransform(totalRotation);
                g.TranslateTransform(0, -imageSize);

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

        private bool IsLeadOnStopbarActive(string leadOnId)
        {
            if (_mapData?.Stopbars == null) return false;

            foreach (var stopbar in _mapData.Stopbars)
            {
                if (stopbar.LeadOnIds != null && stopbar.LeadOnIds.Contains(leadOnId))
                {
                    return stopbar.State;
                }
            }

            return false;
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
            foreach (var windsock in _windsockStates.Keys.ToList())
            {
                var currentState = _windsockStates[windsock];

                int directionVariation = _windRandom.Next(-10, 11);
                int newDirection = (_baseWindDirection + directionVariation + 360) % 360;

                int speedVariation = _windRandom.Next(-1, 2);
                int newSpeed = Math.Max(0, _baseWindSpeed + speedVariation);

                _windsockStates[windsock] = new WindState(newDirection, newSpeed);
            }

            _windSimulationTimer.Interval = _windRandom.Next(1000, 2000);

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
        }

        public int CurrentDirection { get; set; }
        public int CurrentSpeed { get; set; }
        public DateTime LastUpdate { get; set; }
    }
}