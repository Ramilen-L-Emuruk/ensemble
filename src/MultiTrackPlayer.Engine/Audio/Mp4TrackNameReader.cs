using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MultiTrackPlayer.Engine.Audio;

/// <summary>
/// MP4/MOV の moov/trak/udta/name ボックスからトラック名を読み取る。
/// FFmpeg の stream metadata では取得できない OBS 等の独自トラック名に対応。
/// </summary>
internal static class Mp4TrackNameReader
{
    /// <summary>
    /// ファイルを解析し、1-based トラックID → トラック名 の辞書を返す。
    /// </summary>
    public static Dictionary<int, string> Read(string filePath)
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var fs = File.OpenRead(filePath);
            long moovOffset = FindTopLevelBox(fs, "moov");
            if (moovOffset < 0) return result;

            long moovSize = ReadBoxSize(fs, moovOffset);
            ParseMoov(fs, moovOffset + 8, moovOffset + moovSize, result);
        }
        catch
        {
            // メタデータ読み取り失敗は無視してフォールバックに任せる
        }
        return result;
    }

    private static void ParseMoov(Stream fs, long start, long end, Dictionary<int, string> result)
    {
        int trackId = 1;
        long pos = start;
        while (pos < end - 8)
        {
            if (!TryReadBoxHeader(fs, pos, out long size, out string type)) break;
            if (type == "trak")
            {
                string? name = FindTrakName(fs, pos + 8, pos + size);
                if (!string.IsNullOrEmpty(name))
                    result[trackId] = name!;
                trackId++;
            }
            pos += size;
        }
    }

    private static string? FindTrakName(Stream fs, long start, long end)
    {
        long pos = start;
        while (pos < end - 8)
        {
            if (!TryReadBoxHeader(fs, pos, out long size, out string type)) break;
            if (type == "udta")
            {
                string? name = FindUdtaName(fs, pos + 8, pos + size);
                if (name != null) return name;
            }
            pos += size;
        }
        return null;
    }

    private static string? FindUdtaName(Stream fs, long start, long end)
    {
        long pos = start;
        while (pos < end - 8)
        {
            if (!TryReadBoxHeader(fs, pos, out long size, out string type)) break;
            if (type == "name")
            {
                int payloadLen = (int)(size - 8);
                if (payloadLen > 0)
                {
                    fs.Position = pos + 8;
                    var bytes = new byte[payloadLen];
                    int read = fs.Read(bytes, 0, payloadLen);
                    // null 終端があれば除去
                    int nullIdx = Array.IndexOf(bytes, (byte)0, 0, read);
                    int len = nullIdx >= 0 ? nullIdx : read;
                    return Encoding.UTF8.GetString(bytes, 0, len).Trim();
                }
            }
            pos += size;
        }
        return null;
    }

    private static long FindTopLevelBox(Stream fs, string targetType)
    {
        long pos = 0;
        while (pos < fs.Length - 8)
        {
            if (!TryReadBoxHeader(fs, pos, out long size, out string type)) break;
            if (type == targetType) return pos;
            if (size == 0) break;
            pos += size;
        }
        return -1;
    }

    private static long ReadBoxSize(Stream fs, long pos)
    {
        TryReadBoxHeader(fs, pos, out long size, out _);
        return size;
    }

    private static bool TryReadBoxHeader(Stream fs, long pos, out long size, out string type)
    {
        size = 0; type = "";
        if (pos + 8 > fs.Length) return false;
        fs.Position = pos;
        var buf = new byte[8];
        if (fs.Read(buf, 0, 8) < 8) return false;

        size = ((long)buf[0] << 24) | ((long)buf[1] << 16) | ((long)buf[2] << 8) | buf[3];
        type = Encoding.Latin1.GetString(buf, 4, 4);

        if (size == 1) // 64-bit extended size
        {
            var ext = new byte[8];
            if (fs.Read(ext, 0, 8) < 8) return false;
            size = 0;
            for (int i = 0; i < 8; i++) size = (size << 8) | ext[i];
        }
        else if (size == 0)
        {
            size = fs.Length - pos;
        }

        return size >= 8;
    }
}
