using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace LinkTest.Core;

/// <summary>RGB colour for <see cref="PdfWriter"/> (components 0-1).</summary>
public readonly record struct PdfColor(double R, double G, double B)
{
    public static PdfColor Hex(string hex)
    {
        hex = hex.TrimStart('#');
        return new(Convert.ToInt32(hex[..2], 16) / 255.0, Convert.ToInt32(hex[2..4], 16) / 255.0, Convert.ToInt32(hex[4..6], 16) / 255.0);
    }
    public static readonly PdfColor Black = new(0, 0, 0);
    public static readonly PdfColor White = new(1, 1, 1);
}

/// <summary>
/// Minimal PDF 1.4 writer: A4 pages, the standard Helvetica fonts (no embedding), text, lines, rectangles and
/// polylines. Coordinates are in points with the origin at the top-left of the page. Text is WinAnsi encoded;
/// characters outside it are replaced with close ASCII equivalents.
/// </summary>
public sealed class PdfWriter
{
    public const double PageWidth = 595.28, PageHeight = 841.89;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly List<PdfPage> _pages = new();

    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public IReadOnlyList<PdfPage> Pages => _pages;

    public PdfPage AddPage()
    {
        var p = new PdfPage();
        _pages.Add(p);
        return p;
    }

    public byte[] ToBytes()
    {
        var objs = new List<byte[]>();
        int Add(byte[] b) { objs.Add(b); return objs.Count; }
        byte[] A(string s) => Encoding.Latin1.GetBytes(s);

        int catalog = Add(Array.Empty<byte>());
        int pagesObj = Add(Array.Empty<byte>());
        int f1 = Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        int f2 = Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        var kids = new List<int>();
        foreach (var p in _pages)
        {
            byte[] raw = Encoding.Latin1.GetBytes(p.Content.ToString());
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            byte[] data = ms.ToArray();
            var stream = new MemoryStream();
            stream.Write(A($"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n"));
            stream.Write(data);
            stream.Write(A("\nendstream"));
            int content = Add(stream.ToArray());
            kids.Add(Add(A($"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                           $"/Resources << /Font << /F1 {f1} 0 R /F2 {f2} 0 R >> >> /Contents {content} 0 R >>")));
        }
        objs[catalog - 1] = A($"<< /Type /Catalog /Pages {pagesObj} 0 R >>");
        objs[pagesObj - 1] = A($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");
        int info = Add(A($"<< /Title {PdfText.Literal(Title)} /Author {PdfText.Literal(Author)} /Producer (LinkTest) " +
                         $"/CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>"));

        var outp = new MemoryStream();
        outp.Write(A("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new long[objs.Count];
        for (int i = 0; i < objs.Count; i++)
        {
            offsets[i] = outp.Position;
            outp.Write(A($"{i + 1} 0 obj\n"));
            outp.Write(objs[i]);
            outp.Write(A("\nendobj\n"));
        }
        long xref = outp.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root {catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        outp.Write(A(sb.ToString()));
        return outp.ToArray();
    }

    internal static string N(double v) => Math.Round(v, 2).ToString("0.##", Inv);
}

public enum PdfAlign { Left, Center, Right }

public sealed class PdfPage
{
    internal readonly StringBuilder Content = new();
    static string N(double v) => PdfWriter.N(v);
    static double Y(double y) => PdfWriter.PageHeight - y;

    void Color(PdfColor c, bool fill) => Content.Append($"{N(c.R)} {N(c.G)} {N(c.B)} {(fill ? "rg" : "RG")}\n");

    /// <summary>Draws text with its baseline at <paramref name="y"/>.</summary>
    public void Text(double x, double y, string text, double size = 10, bool bold = false, PdfColor? color = null, PdfAlign align = PdfAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return;
        double w = PdfText.Width(text, size, bold);
        if (align == PdfAlign.Right) x -= w;
        else if (align == PdfAlign.Center) x -= w / 2;
        Color(color ?? PdfColor.Black, true);
        Content.Append($"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(Y(y))} Td {PdfText.Literal(text)} Tj ET\n");
    }

    public void Rect(double x, double y, double w, double h, PdfColor? fill = null, PdfColor? stroke = null, double lineWidth = 0.5)
    {
        if (fill is { } f) Color(f, true);
        if (stroke is { } s) { Color(s, false); Content.Append($"{N(lineWidth)} w\n"); }
        Content.Append($"{N(x)} {N(Y(y + h))} {N(w)} {N(h)} re {(fill != null && stroke != null ? "B" : fill != null ? "f" : "S")}\n");
    }

    public void Line(double x1, double y1, double x2, double y2, PdfColor color, double width = 0.5, bool dashed = false)
    {
        Color(color, false);
        Content.Append($"{N(width)} w {(dashed ? "[2 2] 0 d" : "[] 0 d")} {N(x1)} {N(Y(y1))} m {N(x2)} {N(Y(y2))} l S [] 0 d\n");
    }

    /// <summary>Connected line; NaN points break it into segments.</summary>
    public void Polyline(IReadOnlyList<(double x, double y)> pts, PdfColor color, double width = 1)
    {
        Color(color, false);
        Content.Append($"{N(width)} w 1 j 1 J\n");
        bool open = false;
        foreach (var (x, y) in pts)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) { if (open) Content.Append("S\n"); open = false; continue; }
            Content.Append($"{N(x)} {N(Y(y))} {(open ? "l" : "m")}\n");
            open = true;
        }
        if (open) Content.Append("S\n");
        Content.Append("0 j 0 J\n");
    }
}

/// <summary>WinAnsi encoding and Helvetica metrics.</summary>
public static class PdfText
{
    // Advance widths (1/1000 em) for characters 32..126.
    static readonly short[] Regular =
    {
        278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
        1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
        333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584,
    };
    static readonly short[] Bold =
    {
        278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
        975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
        333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584,
    };

    /// <summary>Maps text to WinAnsi bytes, replacing characters the standard fonts lack.</summary>
    public static byte[] Encode(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            string r = c switch
            {
                '↑' => "up", '↓' => "down", '→' => "->", '←' => "<-", '≤' => "<=", '≥' => ">=", '≈' => "~", '∞' => "inf",
                '…' => "\u0085", '–' => "\u0096", '—' => "\u0097", '•' => "\u0095", '‘' => "\u0091", '’' => "\u0092",
                '“' => "\u0093", '”' => "\u0094", '€' => "\u0080", '✓' => "OK", '×' => "×",
                _ => c < 256 ? c.ToString() : "?",
            };
            sb.Append(r);
        }
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static double Width(string s, double size, bool bold)
    {
        var t = bold ? Bold : Regular;
        double w = 0;
        foreach (byte b in Encode(s))
            w += b >= 32 && b <= 126 ? t[b - 32] : b switch { 0x96 => 556, 0x97 => 1000, 0x85 => 1000, 0x95 => 350, 0xB7 => 278, 0xD7 => 584, 0xB0 => 400, _ => 556 };
        return w * size / 1000;
    }

    /// <summary>A PDF literal string, e.g. (text).</summary>
    public static string Literal(string s)
    {
        var sb = new StringBuilder("(");
        foreach (byte b in Encode(s))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b < 32) sb.Append(' ');
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    /// <summary>Shortens text with "..." to fit a width.</summary>
    public static string Fit(string s, double width, double size, bool bold)
    {
        if (Width(s, size, bold) <= width) return s;
        while (s.Length > 1 && Width(s + "...", size, bold) > width) s = s[..^1];
        return s.TrimEnd() + "...";
    }

    /// <summary>Word-wraps text to lines that fit a width.</summary>
    public static List<string> Wrap(string s, double width, double size, bool bold)
    {
        var lines = new List<string>();
        foreach (var para in s.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' '))
            {
                string cand = line.Length == 0 ? word : line + " " + word;
                if (Width(cand, size, bold) <= width || line.Length == 0) line = cand;
                else { lines.Add(line); line = word; }
            }
            lines.Add(line);
        }
        return lines;
    }
}
