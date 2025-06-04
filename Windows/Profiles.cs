using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using vatsys;

namespace BARS.Windows
{
    public partial class Profiles : BaseForm
    {
        // Make airportIcao public so it can be accessed by BARS
        public string AirportIcao { get; private set; }
        public List<string> SelectedProfiles { get; private set; } = new List<string>();
        private Dictionary<string, GenericButton> profileButtons = new Dictionary<string, GenericButton>();
        
        // Flag to track if this form is closing because it's being disposed or hidden
        private bool isDisposing = false;
        
        private const int PROFILE_ENTRY_HEIGHT = 30;
        private const int PROFILE_ENTRY_SPACING = 5;
        
        // Event that fires when a profile is selected
        public event EventHandler<ProfileSelectedEventArgs> ProfileSelected;
        
        public Profiles(string icao)
        {
            InitializeComponent();
            this.AirportIcao = icao;
            this.Text = $"{icao} - Runway Profiles";
            StyleComponents();
            LoadProfiles();
            
            // Check for all active profiles for this airport and update visuals
            SyncActiveProfilesStatus();
            
            // Subscribe to form closing event
            this.FormClosing += Profiles_FormClosing;
            
            // Subscribe to window activation to sync status on focus
            this.Activated += Profiles_Activated;
        }
        
        private void Profiles_Activated(object sender, EventArgs e)
        {
            SyncActiveProfilesStatus();
        }
        
        public void SyncActiveProfilesStatus()
        {
            // Reset all selections
            ResetAllSelections();
            
            // Get all active profiles for this airport
            var openProfiles = BARS.GetOpenProfiles(AirportIcao);
            if (openProfiles.Any())
            {
                foreach (var profile in openProfiles)
                {
                    // Add to our selected profiles list
                    if (!SelectedProfiles.Contains(profile))
                    {
                        SelectedProfiles.Add(profile);
                    }
                    
                    // Update visuals for this profile
                    if (profileButtons.ContainsKey(profile))
                    {
                        profileButtons[profile].BackColor = Color.FromArgb(0, 128, 0); // Green
                    }
                }
            }

            this.Invalidate();
        }
        
        private void Profiles_FormClosing(object sender, FormClosingEventArgs e)
        {
            // If user clicked X or Alt+F4, just hide the window instead of closing
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            // Only unsubscribe from events if the form is truly being disposed
            else if (e.CloseReason == CloseReason.FormOwnerClosing || e.CloseReason == CloseReason.WindowsShutDown)
            {
                BARS.ControllerWindowClosed -= BARS_ControllerWindowClosed;
                this.Activated -= Profiles_Activated;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                isDisposing = true;
                BARS.ControllerWindowClosed -= BARS_ControllerWindowClosed;
                this.Activated -= Profiles_Activated;
            }
            base.Dispose(disposing);
        }
        
        private void StyleComponents()
        {
            this.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            pnl_profiles.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            lbl_header.ForeColor = Colours.GetColour(Colours.Identities.InteractiveText);
            lbl_header.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
        }
        
        private void LoadProfiles()
        {
            // Clear existing profiles
            pnl_profiles.Controls.Clear();
            profileButtons.Clear();

            // Get the BARS profile directory in %localappdata%
            string barsProfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BARS",
                "vatSys");

            // Create directory if it doesn't exist
            if (!Directory.Exists(barsProfilePath))
            {
                Directory.CreateDirectory(barsProfilePath);
                return; // No profiles to load yet
            }

            // Get all XML files that match our airport code
            var profileFiles = Directory.GetFiles(barsProfilePath, $"{AirportIcao}_*.xml");
            var profiles = new List<string>();

            foreach (var file in profileFiles)
            {
                // Extract profile name from filename (remove ICAO_ prefix and .xml extension)
                string fileName = Path.GetFileNameWithoutExtension(file);
                string profileName = fileName.Substring(AirportIcao.Length + 1).Replace("-", "/");
                profiles.Add(profileName);
            }

            // Sort profiles alphabetically
            profiles.Sort();

            // Add each profile to the panel
            for (int i = 0; i < profiles.Count; i++)
            {
                CreateProfileEntry(profiles[i], i);
            }
            
            // Subscribe to controller window events
            BARS.ControllerWindowClosed += BARS_ControllerWindowClosed;
        }
        
        private void BARS_ControllerWindowClosed(object sender, ControllerWindowEventArgs e)
        {
            // If the closed controller window matches our airport, update selection
            if (e.Airport == AirportIcao)
            {
                MMI.InvokeOnGUI(() => 
                {
                    // Remove from selected profiles
                    if (e.Profile != null && SelectedProfiles.Contains(e.Profile))
                    {
                        SelectedProfiles.Remove(e.Profile);
                    }
                    
                    // Update all visual status
                    SyncActiveProfilesStatus();
                });
            }
        }
        
        // Make ResetProfileSelection public so it can be called from BARS
        public void ResetProfileSelection(string profileName)
        {
            // Only remove the specific profile
            if (SelectedProfiles.Contains(profileName))
            {
                SelectedProfiles.Remove(profileName);
            }
            
            // Reset visual for this button
            if (profileButtons.ContainsKey(profileName))
            {
                profileButtons[profileName].BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            }
        }
        
        // Reset all selections
        public void ResetAllSelections()
        {
            SelectedProfiles.Clear();
            
            // Reset all button colors
            foreach (var button in profileButtons.Values)
            {
                button.BackColor = Colours.GetColour(Colours.Identities.WindowBackground);
            }
        }
        
        private void CreateProfileEntry(string profileName, int index)
        {
            // Create a button for the profile
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
        
        public void UpdateSelectedProfileVisual(string profileName)
        {
            // Don't reset other profiles, just set this one as selected
            if (!SelectedProfiles.Contains(profileName))
            {
                SelectedProfiles.Add(profileName);
            }
            
            // Set selected button to green
            if (profileButtons.ContainsKey(profileName))
            {
                profileButtons[profileName].BackColor = Color.FromArgb(0, 128, 0); // Green
            }
        }
        
        // Method to select a profile and notify listeners
        public void SelectProfile(string profileName)
        {
            // Check if there's already a controller window open for this profile
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
    }
    
    // Event args for profile selection
    public class ProfileSelectedEventArgs : EventArgs
    {
        public string Airport { get; private set; }
        public string ProfileName { get; private set; }
        
        public ProfileSelectedEventArgs(string airport, string profileName)
        {
            Airport = airport;
            ProfileName = profileName;
        }
    }
}
