using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TagLib;
using TaskbarLyrics.Core.Abstractions;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.Core.Services;

/// <summary>
/// 本地歌词提供者。直接实现 ILyricProvider（不继承 LyricProviderBase），
/// 避免被其他在线提供者的静态缓存污染导致本地歌词被永久跳过。
/// 按优先级：外部 .lrc 文件 > 音频内嵌歌词。
/// </summary>
public sealed class LocalLyricProvider : ILyricProvider
{
    private static readonly Regex LrcTimestampRegex = new(
        @"\[(\d+)[:：](\d+)(?:[\.\uFF0E:：](\d{1,3}))?\]",
        RegexOptions.Compiled);

    private static readonly Regex OffsetRegex = new(
        @"\[offset\s*[:：]\s*(?<val>[+-]?\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] LrcExtensions = { ".lrc", ".LRC" };

    // 支持内嵌歌词的音频文件扩展名
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".wav", ".m4a", ".ogg",
        ".wma", ".ape", ".opus", ".aac", ".wv",
        ".dsf", ".dff", ".mp4", ".aiff", ".aif"
    };

    private static readonly HashSet<string> AudioExtSet = new(
        AudioExtensions, StringComparer.OrdinalIgnoreCase);

    private readonly Func<IReadOnlyList<string>> _foldersProvider;

    public string SourceApp => "Local";

    public LocalLyricProvider(Func<IReadOnlyList<string>> foldersProvider)
    {
        _foldersProvider = foldersProvider;
    }

    /// <summary>
    /// 不继承 LyricProviderBase，不使用其静态缓存。
    /// </summary>
    public async Task<LyricDocument?> GetLyricsAsync(TrackInfo track, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log.Info($"LocalLyricProvider 检索: '{track.Title}' - '{track.Artist}'");

        var folders = _foldersProvider();
        if (folders == null || folders.Count == 0)
        {
            Log.Debug("LocalLyricProvider 未配置音乐目录");
            return null;
        }

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Log.Debug($"LocalLyricProvider 目录无效: {folder}");
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 策略1: 精确文件名匹配外部 .lrc（快）
            Log.Debug($"LocalLyricProvider 搜索LRC: {folder}");
            var lrcPath = FindLrcFast(folder, track);
            if (lrcPath != null)
            {
                var doc = await ParseLrcFileAsync(lrcPath, cancellationToken);
                if (doc != null)
                {
                    Log.Info($"LocalLyricProvider LRC命中: {lrcPath} ({doc.Lines.Count}行) {sw.ElapsedMilliseconds}ms");
                    return doc;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 策略2: 精确文件名匹配音频文件内嵌歌词
            Log.Debug($"LocalLyricProvider 搜索音频: {folder}");
            var audioPath = FindAudioFast(folder, track);
            if (audioPath != null)
            {
                var doc = await ExtractEmbeddedAsync(audioPath, cancellationToken);
                if (doc != null)
                {
                    Log.Info($"LocalLyricProvider 内嵌命中: {audioPath} ({doc.Lines.Count}行) {sw.ElapsedMilliseconds}ms");
                    return doc;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 策略3: 递归搜索目录树
            Log.Debug($"LocalLyricProvider 递归搜索: {folder}");
            var recursivePath = await FindFileRecursiveAsync(folder, track, cancellationToken);
            if (recursivePath != null)
            {
                if (IsLrcFile(recursivePath))
                {
                    var doc = await ParseLrcFileAsync(recursivePath, cancellationToken);
                    if (doc != null)
                    {
                        Log.Info($"LocalLyricProvider 递归LRC命中: {recursivePath} ({doc.Lines.Count}行) {sw.ElapsedMilliseconds}ms");
                        return doc;
                    }
                }
                else
                {
                    var doc = await ExtractEmbeddedAsync(recursivePath, cancellationToken);
                    if (doc != null)
                    {
                        Log.Info($"LocalLyricProvider 递归内嵌命中: {recursivePath} ({doc.Lines.Count}行) {sw.ElapsedMilliseconds}ms");
                        return doc;
                    }
                }
            }
        }

        Log.Info($"LocalLyricProvider 未找到歌词 ({sw.ElapsedMilliseconds}ms)");
        return null;
    }

    // ========================================================
    // 文件名候选
    // ========================================================

    private static List<string> BuildCandidates(TrackInfo track)
    {
        var result = new List<string>();
        var title = Sanitize(track.Title);
        var artist = Sanitize(track.Artist);

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            result.Add($"{artist} - {title}");
            result.Add($"{title} - {artist}");
            result.Add($"{artist}-{title}");
            result.Add($"{title}-{artist}");
        }
        if (!string.IsNullOrWhiteSpace(title))
            result.Add(title);
        if (!string.IsNullOrWhiteSpace(artist))
            result.Add(artist);
        return result;
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }

    private static bool IsLrcFile(string path)
    {
        var ext = Path.GetExtension(path);
        return LrcExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    // ========================================================
    // 快速匹配（仅根目录）
    // ========================================================

    private static string? FindLrcFast(string folder, TrackInfo track)
    {
        foreach (var candidate in BuildCandidates(track))
        {
            foreach (var ext in LrcExtensions)
            {
                var path = Path.Combine(folder, candidate + ext);
                if (System.IO.File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static string? FindAudioFast(string folder, TrackInfo track)
    {
        foreach (var candidate in BuildCandidates(track))
        {
            foreach (var ext in AudioExtensions)
            {
                var path = Path.Combine(folder, candidate + ext);
                if (System.IO.File.Exists(path)) return path;
            }
        }
        return null;
    }

    // ========================================================
    // 递归搜索（单次扫描，let Task.Run 处理阻塞）
    // ========================================================

    private static async Task<string?> FindFileRecursiveAsync(
        string folder, TrackInfo track, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                IEnumerable<string> allFiles;
                try
                {
                    allFiles = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
                }
                catch { return null; }

                var count = 0;
                foreach (var file in allFiles)
                {
                    if (++count % 1000 == 0 && ct.IsCancellationRequested)
                        return null;

                    var ext = Path.GetExtension(file);
                    if (!IsSupportedExtension(ext))
                        continue;

                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (FileNameMatches(fileName, track))
                        return file;
                }
            }
            catch { }
            return null;
        }, ct);
    }

    private static bool IsSupportedExtension(string ext)
    {
        return LrcExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
               AudioExtSet.Contains(ext);
    }

    // ========================================================
    // 文件名匹配
    // ========================================================

    private static bool FileNameMatches(string fileName, TrackInfo track)
    {
        var title = track.Title?.Trim() ?? string.Empty;
        var artist = track.Artist?.Trim() ?? string.Empty;
        if (title.Length == 0 && artist.Length == 0) return false;

        var fnLower = fileName.ToLowerInvariant();

        if (title.Length > 0 && artist.Length > 0 &&
            fnLower.Contains(title.ToLowerInvariant()) &&
            fnLower.Contains(artist.ToLowerInvariant()))
            return true;

        if (title.Length >= 3 && fnLower.Contains(title.ToLowerInvariant()))
            return true;

        if (artist.Length >= 3 && fnLower.Contains(artist.ToLowerInvariant()))
            return true;

        return false;
    }

    // ========================================================
    // LRC 文件解析
    // ========================================================

    private async Task<LyricDocument?> ParseLrcFileAsync(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var content = System.IO.File.ReadAllText(path);
                var lines = ParseLrc(content);
                return lines.Count > 0
                    ? new LyricDocument(lines, bestScore: 100)
                    : null;
            }
            catch (Exception ex)
            {
                Log.Debug($"LocalLyricProvider LRC解析失败: {path} - {ex.Message}");
                return null;
            }
        }, ct);
    }

    // ========================================================
    // 内嵌歌词提取（三层尝试）
    // ========================================================

    private async Task<LyricDocument?> ExtractEmbeddedAsync(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            // 层1: TagLib# 标准接口
            var doc = TryTagLibStandard(path);
            if (doc != null) return doc;

            // 层2: TagLib# Frame 枚举
            doc = TryTagLibFrames(path);
            if (doc != null) return doc;

            // 层3: 原始字节 LRC 扫描（兜底）
            return TryRawLrcScan(path);
        }, ct);
    }

    /// <summary>
    /// 层1: TagLib# 标准 Tag.Lyrics + Xiph + APE
    /// </summary>
    private static LyricDocument? TryTagLibStandard(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);

            // USLT (ID3v2 MP3) 或 LYRICS (Vorbis/Flac)
            var lyrics = f.Tag.Lyrics;
            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                Log.Debug($"LocalLyricProvider TagLib.Lyrics 读到 {lyrics.Length} 字符 ({path})");
                return BuildDocument(lyrics, 95);
            }

            // SYLT (ID3v2 Synchronized lyrics) — 网易云等常用
            if (f.TagTypes.HasFlag(TagTypes.Id3v2))
            {
                var id3Tag = f.GetTag(TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                if (id3Tag != null)
                {
                    var syltFrames = id3Tag.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>();
                    if (syltFrames != null && syltFrames.Any())
                    {
                        var lrcText = new StringBuilder();
                        foreach (var sylt in syltFrames)
                        {
                            if (sylt.Text != null)
                            {
                                foreach (var item in sylt.Text)
                                {
                                    if (!string.IsNullOrWhiteSpace(item.Text))
                                    {
                                        var ts = TimeSpan.FromMilliseconds(item.Time);
                                        // LRC 格式 [mm:ss.xx] — xx 为百分秒
                                        var timestamp = $"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}]";
                                        lrcText.AppendLine($"{timestamp}{item.Text}");
                                    }
                                }
                            }
                        }
                        var syltResult = lrcText.ToString();
                        if (!string.IsNullOrWhiteSpace(syltResult))
                        {
                            Log.Debug($"LocalLyricProvider SYLT 读到 {syltResult.Length} 字符 ({path})");
                            return BuildDocument(syltResult, 95);
                        }
                    }
                }
            }

            // VorbisComment (FLAC/OGG)
            if (f.Tag is TagLib.Ogg.XiphComment xiph)
            {
                var field = xiph.GetFirstField("LYRICS");
                if (!string.IsNullOrWhiteSpace(field))
                {
                    Log.Debug($"LocalLyricProvider Xiph LYRICS 读到 {field.Length} 字符");
                    return BuildDocument(field, 95);
                }
            }

            // APE tag
            if (f.Tag is TagLib.Ape.Tag ape)
            {
                var item = ape.GetItem("Lyrics");
                if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
                {
                    var txt = item.ToString();
                    Log.Debug($"LocalLyricProvider APE Lyrics 读到 {txt.Length} 字符");
                    return BuildDocument(txt, 95);
                }
            }
        }
        catch (TagLib.UnsupportedFormatException) { }
        catch (Exception ex) { Log.Debug($"LocalLyricProvider TagLib标准异常: {ex.Message}"); }

        return null;
    }

    /// <summary>
    /// 层2: TagLib# Frame 级别枚举（处理 Tags 数组中的文本帧）
    /// </summary>
    private static LyricDocument? TryTagLibFrames(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);

            // 只尝试 ID3v2 tag（最常见的内嵌歌词格式）
            if (f.TagTypes.HasFlag(TagTypes.Id3v2))
            {
                var tag = f.GetTag(TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                if (tag != null)
                {
                    var doc = TryId3v2Tag(tag, path);
                    if (doc != null) return doc;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 尝试 ID3v2 Tag 中所有 Frame 的文本输出
    /// </summary>
    private static LyricDocument? TryId3v2Tag(TagLib.Id3v2.Tag tag, string? path = null)
    {
        try
        {
            // 收集所有 Frame 的 Render 输出
            var allText = new StringBuilder();
            foreach (var frame in tag.GetFrames())
            {
                try
                {
                    // 只处理有渲染输出的 frame（避免用反射访问类型特定属性）
                    var frameText = frame.ToString();
                    if (!string.IsNullOrWhiteSpace(frameText) && frameText.Length > 20)
                    {
                        allText.AppendLine(frameText);
                    }
                }
                catch { }
            }

            var combined = allText.ToString();
            if (!string.IsNullOrWhiteSpace(combined))
            {
                Log.Debug($"LocalLyricProvider ID3v2 Frame 扫描命中 ({path ?? "?"}): {combined.Length} 字符");
                return BuildDocument(combined, 90);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 层3: 原始字节 LRC 模式扫描
    /// </summary>
    private static LyricDocument? TryRawLrcScan(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100) return null;

            var bufSize = (int)Math.Min(info.Length, 512 * 1024);
            var buf = new byte[bufSize];

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // 头部 64KB
            var headLen = Math.Min(bufSize, 64 * 1024);
            var read = fs.Read(buf, 0, headLen);
            var txt = TryDecode(buf, 0, read);
            if (txt != null && HasLrcTimestamps(txt))
            {
                Log.Debug("LocalLyricProvider 头部原始扫描命中");
                return BuildDocument(txt, 90);
            }

            // 尾部 256KB
            if (info.Length > 64 * 1024)
            {
                var tailStart = Math.Max(0, info.Length - 256 * 1024);
                fs.Seek(tailStart, SeekOrigin.Begin);
                read = fs.Read(buf, 0, Math.Min(bufSize, 256 * 1024));
                txt = TryDecode(buf, 0, read);
                if (txt != null && HasLrcTimestamps(txt))
                {
                    Log.Debug("LocalLyricProvider 尾部原始扫描命中");
                    return BuildDocument(txt, 90);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"LocalLyricProvider 原始扫描异常: {ex.Message}");
        }
        return null;
    }

    // ========================================================
    // 文档构造
    // ========================================================

    private static LyricDocument? BuildDocument(string text, int bestScore)
    {
        if (HasLrcTimestamps(text))
        {
            var lines = ParseLrc(text);
            return lines.Count > 0 ? new LyricDocument(lines, bestScore) : null;
        }

        // 纯文本：每行 4 秒间距
        var textLines = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !IsMetadataLine(l))
            .ToList();

        if (textLines.Count == 0) return null;

        var lyricLines = textLines.Select((t, i) =>
            new LyricLine(TimeSpan.FromSeconds(i * 4), t)).ToList();

        return new LyricDocument(lyricLines, bestScore: 80);
    }

    private static bool HasLrcTimestamps(string text) => LrcTimestampRegex.IsMatch(text);

    private static bool IsMetadataLine(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return true;
        if (s == "//") return true;

        // 典型元信息行特征
        if (s.StartsWith("作词") || s.StartsWith("作曲") ||
            s.StartsWith("编曲") || s.StartsWith("歌手") ||
            s.StartsWith("演唱") || s.StartsWith("原唱") ||
            s.StartsWith("制作") || s.StartsWith("出品") ||
            s.StartsWith("监制") || s.StartsWith("混音") ||
            s.StartsWith("母带") || s.StartsWith("录制"))
            return true;

        if (s is "词" or "曲") return true;

        var lower = s.ToLowerInvariant();
        return lower.Contains("lyricist") ||
               lower.Contains("composer") ||
               lower.Contains("arranger") ||
               lower.Contains("producer");
    }

    // ========================================================
    // LRC 解析
    // ========================================================

    private static List<LyricLine> ParseLrc(string lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return new List<LyricLine>();

        var offsetMs = 0;
        var om = OffsetRegex.Match(lrc);
        if (om.Success && int.TryParse(om.Groups["val"].Value, out var off))
            offsetMs = off;

        var result = new List<LyricLine>();
        var lines = lrc.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var matches = LrcTimestampRegex.Matches(trimmed);
            if (matches.Count == 0) continue;

            var textStart = matches[^1].Index + matches[^1].Length;
            var raw = textStart < trimmed.Length ? trimmed[textStart..] : string.Empty;
            var cleaned = Regex.Replace(raw, @"<[^>]+>", "").Trim();

            if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "//" || IsMetadataLine(cleaned))
                continue;

            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var ms = m.Groups.Count > 3 && m.Groups[3].Length > 0
                    ? int.Parse(m.Groups[3].Value.PadRight(3, '0')[..3])
                    : 0;

                var ts = new TimeSpan(0, 0, min, sec, ms)
                    .Add(TimeSpan.FromMilliseconds(offsetMs));
                if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;

                result.Add(new LyricLine(ts, cleaned));
            }
        }

        return result.OrderBy(x => x.Timestamp).ToList();
    }

    // ========================================================
    // 原始字节解码（多编码自动检测）
    // ========================================================

    private static string? TryDecode(byte[] buf, int offset, int len)
    {
        if (len < 50) return null;

        Encoding[] encs;
        try
        {
            encs = new[]
            {
                Encoding.UTF8,
                Encoding.Unicode,
                Encoding.BigEndianUnicode,
                CodePagesEncodingProvider.Instance.GetEncoding(936)!,  // GBK
                CodePagesEncodingProvider.Instance.GetEncoding(950)!,  // Big5
                Encoding.Default
            };
        }
        catch
        {
            encs = new[] { Encoding.UTF8, Encoding.Unicode, Encoding.Default };
        }

        foreach (var enc in encs)
        {
            try
            {
                var txt = enc.GetString(buf, offset, len);
                if (IsReadable(txt)) return txt;
            }
            catch { }
        }
        return null;
    }

    private static bool IsReadable(string s)
    {
        var n = Math.Min(s.Length, 200);
        var good = 0;
        for (var i = 0; i < n; i++)
        {
            var c = s[i];
            if (!char.IsControl(c) || c == '\n' || c == '\r' || c == '\t') good++;
        }
        return good > n * 0.7;
    }
}
