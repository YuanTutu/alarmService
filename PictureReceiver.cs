// PictureReceiver.exe - 报警服务器模拟器 (单文件界面版)
//
// 作者:     yuanbo6
// 开发时间: 2026-09-10
// 版本:     v2.2
//   v1.0  控制台版: http/https 接收摄像头抓拍推送, multipart 内存拆包落盘
//   v2.0  界面版重构: WinForms 界面配置, 内嵌 https 证书, 单 exe 交付
//   v2.1  标题改为"报警服务器模拟器", 窗口横向放宽, 按钮文字完整显示
//   v2.2  按钮高度与保存目录框运行时对齐 (兼容高 DPI 缩放)
//
// 功能:
//   1. 图形界面配置协议(http/https)/监听IP/端口/保存目录, 点"应用并重启监听"生效
//   2. URL 固定为 /test; 收到 multipart/form-data(人脸抓拍推送)在内存中拆包,
//      直接落盘 *_meta.json + *_face.jpg + *_bg.jpg, 不产生 .bin 中间文件
//   3. 其他原始数据按时间戳保存, 扩展名按 Content-Type 和文件头推断
//   4. https 使用编译期内置证书(自签, 有效期100年), 界面和设备端都无需配置证书
//   5. 配置持久化在 exe 同目录 config.txt (界面保存, 也可手工编辑)
//   6. 双击即启动监听; 关闭窗口即停止
//
// 编译 (Windows 自带编译器, Git Bash 写法):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -optimize+
//     -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll
//     -out:PictureReceiver.exe PictureReceiver.cs
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("报警服务器模拟器")]
[assembly: System.Reflection.AssemblyDescription("摄像头抓拍数据接收器 (PictureReceiver)")]
[assembly: System.Reflection.AssemblyCompany("yuanbo6")]
[assembly: System.Reflection.AssemblyProduct("报警服务器模拟器")]
[assembly: System.Reflection.AssemblyCopyright("Copyright yuanbo6 2026")]
[assembly: System.Reflection.AssemblyVersion("2.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.2.0.0")]

namespace PictureReceiver
{
    #region 应用信息

    static class AppInfo
    {
        public const string Version = "v2.2";
        public const string Author = "yuanbo6";
        public const string BuildDate = "2026-09-10";
    }

    #endregion
    #region 配置 (key=value 纯文本, 界面保存 / 手工编辑均可)

    class Config
    {
        public string Protocol = "http";      // http | https
        public string Ip = "0.0.0.0";         // 监听 IP, 0.0.0.0 = 所有网卡
        public int Port = 5578;
        public string SaveDir = @"C:\Picture";

        public static Config Load(string path)
        {
            Config cfg = new Config();
            if (!File.Exists(path)) return cfg;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                // 容错: 手工编辑时顺手加的引号去掉
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);
                switch (key)
                {
                    case "protocol": cfg.Protocol = val.ToLowerInvariant(); break;
                    case "ip": cfg.Ip = val; break;
                    case "port": { int p; if (int.TryParse(val, out p)) cfg.Port = p; break; }
                    case "savedir": cfg.SaveDir = val; break;
                }
            }
            return cfg;
        }

        public static void Save(string path, Config cfg)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# PictureReceiver 配置 (由界面保存, 也可手工编辑)");
            sb.AppendLine("# protocol: http 或 https");
            sb.AppendLine("# ip:       监听 IP; 0.0.0.0 = 所有网卡");
            sb.AppendLine("# port:     监听端口");
            sb.AppendLine("# savedir:  抓拍数据保存目录");
            sb.AppendLine("protocol=" + cfg.Protocol);
            sb.AppendLine("ip=" + cfg.Ip);
            sb.AppendLine("port=" + cfg.Port);
            sb.AppendLine("savedir=" + cfg.SaveDir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
    }

    #endregion

    #region 内置 https 证书

    static class EmbeddedCert
    {
        // 自签证书 (CN=PictureReceiver, 有效期至 2126 年) 在编译期生成后内嵌于此。
        // 作用仅是满足 TLS 握手、加密链路; 抓拍设备不校验证书, 因此对用户不可见、不可配。
        const string PfxBase64 = "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";
        const string Password = "PicRecv2026";

        static X509Certificate2 cached;

        public static X509Certificate2 Load(Action<string> log)
        {
            if (cached != null) return cached;
            try
            {
                cached = new X509Certificate2(Convert.FromBase64String(PfxBase64), Password);
                return cached;
            }
            catch (Exception ex)
            {
                log("[错误] 加载内置 https 证书失败: " + ex.Message);
                return null;
            }
        }
    }

    #endregion

    #region Multipart 解析 (内存中拆包)

    class MultipartPart
    {
        public string Name;
        public string ContentType;
        public byte[] Body;
    }

    static class MultipartParser
    {
        // body 以 "--boundary\r\n" 开头; 返回拆出的部件, 失败返回空列表
        public static List<MultipartPart> Parse(byte[] body)
        {
            List<MultipartPart> parts = new List<MultipartPart>();
            int firstCrlf = IndexOf(body, 0, Crlf);
            if (firstCrlf <= 0) return parts;
            byte[] delim = Concat(Crlf, Slice(body, 0, firstCrlf)); // \r\n + boundary 行

            List<int> marks = new List<int>();
            int pos = firstCrlf;
            while (true)
            {
                int m = IndexOf(body, pos, delim);
                if (m < 0) break;
                marks.Add(m);
                pos = m + delim.Length;
            }

            int segStart = firstCrlf + 2;
            foreach (int mark in marks)
            {
                if (segStart + 1 < body.Length && body[segStart] == (byte)'-' && body[segStart + 1] == (byte)'-') break; // "--boundary--" 结束
                int headEnd = IndexOf(body, segStart, CrlfCrlf);
                if (headEnd >= 0 && headEnd < mark)
                {
                    string headers = Encoding.UTF8.GetString(body, segStart, headEnd - segStart);
                    int bodyStart = headEnd + 4;
                    int bodyEnd = mark;
                    while (bodyEnd > bodyStart && (body[bodyEnd - 1] == (byte)'\n' || body[bodyEnd - 1] == (byte)'\r')) bodyEnd--;

                    MultipartPart p = new MultipartPart();
                    p.Name = ExtractAttr(headers, "name");
                    p.ContentType = GetPlainHeader(headers, "Content-Type");
                    p.Body = Slice(body, bodyStart, bodyEnd);
                    parts.Add(p);
                }
                segStart = mark + delim.Length;
            }
            return parts;
        }

        static readonly byte[] Crlf = Encoding.ASCII.GetBytes("\r\n");
        static readonly byte[] CrlfCrlf = Encoding.ASCII.GetBytes("\r\n\r\n");

        static byte[] Slice(byte[] src, int from, int to)
        {
            byte[] dst = new byte[to - from];
            Array.Copy(src, from, dst, 0, dst.Length);
            return dst;
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] dst = new byte[a.Length + b.Length];
            Array.Copy(a, 0, dst, 0, a.Length);
            Array.Copy(b, 0, dst, a.Length, b.Length);
            return dst;
        }

        static string ExtractAttr(string headers, string attr)
        {
            string needle = attr + "=\"";
            foreach (string line in headers.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.TrimStart().StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase)) continue;
                int p = line.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (p < 0) continue;
                int a = p + needle.Length;
                int b = line.IndexOf('"', a);
                if (b > a) return line.Substring(a, b - a);
            }
            return "";
        }

        static string GetPlainHeader(string headers, string key)
        {
            foreach (string line in headers.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                if (line.Substring(0, c).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(c + 1).Trim();
            }
            return null;
        }

        static int IndexOf(byte[] data, int from, byte[] pattern)
        {
            int max = data.Length - pattern.Length;
            for (int i = from; i <= max; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }

    #endregion

    #region 接收服务 (与旧控制台版逻辑一致, 仅输出改为回调)

    class Receiver
    {
        const string TestPath = "/test";

        Config cfg;
        string saveDir;
        TcpListener listener;
        X509Certificate2 tlsCert;
        volatile bool running;
        long counter = 0;
        int received = 0;

        public Action<string> Log = delegate { };          // 任意线程回调
        public Action<int> ReceivedChanged = delegate { }; // 参数: 累计接收条数

        public bool Running { get { return running; } }
        public int ReceivedCount { get { return received; } }
        public string Endpoint
        {
            get { return cfg == null ? "" : cfg.Protocol + "://" + cfg.Ip + ":" + cfg.Port + "/test"; }
        }
        public string SavePath { get { return saveDir == null ? "" : saveDir; } }

        public bool Start(Config config, string saveDirPath)
        {
            Stop();
            cfg = config;
            saveDir = saveDirPath;

            try { Directory.CreateDirectory(saveDir); }
            catch (Exception ex)
            {
                Log("[错误] 无法创建保存目录 " + saveDir + ": " + ex.Message);
                return false;
            }

            tlsCert = null;
            if (cfg.Protocol == "https")
            {
                tlsCert = EmbeddedCert.Load(Log);
                if (tlsCert == null) return false;
            }

            listener = new TcpListener(ParseBindAddr(cfg.Ip), cfg.Port);
            try { listener.Start(); }
            catch
            {
                Log("[!] 本机当前没有 " + cfg.Ip + " 这个地址, 回退绑定 0.0.0.0 (所有网卡)");
                try
                {
                    listener = new TcpListener(IPAddress.Any, cfg.Port);
                    listener.Start();
                }
                catch (Exception ex)
                {
                    Log("[错误] 监听失败: " + ex.Message + "  (端口可能被占用, 或程序已在运行)");
                    listener = null;
                    return false;
                }
            }

            running = true;
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
            Log("监听中: " + Endpoint + "   保存目录: " + saveDir);
            return true;
        }

        public void Stop()
        {
            running = false;
            TcpListener l = listener;
            listener = null;
            if (l != null) { try { l.Stop(); } catch { } }
        }

        IPAddress ParseBindAddr(string ip)
        {
            IPAddress a;
            if (IPAddress.TryParse(ip, out a)) return a;
            return IPAddress.Any;
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; } // listener 已停止
                ThreadPool.QueueUserWorkItem(delegate(object o) { Handle((TcpClient)o); }, client);
            }
        }

        void Handle(TcpClient client)
        {
            string remote = "";
            try
            {
                remote = client.Client.RemoteEndPoint.ToString();
                using (client)
                using (Stream s = WrapStream(client))
                {
                    try { s.ReadTimeout = 30000; } catch { }

                    string headers = ReadHeaders(s);
                    if (headers == null) { Respond(s, 400, "Bad Request", "empty request"); return; }

                    string[] lines = headers.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    string[] parts = lines[0].Split(' ');
                    string method = parts.Length > 0 ? parts[0] : "";
                    string rawPath = parts.Length > 1 ? parts[1] : "/";
                    int q = rawPath.IndexOf('?');
                    string path = q >= 0 ? rawPath.Substring(0, q) : rawPath;

                    int contentLength = 0;
                    string contentType = "";
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int c = lines[i].IndexOf(':');
                        if (c <= 0) continue;
                        string key = lines[i].Substring(0, c).Trim().ToLowerInvariant();
                        string val = lines[i].Substring(c + 1).Trim();
                        if (key == "content-length") int.TryParse(val, out contentLength);
                        else if (key == "content-type") contentType = val;
                    }

                    // URL 固定 /test
                    if (!path.Equals(TestPath, StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith(TestPath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("404  " + method + " " + rawPath + "  <- " + remote);
                        Respond(s, 404, "Not Found", "use /test");
                        return;
                    }

                    byte[] body;
                    if (contentLength > 0)
                    {
                        body = new byte[contentLength];
                        ReadExact(s, body, contentLength);
                    }
                    else if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        body = ReadUntilClose(s);
                    }
                    else body = new byte[0];

                    if (body.Length == 0)
                    {
                        Log(method + " " + rawPath + " <- " + remote + "  (空请求体, 未保存)");
                        Respond(s, 200, "OK", "OK");
                        return;
                    }

                    int saved = SaveData(body, contentType);
                    int total = Interlocked.Increment(ref received);
                    ReceivedChanged(total);
                    Log(method + " " + rawPath + " <- " + remote + "  共保存 " + saved + " 个文件 (" + body.Length + " bytes, " + contentType + ")");

                    Respond(s, 200, "OK", "OK");
                }
            }
            catch (Exception ex)
            {
                Log("错误 (" + remote + "): " + ex.Message);
            }
        }

        // 判定格式并落盘; 返回保存文件数
        int SaveData(byte[] body, string contentType)
        {
            string baseName = string.Format("cap_{0:yyyyMMdd_HHmmss_fff}_{1}", DateTime.Now, Interlocked.Increment(ref counter));

            // multipart/form-data: 以 "--" 开头 -> 内存拆包, 直接落盘图片和 json, 不产生 bin
            if (body.Length > 4 && body[0] == (byte)'-' && body[1] == (byte)'-')
            {
                List<MultipartPart> parts = MultipartParser.Parse(body);
                if (parts.Count == 0)
                {
                    // 拆包失败: 保留原始 bin 以防数据丢失
                    File.WriteAllBytes(Path.Combine(saveDir, baseName + "_unparsed.bin"), body);
                    Log("[!] multipart 拆包失败, 已保留原始文件 " + baseName + "_unparsed.bin");
                    return 1;
                }
                int n = 0;
                foreach (MultipartPart p in parts)
                {
                    string prefix, ext;
                    if (p.Name == "faceCapture") { prefix = "meta"; ext = ".json"; }
                    else if (p.Name == "faceImage") { prefix = "face"; ext = GuessImgExt(p.Body); }
                    else if (p.Name == "backgroundImage") { prefix = "bg"; ext = GuessImgExt(p.Body); }
                    else { prefix = string.IsNullOrEmpty(p.Name) ? "part" : p.Name; ext = GuessImgExt(p.Body); }

                    string outPath = Path.Combine(saveDir, baseName + "_" + prefix + ext);
                    File.WriteAllBytes(outPath, p.Body);
                    Log("   拆出: " + Path.GetFileName(outPath) + "  (" + p.Body.Length + " bytes)");
                    n++;
                }
                return n;
            }

            // 非 multipart: 原始数据按时间戳 + 扩展名保存
            string ext2 = GuessRawExt(body, contentType);
            File.WriteAllBytes(Path.Combine(saveDir, baseName + ext2), body);
            return 1;
        }

        // http -> 原始 NetworkStream; https -> SslStream (TLS 握手, 使用内置证书)
        Stream WrapStream(TcpClient client)
        {
            Stream ns = client.GetStream();
            if (cfg.Protocol != "https" || tlsCert == null) return ns;
            SslStream ssl = new SslStream(ns, false);
            ssl.AuthenticateAsServer(tlsCert, false, SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12, false);
            return ssl;
        }

        static string GuessImgExt(byte[] body)
        {
            if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF) return ".jpg";
            if (body.Length >= 4 && body[0] == 0x89 && body[1] == 0x50 && body[2] == 0x4E && body[3] == 0x47) return ".png";
            if (body.Length >= 2 && body[0] == 0x42 && body[1] == 0x4D) return ".bmp";
            return ".bin";
        }

        static string GuessRawExt(byte[] body, string contentType)
        {
            string byMagic = GuessImgExt(body);
            if (byMagic != ".bin") return byMagic;
            contentType = (contentType ?? "").ToLowerInvariant();
            if (contentType.Contains("json")) return ".json";
            if (contentType.Contains("text/")) return ".txt";
            if (contentType.Contains("jpeg") || contentType.Contains("jpg")) return ".jpg";
            if (contentType.Contains("png")) return ".png";
            return ".bin";
        }

        static string ReadHeaders(Stream s)
        {
            StringBuilder sb = new StringBuilder();
            int total = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                sb.Append((char)b);
                total++;
                if (total > 64 * 1024) throw new IOException("header too large");
                string str = sb.ToString();
                if (str.EndsWith("\r\n\r\n")) return str.Substring(0, str.Length - 4);
            }
        }

        static void ReadExact(Stream s, byte[] buf, int len)
        {
            int off = 0;
            while (off < len)
            {
                int n = s.Read(buf, off, len - off);
                if (n <= 0) throw new IOException("connection closed before body complete");
                off += n;
            }
        }

        static byte[] ReadUntilClose(Stream s)
        {
            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            return ms.ToArray();
        }

        static void Respond(Stream s, int code, string status, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            string head = string.Format(
                "HTTP/1.1 {0} {1}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {2}\r\nConnection: close\r\n\r\n",
                code, status, b.Length);
            byte[] hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(b, 0, b.Length);
        }
    }

    #endregion

    #region 主界面

    class MainForm : Form
    {
        Receiver receiver = new Receiver();
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string configPath;

        ComboBox ddlProtocol;
        ComboBox ddlIp;
        TextBox txtPort;
        TextBox txtDir;
        Button btnBrowse;
        Button btnOpen;
        Button btnApply;
        Label lblStatus;
        Label lblCount;
        Label lblHttpsHint;
        GroupBox grpConfig;
        GroupBox grpLog;
        TextBox txtLog;

        public MainForm()
        {
            configPath = Path.Combine(exeDir, "config.txt");

            BuildUi();

            Config loaded = Config.Load(configPath);
            if (!File.Exists(configPath)) Config.Save(configPath, loaded); // 首启自动生成
            LoadConfigIntoUi(loaded);

            receiver.Log = AppendLog;
            receiver.ReceivedChanged = delegate(int n)
            {
                SafeInvoke(delegate
                {
                    lblCount.Text = "已接收 " + n + " 条  ·  保存于 " + receiver.SavePath;
                });
            };

            AppendLog(AppInfo.Version + "  作者: " + AppInfo.Author + "  开发时间: " + AppInfo.BuildDate);
            StartListening(false); // 双击即启动
        }

        void BuildUi()
        {
            this.Text = "报警服务器模拟器 " + AppInfo.Version;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.ClientSize = new Size(660, 500);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            lblStatus = new Label();
            lblStatus.Location = new Point(16, 14);
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            lblStatus.ForeColor = Color.Gray;
            lblStatus.Text = "○ 已停止";

            lblCount = new Label();
            lblCount.Location = new Point(18, 44);
            lblCount.AutoSize = true;
            lblCount.ForeColor = Color.FromArgb(90, 90, 90);
            lblCount.Text = "已接收 0 条";

            grpConfig = new GroupBox();
            grpConfig.Text = "配置";
            grpConfig.Location = new Point(16, 70);
            grpConfig.Size = new Size(628, 172);

            Label lblProto = new Label();
            lblProto.Text = "协议";
            lblProto.Location = new Point(16, 30);
            lblProto.AutoSize = true;

            ddlProtocol = new ComboBox();
            ddlProtocol.DropDownStyle = ComboBoxStyle.DropDownList;
            ddlProtocol.Items.Add("http");
            ddlProtocol.Items.Add("https");
            ddlProtocol.Location = new Point(95, 26);
            ddlProtocol.Size = new Size(120, 23);
            ddlProtocol.SelectedIndexChanged += delegate { UpdateHttpsHint(); };

            Label lblIp = new Label();
            lblIp.Text = "监听 IP";
            lblIp.Location = new Point(16, 64);
            lblIp.AutoSize = true;

            ddlIp = new ComboBox();
            ddlIp.DropDownStyle = ComboBoxStyle.DropDownList;
            ddlIp.Location = new Point(95, 60);
            ddlIp.Size = new Size(260, 23);

            Label lblPort = new Label();
            lblPort.Text = "端口";
            lblPort.Location = new Point(16, 98);
            lblPort.AutoSize = true;

            txtPort = new TextBox();
            txtPort.Location = new Point(95, 94);
            txtPort.Size = new Size(90, 23);

            Label lblDir = new Label();
            lblDir.Text = "保存目录";
            lblDir.Location = new Point(16, 132);
            lblDir.AutoSize = true;

            txtDir = new TextBox();
            txtDir.Location = new Point(95, 128);
            txtDir.Size = new Size(333, 23);

            btnBrowse = new Button();
            btnBrowse.Text = "浏览…";
            btnBrowse.Location = new Point(436, 128);
            btnBrowse.Size = new Size(80, 23);
            btnBrowse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.ShowNewFolderButton = true;
                    try { dlg.SelectedPath = txtDir.Text.Trim(); } catch { }
                    if (dlg.ShowDialog(this) == DialogResult.OK) txtDir.Text = dlg.SelectedPath;
                }
            };

            btnOpen = new Button();
            btnOpen.Text = "打开目录";
            btnOpen.Location = new Point(524, 128);
            btnOpen.Size = new Size(92, 23);
            btnOpen.Click += delegate
            {
                string d = txtDir.Text.Trim();
                if (d.Length == 0) return;
                try { System.Diagnostics.Process.Start(d); }
                catch (Exception ex) { MessageBox.Show(this, "无法打开目录: " + ex.Message, "提示"); }
            };

            // 单行文本框高度由字体决定(高 DPI 缩放后与显式设定的按钮高度会有像素差),
            // 窗口显示后按文本框实际高度对齐两个按钮, 保证任何缩放下同行等高
            this.Shown += delegate
            {
                btnBrowse.Height = txtDir.Height;
                btnOpen.Height = txtDir.Height;
                btnBrowse.Top = txtDir.Top;
                btnOpen.Top = txtDir.Top;
            };

            lblHttpsHint = new Label();
            lblHttpsHint.Location = new Point(18, 248);
            lblHttpsHint.AutoSize = true;
            lblHttpsHint.ForeColor = Color.FromArgb(170, 85, 0);
            lblHttpsHint.Visible = false;

            btnApply = new Button();
            btnApply.Text = "应用并重启监听";
            btnApply.Location = new Point(16, 274);
            btnApply.Size = new Size(170, 34);
            btnApply.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            btnApply.Click += delegate
            {
                int p;
                if (!int.TryParse(txtPort.Text.Trim(), out p) || p < 1 || p > 65535)
                {
                    MessageBox.Show(this, "端口必须是 1-65535 之间的数字", "提示");
                    return;
                }
                StartListening(true);
            };

            grpLog = new GroupBox();
            grpLog.Text = "运行日志";
            grpLog.Location = new Point(16, 320);
            grpLog.Size = new Size(628, 164);
            grpLog.Padding = new Padding(10);

            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Dock = DockStyle.Fill;
            txtLog.BackColor = Color.White;
            txtLog.Font = new Font("Consolas", 9F);

            grpLog.Controls.Add(txtLog);
            grpConfig.Controls.Add(lblProto);
            grpConfig.Controls.Add(ddlProtocol);
            grpConfig.Controls.Add(lblIp);
            grpConfig.Controls.Add(ddlIp);
            grpConfig.Controls.Add(lblPort);
            grpConfig.Controls.Add(txtPort);
            grpConfig.Controls.Add(lblDir);
            grpConfig.Controls.Add(txtDir);
            grpConfig.Controls.Add(btnBrowse);
            grpConfig.Controls.Add(btnOpen);

            this.Controls.Add(lblStatus);
            this.Controls.Add(lblCount);
            this.Controls.Add(grpConfig);
            this.Controls.Add(lblHttpsHint);
            this.Controls.Add(btnApply);
            this.Controls.Add(grpLog);

            this.FormClosing += delegate { receiver.Stop(); };
        }

        void LoadConfigIntoUi(Config c)
        {
            ddlProtocol.SelectedItem = (c.Protocol == "https") ? "https" : "http";

            ddlIp.Items.Clear();
            ddlIp.Items.Add("0.0.0.0");
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(Dns.GetHostName());
                foreach (IPAddress a in addrs)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string s = a.ToString();
                        if (!ddlIp.Items.Contains(s)) ddlIp.Items.Add(s);
                    }
                }
            }
            catch { }
            if (!ddlIp.Items.Contains(c.Ip)) ddlIp.Items.Add(c.Ip); // 配置里的 IP 本机当前没有时也保留
            ddlIp.SelectedItem = c.Ip;

            txtPort.Text = c.Port.ToString();
            txtDir.Text = c.SaveDir;
            UpdateHttpsHint();
        }

        void UpdateHttpsHint()
        {
            bool https = ddlProtocol.Text == "https";
            lblHttpsHint.Visible = https;
            if (https)
                lblHttpsHint.Text = "HTTPS 使用程序内置证书: 设备端直接填 https://本机IP:端口/test, 无需配置证书";
        }

        Config ReadFromUi()
        {
            Config c = new Config();
            c.Protocol = ddlProtocol.Text == "https" ? "https" : "http";
            c.Ip = string.IsNullOrEmpty(ddlIp.Text) ? "0.0.0.0" : ddlIp.Text.Trim();
            int p;
            c.Port = int.TryParse(txtPort.Text.Trim(), out p) ? p : 5578;
            c.SaveDir = string.IsNullOrWhiteSpace(txtDir.Text) ? @"C:\Picture" : txtDir.Text.Trim();
            return c;
        }

        void StartListening(bool saveConfig)
        {
            Config c = ReadFromUi();

            string dir;
            try { dir = Path.GetFullPath(Path.IsPathRooted(c.SaveDir) ? c.SaveDir : Path.Combine(exeDir, c.SaveDir)); }
            catch { dir = c.SaveDir; }

            receiver.Stop();
            bool ok = receiver.Start(c, dir);
            UpdateStatus();
            if (ok && saveConfig) Config.Save(configPath, c);
        }

        void UpdateStatus()
        {
            SafeInvoke(delegate
            {
                if (receiver.Running)
                {
                    lblStatus.Text = "● 监听中  " + receiver.Endpoint;
                    lblStatus.ForeColor = Color.FromArgb(0, 128, 0);
                }
                else
                {
                    lblStatus.Text = "○ 已停止";
                    lblStatus.ForeColor = Color.Gray;
                }
                lblCount.Text = "已接收 " + receiver.ReceivedCount + " 条  ·  保存于 " + receiver.SavePath;
            });
        }

        void AppendLog(string line)
        {
            SafeInvoke(delegate
            {
                txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
                if (txtLog.TextLength > 120000)
                {
                    string t = txtLog.Text;
                    txtLog.Text = t.Substring(t.Length - 60000);
                }
            });
        }

        void SafeInvoke(Action action)
        {
            if (!IsHandleCreated) { action(); return; }
            if (InvokeRequired)
                BeginInvoke((MethodInvoker)delegate { try { action(); } catch (ObjectDisposedException) { } });
            else
                action();
        }
    }

    #endregion

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
