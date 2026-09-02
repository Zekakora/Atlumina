using MyAlbum.Core.Models;

namespace MyAlbum.Core.Services;

/// <summary>Result of the CPU color analysis for one photo.</summary>
public sealed record ColorAnalysisResult(string DominantColorsCsv, bool IsMono);

/// <summary>
/// Pure-CPU color analysis (no model, always available): downsamples a photo and computes
/// up to 5 dominant colors (frequency-based HSV-bucket averaging) plus a monochrome
/// (B/W or sepia) flag. This is the cheapest part of the "无意义照片" heuristic and feeds
/// the dominant-color / mono filters.
/// </summary>
public static class ColorAnalysisService
{
    private const int SampleSize = 128;                   // longest side of the pixel sample
    private const int Buckets = 5;                        // dominant colors kept
    private const int HueBins = 24;                       // hue bins of 15°
    private const double MonoSaturationThreshold = 0.12;  // mean HSV saturation below this = mono
    private const double MonoChromaGap = 0.10;            // mean |R-B| spread below this = mono

    /// <summary>
    /// Runs color analysis on <paramref name="filePath"/>. Returns null when the file cannot
    /// be decoded. Dominant colors are "#rrggbb" hex strings, most frequent first.
    /// </summary>
    public static ColorAnalysisResult? Analyze(string filePath)
    {
        var rgb = WicGrayscale.GetRgbFixed(filePath, SampleSize, SampleSize);
        if (rgb is null || rgb.Rgb.Length == 0)
        {
            return null;
        }

        var colors = GetDominantColors(rgb.Rgb);
        bool mono = IsMonochrome(rgb.Rgb);
        string csv = string.Join(",", colors.Select(c => "#" + c.ToString("X6")));
        return new ColorAnalysisResult(csv, mono);
    }

    /// <summary>
    /// Dominant colors via frequency quantization: convert each pixel to HSV, quantize hue
    /// into 24 bins (ignoring near-gray pixels), then return the average RGB of the most
    /// populated bins. Near-gray fallback returns a gray ramp when the photo is mostly gray.
    /// </summary>
    public static List<int> GetDominantColors(byte[] rgb)
    {
        int count = rgb.Length / 3;
        var bins = new List<(int Bin, int Count, int R, int G, int B)>(HueBins);
        for (int b = 0; b < HueBins; b++)
        {
            bins.Add((b, 0, 0, 0, 0));
        }
        int colored = 0;

        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            int r = rgb[o], g = rgb[o + 1], b = rgb[o + 2];
            if (Saturation(r, g, b) < MonoSaturationThreshold)
            {
                continue;
            }
            double h = HsvHue(r, g, b);
            int bin = (int)(h / (360.0 / HueBins)) % HueBins;
            var entry = bins[bin];
            bins[bin] = (bin, entry.Count + 1, entry.R + r, entry.G + g, entry.B + b);
            colored++;
        }

        var result = new List<int>(Buckets);
        if (colored == 0)
        {
            // Mostly gray: return a gray ramp so the palette isn't empty.
            for (int i = 0; i < Buckets; i++)
            {
                int level = 64 + i * 40;
                result.Add((level << 16) | (level << 8) | level);
            }
            return result;
        }

        foreach (var (_, binCount, rSum, gSum, bSum) in bins
            .OrderByDescending(b => b.Count)
            .ThenByDescending(b => Saturation(b.R / Math.Max(1, b.Count), b.G / Math.Max(1, b.Count), b.B / Math.Max(1, b.Count))))
        {
            if (result.Count >= Buckets || binCount == 0)
            {
                break;
            }
            int avgR = (int)Math.Round(rSum / (double)binCount);
            int avgG = (int)Math.Round(gSum / (double)binCount);
            int avgB = (int)Math.Round(bSum / (double)binCount);
            result.Add((avgR << 16) | (avgG << 8) | avgB);
        }
        return result;
    }

    /// <summary>
    /// True when the photo is monochrome: either very low mean saturation, or near-equal RGB
    /// channels (sepia/BW).
    /// </summary>
    public static bool IsMonochrome(byte[] rgb)
    {
        int count = rgb.Length / 3;
        double satSum = 0;
        double chromaSum = 0;
        for (int i = 0; i < count; i++)
        {
            int o = i * 3;
            satSum += Saturation(rgb[o], rgb[o + 1], rgb[o + 2]);
            chromaSum += Math.Abs(rgb[o] - rgb[o + 2]) / 255.0;
        }
        if (count == 0)
        {
            return true;
        }
        double meanSat = satSum / count;
        double meanChroma = chromaSum / count;
        return meanSat < MonoSaturationThreshold || meanChroma < MonoChromaGap;
    }

    private static double HsvHue(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double d = max - min;
        double h;
        if (d == 0)
        {
            return 0;
        }
        if (max == rf)
        {
            h = 60 * (((gf - bf) / d) % 6);
        }
        else if (max == gf)
        {
            h = 60 * (((bf - rf) / d) + 2);
        }
        else
        {
            h = 60 * (((rf - gf) / d) + 4);
        }
        if (h < 0)
        {
            h += 360;
        }
        return h;
    }

    private static double Saturation(int r, int g, int b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        return max == 0 ? 0 : (max - min) / max;
    }
}
