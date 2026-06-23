using System.Text;

namespace SimpleDiffusion.Components;

/// <summary>Reads Stable Diffusion (A1111) generation parameters embedded in a PNG's text chunks.</summary>
public static class PngMetadata
{
    /// <summary>Extract the raw "parameters" text from a base64 (or data-URL) PNG, or null.</summary>
    public static string? ExtractParameters(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma >= 0) base64 = base64[(comma + 1)..];
        }
        try { return ExtractParameters(Convert.FromBase64String(base64)); }
        catch { return null; }
    }

    /// <summary>Extract the "parameters" tEXt/iTXt chunk value from PNG bytes, or null.</summary>
    public static string? ExtractParameters(byte[] png) => ExtractText(png, "parameters");

    /// <summary>Extract a base64 PNG's text chunk for a given keyword, or null.</summary>
    public static string? ExtractText(string? base64, string keyword)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            if (comma >= 0) base64 = base64[(comma + 1)..];
        }
        try { return ExtractText(Convert.FromBase64String(base64), keyword); }
        catch { return null; }
    }

    /// <summary>Extract a tEXt/iTXt chunk value by keyword from PNG bytes, or null.</summary>
    public static string? ExtractText(byte[] png, string keyword)
    {
        // PNG signature.
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return null;

        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (len < 0 || pos + 12 + len > png.Length) break;

            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            int dataStart = pos + 8;

            if (type == "tEXt")
            {
                var (kw, text) = ReadLatin1Keyed(png, dataStart, len);
                if (string.Equals(kw, keyword, StringComparison.OrdinalIgnoreCase)) return text;
            }
            else if (type == "iTXt")
            {
                var text = ReadITxt(png, dataStart, len, out var kw);
                if (text != null && string.Equals(kw, keyword, StringComparison.OrdinalIgnoreCase)) return text;
            }
            else if (type == "IEND") break;

            pos = dataStart + len + 4; // data + 4-byte CRC
        }
        return null;
    }

    private static (string Keyword, string Text) ReadLatin1Keyed(byte[] b, int start, int len)
    {
        int nul = Array.IndexOf(b, (byte)0, start, len);
        if (nul < 0) return ("", "");
        var kw = Encoding.Latin1.GetString(b, start, nul - start);
        var text = Encoding.Latin1.GetString(b, nul + 1, start + len - (nul + 1));
        return (kw, text);
    }

    // iTXt: keyword \0 compFlag compMethod langTag \0 transKeyword \0 text(UTF-8). Uncompressed only.
    private static string? ReadITxt(byte[] b, int start, int len, out string keyword)
    {
        keyword = "";
        int end = start + len;
        int p = Array.IndexOf(b, (byte)0, start, len);
        if (p < 0) return null;
        keyword = Encoding.Latin1.GetString(b, start, p - start);
        p++;
        if (p + 2 > end) return null;
        byte compFlag = b[p++]; p++; // skip comp method
        int langEnd = Array.IndexOf(b, (byte)0, p, end - p); if (langEnd < 0) return null; p = langEnd + 1;
        int transEnd = Array.IndexOf(b, (byte)0, p, end - p); if (transEnd < 0) return null; p = transEnd + 1;
        if (compFlag != 0) return null; // skip compressed payloads
        return Encoding.UTF8.GetString(b, p, end - p);
    }

    /// <summary>Split A1111 parameters text into positive + negative prompts. Either may be empty
    /// (e.g. an image whose info is only the "Steps: …" parameter line has no prompt at all).</summary>
    public static (string Positive, string Negative) ParsePrompts(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters)) return ("", "");
        var text = parameters.Replace("\r\n", "\n");

        int paramsStart = IndexOfLine(text, "Steps:");      // start of the "Steps: …" params line
        int negIdx = IndexOfLine(text, "Negative prompt:");
        int end = paramsStart >= 0 ? paramsStart : text.Length;

        string positive, negative = "";
        if (negIdx >= 0 && (paramsStart < 0 || negIdx < paramsStart))
        {
            positive = text[..negIdx];
            negative = text[(negIdx + "Negative prompt:".Length)..end];
        }
        else
        {
            positive = text[..end];
        }
        return (positive.Trim(), negative.Trim());
    }

    /// <summary>Pull the integer Seed out of an A1111 parameters string, or null if absent.</summary>
    public static long? ParseSeed(string? parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(parameters, @"(?:^|,)\s*Seed:\s*(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, out var s) ? s : null;
    }

    /// <summary>Insert a tEXt chunk (keyword + Latin-1 text) right before IEND, leaving every
    /// existing chunk untouched. Returns the input unchanged if it isn't a valid PNG.</summary>
    public static byte[] AddTextChunk(byte[] png, string keyword, string text)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return png;

        int pos = 8, iendPos = -1;
        while (pos + 12 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (len < 0 || pos + 12 + len > png.Length) break;
            if (Encoding.ASCII.GetString(png, pos + 4, 4) == "IEND") { iendPos = pos; break; }
            pos = pos + 12 + len;
        }
        if (iendPos < 0) return png;

        var kw = Encoding.Latin1.GetBytes(keyword);
        var tx = Encoding.Latin1.GetBytes(text);
        var data = new byte[kw.Length + 1 + tx.Length];
        Buffer.BlockCopy(kw, 0, data, 0, kw.Length);
        data[kw.Length] = 0;
        Buffer.BlockCopy(tx, 0, data, kw.Length + 1, tx.Length);

        var chunk = BuildChunk("tEXt", data);
        var result = new byte[png.Length + chunk.Length];
        Buffer.BlockCopy(png, 0, result, 0, iendPos);
        Buffer.BlockCopy(chunk, 0, result, iendPos, chunk.Length);
        Buffer.BlockCopy(png, iendPos, result, iendPos + chunk.Length, png.Length - iendPos);
        return result;
    }

    private static byte[] BuildChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        chunk[0] = (byte)(data.Length >> 24); chunk[1] = (byte)(data.Length >> 16);
        chunk[2] = (byte)(data.Length >> 8); chunk[3] = (byte)data.Length;
        Encoding.ASCII.GetBytes(type, 0, 4, chunk, 4);
        Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
        uint crc = Crc32(chunk, 4, 8 + data.Length); // CRC over type + data
        int c = 8 + data.Length;
        chunk[c] = (byte)(crc >> 24); chunk[c + 1] = (byte)(crc >> 16);
        chunk[c + 2] = (byte)(crc >> 8); chunk[c + 3] = (byte)crc;
        return chunk;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    private static uint Crc32(byte[] buf, int offset, int end)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = offset; i < end; i++) c = CrcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // Index of a line that starts with prefix (start-of-text or after a newline), else -1.
    private static int IndexOfLine(string text, string prefix)
    {
        if (text.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        int i = text.IndexOf("\n" + prefix, StringComparison.Ordinal);
        return i < 0 ? -1 : i + 1;
    }

    /// <summary>Full, labelled view of an image's metadata: generation info first (the positive
    /// prompt is labelled, since A1111 stores it unlabelled), then any upscaling info, then the
    /// generation timestamp last. A missing positive prompt is noted rather than labelled. Null when
    /// there's nothing to show.</summary>
    public static string? FormatFull(string? parameters, string? upscaling = null, string? generated = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            var text = parameters.Replace("\r\n", "\n");
            var (pos, neg) = ParsePrompts(text);
            int paramsStart = IndexOfLine(text, "Steps:");
            string settings = paramsStart >= 0 ? text[paramsStart..].Trim() : "";

            // No "Positive prompt:" header when there isn't one — just note the absence.
            if (pos.Length > 0) parts.Add("Positive prompt:\n" + pos);
            else parts.Add(neg.Length > 0 ? "(no positive prompt)" : "(no prompt)");

            if (neg.Length > 0) parts.Add("Negative prompt:\n" + neg);
            if (settings.Length > 0) parts.Add(settings);
        }
        if (!string.IsNullOrWhiteSpace(upscaling)) parts.Add("Upscaling:\n" + upscaling.Trim());
        if (!string.IsNullOrWhiteSpace(generated)) parts.Add("Generated: " + generated);
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>A display string of just the prompt words (positive/negative), or null if neither exists.</summary>
    public static string? FormatPrompts(string positive, string negative)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(positive)) parts.Add("Positive:\n" + positive.Trim());
        if (!string.IsNullOrWhiteSpace(negative)) parts.Add("Negative:\n" + negative.Trim());
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
