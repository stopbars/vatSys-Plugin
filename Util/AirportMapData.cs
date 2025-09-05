using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Xml;
using BARS.Util;
using System.Globalization;

namespace BARS.Util
{
    public class AirportMapData
    {
        public AirportMapData(string airportIcao)
        {
            AirportIcao = airportIcao;
            Taxiways = new TaxiwayData();
            LeadOnLights = new List<LeadOnLight>();
            Windsocks = new List<Windsock>();
            Stopbars = new List<MapStopbar>();
            MapBounds = 0.01;
            Rotation = 0.0;
        }

        public string AirportIcao { get; set; }
        public GeoPoint CenterPoint { get; set; }
        public List<LeadOnLight> LeadOnLights { get; set; }
        public double MapBounds { get; set; }
        public double Rotation { get; set; }

        public List<MapStopbar> Stopbars { get; set; }

        public TaxiwayData Taxiways { get; set; }
        public List<Windsock> Windsocks { get; set; }

        public static AirportMapData LoadFromXml(string airportIcao)
        {
            var mapData = new AirportMapData(airportIcao);

            try
            {
                XmlDocument doc = new XmlDocument();
                // Prefer CDN
                string cdnUrl = CdnProfiles.GetAirportXmlUrl(airportIcao);
                if (!string.IsNullOrEmpty(cdnUrl))
                {
                    string xml = CdnProfiles.DownloadXml(cdnUrl);
                    if (!string.IsNullOrWhiteSpace(xml))
                    {
                        doc.LoadXml(xml);
                    }
                    else
                    {
                        throw new FileNotFoundException($"Failed to download airport XML from {cdnUrl}");
                    }
                }
                else
                {
                    // Fallback to local file if CDN index has no entry
                    string xmlPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BARS", "vatSys", $"{airportIcao}.xml");
                    if (!File.Exists(xmlPath))
                    {
                        throw new FileNotFoundException($"Airport map file not found: {xmlPath}");
                    }
                    doc.Load(xmlPath);
                }

                LoadRotationFromPositions(mapData);

                LoadTaxiways(doc, mapData);

                LoadLeadOnLights(doc, mapData);

                LoadWindsocks(doc, mapData);

                LoadStopbars(doc, mapData);

                CalculateMapBounds(mapData);

                return mapData;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error loading airport map data: {ex.Message}", ex);
            }
        }

        // Robust coordinate parser for XML attributes.
        // - Uses invariant culture to avoid locale issues.
        // - If a value looks like micro-degrees (e.g., 1445036), convert to degrees by dividing by 10000.
        private static double ParseCoord(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0.0;
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                // Fallback to current culture if invariant fails
                double.TryParse(s, out v);
            }
            if (Math.Abs(v) > 180.0)
            {
                // Heuristic: values like 1445036 or -374024 are micro-degrees * 10000
                v = v / 10000.0;
            }
            return v;
        }

        public PointF GeoToScreen(GeoPoint geoPoint, RectangleF screenBounds, float zoomLevel, PointF panOffset)
        {
            if (CenterPoint == null)
                return new PointF(0, 0);

            GetMetersPerDegree(out double mPerDegLat, out double mPerDegLon);
            if (MapBounds <= 0)
            {
                return new PointF(0, 0);
            }

            double scale = (screenBounds.Width / MapBounds) * zoomLevel; // square viewport -> uniform scale
            if (double.IsInfinity(scale) || double.IsNaN(scale))
            {
                return new PointF(0, 0);
            }

            double dx = (geoPoint.Longitude - CenterPoint.Longitude) * mPerDegLon;
            double dy = (geoPoint.Latitude - CenterPoint.Latitude) * mPerDegLat;

            if (Math.Abs(Rotation) > 0.001)
            {
                // Rotation is stored as degrees clockwise from North; math rotation is CCW, so negate
                double rotationRadians = -Rotation * Math.PI / 180.0;
                double cosRot = Math.Cos(rotationRadians);
                double sinRot = Math.Sin(rotationRadians);
                double rx = dx * cosRot - dy * sinRot;
                double ry = dx * sinRot + dy * cosRot;
                dx = rx; dy = ry;
            }

            double rawX = dx * scale + screenBounds.X + screenBounds.Width / 2 + panOffset.X;
            double rawY = (-dy) * scale + screenBounds.Y + screenBounds.Height / 2 + panOffset.Y;

            if (Math.Abs(rawX) > float.MaxValue || Math.Abs(rawY) > float.MaxValue ||
                double.IsNaN(rawX) || double.IsNaN(rawY) ||
                double.IsInfinity(rawX) || double.IsInfinity(rawY))
            {
                return new PointF(0, 0);
            }

            float x = (float)rawX;
            float y = (float)rawY;

            return new PointF(x, y);
        }

        // Public wrapper to recompute bounds after changing rotation externally
        public void RecalculateBounds()
        {
            CalculateMapBounds(this);
        }

        public void UpdateLeadOnLightColor(string leadOnId, bool stopbarActive)
        {
            var leadOn = LeadOnLights.Find(l => l.Id == leadOnId);
            if (leadOn != null)
            {
                leadOn.CurrentColor = Color.LimeGreen;
            }
        }

        private static void CalculateMapBounds(AirportMapData mapData)
        {
            if (mapData.Taxiways.Lines.Count == 0 && mapData.LeadOnLights.Count == 0 && mapData.Windsocks.Count == 0 && mapData.Stopbars.Count == 0)
                return;

            // First pass: compute rough lat/lon bounds and set CenterPoint to mid, so meters-per-degree is stable.
            double minLon = double.MaxValue;
            double maxLon = double.MinValue;
            double minLat = double.MaxValue;
            double maxLat = double.MinValue;

            void ExpandLatLon(double lon, double lat)
            {
                minLon = Math.Min(minLon, lon);
                maxLon = Math.Max(maxLon, lon);
                minLat = Math.Min(minLat, lat);
                maxLat = Math.Max(maxLat, lat);
            }

            foreach (var line in mapData.Taxiways.Lines)
            {
                foreach (var point in line.Points)
                {
                    ExpandLatLon(point.Longitude, point.Latitude);
                }
            }

            foreach (var leadOn in mapData.LeadOnLights)
            {
                foreach (var point in leadOn.Line.Points)
                {
                    ExpandLatLon(point.Longitude, point.Latitude);
                }
            }

            foreach (var windsock in mapData.Windsocks)
            {
                ExpandLatLon(windsock.Position.Longitude, windsock.Position.Latitude);
            }

            foreach (var stopbar in mapData.Stopbars)
            {
                ExpandLatLon(stopbar.Position.Longitude, stopbar.Position.Latitude);
            }

            mapData.CenterPoint = new GeoPoint(
                (minLon + maxLon) / 2.0,
                (minLat + maxLat) / 2.0);

            // Second pass: compute bounds in local meters, after applying rotation
            mapData.GetMetersPerDegree(out double mPerDegLat, out double mPerDegLon);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;

            void AccumulateXY(double lon, double lat)
            {
                double dx = (lon - mapData.CenterPoint.Longitude) * mPerDegLon;
                double dy = (lat - mapData.CenterPoint.Latitude) * mPerDegLat;
                if (Math.Abs(mapData.Rotation) > 0.001)
                {
                    // Stored as clockwise; negate for CCW math rotation
                    double r = -mapData.Rotation * Math.PI / 180.0;
                    double c = Math.Cos(r); double s = Math.Sin(r);
                    double rx = dx * c - dy * s;
                    double ry = dx * s + dy * c;
                    dx = rx; dy = ry;
                }
                minX = Math.Min(minX, dx);
                maxX = Math.Max(maxX, dx);
                minY = Math.Min(minY, dy);
                maxY = Math.Max(maxY, dy);
            }

            foreach (var line in mapData.Taxiways.Lines)
            {
                foreach (var point in line.Points) AccumulateXY(point.Longitude, point.Latitude);
            }
            foreach (var leadOn in mapData.LeadOnLights)
            {
                foreach (var point in leadOn.Line.Points) AccumulateXY(point.Longitude, point.Latitude);
            }
            foreach (var windsock in mapData.Windsocks)
            {
                AccumulateXY(windsock.Position.Longitude, windsock.Position.Latitude);
            }
            foreach (var stopbar in mapData.Stopbars)
            {
                AccumulateXY(stopbar.Position.Longitude, stopbar.Position.Latitude);
            }

            double rangeX = (maxX - minX);
            double rangeY = (maxY - minY);
            mapData.MapBounds = Math.Max(rangeX, rangeY);
        }

        private static void LoadLeadOnLights(XmlDocument doc, AirportMapData mapData)
        {
            XmlNodeList leadOnNodes = doc.SelectNodes("//LeadOn"); foreach (XmlNode leadOnNode in leadOnNodes)
            {
                string id = leadOnNode.Attributes["id"].Value;
                var leadOn = new LeadOnLight(id);

                XmlNodeList lines = leadOnNode.SelectNodes("Line");
                if (lines.Count > 0)
                {
                    XmlNode lineNode = lines[0];
                    XmlNodeList points = lineNode.SelectNodes("Point");

                    foreach (XmlNode pointNode in points)
                    {
                        double lon = ParseCoord(pointNode.Attributes["lon"].Value);
                        double lat = ParseCoord(pointNode.Attributes["lat"].Value);
                        leadOn.Line.Points.Add(new GeoPoint(lon, lat));
                    }
                }

                if (leadOn.Line.Points.Count > 0)
                {
                    mapData.LeadOnLights.Add(leadOn);
                }
            }
        }

        private static void LoadRotationFromPositions(AirportMapData mapData)
        {
            try
            {
                string positionsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "vatSys Files",
                    "Profiles",
                    "Australia",
                    "Positions.xml");

                if (!File.Exists(positionsPath))
                {
                    return;
                }

                XmlDocument positionsDoc = new XmlDocument();
                positionsDoc.Load(positionsPath);

                XmlNode positionNode = positionsDoc.SelectSingleNode($"//Position[@Name='{mapData.AirportIcao}' and @Type='ASMGCS']");

                if (positionNode != null)
                {
                    string rotationAttr = positionNode.Attributes["Rotation"]?.Value;
                    string magVarAttr = positionNode.Attributes["MagneticVariation"]?.Value;
                    double rotation = 0.0;
                    double magVar = 0.0;
                    bool hasRot = !string.IsNullOrEmpty(rotationAttr) && double.TryParse(rotationAttr, out rotation);
                    bool hasMag = !string.IsNullOrEmpty(magVarAttr) && double.TryParse(magVarAttr, out magVar);

                    if (hasRot)
                    {
                        mapData.Rotation = hasMag ? (rotation + magVar) : rotation;
                    }
                }
            }
            catch (Exception)
            {
                mapData.Rotation = 0.0;
            }
        }

        private static void LoadStopbars(XmlDocument doc, AirportMapData mapData)
        {
            XmlNodeList stopbarNodes = doc.SelectNodes("//Stopbars/Stopbar");

            foreach (XmlNode stopbarNode in stopbarNodes)
            {
                try
                {
                    XmlNode barsIdNode = stopbarNode.SelectSingleNode("BARSId");
                    XmlNode displayNameNode = stopbarNode.SelectSingleNode("DisplayName");
                    XmlNode positionNode = stopbarNode.SelectSingleNode("Position");
                    XmlNode headingNode = stopbarNode.SelectSingleNode("Heading");

                    if (barsIdNode != null && displayNameNode != null && positionNode != null && headingNode != null)
                    {
                        string barsId = barsIdNode.InnerText;
                        string displayName = displayNameNode.InnerText;
                        double lon = ParseCoord(positionNode.Attributes["lon"].Value);
                        double lat = ParseCoord(positionNode.Attributes["lat"].Value);
                        double heading = double.Parse(headingNode.InnerText, CultureInfo.InvariantCulture);

                        var stopbar = new MapStopbar(barsId, displayName, new GeoPoint(lon, lat), heading);

                        XmlNodeList leadOnNodes = stopbarNode.SelectNodes("LeadOn");
                        foreach (XmlNode leadOnNode in leadOnNodes)
                        {
                            if (leadOnNode.Attributes["id"] != null)
                            {
                                string leadOnId = leadOnNode.Attributes["id"].Value;
                                stopbar.LeadOnIds.Add(leadOnId);
                            }
                        }

                        mapData.Stopbars.Add(stopbar);

                        ControllerHandler.RegisterStopbar(mapData.AirportIcao, displayName, barsId);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading stopbar: {ex.Message}");
                }
            }
        }

        private static void LoadTaxiways(XmlDocument doc, AirportMapData mapData)
        {
            XmlNodeList taxiwayLines = doc.SelectNodes("//Taxiways/Line");

            foreach (XmlNode lineNode in taxiwayLines)
            {
                var line = new GeoLine();
                XmlNodeList points = lineNode.SelectNodes("Point");
                foreach (XmlNode pointNode in points)
                {
                    double lon = ParseCoord(pointNode.Attributes["lon"].Value);
                    double lat = ParseCoord(pointNode.Attributes["lat"].Value);
                    line.Points.Add(new GeoPoint(lon, lat));
                }

                if (line.Points.Count > 0)
                {
                    mapData.Taxiways.Lines.Add(line);
                }
            }
        }

        private static void LoadWindsocks(XmlDocument doc, AirportMapData mapData)
        {
            XmlNodeList windsockNodes = doc.SelectNodes("//Windsocks/Windsock");

            foreach (XmlNode windsockNode in windsockNodes)
            {
                if (windsockNode.Attributes["lon"] != null && windsockNode.Attributes["lat"] != null)
                {
                    double lon = ParseCoord(windsockNode.Attributes["lon"].Value);
                    double lat = ParseCoord(windsockNode.Attributes["lat"].Value);

                    var windsock = new Windsock(new GeoPoint(lon, lat));
                    mapData.Windsocks.Add(windsock);
                }
            }
        }

        // Provide meters-per-degree at the current map center latitude
        private void GetMetersPerDegree(out double mPerDegLat, out double mPerDegLon)
        {
            double lat0 = (CenterPoint?.Latitude ?? 0.0) * Math.PI / 180.0;
            mPerDegLat = 111320.0;               // Approx meters per degree latitude
            mPerDegLon = Math.Cos(lat0) * 111320.0; // Scaled meters per degree longitude
            if (mPerDegLon < 1e-6) mPerDegLon = 1e-6; // guard against poles
        }
    }

    public class GeoLine
    {
        public GeoLine()
        {
            Points = new List<GeoPoint>();
        }

        public List<GeoPoint> Points { get; set; }
    }

    public class GeoPoint
    {
        public GeoPoint(double longitude, double latitude)
        {
            Longitude = longitude;
            Latitude = latitude;
        }

        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class LeadOnLight
    {
        public LeadOnLight(string id)
        {
            Id = id;
            Line = new GeoLine();
            CurrentColor = Color.Gray;
        }

        public Color CurrentColor { get; set; }
        public string Id { get; set; }
        public GeoLine Line { get; set; }
    }

    public class MapStopbar
    {
        public MapStopbar(string barsId, string displayName, GeoPoint position, double heading)
        {
            BarsId = barsId;
            DisplayName = displayName;
            Position = position;
            Heading = heading;
            State = true;
            LeadOnIds = new List<string>();
        }

        public string BarsId { get; set; }
        public string DisplayName { get; set; }
        public double Heading { get; set; }
        public List<string> LeadOnIds { get; set; }
        public GeoPoint Position { get; set; }
        public bool State { get; set; }
    }

    public class TaxiwayData
    {
        public TaxiwayData()
        {
            Lines = new List<GeoLine>();
        }

        public List<GeoLine> Lines { get; set; }
    }

    public class Windsock
    {
        public Windsock(GeoPoint position)
        {
            Position = position;
        }

        public GeoPoint Position { get; set; }
    }
}