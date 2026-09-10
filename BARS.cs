using BARS.Util;
using BARS.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;
using vatsys.Plugin;

namespace BARS
{
    public static class AppData
    {
        public static Version CurrentVersion { get; } = new Version(2, 0, 0);
    }

    [Export(typeof(IPlugin))]
    [Export(typeof(ILabelPlugin))]
    public class BARS : IPlugin, ILabelPlugin
    {
        public static Config config;
        public static List<Profiles> ProfileWindows = new List<Profiles>();
        public CustomToolStripMenuItem configMenu;
        private const int MAX_AIRPORTS = 5;

        private static Dictionary<string, string> ActiveProfiles = new Dictionary<string, string>();
        private static Dictionary<string, string> AirportProfileFormats = new Dictionary<string, string>();
        private static readonly object AirportStateLock = new object();
        private static readonly HashSet<string> AirportsBeingAdded = new HashSet<string>();
        private static List<Controller_INTAS> INTASWindows = new List<Controller_INTAS>();
        private static List<Controller_Legacy> LegacyWindows = new List<Controller_Legacy>();
        private readonly Logger logger = new Logger("BARS for vatSys");
        private readonly NetManager netManager = NetManager.Instance;
        private static readonly object LabelsPatchLock = new object();

        public BARS()
        {
            string dataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BARS"
            ); vatsys.Network.Connected += Vatsys_ConnectionChanged;
            vatsys.Network.Disconnected += Vatsys_ConnectionChanged;

            Directory.CreateDirectory(dataPath);
            Directory.CreateDirectory(Path.Combine(dataPath, "vatSys"));
            if (File.Exists($"{dataPath}\\BARS-V2.log")) File.Delete($"{dataPath}\\BARS-V2.log");

            netManager.Initialize(Properties.Settings.Default.APIKey);

            LabelsPatchResult patchResult = EnsurePilotLabelsPatched();
            if (patchResult.Status == LabelsPatchStatus.Patched)
            {
                logger.Log($"Added BARS pilot indicator to {patchResult.LabelsPath}. A vatSys restart is required.");
            }
            else if (!patchResult.IsAvailable)
            {
                logger.Error(patchResult.Message ?? "BARS pilot labels could not be prepared.");
            }

            logger.Log("Starting BARS for vatSys...");
            _ = Start();
        }

        public static event EventHandler<ControllerWindowEventArgs> ControllerWindowClosed;
        public static Dictionary<string, Dictionary<string, string>> AirportProfiles { get; private set; } =
        new Dictionary<string, Dictionary<string, string>>();

        public static List<string> ControlledAirports { get; private set; } = new List<string>();

        public string DisplayName => "BARS for vatSys";
        public string Name => "BARS for vatSys";

        public static bool LabelsAddedThisLaunch { get; private set; }

        internal static LabelsPatchResult EnsurePilotLabelsPatched()
        {
            lock (LabelsPatchLock)
            {
                LabelsPatchResult result = LabelsProfilePatcher.EnsurePatched();
                if (result.Status == LabelsPatchStatus.Patched)
                {
                    LabelsAddedThisLaunch = true;
                }

                return result;
            }
        }

        public static void SetShowBARSPilots(bool enabled)
        {
            Properties.Settings.Default.ShowBARSPilots = enabled;
            Properties.Settings.Default.Save();

            if (enabled)
            {
                _ = NetManager.Instance.RequestOnlinePilotsAll();
            }
            else
            {
                if (PilotPresenceStore.ClearAll())
                {
                    MMI.RequestRedraw(false, false, false);
                }
            }
        }

        public static bool IsLegacyAirport(string icao)
        {
            string formattedIcao = (icao ?? string.Empty).Trim().ToUpper();
            if (AirportProfileFormats.TryGetValue(formattedIcao, out var format))
            {
                return CdnProfiles.IsLegacyFormat(format);
            }

            return LegacyWindows.Any(c => c.Airport == formattedIcao);
        }

        public static async Task<bool> AddAirport(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return false;

            string formattedIcao = icao.Trim().ToUpper();

            lock (AirportStateLock)
            {
                if (ControlledAirports.Contains(formattedIcao) || AirportsBeingAdded.Contains(formattedIcao))
                {
                    return false;
                }

                if (ControlledAirports.Count + AirportsBeingAdded.Count >= MAX_AIRPORTS)
                {
                    return false;
                }

                AirportsBeingAdded.Add(formattedIcao);
            }

            try
            {
                CdnProfiles.GeneratedProfilesResponse generated;
                try
                {
                    generated = await CdnProfiles.GenerateProfilesAsync(
                        formattedIcao,
                        Properties.Settings.Default.APIKey,
                        forceRefresh: true
                    );

                    if (generated.Profiles == null || generated.Profiles.Count == 0)
                    {
                        MessageBox.Show(
                            $"No vatSys profiles were generated for {formattedIcao}.",
                            "Profile Generation Unavailable",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                        return false;
                    }

                    if (!CdnProfiles.IsLegacyFormat(generated.Format) && !CdnProfiles.IsIntasFormat(generated.Format))
                    {
                        MessageBox.Show(
                            $"Generated profiles for {formattedIcao} returned unsupported format '{generated.Format}'.",
                            "Profile Generation Unavailable",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                        return false;
                    }
                }
                catch (CdnProfiles.ProfileGenerationException ex)
                {
                    MessageBox.Show(
                        ex.Message,
                        "Profile Generation Unavailable",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return false;
                }

                var netHandler = await NetManager.Instance.ConnectAirport(formattedIcao, Network.ControllerId);
                if (netHandler == null)
                {
                    return false;
                }

                lock (AirportStateLock)
                {
                    if (ControlledAirports.Contains(formattedIcao))
                    {
                        return false;
                    }

                    AirportProfileFormats[formattedIcao] = generated.Format;
                    ControlledAirports.Add(formattedIcao);
                }

                if (config != null && !config.IsDisposed)
                {
                    config.SyncAirportList();
                }

                MMI.InvokeOnGUI(() =>
                {
                    if (CdnProfiles.IsIntasFormat(generated.Format))
                    {
                        OpenINTASAirport(formattedIcao);
                    }
                    else
                    {
                        ShowProfilesWindow(formattedIcao);
                    }
                });

                return true;
            }
            finally
            {
                lock (AirportStateLock)
                {
                    AirportsBeingAdded.Remove(formattedIcao);
                }
            }
        }

        public static void DoShowBARS()
        {
            if (config == null || config.IsDisposed)
            {
                config = new Config();
            }

            ShowAndActivate(config);
        }

        private static void ShowAndActivate(Form form)
        {
            if (form == null || form.IsDisposed)
            {
                return;
            }

            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }

            if (!form.Visible)
            {
                Form owner = Form.ActiveForm;
                if (owner != null && !ReferenceEquals(owner, form) && !owner.IsDisposed)
                {
                    form.Show(owner);
                }
                else
                {
                    form.Show();
                }
            }

            form.BringToFront();
            form.Activate();
            form.Invalidate(true);
            form.Update();
        }

        public static string GetActiveProfile(string airport)
        {
            string formattedIcao = airport.Trim().ToUpper();
            if (ActiveProfiles.ContainsKey(formattedIcao))
            {
                return ActiveProfiles[formattedIcao];
            }
            return null;
        }

        private static void SetActiveProfile(string formattedIcao, string profileName)
        {
            if (ActiveProfiles.ContainsKey(formattedIcao))
            {
                ActiveProfiles[formattedIcao] = profileName;
            }
            else
            {
                ActiveProfiles.Add(formattedIcao, profileName);
            }
        }

        private static void SyncProfileWindowStatus(string formattedIcao)
        {
            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == formattedIcao);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() => profileWindow.SyncActiveProfilesStatus());
            }
        }

        public static List<string> GetOpenProfiles(string airport)
        {
            List<string> result = new List<string>();

            string formattedIcao = airport.Trim().ToUpper();
            bool isLegacy = IsLegacyAirport(formattedIcao);

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

        // Method to check if a controller window is open for this airport and profile
        public static bool IsControllerWindowOpen(string airport, string profile)
        {
            string formattedIcao = airport.Trim().ToUpper();
            bool isLegacy = IsLegacyAirport(formattedIcao);

            if (isLegacy)
            {
                return LegacyWindows.Any(c => c.Airport == formattedIcao && c.ActiveProfile == profile);
            }
            else
            {
                return INTASWindows.Any(c => c.Airport == formattedIcao && c.ActiveProfile == profile);
            }
        }

        public static void OpenControllerWithProfile(string airport, string profileName)
        {
            if (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(profileName))
                return; string formattedIcao = airport.Trim().ToUpper();

            bool isLegacy = IsLegacyAirport(formattedIcao);

            if (isLegacy)
            {
                Controller_Legacy existingController = LegacyWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profileName);

                if (existingController != null)
                {
                    SetActiveProfile(formattedIcao, profileName);
                    ShowAndActivate(existingController);
                    return;
                }

                try
                {
                    string xml = CdnProfiles.GetLegacyProfileXml(formattedIcao, profileName);
                    if (string.IsNullOrWhiteSpace(xml))
                    {
                        MessageBox.Show($"Generated profile not found for {formattedIcao} - {profileName}",
                            "Profile Not Found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        SyncProfileWindowStatus(formattedIcao);
                        return;
                    }
                }
                catch (CdnProfiles.ProfileGenerationException ex)
                {
                    MessageBox.Show(ex.Message,
                        "Profile Generation Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SyncProfileWindowStatus(formattedIcao);
                    return;
                }

                SetActiveProfile(formattedIcao, profileName);
                Controller_Legacy newController = new Controller_Legacy(formattedIcao, profileName);
                newController.FormClosed += (s, e) => HandleControllerWindowClosed(s, formattedIcao, profileName);
                LegacyWindows.Add(newController);
                ShowAndActivate(newController);
            }
            else
            {
                Controller_INTAS existingController = INTASWindows.FirstOrDefault(c =>
                    c.Airport == formattedIcao && c.ActiveProfile == profileName);

                if (existingController != null)
                {
                    SetActiveProfile(formattedIcao, profileName);
                    ShowAndActivate(existingController);
                    return;
                }

                try
                {
                    if (!CdnProfiles.HasIntasProfile(formattedIcao))
                    {
                        MessageBox.Show($"No generated INTAS profile found for {formattedIcao}.",
                            "Profile Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        SyncProfileWindowStatus(formattedIcao);
                        return;
                    }
                }
                catch (CdnProfiles.ProfileGenerationException ex)
                {
                    MessageBox.Show(ex.Message,
                        "Profile Generation Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SyncProfileWindowStatus(formattedIcao);
                    return;
                }

                SetActiveProfile(formattedIcao, profileName);
                Controller_INTAS newController = new Controller_INTAS(formattedIcao, profileName);
                newController.FormClosed += (s, e) => HandleControllerWindowClosed(s, formattedIcao, profileName);
                INTASWindows.Add(newController);
                ShowAndActivate(newController);
            }

            SyncProfileWindowStatus(formattedIcao);
        }

        public static void OpenINTASAirport(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return; string formattedIcao = icao.Trim().ToUpper();

            if (!ControlledAirports.Contains(formattedIcao))
                return;

            try
            {
                if (!CdnProfiles.HasIntasProfile(formattedIcao))
                {
                    MessageBox.Show($"No generated INTAS profile found for {formattedIcao}.",
                        "Profile Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            catch (CdnProfiles.ProfileGenerationException ex)
            {
                MessageBox.Show(ex.Message,
                    "Profile Generation Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Controller_INTAS existingController = INTASWindows.FirstOrDefault(c => c.Airport == formattedIcao);

            if (existingController != null)
            {
                ShowAndActivate(existingController);
                return;
            }

            Controller_INTAS newController = new Controller_INTAS(formattedIcao, formattedIcao);
            newController.FormClosed += (s, e) => HandleControllerWindowClosed(s, formattedIcao, formattedIcao);
            INTASWindows.Add(newController);
            ShowAndActivate(newController);
        }

        public static async Task RemoveAirport(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return; string formattedIcao = icao.Trim().ToUpper();

            if (!ControlledAirports.Contains(formattedIcao))
                return;

            ControlledAirports.Remove(formattedIcao);

            await NetManager.Instance.DisconnectAirport(formattedIcao);

            if (config != null && !config.IsDisposed)
            {
                config.SyncAirportList();
            }

            // Remove all controller windows for this airport
            RemoveControllerWindows(formattedIcao);
            AirportProfileFormats.Remove(formattedIcao);
        }

        public static void RemoveControllerWindow(string airport, string profile)
        {
            string formattedIcao = airport.Trim().ToUpper();
            bool isLegacy = IsLegacyAirport(formattedIcao);

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

        public static void RemoveControllerWindows(string icao)
        {
            if (string.IsNullOrWhiteSpace(icao))
                return; string formattedIcao = icao.Trim().ToUpper();
            if (ActiveProfiles.ContainsKey(formattedIcao))
            {
                ActiveProfiles.Remove(formattedIcao);
            }

            if (IsLegacyAirport(formattedIcao))
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

            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == formattedIcao);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() => profileWindow.SyncActiveProfilesStatus());
            }
        }

        public static void RemoveProfile(string airport, string profileName)
        {
            string formattedIcao = airport.Trim().ToUpper();

            if (ActiveProfiles.ContainsKey(formattedIcao) && ActiveProfiles[formattedIcao] == profileName)
            {
                ActiveProfiles.Remove(formattedIcao);
            }

            bool isLegacy = IsLegacyAirport(formattedIcao);
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

            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == formattedIcao);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() => profileWindow.SyncActiveProfilesStatus());
            }
        }

        public static void ShowProfilesWindow(string icao)
        {
            var existingWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == icao);
            if (existingWindow != null && !existingWindow.IsDisposed)
            {
                ShowAndActivate(existingWindow);

                existingWindow.SyncActiveProfilesStatus();
                return;
            }

            var profileWindow = new Profiles(icao);
            profileWindow.ProfileSelected += (s, e) =>
            {
                OpenControllerWithProfile(e.Airport, e.ProfileName);
            };

            profileWindow.Disposed += (s, e) =>
            {
                if (s is Profiles p)
                {
                    ProfileWindows.Remove(p);
                }
            };

            ProfileWindows.Add(profileWindow);
            ShowAndActivate(profileWindow);
        }

        public static void UpdateApiKey(string newApiKey)
        {
            Properties.Settings.Default.APIKey = newApiKey;
            Properties.Settings.Default.Save();
            // Apply new key across existing connections without requiring restart
            _ = NetManager.Instance.UpdateApiKeyAsync(newApiKey);
        }

        public void OnFDRUpdate(FDP2.FDR updated)
        {
        }

        public void OnRadarTrackUpdate(RDP.RadarTrack updated)
        {
        }

        public CustomLabelItem GetCustomLabelItem(
            string itemType,
            Track track,
            FDP2.FDR flightDataRecord,
            RDP.RadarTrack radarTrack)
        {
            if (!string.Equals(itemType, LabelsProfilePatcher.LabelItemType, StringComparison.Ordinal) ||
                !Properties.Settings.Default.ShowBARSPilots)
            {
                return null;
            }

            FDP2.FDR fdr = flightDataRecord ?? track?.GetFDR(true);
            string callsign = fdr?.Callsign;
            if (string.IsNullOrWhiteSpace(callsign))
            {
                callsign = radarTrack?.ActualAircraft?.Callsign;
            }
            if (string.IsNullOrWhiteSpace(callsign))
            {
                callsign = track?.GetRadarTrack()?.ActualAircraft?.Callsign;
            }

            if (!PilotPresenceStore.IsOnline(callsign))
            {
                return null;
            }

            return new CustomLabelItem
            {
                Text = "B",
                ForeColourIdentity = Colours.Identities.Default
            };
        }

        public CustomColour SelectASDTrackColour(Track track)
        {
            return null;
        }

        public CustomColour SelectGroundTrackColour(Track track)
        {
            return null;
        }

        private static void HandleControllerWindowClosed(object sender, string airport, string profile)
        {
            if (ActiveProfiles.ContainsKey(airport) &&
                (profile == null || ActiveProfiles[airport] == profile))
            {
                ActiveProfiles.Remove(airport);
            }

            if (sender is Controller_Legacy legacyController)
            {
                LegacyWindows.Remove(legacyController);
            }
            else if (sender is Controller_INTAS intasController)
            {
                INTASWindows.Remove(intasController);
            }

            ControllerWindowClosed?.Invoke(null, new ControllerWindowEventArgs(airport, profile));

            UpdateProfileWindowSelections(airport, profile);
        }

        private static void UpdateProfileWindowSelections(string airport, string profile)
        {
            var profileWindow = ProfileWindows.FirstOrDefault(p => p.AirportIcao == airport);
            if (profileWindow != null && !profileWindow.IsDisposed)
            {
                MMI.InvokeOnGUI(() =>
                {
                    if (profile != null)
                    {
                        profileWindow.ResetProfileSelection(profile);
                    }
                    else
                    {
                        profileWindow.SyncActiveProfilesStatus();
                    }
                });
            }
        }

        private void BARSMenu_Click(object sender, EventArgs e)
        {
            MMI.InvokeOnGUI(() => DoShowBARS());
        }

        private Task Start()
        {
            try
            {
                logger.Log("Populating the vatSys toolstrip...");

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

                if (configMenu?.Item != null)
                {
                    MMI.InvokeOnGUI(() =>
                    {
                        configMenu.Item.Enabled = false;
                        logger.Log("Menu item disabled");
                    });
                }

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
    }

    public class ControllerWindowEventArgs : EventArgs
    {
        public ControllerWindowEventArgs(string airport, string profile)
        {
            Airport = airport;
            Profile = profile;
        }

        public string Airport { get; private set; }
        public string Profile { get; private set; }
    }
}
