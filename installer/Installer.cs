using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CrowdPlayInstaller
{
    internal static class InstallerProgram
    {
        private const string PayloadResource1 = "CrowdPlay.Payload1";
        private const string PayloadResource2 = "CrowdPlay.Payload2";
        private const string PatchToolsResource = "CrowdPlay.PatchTools";
        private const string UninstallerResource = "CrowdPlay.Uninstaller";
        private const string BannerResource1 = "CrowdPlay.Banner1";
        private const string BannerResource2 = "CrowdPlay.Banner2";
        private const uint ShellUpdateItem = 0x00002000;
        private const uint ShellPathWide = 0x0005;
        private const uint ShellFlush = 0x1000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags,
            string item1, IntPtr item2);

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerForm());
        }

        internal static void Install(string folder, GameSeason season)
        {
            string root = InstallCore.ValidateGameFolder(folder, season);
#if !TEST_BUILD
            string processName = Path.GetFileNameWithoutExtension(InstallCore.GameExe(season));
            if (Process.GetProcessesByName(processName).Length != 0)
                throw new InvalidOperationException("Close the game before installing CrowdPlay.");
#endif

            // The game-specific archives are reconstructed from this installation's
            // verified original archives before anything in the game folder changes.
            Dictionary<string, byte[]> package = LoadPayload(root, season);
            if (package.Count == 0)
                throw new InvalidDataException("The installer has no mod files in its payload.");
            package.Add(InstallCore.UninstallerName, ReadResource(UninstallerResource));

            InstallManifest previous = InstallCore.ReadManifest(root);
            if (previous != null && previous.Season != season)
                throw new InvalidOperationException("A CrowdPlay manifest for the other season is already in this folder.");
            InstallCore.VerifyOwnedFiles(root, previous, false);

            foreach (string relative in package.Keys)
            {
                string target = InstallCore.Resolve(root, relative);
                if (Directory.Exists(target))
                    throw new InvalidOperationException("A folder occupies the mod file path: " + relative);
                if (File.Exists(target) && (previous == null || !previous.Files.ContainsKey(relative)))
                    throw new InvalidOperationException("A non-CrowdPlay file already exists: " + relative +
                        ". Installation stopped before changing the game.");
            }

            string[] runtimePaths = new string[] { @"CrowdPlayLAN\host-settings.xml",
                @"CrowdPlayLAN\host-settings.next.xml",
                @"CrowdPlayLAN\debug-report.txt" };
            foreach (string relative in runtimePaths)
            {
                string target = InstallCore.Resolve(root, relative);
                if (File.Exists(target) && (previous == null || !previous.RuntimeFiles.Contains(relative)))
                    throw new InvalidOperationException("A non-CrowdPlay settings file already exists: " + relative);
            }

            InstallManifest next = new InstallManifest();
            next.Season = season;
            foreach (KeyValuePair<string, byte[]> entry in package)
                next.Files.Add(entry.Key, new InstalledFile {
                    RelativePath = entry.Key, Sha256 = InstallCore.HashBytes(entry.Value) });
            foreach (string relative in runtimePaths) next.RuntimeFiles.Add(relative);

            string transaction = Path.Combine(root, "CrowdPlay.install.txn." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transaction);
            Dictionary<string, string> oldBackups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string oldManifestBackup = null;
            bool beganChanges = false;
            try
            {
                int index = 0;
                if (previous != null)
                {
                    foreach (InstalledFile old in previous.Files.Values)
                    {
                        string backup = Path.Combine(transaction, "old_" + index++.ToString("D8"));
                        File.Copy(InstallCore.Resolve(root, old.RelativePath), backup);
                        oldBackups.Add(old.RelativePath, backup);
                    }
                    oldManifestBackup = Path.Combine(transaction, "old_manifest.xml");
                    File.Copy(InstallCore.Resolve(root, InstallCore.ManifestName), oldManifestBackup);
                }
                index = 0;
                foreach (KeyValuePair<string, byte[]> entry in package)
                {
                    string stage = Path.Combine(transaction, "new_" + index++.ToString("D8"));
                    File.WriteAllBytes(stage, entry.Value);
                    staged.Add(entry.Key, stage);
                }
                string newManifestStage = Path.Combine(transaction, "new_manifest.xml");
                InstallCore.WriteManifest(newManifestStage, next);

                beganChanges = true;
                if (previous != null)
                {
                    foreach (InstalledFile old in previous.Files.Values)
                        File.Delete(InstallCore.Resolve(root, old.RelativePath));
                    File.Delete(InstallCore.Resolve(root, InstallCore.ManifestName));
                }
                foreach (KeyValuePair<string, string> entry in staged)
                {
                    string target = InstallCore.Resolve(root, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(entry.Value, target);
                }
                File.Move(newManifestStage, InstallCore.Resolve(root, InstallCore.ManifestName));
                if (previous != null)
                {
                    foreach (InstalledFile old in previous.Files.Values)
                        if (!package.ContainsKey(old.RelativePath))
                            InstallCore.RemoveEmptyParents(root, InstallCore.Resolve(root, old.RelativePath));
                }
            }
            catch
            {
                if (beganChanges)
                {
                    foreach (string relative in package.Keys)
                    {
                        string target = InstallCore.Resolve(root, relative);
                        if (File.Exists(target)) File.Delete(target);
                    }
                    foreach (KeyValuePair<string, string> old in oldBackups)
                    {
                        string target = InstallCore.Resolve(root, old.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(old.Value, target, true);
                    }
                    string manifestPath = InstallCore.Resolve(root, InstallCore.ManifestName);
                    if (File.Exists(manifestPath)) File.Delete(manifestPath);
                    if (oldManifestBackup != null) File.Copy(oldManifestBackup, manifestPath);
                    foreach (string relative in package.Keys)
                        InstallCore.RemoveEmptyParents(root, InstallCore.Resolve(root, relative));
                }
                throw;
            }
            finally
            {
                string checkedTransaction = Path.GetFullPath(transaction);
                string checkedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                if (!checkedTransaction.StartsWith(checkedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The transaction folder is outside the game folder.");
                if (Directory.Exists(checkedTransaction)) Directory.Delete(checkedTransaction, true);
            }
            try
            {
                SHChangeNotify(ShellUpdateItem, ShellPathWide | ShellFlush,
                    InstallCore.Resolve(root, InstallCore.UninstallerName), IntPtr.Zero);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static Dictionary<string, byte[]> LoadPayload(string gameRoot, GameSeason season)
        {
            Dictionary<string, byte[]> result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            AddPackageFile(result, @"CrowdPlayLAN\assets\VotingBanner.png",
                ReadVotingBanner(season));
            string tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string tempRoot = Path.Combine(tempParent, "CrowdPlay-patch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                string payloadRoot = Path.Combine(tempRoot, "payload");
                string toolsRoot = Path.Combine(tempRoot, "tools");
                string rebuiltRoot = Path.Combine(tempRoot, "rebuilt");
                Directory.CreateDirectory(payloadRoot);
                Directory.CreateDirectory(toolsRoot);
                Directory.CreateDirectory(rebuiltRoot);

                ExtractEmbeddedZip(season == GameSeason.Season1 ? PayloadResource1 : PayloadResource2,
                    payloadRoot, 1024L * 1024 * 1024);
                ExtractEmbeddedZip(PatchToolsResource, toolsRoot, 512L * 1024 * 1024);

                string installRoot = Path.Combine(payloadRoot, "install");
                string patchesRoot = Path.Combine(payloadRoot, "patches");
                if (!Directory.Exists(installRoot) || !File.Exists(Path.Combine(patchesRoot, "patch-manifest.json")))
                    throw new InvalidDataException("The installer patch payload is incomplete.");

                string python = Path.Combine(toolsRoot, "python", "python.exe");
                string helper = Path.Combine(toolsRoot, "apply_patches.py");
                string xdelta = Path.Combine(toolsRoot, "python", "xdelta3.exe");
                if (!File.Exists(python) || !File.Exists(helper) || !File.Exists(xdelta))
                    throw new InvalidDataException("The installer is missing its local patch tools.");
                RebuildGameSidecars(python, helper, xdelta, gameRoot, season, payloadRoot, rebuiltRoot);

                AddInstalledTree(result, installRoot);
                string[] sidecars = season == GameSeason.Season1
                    ? new string[] {
                        @"Archives\MCSM_pc_CrowdPlayLAN_Project.ttarch2",
                        @"Archives\MCSM_pc_CrowdPlayLAN_Menu.ttarch2",
                        @"Archives\_resdesc_50_CrowdPlayLAN_Project.lua",
                        @"Archives\_resdesc_50_CrowdPlayLAN_Menu.lua" }
                    : new string[] {
                        @"Archives\MC2_pc_CrowdPlayLAN_Project.ttarch2",
                        @"Archives\MC2_pc_CrowdPlayLAN_Menu.ttarch2",
                        @"Archives\_resdesc_50_CrowdPlayLAN_Project.lua",
                        @"Archives\_resdesc_50_CrowdPlayLAN_Menu.lua" };
                foreach (string relative in sidecars)
                {
                    string source = InstallCore.Resolve(rebuiltRoot, relative);
                    if (!File.Exists(source))
                        throw new InvalidDataException("Local patching did not create " + relative + ".");
                    AddPackageFile(result, relative, File.ReadAllBytes(source));
                }
                string cursorRelative = @"CrowdPlayLAN\assets\StoryModeCursor.png";
                string cursorSource = InstallCore.Resolve(rebuiltRoot, cursorRelative);
                if (!File.Exists(cursorSource))
                    throw new InvalidDataException("Local patching did not create the voting cursor.");
                AddPackageFile(result, cursorRelative, File.ReadAllBytes(cursorSource));
            }
            finally
            {
                string checkedRoot = Path.GetFullPath(tempRoot);
                if (!checkedRoot.StartsWith(tempParent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                    (Directory.Exists(checkedRoot) &&
                        (File.GetAttributes(checkedRoot) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("The temporary patch folder is unsafe to clean up.");
                if (Directory.Exists(checkedRoot)) Directory.Delete(checkedRoot, true);
            }
            return result;
        }

        private static byte[] ReadVotingBanner(GameSeason season)
        {
            string name = season == GameSeason.Season1 ? "S1_Banner.png" : "S2_Banner.png";
            string expected = season == GameSeason.Season1
                ? "3166B641257DAB0E2A42537E9956F1E8934EE736D6BCCFE47FDE80FEA60BB658"
                : "56FE183A7E43FBBEA74D2F14BC9C2FFA8085D4AF14FF79BD7D5DA6DA8164103F";
            byte[] bytes = ReadResource(season == GameSeason.Season1 ? BannerResource1 : BannerResource2);
            if (!String.Equals(InstallCore.HashBytes(bytes), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded " + name + " differs from the tested CrowdPlay banner.");
            return bytes;
        }

        private static void AddInstalledTree(Dictionary<string, byte[]> result, string installRoot)
        {
            string checkedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
            long totalLength = 0;
            foreach (string file in Directory.GetFiles(checkedRoot, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The installer payload contains a linked file.");
                string relative = file.Substring(checkedRoot.Length + 1);
                string checkedPath = InstallCore.RelativePath(relative);
                if (checkedPath.StartsWith(@"Archives\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Game sidecar archives must be rebuilt from the local game.");
                FileInfo info = new FileInfo(file);
                totalLength += info.Length;
                if (info.Length > 512L * 1024 * 1024 || totalLength > 1024L * 1024 * 1024)
                    throw new InvalidDataException("The mod payload is unexpectedly large.");
                AddPackageFile(result, checkedPath, File.ReadAllBytes(file));
            }
        }

        private static void AddPackageFile(Dictionary<string, byte[]> result, string relative, byte[] bytes)
        {
            string checkedPath = InstallCore.RelativePath(relative);
            if (String.Equals(checkedPath, InstallCore.ManifestName, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(checkedPath, InstallCore.UninstallerName, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(checkedPath, @"CrowdPlayLAN\host-settings.xml", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(checkedPath, @"CrowdPlayLAN\host-settings.next.xml", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(checkedPath, @"CrowdPlayLAN\debug-report.txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The payload uses a reserved installer file name.");
            if (result.ContainsKey(checkedPath))
                throw new InvalidDataException("The payload has duplicate file paths: " + checkedPath);
            result.Add(checkedPath, bytes);
        }

        private static void ExtractEmbeddedZip(string resource, string destination, long maxTotalBytes)
        {
            using (MemoryStream stream = new MemoryStream(ReadResource(resource), false))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                long total = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Name.Length == 0) continue;
                    string relative = InstallCore.RelativePath(entry.FullName);
                    if (entry.Length < 0 || entry.Length > 512L * 1024 * 1024)
                        throw new InvalidDataException("An installer resource is unexpectedly large.");
                    total += entry.Length;
                    if (total > maxTotalBytes)
                        throw new InvalidDataException("An installer resource is unexpectedly large.");
                    string target = InstallCore.Resolve(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    {
                        input.CopyTo(output);
                        if (output.Length != entry.Length)
                            throw new InvalidDataException("An installer resource was truncated.");
                    }
                }
            }
        }

        private static void RebuildGameSidecars(string python, string helper, string xdelta,
            string gameRoot, GameSeason season, string payloadRoot, string outputRoot)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = python;
            start.Arguments = QuoteArgument(helper) + " --game-root " + QuoteArgument(gameRoot) +
                " --season " + (season == GameSeason.Season1 ? "s1" : "s2") +
                " --payload-dir " + QuoteArgument(payloadRoot) +
                " --output-dir " + QuoteArgument(outputRoot) +
                " --xdelta " + QuoteArgument(xdelta);
            start.WorkingDirectory = Path.GetDirectoryName(helper);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
            start.EnvironmentVariables.Remove("PYTHONHOME");
            start.EnvironmentVariables.Remove("PYTHONPATH");
            string temporaryWork = Path.GetDirectoryName(payloadRoot);
            start.EnvironmentVariables["TMPDIR"] = temporaryWork;
            start.EnvironmentVariables["TEMP"] = temporaryWork;
            start.EnvironmentVariables["TMP"] = temporaryWork;
            StringBuilder output = new StringBuilder();
            object outputLock = new object();
            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock (outputLock) {
                        if (output.Length < 8192) output.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock (outputLock) {
                        if (output.Length < 8192) output.AppendLine(e.Data);
                    }
                };
                if (!process.Start())
                    throw new InvalidOperationException("The local patch tool could not start.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (!process.WaitForExit(600000))
                {
                    process.Kill();
                    process.WaitForExit();
                    throw new TimeoutException("Reconstructing the local game archives took too long.");
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    string message;
                    lock (outputLock) message = output.ToString().Trim();
                    throw new InvalidOperationException("Local game patching failed: " + message);
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value.IndexOf('"') >= 0)
                throw new InvalidDataException("A patch tool path contains an invalid quote.");
            return "\"" + value + "\"";
        }

        private static byte[] ReadResource(string name)
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (input == null)
                    throw new InvalidDataException("The installer is missing its embedded " + name + " resource.");
                using (MemoryStream output = new MemoryStream())
                {
                    input.CopyTo(output);
                    return output.ToArray();
                }
            }
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly ComboBox seasonChoice = new ComboBox();
        private readonly TextBox pathBox = new TextBox();
        private readonly Button installButton = new Button();

        internal InstallerForm()
        {
            Text = "CrowdPlay Installer";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(525, 225);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);

            Label intro = new Label();
            intro.Text = "Install CrowdPlay into one Minecraft: Story Mode season.";
            intro.SetBounds(20, 18, 480, 25);
            Controls.Add(intro);

            Label seasonLabel = new Label();
            seasonLabel.Text = "Season";
            seasonLabel.SetBounds(20, 58, 85, 25);
            Controls.Add(seasonLabel);
            seasonChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            seasonChoice.Items.Add("Season 1");
            seasonChoice.Items.Add("Season 2");
            seasonChoice.SelectedIndex = 0;
            seasonChoice.SetBounds(105, 55, 170, 25);
            Controls.Add(seasonChoice);

            Label pathLabel = new Label();
            pathLabel.Text = "Game folder";
            pathLabel.SetBounds(20, 99, 85, 25);
            Controls.Add(pathLabel);
            pathBox.SetBounds(105, 96, 315, 25);
            pathBox.Leave += delegate { SelectSeasonFromFolder(); };
            Controls.Add(pathBox);
            Button browse = new Button();
            browse.Text = "Browse";
            browse.SetBounds(427, 95, 78, 28);
            browse.Click += BrowseClicked;
            Controls.Add(browse);

            Label help = new Label();
            help.Text = "Run this installer again and select the other season to install both.";
            help.SetBounds(20, 139, 485, 28);
            Controls.Add(help);
            installButton.Text = "Install CrowdPlay";
            installButton.SetBounds(352, 175, 153, 32);
            installButton.Click += InstallClicked;
            Controls.Add(installButton);
            AcceptButton = installButton;
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the folder containing the selected game's EXE and Archives folder.";
                if (Directory.Exists(pathBox.Text)) dialog.SelectedPath = pathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    pathBox.Text = dialog.SelectedPath;
                    SelectSeasonFromFolder();
                }
            }
        }

        private void SelectSeasonFromFolder()
        {
            GameSeason? detected = InstallCore.DetectGameSeason(pathBox.Text.Trim());
            if (detected.HasValue)
                seasonChoice.SelectedIndex = detected.Value == GameSeason.Season1 ? 0 : 1;
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            installButton.Enabled = false;
            UseWaitCursor = true;
            bool installed = false;
            try
            {
                SelectSeasonFromFolder();
                GameSeason season = seasonChoice.SelectedIndex == 0 ? GameSeason.Season1 : GameSeason.Season2;
                InstallerProgram.Install(pathBox.Text, season);
                MessageBox.Show(this,
                    "CrowdPlay was installed. Settings will be available in the game. " +
                    "UninstallCrowdPlay.exe was placed in the game folder.",
                    "CrowdPlay", MessageBoxButtons.OK, MessageBoxIcon.Information);
                installed = true;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "CrowdPlay installation stopped",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                installButton.Enabled = true;
            }
            if (installed) Close();
        }
    }
}
