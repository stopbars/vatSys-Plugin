using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using vatsys;

namespace BARS.Windows
{
    public partial class Profiles : BaseForm
    {
        private const int PROFILE_ENTRY_HEIGHT = 30;

        private const int PROFILE_ENTRY_SPACING = 5;

        private bool isDisposing = false;

        private Dictionary<string, GenericButton> profileButtons = new Dictionary<string, GenericButton>(); public Profiles(string icao)
        {
            InitializeComponent();
            this.AirportIcao = icao;


            bool isLegacy = icao == "YSSY" || icao == "YSCB";

            if (isLegacy)
            {
                this.Text = $"{icao} - Runway Profiles";
                StyleComponents();
                LoadProfiles();


                SyncActiveProfilesStatus();
            }
            else
            {
                this.Text = $"{icao} - INTAS Controller";
                StyleComponents();
                LoadINTASInterface();
            }


            this.FormClosing += Profiles_FormClosing;


            this.Activated += Profiles_Activated;
        }


        public event EventHandler<ProfileSelectedEventArgs> ProfileSelected;


        public string AirportIcao { get; private set; }

        public List<string> SelectedProfiles { get; private set; } = new List<string>();


        public void ResetAllSelections()
        {
            SelectedProfiles.Clear();


            foreach (var button in profileButtons.Values)
            {
                button.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            }
        }


        public void ResetProfileSelection(string profileName)
        {

            if (SelectedProfiles.Contains(profileName))
            {
                SelectedProfiles.Remove(profileName);
            }


            if (profileButtons.ContainsKey(profileName))
            {
                profileButtons[profileName].BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            }
        }


        public void SelectProfile(string profileName)
        {

            if (BARS.IsControllerWindowOpen(AirportIcao, profileName))
            {
                BARS.RemoveControllerWindow(AirportIcao, profileName);
                UpdateSelectedProfileVisual(profileName);
            }
            else
            {
                UpdateSelectedProfileVisual(profileName);
                ProfileSelected?.Invoke(this, new ProfileSelectedEventArgs(AirportIcao, profileName));
            }
        }
        public void SyncActiveProfilesStatus()
        {

            bool isLegacy = AirportIcao == "YSSY" || AirportIcao == "YSCB";

            if (!isLegacy)
            {

                bool isOpen = BARS.IsControllerWindowOpen(AirportIcao, AirportIcao);

                if (profileButtons.ContainsKey("INTAS"))
                {
                    profileButtons["INTAS"].BackColor = isOpen ?
                        Color.FromArgb(0, 128, 0) :
                        Colours.GetColour(Colours.Identities.WindowBackground);
                }
                return;
            }



            ResetAllSelections();

            var openProfiles = BARS.GetOpenProfiles(AirportIcao);
            if (openProfiles.Any())
            {
                foreach (var profile in openProfiles)
                {

                    if (!SelectedProfiles.Contains(profile))
                    {
                        SelectedProfiles.Add(profile);
                    }

                    if (profileButtons.ContainsKey(profile))
                    {
                        profileButtons[profile].BackColor = Color.FromArgb(0, 128, 0);
                    }
                }
            }

            this.Invalidate();
        }

        public void UpdateSelectedProfileVisual(string profileName)
        {

            if (!SelectedProfiles.Contains(profileName))
            {
                SelectedProfiles.Add(profileName);
            }


            if (profileButtons.ContainsKey(profileName))
            {
                profileButtons[profileName].BackColor = Color.FromArgb(0, 128, 0);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                BARS.ControllerWindowClosed -= BARS_ControllerWindowClosed;
                this.Activated -= Profiles_Activated;
                components?.Dispose();
            }
            base.Dispose(disposing);
        }
        
        private void BARS_ControllerWindowClosed(object sender, ControllerWindowEventArgs e)
        {

            if (e.Airport == AirportIcao)
            {
                MMI.InvokeOnGUI(() =>
                {

                    bool isLegacy = AirportIcao == "YSSY" || AirportIcao == "YSCB";

                    if (isLegacy)
                    {


                        if (e.Profile != null && SelectedProfiles.Contains(e.Profile))
                        {
                            SelectedProfiles.Remove(e.Profile);
                        }


                        SyncActiveProfilesStatus();
                    }
                    else
                    {

                        SyncActiveProfilesStatus();
                    }
                });
            }
        }

        private void CreateProfileEntry(string profileName, int index)
        {

            var profileButton = new GenericButton
            {
                Text = profileName,
                Size = new Size(pnl_profiles.Width - 20, PROFILE_ENTRY_HEIGHT),
                Location = new Point(10, index * (PROFILE_ENTRY_HEIGHT + PROFILE_ENTRY_SPACING) + 5),
                Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                FlatStyle = FlatStyle.Flat,
                BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
                ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
                Tag = profileName
            };

            profileButton.Click += (s, e) =>
            {
                if (s is GenericButton btn)
                {
                    SelectProfile(btn.Tag.ToString());
                }
            };

            profileButtons[profileName] = profileButton;
            pnl_profiles.Controls.Add(profileButton);
        }

        private void LoadProfiles()
        {

            pnl_profiles.Controls.Clear();
            profileButtons.Clear();


            string barsProfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BARS",
                "vatSys");


            if (!Directory.Exists(barsProfilePath))
            {
                Directory.CreateDirectory(barsProfilePath);
                return;
            }


            var profileFiles = Directory.GetFiles(barsProfilePath, $"{AirportIcao}_*.xml");
            var profiles = new List<string>();

            foreach (var file in profileFiles)
            {

                string fileName = Path.GetFileNameWithoutExtension(file);
                string profileName = fileName.Substring(AirportIcao.Length + 1).Replace("-", "/");
                profiles.Add(profileName);
            }


            profiles.Sort();


            for (int i = 0; i < profiles.Count; i++)
            {
                CreateProfileEntry(profiles[i], i);
            }

            BARS.ControllerWindowClosed += BARS_ControllerWindowClosed;
        }

        private void LoadINTASInterface()
        {

            pnl_profiles.Controls.Clear();
            profileButtons.Clear();


            string barsProfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BARS",
                "vatSys");


            string profileFilePath = Path.Combine(barsProfilePath, $"{AirportIcao}.xml");

            if (File.Exists(profileFilePath))
            {
                var openButton = new GenericButton
                {
                    Text = "OPEN INTAS CONTROLLER",
                    Size = new Size(pnl_profiles.Width - 20, 40),
                    Location = new Point(10, 10),
                    Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
                    ForeColor = Colours.GetColour(Colours.Identities.InteractiveText)
                };

                openButton.Click += (s, e) =>
                {
                    BARS.OpenINTASAirport(AirportIcao);
                    this.Hide();
                };

                pnl_profiles.Controls.Add(openButton);
                profileButtons["INTAS"] = openButton;
            }
            else
            {

                var noProfileLabel = new TextLabel
                {
                    Text = $"No profile found for {AirportIcao}\nExpected file: {AirportIcao}.xml",
                    Size = new Size(pnl_profiles.Width - 20, 60),
                    Location = new Point(10, 10),
                    Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                    ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
                    BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
                    TextAlign = ContentAlignment.MiddleCenter
                };

                pnl_profiles.Controls.Add(noProfileLabel);
            }
        }

        private void Profiles_Activated(object sender, EventArgs e)
        {
            SyncActiveProfilesStatus();
        }

        private void Profiles_FormClosing(object sender, FormClosingEventArgs e)
        {

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }

            else if (e.CloseReason == CloseReason.FormOwnerClosing || e.CloseReason == CloseReason.WindowsShutDown)
            {
                BARS.ControllerWindowClosed -= BARS_ControllerWindowClosed;
                this.Activated -= Profiles_Activated;
            }
        }

        private void StyleComponents()
        {
            this.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            pnl_profiles.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            lbl_header.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            lbl_header.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
        }
    }

    public class ProfileSelectedEventArgs : EventArgs
    {
        public ProfileSelectedEventArgs(string airport, string profileName)
        {
            Airport = airport;
            ProfileName = profileName;
        }

        public string Airport { get; private set; }
        public string ProfileName { get; private set; }
    }
}