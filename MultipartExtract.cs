// MultipartExtract.exe - 把 recv_*.bin (multipart/form-data) 拆解为 meta.json + face.jpg + bg.jpg
// 用法: MultipartExtract.exe [目录]  (默认当前目录)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

class MultipartExtract
{
    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        string[] files = Directory.GetFiles(dir, "recv_*.bin");
        if (files.Length == 0) { Console.WriteLine("未找到 recv_*.bin"); return 1; }

        foreach (string file in files)
        {
            try { ExtractFile(file); }
            catch (Exception ex) { Console.WriteLine("[错误] {0}: {1}", file, ex.Message); }
        }
        return 0;
    }

    static void ExtractFile(string file)
    {
        byte[] data = File.ReadAllBytes(file);
        string baseName = Path.GetFileNameWithoutExtension(file);
        string outDir = Path.GetDirectoryName(file);

        // boundary = 第一行内容（"--boundary"）
        int firstCrlf = IndexOf(data, 0, Encoding.ASCII.GetBytes("\r\n"));
        string boundary = Encoding.ASCII.GetString(data, 0, firstCrlf);
        byte[] delim = Encoding.ASCII.GetBytes("\r\n" + boundary);

        // 所有 "\r\n--boundary" 的位置 = 部件段落的终点
        List<int> marks = new List<int>();
        int pos = firstCrlf;
        while (true)
        {
            int m = IndexOf(data, pos, delim);
            if (m < 0) break;
            marks.Add(m);
            pos = m + delim.Length;
        }

        // 段列表: 第一段从首行 boundary 之后开始，到第一个 mark；依此类推
        List<int[]> segs = new List<int[]>();
        int segStart = firstCrlf + 2;
        for (int i = 0; i < marks.Count; i++)
        {
            segs.Add(new int[] { segStart, marks[i] });
            segStart = marks[i] + delim.Length;
        }
        // marks 之后剩余的是 "--boundary--" 结束标记段，无头无体，跳过

        int saved = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            int start = segs[i][0], end = segs[i][1];
            if (start + 1 < data.Length && data[start] == (byte)'-' && data[start + 1] == (byte)'-') continue; // 结束标记

            int headEnd = IndexOf(data, start, Encoding.ASCII.GetBytes("\r\n\r\n"));
            if (headEnd < 0 || headEnd >= end) continue;
            string headers = Encoding.UTF8.GetString(data, start, headEnd - start);
            int bodyStart = headEnd + 4;
            int bodyEnd = end;
            // body 末尾的 \r\n 属于分隔符，去掉
            while (bodyEnd > bodyStart && (data[bodyEnd - 1] == (byte)'\n' || data[bodyEnd - 1] == (byte)'\r')) bodyEnd--;

            string name = ExtractAttr(headers, "name");
            string contentType = ExtractAttr(headers, "content-type") ?? GetPlainHeader(headers, "Content-Type");
            byte[] body = new byte[bodyEnd - bodyStart];
            Array.Copy(data, bodyStart, body, 0, body.Length);

            string ext, prefix;
            if (name == "faceCapture") { ext = ".json"; prefix = "meta"; }
            else if (name == "faceImage") { ext = GuessImgExt(body); prefix = "face"; }
            else if (name == "backgroundImage") { ext = GuessImgExt(body); prefix = "bg"; }
            else { ext = GuessImgExt(body); prefix = Safe(name, i); }

            string outPath = Path.Combine(outDir, baseName + "_" + prefix + ext);
            File.WriteAllBytes(outPath, body);
            Console.WriteLine("拆出: {0}  ({1}, {2} bytes){3}",
                Path.GetFileName(outPath), contentType ?? "?", body.Length, IntegrityNote(body));
            saved++;
        }
        Console.WriteLine("[完成] {0} -> {1} 个部件\n", Path.GetFileName(file), saved);
    }

    // 从 Content-Disposition 行里取 name="xxx" / filename="yyy" 的值
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

    static string GuessImgExt(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF) return ".jpg";
        if (body.Length >= 4 && body[0] == 0x89 && body[1] == 0x50 && body[2] == 0x4E && body[3] == 0x47) return ".png";
        if (body.Length >= 2 && body[0] == 0x42 && body[1] == 0x4D) return ".bmp";
        return ".bin";
    }

    // 校验 JPEG 完整性: SOI(FFD8FF) 开头 + EOI(FFD9) 结尾
    static string IntegrityNote(byte[] body)
    {
        if (body.Length >= 4 && body[0] == 0xFF && body[1] == 0xD8)
        {
            bool eoi = body[body.Length - 2] == 0xFF && body[body.Length - 1] == 0xD9;
            return eoi ? " [JPEG完整]" : " [JPEG缺少EOI]";
        }
        return "";
    }

    static string Safe(string s, int i)
    {
        if (string.IsNullOrEmpty(s)) return "part" + i;
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
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
