using System;
using BARS.Util;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using vatsys;

namespace BARS.Windows
{
    public partial class Profiles : BaseForm
    {
        // Make airportIcao public so it can be accessed by BARS
        public string AirportIcao { get; private set; }
        public List<string> SelectedProfiles { get; private set; } = new List<string>();
        private const int PROFILE_ENTRY_HEIGHT = 30;

        private const int PROFILE_ENTRY_SPACING = 5;

        private bool isDisposing = false;

        private Dictionary<string, GenericButton> profileButtons = new Dictionary<string, GenericButton>();

        // Regex to detect runway pair names like "16/34", "16L/34R", with optional spaces
        private static readonly Regex RunwayPairRegex = new Regex(
            @"^\s*(?<aNum>\d{1,2})(?<aSuf>[LRC]*)\s*/\s*(?<bNum>\d{1,2})(?<bSuf>[LRC]*)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private class ProfileItem
        {
            public string Original { get; set; }
            public string Display { get; set; }
            public bool IsRunway { get; set; }
            public int PrimarySort { get; set; }
            public string SecondarySort { get; set; }
        }

        public Profiles(string icao)
        {
            InitializeComponent();
            // Lock window sizing – prevent scaling
            this.Resizeable = false;
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MinimumSize = this.Size;
            this.MaximumSize = this.Size;
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

        private void CreateProfileEntry(string originalName, string displayName, int index)
        {

            var profileButton = new GenericButton
            {
                Text = displayName,
                Size = new Size(pnl_profiles.Width - 20, PROFILE_ENTRY_HEIGHT),
                Location = new Point(10, index * (PROFILE_ENTRY_HEIGHT + PROFILE_ENTRY_SPACING) + 5),
                Font = new Font("Terminus (TTF)", 16F, FontStyle.Regular, GraphicsUnit.Pixel),
                FlatStyle = FlatStyle.Flat,
                BackColor = Colours.GetColour(Colours.Identities.WindowBackground),
                ForeColor = Colours.GetColour(Colours.Identities.InteractiveText),
                Tag = originalName
            };

            profileButton.Click += (s, e) =>
            {
                if (s is GenericButton btn)
                {
                    SelectProfile(btn.Tag.ToString());
                }
            };

            profileButtons[originalName] = profileButton;
            pnl_profiles.Controls.Add(profileButton);
        }

        private static bool TryParseRunwayToken(string token, out int number, out string suffix)
        {
            number = 0;
            suffix = string.Empty;
            if (string.IsNullOrWhiteSpace(token)) return false;
            token = token.Trim();
            // extract leading digits
            int i = 0;
            while (i < token.Length && char.IsDigit(token[i])) i++;
            if (i == 0) return false;
            if (!int.TryParse(token.Substring(0, i), out number)) return false;
            if (number < 1 || number > 36) return false;
            suffix = token.Substring(i).ToUpperInvariant();
            // Only allow L/R/C suffixes
            if (suffix.Length > 0 && !Regex.IsMatch(suffix, @"^[LRC]+$", RegexOptions.IgnoreCase))
            {
                return false;
            }
            return true;
        }

        private static ProfileItem AnalyzeProfileName(string original)
        {
            var m = RunwayPairRegex.Match(original ?? string.Empty);
            if (!m.Success)
            {
                // Not a runway pair; return as-is
                return new ProfileItem
                {
                    Original = original,
                    Display = original,
                    IsRunway = false,
                    PrimarySort = int.MaxValue,
                    SecondarySort = (original ?? string.Empty).ToUpperInvariant()
                };
            }

            // Parse tokens
            var aNumStr = m.Groups["aNum"].Value;
            var aSuf = m.Groups["aSuf"].Value.ToUpperInvariant();
            var bNumStr = m.Groups["bNum"].Value;
            var bSuf = m.Groups["bSuf"].Value.ToUpperInvariant();

            if (!TryParseRunwayToken(aNumStr + aSuf, out var aNum, out var aSufNorm) ||
                !TryParseRunwayToken(bNumStr + bSuf, out var bNum, out var bSufNorm))
            {
                // Fallback to non-runway if parsing fails
                return new ProfileItem
                {
                    Original = original,
                    Display = original,
                    IsRunway = false,
                    PrimarySort = int.MaxValue,
                    SecondarySort = (original ?? string.Empty).ToUpperInvariant()
                };
            }

            // Order tokens by numeric runway ascending
            int leftNum = aNum, rightNum = bNum; string leftSuf = aSufNorm, rightSuf = bSufNorm;
            if (bNum < aNum)
            {
                leftNum = bNum; rightNum = aNum; leftSuf = bSufNorm; rightSuf = aSufNorm;
            }

            // Build display string with smaller/bigger first and preserve suffixes
            string display = $"{leftNum}{leftSuf}/{rightNum}{rightSuf}";

            // Sorting: primary by smaller numeric runway, secondary alphabetically on the display string
            return new ProfileItem
            {
                Original = original,
                Display = display,
                IsRunway = true,
                PrimarySort = Math.Min(aNum, bNum),
                SecondarySort = display.ToUpperInvariant()
            };
        }

        private static List<ProfileItem> FormatAndSortProfiles(IEnumerable<string> originals)
        {
            var items = new List<ProfileItem>();
            foreach (var o in originals ?? Enumerable.Empty<string>())
            {
                items.Add(AnalyzeProfileName(o));
            }

            // Runway pairs first, sorted by number then alphabetically; others after, alphabetically
            var ordered = items
                .OrderByDescending(i => i.IsRunway)
                .ThenBy(i => i.PrimarySort)
                .ThenBy(i => i.SecondarySort)
                .ToList();

            return ordered;
        }

        private void LoadProfiles()
        {
            pnl_profiles.Controls.Clear();
            profileButtons.Clear();

            // Fetch legacy profile names from CDN index (original names)
            var originalProfiles = CdnProfiles.GetLegacyProfileNames(AirportIcao) ?? new List<string>();

            // Compute display formatting and stable numeric sorting
            var formatted = FormatAndSortProfiles(originalProfiles);

            for (int i = 0; i < formatted.Count; i++)
            {
                CreateProfileEntry(formatted[i].Original, formatted[i].Display, i);
            }

            BARS.ControllerWindowClosed += BARS_ControllerWindowClosed;
        }

        private void LoadINTASInterface()
        {
            pnl_profiles.Controls.Clear();
            profileButtons.Clear();

            // Show open button if CDN has an INTAS map for this airport
            var cdnUrl = CdnProfiles.GetAirportXmlUrl(AirportIcao);
            if (!string.IsNullOrEmpty(cdnUrl))
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
                    Text = $"No online profile found for {AirportIcao}",
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