using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    // Folder picker for the settings page. Browsers cannot hand a page the full path of a folder the
    // user picks (showDirectoryPicker only returns a handle), and a native Windows dialog would open
    // behind a fullscreen game and is useless from a tablet. So the page asks the mod to list folders
    // and the user clicks through them; this works the same in the Steam overlay, a browser or a tablet.
    // Only folder names are listed, never file contents.
    internal static class WebUiFolders
    {
        private const int MaxEntries = 500;

        public static Dictionary<string, object> List(string? path)
        {
            List<object> drives = new List<object>();
            foreach (DriveInfo drive in SafeDrives())
            {
                drives.Add(new Dictionary<string, object>
                {
                    ["name"] = DriveLabel(drive),
                    ["path"] = drive.RootDirectory.FullName,
                });
            }

            List<object> entries = new List<object>();
            string current = string.Empty;
            string parent = string.Empty;
            string error = string.Empty;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    current = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path!.Trim()));
                    if (!Directory.Exists(current))
                    {
                        error = "这个文件夹不存在。";
                    }
                    else
                    {
                        DirectoryInfo info = new DirectoryInfo(current);
                        parent = info.Parent?.FullName ?? string.Empty;
                        List<DirectoryInfo> children = new List<DirectoryInfo>(info.GetDirectories());
                        children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                        foreach (DirectoryInfo child in children)
                        {
                            if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                            {
                                continue;
                            }

                            entries.Add(new Dictionary<string, object> { ["name"] = child.Name, ["path"] = child.FullName });
                            if (entries.Count >= MaxEntries)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    error = "没有权限打开这个文件夹。";
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                }
            }

            return new Dictionary<string, object>
            {
                ["path"] = current,
                ["parent"] = parent,
                ["entries"] = entries,
                ["drives"] = drives,
                ["error"] = error,
            };
        }

        // Opens a known folder in Explorer. Only fixed targets are accepted; the page cannot open an
        // arbitrary path through this.
        public static Dictionary<string, object> Open(string? target, Settings settings)
        {
            string path;
            bool select = false;
            switch (target)
            {
                case "export":
                    path = ChartRenderPaths.GetExportDirectory(settings);
                    break;
                case "workspace":
                    path = ChartRenderPaths.GetWorkspaceDirectory(settings);
                    break;
                case "logs":
                    path = Path.Combine(ChartRenderPaths.GetWorkspaceDirectory(settings), "Logs");
                    break;
                case "diagnostics":
                    path = ChartRenderDiagnosticsExport.GetDirectory(settings);
                    break;
                case "lastOutput":
                    path = ChartRenderSession.LastOutputPath;
                    select = true;
                    break;
                default:
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Unknown folder." };
            }

            try
            {
                if (select && File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else
                {
                    string folder = select ? Path.GetDirectoryName(path) ?? string.Empty : path;
                    Directory.CreateDirectory(folder);
                    Process.Start("explorer.exe", "\"" + folder + "\"");
                }

                return new Dictionary<string, object> { ["ok"] = true };
            }
            catch (Exception exception)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = exception.Message };
            }
        }

        private static IEnumerable<DriveInfo> SafeDrives()
        {
            DriveInfo[] all;
            try
            {
                all = DriveInfo.GetDrives();
            }
            catch
            {
                yield break;
            }

            foreach (DriveInfo drive in all)
            {
                bool ready;
                try
                {
                    ready = drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable);
                }
                catch
                {
                    ready = false;
                }

                if (ready)
                {
                    yield return drive;
                }
            }
        }

        private static string DriveLabel(DriveInfo drive)
        {
            string name = drive.Name.TrimEnd('\\');
            try
            {
                string label = drive.VolumeLabel;
                long free = drive.AvailableFreeSpace;
                return (string.IsNullOrEmpty(label) ? name : label + "（" + name + "）")
                    + " · 剩余 " + ChartRenderWorkspace.FormatBytes(free);
            }
            catch
            {
                return name;
            }
        }
    }
}
