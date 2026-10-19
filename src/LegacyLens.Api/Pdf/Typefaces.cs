using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace LegacyLens.Api.Pdf;

/// <summary>
/// The faces the PDF is set in, read from the executable itself.
///
/// PDFsharp on anything but Windows has no idea where fonts live, and on
/// Windows it would find Arial rather than Montserrat. Either way the document
/// would change face with the machine that printed it, so the fonts travel in
/// the assembly and nothing is looked up on disk.
/// </summary>
internal sealed class Typefaces : IFontResolver
{
    /// <summary>Body text. Bold maps to the semibold cut, as on the page.</summary>
    public const string Sans = "Montserrat";

    /// <summary>Titles and the large figures.</summary>
    public const string Heavy = "Montserrat ExtraBold";

    /// <summary>Paths, identifiers, package names.</summary>
    public const string Mono = "DejaVu Sans Mono";

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, byte[]> Faces = new();

    /// <summary>
    /// The resolver is process-wide in PDFsharp and can be set once. Every
    /// caller goes through here, so a test suite rendering several documents
    /// does not trip over a second assignment.
    /// </summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is Typefaces) return;
            GlobalFontSettings.FontResolver = new Typefaces();
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        // Italic is drawn as an oblique of the upright face. The report uses it
        // for one line per step, and a fourth file for that is not worth it.
        var face = familyName switch
        {
            Heavy => "Montserrat-ExtraBold",
            Mono => "DejaVuSansMono",
            _ when bold => "Montserrat-SemiBold",
            _ => "Montserrat-Regular",
        };

        var simulateBold = bold && familyName == Mono;
        return new FontResolverInfo(face, simulateBold, italic);
    }

    public byte[]? GetFont(string faceName) =>
        Faces.GetOrAdd(faceName, name =>
        {
            using var stream = typeof(Typefaces).Assembly
                .GetManifestResourceStream($"Fonts.{name}.ttf")
                ?? throw new InvalidOperationException($"The font {name} is not in the executable.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
}
