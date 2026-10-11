using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

namespace CrowdPlayHost
{
    internal sealed class HostSettings
    {
        internal int Port = 26844;
        internal int DefaultDurationMs = 15000;
        internal bool ShowCounts = true;
        internal bool HostDecides = false;
        internal bool Overlay = true;

        internal static HostSettings Load(string root, bool seasonTwo)
        {
            HostSettings settings = new HostSettings();
            if (seasonTwo) settings.Port = 26845;
            string path = Path.Combine(root, "CrowdPlayLAN", "host-settings.xml");
            if (!File.Exists(path)) return settings;
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(path,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(reader);
            XmlElement top = doc.DocumentElement;
            if (top == null || top.Name != "CrowdPlayHostSettings" || top.GetAttribute("version") != "1")
                throw new InvalidDataException("CrowdPlay host settings are invalid.");
            int number;
            if (Int32.TryParse(top.GetAttribute("port"), out number) && number >= 1024 && number <= 65535)
                settings.Port = number;
            if (Int32.TryParse(top.GetAttribute("defaultDurationMs"), out number) && number >= 1000 && number <= 120000)
                settings.DefaultDurationMs = number;
            settings.ShowCounts = top.GetAttribute("showCounts") != "false";
            settings.HostDecides = top.GetAttribute("hostDecides") == "true";
            settings.Overlay = top.GetAttribute("overlay") != "false";
            return settings;
        }

        internal void Save(string root)
        {
            string folder = Path.Combine(root, "CrowdPlayLAN");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, "host-settings.xml");
            string stage = Path.Combine(folder, "host-settings.next.xml");
            XmlWriterSettings format = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (XmlWriter writer = XmlWriter.Create(stage, format))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("CrowdPlayHostSettings");
                writer.WriteAttributeString("version", "1");
                writer.WriteAttributeString("port", Port.ToString());
                writer.WriteAttributeString("defaultDurationMs", DefaultDurationMs.ToString());
                writer.WriteAttributeString("showCounts", ShowCounts ? "true" : "false");
                writer.WriteAttributeString("hostDecides", HostDecides ? "true" : "false");
                writer.WriteAttributeString("overlay", Overlay ? "true" : "false");
                writer.WriteEndElement();
            }
            if (File.Exists(target)) File.Replace(stage, target, null);
            else File.Move(stage, target);
        }
    }

    internal sealed class VotePoll
    {
        internal string Id;
        internal readonly List<string> Choices = new List<string>();
        internal readonly Dictionary<string, int> Votes = new Dictionary<string, int>(StringComparer.Ordinal);
        internal long DeadlineMs;
        internal int DurationMs;
        internal bool IsManual;
        internal bool HostDecides;
        internal bool Ended;
        internal long EndedAtMs;
        internal int FinalWinner;
        internal int FinalPublicWinner;
        internal int HostVote;

        internal int[] Counts()
        {
            int[] result = new int[Choices.Count];
            foreach (int choice in Votes.Values)
                if (choice >= 1 && choice <= result.Length) result[choice - 1]++;
            return result;
        }

        internal int[] PublicCounts()
        {
            int[] result = Counts();
            if (HostVote >= 1 && HostVote <= result.Length) result[HostVote - 1]++;
            return result;
        }

        private int PublicWinner()
        {
            int[] counts = PublicCounts();
            int best = 0;
            List<int> tied = new List<int>();
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > best) { best = counts[i]; tied.Clear(); tied.Add(i + 1); }
                else if (counts[i] == best && best > 0) tied.Add(i + 1);
            }
            if (best == 0) return 0;
            if (tied.Count == 1) return tied[0];
            // Match the game bridge: it prefers the phone vote winner when
            // the combined host and phone totals are tied.
            if (tied.Contains(FinalWinner)) return FinalWinner;
            return tied[SecureRandom.Below(tied.Count)];
        }

        internal int Leader()
        {
            int[] counts = Counts();
            int best = 0, leader = 0;
            bool tied = false;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > best) { best = counts[i]; leader = i + 1; tied = false; }
                else if (counts[i] == best && best > 0) tied = true;
            }
            return best == 0 || tied ? 0 : leader;
        }

        internal void End(long? endedAtMs = null)
        {
            if (Ended) return;
            Ended = true;
            EndedAtMs = endedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int[] counts = Counts();
            int best = 0;
            foreach (int count in counts) if (count > best) best = count;
            if (best == 0) FinalWinner = 0;
            else
            {
                List<int> tied = new List<int>();
                for (int i = 0; i < counts.Length; i++)
                    if (counts[i] == best) tied.Add(i + 1);
                FinalWinner = tied[SecureRandom.Below(tied.Count)];
            }
            FinalPublicWinner = HostDecides && HostVote > 0 ? HostVote : PublicWinner();
        }
    }

    internal static class SecureRandom
    {
        private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        internal static int Below(int limit)
        {
            if (limit <= 0) throw new ArgumentOutOfRangeException("limit");
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                uint threshold = UInt32.MaxValue - UInt32.MaxValue % (uint)limit;
                uint value;
                do { random.GetBytes(bytes); value = BitConverter.ToUInt32(bytes, 0); }
                while (value >= threshold);
                return (int)(value % (uint)limit);
            }
        }

        internal static string RoomCode()
        {
            StringBuilder code = new StringBuilder(8);
            for (int i = 0; i < 8; i++) code.Append(Alphabet[Below(Alphabet.Length)]);
            return code.ToString();
        }
    }

    internal static class TunnelJob
    {
        private const uint KillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
            ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        internal static IntPtr CreateKillOnClose()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            ExtendedLimits limits = new ExtendedLimits();
            limits.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (SetInformationJobObject(job, 9, ref limits,
                (uint)Marshal.SizeOf(typeof(ExtendedLimits)))) return job;
            CloseHandle(job);
            return IntPtr.Zero;
        }

        internal static bool Assign(IntPtr job, Process process)
        {
            return job != IntPtr.Zero && AssignProcessToJobObject(job, process.Handle);
        }

        internal static void Close(IntPtr job)
        {
            if (job != IntPtr.Zero) CloseHandle(job);
        }

        // Process.WaitForExit can dispatch Exited handlers, which take the host lock.
        // Waiting on the OS handle avoids that callback while StopTunnel holds it.
        internal static void WaitForExit(Process process, uint milliseconds)
        {
            try { WaitForSingleObject(process.Handle, milliseconds); } catch { }
        }

        internal static void Terminate(Process process)
        {
            // Do not invoke Process.Kill/HasExited while the host lock is held:
            // those APIs can dispatch the Exited handler, which needs that lock.
            try { TerminateProcess(process.Handle, 1); } catch { }
        }

    }

    internal sealed class HttpRequest
    {
        internal string Method;
        internal string Path;
        internal string Query;
        internal string Body;
        internal readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static HttpRequest Read(NetworkStream stream)
        {
            MemoryStream header = new MemoryStream();
            int state = 0;
            while (header.Length < 16384)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                header.WriteByte((byte)value);
                if (state == 0) state = value == '\r' ? 1 : 0;
                else if (state == 1) state = value == '\n' ? 2 : 0;
                else if (state == 2) state = value == '\r' ? 3 : 0;
                else if (state == 3) state = value == '\n' ? 4 : 0;
                if (state == 4) break;
            }
            if (state != 4) throw new InvalidDataException("HTTP headers are too large.");
            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || (first[0] != "GET" && first[0] != "POST") ||
                !first[1].StartsWith("/", StringComparison.Ordinal) || !first[2].StartsWith("HTTP/1."))
                throw new InvalidDataException("Unsupported HTTP request.");
            HttpRequest request = new HttpRequest();
            request.Method = first[0];
            int question = first[1].IndexOf('?');
            request.Path = question < 0 ? first[1] : first[1].Substring(0, question);
            request.Query = question < 0 ? "" : first[1].Substring(question + 1);
            for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            int length = 0;
            string lengthText;
            if (request.Headers.TryGetValue("Content-Length", out lengthText) &&
                (!Int32.TryParse(lengthText, out length) || length < 0 || length > 65536))
                throw new InvalidDataException("Invalid HTTP body length.");
            string expectation;
            if (length > 0 && request.Headers.TryGetValue("Expect", out expectation) &&
                expectation.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                byte[] proceed = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                stream.Write(proceed, 0, proceed.Length);
                stream.Flush();
            }
            byte[] body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int count = stream.Read(body, read, length - read);
                if (count <= 0) throw new EndOfStreamException();
                read += count;
            }
            request.Body = Encoding.UTF8.GetString(body);
            return request;
        }

        internal string Header(string key)
        {
            string value;
            return Headers.TryGetValue(key, out value) ? value : "";
        }

        internal List<KeyValuePair<string, string>> Parameters()
        {
            List<KeyValuePair<string, string>> values = ParseForm(Query);
            if (Body.Length != 0 && Header("Content-Type").StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                values.AddRange(ParseForm(Body));
            return values;
        }

        private static List<KeyValuePair<string, string>> ParseForm(string encoded)
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            if (encoded.Length == 0) return result;
            foreach (string part in encoded.Split('&'))
            {
                if (part.Length == 0) continue;
                int equal = part.IndexOf('=');
                string name = equal < 0 ? part : part.Substring(0, equal);
                string value = equal < 0 ? "" : part.Substring(equal + 1);
                result.Add(new KeyValuePair<string, string>(Decode(name), Decode(value)));
            }
            return result;
        }

        private static string Decode(string value)
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
    }

    internal sealed class HttpResponse
    {
        internal int Status = 200;
        internal string ContentType = "text/plain";
        internal string Body = "";
        internal byte[] BinaryBody;
        internal string Cookie;
        internal bool CookieSecure;
        internal Action AfterWrite;

        internal static HttpResponse Plain(int status, string body)
        {
            return new HttpResponse { Status = status, Body = body };
        }

        internal static HttpResponse Json(int status, object value)
        {
            return new HttpResponse { Status = status, ContentType = "application/json",
                Body = new JavaScriptSerializer().Serialize(value) };
        }

        internal void Write(NetworkStream stream)
        {
            byte[] bodyBytes = BinaryBody ?? Encoding.UTF8.GetBytes(Body);
            string reason = Status == 200 ? "OK" : Status == 400 ? "Bad Request" :
                Status == 403 ? "Forbidden" : Status == 404 ? "Not Found" :
                Status == 405 ? "Method Not Allowed" :
                Status == 409 ? "Conflict" : "Internal Server Error";
            StringBuilder header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(reason).Append("\r\n");
            header.Append("Content-Type: ").Append(ContentType);
            if (BinaryBody == null) header.Append("; charset=utf-8");
            header.Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("X-Content-Type-Options: nosniff\r\n");
            header.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            if (!String.IsNullOrEmpty(Cookie))
            {
                header.Append("Set-Cookie: cpvoter=").Append(Cookie).Append("; Path=/; HttpOnly; SameSite=Lax");
                if (CookieSecure) header.Append("; Secure");
                header.Append("\r\n");
            }
            header.Append("Connection: close\r\n\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }
    }

    internal sealed class LanServer
    {
        private readonly object sync = new object();
        private readonly string root;
        private string room;
        private readonly int controlPort;
        private readonly Dictionary<string, long> players = new Dictionary<string, long>(StringComparer.Ordinal);
        private HostSettings settings;
        private VotePoll poll;
        private bool enabled;
        private bool gamePaused;
        private long pauseStartedMs;
        private TcpListener controlListener;
        private List<TcpListener> lanListeners = new List<TcpListener>();
        private IPAddress advertisedAddress = IPAddress.Loopback;
        private volatile bool stopping;
        private bool onlineMode;
        private string tunnelBaseUrl = "";
        private string tunnelState = "READY";
        private Process tunnelProcess;
        private IntPtr tunnelJob = IntPtr.Zero;
        private int tunnelGeneration;
        private long tunnelLastStartMs;
        private string tunnelError = "";
        private readonly bool testMode;
        private readonly AutoResetEvent keepaliveWake = new AutoResetEvent(false);
        private Thread keepaliveThread;
        private HttpWebRequest keepaliveRequest;
        private int keepaliveProbeCount;

        private static readonly Regex TunnelUrlPattern = new Regex(
            @"https://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com(?=$|[^a-z0-9.-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal LanServer(string root, HostSettings settings, int controlPort, bool testMode)
        {
            this.root = root;
            this.settings = settings;
            this.room = SecureRandom.RoomCode();
            this.controlPort = controlPort;
            this.testMode = testMode;
        }

        internal void Start()
        {
            controlListener = new TcpListener(IPAddress.Loopback, controlPort);
            controlListener.Start(32);
            StartAcceptThread(controlListener, true);
            try { BindLan(settings.Port); }
            catch { controlListener.Stop(); throw; }
            keepaliveThread = new Thread(KeepaliveLoop);
            keepaliveThread.IsBackground = true;
            keepaliveThread.Name = "CrowdPlay Online keepalive";
            keepaliveThread.Start();
        }

        internal void Stop()
        {
            stopping = true;
            lock (sync)
            {
                StopTunnel();
                if (tunnelJob != IntPtr.Zero)
                {
                    TunnelJob.Close(tunnelJob);
                    tunnelJob = IntPtr.Zero;
                }
                if (controlListener != null)
                {
                    controlListener.Stop();
                    controlListener = null;
                }
                foreach (TcpListener listener in lanListeners) listener.Stop();
                lanListeners.Clear();
            }
            keepaliveWake.Set();
            if (keepaliveThread != null && Thread.CurrentThread != keepaliveThread)
                keepaliveThread.Join(6000);
        }

        internal bool IsStopping { get { return stopping; } }

        // These tunnel methods run with sync held. Only the host owns this child;
        // --no-autoupdate prevents cloudflared from replacing the supervised process.
        private void StartTunnel()
        {
            StopTunnel();
            tunnelState = "PENDING";
            tunnelLastStartMs = Now();
            string binary = Path.Combine(root, "cloudflared.exe");
            if (!File.Exists(binary))
            {
                tunnelState = "ERROR";
                tunnelError = "BINARY_MISSING";
                return;
            }
            int generation = ++tunnelGeneration;
            Process child = new Process();
            child.StartInfo = new ProcessStartInfo
            {
                FileName = binary,
                // NUL is an empty config on Windows. It prevents a user's own
                // ~/.cloudflared/config.yaml from changing Quick Tunnel behavior.
                Arguments = "tunnel --url http://127.0.0.1:" + settings.Port +
                    " --no-autoupdate --config NUL",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            child.EnableRaisingEvents = true;
            child.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                { TunnelOutput(child, generation, eventArgs.Data); };
            child.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                { TunnelOutput(child, generation, eventArgs.Data); };
            child.Exited += delegate(object sender, EventArgs eventArgs)
                { TunnelExited(child, generation); };
            try
            {
                if (!child.Start()) throw new IOException("Tunnel did not start.");
                if (tunnelJob == IntPtr.Zero) tunnelJob = TunnelJob.CreateKillOnClose();
                if (tunnelJob != IntPtr.Zero && !TunnelJob.Assign(tunnelJob, child))
                {
                    // Some existing Windows jobs disallow nested assignment.
                    // Explicit termination remains the normal cleanup path.
                    TunnelJob.Close(tunnelJob);
                    tunnelJob = IntPtr.Zero;
                }
                tunnelProcess = child;
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
            }
            catch
            {
                tunnelGeneration++;
                tunnelProcess = null;
                tunnelBaseUrl = "";
                tunnelState = "ERROR";
                tunnelError = "START_FAILED";
                TunnelJob.Terminate(child);
                ReapTunnel(child);
            }
        }

        private void TunnelOutput(Process child, int generation, string line)
        {
            if (String.IsNullOrEmpty(line)) return;
            Match match = TunnelUrlPattern.Match(line);
            if (!match.Success) return;
            Uri address;
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out address) ||
                address.Scheme != Uri.UriSchemeHttps || address.Port != 443 ||
                !address.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase)) return;
            lock (sync)
            {
                if (!stopping && onlineMode && enabled && generation == tunnelGeneration &&
                    Object.ReferenceEquals(tunnelProcess, child))
                {
                    tunnelBaseUrl = address.GetLeftPart(UriPartial.Authority);
                    tunnelState = "READY";
                    tunnelError = "";
                    keepaliveWake.Set();
                }
            }
        }

        private void TunnelExited(Process child, int generation)
        {
            bool dispose = false;
            lock (sync)
            {
                if (generation == tunnelGeneration && Object.ReferenceEquals(tunnelProcess, child))
                {
                    tunnelProcess = null;
                    tunnelBaseUrl = "";
                    tunnelState = onlineMode ? "ERROR" : "READY";
                    tunnelError = onlineMode ? "PROCESS_EXITED" : "";
                    dispose = true;
                }
            }
            if (dispose) child.Dispose();
        }

        private static void ReapTunnel(Process child)
        {
            // Process exit/output callbacks can need sync. Never wait or dispose
            // inside a control request while that lock is held.
            ThreadPool.QueueUserWorkItem(delegate(object ignored)
            {
                TunnelJob.WaitForExit(child, 1500);
                try { child.Dispose(); } catch { }
            });
        }

        private void StopTunnel()
        {
            tunnelGeneration++;
            Process child = tunnelProcess;
            tunnelProcess = null;
            tunnelBaseUrl = "";
            tunnelState = onlineMode ? "PENDING" : "READY";
            tunnelError = "";
            HttpWebRequest activeRequest = keepaliveRequest;
            keepaliveRequest = null;
            if (activeRequest != null)
            {
                ThreadPool.QueueUserWorkItem(delegate(object ignored)
                {
                    try { activeRequest.Abort(); } catch { }
                });
            }
            keepaliveWake.Set();
            if (child == null) return;
            TunnelJob.Terminate(child);
            // Wait on the kernel handle only. This does not dispatch Process.Exited
            // while the menu request owns sync, and guarantees the tunnel has
            // closed before Local/Off is acknowledged.
            TunnelJob.WaitForExit(child, 1500);
            ReapTunnel(child);
        }

        private void KeepaliveLoop()
        {
            // The public URL must be used: pinging loopback would never reach Cloudflare.
            // Test mode probes the same route locally so timing and cleanup can be checked
            // without relying on an external tunnel in the integration test.
            int interval = testMode ? 250 : 15000;
            while (!stopping)
            {
                keepaliveWake.WaitOne(interval);
                if (stopping) break;
                HttpWebRequest probe;
                lock (sync)
                {
                    if (stopping || !enabled || !onlineMode) continue;
                    MaybeRestartTunnel();
                    if (tunnelState != "READY" || tunnelBaseUrl.Length == 0) continue;
                    string target = testMode
                        ? "http://127.0.0.1:" + settings.Port + "/keepalive"
                        : tunnelBaseUrl + "/keepalive";
                    probe = (HttpWebRequest)WebRequest.Create(target);
                    probe.Method = "GET";
                    probe.Timeout = 5000;
                    probe.ReadWriteTimeout = 5000;
                    probe.AllowAutoRedirect = false;
                    probe.KeepAlive = false;
                    keepaliveRequest = probe;
                }
                try { using (WebResponse response = probe.GetResponse()) { } }
                catch (WebException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    lock (sync)
                        if (Object.ReferenceEquals(keepaliveRequest, probe)) keepaliveRequest = null;
                }
            }
        }

        private void MaybeRestartTunnel()
        {
            if (onlineMode && enabled && tunnelProcess == null &&
                Now() - tunnelLastStartMs >= 5000)
                StartTunnel();
        }

        private bool IsTunnelHost(string host)
        {
            if (!onlineMode || tunnelBaseUrl.Length == 0) return false;
            Uri address;
            if (!Uri.TryCreate(tunnelBaseUrl, UriKind.Absolute, out address)) return false;
            return String.Equals(host, address.Host, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, address.Host + ":443", StringComparison.OrdinalIgnoreCase);
        }

        private void BindLan(int port)
        {
            List<IPAddress> addresses = new List<IPAddress>(PrivateAddresses());
            List<TcpListener> next = new List<TcpListener>();
            IPAddress advertised = IPAddress.Loopback;
            int chosenPort = port;
            try
            {
                bool bound = false;
                for (int attempt = 0; attempt <= 10; attempt++)
                {
                    int candidatePort = port + attempt;
                    if (candidatePort > 65535) break;
                    TcpListener preferred = null;
                    TcpListener loopback = null;
                    try
                    {
                        if (addresses.Count != 0)
                        {
                            preferred = new TcpListener(addresses[0], candidatePort);
                            preferred.Start(32);
                        }
                        // The tunnel connects to this listener, never the control port.
                        loopback = new TcpListener(IPAddress.Loopback, candidatePort);
                        loopback.Start(32);
                    }
                    catch (SocketException)
                    {
                        if (preferred != null) preferred.Stop();
                        if (loopback != null) loopback.Stop();
                        continue;
                    }
                    if (preferred != null)
                    {
                        next.Add(preferred);
                        advertised = addresses[0];
                    }
                    next.Add(loopback);
                    for (int i = 1; i < addresses.Count; i++)
                    {
                        try
                        {
                            TcpListener extra = new TcpListener(addresses[i], candidatePort);
                            extra.Start(32);
                            next.Add(extra);
                        }
                        catch (SocketException) { }
                    }
                    chosenPort = candidatePort;
                    bound = true;
                    break;
                }
                if (!bound)
                    throw new IOException("No LAN port is available for CrowdPlay.");
            }
            catch
            {
                foreach (TcpListener listener in next) listener.Stop();
                throw;
            }
            List<TcpListener> old;
            lock (sync)
            {
                old = lanListeners;
                lanListeners = next;
                advertisedAddress = advertised;
                settings.Port = chosenPort;
                if (onlineMode)
                {
                    StopTunnel();
                    StartTunnel();
                }
            }
            foreach (TcpListener listener in old) listener.Stop();
            foreach (TcpListener listener in next) StartAcceptThread(listener, false);
        }

        private void StartAcceptThread(TcpListener listener, bool controlChannel)
        {
            TcpListener captured = listener;
            bool capturedControl = controlChannel;
            Thread thread = new Thread(delegate() { AcceptLoop(captured, capturedControl); });
            thread.IsBackground = true;
            thread.Name = "CrowdPlay HTTP listener";
            thread.Start();
        }

        private static IEnumerable<IPAddress> PrivateAddresses()
        {
            List<KeyValuePair<int, IPAddress>> candidates = new List<KeyValuePair<int, IPAddress>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties properties = network.GetIPProperties();
                bool gateway = false;
                foreach (GatewayIPAddressInformation item in properties.GatewayAddresses)
                    if (item.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !item.Address.Equals(IPAddress.Any)) gateway = true;
                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                {
                    IPAddress ip = address.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = ip.GetAddressBytes();
                    bool privateIp = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                        (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
                    if (privateIp && seen.Add(ip.ToString()))
                    {
                        int score = gateway ? 0 : 100;
                        if (network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 0;
                        else if (network.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 10;
                        else score += 20;
                        if (b[0] == 169 && b[1] == 254) score += 200;
                        candidates.Add(new KeyValuePair<int, IPAddress>(score, ip));
                    }
                }
            }
            candidates.Sort(delegate(KeyValuePair<int, IPAddress> left, KeyValuePair<int, IPAddress> right)
                { return left.Key.CompareTo(right.Key); });
            foreach (KeyValuePair<int, IPAddress> item in candidates) yield return item.Value;
        }

        private void AcceptLoop(TcpListener listener, bool controlChannel)
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                ThreadPool.QueueUserWorkItem(delegate(object state) { Handle((TcpClient)state, controlChannel); }, client);
            }
        }

        private void Handle(TcpClient client, bool controlChannel)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    NetworkStream stream = client.GetStream();
                    HttpRequest request = HttpRequest.Read(stream);
                    IPAddress remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    HttpResponse response = Route(request, controlChannel, IPAddress.IsLoopback(remote));
                    try { response.Write(stream); }
                    finally { if (response.AfterWrite != null) response.AfterWrite(); }
                }
                catch (InvalidDataException)
                {
                    try { HttpResponse.Plain(400, "Bad request").Write(client.GetStream()); } catch { }
                }
                catch { }
            }
        }

        private HttpResponse Route(HttpRequest request, bool controlChannel, bool loopback)
        {
            if (controlChannel)
            {
                if (!request.Path.StartsWith("/control/", StringComparison.Ordinal))
                    return HttpResponse.Plain(404, "Not found");
                if (!loopback) return HttpResponse.Plain(403, "FORBIDDEN");
                // A browser can reach loopback from an unrelated site. The game bridge and
                // uninstaller do not send these browser navigation headers.
                if (request.Headers.ContainsKey("Origin") ||
                    request.Headers.ContainsKey("Referer") ||
                    request.Headers.ContainsKey("Referrer"))
                    return HttpResponse.Plain(403, "FORBIDDEN");
                string host = request.Header("Host");
                if (host.Length != 0 &&
                    !String.Equals(host, "127.0.0.1:" + controlPort, StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                    return HttpResponse.Plain(403, "FORBIDDEN");
                return Control(request);
            }
            // A tunnel request reaches the loopback phone listener. Remote address
            // alone cannot distinguish it from the local game bridge.
            if (request.Path.StartsWith("/control/", StringComparison.Ordinal))
                return HttpResponse.Plain(403, "FORBIDDEN");
            if (request.Path == "/keepalive" && request.Method == "GET")
            {
                lock (sync)
                {
                    if (!enabled || !onlineMode) return HttpResponse.Plain(404, "Not found");
                    if (testMode) Interlocked.Increment(ref keepaliveProbeCount);
                    return HttpResponse.Plain(200, "OK");
                }
            }
            if (request.Path == "/" && request.Method == "GET")
            {
                lock (sync)
                    return new HttpResponse { ContentType = "text/html", Body = EntryPage(false, !enabled) };
            }
            if (request.Path == "/font.otf" && request.Method == "GET")
                return LocalAsset("Minecraft.otf", "font/otf");
            if (request.Path == "/vote-dirt.png" && request.Method == "GET")
                return LocalAsset("Dirt_background_BE2.png", "image/png");
            if (request.Path == "/story-mode-logo.png" && request.Method == "GET")
                return LocalAsset("StoryModeLogo.png", "image/png");
            if (request.Path == "/voting-banner.png" && request.Method == "GET")
                return LocalAsset("VotingBanner.png", "image/png");
            if (request.Path == "/story-mode-cursor.png" && request.Method == "GET")
                return LocalAsset("StoryModeCursor.png", "image/png");
            if (request.Path == "/qrcode-generator.js" && request.Method == "GET")
                return new HttpResponse { ContentType = "application/javascript", BinaryBody = ResourceBytes("CrowdPlay.QrEncoder") };
            if (request.Path == "/qrcode-generator-license.txt" && request.Method == "GET")
                return new HttpResponse { ContentType = "text/plain", Body = ResourceText("CrowdPlay.QrEncoderLicense") };
            if (request.Path.StartsWith("/qr/", StringComparison.Ordinal))
            {
                lock (sync)
                {
                    if (!enabled)
                        return new HttpResponse { Status = 403, ContentType = "text/html", Body = EntryPage(false, true) };
                    if (request.Method != "GET" || request.Path != "/qr/" + room)
                        return new HttpResponse { Status = 404, ContentType = "text/html", Body = EntryPage(true) };
                    string target = JoinUrl();
                    if (target.Length == 0) return HttpResponse.Plain(409, "Online tunnel is not ready");
                    return new HttpResponse { ContentType = "text/html", Body = QrPage(room, target) };
                }
            }
            if (request.Path.StartsWith("/join/", StringComparison.Ordinal))
            {
                lock (sync)
                {
                    if (!enabled)
                        return new HttpResponse { Status = 403, ContentType = "text/html", Body = EntryPage(false, true) };
                    if (request.Method != "GET" || request.Path != "/join/" + room)
                        return new HttpResponse { Status = 404, ContentType = "text/html", Body = EntryPage(true) };
                    string existing = ExistingClientCookie(request);
                    HttpResponse page = new HttpResponse { ContentType = "text/html", Body = PhonePage(room) };
                    page.Cookie = existing ?? ClientCookie(request);
                    page.CookieSecure = IsTunnelHost(request.Header("Host"));
                    TouchPlayer(page.Cookie);
                    return page;
                }
            }
            if (request.Path == "/api/state") return PublicState(request);
            if (request.Path == "/api/vote") return PublicVote(request);
            if (request.Path == "/api/leave") return PublicLeave(request);
            return HttpResponse.Plain(404, "Not found");
        }

        private HttpResponse Control(HttpRequest request)
        {
            string path = request.Path;
            if (path == "/control/status")
            {
                lock (sync)
                {
                    MaybeRestartTunnel();
                    return HttpResponse.Plain(200, "READY|" + room + "|" + JoinUrl() + "|" +
                        PlayerCount() + "|" + (enabled ? "1" : "0") + "|" +
                        (onlineMode ? "ONLINE" : "LOCAL") + "|" + tunnelState + "|" + tunnelError);
                }
            }
            if (path == "/control/enabled")
            {
                if (request.Method != "POST") return PostRequired();
                string value = Value(RequestArgs(request), "value");
                if (value != "0" && value != "1") return HttpResponse.Plain(400, "ERR|INVALID_ENABLED");
                lock (sync)
                {
                    bool next = value == "1";
                    if (!next)
                    {
                        onlineMode = false;
                        StopTunnel();
                    }
                    if (enabled && !next)
                    {
                        poll = null;
                        players.Clear();
                        // A disabled room must not silently accept an old join link when re-enabled.
                        string previous = room;
                        do { room = SecureRandom.RoomCode(); } while (room == previous);
                    }
                    enabled = next;
                    return HttpResponse.Plain(200, "ENABLED|" + (enabled ? "1" : "0"));
                }
            }
            if (path == "/control/mode")
            {
                if (request.Method != "POST") return PostRequired();
                string value = Value(RequestArgs(request), "value");
                if (value != "local" && value != "online")
                    return HttpResponse.Plain(400, "ERR|INVALID_MODE");
                lock (sync)
                {
                    if (!enabled) return HttpResponse.Plain(409, "ERR|DISABLED");
                    if (value == "local")
                    {
                        onlineMode = false;
                        StopTunnel();
                    }
                    else if (!onlineMode)
                    {
                        onlineMode = true;
                        StartTunnel();
                    }
                    return HttpResponse.Plain(200, "MODE|" +
                        (onlineMode ? "ONLINE" : "LOCAL") + "|" + tunnelState + "|" + tunnelError);
                }
            }
            if (path == "/control/pause")
            {
                if (request.Method != "POST") return PostRequired();
                string value = Value(RequestArgs(request), "value");
                if (value != "0" && value != "1") return HttpResponse.Plain(400, "ERR|INVALID_PAUSE");
                lock (sync)
                {
                    bool next = value == "1";
                    if (next != gamePaused)
                    {
                        if (next)
                        {
                            RefreshPoll();
                            pauseStartedMs = Now();
                        }
                        else if (poll != null && !poll.IsManual && !poll.Ended)
                        {
                            poll.DeadlineMs += Math.Max(0L, Now() - pauseStartedMs);
                        }
                        gamePaused = next;
                    }
                    return HttpResponse.Plain(200, "PAUSED|" + (gamePaused ? "1" : "0"));
                }
            }
            if (path == "/control/new-room")
            {
                if (request.Method != "POST") return PostRequired();
                lock (sync)
                {
                    string previous = room;
                    do { room = SecureRandom.RoomCode(); } while (room == previous);
                    poll = null;
                    players.Clear();
                    return HttpResponse.Plain(200, "NEWROOM|" + room + "|" + JoinUrl());
                }
            }
            if (path == "/control/copy")
            {
                if (request.Method != "POST") return PostRequired();
                string kind = Value(RequestArgs(request), "kind");
                string value;
                lock (sync)
                {
                    if (kind == "room") value = room;
                    else if (kind == "address")
                    {
                        value = onlineMode ? tunnelBaseUrl : advertisedAddress + ":" + settings.Port;
                    }
                    else if (kind == "link") value = JoinUrl();
                    else return HttpResponse.Plain(400, "ERR|INVALID_KIND");
                    if (value.Length == 0) return HttpResponse.Plain(409, "ERR|TUNNEL_NOT_READY");
                }
                return TryCopyText(value)
                    ? HttpResponse.Plain(200, "COPIED|" + kind.ToUpperInvariant())
                    : HttpResponse.Plain(409, "ERR|CLIPBOARD_UNAVAILABLE");
            }
            if (path == "/control/report")
                return request.Method == "POST" ? SaveReport() : PostRequired();
            if (path == "/control/start")
                return request.Method == "POST" ? StartPoll(request) : PostRequired();
            if (path == "/control/host-vote")
                return request.Method == "POST" ? SetHostVote(request) : PostRequired();
            if (path == "/control/state") return ControlState();
            if (path == "/control/end")
                return request.Method == "POST" ? EndPoll(request) : PostRequired();
            if (path == "/control/cancel")
            {
                if (request.Method != "POST") return PostRequired();
                lock (sync) poll = null;
                return HttpResponse.Plain(200, "OK");
            }
            if (path == "/control/config")
            {
                if (request.Method != "POST" && (request.Query.Length != 0 || request.Body.Length != 0))
                    return PostRequired();
                return Configure(request);
            }
            if (path == "/control/heartbeat") return HttpResponse.Plain(200, "OK");
            if (path == "/control/keepalive-probe" && testMode)
                return HttpResponse.Plain(200, Interlocked.CompareExchange(ref keepaliveProbeCount, 0, 0).ToString());
            if (path == "/control/identity")
                return HttpResponse.Plain(200, "INSTANCE|" + Program.InstanceId(root));
            if (path == "/control/shutdown")
            {
                if (request.Method != "POST") return PostRequired();
                if (Value(RequestArgs(request), "instance") != Program.InstanceId(root))
                    return HttpResponse.Plain(403, "FORBIDDEN");
                lock (sync)
                {
                    onlineMode = false;
                    StopTunnel();
                }
                // Finish the HTTP response before the main loop closes its listener.
                return new HttpResponse { Body = "OK", AfterWrite = delegate() { stopping = true; } };
            }
            return HttpResponse.Plain(404, "Not found");
        }

        private static HttpResponse PostRequired()
        {
            return HttpResponse.Plain(405, "POST required");
        }

        private static bool TryCopyText(string value)
        {
            bool copied = false;
            Thread thread = new Thread(delegate()
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        Clipboard.SetText(value, TextDataFormat.UnicodeText);
                        copied = true;
                        return;
                    }
                    catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(80); }
                    catch { return; }
                }
            });
            thread.IsBackground = true;
            try
            {
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(1500);
            }
            catch { return false; }
            return copied;
        }

        private HttpResponse SaveReport()
        {
            string report;
            lock (sync)
            {
                RefreshPoll();
                StringBuilder details = new StringBuilder();
                details.AppendLine("CrowdPlay diagnostic");
                details.AppendLine("Generated (UTC): " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                details.AppendLine("Season: " + (File.Exists(Path.Combine(root, "Minecraft2.exe")) ? "2" : "1"));
                details.AppendLine("Room code: " + room);
                details.AppendLine("Phone address: " + advertisedAddress + ":" + settings.Port);
                details.AppendLine("Mode: " + (onlineMode ? "online" : "local"));
                details.AppendLine("Tunnel state: " + tunnelState);
                if (tunnelBaseUrl.Length != 0) details.AppendLine("Online address: " + tunnelBaseUrl);
                details.AppendLine("Connected players: " + PlayerCount());
                details.AppendLine("Enabled: " + (enabled ? "yes" : "no"));
                details.AppendLine("Game paused: " + (gamePaused ? "yes" : "no"));
                details.AppendLine("Poll status: " + (poll == null ? "idle" : poll.Ended ? "ended" : "active"));
                if (poll != null)
                {
                    details.AppendLine("Poll ID: " + poll.Id);
                    details.AppendLine("Poll choice count: " + poll.Choices.Count);
                    details.AppendLine("Poll vote counts: " + CountsCsv(poll));
                    details.AppendLine("Poll winner: " + (poll.Ended ? poll.FinalWinner : poll.Leader()));
                }
                details.AppendLine("Host decides: " + (settings.HostDecides ? "yes" : "no"));
                details.AppendLine("Show on-screen votes: " + (settings.Overlay ? "yes" : "no"));
                details.AppendLine("Show phone vote counts: " + (settings.ShowCounts ? "yes" : "no"));
                details.AppendLine("Default vote duration (ms): " + settings.DefaultDurationMs);
                report = details.ToString();
            }
            string path = Path.Combine(root, "CrowdPlayLAN", "debug-report.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, report, new UTF8Encoding(false));
                return HttpResponse.Plain(200, "SAVED|" + path);
            }
            catch { return HttpResponse.Plain(409, "ERR|REPORT_UNAVAILABLE"); }
        }

        // Call only while holding sync, so a room rotation and its URL stay paired.
        private string JoinUrl()
        {
            if (onlineMode)
                return tunnelBaseUrl.Length == 0 ? "" : tunnelBaseUrl + "/join/" + room;
            return "http://" + advertisedAddress + ":" + settings.Port + "/join/" + room;
        }

        private HttpResponse StartPoll(HttpRequest request)
        {
            List<KeyValuePair<string, string>> args = RequestArgs(request);
            string id = Value(args, "poll");
            string manual = Value(args, "manual");
            int duration;
            if (!Int32.TryParse(Value(args, "duration_ms"), out duration))
                duration = settings.DefaultDurationMs;
            List<string> choices = Values(args, "choice");
            if (choices.Count == 0) choices = Values(args, "choices");
            if (id.Length == 0 || id.Length > 128 || id.IndexOf('|') >= 0 ||
                (manual.Length != 0 && manual != "0" && manual != "1") ||
                duration < 1000 || duration > 120000 || choices.Count < 2 || choices.Count > 6)
                return HttpResponse.Plain(400, "ERR|INVALID_POLL");
            foreach (string choice in choices)
                if (choice.Length == 0 || choice.Length > 300)
                    return HttpResponse.Plain(400, "ERR|INVALID_CHOICE");
            lock (sync)
            {
                if (!enabled) return HttpResponse.Plain(409, "ERR|DISABLED");
                RefreshPoll();
                if (poll != null && poll.Id == id && !poll.Ended)
                    return HttpResponse.Plain(200, "STARTED|" + room + "|" + poll.DeadlineMs);
                VotePoll fresh = new VotePoll();
                fresh.Id = id;
                fresh.Choices.AddRange(choices);
                fresh.DurationMs = duration;
                fresh.IsManual = manual == "1";
                fresh.HostDecides = settings.HostDecides;
                fresh.DeadlineMs = (gamePaused && !fresh.IsManual ? pauseStartedMs : Now()) + duration;
                poll = fresh;
                return HttpResponse.Plain(200, "STARTED|" + room + "|" + fresh.DeadlineMs);
            }
        }

        private HttpResponse ControlState()
        {
            lock (sync)
            {
                RefreshPoll();
                if (poll == null) return HttpResponse.Plain(200, "IDLE||0|0|0");
                string status = poll.Ended ? "ENDED" : "ACTIVE";
                int winner = poll.Ended ? poll.FinalWinner : poll.Leader();
                long remaining = RemainingMs(poll);
                return HttpResponse.Plain(200, status + "|" + poll.Id + "|" +
                    CountsCsv(poll) + "|" + winner + "|" + remaining);
            }
        }

        private HttpResponse SetHostVote(HttpRequest request)
        {
            List<KeyValuePair<string, string>> args = RequestArgs(request);
            string id = Value(args, "poll");
            int choice;
            if (id.Length == 0 || !Int32.TryParse(Value(args, "choice"), out choice))
                return HttpResponse.Plain(400, "ERR|INVALID_HOST_VOTE");
            lock (sync)
            {
                if (!enabled || poll == null || poll.Id != id)
                    return HttpResponse.Plain(409, "ERR|NO_POLL");
                RefreshPoll();
                if (poll.Ended) return HttpResponse.Plain(409, "ERR|VOTING_CLOSED");
                if (choice < 1 || choice > poll.Choices.Count)
                    return HttpResponse.Plain(400, "ERR|INVALID_HOST_VOTE");
                // Replacing the value records one host ballot, even if the
                // player changes selection before the story timer expires.
                poll.HostVote = choice;
                return HttpResponse.Plain(200, "HOSTVOTE|" + choice);
            }
        }

        private HttpResponse EndPoll(HttpRequest request)
        {
            string id = Value(RequestArgs(request), "poll");
            lock (sync)
            {
                if (poll == null || (id.Length != 0 && poll.Id != id))
                    return HttpResponse.Plain(409, "ERR|NO_POLL");
                poll.End();
                return HttpResponse.Plain(200, "ENDED|" + poll.FinalWinner + "|" + CountsCsv(poll));
            }
        }

        private static string CountsCsv(VotePoll value)
        {
            int[] counts = value.Counts();
            string[] pieces = new string[counts.Length];
            for (int i = 0; i < counts.Length; i++) pieces[i] = counts[i].ToString();
            return String.Join(",", pieces);
        }

        private HttpResponse Configure(HttpRequest request)
        {
            List<KeyValuePair<string, string>> args = RequestArgs(request);
            string portText = Value(args, "port");
            string durationText = Value(args, "default_duration_ms");
            string show = Value(args, "show_counts");
            string host = Value(args, "host_decides");
            string overlay = Value(args, "overlay");
            int newPort = settings.Port, duration = settings.DefaultDurationMs;
            if ((portText.Length != 0 && (!Int32.TryParse(portText, out newPort) || newPort < 1024 || newPort > 65535)) ||
                (durationText.Length != 0 && (!Int32.TryParse(durationText, out duration) || duration < 1000 || duration > 120000)) ||
                (show.Length != 0 && show != "0" && show != "1") ||
                (host.Length != 0 && host != "0" && host != "1") ||
                (overlay.Length != 0 && overlay != "0" && overlay != "1"))
                return HttpResponse.Plain(400, "ERR|INVALID_CONFIG");
            try
            {
                if (newPort != settings.Port) BindLan(newPort);
                lock (sync)
                {
                    settings.DefaultDurationMs = duration;
                    if (show.Length != 0) settings.ShowCounts = show == "1";
                    if (host.Length != 0) settings.HostDecides = host == "1";
                    if (overlay.Length != 0) settings.Overlay = overlay == "1";
                    if (args.Count != 0) settings.Save(root);
                    return HttpResponse.Plain(200, "CONFIG|" + (settings.HostDecides ? "1" : "0") +
                        "|" + (settings.Overlay ? "1" : "0") + "|" + (settings.ShowCounts ? "1" : "0"));
                }
            }
            catch { return HttpResponse.Plain(409, "ERR|PORT_OR_SETTINGS_UNAVAILABLE"); }
        }

        private HttpResponse PublicState(HttpRequest request)
        {
            lock (sync)
            {
                if (!enabled)
                    return HttpResponse.Json(403, new { error = "Crowd Play is off in the game" });
                if (Value(request.Parameters(), "room") != room)
                    return HttpResponse.Json(404, new { error = "Room not found" });
                string client = ExistingClientCookie(request);
                if (client != null) TouchPlayer(client);
                RefreshPoll();
                if (poll == null || (poll.Ended && Now() - poll.EndedAtMs >= 10000))
                    return HttpResponse.Json(200, new { status = "idle", room = room, choices = new string[0],
                        counts = new int[0], winner = 0, remainingMs = 0L, durationMs = 0, paused = false,
                        showCounts = settings.ShowCounts, hostChoice = 0 });
                return HttpResponse.Json(200, new { status = poll.Ended ? "ended" : "active",
                    room = room, poll = poll.Id, choices = poll.Choices.ToArray(),
                    counts = poll.Ended || settings.ShowCounts ? poll.PublicCounts() : new int[0],
                    winner = poll.Ended ? poll.FinalPublicWinner : 0,
                    remainingMs = RemainingMs(poll),
                    durationMs = poll.DurationMs,
                    paused = gamePaused && !poll.IsManual && !poll.Ended,
                    showCounts = settings.ShowCounts, hostChoice = poll.HostVote });
            }
        }

        private HttpResponse PublicVote(HttpRequest request)
        {
            if (request.Method != "POST") return HttpResponse.Json(400, new { error = "POST required" });
            List<KeyValuePair<string, string>> args = RequestArgs(request);
            int choice;
            if (!Int32.TryParse(Value(args, "choice"), out choice))
                return HttpResponse.Json(400, new { error = "Invalid choice" });
            string client = ClientCookie(request);
            lock (sync)
            {
                if (!enabled)
                    return HttpResponse.Json(403, new { error = "Crowd Play is off in the game" });
                if (Value(args, "room") != room)
                    return HttpResponse.Json(404, new { error = "Room not found" });
                RefreshPoll();
                if (poll == null || poll.Ended)
                    return HttpResponse.Json(409, new { error = "Voting is closed" });
                if (choice < 1 || choice > poll.Choices.Count)
                    return HttpResponse.Json(400, new { error = "Invalid choice" });
                poll.Votes[client] = choice;
                TouchPlayer(client);
                HttpResponse result = HttpResponse.Json(200, new { ok = true, choice = choice });
                result.Cookie = client;
                return result;
            }
        }

        private HttpResponse PublicLeave(HttpRequest request)
        {
            if (request.Method != "POST") return HttpResponse.Json(400, new { error = "POST required" });
            lock (sync)
            {
                if (!enabled)
                    return HttpResponse.Json(403, new { error = "Crowd Play is off in the game" });
                if (Value(RequestArgs(request), "room") != room)
                    return HttpResponse.Json(404, new { error = "Room not found" });
                string client = ExistingClientCookie(request);
                if (client != null)
                {
                    players.Remove(client);
                }
                // Votes already cast remain counted for the current choice.
                return HttpResponse.Json(200, new { ok = true });
            }
        }

        private void RefreshPoll()
        {
            if (poll != null && !poll.Ended && PollNow(poll) >= poll.DeadlineMs)
                poll.End(poll.DeadlineMs);
        }

        private long PollNow(VotePoll current)
        {
            return gamePaused && !current.IsManual ? pauseStartedMs : Now();
        }

        private long RemainingMs(VotePoll current)
        {
            return current.Ended ? 0L : Math.Max(0L, current.DeadlineMs - PollNow(current));
        }

        private static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        private static string ClientCookie(HttpRequest request)
        {
            string existing = ExistingClientCookie(request);
            return existing ?? Guid.NewGuid().ToString("N");
        }

        private static string ExistingClientCookie(HttpRequest request)
        {
            string raw = request.Header("Cookie");
            foreach (string part in raw.Split(';'))
            {
                string value = part.Trim();
                if (value.StartsWith("cpvoter=", StringComparison.Ordinal))
                {
                    string id = value.Substring(8);
                    Guid parsed;
                    if (id.Length == 32 && Guid.TryParseExact(id, "N", out parsed)) return id;
                }
            }
            return null;
        }

        private void TouchPlayer(string client)
        {
            players[client] = Now();
        }

        private int PlayerCount()
        {
            long cutoff = Now() - 30000;
            List<string> old = new List<string>();
            foreach (KeyValuePair<string, long> entry in players)
                if (entry.Value < cutoff) old.Add(entry.Key);
            foreach (string key in old) players.Remove(key);
            return players.Count;
        }

        private static List<KeyValuePair<string, string>> RequestArgs(HttpRequest request)
        {
            List<KeyValuePair<string, string>> args = request.Parameters();
            if (request.Body.Length > 0 && request.Header("Content-Type").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                object value = new JavaScriptSerializer().DeserializeObject(request.Body);
                Dictionary<string, object> json = value as Dictionary<string, object>;
                if (json == null) throw new InvalidDataException("Invalid JSON request.");
                foreach (KeyValuePair<string, object> entry in json)
                {
                    object[] array = entry.Value as object[];
                    if (array != null)
                    {
                        foreach (object item in array)
                            args.Add(new KeyValuePair<string, string>(entry.Key == "choices" ? "choice" : entry.Key,
                                Convert.ToString(item)));
                    }
                    else args.Add(new KeyValuePair<string, string>(entry.Key, Convert.ToString(entry.Value)));
                }
            }
            return args;
        }

        private static string Value(List<KeyValuePair<string, string>> args, string key)
        {
            foreach (KeyValuePair<string, string> item in args)
                if (String.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) return item.Value;
            return "";
        }

        private static List<string> Values(List<KeyValuePair<string, string>> args, string key)
        {
            List<string> result = new List<string>();
            foreach (KeyValuePair<string, string> item in args)
                if (String.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) result.Add(item.Value);
            return result;
        }

        private HttpResponse LocalAsset(string name, string contentType)
        {
            string path = Path.Combine(root, "CrowdPlayLAN", "assets", name);
            if (!File.Exists(path)) return HttpResponse.Plain(404, "Not found");
            return new HttpResponse { ContentType = contentType, BinaryBody = File.ReadAllBytes(path) };
        }

        private static byte[] ResourceBytes(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("Missing host resource: " + name);
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        private static string ResourceText(string name)
        {
            return Encoding.UTF8.GetString(ResourceBytes(name));
        }

        private static string PhonePage(string currentRoom)
        {
            // Room codes are generated from a fixed ASCII alphabet. Choice text is
            // inserted by the page with textContent, never as HTML.
            return ResourceText("CrowdPlay.PhonePage").Replace("__ROOM_CODE__", currentRoom);
        }

        private static string QrPage(string currentRoom, string joinUrl)
        {
            return ResourceText("CrowdPlay.QrPage")
                .Replace("__ROOM_CODE__", currentRoom)
                .Replace("__JOIN_URL__", joinUrl);
        }

        private static string EntryPage(bool invalidRoom = false, bool disabled = false)
        {
            return ResourceText("CrowdPlay.EntryPage").Replace("__INITIAL_ERROR__",
                invalidRoom ? "That room code was not found. Check the code shown in the game." :
                disabled ? "Crowd Play is off in the game. Ask the host to turn it on." : "");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            bool testMode = false;
            int gamePid = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--test") testMode = true;
                else if (args[i] == "--game-pid" && i + 1 < args.Length && Int32.TryParse(args[++i], out gamePid) && gamePid > 0) { }
                else return 2;
            }
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            bool seasonTwo = File.Exists(Path.Combine(root, "Minecraft2.exe"));
            if (seasonTwo == File.Exists(Path.Combine(root, "GameApp.exe")) && !testMode) return 2;
            string game = seasonTwo ? "Minecraft2" : "GameApp";
            int controlPort = seasonTwo ? 17862 : 17861;
            string mutexName = @"Local\CrowdPlayHost_" + InstanceId(root);
            bool acquired;
            using (Mutex singleton = new Mutex(true, mutexName, out acquired))
            {
                if (!acquired) return 0;
                LanServer server = null;
                try
                {
                    HostSettings settings = HostSettings.Load(root, seasonTwo);
                    server = new LanServer(root, settings, controlPort, testMode);
                    server.Start();
                    bool seenGame = false;
                    DateTime started = DateTime.UtcNow;
                    DateTime parentStarted = DateTime.MinValue;
                    if (gamePid > 0)
                    {
                        try { parentStarted = Process.GetProcessById(gamePid).StartTime; }
                        catch { return 2; }
                    }
                    while (!server.IsStopping)
                    {
                        if (testMode) { Thread.Sleep(250); continue; }
                        if (gamePid > 0)
                        {
                            try
                            {
                                Process parent = Process.GetProcessById(gamePid);
                                if (parent.HasExited || parent.StartTime != parentStarted) break;
                            }
                            catch { break; }
                            Thread.Sleep(1000);
                            continue;
                        }
                        bool running = Process.GetProcessesByName(game).Length != 0;
                        if (running) seenGame = true;
                        if ((seenGame && !running) || (!seenGame && DateTime.UtcNow - started > TimeSpan.FromSeconds(30)))
                            break;
                        Thread.Sleep(1000);
                    }
                    return 0;
                }
                catch { return 1; }
                finally
                {
                    if (server != null)
                        try { server.Stop(); } catch { }
                }
            }
        }

        internal static string InstanceId(string root)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
                StringBuilder result = new StringBuilder();
                foreach (byte value in digest) result.Append(value.ToString("x2"));
                return result.ToString().Substring(0, 20);
            }
        }
    }
}
