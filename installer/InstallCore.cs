using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace CrowdPlayInstaller
{
    internal enum GameSeason { Season1, Season2 }

    internal sealed class InstalledFile
    {
        internal string RelativePath;
        internal string Sha256;
    }

    internal sealed class InstallManifest
    {
        internal GameSeason Season;
        internal readonly Dictionary<string, InstalledFile> Files =
            new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> RuntimeFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class InstallCore
    {
        internal const string ManifestName = "CrowdPlay.install.xml";
        internal const string UninstallerName = "UninstallCrowdPlay.exe";

        internal static string GameExe(GameSeason season)
        {
            return season == GameSeason.Season1 ? "GameApp.exe" : "Minecraft2.exe";
        }

        internal static GameSeason? DetectGameSeason(string folder)
        {
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            bool season1 = File.Exists(Path.Combine(folder, GameExe(GameSeason.Season1)));
            bool season2 = File.Exists(Path.Combine(folder, GameExe(GameSeason.Season2)));
            if (season1 == season2) return null;
            return season1 ? GameSeason.Season1 : GameSeason.Season2;
        }

        internal static string ValidateGameFolder(string folder, GameSeason season)
        {
            if (String.IsNullOrWhiteSpace(folder))
                throw new InvalidOperationException("Choose the game's installation folder.");
            string root = Path.GetFullPath(folder.Trim());
            if (!Directory.Exists(root))
                throw new InvalidOperationException("The selected folder does not exist.");
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The selected folder is a link or junction. Choose the real game folder.");
            if (!File.Exists(Path.Combine(root, GameExe(season))))
            {
                GameSeason? detected = DetectGameSeason(root);
                if (detected.HasValue)
                    throw new InvalidOperationException("This folder contains " + GameExe(detected.Value) +
                        " for " + (detected.Value == GameSeason.Season1 ? "Season 1" : "Season 2") +
                        ". Select that season in the installer.");
                throw new InvalidOperationException("This folder does not contain " + GameExe(season) +
                    " for the selected season.");
            }
            if (!Directory.Exists(Path.Combine(root, "Archives")))
                throw new InvalidOperationException("This folder does not contain an Archives directory.");
#if !TEST_BUILD
            ValidateSupportedBuild(root, season);
#endif
            return root;
        }

#if !TEST_BUILD
        private static void ValidateSupportedBuild(string root, GameSeason season)
        {
            string[] files;
            string[] hashes;
            if (season == GameSeason.Season1)
            {
                files = new string[] { "GameApp.exe", "Archives\\MCSM_pc_Project_data.ttarch2",
                    "Archives\\MCSM_pc_Project_ms.ttarch2", "Archives\\MCSM_pc_Menu_data.ttarch2",
                    "Archives\\MCSM_pc_Menu_ms.ttarch2" };
                hashes = new string[] {
                    "6987132C74E72FB015F38E12A9DCABCD526E63361E2E1C41A91E2EA2DE5D8FC4",
                    "F2D981A9B7F313C613712E5BE665CE24E700575EBE744FFBE6C9CFF67C818E37",
                    "294A8208D572459C5C4B044BAF92E8A3624B08CA8721DFD24538A1F1F7472FE8",
                    "98E8C7CD5B78453A80FDAE3FEE03B715F43CE590D0D7701B67C69B861DEC8240",
                    "C089C62CB293A19F94D32AB5D634BAFF474DCBF69CACBF14AC8C71E058F5BF85" };
            }
            else
            {
                files = new string[] { "Minecraft2.exe", "Archives\\MC2_pc_Project_data.ttarch2",
                    "Archives\\MC2_pc_Project_ms.ttarch2", "Archives\\MC2_pc_Menu_data.ttarch2",
                    "Archives\\MC2_pc_Menu_ms.ttarch2" };
                hashes = new string[] {
                    "CBD6CC9761919A94D82C098642B3656EE2D4BEF0BD45746B1E3B98DA5F12C4BA",
                    "58168BDDBB129133A3867AA59FBC0E3CA3E87D55BB7D219B1937ECD5B92A436B",
                    "6AF5E500C8E3E2D2650A61F40D29A005BA79DE3923B1D174B43170DA92C7A4F6",
                    "AED3E394E607835002F405E9D85A4308B200B8EFC3C1E5114A4DCB7C97DAA829",
                    "DB161C7682FB143CF85124F31F2A9520D303E3893B0EBDF0726043DE5FA698E5" };
            }
            for (int i = 0; i < files.Length; i++)
            {
                string path = Resolve(root, files[i]);
                if (!File.Exists(path) || !String.Equals(HashFile(path), hashes[i], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsupported game build. " + files[i] +
                        " does not match the tested GOG release. No files were changed.");
            }
        }
#endif

        internal static string RelativePath(string candidate)
        {
            if (String.IsNullOrEmpty(candidate))
                throw new InvalidDataException("A package path is empty.");
            string value = candidate.Replace('/', '\\');
            if (Path.IsPathRooted(value) || value[0] == '\\' || value.IndexOf(':') >= 0)
                throw new InvalidDataException("A package path is absolute: " + candidate);
            string[] parts = value.Split('\\');
            if (parts.Length == 0)
                throw new InvalidDataException("A package path is empty.");
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." ||
                    part[part.Length - 1] == '.' || part[part.Length - 1] == ' ' ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Unsafe package path: " + candidate);
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem == "COM1" || stem == "COM2" || stem == "COM3" || stem == "COM4" ||
                    stem == "COM5" || stem == "COM6" || stem == "COM7" || stem == "COM8" || stem == "COM9" ||
                    stem == "LPT1" || stem == "LPT2" || stem == "LPT3" || stem == "LPT4" ||
                    stem == "LPT5" || stem == "LPT6" || stem == "LPT7" || stem == "LPT8" || stem == "LPT9")
                    throw new InvalidDataException("Unsafe package path: " + candidate);
            }
            return String.Join("\\", parts);
        }

        internal static string Resolve(string root, string relative)
        {
            string checkedPath = RelativePath(relative);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(fullRoot, checkedPath));
            if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package path leaves the game folder: " + relative);
            string current = fullRoot;
            string[] parts = checkedPath.Split('\\');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = Path.Combine(current, parts[i]);
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A destination folder is a link or junction: " + current);
            }
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A destination file is a link: " + full);
            return full;
        }

        internal static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return Hex(sha.ComputeHash(bytes));
        }

        internal static string HashFile(string file)
        {
            using (FileStream input = File.OpenRead(file))
            using (SHA256 sha = SHA256.Create())
                return Hex(sha.ComputeHash(input));
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
                result.Append(value.ToString("x2"));
            return result.ToString();
        }

        internal static InstallManifest ReadManifest(string root)
        {
            string path = Resolve(root, ManifestName);
            if (!File.Exists(path))
                return null;
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(path, settings))
                doc.Load(reader);
            XmlElement top = doc.DocumentElement;
            if (top == null || top.Name != "CrowdPlayInstall" || top.GetAttribute("version") != "1")
                throw new InvalidDataException("The CrowdPlay install manifest is not recognized.");
            string seasonText = top.GetAttribute("season");
            InstallManifest manifest = new InstallManifest();
            if (seasonText == "1") manifest.Season = GameSeason.Season1;
            else if (seasonText == "2") manifest.Season = GameSeason.Season2;
            else throw new InvalidDataException("The CrowdPlay install manifest has an invalid season.");
            foreach (XmlNode node in top.ChildNodes)
            {
                XmlElement entry = node as XmlElement;
                if (entry == null || (entry.Name != "File" && entry.Name != "RuntimeFile"))
                    throw new InvalidDataException("The CrowdPlay install manifest has an invalid entry.");
                string relative = RelativePath(entry.GetAttribute("path"));
                if (entry.Name == "RuntimeFile")
                {
                    if (!manifest.RuntimeFiles.Add(relative) || manifest.Files.ContainsKey(relative) ||
                        String.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The CrowdPlay install manifest has an invalid runtime file record.");
                    continue;
                }
                string hash = entry.GetAttribute("sha256").ToLowerInvariant();
                if (hash.Length != 64 || !IsHex(hash) ||
                    String.Equals(relative, ManifestName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The CrowdPlay install manifest has an invalid file record.");
                if (manifest.Files.ContainsKey(relative) || manifest.RuntimeFiles.Contains(relative))
                    throw new InvalidDataException("The CrowdPlay install manifest has duplicate files.");
                manifest.Files.Add(relative, new InstalledFile { RelativePath = relative, Sha256 = hash });
            }
            if (!manifest.Files.ContainsKey(UninstallerName))
                throw new InvalidDataException("The CrowdPlay install manifest does not list the uninstaller.");
            return manifest;
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    return false;
            return true;
        }

        internal static void VerifyOwnedFiles(string root, InstallManifest manifest, bool allowMissing)
        {
            if (manifest == null) return;
            foreach (InstalledFile record in manifest.Files.Values)
            {
                string full = Resolve(root, record.RelativePath);
                if (!File.Exists(full) && allowMissing) continue;
                if (!File.Exists(full) || !String.Equals(HashFile(full), record.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An installed CrowdPlay file has changed: " + record.RelativePath +
                        ". Keep it and reinstall only after resolving the change manually.");
            }
        }

        internal static void WriteManifest(string path, InstallManifest manifest)
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.Encoding = new UTF8Encoding(false);
            using (XmlWriter writer = XmlWriter.Create(path, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("CrowdPlayInstall");
                writer.WriteAttributeString("version", "1");
                writer.WriteAttributeString("season", manifest.Season == GameSeason.Season1 ? "1" : "2");
                List<string> names = new List<string>(manifest.Files.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    InstalledFile record = manifest.Files[name];
                    writer.WriteStartElement("File");
                    writer.WriteAttributeString("path", record.RelativePath);
                    writer.WriteAttributeString("sha256", record.Sha256);
                    writer.WriteEndElement();
                }
                List<string> runtimeNames = new List<string>(manifest.RuntimeFiles);
                runtimeNames.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in runtimeNames)
                {
                    writer.WriteStartElement("RuntimeFile");
                    writer.WriteAttributeString("path", name);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        internal static void RemoveEmptyParents(string root, string file)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string current = Path.GetDirectoryName(file);
            while (current != null && !String.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                current.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current) || Directory.GetFileSystemEntries(current).Length != 0)
                    break;
                Directory.Delete(current);
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
