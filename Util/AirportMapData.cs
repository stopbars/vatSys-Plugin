using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Xml;

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
            string xmlPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BARS", "vatSys", $"{airportIcao}.xml");

            if (!File.Exists(xmlPath))
            {
                throw new FileNotFoundException($"Airport map file not found: {xmlPath}");
            }

            var mapData = new AirportMapData(airportIcao);

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(xmlPath);

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

        public PointF GeoToScreen(GeoPoint geoPoint, RectangleF screenBounds)
        {
            if (CenterPoint == null)
                return new PointF(0, 0);

            double scaleX = screenBounds.Width / MapBounds;
            double scaleY = screenBounds.Height / MapBounds;

            if (MapBounds <= 0 || double.IsInfinity(scaleX) || double.IsInfinity(scaleY))
            {
                return new PointF(0, 0);
            }

            double deltaX = geoPoint.Longitude - CenterPoint.Longitude;
            double deltaY = CenterPoint.Latitude - geoPoint.Latitude;

            if (Math.Abs(Rotation) > 0.001)
            {
                double rotationRadians = Rotation * Math.PI / 180.0;
                double cosRot = Math.Cos(rotationRadians);
                double sinRot = Math.Sin(rotationRadians);

                double rotatedX = deltaX * cosRot - deltaY * sinRot;
                double rotatedY = deltaX * sinRot + deltaY * cosRot;

                deltaX = rotatedX;
                deltaY = rotatedY;
            }
            double rawX = deltaX * scaleX + screenBounds.X + screenBounds.Width / 2;
            double rawY = deltaY * scaleY + screenBounds.Y + screenBounds.Height / 2;

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

        public PointF GeoToScreen(GeoPoint geoPoint, RectangleF screenBounds, float zoomLevel, PointF panOffset)
        {
            if (CenterPoint == null)
                return new PointF(0, 0);

            double scaleX = (screenBounds.Width / MapBounds) * zoomLevel;
            double scaleY = (screenBounds.Height / MapBounds) * zoomLevel;

            if (MapBounds <= 0 || double.IsInfinity(scaleX) || double.IsInfinity(scaleY))
            {
                return new PointF(0, 0);
            }

            double deltaX = geoPoint.Longitude - CenterPoint.Longitude;
            double deltaY = CenterPoint.Latitude - geoPoint.Latitude;

            if (Math.Abs(Rotation) > 0.001)
            {
                double rotationRadians = Rotation * Math.PI / 180.0;
                double cosRot = Math.Cos(rotationRadians);
                double sinRot = Math.Sin(rotationRadians);

                double rotatedX = deltaX * cosRot - deltaY * sinRot;
                double rotatedY = deltaX * sinRot + deltaY * cosRot;

                deltaX = rotatedX;
                deltaY = rotatedY;
            }

            double rawX = deltaX * scaleX + screenBounds.X + screenBounds.Width / 2 + panOffset.X;
            double rawY = deltaY * scaleY + screenBounds.Y + screenBounds.Height / 2 + panOffset.Y;

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

            double minLon = double.MaxValue;
            double maxLon = double.MinValue;
            double minLat = double.MaxValue;
            double maxLat = double.MinValue;

            foreach (var line in mapData.Taxiways.Lines)
            {
                foreach (var point in line.Points)
                {
                    minLon = Math.Min(minLon, point.Longitude);
                    maxLon = Math.Max(maxLon, point.Longitude);
                    minLat = Math.Min(minLat, point.Latitude);
                    maxLat = Math.Max(maxLat, point.Latitude);
                }
            }

            foreach (var leadOn in mapData.LeadOnLights)
            {
                foreach (var point in leadOn.Line.Points)
                {
                    minLon = Math.Min(minLon, point.Longitude);
                    maxLon = Math.Max(maxLon, point.Longitude);
                    minLat = Math.Min(minLat, point.Latitude);
                    maxLat = Math.Max(maxLat, point.Latitude);
                }
            }

            foreach (var windsock in mapData.Windsocks)
            {
                minLon = Math.Min(minLon, windsock.Position.Longitude);
                maxLon = Math.Max(maxLon, windsock.Position.Longitude);
                minLat = Math.Min(minLat, windsock.Position.Latitude);
                maxLat = Math.Max(maxLat, windsock.Position.Latitude);
            }

            foreach (var stopbar in mapData.Stopbars)
            {
                minLon = Math.Min(minLon, stopbar.Position.Longitude);
                maxLon = Math.Max(maxLon, stopbar.Position.Longitude);
                minLat = Math.Min(minLat, stopbar.Position.Latitude);
                maxLat = Math.Max(maxLat, stopbar.Position.Latitude);
            }

            foreach (var stopbar in mapData.Stopbars)
            {
                minLon = Math.Min(minLon, stopbar.Position.Longitude);
                maxLon = Math.Max(maxLon, stopbar.Position.Longitude);
                minLat = Math.Min(minLat, stopbar.Position.Latitude);
                maxLat = Math.Max(maxLat, stopbar.Position.Latitude);
            }

            mapData.CenterPoint = new GeoPoint(
                (minLon + maxLon) / 2.0,
                (minLat + maxLat) / 2.0);

            if (Math.Abs(mapData.Rotation) > 0.001)
            {
                CalculateRotatedBounds(mapData, minLon, maxLon, minLat, maxLat);
            }
            else
            {
                double lonRange = maxLon - minLon;
                double latRange = maxLat - minLat;
                mapData.MapBounds = Math.Max(lonRange, latRange) * 1;
            }
        }

        private static void CalculateRotatedBounds(AirportMapData mapData, double minLon, double maxLon, double minLat, double maxLat)
        {
            var corners = new[]
            {
                new { Lon = minLon, Lat = minLat },
                new { Lon = maxLon, Lat = minLat },
                new { Lon = maxLon, Lat = maxLat },
                new { Lon = minLon, Lat = maxLat }
            };

            double rotationRadians = mapData.Rotation * Math.PI / 180.0;
            double cosRot = Math.Cos(rotationRadians);
            double sinRot = Math.Sin(rotationRadians);

            double minRotatedX = double.MaxValue;
            double maxRotatedX = double.MinValue;
            double minRotatedY = double.MaxValue;
            double maxRotatedY = double.MinValue;

            foreach (var corner in corners)
            {
                double deltaX = corner.Lon - mapData.CenterPoint.Longitude;
                double deltaY = mapData.CenterPoint.Latitude - corner.Lat;

                double rotatedX = deltaX * cosRot - deltaY * sinRot;
                double rotatedY = deltaX * sinRot + deltaY * cosRot;

                minRotatedX = Math.Min(minRotatedX, rotatedX);
                maxRotatedX = Math.Max(maxRotatedX, rotatedX);
                minRotatedY = Math.Min(minRotatedY, rotatedY);
                maxRotatedY = Math.Max(maxRotatedY, rotatedY);
            }
            double rotatedXRange = maxRotatedX - minRotatedX;
            double rotatedYRange = maxRotatedY - minRotatedY;

            mapData.MapBounds = Math.Max(rotatedXRange, rotatedYRange) * 1;
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
                        double lon = double.Parse(pointNode.Attributes["lon"].Value);
                        double lat = double.Parse(pointNode.Attributes["lat"].Value);
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
                    if (!string.IsNullOrEmpty(rotationAttr) && double.TryParse(rotationAttr, out double rotation))
                    {
                        mapData.Rotation = rotation;
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
                        double lon = double.Parse(positionNode.Attributes["lon"].Value);
                        double lat = double.Parse(positionNode.Attributes["lat"].Value);
                        double heading = double.Parse(headingNode.InnerText);

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
                    double lon = double.Parse(pointNode.Attributes["lon"].Value);
                    double lat = double.Parse(pointNode.Attributes["lat"].Value);
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
                    double lon = double.Parse(windsockNode.Attributes["lon"].Value);
                    double lat = double.Parse(windsockNode.Attributes["lat"].Value);

                    var windsock = new Windsock(new GeoPoint(lon, lat));
                    mapData.Windsocks.Add(windsock);
                }
            }
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