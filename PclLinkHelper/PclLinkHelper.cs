using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PclLinkHelper
{
    internal sealed class PanelRefs
    {
        public Border Root;
        public TextBlock Detail;
        public StackPanel InputRow;
        public TextBox CodeBox;
        public Button Submit;
        public TextBlock Status;
        public TextBlock CodeInfo;
    }
    internal sealed class HallPanelRefs
    {
        public Border Root;
        public TextBlock Status;
        public TextBlock Rooms;
        public StackPanel RoomButtons;
        public TextBlock Friends;
        public TextBlock Hint;
    }
    internal sealed class ChatFriendInfo
    {
        public string Device;
        public string Name;
        public bool Online;
        public string Room;
    }
    internal sealed class ChatLineInfo
    {
        public long Id;
        public long Ts;
        public string Scope;
        public string Room;
        public string FromDevice;
        public string FromName;
        public string To;
        public string Text;
    }
    internal sealed class ChatPanelRefs
    {
        public Border Root;
        public TextBlock Title;
        public TextBlock MyCode;
        public Button ModeGlobal;
        public Button ModeRoom;
        public Button ModePrivate;
        public ComboBox FriendBox;
        public Button AddFriendBtn;
        public Button RemoveFriendBtn;
        public StackPanel PrivateRow;
        public StackPanel AddRow;
        public TextBox FriendCodeBox;
        public Button ConfirmAddBtn;
        public ListBox MessageList;
        public TextBlock CharCount;
        public TextBox Input;
        public Button Send;
        public TextBlock Status;
        public Window Window;
    }
    internal sealed class RightColumnRefs
    {
        public StackPanel Root;
        public RelayPanelRefs Relay;
        public HallPanelRefs Hall;
        public ChatPanelRefs Chat;
    }
    internal sealed class RelayPanelRefs
    {
        public Border Root;
        public TextBlock RelayText;
        public TextBlock Status;
        public TextBlock Info;
        public TextBlock Bandwidth;
        public Button EditBtn;
        public StackPanel EditRow;
        public TextBox RelayBox;
        public Button SaveBtn;
        public Button CancelBtn;
        public StackPanel Container;
    }

    public static class Helper
    {
        private const string ServerBase = "http://106.55.39.109:8088";
        private static readonly object BeatLock = new object();
        private static readonly object NetLock = new object();
        private static DateTime LastBeat = DateTime.MinValue;
        private static string CachedNetwork = null;
        private static DateTime NetworkFetched = DateTime.MinValue;
        private static PanelRefs _leftPanel;
        private static RelayPanelRefs _rightPanel;
        private static DispatcherTimer _relayTimer;
        private static DispatcherTimer _verifyTimer;
        private static bool _verifying = false;
        private static bool _autoVerifying = false;
        private static object _pageLink = null;
        private static HallPanelRefs _hallPanel;
        private static RightColumnRefs _rightColumn;
        private static ChatPanelRefs _chatPanel;
        private static DispatcherTimer _chatTimer;
        private static long _chatSeq = 0;
        private static string _myDevice = "";
        private static string _chatMode = "global";
        private static string _chatFriendDevice = null;
        private static int _chatUnread = 0;
        private static string _myFriendCode = "";
        private static string _chatRoomName = "";
        private static bool _chatUpdatingFriends = false;
        private static readonly List<ChatLineInfo> _chatMsgs = new List<ChatLineInfo>();
        private static readonly List<ChatFriendInfo> _chatFriends = new List<ChatFriendInfo>();

        // ================= 路径 / 认证文件 =================

        private static string AuthFilePath()
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCL", "link_auth.dat");
            }
            catch { return null; }
        }

        private static void ReadAuth(string file, out string device, out string token)
        {
            device = "";
            token = "";
            try
            {
                if (file == null || !File.Exists(file)) return;
                string text = File.ReadAllText(file);
                device = ExtractJson(text, "device") ?? "";
                token = ExtractJson(text, "token") ?? "";
            }
            catch { }
        }

        private static void WriteAuth(string file, string device, string token)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return;
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = "{\"server\":\"" + ServerBase + "\",\"device\":\"" + Escape(device) + "\",\"token\":\"" + Escape(token) + "\"}";
                File.WriteAllText(file, json, new UTF8Encoding(false));
            }
            catch { }
        }

        // ================= 心跳 =================

        private static void SendHeartbeat(string device, string token, string network, string hostname)
        {
            SendHeartbeat(device, token, network, hostname, null);
        }

        private static void SendHeartbeat(string device, string token, string network, string hostname, string invite)
        {
            try
            {
                if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(token)) return;
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"device\":\"").Append(Escape(device)).Append("\",\"token\":\"").Append(Escape(token)).Append("\"");
                if (!string.IsNullOrEmpty(network)) sb.Append(",\"network\":\"").Append(Escape(network)).Append("\"");
                if (!string.IsNullOrEmpty(hostname)) sb.Append(",\"hostname\":\"").Append(Escape(hostname)).Append("\"");
                if (!string.IsNullOrEmpty(invite)) sb.Append(",\"invite\":\"").Append(Escape(invite)).Append("\"");
                sb.Append("}");
                PostJson(ServerBase + "/api/heartbeat", sb.ToString(), 2500);
            }
            catch { }
        }

        private static void Heartbeat(string easyTierDir, int rpcPort)
        {
            try
            {
                lock (BeatLock)
                {
                    if ((DateTime.UtcNow - LastBeat).TotalSeconds < 20) return;
                    LastBeat = DateTime.UtcNow;
                }
                if (string.IsNullOrEmpty(easyTierDir)) return;
                string dir = easyTierDir.TrimEnd('\\', '/');
                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent)) return;
                string authFile = Path.Combine(parent, "link_auth.dat");
                if (!File.Exists(authFile)) return;
                string device, token;
                ReadAuth(authFile, out device, out token);
                if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(token)) return;
                string network = GetNetworkName(dir, rpcPort);
                string invite = TryGetInviteCode();
                SendHeartbeat(device, token, network, Environment.MachineName, invite);
            }
            catch { }
        }

        private static string GetNetworkName(string easyTierDir, int rpcPort)
        {
            try
            {
                lock (NetLock)
                {
                    if (CachedNetwork != null && (DateTime.UtcNow - NetworkFetched).TotalSeconds < 120)
                        return CachedNetwork;
                }
                string exe = Path.Combine(easyTierDir, "联机模块 CLI.exe");
                if (!File.Exists(exe)) return null;
                ProcessStartInfo psi = new ProcessStartInfo(exe, "-o json -p 127.0.0.1:" + rpcPort + " node info");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.WorkingDirectory = easyTierDir;
                string output;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    Task<string> read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(2000))
                    {
                        try { p.Kill(); } catch { }
                        output = read.Wait(200) ? (read.Result ?? "") : "";
                    }
                    else
                    {
                        output = read.Wait(500) ? (read.Result ?? "") : "";
                    }
                }
                string name = null;
                Match m = Regex.Match(output, "network_name\\s*=\\s*\\\\?\"([^\"\\\\]+)\\\\?\"");
                if (m.Success) name = m.Groups[1].Value;
                lock (NetLock)
                {
                    CachedNetwork = name;
                    NetworkFetched = DateTime.UtcNow;
                }
                return name;
            }
            catch { return null; }
        }

        // ================= HTTP / JSON 工具 =================

        private static string TryGetInviteCode()
        {
            try
            {
                object page = _pageLink;
                if (page == null) return null;
                MethodInfo m = page.GetType().GetMethod("GetInviteCode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null) return null;
                object r = m.Invoke(page, null);
                string code = r as string;
                if (string.IsNullOrEmpty(code)) return null;
                code = code.Trim();
                if (code.Length < 14 || code.IndexOf('-') < 0) return null;
                return code;
            }
            catch { return null; }
        }

        private static string HttpGet(string url, int timeoutMs, out long rttMs)
        {
            rttMs = -1;
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    sw.Stop();
                    rttMs = sw.ElapsedMilliseconds;
                    return body;
                }
            }
            catch { return null; }
        }

        private static string PostJson(string url, string body, int timeoutMs)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream())
                {
                    s.Write(data, 0, data.Length);
                }
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (WebException we)
                {
                    HttpWebResponse resp = we.Response as HttpWebResponse;
                    if (resp != null)
                    {
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                    return null;
                }
            }
            catch { return null; }
        }

        private static string ExtractJson(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            i = json.IndexOf('"', i);
            if (i < 0) return null;
            int j = json.IndexOf('"', i + 1);
            if (j < 0) return null;
            return json.Substring(i + 1, j - i - 1);
        }

        private static string ExtractObject(string json, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return null;
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return null;
                i = json.IndexOf(':', i);
                if (i < 0) return null;
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length || json[i] != '{') return null;
                int depth = 0;
                for (int j = i; j < json.Length; j++)
                {
                    if (json[j] == '{') depth++;
                    else if (json[j] == '}')
                    {
                        depth--;
                        if (depth == 0) return json.Substring(i, j - i + 1);
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static List<string> ExtractArrayObjects(string json, string key)
        {
            List<string> result = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(json)) return result;
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return result;
                i = json.IndexOf('[', i);
                if (i < 0) return result;
                int depth = 0;
                bool inStr = false;
                bool esc = false;
                int objStart = -1;
                for (int j = i; j < json.Length; j++)
                {
                    char c = json[j];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') inStr = false;
                        continue;
                    }
                    if (c == '"') { inStr = true; continue; }
                    if (c == '{')
                    {
                        if (depth == 0) objStart = j;
                        depth++;
                    }
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0 && objStart >= 0)
                        {
                            result.Add(json.Substring(objStart, j - objStart + 1));
                            objStart = -1;
                        }
                    }
                    else if (c == ']' && depth == 0) break;
                }
            }
            catch { }
            return result;
        }

        private static double JsonDouble(string json, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return 0;
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return 0;
                i = json.IndexOf(':', i);
                if (i < 0) return 0;
                int j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                int start = j;
                if (j < json.Length && json[j] == '-') j++;
                while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '.')) j++;
                double v;
                if (j > start && double.TryParse(json.Substring(start, j - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
                return 0;
            }
            catch { return 0; }
        }

        private static bool JsonBool(string json, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return false;
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return false;
                i = json.IndexOf(':', i);
                if (i < 0) return false;
                string rest = json.Substring(i + 1).TrimStart();
                return rest.StartsWith("true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static long JsonNumber(string json, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return 0;
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return 0;
                i = json.IndexOf(':', i);
                if (i < 0) return 0;
                int j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                int start = j;
                if (j < json.Length && json[j] == '-') j++;
                while (j < json.Length && char.IsDigit(json[j])) j++;
                long v;
                if (j > start && long.TryParse(json.Substring(start, j - start), out v)) return v;
                return 0;
            }
            catch { return 0; }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string NormalizeCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Regex.Replace(text, "[^0-9A-Za-z]", "").ToUpperInvariant();
        }

        private static string FormatExpiry(long epochSeconds)
        {
            try
            {
                if (epochSeconds <= 0) return "-";
                DateTime dt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).LocalDateTime;
                return dt.ToString("yyyy-MM-dd");
            }
            catch { return "-"; }
        }

        // ================= 联机页验证面板 =================

        /// <summary>
        /// 在 PCL 联机页左上角创建/刷新验证面板。由注入到 PageLinkMain.StartTimerThread 的调用触发。
        /// </summary>
        public static void InitVerifyPanel(object page)
        {
            try
            {
                if (page == null) return;
                _pageLink = page;
                FieldInfo fi = page.GetType().GetField("_PanSelect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Grid grid = fi == null ? null : fi.GetValue(page) as Grid;
                if (grid == null) return;
                PanelRefs existing = null;
                RelayPanelRefs existingRight = null;
                RightColumnRefs existingColumn = null;
                foreach (UIElement child in grid.Children)
                {
                    FrameworkElement fe = child as FrameworkElement;
                    if (fe == null || fe.Tag == null) continue;
                    if (fe.Tag is PanelRefs) existing = fe.Tag as PanelRefs;
                    else if (fe.Tag is RelayPanelRefs) existingRight = fe.Tag as RelayPanelRefs;
                    else if (fe.Tag is RightColumnRefs) existingColumn = fe.Tag as RightColumnRefs;
                }
                if (existing == null)
                {
                    PanelRefs pr = BuildPanel();
                    grid.Children.Add(pr.Root);
                    Grid.SetRow(pr.Root, 0);
                    Grid.SetRowSpan(pr.Root, 7);
                    Grid.SetColumn(pr.Root, 0);
                    Grid.SetColumnSpan(pr.Root, 3);
                    pr.Root.Tag = pr;
                    _leftPanel = pr;
                    RefreshStatus(pr);
                }
                else
                {
                    _leftPanel = existing;
                    RefreshStatus(existing);
                }
                if (existingColumn == null)
                {
                    StackPanel stack = new StackPanel();
                    stack.HorizontalAlignment = HorizontalAlignment.Right;
                    stack.VerticalAlignment = VerticalAlignment.Top;
                    stack.Margin = new Thickness(0, 10, 20, 0);
                    RelayPanelRefs rp = BuildRelayPanel();
                    rp.Root.Margin = new Thickness(0);
                    rp.Root.HorizontalAlignment = HorizontalAlignment.Stretch;
                    HallPanelRefs hp = BuildHallPanel();
                    hp.Root.Margin = new Thickness(0, 8, 0, 0);
                    stack.Children.Add(rp.Root);
                    stack.Children.Add(hp.Root);
                    RightColumnRefs rc = new RightColumnRefs();
                    rc.Root = stack;
                    rc.Relay = rp;
                    rc.Hall = hp;
                    grid.Children.Add(stack);
                    Grid.SetRow(stack, 0);
                    Grid.SetRowSpan(stack, 7);
                    Grid.SetColumn(stack, 0);
                    Grid.SetColumnSpan(stack, 3);
                    stack.Tag = rc;
                    rp.Root.Tag = rp;
                    hp.Root.Tag = hp;
                    _rightColumn = rc;
                    _rightPanel = rp;
                    _hallPanel = hp;
                }
                else
                {
                    _rightColumn = existingColumn;
                    _rightPanel = existingColumn.Relay;
                    _hallPanel = existingColumn.Hall;
                }
                RefreshRelay(_rightPanel);
                RefreshHall(_hallPanel);
                if (_relayTimer == null)
                {
                    _relayTimer = new DispatcherTimer();
                    _relayTimer.Interval = TimeSpan.FromSeconds(15);
                    _relayTimer.Tick += delegate { RefreshRelay(_rightPanel); RefreshHall(_hallPanel); };
                    _relayTimer.Start();
                }
                if (_verifyTimer == null)
                {
                    _verifyTimer = new DispatcherTimer();
                    _verifyTimer.Interval = TimeSpan.FromSeconds(15);
                    _verifyTimer.Tick += delegate { if (!_verifying) RefreshStatus(_leftPanel, true); };
                    _verifyTimer.Start();
                }
            }
            catch { }
        }

        private static PanelRefs BuildPanel()
        {
            PanelRefs pr = new PanelRefs();
            Border root = new Border();
            root.Width = 300;
            root.Padding = new Thickness(11, 8, 11, 8);
            root.CornerRadius = new CornerRadius(8);
            root.Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
            root.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            root.BorderThickness = new Thickness(1);
            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.VerticalAlignment = VerticalAlignment.Top;
            root.Margin = new Thickness(20, 10, 0, 0);
            StackPanel sp = new StackPanel();
            root.Child = sp;

            TextBlock title = new TextBlock();
            title.Text = "联机接入";
            title.FontSize = 13;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            sp.Children.Add(title);

            TextBlock detail = new TextBlock();
            detail.FontSize = 11;
            detail.LineHeight = 17;
            detail.Margin = new Thickness(0, 4, 0, 0);
            detail.TextWrapping = TextWrapping.Wrap;
            detail.MaxWidth = 274;
            detail.Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
            detail.Text = "正在检查联机状态…";
            sp.Children.Add(detail);

            StackPanel inputRow = new StackPanel();
            inputRow.Orientation = Orientation.Horizontal;
            inputRow.Margin = new Thickness(0, 7, 0, 0);
            inputRow.Visibility = Visibility.Collapsed;
            TextBox codeBox = new TextBox();
            codeBox.Width = 168;
            codeBox.Height = 25;
            codeBox.FontSize = 12;
            codeBox.MaxLength = 23;
            codeBox.VerticalContentAlignment = VerticalAlignment.Center;
            codeBox.ToolTip = "格式：XXXX-XXXX-XXXX-XXXX（横杠可省略）";
            inputRow.Children.Add(codeBox);
            Button submit = new Button();
            submit.Content = "验证";
            submit.Padding = new Thickness(9, 1, 9, 1);
            submit.Margin = new Thickness(8, 0, 0, 0);
            submit.FontSize = 12;
            inputRow.Children.Add(submit);
            sp.Children.Add(inputRow);

            TextBlock status = new TextBlock();
            status.FontSize = 11;
            status.Margin = new Thickness(0, 4, 0, 0);
            status.TextWrapping = TextWrapping.Wrap;
            status.MaxWidth = 274;
            status.Visibility = Visibility.Collapsed;
            sp.Children.Add(status);

            TextBlock codeInfo = new TextBlock();
            codeInfo.FontSize = 10.5;
            codeInfo.Margin = new Thickness(0, 3, 0, 0);
            codeInfo.TextWrapping = TextWrapping.Wrap;
            codeInfo.MaxWidth = 274;
            codeInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            codeInfo.Visibility = Visibility.Collapsed;
            sp.Children.Add(codeInfo);

            pr.Root = root;
            pr.Detail = detail;
            pr.InputRow = inputRow;
            pr.CodeBox = codeBox;
            pr.Submit = submit;
            pr.Status = status;
            pr.CodeInfo = codeInfo;

            submit.Click += delegate { OnSubmit(pr); };
            codeBox.KeyDown += delegate (object sender, System.Windows.Input.KeyEventArgs e)
            {
                if (e.Key == System.Windows.Input.Key.Enter) OnSubmit(pr);
            };
            return pr;
        }

        private const string DefaultRelay = "tcp://106.55.39.109:11610";

        private static string GetSetupIniPath()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string dir = Path.GetDirectoryName(exe);
                return Path.Combine(dir, "PCL", "Setup.ini");
            }
            catch { return null; }
        }

        private static string ReadCustomPeer()
        {
            try
            {
                string file = GetSetupIniPath();
                if (file == null || !File.Exists(file)) return DefaultRelay;
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (line.StartsWith("LinkCustomPeer:", StringComparison.Ordinal))
                    {
                        string v = line.Substring("LinkCustomPeer:".Length).Trim();
                        if (v.Length > 0) return v;
                    }
                }
            }
            catch { }
            return DefaultRelay;
        }

        private static string MaskRelay(string relay)
        {
            try
            {
                if (string.IsNullOrEmpty(relay)) return relay;
                string scheme = "";
                string rest = relay;
                int idx = relay.IndexOf("://", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    scheme = relay.Substring(0, idx + 3);
                    rest = relay.Substring(idx + 3);
                }
                int colon = rest.LastIndexOf(':');
                string host = colon > 0 ? rest.Substring(0, colon) : rest;
                string port = colon > 0 ? rest.Substring(colon) : "";
                if (Regex.IsMatch(host, "^\\d{1,3}(\\.\\d{1,3}){3}$"))
                {
                    string[] parts = host.Split('.');
                    host = parts[0] + "." + parts[1] + ".***.***";
                }
                else
                {
                    if (host.Length > 6) host = host.Substring(0, 2) + "***" + host.Substring(host.Length - 2);
                    else host = "***";
                }
                return scheme + host + port;
            }
            catch { return relay; }
        }

        private static string NormalizeRelay(string text)
        {
            try
            {
                string v = (text ?? "").Trim();
                if (v.Length == 0) return null;
                if (v.IndexOf("://", StringComparison.Ordinal) < 0) v = "tcp://" + v;
                Match m = Regex.Match(v, "^(tcp|udp)://([^\\s/:]+):(\\d{1,5})$");
                if (!m.Success) return null;
                int port;
                if (!int.TryParse(m.Groups[3].Value, out port) || port < 1 || port > 65535) return null;
                return v;
            }
            catch { return null; }
        }

        private static bool TrySetPclSetting(string key, string value)
        {
            try
            {
                Assembly asm = Assembly.GetEntryAssembly();
                if (asm == null) return false;
                Type t = asm.GetType("PCL.Settings");
                if (t == null) return false;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "Set") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    m.Invoke(null, new object[] { key, value, null });
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static bool SaveCustomPeer(string value)
        {
            try
            {
                string file = GetSetupIniPath();
                if (file != null)
                {
                    List<string> lines = new List<string>();
                    if (File.Exists(file)) lines.AddRange(File.ReadAllLines(file, Encoding.UTF8));
                    bool found = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].StartsWith("LinkCustomPeer:", StringComparison.Ordinal))
                        {
                            lines[i] = "LinkCustomPeer:" + value;
                            found = true;
                            break;
                        }
                    }
                    if (!found) lines.Add("LinkCustomPeer:" + value);
                    string dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(file, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
                }
                return TrySetPclSetting("LinkCustomPeer", value);
            }
            catch { return false; }
        }

        private static long TestRelayTcp(string relay, int timeoutMs)
        {
            try
            {
                string v = relay ?? "";
                int idx = v.IndexOf("://", StringComparison.Ordinal);
                if (idx >= 0) v = v.Substring(idx + 3);
                int colon = v.LastIndexOf(':');
                if (colon <= 0) return -1;
                string host = v.Substring(0, colon);
                int port;
                if (!int.TryParse(v.Substring(colon + 1), out port)) return -1;
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    IAsyncResult ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return -1;
                    try { c.EndConnect(ar); } catch { return -1; }
                    sw.Stop();
                    return sw.ElapsedMilliseconds;
                }
            }
            catch { return -1; }
        }

        private static RelayPanelRefs BuildRelayPanel()
        {
            RelayPanelRefs rp = new RelayPanelRefs();
            Border root = new Border();
            root.Width = 300;
            root.Padding = new Thickness(11, 8, 11, 8);
            root.CornerRadius = new CornerRadius(8);
            root.Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
            root.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            root.BorderThickness = new Thickness(1);
            root.HorizontalAlignment = HorizontalAlignment.Right;
            root.VerticalAlignment = VerticalAlignment.Top;
            root.Margin = new Thickness(0, 10, 20, 0);
            StackPanel sp = new StackPanel();
            root.Child = sp;

            TextBlock title = new TextBlock();
            title.Text = "中继服务器";
            title.FontSize = 13;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            sp.Children.Add(title);

            TextBlock relayText = new TextBlock();
            relayText.FontSize = 11;
            relayText.Margin = new Thickness(0, 3, 0, 0);
            relayText.TextWrapping = TextWrapping.Wrap;
            relayText.MaxWidth = 274;
            relayText.Text = "中继：" + MaskRelay(ReadCustomPeer());
            relayText.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
            sp.Children.Add(relayText);

            TextBlock status = new TextBlock();
            status.FontSize = 11;
            status.Margin = new Thickness(0, 4, 0, 0);
            status.TextWrapping = TextWrapping.Wrap;
            status.MaxWidth = 274;
            status.Text = "检查中…";
            status.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            sp.Children.Add(status);

            TextBlock info = new TextBlock();
            info.FontSize = 11;
            info.Margin = new Thickness(0, 3, 0, 0);
            info.TextWrapping = TextWrapping.Wrap;
            info.MaxWidth = 274;
            info.Text = "在线玩家 - · 活跃房间 -";
            info.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            sp.Children.Add(info);

            TextBlock bandwidth = new TextBlock();
            bandwidth.FontSize = 10.5;
            bandwidth.Margin = new Thickness(0, 3, 0, 0);
            bandwidth.TextWrapping = TextWrapping.Wrap;
            bandwidth.MaxWidth = 274;
            bandwidth.Text = "服务器带宽 - / -";
            bandwidth.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            bandwidth.Visibility = Visibility.Collapsed;
            sp.Children.Add(bandwidth);

            Button edit = new Button();
            edit.Content = "修改中继地址";
            edit.FontSize = 11;
            edit.Padding = new Thickness(9, 1, 9, 1);
            edit.Margin = new Thickness(0, 4, 0, 0);
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            edit.Visibility = Visibility.Collapsed;
            sp.Children.Add(edit);

            StackPanel editRow = new StackPanel();
            editRow.Orientation = Orientation.Horizontal;
            editRow.Margin = new Thickness(0, 4, 0, 0);
            editRow.Visibility = Visibility.Collapsed;
            TextBox relayBox = new TextBox();
            relayBox.Width = 150;
            relayBox.Height = 24;
            relayBox.FontSize = 11.5;
            relayBox.VerticalContentAlignment = VerticalAlignment.Center;
            relayBox.ToolTip = "例如 tcp://1.2.3.4:11610";
            editRow.Children.Add(relayBox);
            Button save = new Button();
            save.Content = "保存";
            save.FontSize = 11;
            save.Padding = new Thickness(7, 1, 7, 1);
            save.Margin = new Thickness(5, 0, 0, 0);
            editRow.Children.Add(save);
            Button cancel = new Button();
            cancel.Content = "取消";
            cancel.FontSize = 11;
            cancel.Padding = new Thickness(7, 1, 7, 1);
            cancel.Margin = new Thickness(4, 0, 0, 0);
            editRow.Children.Add(cancel);

            rp.Root = root;
            rp.RelayText = relayText;
            rp.Status = status;
            rp.Info = info;
            rp.Bandwidth = bandwidth;
            rp.EditBtn = edit;
            rp.EditRow = editRow;
            rp.RelayBox = relayBox;
            rp.SaveBtn = save;
            rp.CancelBtn = cancel;
            rp.Container = sp;

            edit.Click += delegate
            {
                rp.RelayBox.Text = ReadCustomPeer();
                if (rp.Container != null && !rp.Container.Children.Contains(rp.EditRow))
                {
                    rp.Container.Children.Add(rp.EditRow);
                }
                rp.EditRow.Visibility = Visibility.Visible;
                rp.RelayBox.Focus();
            };
            cancel.Click += delegate { rp.EditRow.Visibility = Visibility.Collapsed; };
            save.Click += delegate
            {
                string v = NormalizeRelay(rp.RelayBox.Text);
                if (v == null)
                {
                    SetRelayStatus(rp, "格式错误，例如 tcp://1.2.3.4:11610", true);
                    return;
                }
                bool immediate = SaveCustomPeer(v);
                rp.RelayText.Text = "中继：" + MaskRelay(v);
                rp.EditRow.Visibility = Visibility.Collapsed;
                SetRelayStatus(rp, immediate ? "已保存，立即生效" : "已保存，重启 PCL 后生效", false);
                RefreshRelay(rp);
            };
            return rp;
        }

        private static void SetRelayStatus(RelayPanelRefs rp, string text, bool error)
        {
            rp.Status.Text = text;
            rp.Status.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xB2, 0x26, 0x00) : Color.FromRgb(0x0A, 0x7D, 0x33));
        }

        private static void RefreshRelay(RelayPanelRefs rp)
        {
            if (rp == null) return;
            string relay = ReadCustomPeer();
            if (rp.RelayText != null) rp.RelayText.Text = "中继：" + MaskRelay(relay);
            bool isDefault = string.Equals(relay, DefaultRelay, StringComparison.OrdinalIgnoreCase);
            if (!isDefault)
            {
                string addr = relay;
                rp.Info.Text = "自定义中继";
                rp.Bandwidth.Visibility = Visibility.Collapsed;
                SetRelayStatus(rp, "检查中…", false);
                Task.Run(delegate
                {
                    long ms = TestRelayTcp(addr, 2500);
                    RunOnUiRelay(rp, delegate
                    {
                        if (ms >= 0)
                        {
                            SetRelayStatus(rp, "● 自定义中继可连接 · " + ms + " ms", false);
                            rp.Info.Text = "自定义中继：无法获取在线人数/带宽数据";
                        }
                        else
                        {
                            SetRelayStatus(rp, "● 自定义中继无法连接", true);
                            rp.Info.Text = "请检查地址、端口和中继服务是否运行";
                        }
                    });
                });
                return;
            }
            Task.Run(delegate
            {
                long rtt = -1;
                string res = HttpGet(ServerBase + "/api/relay", 3000, out rtt);
                bool ok = res != null && JsonBool(res, "ok");
                long conns = res == null ? 0 : JsonNumber(res, "conns");
                long players = res == null ? 0 : JsonNumber(res, "players");
                long rooms = res == null ? 0 : JsonNumber(res, "rooms");
                double rx = res == null ? 0 : JsonDouble(res, "rx_mbps");
                double tx = res == null ? 0 : JsonDouble(res, "tx_mbps");
                RunOnUiRelay(rp, delegate
                {
                    if (ok)
                    {
                        SetRelayStatus(rp, "● 在线 · 延迟 " + rtt + " ms", false);
                        rp.Info.Text = "在线玩家 " + players + " · 活跃房间 " + rooms + " · 连接 " + conns;
                        rp.Bandwidth.Text = "服务器带宽 ↓ " + rx.ToString("0.0") + " / ↑ " + tx.ToString("0.0") + " Mbps";
                        rp.Bandwidth.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        SetRelayStatus(rp, "● 离线 · 无法连接", true);
                        rp.Info.Text = "请检查网络或服务器状态";
                        rp.Bandwidth.Visibility = Visibility.Collapsed;
                    }
                });
            });
        }
        private static HallPanelRefs BuildHallPanel()
        {
            HallPanelRefs hp = new HallPanelRefs();
            Border root = new Border();
            root.Width = 300;
            root.Padding = new Thickness(11, 8, 11, 8);
            root.CornerRadius = new CornerRadius(8);
            root.Background = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
            root.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
            root.BorderThickness = new Thickness(1);
            StackPanel sp = new StackPanel();
            root.Child = sp;

            TextBlock title = new TextBlock();
            title.Text = "联机大厅";
            title.FontSize = 13;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            sp.Children.Add(title);

            TextBlock status = new TextBlock();
            status.FontSize = 11;
            status.Margin = new Thickness(0, 3, 0, 0);
            status.TextWrapping = TextWrapping.Wrap;
            status.MaxWidth = 274;
            status.Text = "正在加载…";
            status.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            sp.Children.Add(status);

            TextBlock rooms = new TextBlock();
            rooms.FontSize = 11;
            rooms.Margin = new Thickness(0, 3, 0, 0);
            rooms.TextWrapping = TextWrapping.Wrap;
            rooms.MaxWidth = 274;
            rooms.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            sp.Children.Add(rooms);

            StackPanel roomButtons = new StackPanel();
            sp.Children.Add(roomButtons);

            TextBlock friends = new TextBlock();
            friends.FontSize = 10.5;
            friends.Margin = new Thickness(0, 3, 0, 0);
            friends.TextWrapping = TextWrapping.Wrap;
            friends.MaxWidth = 274;
            friends.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            sp.Children.Add(friends);

            TextBlock hint = new TextBlock();
            hint.FontSize = 10.5;
            hint.Margin = new Thickness(0, 2, 0, 0);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.MaxWidth = 274;
            hint.Visibility = Visibility.Collapsed;
            sp.Children.Add(hint);

            hp.Root = root;
            hp.Status = status;
            hp.Rooms = rooms;
            hp.RoomButtons = roomButtons;
            hp.Friends = friends;
            hp.Hint = hint;
            return hp;
        }

        private static void RefreshHall(HallPanelRefs hp)
        {
            if (hp == null) return;
            string authFile = AuthFilePath();
            string device, token;
            ReadAuth(authFile, out device, out token);
            if (string.IsNullOrEmpty(token))
            {
                hp.Status.Text = "尚未验证";
                hp.Rooms.Text = "验证后可查看在线房间和好友";
                hp.RoomButtons.Children.Clear();
                hp.Friends.Text = "";
                return;
            }
            string d = device, t = token;
            Task.Run(delegate
            {
                Stopwatch sw = Stopwatch.StartNew();
                string res = PostJson(ServerBase + "/api/hall",
                    "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(t) + "\"}", 4000);
                sw.Stop();
                long rtt = sw.ElapsedMilliseconds;
                RunOnUiHall(hp, delegate
                {
                    if (res == null)
                    {
                        hp.Status.Text = "● 大厅连接失败（网络）";
                        hp.Rooms.Text = "";
                        hp.RoomButtons.Children.Clear();
                        hp.Friends.Text = "";
                        return;
                    }
                    if (!JsonBool(res, "ok"))
                    {
                        string reason = ExtractJson(res, "reason");
                        hp.Status.Text = string.Equals(reason, "not_verified", StringComparison.OrdinalIgnoreCase)
                            ? "● 验证失效，无法查看大厅" : "● 大厅加载失败";
                        hp.Rooms.Text = "";
                        hp.RoomButtons.Children.Clear();
                        hp.Friends.Text = "";
                        return;
                    }
                    string server = ExtractObject(res, "server");
                    long players = server == null ? 0 : JsonNumber(server, "players");
                    long roomsN = server == null ? 0 : JsonNumber(server, "rooms");
                    hp.Status.Text = "● 在线 · 玩家 " + players + " · 房间 " + roomsN + " · 延迟 " + rtt + " ms";

                    hp.RoomButtons.Children.Clear();
                    List<string> roomObjs = ExtractArrayObjects(res, "rooms");
                    List<string> roomLines = new List<string>();
                    for (int i = 0; i < roomObjs.Count && i < 3; i++)
                    {
                        string ro = roomObjs[i];
                        string name = ExtractJson(ro, "name") ?? "?";
                        long pl = JsonNumber(ro, "players");
                        string invite = ExtractJson(ro, "invite");
                        roomLines.Add("• " + name + " · " + pl + " 人");
                        if (!string.IsNullOrEmpty(invite))
                        {
                            Button b = new Button();
                            b.Content = "复制「" + name + "」邀请码";
                            b.FontSize = 10.5;
                            b.Padding = new Thickness(6, 1, 6, 1);
                            b.Margin = new Thickness(0, 3, 0, 0);
                            b.HorizontalAlignment = HorizontalAlignment.Left;
                            string codeCopy = invite;
                            b.Click += delegate
                            {
                                try
                                {
                                    Clipboard.SetText(codeCopy);
                                    hp.Hint.Text = "邀请码已复制，去「加入房间」粘贴";
                                    hp.Hint.Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x7D, 0x33));
                                    hp.Hint.Visibility = Visibility.Visible;
                                }
                                catch { }
                            };
                            hp.RoomButtons.Children.Add(b);
                        }
                    }
                    hp.Rooms.Text = roomObjs.Count == 0 ? "当前没有房间" : string.Join("\n", roomLines.ToArray());

                    List<string> devObjs = ExtractArrayObjects(res, "devices");
                    List<string> names = new List<string>();
                    for (int i = 0; i < devObjs.Count && i < 6; i++)
                    {
                        string hn = ExtractJson(devObjs[i], "hostname") ?? "?";
                        string rm = ExtractJson(devObjs[i], "room") ?? "";
                        names.Add(string.IsNullOrEmpty(rm) ? hn : (hn + "(" + rm + ")"));
                    }
                    hp.Friends.Text = names.Count == 0 ? "在线好友：无" : ("在线好友：" + string.Join("、", names.ToArray()));
                });
            });
        }

        private static void RunOnUiHall(HallPanelRefs hp, Action action)
        {
            try
            {
                if (hp.Root.Dispatcher.CheckAccess()) action();
                else hp.Root.Dispatcher.BeginInvoke(action);
            }
            catch { }
        }

        private static Window _friendWindow = null;

        public const string PluginVersion = "1.1.0";

        /// <summary>在「关于与鸣谢」页的当前版本下面追加联机插件版本号。</summary>
        public static void InitAboutPatchInfo(object page)
        {
            try
            {
                if (page == null) return;
                FieldInfo fi = page.GetType().GetField("_ItemAboutPcl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fi == null) fi = page.GetType().GetField("ItemAboutPcl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object item = fi == null ? null : fi.GetValue(page);
                if (item == null) return;
                DependencyObject parentObj = VisualTreeHelper.GetParent(item as DependencyObject);
                Grid parentGrid = parentObj as Grid;
                if (parentGrid == null) return;
                foreach (UIElement child in parentGrid.Children)
                {
                    FrameworkElement fe = child as FrameworkElement;
                    if (fe != null && (fe.Tag as string) == "PclPluginVersion") return;
                }
                parentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock pluginText = new TextBlock();
                pluginText.Text = "联机插件版本：v" + PluginVersion + "（联机验证 / 大厅 / 聊天）";
                pluginText.FontSize = 12;
                pluginText.Opacity = 0.7;
                pluginText.Margin = new Thickness(0, 0, 0, 0);
                pluginText.Tag = "PclPluginVersion";
                Grid.SetRow(pluginText, parentGrid.RowDefinitions.Count - 1);
                Grid.SetColumn(pluginText, 1);
                parentGrid.Children.Add(pluginText);
            }
            catch { }
        }

        /// <summary>PCL 切换页面时会按页索引取 PanTitleSelect 的子元素；我们插入「好友」按钮后需要跳过它。</summary>
        public static object GetNavChild(object collection, int index)
        {
            try
            {
                System.Collections.IList list = collection as System.Collections.IList;
                if (list == null) return null;
                int target = index;
                for (int i = 0; i < list.Count; i++)
                {
                    FrameworkElement fe = list[i] as FrameworkElement;
                    if (fe != null && (fe.Tag as string) == "PclFriendNav")
                    {
                        if (i <= target) target++;
                        break;
                    }
                }
                if (target >= 0 && target < list.Count) return list[target];
                return null;
            }
            catch { return null; }
        }

        /// <summary>在联机页中只创建右侧的「中继服务器 + 联机大厅」面板（不创建验证面板）。</summary>
        public static void InitLinkPanels(object page)
        {
            try
            {
                if (page == null) return;
                _pageLink = page;
                FieldInfo fi = page.GetType().GetField("_PanSelect", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Grid grid = fi == null ? null : fi.GetValue(page) as Grid;
                if (grid == null) return;
                RightColumnRefs existingColumn = null;
                foreach (UIElement child in grid.Children)
                {
                    FrameworkElement fe = child as FrameworkElement;
                    if (fe != null && fe.Tag is RightColumnRefs) existingColumn = fe.Tag as RightColumnRefs;
                }
                if (existingColumn == null)
                {
                    StackPanel stack = new StackPanel();
                    stack.HorizontalAlignment = HorizontalAlignment.Right;
                    stack.VerticalAlignment = VerticalAlignment.Top;
                    stack.Margin = new Thickness(0, 10, 20, 0);
                    RelayPanelRefs rp = BuildRelayPanel();
                    rp.Root.Margin = new Thickness(0);
                    rp.Root.HorizontalAlignment = HorizontalAlignment.Stretch;
                    HallPanelRefs hp = BuildHallPanel();
                    hp.Root.Margin = new Thickness(0, 8, 0, 0);
                    stack.Children.Add(rp.Root);
                    stack.Children.Add(hp.Root);
                    RightColumnRefs rc = new RightColumnRefs();
                    rc.Root = stack;
                    rc.Relay = rp;
                    rc.Hall = hp;
                    grid.Children.Add(stack);
                    Grid.SetRow(stack, 0);
                    Grid.SetRowSpan(stack, 7);
                    Grid.SetColumn(stack, 0);
                    Grid.SetColumnSpan(stack, 3);
                    stack.Tag = rc;
                    rp.Root.Tag = rp;
                    hp.Root.Tag = hp;
                    _rightColumn = rc;
                    _rightPanel = rp;
                    _hallPanel = hp;
                }
                else
                {
                    _rightColumn = existingColumn;
                    _rightPanel = existingColumn.Relay;
                    _hallPanel = existingColumn.Hall;
                }
                RefreshRelay(_rightPanel);
                RefreshHall(_hallPanel);
                if (_relayTimer == null)
                {
                    _relayTimer = new DispatcherTimer();
                    _relayTimer.Interval = TimeSpan.FromSeconds(15);
                    _relayTimer.Tick += delegate { RefreshRelay(_rightPanel); RefreshHall(_hallPanel); };
                    _relayTimer.Start();
                }
            }
            catch (Exception ex) { NavLog("init link panels error: " + ex.Message); }
        }

        private static bool _heartbeatLoopStarted = false;

        /// <summary>启动后台心跳（上报房间名/邀请码，供大厅与在线统计使用）。</summary>
        public static void StartHeartbeat(string easyTierDir, int rpcPort)
        {
            if (_heartbeatLoopStarted) return;
            _heartbeatLoopStarted = true;
            Task.Run(delegate
            {
                while (true)
                {
                    try { Heartbeat(easyTierDir, rpcPort); } catch { }
                    try { Task.Delay(15000).Wait(); } catch { }
                }
            });
        }

        public static void InitFriendButton()
        {
            try
            {
                NavLog("InitFriendButton called");
                CreateFriendNavButton();
            }
            catch (Exception ex) { NavLog("init error: " + ex.Message); }
        }

        private static int _friendNavAttempts = 0;

        private static void NavLog(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "pclink_nav.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + text + "\r\n");
            }
            catch { }
        }

        private static FrameworkElement FindByName(DependencyObject parent, string name)
        {
            try
            {
                FrameworkElement fe = parent as FrameworkElement;
                if (fe != null && fe.Name == name) return fe;
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    FrameworkElement r = FindByName(VisualTreeHelper.GetChild(parent, i), name);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        private static void ScheduleFriendNavRetry()
        {
            try
            {
                if (_friendNavAttempts > 40) return;
                _friendNavAttempts++;
                DispatcherTimer t = new DispatcherTimer();
                t.Interval = TimeSpan.FromMilliseconds(300);
                t.Tick += delegate
                {
                    t.Stop();
                    CreateFriendNavButton();
                };
                t.Start();
            }
            catch { }
        }

        private static void CreateFriendNavButton()
        {
            try
            {
                if (Application.Current == null) { NavLog("app null"); ScheduleFriendNavRetry(); return; }
                Window win = Application.Current.MainWindow;
                if (win == null) { NavLog("window null"); ScheduleFriendNavRetry(); return; }
                StackPanel pan = FindByName(win, "PanTitleSelect") as StackPanel;
                if (pan == null) { NavLog("PanTitleSelect not found"); ScheduleFriendNavRetry(); return; }
                foreach (UIElement child in pan.Children)
                {
                    FrameworkElement fe = child as FrameworkElement;
                    if (fe != null && (fe.Tag as string) == "PclFriendNav") { NavLog("already exists"); return; }
                }
                Button btn = new Button();
                btn.Tag = "PclFriendNav";
                btn.Content = "好友";
                btn.FontSize = 15;
                btn.Foreground = Brushes.White;
                btn.Background = Brushes.Transparent;
                btn.BorderThickness = new Thickness(0);
                btn.Padding = new Thickness(10, 4, 10, 4);
                btn.Margin = new Thickness(5, 0, 5, 0);
                btn.Cursor = Cursors.Hand;
                btn.VerticalAlignment = VerticalAlignment.Center;
                btn.Click += delegate { ToggleFriendWindow(); };
                if (pan.Children.Count > 1) pan.Children.Insert(1, btn);
                else pan.Children.Add(btn);
                NavLog("inserted, count=" + pan.Children.Count);
            }
            catch (Exception ex) { NavLog("error: " + ex.Message); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        private static void FlashFriendWindow(Window w)
        {
            try
            {
                if (w == null) return;
                IntPtr handle = new WindowInteropHelper(w).Handle;
                if (handle == IntPtr.Zero) return;
                FLASHWINFO f = new FLASHWINFO();
                f.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
                f.hwnd = handle;
                f.dwFlags = 3;
                f.uCount = 3;
                f.dwTimeout = 0;
                FlashWindowEx(ref f);
            }
            catch { }
        }

        public static void ToggleFriendWindow()
        {
            try
            {
                NavLog("toggle friend window");
                if (_friendWindow != null)
                {
                    _friendWindow.Activate();
                    return;
                }
                Window w = new Window();
                w.Title = "好友";
                w.Width = 460;
                w.Height = 640;
                w.MinWidth = 380;
                w.MinHeight = 460;
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                w.Owner = Application.Current.MainWindow;
                w.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF7, 0xFD));
                w.ResizeMode = ResizeMode.CanResizeWithGrip;
                Grid host = new Grid();
                host.Margin = new Thickness(12, 10, 12, 12);
                ChatPanelRefs cp = BuildChatPanel(-1);
                host.Children.Add(cp.Root);
                w.Content = host;
                cp.Window = w;
                _friendWindow = w;
                _chatPanel = cp;
                w.Closed += delegate { _friendWindow = null; _chatPanel = null; };
                w.Show();
                RefreshChat(cp);
                if (_chatTimer == null)
                {
                    _chatTimer = new DispatcherTimer();
                    _chatTimer.Interval = TimeSpan.FromSeconds(3);
                    _chatTimer.Tick += delegate { if (_chatPanel != null) RefreshChat(_chatPanel); };
                    _chatTimer.Start();
                }
                NavLog("friend window shown");
            }
            catch (Exception ex) { NavLog("friend window error: " + ex.ToString()); }
        }

        private static ChatPanelRefs BuildChatPanel(int width)
        {
            ChatPanelRefs cp = new ChatPanelRefs();
            Border root = new Border();
            if (width > 0) { root.Width = width; root.HorizontalAlignment = HorizontalAlignment.Left; }
            else { root.HorizontalAlignment = HorizontalAlignment.Stretch; }
            root.Padding = new Thickness(14, 12, 14, 12);
            root.CornerRadius = new CornerRadius(8);
            root.Background = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255));
            root.BorderBrush = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
            root.BorderThickness = new Thickness(1);
            root.VerticalAlignment = VerticalAlignment.Stretch;
            Grid g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Child = g;

            StackPanel head = new StackPanel();
            TextBlock title = new TextBlock();
            title.Text = "好友与聊天";
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            head.Children.Add(title);
            TextBlock myCode = new TextBlock();
            myCode.FontSize = 11;
            myCode.Margin = new Thickness(0, 3, 0, 0);
            myCode.TextWrapping = TextWrapping.Wrap;
            myCode.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            myCode.Text = "我的好友码：加载中…";
            myCode.Cursor = Cursors.Hand;
            myCode.ToolTip = "点击复制我的好友码";
            head.Children.Add(myCode);
            g.Children.Add(head);
            Grid.SetRow(head, 0);

            StackPanel modeRow = new StackPanel();
            modeRow.Orientation = Orientation.Horizontal;
            modeRow.Margin = new Thickness(0, 8, 0, 0);
            Button modeGlobal = MakeChatModeButton("公共聊天");
            Button modeRoom = MakeChatModeButton("房间聊天");
            Button modePrivate = MakeChatModeButton("私聊");
            modeRow.Children.Add(modeGlobal);
            modeRow.Children.Add(modeRoom);
            modeRow.Children.Add(modePrivate);
            g.Children.Add(modeRow);
            Grid.SetRow(modeRow, 1);

            StackPanel privateRow = new StackPanel();
            privateRow.Orientation = Orientation.Horizontal;
            privateRow.Margin = new Thickness(0, 6, 0, 0);
            privateRow.Visibility = Visibility.Collapsed;
            ComboBox friendBox = new ComboBox();
            friendBox.Width = 220;
            friendBox.Height = 26;
            friendBox.FontSize = 12;
            privateRow.Children.Add(friendBox);
            Button addFriendBtn = new Button();
            addFriendBtn.Content = "加好友";
            addFriendBtn.FontSize = 11.5;
            addFriendBtn.Padding = new Thickness(9, 2, 9, 2);
            addFriendBtn.Margin = new Thickness(6, 0, 0, 0);
            privateRow.Children.Add(addFriendBtn);
            Button removeFriendBtn = new Button();
            removeFriendBtn.Content = "删除";
            removeFriendBtn.FontSize = 11.5;
            removeFriendBtn.Padding = new Thickness(9, 2, 9, 2);
            removeFriendBtn.Margin = new Thickness(6, 0, 0, 0);
            privateRow.Children.Add(removeFriendBtn);
            g.Children.Add(privateRow);
            Grid.SetRow(privateRow, 2);

            StackPanel addRow = new StackPanel();
            addRow.Orientation = Orientation.Horizontal;
            addRow.Margin = new Thickness(0, 6, 0, 0);
            addRow.Visibility = Visibility.Collapsed;
            TextBox friendCodeBox = new TextBox();
            friendCodeBox.Width = 160;
            friendCodeBox.Height = 26;
            friendCodeBox.FontSize = 12;
            friendCodeBox.VerticalContentAlignment = VerticalAlignment.Center;
            friendCodeBox.ToolTip = "输入对方的好友码，例如 ZKWC4H";
            addRow.Children.Add(friendCodeBox);
            Button confirmAdd = new Button();
            confirmAdd.Content = "添加好友";
            confirmAdd.FontSize = 11.5;
            confirmAdd.Padding = new Thickness(9, 2, 9, 2);
            confirmAdd.Margin = new Thickness(6, 0, 0, 0);
            addRow.Children.Add(confirmAdd);
            g.Children.Add(addRow);
            Grid.SetRow(addRow, 3);

            ListBox messageList = new ListBox();
            messageList.Margin = new Thickness(0, 8, 0, 0);
            messageList.MinHeight = 160;
            messageList.FontSize = 12.5;
            messageList.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFD));
            messageList.BorderBrush = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0));
            messageList.BorderThickness = new Thickness(1);
            messageList.Padding = new Thickness(6);
            messageList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            ScrollViewer.SetVerticalScrollBarVisibility(messageList, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(messageList, ScrollBarVisibility.Disabled);
            g.Children.Add(messageList);
            Grid.SetRow(messageList, 4);

            Grid statusRow = new Grid();
            statusRow.Margin = new Thickness(0, 6, 0, 0);
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock status = new TextBlock();
            status.FontSize = 11;
            status.TextWrapping = TextWrapping.Wrap;
            status.Text = "连接中…";
            status.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            statusRow.Children.Add(status);
            Grid.SetColumn(status, 0);
            TextBlock charCount = new TextBlock();
            charCount.FontSize = 10.5;
            charCount.Text = "0/200";
            charCount.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            charCount.VerticalAlignment = VerticalAlignment.Bottom;
            charCount.Margin = new Thickness(8, 0, 0, 0);
            statusRow.Children.Add(charCount);
            Grid.SetColumn(charCount, 1);
            g.Children.Add(statusRow);
            Grid.SetRow(statusRow, 5);

            Grid inputRow = new Grid();
            inputRow.Margin = new Thickness(0, 6, 0, 0);
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBox input = new TextBox();
            input.Height = 30;
            input.FontSize = 12.5;
            input.VerticalContentAlignment = VerticalAlignment.Center;
            input.MaxLength = 200;
            input.ToolTip = "输入消息后按回车发送（单条最多 200 字）";
            inputRow.Children.Add(input);
            Grid.SetColumn(input, 0);
            Button send = new Button();
            send.Content = "发送";
            send.FontSize = 12.5;
            send.Padding = new Thickness(16, 3, 16, 3);
            send.Margin = new Thickness(8, 0, 0, 0);
            inputRow.Children.Add(send);
            Grid.SetColumn(send, 1);
            g.Children.Add(inputRow);
            Grid.SetRow(inputRow, 6);

            cp.Root = root;
            cp.Title = title;
            cp.MyCode = myCode;
            cp.ModeGlobal = modeGlobal;
            cp.ModeRoom = modeRoom;
            cp.ModePrivate = modePrivate;
            cp.FriendBox = friendBox;
            cp.AddFriendBtn = addFriendBtn;
            cp.RemoveFriendBtn = removeFriendBtn;
            cp.PrivateRow = privateRow;
            cp.AddRow = addRow;
            cp.FriendCodeBox = friendCodeBox;
            cp.ConfirmAddBtn = confirmAdd;
            cp.MessageList = messageList;
            cp.CharCount = charCount;
            cp.Status = status;
            cp.Input = input;
            cp.Send = send;

            myCode.MouseLeftButtonUp += delegate
            {
                try { if (!string.IsNullOrEmpty(_myFriendCode)) Clipboard.SetText(_myFriendCode); } catch { }
            };
            modeGlobal.Click += delegate { SetChatMode(cp, "global"); };
            modeRoom.Click += delegate { SetChatMode(cp, "room"); };
            modePrivate.Click += delegate { SetChatMode(cp, "private"); };
            friendBox.SelectionChanged += delegate
            {
                if (_chatUpdatingFriends) return;
                try
                {
                    if (friendBox.SelectedIndex >= 0 && friendBox.SelectedIndex < _chatFriends.Count)
                    {
                        _chatFriendDevice = _chatFriends[friendBox.SelectedIndex].Device;
                        cp.AddRow.Visibility = Visibility.Collapsed;
                        _chatUnread = 0;
                        RebuildChatList(cp);
                        UpdateChatView(cp);
                    }
                }
                catch { }
            };
            addFriendBtn.Click += delegate
            {
                cp.AddRow.Visibility = cp.AddRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            };
            removeFriendBtn.Click += delegate { RemoveFriend(cp); };
            confirmAdd.Click += delegate { AddFriend(cp); };
            send.Click += delegate { SendChat(cp); };
            input.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { e.Handled = true; SendChat(cp); }
            };
            input.TextChanged += delegate
            {
                try { cp.CharCount.Text = input.Text.Length + "/200"; } catch { }
            };
            SetChatMode(cp, "global");
            return cp;
        }

        private static Button MakeChatModeButton(string text)
        {
            Button b = new Button();
            b.Content = text;
            b.FontSize = 12;
            b.Padding = new Thickness(12, 3, 12, 3);
            b.Margin = new Thickness(0, 0, 6, 0);
            return b;
        }

        private static void SetChatMode(ChatPanelRefs cp, string mode)
        {
            _chatMode = mode;
            _chatUnread = 0;
            UpdateModeButtons(cp);
            cp.PrivateRow.Visibility = mode == "private" ? Visibility.Visible : Visibility.Collapsed;
            if (mode == "private")
            {
                cp.AddRow.Visibility = Visibility.Collapsed;
                RefreshFriendBox(cp);
                if (_chatFriendDevice == null && _chatFriends.Count > 0)
                {
                    _chatFriendDevice = _chatFriends[0].Device;
                }
            }
            RebuildChatList(cp);
            UpdateChatView(cp);
        }

        private static void UpdateModeButtons(ChatPanelRefs cp)
        {
            try
            {
                Brush on = new SolidColorBrush(Color.FromRgb(0x2D, 0x7D, 0xFF));
                Brush off = Brushes.White;
                Brush onFg = Brushes.White;
                Brush offFg = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
                cp.ModeGlobal.Background = _chatMode == "global" ? on : off;
                cp.ModeGlobal.Foreground = _chatMode == "global" ? onFg : offFg;
                cp.ModeRoom.Background = _chatMode == "room" ? on : off;
                cp.ModeRoom.Foreground = _chatMode == "room" ? onFg : offFg;
                cp.ModePrivate.Background = _chatMode == "private" ? on : off;
                cp.ModePrivate.Foreground = _chatMode == "private" ? onFg : offFg;
            }
            catch { }
        }

        private static void RefreshFriendBox(ChatPanelRefs cp)
        {
            try
            {
                _chatUpdatingFriends = true;
                cp.FriendBox.Items.Clear();
                for (int i = 0; i < _chatFriends.Count; i++)
                {
                    ChatFriendInfo f = _chatFriends[i];
                    string label = (f.Online ? "● " : "○ ") + f.Name + (f.Online ? "（在线" : "（离线");
                    if (!string.IsNullOrEmpty(f.Room) && f.Online) label += " · " + f.Room;
                    label += "）";
                    cp.FriendBox.Items.Add(label);
                }
                int sel = 0;
                for (int i = 0; i < _chatFriends.Count; i++)
                {
                    if (_chatFriends[i].Device == _chatFriendDevice) { sel = i; break; }
                }
                if (_chatFriends.Count > 0)
                {
                    cp.FriendBox.SelectedIndex = sel;
                    _chatFriendDevice = _chatFriends[sel].Device;
                }
                else
                {
                    _chatFriendDevice = null;
                }
            }
            catch { }
            finally { _chatUpdatingFriends = false; }
        }

        private static bool ChatMatches(ChatLineInfo m)
        {
            if (_chatMode == "global") return m.Scope == "global" || string.IsNullOrEmpty(m.Scope);
            if (_chatMode == "room") return m.Scope == "room";
            if (_chatMode == "private")
                return m.Scope == "private" && _chatFriendDevice != null &&
                       (m.FromDevice == _chatFriendDevice || m.To == _chatFriendDevice);
            return false;
        }

        private static string FormatChatLine(ChatLineInfo m)
        {
            string time = "";
            try
            {
                if (m.Ts > 0) time = "[" + DateTimeOffset.FromUnixTimeSeconds(m.Ts).ToLocalTime().ToString("HH:mm") + "] ";
            }
            catch { }
            string prefix = m.Scope == "private" ? "【私聊】" : (m.Scope == "room" ? "【房间】" : "");
            string name = m.FromDevice == _myDevice ? "我" : m.FromName;
            return time + prefix + name + "：" + m.Text;
        }

        private static void RebuildChatList(ChatPanelRefs cp)
        {
            try
            {
                if (cp == null || cp.MessageList == null) return;
                cp.MessageList.Items.Clear();
                int shown = 0;
                for (int i = _chatMsgs.Count - 1; i >= 0 && shown < 100; i--)
                {
                    ChatLineInfo m = _chatMsgs[i];
                    if (!ChatMatches(m)) continue;
                    shown++;
                }
                int total = 0;
                for (int i = 0; i < _chatMsgs.Count; i++) if (ChatMatches(_chatMsgs[i])) total++;
                int start = Math.Max(0, total - 100);
                int seen = 0;
                for (int i = 0; i < _chatMsgs.Count; i++)
                {
                    ChatLineInfo m = _chatMsgs[i];
                    if (!ChatMatches(m)) continue;
                    if (seen < start) { seen++; continue; }
                    seen++;
                    TextBlock tb = new TextBlock();
                    tb.Text = FormatChatLine(m);
                    tb.TextWrapping = TextWrapping.Wrap;
                    tb.FontSize = 12.5;
                    tb.Margin = new Thickness(2, 3, 2, 3);
                    if (m.FromDevice == _myDevice) tb.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x5F, 0xBF));
                    else tb.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                    ListBoxItem item = new ListBoxItem();
                    item.Content = tb;
                    item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    cp.MessageList.Items.Add(item);
                }
                if (cp.MessageList.Items.Count > 0)
                {
                    cp.MessageList.ScrollIntoView(cp.MessageList.Items[cp.MessageList.Items.Count - 1]);
                }
            }
            catch { }
        }

        private static void UpdateChatView(ChatPanelRefs cp)
        {
            try
            {
                if (cp == null) return;
                string status;
                if (_chatMode == "private")
                {
                    string nm = "未选择好友";
                    for (int i = 0; i < _chatFriends.Count; i++)
                    {
                        if (_chatFriends[i].Device == _chatFriendDevice) { nm = _chatFriends[i].Name; break; }
                    }
                    status = "● 私聊：" + nm;
                }
                else if (_chatMode == "room")
                {
                    status = string.IsNullOrEmpty(_chatRoomName) ? "● 当前未加入房间" : ("● 房间：" + _chatRoomName);
                }
                else
                {
                    status = "● 公共聊天室";
                }
                int online = 0;
                for (int i = 0; i < _chatFriends.Count; i++) if (_chatFriends[i].Online) online++;
                status += " · 好友 " + _chatFriends.Count + "（在线 " + online + "）";
                if (_chatUnread > 0) status += " · 新消息 " + _chatUnread;
                cp.Status.Text = status;
                cp.Status.Foreground = _chatUnread > 0
                    ? new SolidColorBrush(Color.FromRgb(0xB2, 0x26, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                string title = "好友与聊天";
                if (_chatUnread > 0) title += "（新消息 " + _chatUnread + "）";
                cp.Title.Text = title;
                if (cp.Window != null && _chatUnread > 0) cp.Window.Title = title;
                cp.MyCode.Text = string.IsNullOrEmpty(_myFriendCode)
                    ? "我的好友码：加载中…" : ("我的好友码：" + _myFriendCode + "（点击复制）");
            }
            catch { }
        }

        private static void RefreshChat(ChatPanelRefs cp)
        {
            if (cp == null) return;
            string authFile = AuthFilePath();
            string device, token;
            ReadAuth(authFile, out device, out token);
            if (string.IsNullOrEmpty(token))
            {
                cp.Status.Text = "验证后可以聊天";
                return;
            }
            string d = device, t = token;
            _myDevice = device;
            long since = _chatSeq;
            Task.Run(delegate
            {
                string res = PostJson(ServerBase + "/api/chat/poll",
                    "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(t) + "\",\"since\":" + since + "}", 4000);
                RunOnUiChat(cp, delegate
                {
                    if (_chatPanel != cp) return;
                    if (res == null) { cp.Status.Text = "● 聊天连接失败（网络）"; return; }
                    if (!JsonBool(res, "ok"))
                    {
                        string reason = ExtractJson(res, "reason");
                        cp.Status.Text = string.Equals(reason, "not_verified", StringComparison.OrdinalIgnoreCase)
                            ? "● 验证失效，无法聊天" : "● 聊天加载失败";
                        return;
                    }
                    string myCode = ExtractJson(res, "my_code");
                    if (!string.IsNullOrEmpty(myCode)) _myFriendCode = myCode;
                    _chatRoomName = ExtractJson(res, "room") ?? "";
                    List<string> fObjs = ExtractArrayObjects(res, "friends");
                    _chatFriends.Clear();
                    foreach (string fo in fObjs)
                    {
                        ChatFriendInfo f = new ChatFriendInfo();
                        f.Device = ExtractJson(fo, "device") ?? "";
                        f.Name = ExtractJson(fo, "name") ?? "?";
                        f.Online = JsonBool(fo, "online");
                        f.Room = ExtractJson(fo, "room") ?? "";
                        _chatFriends.Add(f);
                    }
                    List<string> mObjs = ExtractArrayObjects(res, "messages");
                    bool firstLoad = (_chatSeq == 0 && _chatMsgs.Count == 0);
                    int matchedNew = 0;
                    foreach (string mo in mObjs)
                    {
                        ChatLineInfo m = new ChatLineInfo();
                        m.Id = JsonNumber(mo, "id");
                        m.Ts = JsonNumber(mo, "ts");
                        m.Scope = ExtractJson(mo, "scope");
                        m.Room = ExtractJson(mo, "room") ?? "";
                        m.FromDevice = ExtractJson(mo, "device") ?? "";
                        m.FromName = ExtractJson(mo, "name") ?? "?";
                        m.To = ExtractJson(mo, "to") ?? "";
                        m.Text = ExtractJson(mo, "text") ?? "";
                        _chatMsgs.Add(m);
                        if (ChatMatches(m)) matchedNew++;
                        else if (m.FromDevice != d) _chatUnread++;
                    }
                    if (_chatMsgs.Count > 200) _chatMsgs.RemoveRange(0, _chatMsgs.Count - 200);
                    if (firstLoad) _chatUnread = 0;
                    long seq = JsonNumber(res, "seq");
                    if (seq > _chatSeq) _chatSeq = seq;
                    if (_chatMode == "private") RefreshFriendBox(cp);
                    if (matchedNew > 0) RebuildChatList(cp);
                    UpdateChatView(cp);
                    cp.Send.IsEnabled = true;
                    if (_chatUnread > 0 && cp.Window != null && !cp.Window.IsActive) FlashFriendWindow(cp.Window);
                });
            });
        }

        private static void SendChat(ChatPanelRefs cp)
        {
            if (cp == null) return;
            string text = (cp.Input.Text ?? "").Trim();
            if (text.Length == 0) { cp.Status.Text = "请输入内容"; return; }
            string authFile = AuthFilePath();
            string device, token;
            ReadAuth(authFile, out device, out token);
            if (string.IsNullOrEmpty(token)) { cp.Status.Text = "验证后可以聊天"; return; }
            string scope = _chatMode;
            string to = "";
            if (scope == "room" && string.IsNullOrEmpty(_chatRoomName)) { cp.Status.Text = "你还没有加入房间"; return; }
            if (scope == "private")
            {
                if (string.IsNullOrEmpty(_chatFriendDevice)) { cp.Status.Text = "请先添加/选择好友"; return; }
                to = _chatFriendDevice;
            }
            string d = device, t = token;
            cp.Send.IsEnabled = false;
            cp.Status.Text = "发送中…";
            Task.Run(delegate
            {
                string body = "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(t) +
                              "\",\"scope\":\"" + Escape(scope) + "\"";
                if (!string.IsNullOrEmpty(to)) body += ",\"to\":\"" + Escape(to) + "\"";
                body += ",\"text\":\"" + Escape(text) + "\"}";
                string res = PostJson(ServerBase + "/api/chat/send", body, 4000);
                RunOnUiChat(cp, delegate
                {
                    if (_chatPanel != cp) return;
                    cp.Send.IsEnabled = true;
                    if (res != null && JsonBool(res, "ok"))
                    {
                        cp.Input.Text = "";
                        _chatUnread = 0;
                        RefreshChat(cp);
                    }
                    else
                    {
                        string err = res == null ? "网络错误" : (ExtractJson(res, "error") ?? "发送失败");
                        cp.Status.Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x26, 0x00));
                        cp.Status.Text = "● " + err;
                    }
                });
            });
        }

        private static void AddFriend(ChatPanelRefs cp)
        {
            string code = (cp.FriendCodeBox.Text ?? "").Trim();
            if (code.Length == 0) { cp.Status.Text = "请输入好友码"; return; }
            string authFile = AuthFilePath();
            string device, token;
            ReadAuth(authFile, out device, out token);
            if (string.IsNullOrEmpty(token)) { cp.Status.Text = "验证后可以添加好友"; return; }
            string d = device, t = token;
            cp.ConfirmAddBtn.IsEnabled = false;
            cp.Status.Text = "添加中…";
            Task.Run(delegate
            {
                string body = "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(t) +
                              "\",\"code\":\"" + Escape(code) + "\"}";
                string res = PostJson(ServerBase + "/api/friend/add", body, 4000);
                RunOnUiChat(cp, delegate
                {
                    if (_chatPanel != cp) return;
                    cp.ConfirmAddBtn.IsEnabled = true;
                    if (res != null && JsonBool(res, "ok"))
                    {
                        cp.FriendCodeBox.Text = "";
                        cp.AddRow.Visibility = Visibility.Collapsed;
                        cp.Status.Text = "● 好友添加成功";
                        RefreshChat(cp);
                    }
                    else
                    {
                        string err = res == null ? "网络错误" : (ExtractJson(res, "error") ?? "添加失败");
                        cp.Status.Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x26, 0x00));
                        cp.Status.Text = "● " + err;
                    }
                });
            });
        }

        private static void RemoveFriend(ChatPanelRefs cp)
        {
            if (cp == null || string.IsNullOrEmpty(_chatFriendDevice)) { if (cp != null) cp.Status.Text = "请先选择好友"; return; }
            string name = "该好友";
            for (int i = 0; i < _chatFriends.Count; i++) if (_chatFriends[i].Device == _chatFriendDevice) name = _chatFriends[i].Name;
            if (MessageBox.Show("确定要删除好友「" + name + "」吗？", "好友", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            string authFile = AuthFilePath();
            string device, token;
            ReadAuth(authFile, out device, out token);
            if (string.IsNullOrEmpty(token)) return;
            string d = device, t = token, to = _chatFriendDevice;
            cp.Status.Text = "删除中…";
            Task.Run(delegate
            {
                string body = "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(t) + "\",\"to\":\"" + Escape(to) + "\"}";
                string res = PostJson(ServerBase + "/api/friend/remove", body, 4000);
                RunOnUiChat(cp, delegate
                {
                    if (_chatPanel != cp) return;
                    if (res != null && JsonBool(res, "ok"))
                    {
                        _chatFriendDevice = null;
                        cp.Status.Text = "● 已删除好友";
                        RefreshChat(cp);
                    }
                    else
                    {
                        cp.Status.Text = "● 删除失败";
                    }
                });
            });
        }

        private static void RunOnUiChat(ChatPanelRefs cp, Action action)
        {
            try
            {
                if (cp.Root.Dispatcher.CheckAccess()) action();
                else cp.Root.Dispatcher.BeginInvoke(action);
            }
            catch { }
        }
        private static void RunOnUiRelay(RelayPanelRefs rp, Action action)
        {
            try
            {
                if (rp.Root.Dispatcher.CheckAccess()) action();
                else rp.Root.Dispatcher.BeginInvoke(action);
            }
            catch { }
        }

        private static void AutoVerify(PanelRefs pr)
        {
            if (_autoVerifying) return;
            _autoVerifying = true;
            string authFile = AuthFilePath();
            string device, token0;
            ReadAuth(authFile, out device, out token0);
            if (string.IsNullOrEmpty(device))
            {
                device = "pc-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                WriteAuth(authFile, device, "");
            }
            string d = device;
            pr.InputRow.Visibility = Visibility.Collapsed;
            pr.Detail.Text = "正在自动接入（无需验证码）…";
            pr.Detail.Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
            pr.CodeInfo.Text = "";
            pr.CodeInfo.Visibility = Visibility.Collapsed;
            SetStatus(pr, "正在连接服务器…", false);
            Task.Run(delegate
            {
                string res = PostJson(ServerBase + "/api/verify", "{\"device\":\"" + Escape(d) + "\"}", 6000);
                bool ok = res != null && JsonBool(res, "ok");
                string token = res == null ? null : ExtractJson(res, "token");
                RunOnUi(pr, delegate
                {
                    _autoVerifying = false;
                    if (ok && !string.IsNullOrEmpty(token))
                    {
                        WriteAuth(authFile, d, token);
                        RefreshStatus(pr);
                    }
                    else
                    {
                        pr.InputRow.Visibility = Visibility.Collapsed;
                        pr.Detail.Text = "自动接入失败：请检查网络，稍后自动重试。";
                        pr.Detail.Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x26, 0x00));
                        pr.CodeInfo.Text = "";
                        pr.CodeInfo.Visibility = Visibility.Collapsed;
                        SetStatus(pr, "", false);
                    }
                });
            });
        }

        private static void RefreshStatus(PanelRefs pr)
        {
            RefreshStatus(pr, false);
        }

        private static void RefreshStatus(PanelRefs pr, bool silent)
        {
            string authFile = AuthFilePath();
            string device, token;
            ReadAuth(authFile, out device, out token);
            if (string.IsNullOrEmpty(device))
            {
                device = "pc-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                WriteAuth(authFile, device, "");
                token = "";
            }
            if (string.IsNullOrEmpty(token))
            {
                AutoVerify(pr);
                return;
            }
            if (!silent)
            {
                pr.Detail.Text = "正在检查验证状态…";
                pr.InputRow.Visibility = Visibility.Collapsed;
                SetStatus(pr, "正在连接验证服务器…", false);
            }
            string d = device, t = token;
            Task.Run(delegate
            {
                string res = PostJson(ServerBase + "/api/status",
                    "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(t) + "\"}", 6000);
                long expires = 0;
                bool ok = false;
                string codeObj = null;
                string codeState = null;
                string codeDisplay = null;
                long serverTime = 0;
                bool admitted = true;
                bool queued = false;
                long position = 0;
                long players = 0;
                long capacity = 300;
                if (res != null)
                {
                    ok = JsonBool(res, "ok");
                    if (ok)
                    {
                        expires = JsonNumber(res, "expires");
                        codeObj = ExtractObject(res, "code");
                        codeState = ExtractJson(res, "code_state");
                        codeDisplay = ExtractJson(res, "code_display");
                        serverTime = JsonNumber(res, "server_time");
                        admitted = JsonBool(res, "admitted");
                        queued = JsonBool(res, "queued");
                        position = JsonNumber(res, "position");
                        players = JsonNumber(res, "players");
                        capacity = JsonNumber(res, "capacity");
                        if (capacity <= 0) capacity = 300;
                        SendHeartbeat(d, t, null, Environment.MachineName);
                    }
                }
                string reason = res == null ? null : ExtractJson(res, "reason");
                string invalidState = res == null ? null : ExtractJson(res, "code_state");
                RunOnUi(pr, delegate
                {
                    if (res == null)
                    {
                        ShowInput(pr, "无法连接验证服务器，请检查网络后重试。", true);
                    }
                    else if (ok)
                    {
                        ShowVerified(pr, expires, codeObj, codeState, codeDisplay, serverTime, admitted, queued, position, players, capacity);
                    }
                    else if (string.Equals(reason, "code_invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        string msg;
                        if (string.Equals(invalidState, "expired", StringComparison.OrdinalIgnoreCase))
                            msg = "验证码已过期，请使用新的验证码重新验证。";
                        else if (string.Equals(invalidState, "exhausted", StringComparison.OrdinalIgnoreCase))
                            msg = "验证码可用次数已用完，请使用新的验证码重新验证。";
                        else if (string.Equals(invalidState, "deleted", StringComparison.OrdinalIgnoreCase))
                            msg = "验证码已被房主删除，请使用新的验证码重新验证。";
                        else
                            msg = "验证码已失效，请使用新的验证码重新验证。";
                        ShowInput(pr, msg, true);
                    }
                    else
                    {
                        ShowInput(pr, "验证已失效，请输入新的验证码重新验证。", true);
                    }
                });
            });
        }

        private static string MaskCode(string codeText)
        {
            try
            {
                if (string.IsNullOrEmpty(codeText) || codeText == "-") return "-";
                StringBuilder sb = new StringBuilder();
                foreach (char ch in codeText)
                {
                    if (ch == '-') sb.Append('-');
                    else sb.Append('*');
                }
                return sb.ToString();
            }
            catch { return "****"; }
        }

        private static void ShowVerified(PanelRefs pr, long expires, string codeObj, string codeState, string codeDisplay, long serverTime)
        {
            ShowVerified(pr, expires, codeObj, codeState, codeDisplay, serverTime, true, false, 0, 0, 300);
        }

        private static void ShowVerified(PanelRefs pr, long expires, string codeObj, string codeState, string codeDisplay, long serverTime,
                                         bool admitted, bool queued, long position, long players, long capacity)
        {
            pr.InputRow.Visibility = Visibility.Collapsed;
            if (capacity <= 0) capacity = 300;
            if (queued)
            {
                long ahead = position > 0 ? position - 1 : 0;
                pr.Detail.Text = "排队中 · 当前在线 " + players + " / " + capacity + "\n前面还有 " + ahead + " 人，空出位置后自动接入";
                pr.Detail.Foreground = new SolidColorBrush(Color.FromRgb(0xB2, 0x6A, 0x00));
                SetStatus(pr, "请保持联机页打开，每 15 秒自动检查一次。", false);
                pr.CodeInfo.Text = "已关闭验证码验证，无需输入验证码";
                pr.CodeInfo.Visibility = Visibility.Visible;
                return;
            }
            pr.Detail.Text = "已接入 · 本机有效期至 " + FormatExpiry(expires) + "（自动续期）";
            pr.Detail.Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x7D, 0x33));
            SetStatus(pr, "", false);
            pr.CodeInfo.Text = "已关闭验证码验证 · 当前在线 " + players + " / " + capacity;
            pr.CodeInfo.Visibility = Visibility.Visible;
        }

        private static void ShowInput(PanelRefs pr, string message)
        {
            ShowInput(pr, message, false);
        }

        private static void ShowInput(PanelRefs pr, string message, bool error)
        {
            pr.InputRow.Visibility = Visibility.Collapsed;
            pr.Detail.Text = message;
            pr.Detail.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xB2, 0x26, 0x00) : Color.FromRgb(0x77, 0x77, 0x77));
            pr.Status.Text = "";
            pr.Status.Visibility = Visibility.Collapsed;
            pr.CodeInfo.Text = "";
            pr.CodeInfo.Visibility = Visibility.Collapsed;
            pr.Submit.IsEnabled = true;
        }

        private static void SetStatus(PanelRefs pr, string text, bool error)
        {
            pr.Status.Text = text ?? "";
            pr.Status.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xB2, 0x26, 0x00) : Color.FromRgb(0x88, 0x88, 0x88));
            pr.Status.Visibility = string.IsNullOrEmpty(pr.Status.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void OnSubmit(PanelRefs pr)
        {
            try
            {
                string code = NormalizeCode(pr.CodeBox.Text);
                if (string.IsNullOrEmpty(code))
                {
                    SetStatus(pr, "请输入验证码", true);
                    return;
                }
                if (_verifying) return;
                _verifying = true;
                pr.Submit.IsEnabled = false;
                SetStatus(pr, "正在验证…", false);
                string authFile = AuthFilePath();
                string device, token;
                ReadAuth(authFile, out device, out token);
                if (string.IsNullOrEmpty(device))
                {
                    device = "pc-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                    WriteAuth(authFile, device, "");
                }
                string d = device, c = code;
                Task.Run(delegate
                {
                    string res = PostJson(ServerBase + "/api/verify",
                        "{\"code\":\"" + Escape(c) + "\",\"device\":\"" + Escape(d) + "\"}", 15000);
                    bool ok = res != null && JsonBool(res, "ok");
                    string token2 = res == null ? null : ExtractJson(res, "token");
                    string err = res == null ? "无法连接验证服务器" : (ExtractJson(res, "error") ?? "验证失败");
                    long expires = 0;
                    string codeObj = null;
                    string codeState = null;
                    string codeDisplay = null;
                    long serverTime = 0;
                    if (ok && res != null)
                    {
                        string st = PostJson(ServerBase + "/api/status",
                            "{\"device\":\"" + Escape(d) + "\",\"token\":\"" + Escape(token2 ?? "") + "\"}", 6000);
                        if (st != null && JsonBool(st, "ok"))
                        {
                            expires = JsonNumber(st, "expires");
                            codeObj = ExtractObject(st, "code");
                            codeState = ExtractJson(st, "code_state");
                            codeDisplay = ExtractJson(st, "code_display");
                            serverTime = JsonNumber(st, "server_time");
                        }
                        WriteAuth(authFile, d, token2 ?? "");
                        SendHeartbeat(d, token2 ?? "", null, Environment.MachineName);
                    }
                    RunOnUi(pr, delegate
                    {
                        _verifying = false;
                        if (ok)
                        {
                            ShowVerified(pr, expires, codeObj, codeState, codeDisplay, serverTime);
                            pr.CodeBox.Text = "";
                        }
                        else
                        {
                            pr.Submit.IsEnabled = true;
                            SetStatus(pr, "验证失败：" + err, true);
                        }
                    });
                });
            }
            catch
            {
                pr.Submit.IsEnabled = true;
            }
        }

        private static void RunOnUi(PanelRefs pr, Action action)
        {
            try
            {
                if (pr.Root.Dispatcher.CheckAccess()) action();
                else pr.Root.Dispatcher.BeginInvoke(action);
            }
            catch { }
        }

        // ================= EasyTier CLI =================

        /// <summary>
        /// Runs the EasyTier CLI ("联机模块 CLI.exe") in the given directory and returns its stdout JSON.
        /// Replaces the function that was stripped from the official PCL build.
        /// </summary>
        public static string GetPeersJson(string easyTierDir, int rpcPort)
        {
            Heartbeat(easyTierDir, rpcPort);
            try
            {
                string exe = Path.Combine(easyTierDir ?? string.Empty, "联机模块 CLI.exe");
                if (!File.Exists(exe)) return "";
                ProcessStartInfo psi = new ProcessStartInfo(exe, "-o json -p 127.0.0.1:" + rpcPort + " peer");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.WorkingDirectory = easyTierDir;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    Task<string> read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        return read.Wait(200) ? (read.Result ?? "") : "";
                    }
                    return read.Wait(500) ? (read.Result ?? "") : "";
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
