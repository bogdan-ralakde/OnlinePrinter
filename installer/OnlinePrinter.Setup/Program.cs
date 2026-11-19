using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OnlinePrinter.Setup;

/// <summary>
/// Single-EXE installer for the Online Printer Client. No PowerShell, CMD or batch files.
/// The published client executable is embedded as a resource.
/// </summary>
internal static class Program
{
    private const string AppName = "Online Printer Client";
    private const string Publisher = "Online Printer";
    private const string ClientExe = "OnlinePrinter.Client.exe";
    private const string SetupExe = "OnlinePrinterClient-Setup.exe";
    private const string StartMenuFolder = "Online Printer";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OnlinePrinterClient";
    private const string PayloadResource = "OnlinePrinter.Setup.Client.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        var silent = Has(args, "--silent") || Has(args, "--quiet");

        if (Has(args, "--uninstall"))
            return Uninstall(silent);

        var dir = GetValue(args, "--dir") ?? GetInstalledDir() ?? DefaultInstallDir();
        var launch = Has(args, "--launch");
        var desktop = !Has(args, "--no-desktop");

        if (silent)
            return Install(dir, launch, desktop, true);

        System.Windows.Forms.Application.Run(new SetupForm(dir, desktop, launch));
        return 0;
    }

    private static int Install(string dir, bool launch, bool desktop, bool silent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) dir = DefaultInstallDir();
            Directory.CreateDirectory(dir);

            using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource);
            if (payload == null)
                return Fail("This setup file is missing the client payload. Download the installer again from the GitHub release.", silent);

            // Allow in-place upgrade while the previous client is running.
            foreach (var p in Process.GetProcessesByName("OnlinePrinter.Client"))
            {
                try { p.Kill(); } catch { }
            }

            var target = Path.Combine(dir, ClientExe);
            using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                payload.CopyTo(fs);

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.1.1";
            File.WriteAllText(Path.Combine(dir, "VERSION.txt"), AppName + " " + version + Environment.NewLine);

            // Keep the setup next to the app so Apps & features / Add or remove programs keeps working.
            var setupCopy = Path.Combine(dir, SetupExe);
            var ownPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(ownPath) &&
                !string.Equals(Path.GetFullPath(ownPath), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(ownPath, setupCopy, true);
            }
            else if (!File.Exists(setupCopy) && !string.IsNullOrWhiteSpace(ownPath))
            {
                File.Copy(ownPath, setupCopy, true);
            }

            CreateShortcuts(dir, target, desktop);
            WriteUninstallKey(dir, version, setupCopy);

            if (launch)
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = dir });
            }

            if (!silent)
            {
                System.Windows.Forms.MessageBox.Show(
                    AppName + " installed to:\n" + dir +
                    "\n\nNext: start the client, enter the server URL, request approval, and use the Virtual Printer tab to install/repair the local 'Online Printer' printer.",
                    AppName + " - Setup",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            return Fail("Installation failed:\n" + ex.Message, silent);
        }
    }

    private static int Uninstall(bool silent)
    {
        try
        {
            if (!silent &&
                System.Windows.Forms.MessageBox.Show("Remove " + AppName + " from this computer?", AppName + " - Uninstall",
                    System.Windows.Forms.MessageBoxButtons.YesNo, System.Windows.Forms.MessageBoxIcon.Question) != System.Windows.Forms.DialogResult.Yes)
                return 0;

            foreach (var p in Process.GetProcessesByName("OnlinePrinter.Client"))
            {
                try { p.Kill(); } catch { }
            }

            var dir = GetInstalledDir();
            RemoveShortcuts();
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                foreach (var file in new[] { ClientExe, SetupExe, "VERSION.txt" })
                {
                    try
                    {
                        var path = Path.Combine(dir, file);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }

            using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey, true))
            {
                if (key != null) Registry.LocalMachine.DeleteSubKey(UninstallKey, false);
            }

            if (!silent)
            {
                System.Windows.Forms.MessageBox.Show(AppName + " was removed.", AppName + " - Uninstall",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            return Fail("Uninstall failed:\n" + ex.Message, silent);
        }
    }

    private static int Fail(string message, bool silent)
    {
        if (silent)
            Console.Error.WriteLine(message);
        else
            System.Windows.Forms.MessageBox.Show(message, AppName + " - Setup",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        return 1;
    }

    private static void CreateShortcuts(string dir, string target, bool desktop)
    {
        var programs = ShortcutFolder(Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs);
        var smDir = Path.Combine(programs, StartMenuFolder);
        try { Directory.CreateDirectory(smDir); } catch { return; }
        CreateShortcut(Path.Combine(smDir, AppName + ".lnk"), target, "Launch " + AppName, dir);

        if (desktop)
        {
            var desktopDir = ShortcutFolder(Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.DesktopDirectory);
            CreateShortcut(Path.Combine(desktopDir, AppName + ".lnk"), target, "Launch " + AppName, dir);
        }
    }

    private static void RemoveShortcuts()
    {
        var programs = ShortcutFolder(Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs);
        TryDelete(Path.Combine(programs, StartMenuFolder, AppName + ".lnk"));
        try
        {
            var smDir = Path.Combine(programs, StartMenuFolder);
            if (Directory.Exists(smDir) && !Directory.EnumerateFileSystemEntries(smDir).Any()) Directory.Delete(smDir);
        }
        catch { }

        var desktopDir = ShortcutFolder(Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.DesktopDirectory);
        TryDelete(Path.Combine(desktopDir, AppName + ".lnk"));
    }

    private static string ShortcutFolder(Environment.SpecialFolder common, Environment.SpecialFolder fallback)
    {
        var path = Environment.GetFolderPath(common);
        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetFolderPath(fallback);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void WriteUninstallKey(string dir, string version, string setupCopy)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", "\"" + setupCopy + "\" --uninstall");
        key.SetValue("QuietUninstallString", "\"" + setupCopy + "\" --uninstall --silent");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static string? GetInstalledDir()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(UninstallKey);
                var value = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }
        }
        return null;
    }

    private static string DefaultInstallDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Online Printer");

    private static bool Has(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1].Trim('"');
        }
        return null;
    }

    private static void CreateShortcut(string linkPath, string target, string description, string workingDir)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(linkPath, true);
            if (link is IDisposable d) d.Dispose();
        }
        catch
        {
            // Shortcut creation is best-effort; the app itself is already installed.
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }

    private sealed class SetupForm : System.Windows.Forms.Form
    {
        private readonly System.Windows.Forms.TextBox _dir;
        private readonly System.Windows.Forms.CheckBox _desktop;
        private readonly System.Windows.Forms.CheckBox _launch;

        public SetupForm(string dir, bool desktop, bool launch)
        {
            Text = AppName + " - Setup";
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new System.Drawing.Size(560, 250);
            Font = new System.Drawing.Font("Segoe UI", 9F);

            var intro = new System.Windows.Forms.Label
            {
                Text = "Install " + AppName + " on this computer",
                Location = new System.Drawing.Point(20, 18),
                AutoSize = true,
                Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold)
            };
            var dirLabel = new System.Windows.Forms.Label
            {
                Text = "Destination folder:",
                Location = new System.Drawing.Point(20, 62),
                AutoSize = true
            };
            _dir = new System.Windows.Forms.TextBox
            {
                Text = dir,
                Location = new System.Drawing.Point(20, 82),
                Size = new System.Drawing.Size(410, 25)
            };
            var browse = new System.Windows.Forms.Button
            {
                Text = "Browse...",
                Location = new System.Drawing.Point(440, 81),
                Size = new System.Drawing.Size(100, 27)
            };
            browse.Click += (_, _) =>
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _dir.Text };
                if (dlg.ShowDialog(this) == System.Windows.Forms.DialogResult.OK) _dir.Text = dlg.SelectedPath;
            };

            _desktop = new System.Windows.Forms.CheckBox
            {
                Text = "Create a desktop shortcut",
                Checked = desktop,
                Location = new System.Drawing.Point(20, 125),
                AutoSize = true
            };
            _launch = new System.Windows.Forms.CheckBox
            {
                Text = "Launch " + AppName + " after installation",
                Checked = launch,
                Location = new System.Drawing.Point(20, 150),
                AutoSize = true
            };

            var install = new System.Windows.Forms.Button
            {
                Text = "Install",
                Location = new System.Drawing.Point(360, 200),
                Size = new System.Drawing.Size(90, 30)
            };
            install.Click += (_, _) =>
            {
                var result = Install(_dir.Text.Trim(), _launch.Checked, _desktop.Checked, false);
                if (result == 0) { DialogResult = System.Windows.Forms.DialogResult.OK; Close(); }
            };
            var cancel = new System.Windows.Forms.Button
            {
                Text = "Cancel",
                Location = new System.Drawing.Point(460, 200),
                Size = new System.Drawing.Size(80, 30),
                DialogResult = System.Windows.Forms.DialogResult.Cancel
            };

            Controls.Add(intro);
            Controls.Add(dirLabel);
            Controls.Add(_dir);
            Controls.Add(browse);
            Controls.Add(_desktop);
            Controls.Add(_launch);
            Controls.Add(install);
            Controls.Add(cancel);
            AcceptButton = install;
            CancelButton = cancel;
        }
    }
}
