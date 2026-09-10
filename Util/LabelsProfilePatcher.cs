using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using vatsys;

namespace BARS.Util
{
    internal enum LabelsPatchStatus
    {
        AlreadyPresent,
        Patched,
        ProfileUnavailable,
        LabelsUnavailable,
        UnsupportedLayout,
        Failed
    }

    internal sealed class LabelsPatchResult
    {
        public LabelsPatchResult(LabelsPatchStatus status, string labelsPath = null, string message = null)
        {
            Status = status;
            LabelsPath = labelsPath;
            Message = message;
        }

        public LabelsPatchStatus Status { get; }
        public string LabelsPath { get; }
        public string Message { get; }
        public bool IsAvailable => Status == LabelsPatchStatus.AlreadyPresent || Status == LabelsPatchStatus.Patched;
    }

    internal static class LabelsProfilePatcher
    {
        internal const string LabelItemType = "BARS_PILOT";

        private static readonly HashSet<string> GroundLabelTypes = new HashSet<string>(
            new[] { "GroundAir", "GroundLimited", "GroundDeparture", "GroundArrival" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly object PatchLock = new object();
        private static Func<string> LabelsPathResolver = FindActiveLabelsPath;

        public static LabelsPatchResult EnsurePatched()
        {
            lock (PatchLock)
            {
                try
                {
                    string labelsPath = LabelsPathResolver();
                    if (labelsPath == null)
                    {
                        LabelsPatchStatus status = Profile.Loaded
                            ? LabelsPatchStatus.LabelsUnavailable
                            : LabelsPatchStatus.ProfileUnavailable;
                        return new LabelsPatchResult(
                            status,
                            message: Profile.Loaded
                                ? $"Labels.xml could not be found for the active vatSys profile '{Profile.Name}'."
                                : "The active vatSys profile is not available yet.");
                    }

                    var document = new XmlDocument { PreserveWhitespace = true };
                    document.Load(labelsPath);

                    XmlNodeList labels = document.SelectNodes("/Labels/Label");
                    if (labels == null)
                    {
                        return new LabelsPatchResult(
                            LabelsPatchStatus.UnsupportedLayout,
                            labelsPath,
                            "The active profile uses an unsupported Labels.xml layout.");
                    }

                    int targetCount = 0;
                    int readyTargetCount = 0;
                    int patchedCount = 0;
                    foreach (XmlElement label in labels.OfType<XmlElement>())
                    {
                        string labelType = label.GetAttribute("Type");
                        if (!GroundLabelTypes.Contains(labelType))
                        {
                            continue;
                        }

                        targetCount++;
                        XmlElement existingBarsItem =
                            label.SelectSingleNode($".//Item[@Type='{LabelItemType}']") as XmlElement;
                        if (existingBarsItem != null)
                        {
                            readyTargetCount++;
                            if (!string.IsNullOrEmpty(existingBarsItem.GetAttribute("Colour")))
                            {
                                // Empty/default makes the custom item inherit the
                                // same state-dependent colour as the rest of the tag.
                                existingBarsItem.SetAttribute("Colour", string.Empty);
                                patchedCount++;
                            }
                            continue;
                        }

                        XmlNode callsignItem = label.SelectSingleNode(".//Item[@Type='LABEL_ITEM_ACID']");
                        if (callsignItem?.ParentNode == null)
                        {
                            continue;
                        }

                        XmlElement barsItem = document.CreateElement("Item");
                        barsItem.SetAttribute("Type", LabelItemType);
                        barsItem.SetAttribute("Colour", string.Empty);
                        barsItem.SetAttribute("LeftClick", string.Empty);
                        barsItem.SetAttribute("MiddleClick", string.Empty);
                        barsItem.SetAttribute("RightClick", string.Empty);
                        barsItem.SetAttribute("LeftPadding", "1");

                        XmlNode insertionPoint = callsignItem;
                        string indentation = GetIndentation(callsignItem);
                        if (indentation != null)
                        {
                            XmlWhitespace whitespace = document.CreateWhitespace(indentation);
                            insertionPoint = callsignItem.ParentNode.InsertAfter(whitespace, insertionPoint);
                        }

                        callsignItem.ParentNode.InsertAfter(barsItem, insertionPoint);
                        patchedCount++;
                        readyTargetCount++;
                    }

                    if (targetCount == 0)
                    {
                        return new LabelsPatchResult(
                            LabelsPatchStatus.UnsupportedLayout,
                            labelsPath,
                            "No supported ground-label definitions were found in the active profile.");
                    }

                    if (readyTargetCount != targetCount)
                    {
                        return new LabelsPatchResult(
                            LabelsPatchStatus.UnsupportedLayout,
                            labelsPath,
                            "BARS could not find a callsign position in each ground-label definition.");
                    }

                    if (patchedCount == 0)
                    {
                        return new LabelsPatchResult(LabelsPatchStatus.AlreadyPresent, labelsPath);
                    }

                    string temporaryPath = labelsPath + ".bars.tmp";
                    string backupPath = labelsPath + ".bars.bak";
                    try
                    {
                        document.Save(temporaryPath);

                        var validationDocument = new XmlDocument();
                        validationDocument.Load(temporaryPath);

                        try
                        {
                            File.Replace(temporaryPath, labelsPath, backupPath, true);
                        }
                        catch (PlatformNotSupportedException)
                        {
                            File.Copy(labelsPath, backupPath, true);
                            File.Copy(temporaryPath, labelsPath, true);
                        }
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            try { File.Delete(temporaryPath); } catch { }
                        }
                    }

                    return new LabelsPatchResult(LabelsPatchStatus.Patched, labelsPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return new LabelsPatchResult(
                        LabelsPatchStatus.Failed,
                        message: $"BARS does not have permission to update Labels.xml: {ex.Message}");
                }
                catch (IOException ex)
                {
                    return new LabelsPatchResult(
                        LabelsPatchStatus.Failed,
                        message: $"BARS could not safely update Labels.xml: {ex.Message}");
                }
                catch (XmlException ex)
                {
                    return new LabelsPatchResult(
                        LabelsPatchStatus.Failed,
                        message: $"The active profile's Labels.xml is invalid: {ex.Message}");
                }
                catch (Exception ex)
                {
                    return new LabelsPatchResult(
                        LabelsPatchStatus.Failed,
                        message: $"BARS could not prepare ground-tag indicators: {ex.Message}");
                }
            }
        }

        private static string FindActiveLabelsPath()
        {
            if (!Profile.Loaded || string.IsNullOrWhiteSpace(Profile.Name))
            {
                return null;
            }

            // vatSys keeps the selected profile's actual directory here. Unlike
            // Profile.Name, this also works for profiles whose display name,
            // version, and directory name are all different.
            string datasetPath = GetVatSysDatasetPath();
            string selectedLabelsPath = GetLabelsPathFromDirectory(datasetPath, false);
            if (selectedLabelsPath != null)
            {
                return selectedLabelsPath;
            }

            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string profilesRoot = Path.Combine(documentsPath, "vatSys Files", "Profiles");
            if (!Directory.Exists(profilesRoot))
            {
                return null;
            }

            var matches = new List<string>();
            foreach (string profileXmlPath in Directory.EnumerateFiles(profilesRoot, "Profile.xml", SearchOption.AllDirectories))
            {
                string candidate = GetLabelsPathFromDirectory(Path.GetDirectoryName(profileXmlPath), true);
                if (candidate != null)
                {
                    matches.Add(candidate);
                }
            }

            // Never patch an arbitrary profile if two installed datasets expose
            // the same display identity.
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string GetVatSysDatasetPath()
        {
            try
            {
                Assembly vatSysAssembly = typeof(Profile).Assembly;
                Type settingsType = vatSysAssembly.GetType("vatsys.Properties.Settings", false);
                if (settingsType == null)
                {
                    return null;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance;
                PropertyInfo defaultProperty = settingsType.GetProperty("Default", flags);
                PropertyInfo datasetPathProperty = settingsType.GetProperty("DatasetPath", flags);
                object settings = defaultProperty?.GetValue(null, null);
                return datasetPathProperty?.GetValue(settings, null) as string;
            }
            catch
            {
                // Older or future vatSys versions may store this differently;
                // the exact Profile.xml identity fallback below remains safe.
                return null;
            }
        }

        private static string GetLabelsPathFromDirectory(string profileDirectory, bool requireActiveIdentity)
        {
            if (string.IsNullOrWhiteSpace(profileDirectory))
            {
                return null;
            }

            try
            {
                string profileXmlPath = Path.Combine(profileDirectory, "Profile.xml");
                string labelsPath = Path.Combine(profileDirectory, "Labels.xml");
                if (!File.Exists(profileXmlPath) || !File.Exists(labelsPath))
                {
                    return null;
                }

                if (!requireActiveIdentity)
                {
                    return labelsPath;
                }

                var profileDocument = new XmlDocument();
                profileDocument.Load(profileXmlPath);
                XmlElement profileElement = profileDocument.DocumentElement;
                if (profileElement == null ||
                    !string.Equals(profileElement.Name, "Profile", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string displayName = profileElement.GetAttribute("FullName");
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = profileElement.GetAttribute("Name");
                }

                XmlElement versionElement = profileElement.SelectSingleNode("Version") as XmlElement;
                if (versionElement != null)
                {
                    string airac = versionElement.GetAttribute("AIRAC");
                    string revision = versionElement.GetAttribute("Revision");
                    if (!string.IsNullOrWhiteSpace(airac) || !string.IsNullOrWhiteSpace(revision))
                    {
                        displayName = $"{displayName} {airac}{revision}";
                    }
                }

                return string.Equals(displayName.Trim(), Profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? labelsPath
                    : null;
            }
            catch (XmlException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string GetIndentation(XmlNode node)
        {
            if (node?.PreviousSibling is XmlWhitespace whitespace)
            {
                return whitespace.Value;
            }

            return null;
        }
    }
}
