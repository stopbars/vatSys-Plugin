using BARS.Util;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using vatsys;

namespace BARS.Windows
{
    public partial class Controller_INTAS : BaseForm
    {
        private PointF centerPoint;
        private string currentMetarText;
        private Dictionary<float, Dictionary<FontStyle, Font>> fontCache = new Dictionary<float, Dictionary<FontStyle, Font>>();

        private Dictionary<string, List<MapElement>> groundElements = new Dictionary<string, List<MapElement>>();

        private bool isResizing = false;

        private Size lastSize;
        private Logger logger = new Logger("Controller_INTAS");

        private Size originalFormSize;

        private Dictionary<Control, bool> originalVisibility = new Dictionary<Control, bool>();
        private Timer resizeTimer = new Timer();

        public Controller_INTAS(string Airport, string Profile)
        {
            InitializeComponent();
            this.Airport = Airport;
            this.ActiveProfile = Profile;
            string displayTitle = (Airport == Profile) ? $"BARS - {Airport} - INTAS" : $"BARS - {Airport} - {Profile} - INTAS";
            this.Text = displayTitle;

            this.FormClosing += Controller_FormClosing;
            this.Load += Controller_INTAS_Load;

            _ = SetupResizeHandling();

            ControllerHandler.StopbarStateChanged += StopbarStateChanged;
            MET.Instance.ProductsChanged += METARChanged;
        }

        public string ActiveProfile { get; private set; }

        public string Airport { get; set; }

        public void SetStopbarState(string barsId, bool state, bool autoRaise = true)
        {
            ControllerHandler.SetStopbarState(this.Airport, barsId, state, WindowType.INTAS, autoRaise);
        }

        public void ToggleStopbar(string barsId, bool autoRaise = true)
        {
            ControllerHandler.ToggleStopbar(this.Airport, barsId, WindowType.INTAS, autoRaise);
        }

        private void AirportMapControl_StopbarClicked(object sender, AirportMapControl.StopbarClickEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleStopbar(e.BarsId, true);
                logger.Log($"Stopbar {e.BarsId} left-clicked and toggled with autoRaise");
            }
            else if (e.Button == MouseButtons.Right)
            {
                ToggleStopbar(e.BarsId, false);
                logger.Log($"Stopbar {e.BarsId} right-clicked and toggled without autoRaise");
            }
        }

        private void ApplyResize()
        {
            lastSize = this.ClientSize;

            if (airportMapControl != null)
            {
                int padding = 0;
                airportMapControl.Location = new Point(padding, padding);
                airportMapControl.Size = new Size(
                    this.ClientSize.Width - (padding * 2),
                    this.ClientSize.Height - (padding * 2)
                );
                float ratio = Math.Min(
                    (float)this.ClientSize.Width / originalFormSize.Width,
                    (float)this.ClientSize.Height / originalFormSize.Height
                );

                if (ratio <= 0f || float.IsNaN(ratio) || float.IsInfinity(ratio))
                {
                    ratio = 0.1f;
                }

                airportMapControl.SetScalingRatio(ratio);

                airportMapControl.Invalidate();
                airportMapControl.Update();
            }

            this.PerformLayout();
            this.Refresh();
        }

        private void Controller_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                ControllerHandler.StopbarStateChanged -= StopbarStateChanged;

                MET.Instance.ProductsChanged -= METARChanged;
            }
        }

        private void Controller_INTAS_Load(object sender, EventArgs e)
        {
            try
            {
                originalFormSize = this.ClientSize;
                lastSize = this.ClientSize;

                StoreOriginalControlInfo(this);

                airportMapControl.LoadAirportMap(this.Airport);

                airportMapControl.StopbarClicked += AirportMapControl_StopbarClicked;

                LoadGroundLayout(this.Airport);

                ApplyResize();
                airportMapControl.Invalidate();
                this.Refresh();

                LoadMetar(this.Airport);

                airportMapControl.SetWind(0, 0);

                var delayTimer = new Timer { Interval = 50, Enabled = true };
                delayTimer.Tick += (s, args) =>
                {
                    delayTimer.Stop();
                    delayTimer.Dispose();
                    ApplyResize();
                };
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load airport map for {this.Airport}: {ex.Message}",
                    "Map Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Controller_Resize(object sender, EventArgs e)
        {
            if (originalFormSize.Width == 0 || originalFormSize.Height == 0)
                return;

            if (isResizing && airportMapControl != null)
            {
                int padding = 0;
                airportMapControl.Location = new Point(padding, padding);
                airportMapControl.Size = new Size(
                    this.ClientSize.Width - (padding * 2),
                    this.ClientSize.Height - (padding * 2)
                );
                airportMapControl.Invalidate();

                resizeTimer.Stop();
                resizeTimer.Start();
            }
            else if (!isResizing)
            {
                ApplyResize();
            }
        }

        private void Controller_ResizeBegin(object sender, EventArgs e)
        {
            isResizing = true;
            resizeTimer.Stop();

            foreach (Control control in GetAllControls(this))
            {
                if (!originalVisibility.ContainsKey(control))
                {
                    originalVisibility[control] = control.Visible;
                }
            }

            foreach (Control control in GetAllControls(this))
            {
                if (control != airportMapControl)
                {
                    control.Visible = false;
                }
            }

            if (airportMapControl != null)
            {
                airportMapControl.Visible = true;

                int padding = 0;
                airportMapControl.Location = new Point(padding, padding);
                airportMapControl.Size = new Size(
                    this.ClientSize.Width - (padding * 2),
                    this.ClientSize.Height - (padding * 2)
                );
            }
        }

        private void Controller_ResizeEnd(object sender, EventArgs e)
        {
            isResizing = false;
            resizeTimer.Stop();
            ApplyResize();

            foreach (Control control in originalVisibility.Keys)
            {
                if (!control.IsDisposed)
                {
                    control.Visible = originalVisibility[control];
                }
            }

            this.PerformLayout();
            this.Refresh();
        }

        private void Controller_SizeChanged(object sender, EventArgs e)
        {
            if (!isResizing)
            {
                ApplyResize();
            }
        }

        private IEnumerable<Control> GetAllControls(Control container)
        {
            var controls = new List<Control>();

            foreach (Control control in container.Controls)
            {
                controls.Add(control);
                controls.AddRange(GetAllControls(control));
            }

            return controls;
        }

        private void LoadGroundLayout(string icao)
        {
            try
            {
                string profilesPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "vatSys Files",
                    "Profiles"
                );
                string asmgcsFile = $"ASMGCS_{icao}.xml";
                string filePath = Directory.GetFiles(profilesPath, asmgcsFile, SearchOption.AllDirectories).FirstOrDefault();
                if (filePath == null)
                {
                    logger.Log($"ASMGCS file not found for {icao}");
                    return;
                }
                var doc = XDocument.Load(filePath);
                groundElements.Clear();

                double centerLat = 0, centerLon = 0;
                var firstMap = doc.Root.Elements("Map").FirstOrDefault();
                if (firstMap != null)
                {
                    string centerAttr = firstMap.Attribute("Center")?.Value;
                    if (!string.IsNullOrEmpty(centerAttr))
                    {
                        var centerMatch = Regex.Match(centerAttr, @"^([+\-]?\d+\.\d+)([+\-]\d+\.\d+)$");
                        if (centerMatch.Success)
                        {
                            centerLat = double.Parse(centerMatch.Groups[1].Value);
                            centerLon = double.Parse(centerMatch.Groups[2].Value);
                            logger.Log($"Parsed center point: Lat={centerLat}, Lon={centerLon}");
                        }
                    }
                }

                foreach (var map in doc.Root.Elements("Map"))
                {
                    string mapType = map.Attribute("Type")?.Value ?? "";
                    var elements = new List<MapElement>();

                    foreach (var infill in map.Elements("Infill"))
                    {
                        var points = ParseInfillPoints(infill, centerLat, centerLon);
                        if (points.Count >= 3)
                        {
                            elements.Add(new MapElement
                            {
                                Name = infill.Attribute("Name")?.Value ?? "",
                                Points = points
                            });
                        }
                    }

                    if (elements.Any())
                    {
                        groundElements[mapType] = elements;
                    }
                }

                if (centerLat != 0 && centerLon != 0)
                {
                    centerPoint = new PointF((float)centerLon, (float)centerLat);
                }
                airportMapControl.LoadGroundLayout(groundElements);
            }
            catch (Exception ex)
            {
                logger.Log($"Error loading layout: {ex}");
            }
        }

        private void LoadMetar(string icao)
        {
            try
            {
                MET.Instance.RequestProduct(new MET.ProductRequest(MET.ProductType.VATSIM_METAR, icao, true));
                logger.Log($"Requested METAR for {icao} from VATSIM.");
            }
            catch (Exception ex)
            {
                logger.Log($"Error loading METAR for {icao}: {ex.Message}");
                MessageBox.Show($"Failed to load METAR for {icao}: {ex.Message}",
                    "METAR Loading Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void METARChanged(object sender, MET.ProductsChangedEventArgs e)
        {
            var products = MET.Instance.GetProducts(e.ProductRequest);
            if (products == null || products.Count == 0)
            {
                logger.Log($"No METAR products returned for {e.ProductRequest.Icao}.");
                return;
            }

            var metar = products[0];
            if (metar.Type != MET.ProductType.VATSIM_METAR || metar.Text == "No product available.")
            {
                logger.Log($"No valid METAR available for {e.ProductRequest.Icao}.");
                return;
            }

            if (e.ProductRequest.Icao != this.Airport)
            {
                logger.Log($"Received METAR for {e.ProductRequest.Icao}, but this controller is for {this.Airport}. Ignoring.");
                return;
            }
            currentMetarText = metar.Text;

            airportMapControl.UpdateWindFromMetar(metar.Text);

            logger.Log($"Received METAR for {e.ProductRequest.Icao}: {metar.Text}");
        }

        private List<Coordinate> ParseInfillPoints(XElement infill, double centerLat, double centerLon)
        {
            var points = new List<Coordinate>();
            try
            {
                var pointData = infill.Element("Point")?.Value.Trim();
                if (string.IsNullOrEmpty(pointData)) return points;

                foreach (string line in pointData.Split(new[] { '/', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var str = line.ToUpperInvariant().Trim();
                    if (Regex.IsMatch(str, "^(\\+|\\-)\\d+.\\d+(\\+|\\-)\\d+.\\d+$"))
                    {
                        var coordinate = new Coordinate();
                        coordinate.ParseIsoString(str);
                        points.Add(coordinate);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log($"Error parsing infill points: {ex}");
            }
            return points;
        }

        private void ResizeTimer_Tick(object sender, EventArgs e)
        {
            resizeTimer.Stop();

            if (!isResizing)
            {
                ApplyResize();

                foreach (Control control in originalVisibility.Keys)
                {
                    if (!control.IsDisposed)
                    {
                        control.Visible = originalVisibility[control];
                    }
                }

                this.PerformLayout();
                this.Refresh();
            }
        }

        private async Task SetupResizeHandling()
        {
            resizeTimer.Interval = 100;
            resizeTimer.Tick += ResizeTimer_Tick;

            this.ResizeBegin += Controller_ResizeBegin;
            this.ResizeEnd += Controller_ResizeEnd;
            this.Resize += Controller_Resize;
            this.SizeChanged += Controller_SizeChanged;

            this.DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);

            await Task.Yield();
        }

        private void StopbarStateChanged(object sender, StopbarEventArgs e)
        {
            if (e.Stopbar.Airport == this.Airport && e.WindowType == WindowType.INTAS)
            {
                UpdateStopbarUI(e.Stopbar);
            }
        }

        private void StoreOriginalControlInfo(Control container)
        {
            foreach (Control control in GetAllControls(container))
            {
                if (!originalVisibility.ContainsKey(control))
                {
                    originalVisibility[control] = control.Visible;
                }

                if (control.Controls.Count > 0)
                {
                    StoreOriginalControlInfo(control);
                }
            }
        }

        private void UpdateStopbarUI(Stopbar stopbar)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<Stopbar>(UpdateStopbarUI), stopbar);
                return;
            }

            airportMapControl.UpdateStopbarState(stopbar.BARSId, stopbar.State);

            airportMapControl.UpdateLeadOnLightsForStopbar(stopbar.BARSId, stopbar.State);

            logger.Log($"Updated stopbar {stopbar.BARSId}, state: {stopbar.State}");
        }
    }

    public class MapElement
    {
        public string Name { get; set; }
        public List<Coordinate> Points { get; set; } = new List<Coordinate>();
    }
}