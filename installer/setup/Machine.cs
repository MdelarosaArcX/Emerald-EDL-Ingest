using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Web.Script.Serialization;

namespace Emerald.Setup;

/// <summary>What the setup needs to know about this machine before it asks anything.</summary>
internal static class Machine
{
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emerald", "settings.json");

    public static string DefaultDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emerald");

    /// <summary>The version of a file, as "6.36.1", or null when it is not there.</summary>
    public static Version VersionOf(string path)
    {
        if (!File.Exists(path)) return null;

        FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
    }

    public static string Show(Version v) =>
        v is null ? "" : v.Revision == 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString();

    /// <summary>The DELTACAST library Emerald calls, as installed in System32.</summary>
    public static Version InstalledDeltacast() =>
        VersionOf(Path.Combine(Environment.SystemDirectory, "VideoMasterHD.dll"));

    /// <summary>DELTACAST cards fitted now, with Windows' word for their state.</summary>
    public static List<(string Name, string Status)> DeltacastCards()
    {
        var cards = new List<(string, string)>();

        try
        {
            // Win32_PnPEntity lists present devices only. 1B66 is DELTACAST's PCI vendor id.
            using var search = new ManagementObjectSearcher(
                "SELECT Name, Status, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI%VEN_1B66%'");

            foreach (ManagementBaseObject device in search.Get())
                cards.Add(((string)device["Name"] ?? "DELTACAST card", (string)device["Status"] ?? ""));
        }
        catch
        {
            // Not knowing is not a reason to stop an install.
        }

        return cards;
    }

    public static bool EmeraldRunning() => Process.GetProcessesByName("Emerald.App").Length > 0;

    /// <summary>The folders already in settings.json, by name; empty when there is no file.</summary>
    public static Dictionary<string, string> SavedFolders()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(SettingsFile)) return found;

            var json = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(SettingsFile))
                as Dictionary<string, object>;
            if (json is null) return found;

            foreach (string key in new[] { "captureFolder", "highResFolder", "lowResFolder", "dataFolder" })
                if (json.TryGetValue(key, out object value) && value is string s && !string.IsNullOrWhiteSpace(s))
                    found[key] = s;
        }
        catch
        {
            // An unreadable file is left for install.bat's helper, which refuses to overwrite it.
        }

        return found;
    }

    /// <summary>
    /// The roomiest fixed drive that is not the system one - almost always where a plant wants
    /// its recordings. The system drive when there is no other. As install.bat chooses it.
    /// </summary>
    public static string BestDrive()
    {
        string system = Path.GetPathRoot(Environment.SystemDirectory);

        DriveInfo best = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady &&
                        !string.Equals(d.Name, system, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();

        return (best?.Name ?? system).TrimEnd('\\');
    }

    /// <summary>"812 GB free on E:", or empty when the path is not on a drive this can see.</summary>
    public static string FreeSpace(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path));
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return $"{root.TrimEnd('\\')} is not ready";

            return $"{drive.AvailableFreeSpace / (double)(1L << 30):N0} GB free on {root.TrimEnd('\\')}";
        }
        catch
        {
            return "";
        }
    }
}
