using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace BARS.Windows
{
    public partial class Config : BaseForm
    {
        private const int AIRPORT_ENTRY_HEIGHT = 30;
        private const int AIRPORT_ENTRY_SPACING = 5;
        private const int MAX_AIRPORTS = 5;

        public Config()
        {
            InitializeComponent();
            this.MiddleClickClose = false;

            // Prevent resizing the config window
            this.Resizeable = false;
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MinimumSize = this.Size;
            this.MaximumSize = this.Size;

            StyleComponent();
            SyncAirportList();

            this.FormClosing += Config_FormClosing;
        }

        public void SyncAirportList()
        {
            pnl_airports.Controls.Clear();

            int index = 0;
            foreach (string airport in BARS.ControlledAirports.Take(MAX_AIRPORTS))
            {
                CreateAirportEntry(airport, index);
                index++;
            }
        }

        private async Task AddAirport(string icao)
        {
            try
            {
                btn_add.Enabled = false;
                btn_add.Size = new Size(82, 24);
                btn_add.Text = "ADDING...";
                await BARS.AddAirport(icao);
                txt_icao.Clear();
                txt_icao.Focus();
                btn_add.Enabled = true;
                btn_add.Size = new Size(48, 24);
                btn_add.Text = "ADD";
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "BARS Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void apiKeyInput_TextChanged(object sender, EventArgs e)
        {
            BARS.UpdateApiKey(txt_key.Text.Trim());
        }

        private void btn_add_Click(object sender, EventArgs e)
        {
            string icao = txt_icao.Text.Trim().ToUpper();
            if (!string.IsNullOrWhiteSpace(icao))
            {
                _ = AddAirport(icao);
            }
        }

        private void Config_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            foreach (var profileWindow in BARS.ProfileWindows)
            {
                if (!profileWindow.IsDisposed)
                {
                    profileWindow.Hide();
                }
            }
        }

        private void CreateAirportEntry(string icao, int index)
        {
            var label = new TextLabel
            {
                Text = icao,
                AutoSize = true,
                Location = new Point(5, index * (AIRPORT_ENTRY_HEIGHT + AIRPORT_ENTRY_SPACING) + 5),
                Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
            };
            bool isLegacy = icao == "YSSY" || icao == "YSCB";

            var btnProfiles = new GenericButton
            {
                Text = isLegacy ? "PROFILES" : "OPEN",
                Size = new Size(75, 24),
                Location = new Point(pnl_airports.Width - 165, label.Location.Y),
                Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                FlatStyle = FlatStyle.Flat,
                BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
                ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
                Tag = icao
            };

            btnProfiles.Click += (s, e) =>
            {
                if (s is GenericButton btn)
                {
                    string airportIcao = btn.Tag.ToString();
                    bool isLegacyAirport = airportIcao == "YSSY" || airportIcao == "YSCB";

                    if (isLegacyAirport)
                    {
                        BARS.ShowProfilesWindow(airportIcao);
                    }
                    else
                    {
                        BARS.OpenINTASAirport(airportIcao);
                    }
                }
            };

            var btnRemove = new GenericButton
            {
                Text = "REMOVE",
                Size = new Size(60, 24),
                Location = new Point(pnl_airports.Width - 87, label.Location.Y),
                Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                FlatStyle = FlatStyle.Flat,
                BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
                ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
                Tag = icao
            };

            btnRemove.Click += (s, e) =>
            {
                if (s is GenericButton btn)
                {
                    _ = BARS.RemoveAirport(btn.Tag.ToString());

                    var profileWindow = BARS.ProfileWindows.FirstOrDefault(pw => pw.AirportIcao == btn.Tag.ToString());
                    if (profileWindow != null && !profileWindow.IsDisposed)
                    {
                        profileWindow.Close();
                        profileWindow.Dispose();
                    }
                }
            };

            pnl_airports.Controls.Add(label);
            pnl_airports.Controls.Add(btnProfiles);
            pnl_airports.Controls.Add(btnRemove);
        }

        private void StyleComponent()
        {
            this.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            pnl_airports.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

            lbl_icao.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            lbl_icao.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            txt_icao.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            txt_icao.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            btn_add.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            btn_add.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            btn_add.FlatStyle = FlatStyle.Flat;
            txt_key.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            txt_key.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            lbl_key.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            lbl_key.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);

            txt_key.Text = Properties.Settings.Default.APIKey;
        }

        private void txt_icao_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Enter)
            {
                btn_add_Click(sender, e);
                return;
            }

            // Disallow spaces and any non-letter characters. Allow control keys (Backspace, etc.).
            if (!char.IsControl(e.KeyChar))
            {
                // Only allow A-Z letters
                if (!char.IsLetter(e.KeyChar))
                {
                    e.Handled = true;
                    return;
                }

                var tb = sender as TextBox;
                if (tb != null)
                {
                    bool replacingSelection = tb.SelectionLength > 0;
                    if (!replacingSelection && tb.TextLength >= 4)
                    {
                        e.Handled = true;
                        return;
                    }
                }

                e.KeyChar = char.ToUpperInvariant(e.KeyChar);
            }
        }
    }
}