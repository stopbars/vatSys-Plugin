using BARS.Util;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using vatsys;

namespace BARS.Windows
{
    public partial class Controller_Legacy : BaseForm
    {
        private static readonly Logger logger = new Logger("LeagcyController");
        private List<Stopbar> AirportStopBars = new List<Stopbar>();
        private List<Font> createdFonts = new List<Font>();
        private Dictionary<float, Dictionary<FontStyle, Font>> fontCache = new Dictionary<float, Dictionary<FontStyle, Font>>();
        private bool isAdjustingFormSize = false;
        private bool isResizing = false;
        private Size lastSize;
        private float originalAspectRatio;
        private Dictionary<Control, Rectangle> originalControlDimensions = new Dictionary<Control, Rectangle>();
        private Dictionary<Control, Font> originalFonts = new Dictionary<Control, Font>();
        private Size originalFormSize;
        private Dictionary<Control, bool> originalVisibility = new Dictionary<Control, bool>();
        private Timer resizeTimer = new Timer();

        public Controller_Legacy(string Airport, string Profile)
        {
            InitializeComponent();
            this.Airport = Airport;
            this.ActiveProfile = Profile;
            this.Text = $"BARS - {Airport} - {Profile}";
            _ = InitializeStyle();
            _ = SetupResizeHandling();
            _ = LoadProfile();
        }


        public string ActiveProfile { get; private set; }

        public string Airport { get; set; }

        public void SetStopbarState(string barsId, bool state, bool autoRaise = true)
        {
            ControllerHandler.SetStopbarState(this.Airport, barsId, state, WindowType.Legacy, autoRaise);
        }

        public void ToggleStopbar(string barsId, bool autoRaise = true)
        {
            ControllerHandler.ToggleStopbar(this.Airport, barsId, WindowType.Legacy, autoRaise);
        }

        private void ApplyResize()
        {
            if (this.ClientSize == lastSize)
                return;

            lastSize = this.ClientSize;

            if (pnl_legacy != null)
            {
                pnl_legacy.Location = new Point(0, 0);
                pnl_legacy.Size = this.ClientSize;
            }

            float ratio = Math.Min(
                (float)this.ClientSize.Width / originalFormSize.Width,
                (float)this.ClientSize.Height / originalFormSize.Height
            );

            ResizeAllControls(ratio);
        }

        private void ConfigureRunways(XmlDocument doc)
        {

            XmlNode hRunway = doc.SelectSingleNode("//RunwayConfig/HorizontalRunway");
            if (hRunway != null && bool.Parse(hRunway.SelectSingleNode("Visible").InnerText))
            {
                pnl_1_runway.Visible = true;
                lbl_L.Text = hRunway.SelectSingleNode("LeftEnd/Name").InnerText;
                lbl_R.Text = hRunway.SelectSingleNode("RightEnd/Name").InnerText;
            }
            else
            {
                pnl_1_runway.Visible = false;
            }


            XmlNode vRunway = doc.SelectSingleNode("//RunwayConfig/VerticalRunway");
            if (vRunway != null && bool.Parse(vRunway.SelectSingleNode("Visible").InnerText))
            {
                pnl_2_runway.Visible = true;
                lbl_T.Text = vRunway.SelectSingleNode("TopEnd/Name").InnerText;
                lbl_B.Text = vRunway.SelectSingleNode("BottomEnd/Name").InnerText;
            }
            else
            {
                pnl_2_runway.Visible = false;
            }
        }

        private void ConfigureStopbars(XmlDocument doc)
        {
            XmlNodeList stopbars = doc.SelectNodes("//Stopbars/Stopbar");
            foreach (XmlNode stopbar in stopbars)
            {
                string id = stopbar.SelectSingleNode("ID").InnerText;
                string displayName = stopbar.SelectSingleNode("DisplayName").InnerText;
                string barsId = stopbar.SelectSingleNode("BARSId").InnerText;

                Label label = Controls.Find($"lbl_{id}", true).FirstOrDefault() as Label;
                if (label != null)
                {
                    label.Text = displayName;
                    label.Name = $"lbl_{barsId}";
                    label.Visible = true;
                }

                Panel taxi = Controls.Find($"pnl_{id}_taxi", true).FirstOrDefault() as Panel;
                if (taxi != null)
                {
                    taxi.Name = $"pnl_{barsId}_taxi";
                    taxi.Visible = true;
                }

                Panel tri = Controls.Find($"pnl_{id}_tri", true).FirstOrDefault() as Panel;
                if (tri != null)
                {
                    tri.Name = $"pnl_{barsId}_tri";
                    tri.Visible = true;
                }
            }


            XmlNodeList crossbars = doc.SelectNodes("//CrossbarsConfig/Crossbar");
            foreach (XmlNode crossbar in crossbars)
            {
                string id = crossbar.SelectSingleNode("ID").InnerText;
                string displayName = crossbar.SelectSingleNode("DisplayName").InnerText;
                string barsId = crossbar.SelectSingleNode("BARSId").InnerText;

                Label label = Controls.Find($"lbl_{id}", true).FirstOrDefault() as Label;
                if (label != null)
                {
                    label.Text = displayName;
                    label.Name = $"lbl_{barsId}";
                    label.Visible = true;
                }

                Panel tri = Controls.Find($"pnl_{id}_tri", true).FirstOrDefault() as Panel;
                if (tri != null)
                {
                    tri.Name = $"pnl_{barsId}_tri";
                    tri.BackColor = Color.FromArgb(255, 91, 91, 91);
                    tri.Visible = true;
                }
            }
        }

        private void Controller_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {

                ControllerHandler.StopbarStateChanged -= StopbarStateChanged;

                resizeTimer.Dispose();
            }
        }

        private void Controller_Load(object sender, EventArgs e)
        {
            RemoveAllAnchors(this);

            originalFormSize = this.ClientSize;
            originalAspectRatio = (float)originalFormSize.Width / originalFormSize.Height;
            lastSize = this.ClientSize;

            if (pnl_legacy != null)
            {
                pnl_legacy.Location = new Point(0, 0);
                pnl_legacy.Size = this.ClientSize;
            }

            StoreOriginalControlInfo(this);

            foreach (Control control in GetAllControls(this))
            {
                if (control is Panel panel)
                {
                    SetDoubleBuffered(panel);
                }


                if (control is Label label && control.Cursor == Cursors.Hand && control.Visible)
                {
                    label.MouseUp += Stopbar_Click;
                }
            }

            foreach (Stopbar stopbar in ControllerHandler.GetStopbarsForAirport(Airport))
            {
                UpdateStopbarUI(stopbar);
            }
        }

        private void Controller_Resize(object sender, EventArgs e)
        {
            if (originalFormSize.Width == 0 || originalFormSize.Height == 0)
                return;

            if (isResizing && pnl_legacy != null)
            {
                pnl_legacy.Location = new Point(0, 0);
                pnl_legacy.Size = this.ClientSize;

                resizeTimer.Stop();
                resizeTimer.Start();
            }
            else if (!isResizing && !isAdjustingFormSize)
            {
                if (this.ClientSize != lastSize)
                {
                    ApplyResize();
                }
            }
        }

        private void Controller_ResizeBegin(object sender, EventArgs e)
        {
            isResizing = true;
            resizeTimer.Stop();

            foreach (Control control in GetAllControls(this))
            {
                if (control != pnl_legacy && control.Parent != pnl_legacy)
                {
                    control.Visible = false;
                }
            }

            if (pnl_legacy != null)
            {
                pnl_legacy.Visible = true;
                pnl_legacy.Location = new Point(0, 0);
                pnl_legacy.Size = this.ClientSize;
            }
        }

        private void Controller_ResizeEnd(object sender, EventArgs e)
        {
            isResizing = false;
            resizeTimer.Stop();
            EnforceAspectRatio();
            ApplyResize();

            foreach (Control control in originalVisibility.Keys)
            {
                if (!control.IsDisposed)
                {
                    control.Visible = originalVisibility[control];
                }
            }
        }

        private void Controller_SizeChanged(object sender, EventArgs e)
        {
            if (!isAdjustingFormSize)
            {
                EnforceAspectRatio();
            }
        }

        private void EnforceAspectRatio()
        {
            if (isAdjustingFormSize || originalFormSize.Width == 0 || originalFormSize.Height == 0)
                return;

            if (isResizing)
                return;

            try
            {
                isAdjustingFormSize = true;

                float currentAspectRatio = (float)this.ClientSize.Width / this.ClientSize.Height;

                if (Math.Abs(currentAspectRatio - originalAspectRatio) > 0.01f)
                {
                    Size newClientSize;
                    bool isScalingUp = this.ClientSize.Width > lastSize.Width || this.ClientSize.Height > lastSize.Height;

                    if (isScalingUp)
                    {
                        float widthBasedOnHeight = this.ClientSize.Height * originalAspectRatio;
                        float heightBasedOnWidth = this.ClientSize.Width / originalAspectRatio;

                        if (widthBasedOnHeight > this.ClientSize.Width)
                        {
                            newClientSize = new Size(
                                (int)widthBasedOnHeight,
                                this.ClientSize.Height
                            );
                        }
                        else
                        {
                            newClientSize = new Size(
                                this.ClientSize.Width,
                                (int)heightBasedOnWidth
                            );
                        }
                    }
                    else
                    {
                        if (currentAspectRatio > originalAspectRatio)
                        {
                            newClientSize = new Size(
                                (int)(this.ClientSize.Height * originalAspectRatio),
                                this.ClientSize.Height
                            );
                        }
                        else
                        {
                            newClientSize = new Size(
                                this.ClientSize.Width,
                                (int)(this.ClientSize.Width / originalAspectRatio)
                            );
                        }
                    }

                    Size difference = new Size(
                        this.Size.Width - this.ClientSize.Width,
                        this.Size.Height - this.ClientSize.Height
                    );

                    this.Size = new Size(
                        newClientSize.Width + difference.Width,
                        newClientSize.Height + difference.Height
                    );
                }
            }
            finally
            {
                isAdjustingFormSize = false;
            }
        }

        private IEnumerable<Control> GetAllControls(Control container)
        {
            List<Control> controlList = new List<Control>();
            foreach (Control c in container.Controls)
            {
                controlList.Add(c);
                controlList.AddRange(GetAllControls(c));
            }
            return controlList;
        }

        private async Task InitializeStyle()
        {
            foreach (Control control in GetAllControls(this))
            {
                if (control != null && control.Name.EndsWith("_taxi"))
                {
                    control.BackColor = Color.FromArgb(255, 144, 144, 144);
                }

                if (control != null && control.Name.EndsWith("_runway"))
                {
                    control.BackColor = Color.FromArgb(255, 91, 91, 91);
                }

                if (control != null && control.Name.EndsWith("_tri"))
                {
                    control.BackColor = Color.FromArgb(255, 67, 67, 67);
                }

                if (control != null && control.Name.StartsWith("lbl_s") || control.Name.EndsWith("_S"))
                {
                    control.ForeColor = Color.FromArgb(255, 0, 0, 0);
                    control.BackColor = Color.FromArgb(255, 255, 255, 255);
                }

                if (control != null && control.BackColor == Color.PeachPuff)
                {
                    control.BackColor = Color.FromArgb(255, 91, 91, 91);
                }
            }

            await Task.Yield();
        }

        private async Task LoadProfile()
        {
            try
            {
                string profilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BARS", "vatSys", $"{Airport}_{ActiveProfile.Replace("/", "-")}.xml");

                if (!File.Exists(profilePath))
                {
                    MessageBox.Show($"Profile file not found: {profilePath}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                XmlDocument doc = new XmlDocument();
                doc.Load(profilePath);


                ConfigureRunways(doc);


                ConfigureStopbars(doc);


                RegisterStopbars(doc);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading profile: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            await Task.Yield();
        }

        private void RegisterStopbars(XmlDocument doc)
        {
            XmlNodeList stopbars = doc.SelectNodes("//Stopbars/Stopbar");
            foreach (XmlNode stopbar in stopbars)
            {
                string barsId = stopbar.SelectSingleNode("BARSId").InnerText;
                string displayName = stopbar.SelectSingleNode("DisplayName").InnerText;
                ControllerHandler.RegisterStopbar(Airport, displayName, barsId, true);
            }

            XmlNodeList crossbars = doc.SelectNodes("//CrossbarsConfig/Crossbar");
            foreach (XmlNode crossbar in crossbars)
            {
                string barsId = crossbar.SelectSingleNode("BARSId").InnerText;
                string displayName = crossbar.SelectSingleNode("DisplayName").InnerText;
                ControllerHandler.RegisterStopbar(Airport, displayName, barsId, true);
            }
        }

        private void RemoveAllAnchors(Control container)
        {
            foreach (Control control in container.Controls)
            {
                control.Anchor = AnchorStyles.None;

                if (control.Controls.Count > 0)
                {
                    RemoveAllAnchors(control);
                }
            }
        }

        private void ResizeAllControls(float ratio)
        {
            this.SuspendLayout();

            Dictionary<float, Dictionary<FontStyle, Font>> fontCache = new Dictionary<float, Dictionary<FontStyle, Font>>();

            foreach (var kvp in originalControlDimensions)
            {
                Control control = kvp.Key;

                if (control.IsDisposed)
                    continue;

                if (control == pnl_legacy)
                {
                    control.Location = new Point(0, 0);
                    control.Size = this.ClientSize;
                    continue;
                }

                Rectangle original = kvp.Value;

                int newX = (int)(original.X * ratio);
                int newY = (int)(original.Y * ratio);
                int newWidth = Math.Max(1, (int)(original.Width * ratio));
                int newHeight = Math.Max(1, (int)(original.Height * ratio));

                control.Location = new Point(newX, newY);
                control.Size = new Size(newWidth, newHeight);

                if (originalFonts.ContainsKey(control))
                {
                    float fontReductionFactor = 0.80f;
                    float newFontSize = originalFonts[control].Size * ratio * fontReductionFactor;

                    try
                    {
                        Font newFont = null;
                        FontStyle style = originalFonts[control].Style;
                        float cacheKey = newFontSize;

                        if (!fontCache.ContainsKey(cacheKey))
                        {
                            fontCache[cacheKey] = new Dictionary<FontStyle, Font>();
                        }

                        if (!fontCache[cacheKey].ContainsKey(style))
                        {
                            fontCache[cacheKey][style] = new Font(
                                originalFonts[control].FontFamily,
                                newFontSize,
                                style
                            );
                        }

                        newFont = fontCache[cacheKey][style];

                        if (!control.IsDisposed)
                        {
                            control.Font = newFont;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            this.ResumeLayout(false);
            this.PerformLayout();
            this.Refresh();
        }

        private void ResizeTimer_Tick(object sender, EventArgs e)
        {
            resizeTimer.Stop();

            if (!isResizing)
            {
                EnforceAspectRatio();
                ApplyResize();

                foreach (Control control in originalVisibility.Keys)
                {
                    if (!control.IsDisposed)
                    {
                        control.Visible = originalVisibility[control];
                    }
                }
            }
        }

        private void SetDoubleBuffered(Control control)
        {
            var propInfo = typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            propInfo.SetValue(control, true, null);
        }

        private async Task SetupResizeHandling()
        {
            resizeTimer.Interval = 100;
            resizeTimer.Tick += ResizeTimer_Tick;

            this.FormClosing += Controller_FormClosing;
            this.Load += Controller_Load;
            this.ResizeBegin += Controller_ResizeBegin;
            this.ResizeEnd += Controller_ResizeEnd;
            this.Resize += Controller_Resize;
            this.SizeChanged += Controller_SizeChanged;

            this.DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint, true);


            ControllerHandler.StopbarStateChanged += StopbarStateChanged;

            await Task.Yield();
        }

        private void Stopbar_Click(object sender, MouseEventArgs e)
        {
            Label labelClicked = (Label)sender;

            foreach (Stopbar stopbar in ControllerHandler.GetStopbarsForAirport(Airport))
            {

                if (labelClicked.Name == $"lbl_{stopbar.BARSId}")
                {
                    logger.Log($"Clicked stopbar: {stopbar.BARSId}");
                    if (e.Button == MouseButtons.Left)
                    {
                        logger.Log($"Toggling stopbar: {stopbar.BARSId}");
                        ToggleStopbar(stopbar.BARSId, true);
                    }
                    else if (e.Button == MouseButtons.Right)
                    {
                        logger.Log($"Setting stopbar state: {stopbar.BARSId}");
                        ToggleStopbar(stopbar.BARSId, false);
                    }

                    break;
                }
            }
        }

        private void StopbarStateChanged(object sender, StopbarEventArgs e)
        {

            if (e.Stopbar.Airport == this.Airport && e.WindowType == WindowType.Legacy)
            {

                UpdateStopbarUI(e.Stopbar);
            }
        }

        private void StoreOriginalControlInfo(Control container)
        {
            foreach (Control control in container.Controls)
            {
                originalControlDimensions[control] = new Rectangle(control.Location, control.Size);
                originalVisibility[control] = control.Visible;

                if (control.Font != null)
                {
                    originalFonts[control] = control.Font;
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


            foreach (Control control in GetAllControls(this))
            {
                if (control != null)
                {

                    string taxiName = $"pnl_{stopbar.BARSId}_taxi";
                    string triName = $"pnl_{stopbar.BARSId}_tri";

                    if (control.Name == taxiName)
                    {
                        control.BackgroundImage = stopbar.State ? null : Properties.Resources.LeadOn;
                    }
                    else if (control.Name == triName && control.Tag != null)
                    {
                        if (control.Tag.ToString() == "T")
                        {
                            control.BackgroundImage = stopbar.State ? Properties.Resources.tri_T : Properties.Resources.tri_T_off;
                        }
                        else
                        {
                            control.BackgroundImage = stopbar.State ? Properties.Resources.tri_B : Properties.Resources.tri_B_off;
                        }
                    }
                }
            }
        }
    }
}