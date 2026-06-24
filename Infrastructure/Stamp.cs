using System.Globalization;

namespace SimpleDiffusion.Infrastructure;

/// <summary>
/// Culture-invariant local timestamps for filenames and embedded metadata. Using the invariant
/// culture keeps generated names/stamps identical on every machine — otherwise a non-Gregorian
/// calendar (e.g. th-TH, ar-SA) would emit a different year or non-ASCII digits in filenames.
/// </summary>
public static class Stamp
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Filename-safe local timestamp: <c>yyyyMMdd_HHmmss</c>.</summary>
    public static string File() => DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);

    /// <summary>Filename-safe timestamp for a specific time: <c>yyyyMMdd_HHmmss</c>.</summary>
    public static string File(DateTime dt) => dt.ToString("yyyyMMdd_HHmmss", Inv);

    /// <summary>Filename-safe local timestamp with milliseconds: <c>yyyyMMdd_HHmmss_fff</c>.</summary>
    public static string FileMs() => DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", Inv);

    /// <summary>Human-readable local timestamp for embedded PNG metadata: <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    public static string Meta() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv);

    /// <summary>Date only: <c>yyyy-MM-dd</c>.</summary>
    public static string Date(DateTime dt) => dt.ToString("yyyy-MM-dd", Inv);
    public static string Date(DateTimeOffset dt) => dt.ToString("yyyy-MM-dd", Inv);
}
