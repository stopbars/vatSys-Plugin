using BARS.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;
using BARS.Windows;

namespace BARS
{
    public static class AppData
    {
        public static Version CurrentVersion { get; } = new Version(2, 0, 0);
    }

    [Export(typeof(IPlugin))]
    public class BARS : IPlugin
    {
        private readonly Logger logger = new Logger("BARS for vatSys");
        private readonly NetManager netManager = NetManager.Instance;

        // Config Window
        public static Config config;
        public CustomToolStripMenuItem configMenu;

        // List of controller windows
        private static List<Controller_Legacy> LegacyWindows = new List<Controller_Legacy>();
        private static List<Controller_INTAS> INTASWindows = new List<Controller_INTAS>();

        // List of profile windows currently open
        public static List<Profiles> ProfileWindows = new List<Profiles>();

        // List of airports being controlled
        public static List<string> ControlledAirports { get; private set; } = new List<string>();

        // Dictionary to track active profiles for each airport
        public static Dictionary<string, Dictionary<string, string>> AirportProfiles { get; private set; } =
            new Dictionary<string, Dictionary<string, string>>();

        // Dictionary to track which profile is active for each airport
        private static Dictionary<string, string> ActiveProfiles = new Dictionary<string, string>();

        private const int MAX_AIRPORTS = 5;

        // Event that fires when controller window is closed
        public static event EventHandler<ControllerWindowEventArgs> ControllerWindowClosed;

        public BARS()
        {
            string dataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BARS"
            );

            // Register network event handlers
            vatsys.Network.Connected += Vatsys_ConnectionChanged;
            vatsys.Network.Disconnected += Vatsys_ConnectionChanged;

            // Initialize directories
            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(Path.Combine(dataPath, "vatSys-Airports"));
            if (File.Exists($"{dataPath}\\BARS-V2.log")) File.Delete($"{dataPath}\\BARS-V2.log");

            // Initialize NetManager with API key from settings
            netManager.Initialize(Properties.Settings.Default.APIKey);

            logger.Log("Starting BARS for vatSys...");
            _ = Start();
        }

        public string Name => "BARS for vatSys";
        public string DisplayName => "BARS for vatSys";

        private Task Start()
        {
            try
            {
                logger.Log("Populating the vatSys toolstrip...");

                // Add buttons to vatSys toolstrip
                configMenu = new CustomToolStripMenuItem(
                    CustomToolStripMenuItemWindowType.Main,
                    CustomToolStripMenuItemCategory.Custom,
                    new ToolStripMenuItem("BARS")
                );
                configMenu.Category = CustomToolStripMenuItemCategory.Windows;
                configMenu.Item.Click += BARSMenu_Click;
                configMenu.Item.Enabled = false;
                MMI.AddCustomMenuItem(configMenu);

                logger.Log("Started successfully.");
            }
            catch (Exception e)
            {
                logger.Error($"Error in Start: {e.Message}");
            }

            return Task.CompletedTask;
        }

        private void BARSMenu_Click(object sender, EventArgs e)
        {
            MMI.InvokeOnGUI(() => DoShowBARS());
        }

        public void OnFDRUpdate(FDP2.FDR updated)
        {
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
        }

        // Helper method to handle controller window closed
        private static void HandleControllerWindowClosed(object sender, string airport, string profile)
        {
            // Remove from active profiles if this is the active profile
            if (ActiveProfiles.ContainsKey(airport) &&
                (profile == null || ActiveProfiles[airport] == profile))
            {
                ActiveProfiles.Remove(airport);
            }

            // Notify listeners
            ControllerWindowClosed?.Invoke(null, new ControllerWindowEventArgs(airport, profile));

            // Remove the window from the list
            if (sender is Controller_Legacy legacyController)
            {
                LegacyWindows.Remove(legacyController);
            }
            else if (sender is Controller_INTAS intasController)
            {
                INTASWindows.Remove(intasController);
            }

            // Update any open profile windows for this airport
            UpdateProfileWindowSelections(airport, profile);
        }

        // Method to update profile window selections when a controller is closed
        private static void UpdateProfileWindowSelections(string airport, string profile)
        {
            // Find any profile windows for this airport and update their selection state
            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == airport);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() =>
                {
                    if (profile != null)
                    {
                        // Just reset the specific profile that was closed
                        profileWindow.ResetProfileSelection(profile);
                    }
                    else
                    {
                        // Sync with all current open profiles for this airport
                        profileWindow.SyncActiveProfilesStatus();
                    }
                });
            }
        }

        // Method to open a controller window with a specific runway profile
        public static void OpenControllerWithProfile(string airport, string profileName)
        {
            if (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(profileName))
                return;

            string formattedIcao = airport.Trim().ToUpper();

            // Track the active profile for this airport
            if (ActiveProfiles.ContainsKey(formattedIcao))
            {
                ActiveProfiles[formattedIcao] = profileName;
            }
            else
            {
                ActiveProfiles.Add(formattedIcao, profileName);
            }

            // Determine if this airport uses Legacy or INTAS controller
            bool isLegacy = formattedIcao == "YSSY" || formattedIcao == "YSCB";

            if (isLegacy)
            {
                // Check if a controller window with this profile already exists
                Controller_Legacy existingController = LegacyWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profileName);

                if (existingController != null)
                {
                    existingController.Show(Form.ActiveForm);
                    existingController.BringToFront();
                    return;
                }

                // Create a new controller window
                Controller_Legacy newController = new Controller_Legacy(formattedIcao, profileName);
                newController.FormClosed += (s, e) => HandleControllerWindowClosed(s, formattedIcao, profileName);
                LegacyWindows.Add(newController);
                newController.Show(Form.ActiveForm);
            }
            else
            {
                // Check if a controller window with this profile already exists
                Controller_INTAS existingController = INTASWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profileName);

                if (existingController != null)
                {
                    existingController.Show(Form.ActiveForm);
                    existingController.BringToFront();
                    return;
                }

                // Create a new controller window
                Controller_INTAS newController = new Controller_INTAS(formattedIcao, profileName);
                newController.FormClosed += (s, e) => HandleControllerWindowClosed(s, formattedIcao, profileName);
                INTASWindows.Add(newController);
                newController.Show(Form.ActiveForm);
            }

            // Update any open profile windows for this airport
            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == formattedIcao);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() => profileWindow.SyncActiveProfilesStatus());
            }
        }

        // Method to get all open profiles for an airport
        public static List<string> GetOpenProfiles(string airport)
        {
            List<string> result = new List<string>();

            string formattedIcao = airport.Trim().ToUpper();
            bool isLegacy = formattedIcao == "YSSY" || formattedIcao == "YSCB";

            if (isLegacy)
            {
                var controllers = LegacyWindows.Where(c => c.Airport == formattedIcao).ToList();
                foreach (var controller in controllers)
                {
                    if (!string.IsNullOrEmpty(controller.ActiveProfile) && !result.Contains(controller.ActiveProfile))
                    {
                        result.Add(controller.ActiveProfile);
                    }
                }
            }
            else
            {
                var controllers = INTASWindows.Where(c => c.Airport == formattedIcao).ToList();
                foreach (var controller in controllers)
                {
                    if (!string.IsNullOrEmpty(controller.ActiveProfile) && !result.Contains(controller.ActiveProfile))
                    {
                        result.Add(controller.ActiveProfile);
                    }
                }
            }

            return result;
        }

        // Method to get the active profile for an airport
        public static string GetActiveProfile(string airport)
        {
            string formattedIcao = airport.Trim().ToUpper();
            if (ActiveProfiles.ContainsKey(formattedIcao))
            {
                return ActiveProfiles[formattedIcao];
            }
            return null;
        }

        // Method to check if a controller window is open for this airport and profile
        public static bool IsControllerWindowOpen(string airport, string profile)
        {
            string formattedIcao = airport.Trim().ToUpper();
            bool isLegacy = formattedIcao == "YSSY" || formattedIcao == "YSCB";

            if (isLegacy)
            {
                return LegacyWindows.Any(c => c.Airport == formattedIcao && c.ActiveProfile == profile);
            }
            else
            {
                return INTASWindows.Any(c => c.Airport == formattedIcao && c.ActiveProfile == profile);
            }
        }

        public static void RemoveControllerWindow(string airport, string profile)
        {
            string formattedIcao = airport.Trim().ToUpper();
            bool isLegacy = formattedIcao == "YSSY" || formattedIcao == "YSCB";

            if (isLegacy)
            {
                var controller = LegacyWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profile);

                if (controller != null)
                {
                    MMI.InvokeOnGUI(() =>
                    {
                        controller.Close();
                        controller.Dispose();
                    });
                }
            }
            else
            {
                var controller = INTASWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profile);

                if (controller != null)
                {
                    MMI.InvokeOnGUI(() =>
                    {
                        controller.Close();
                        controller.Dispose();
                    });
                }
            }
        }

        public static void RemoveProfile(string airport, string profileName)
        {
            string formattedIcao = airport.Trim().ToUpper();

            // If this is the active profile, remove it from the active profiles
            if (ActiveProfiles.ContainsKey(formattedIcao) && ActiveProfiles[formattedIcao] == profileName)
            {
                ActiveProfiles.Remove(formattedIcao);
            }

            // Remove the controller window for this profile
            bool isLegacy = formattedIcao == "YSSY" || formattedIcao == "YSCB";
            if (isLegacy)
            {
                Controller_Legacy controllerToRemove = LegacyWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profileName);

                if (controllerToRemove != null)
                {
                    controllerToRemove.Close();
                    LegacyWindows.Remove(controllerToRemove);
                }
            }
            else
            {
                Controller_INTAS controllerToRemove = INTASWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profileName);

                if (controllerToRemove != null)
                {
                    controllerToRemove.Close();
                    INTASWindows.Remove(controllerToRemove);
                }
            }

            // Update any open profile windows
            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == formattedIcao);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() => profileWindow.SyncActiveProfilesStatus());
            }
        }

        public static void DoShowBARS()
        {
            if (config == null || config.IsDisposed)
            {
                config = new Config();
            }
            else if (config.Visible)
            {
                return;
            }
            config.Show(Form.ActiveForm);
        }

        public static async Task<bool> AddAirport(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return false;

            string formattedIcao = icao.Trim().ToUpper();

            // Check if it already exists
            if (ControlledAirports.Contains(formattedIcao))
                return false;

            // Check if we've reached the maximum limit
            if (ControlledAirports.Count >= MAX_AIRPORTS)
                return false;

            // Ensure WebSocket connection exists for this airport
            var netHandler = await NetManager.Instance.ConnectAirport(formattedIcao, Network.ControllerId);
            if (netHandler == null)
            {
                // Connection failed
                return false;
            }

            // Add to the list
            ControlledAirports.Add(formattedIcao);

            // Update the config window if it's open
            if (config != null && !config.IsDisposed)
            {
                config.SyncAirportList();
            }

            // Show the profile selection window instead of directly opening a controller
            MMI.InvokeOnGUI(() => ShowProfilesWindow(formattedIcao));

            return true;
        }

        public static void ShowProfilesWindow(string icao)
        {
            // Check if there's already a profile window open for this airport
            var existingWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == icao);
            if (existingWindow != null && !existingWindow.IsDisposed)
            {
                existingWindow.Show(Form.ActiveForm);
                existingWindow.BringToFront();

                // Make sure to update all profile statuses
                existingWindow.SyncActiveProfilesStatus();
                return;
            }

            // Create a new profile window
            var profileWindow = new Profiles(icao);
            profileWindow.ProfileSelected += (s, e) =>
            {
                // When a profile is selected, open a controller window with that profile
                OpenControllerWithProfile(e.Airport, e.ProfileName);
            };

            // Only remove from list when truly disposing
            profileWindow.Disposed += (s, e) =>
            {
                if (s is Profiles p)
                {
                    ProfileWindows.Remove(p);
                }
            };

            ProfileWindows.Add(profileWindow);
            profileWindow.Show(Form.ActiveForm);
        }

        public static async Task RemoveAirport(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return;

            string formattedIcao = icao.Trim().ToUpper();

            // Check if it exists
            if (!ControlledAirports.Contains(formattedIcao))
                return;

            // Remove from the list
            ControlledAirports.Remove(formattedIcao);

            // Disconnect WebSocket for this airport
            await NetManager.Instance.DisconnectAirport(formattedIcao);

            // Update the config window if it's open
            if (config != null && !config.IsDisposed)
            {
                config.SyncAirportList();
            }

            // Remove all controller windows for this airport
            RemoveControllerWindows(formattedIcao);
        }

        public static void RemoveControllerWindows(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return;

            string formattedIcao = icao.Trim().ToUpper();

            // Remove any active profiles for this airport
            if (ActiveProfiles.ContainsKey(formattedIcao))
            {
                ActiveProfiles.Remove(formattedIcao);
            }

            // Close all controller windows for this airport regardless of profile
            if (formattedIcao == "YSSY" || formattedIcao == "YSCB")
            {
                var controllersToRemove = LegacyWindows.Where(c => c.Airport == formattedIcao).ToList();
                foreach (var controller in controllersToRemove)
                {
                    controller.Close();
                    LegacyWindows.Remove(controller);
                }
            }
            else
            {
                var controllersToRemove = INTASWindows.Where(c => c.Airport == formattedIcao).ToList();
                foreach (var controller in controllersToRemove)
                {
                    controller.Close();
                    INTASWindows.Remove(controller);
                }
            }

            // Update any open profile windows
            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == formattedIcao);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() => profileWindow.SyncActiveProfilesStatus());
            }
        }

        private async void Vatsys_ConnectionChanged(object sender, EventArgs e)
        {
            if (Network.IsConnected)
            {
                while (configMenu == null)
                {
                    await Task.Delay(500);
                    logger.Log("Waiting for menu initialization...");
                }

                MMI.InvokeOnGUI(async () =>
                {
                    // Additional safety check inside the invoke
                    while (configMenu?.Item == null)
                    {
                        await Task.Delay(100);
                        logger.Log("Waiting for menu item initialization...");
                    }

                    configMenu.Item.Enabled = true;

                    logger.Log("Menu item enabled");
                });

                logger.Log("Connected");
            }
            else
            {
                logger.Log("Disconnected");

                // Create a copy of airports to avoid collection modification during enumeration
                var airportsToRemove = ControlledAirports.ToList();
                var ProfileWindowsToRemove = ProfileWindows.ToList();

                if (config != null)
                {
                    config.Close();
                    config.Dispose();
                }

                foreach (var airport in airportsToRemove)
                {
                    await RemoveAirport(airport);
                }

                foreach (var profileWindow in ProfileWindowsToRemove)
                {
                    profileWindow.Close();
                    profileWindow.Dispose();
                }
            }
        }


        // Update API key setting and reinitialize NetManager
        public static void UpdateApiKey(string newApiKey)
        {
            Properties.Settings.Default.APIKey = newApiKey;
            Properties.Settings.Default.Save();
            NetManager.Instance.Initialize(newApiKey);
        }
    }

    // Event args for controller window events
    public class ControllerWindowEventArgs : EventArgs
    {
        public string Airport { get; private set; }
        public string Profile { get; private set; }

        public ControllerWindowEventArgs(string airport, string profile)
        {
            Airport = airport;
            Profile = profile;
        }
    }
}