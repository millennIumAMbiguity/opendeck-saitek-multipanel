using StbImageSharp;

namespace SaitekMultiPanel;

/// <summary>
/// The panel's button LEDs are on/off only, but OpenDeck sends rendered key images.
/// Treat an image as "lit" when its average brightness exceeds a threshold, so a dark
/// state image (or no action) turns the LED off and a bright one turns it on.
/// </summary>
public static class LedImageAnalyzer
{
    public static int Threshold { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("MULTIPANEL_LED_THRESHOLD"), out var t) ? t : 40;

    public static bool IsLit(string? dataUrl, int position = -1)
    {
        if (string.IsNullOrEmpty(dataUrl))
            return false;

        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
            return false;

        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlue);
            var luminance = MeanLuminance(image);
            if (Log.Verbose)
            {
                // Debug aid: the exact image OpenDeck sent, to check what the LED decision was based on.
                File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"multipanel-key{position}.jpg"), bytes);
                Log.Debug($"key {position} image {image.Width}x{image.Height} mean brightness {luminance:0} (lit above {Threshold})");
            }
            return luminance > Threshold;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn($"Could not decode key image: {ex.Message}");
            return false;
        }
    }

    /// <summary>Samples a 24x24 grid of the decoded RGB pixels.</summary>
    private static double MeanLuminance(ImageResult image)
    {
        const int samples = 24;
        var data = image.Data;
        double sum = 0;
        for (var y = 0; y < samples; y++)
        {
            var row = y * image.Height / samples * image.Width;
            for (var x = 0; x < samples; x++)
            {
                var i = (row + x * image.Width / samples) * 3;
                sum += 0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2];
            }
        }
        return sum / (samples * samples);
    }
}
