using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CrowdPlayInstaller
{
    internal static class UninstallerProgram
    {
        private const int MoveFileDelayUntilReboot = 4;
        private const string AllowedPathsResource = "CrowdPlay.AllowedPaths";
        private const int WhCbt = 5;
        private const int HcbtActivate = 5;
        private const uint WmSetIcon = 0x0080;
        private const int IconSmall = 0;
        private const int IconBig = 1;

        private delegate IntPtr HookProc(int code, IntPtr window, IntPtr details);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFile, string newFile, int flags);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback,
            IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code,
            IntPtr window, IntPtr details);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message,
            IntPtr iconType, IntPtr icon);

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length == 2 && args[0] == "--perform")
                {
                    Perform(args[1]);
                    ShowMessage("CrowdPlay was removed from the game folder.",
                        "CrowdPlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    MoveFileEx(Application.ExecutablePath, null, MoveFileDelayUntilReboot);
                    return;
                }
                if (args.Length != 0)
                    throw new InvalidOperationException("Unknown uninstaller command.");
                string root = Path.GetDirectoryName(Application.ExecutablePath);
                InstallManifest manifest = InstallCore.ReadManifest(root);
                if (manifest == null)
                    throw new InvalidOperationException("CrowdPlay.install.xml was not found beside the uninstaller.");
                VerifyAllowedPaths(manifest);
                InstallCore.VerifyOwnedFiles(root, manifest, true);
                string processName = Path.GetFileNameWithoutExtension(InstallCore.GameExe(manifest.Season));
                if (Process.GetProcessesByName(processName).Length != 0)
                    throw new InvalidOperationException("Close the game before removing CrowdPlay.");
                if (ShowMessage("Remove CrowdPlay and its installed files from this game folder?",
                    "Uninstall CrowdPlay", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                string temp = Path.Combine(Path.GetTempPath(),
                    "CrowdPlay-uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Application.ExecutablePath, temp);
                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = temp;
                start.Arguments = "--perform " + Quote(root);
                start.UseShellExecute = false;
                Process.Start(start);
            }
            catch (Exception error)
            {
                ShowMessage(error.Message, "CrowdPlay removal stopped",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static DialogResult ShowMessage(string message, string title,
            MessageBoxButtons buttons, MessageBoxIcon symbol)
        {
            using (Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            {
                if (icon == null)
                    return MessageBox.Show(message, title, buttons, symbol);

                IntPtr hook = IntPtr.Zero;
                HookProc callback = delegate(int code, IntPtr window, IntPtr details)
                {
                    if (code == HcbtActivate)
                    {
                        SendMessage(window, WmSetIcon, new IntPtr(IconBig), icon.Handle);
                        SendMessage(window, WmSetIcon, new IntPtr(IconSmall), icon.Handle);
                    }
                    return CallNextHookEx(hook, code, window, details);
                };
                hook = SetWindowsHookEx(WhCbt, callback, IntPtr.Zero, GetCurrentThreadId());
                if (hook == IntPtr.Zero)
                    return MessageBox.Show(message, title, buttons, symbol);
                try
                {
                    return MessageBox.Show(message, title, buttons, symbol);
                }
                finally
                {
                    UnhookWindowsHookEx(hook);
                    GC.KeepAlive(callback);
                }
            }
        }

        private static string Quote(string argument)
        {
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        private static void Perform(string folder)
        {
            string root = Path.GetFullPath(folder);
            if (!Directory.Exists(root))
                throw new InvalidOperationException("The game folder no longer exists.");
            InstallManifest manifest = InstallCore.ReadManifest(root);
            if (manifest == null)
                throw new InvalidOperationException("CrowdPlay.install.xml was not found in the game folder.");
            VerifyAllowedPaths(manifest);
            InstallCore.VerifyOwnedFiles(root, manifest, true);
#if !TEST_BUILD
            string processName = Path.GetFileNameWithoutExtension(InstallCore.GameExe(manifest.Season));
            if (Process.GetProcessesByName(processName).Length != 0)
                throw new InvalidOperationException("Close the game before removing CrowdPlay.");
#endif
            StopMatchingHost(root, manifest.Season);
            StopMatchingTunnel(root);

            string[] runtime = new string[manifest.RuntimeFiles.Count];
            manifest.RuntimeFiles.CopyTo(runtime);
            foreach (string relative in runtime)
            {
                string target = InstallCore.Resolve(root, relative);
                if (File.Exists(target)) File.Delete(target);
                manifest.RuntimeFiles.Remove(relative);
                SaveRemaining(root, manifest);
                InstallCore.RemoveEmptyParents(root, target);
            }

            // Keep the manifest and the original uninstaller until every other owned file is gone.
            string[] names = new string[manifest.Files.Count];
            manifest.Files.Keys.CopyTo(names, 0);
            foreach (string relative in names)
            {
                if (String.Equals(relative, InstallCore.UninstallerName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string target = InstallCore.Resolve(root, relative);
                File.Delete(target);
                manifest.Files.Remove(relative);
                SaveRemaining(root, manifest);
                InstallCore.RemoveEmptyParents(root, target);
            }

            string original = InstallCore.Resolve(root, InstallCore.UninstallerName);
            bool removed = false;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Delete(original);
                    removed = true;
                    break;
                }
                catch (IOException)
                {
                    Thread.Sleep(250);
                }
            }
            if (!removed)
                throw new IOException("The uninstaller file is still in use. Run it again after the other instance closes.");
            File.Delete(InstallCore.Resolve(root, InstallCore.ManifestName));
        }

        private static void SaveRemaining(string root, InstallManifest manifest)
        {
            string target = InstallCore.Resolve(root, InstallCore.ManifestName);
            string stage = InstallCore.Resolve(root, "CrowdPlay.install.next.xml");
            if (File.Exists(stage))
                throw new InvalidOperationException("A previous uninstallation staging file remains in the game folder.");
            try
            {
                InstallCore.WriteManifest(stage, manifest);
                File.Replace(stage, target, null);
            }
            finally
            {
                if (File.Exists(stage)) File.Delete(stage);
            }
        }

        private static void VerifyAllowedPaths(InstallManifest manifest)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(AllowedPathsResource))
            {
                if (stream == null)
                    throw new InvalidDataException("The uninstaller is missing its owned-file list.");
                System.Collections.Generic.HashSet<string> allowed =
                    new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length != 0)
                            allowed.Add(InstallCore.RelativePath(line));
                    }
                }
                if (!allowed.Contains(InstallCore.UninstallerName))
                    throw new InvalidDataException("The uninstaller's owned-file list is invalid.");
                foreach (string relative in manifest.Files.Keys)
                    if (!allowed.Contains(relative))
                        throw new InvalidDataException("The manifest lists a file this uninstaller never installed: " + relative);
                foreach (string relative in manifest.RuntimeFiles)
                    if (!allowed.Contains(relative))
                        throw new InvalidDataException("The manifest lists an unknown runtime file: " + relative);
            }
        }

        private static void StopMatchingHost(string root, GameSeason season)
        {
            int controlPort = season == GameSeason.Season1 ? 17861 : 17862;
            string baseUrl = "http://127.0.0.1:" + controlPort + "/control/";
            try
            {
                string expected = "INSTANCE|" + InstanceId(root);
                if (Get(baseUrl + "identity") == expected)
                {
                    Post(baseUrl + "shutdown?instance=" + InstanceId(root));
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        Thread.Sleep(250);
                        try
                        {
                            if (Get(baseUrl + "identity") != expected) return;
                        }
                        catch (WebException) { return; }
                    }
                    throw new InvalidOperationException("Close the CrowdPlay host process before uninstalling.");
                }
            }
            catch (WebException) { }
            catch (IOException) { }
        }

        private static void StopMatchingTunnel(string root)
        {
            string ownedPath = Path.GetFullPath(InstallCore.Resolve(root, "cloudflared.exe"));
            foreach (Process process in Process.GetProcessesByName("cloudflared"))
            {
                using (process)
                {
                    string runningPath;
                    try
                    {
                        runningPath = Path.GetFullPath(process.MainModule.FileName);
                    }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception) { continue; }
                    if (!String.Equals(runningPath, ownedPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!process.HasExited)
                    {
                        process.Kill();
                        if (!process.WaitForExit(5000))
                            throw new IOException("Close the CrowdPlay tunnel process before uninstalling.");
                    }
                }
            }
        }

        private static string Get(string url)
        {
            return Request(url, "GET");
        }

        private static string Post(string url)
        {
            return Request(url, "POST");
        }

        private static string Request(string url, string method)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            if (method == "POST") request.ContentLength = 0;
            request.Timeout = 1500;
            request.ReadWriteTimeout = 1500;
            request.Proxy = null;
            using (WebResponse response = request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        private static string InstanceId(string root)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
                StringBuilder text = new StringBuilder();
                foreach (byte value in digest) text.Append(value.ToString("x2"));
                return text.ToString().Substring(0, 20);
            }
        }
    }
}
