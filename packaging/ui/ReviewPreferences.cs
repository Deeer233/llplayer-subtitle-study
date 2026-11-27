using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace LLPlayer.Services;

internal sealed record ReviewPreferences([property: JsonRequired] double Width, [property: JsonRequired] double Height,
    [property: JsonRequired] int Tab, [property: JsonRequired] Guid? Lookup)
{
    internal const double DefaultWidth = 640, DefaultHeight = 730, MinWidth = 450, MinHeight = 430;
    internal static readonly ReviewPreferences Default = new(DefaultWidth, DefaultHeight, 0, null);
    private const string FileName = "polish-window.json";

    internal static ReviewPreferences Load(string dataFolder)
    {
        try
        {
            var file = new FileInfo(Path.Combine(dataFolder, FileName));
            if (!file.Exists || file.Length > 4096) return Default;
            var prefs = JsonSerializer.Deserialize<ReviewPreferences>(File.ReadAllBytes(file.FullName));
            return prefs is not null && prefs.IsValid ? prefs : Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return Default; }
    }

    internal bool Save(string dataFolder)
    {
        if (!IsValid) return false;
        var temp = Path.Combine(dataFolder, $".polish-window-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, this);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, Path.Combine(dataFolder, FileName), overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    internal (double Width, double Height) Fit(Rect workArea) => (
        Math.Clamp(Width, MinWidth, Math.Max(MinWidth, workArea.Width)),
        Math.Clamp(Height, MinHeight, Math.Max(MinHeight, workArea.Height)));

    private bool IsValid => double.IsFinite(Width) && Width >= MinWidth && Width <= 10000
        && double.IsFinite(Height) && Height >= MinHeight && Height <= 10000 && Tab is >= 0 and < 3;
}
