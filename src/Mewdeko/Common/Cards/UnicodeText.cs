using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Buffer = HarfBuzzSharp.Buffer;

namespace Mewdeko.Common.Cards;

/// <summary>
///     Draws and measures text with HarfBuzz shaping, taking a fallback font for each grapheme the font has no glyph for.
/// </summary>
public static class UnicodeText
{
    /// <summary>
    ///     Draws text at a baseline and returns its width.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The baseline.</param>
    /// <param name="text">The text.</param>
    /// <param name="font">The preferred font.</param>
    /// <param name="paint">The paint.</param>
    /// <returns>The drawn width.</returns>
    public static float Draw(SKCanvas canvas, float x, float y, string text, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (font.ContainsGlyphs(text))
            return DrawShaped(canvas, x, y, text, font, paint);

        var width = 0f;
        foreach (var (run, typeface) in Runs(text, font))
        {
            if (typeface is null)
            {
                width += DrawShaped(canvas, x + width, y, run, font, paint);
                continue;
            }

            using var fallback = typeface.ToFont(font.Size);
            width += DrawShaped(canvas, x + width, y, run, fallback, paint);
        }

        return width;
    }

    /// <summary>
    ///     Measures text the way <see cref="Draw" /> draws it.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The preferred font.</param>
    /// <returns>The width.</returns>
    public static float Measure(string text, SKFont font)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        if (font.ContainsGlyphs(text))
            return MeasureShaped(text, font);

        var width = 0f;
        foreach (var (run, typeface) in Runs(text, font))
        {
            if (typeface is null)
            {
                width += MeasureShaped(run, font);
                continue;
            }

            using var fallback = typeface.ToFont(font.Size);
            width += MeasureShaped(run, fallback);
        }

        return width;
    }

    private static List<(string Text, SKTypeface? Typeface)> Runs(string text, SKFont font)
    {
        var runs = new List<(string, SKTypeface?)>();
        var current = new StringBuilder();
        SKTypeface? currentFace = null;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var face = font.ContainsGlyphs(element)
                ? null
                : SKFontManager.Default.MatchCharacter(font.Typeface.FamilyName, font.Typeface.FontStyle, null,
                    element.EnumerateRunes().First().Value);
            if (current.Length > 0 && face?.FamilyName != currentFace?.FamilyName)
            {
                runs.Add((current.ToString(), currentFace));
                current.Clear();
            }

            current.Append(element);
            currentFace = face;
        }

        if (current.Length > 0)
            runs.Add((current.ToString(), currentFace));
        return runs;
    }

    private static float DrawShaped(SKCanvas canvas, float x, float y, string text, SKFont font, SKPaint paint)
    {
        var width = MeasureShaped(text, font);
        canvas.DrawShapedText(text, x, y, SKTextAlign.Left, font, paint);
        return width;
    }

    private static float MeasureShaped(string text, SKFont font)
    {
        try
        {
            using var blob = font.Typeface.OpenStream().ToHarfBuzzBlob();
            using var face = new Face(blob, 0);
            face.UnitsPerEm = font.Typeface.UnitsPerEm;
            using var hbFont = new HarfBuzzSharp.Font(face);
            using var buffer = new Buffer();
            buffer.AddUtf16(text);
            buffer.GuessSegmentProperties();
            hbFont.Shape(buffer);
            hbFont.GetScale(out var xScale, out _);
            return buffer.GlyphPositions.Sum(p => p.XAdvance) * (font.Size / xScale);
        }
        catch
        {
            return font.MeasureText(text);
        }
    }
}
