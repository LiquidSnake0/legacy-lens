using PdfSharp.Drawing;
using Color = MigraDoc.DocumentObjectModel.Color;

namespace LegacyLens.Api.Pdf;

/// <summary>
/// The tokens of web/src/styles.scss and site/assets/site.css, for paper.
///
/// Greys and one accent, no red. Concrete appears on the cover only: in the
/// body a grey ground costs toner on every page and makes small type harder to
/// read, so the report itself is ink on white and the identity is carried by
/// the face, the flat black blocks and the one green.
/// </summary>
internal static class Palette
{
    public const uint Ink = 0x161616;
    public const uint Muted = 0x4a4a48;
    public const uint Faint = 0x6b6a67;
    public const uint Line = 0xc9c9c9;
    public const uint Wash = 0xf2f2f2;
    public const uint Slab = 0xf1f0ec;
    public const uint Slab2 = 0xe4e2dd;
    public const uint Wall = 0xbdbbb6;
    public const uint Accent = 0x2d7548;
    public const uint OnBlock = 0xf1f0ec;
    public const uint Paper = 0xffffff;

    public static XColor X(uint rgb) => XColor.FromArgb((int)(0xff000000 | rgb));

    public static XColor X(uint rgb, double alpha) =>
        XColor.FromArgb((int)Math.Round(alpha * 255), (int)(rgb >> 16) & 0xff, (int)(rgb >> 8) & 0xff, (int)rgb & 0xff);

    public static Color M(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
