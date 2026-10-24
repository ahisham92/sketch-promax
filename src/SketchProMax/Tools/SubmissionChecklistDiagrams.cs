using System.Globalization;

namespace SketchProMax.Tools;

/// <summary>
/// The small cross-section sketches the submission checklist shows (a question's details, the setup's type cards, the
/// Word file): one per structure type and one of the design levels. Each is a list of shapes on a 400 x 220 drawing
/// (y down), in materials the window and the Word file colour the same way. Typical sections only, not to scale: they
/// show what the question means (which level, which layer), not the project's design. No Revit or WPF here.
/// </summary>
internal static class SubmissionChecklistDiagrams
{
    public const double Width = 400, Height = 220;

    /// <summary>What a shape is drawn in: its fill and outline.</summary>
    public enum Look { Water, Soil, Fill, Rock, Concrete, Steel, Line, Dash, Accent }

    /// <summary>
    /// A shape: Poly (closed, filled), Path (open line), Rect (x, y, w, h), Circle (x, y, r), Level (a design level: y,
    /// with its label on the left and a marker at the right) or Text (x, y). Points are x, y pairs.
    /// </summary>
    public sealed record Shape(string Kind, double[] Points, Look Look, string Text = "");

    public sealed record Diagram(string Key, string Title, IReadOnlyList<Shape> Shapes);

    private static Shape Poly(Look look, params double[] p) => new("poly", p, look);
    private static Shape Line(Look look, params double[] p) => new("path", p, look);
    private static Shape Rect(Look look, double x, double y, double w, double h) => new("rect", [x, y, w, h], look);
    private static Shape Circle(Look look, double x, double y, double r) => new("circle", [x, y, r], look);
    private static Shape Level(double y, string label) => new("level", [y], Look.Accent, label);
    private static Shape Text(double x, double y, string text) => new("text", [x, y], Look.Line, text);

    // Design levels shared by the sketches (y on the drawing).
    private const double Cope = 42, Hw = 78, Cd = 96;

    public static readonly Diagram[] All =
    [
        new("levels", "Design levels", Levels()),
        new("piled-deck", "Deck on piles", PiledDeck()),
        new("gravity-blocks", "Gravity block wall", Blocks()),
        new("caissons", "Caisson", Caisson()),
        new("sheet-piles", "Anchored sheet pile wall", SheetPiles()),
        new("retaining-wall", "Retaining wall", RetainingWall()),
        new("combi-wall", "Combi-wall quay", CombiWall()),
        new("relieving-platform", "Relieving platform quay", ReliefPlatform()),
        new("dolphins", "Breasting dolphin", Dolphin()),
        new("trestle", "Approach trestle", Trestle()),
        new("roro", "Ro-Ro ramp", RoRo()),
        new("pontoon", "Floating pontoon", Pontoon()),
        new("diaphragm-wall", "Anchored diaphragm wall", DiaphragmWall()),
        new("dredging", "Dredging and reclamation", Dredging()),
        new("crane-rails", "Crane rails", CraneRails()),
        new("pavement", "Yard pavement", Pavement()),
    ];

    public static Diagram? Find(string key) => All.FirstOrDefault(d => d.Key == key);

    /// <summary>The diagram a question shows: its own, else its structure type's (null when neither has one).</summary>
    public static Diagram? For(SubmissionChecklistMath.Question q, IReadOnlyList<string> projectTypes)
    {
        if (q.Diagram.Length > 0 && Find(q.Diagram) is { } own) return own;
        var type = SubmissionChecklistMath.TypeOfQuestion(q, projectTypes);
        return type.Length > 0 ? Find(type) : null;
    }

    private static List<Shape> Levels() =>
    [
        Poly(Look.Water, 0, 60, 230, 60, 230, 180, 0, 180),
        Poly(Look.Soil, 0, 180, 230, 180, 230, 220, 0, 220),
        Poly(Look.Concrete, 230, 30, 260, 30, 260, 220, 230, 220),
        Poly(Look.Fill, 260, 30, 400, 30, 400, 220, 260, 220),
        new("level", [30], Look.Accent, "Cope / deck level"),
        new("level", [60], Look.Accent, "Extreme water level"),
        new("level", [78], Look.Accent, "MHWS / HAT"),
        new("level", [100], Look.Accent, "MSL"),
        new("level", [122], Look.Accent, "MLWS"),
        new("level", [138], Look.Accent, "CD / LAT  0.00"),
        new("level", [180], Look.Accent, "Dredge level"),
        new("level", [192], Look.Accent, "Overdredge + tolerance"),
        Line(Look.Dash, 0, 192, 230, 192),
        Text(280, 70, "Agree every level"),
        Text(280, 84, "and its datum with"),
        Text(280, 98, "grading and marine"),
    ];

    private static List<Shape> PiledDeck() =>
    [
        Poly(Look.Water, 0, Hw, 289, Hw, 120, 165, 0, 165),
        Poly(Look.Soil, 0, 165, 120, 165, 320, 62, 400, 62, 400, 220, 0, 220),
        Line(Look.Rock, 120, 165, 320, 62),
        Rect(Look.Steel, 138, 58, 6, 150), Rect(Look.Steel, 188, 58, 6, 140), Rect(Look.Steel, 238, 58, 6, 130), Rect(Look.Steel, 288, 58, 6, 110),
        Poly(Look.Steel, 300, 58, 306, 58, 336, 200, 330, 200),
        Rect(Look.Concrete, 120, Cope, 215, 16),
        Rect(Look.Concrete, 112, 30, 8, 30),
        Level(Cope, "Deck level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(165, "Dredge level"), Level(205, "Pile toe"),
        Text(300, 150, "Slope under deck"),
    ];

    private static List<Shape> Blocks()
    {
        var shapes = new List<Shape>
        {
            Poly(Look.Water, 0, Hw, 180, Hw, 180, 170, 150, 170, 140, 182, 0, 182),
            Poly(Look.Soil, 0, 182, 400, 182, 400, 220, 0, 220),
            Poly(Look.Rock, 140, 182, 150, 170, 315, 170, 330, 182),
            Poly(Look.Fill, 240, Cope, 400, Cope, 400, 182, 330, 182, 315, 170, 300, 170, 300, 150, 280, 150, 280, 130, 262, 130, 262, 110, 248, 110, 248, 90, 238, 90, 238, 70, 240, 70),
            Poly(Look.Rock, 100, 182, 140, 182, 150, 174),
        };
        double[][] courses = [[150, 120], [130, 100], [110, 82], [90, 68], [70, 58]];
        foreach (var c in courses) shapes.Add(Rect(Look.Concrete, 180, c[0], c[1], 20));
        shapes.Add(Rect(Look.Concrete, 172, Cope, 68, 28));
        shapes.AddRange([Level(Cope, "Cope level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(170, "Founding level"), Level(182, "Dredge level"), Text(270, 120, "Backfill + filter")]);
        return shapes;
    }

    private static List<Shape> Caisson() =>
    [
        Poly(Look.Water, 0, Hw, 170, Hw, 170, 170, 150, 170, 140, 182, 0, 182),
        Poly(Look.Soil, 0, 182, 400, 182, 400, 220, 0, 220),
        Poly(Look.Rock, 140, 182, 150, 170, 305, 170, 320, 182),
        Poly(Look.Fill, 290, Cope, 400, Cope, 400, 182, 320, 182, 305, 170, 290, 170),
        Rect(Look.Concrete, 170, 60, 120, 110),
        Rect(Look.Fill, 176, 66, 108, 98),
        Rect(Look.Concrete, 212, 66, 4, 98), Rect(Look.Concrete, 246, 66, 4, 98),
        Rect(Look.Concrete, 164, Cope, 126, 18),
        Level(Cope, "Cope level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(170, "Founding level"), Level(182, "Dredge level"),
        Text(196, 120, "Cell fill"),
    ];

    private static List<Shape> SheetPiles() =>
    [
        Poly(Look.Water, 0, Hw, 200, Hw, 200, 170, 0, 170),
        Poly(Look.Soil, 0, 170, 200, 170, 200, 220, 0, 220),
        Poly(Look.Fill, 204, Cope, 400, Cope, 400, 220, 204, 220),
        Rect(Look.Steel, 198, 50, 6, 158),
        Rect(Look.Steel, 328, 52, 6, 58),
        Line(Look.Steel, 204, 64, 328, 64),
        Rect(Look.Concrete, 188, 36, 26, 20),
        Line(Look.Dash, 204, 170, 330, 40),
        Level(Cope, "Cope level"), Level(64, "Tie rod level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(170, "Dredge level"), Level(208, "Sheet pile toe"),
        Text(296, 100, "Anchor wall"), Text(250, 140, "Active wedge"),
    ];

    private static List<Shape> RetainingWall() =>
    [
        Poly(Look.Soil, 0, 150, 200, 150, 200, 220, 0, 220),
        Poly(Look.Fill, 216, Cope, 400, Cope, 400, 220, 300, 220, 300, 186, 216, 186),
        Poly(Look.Soil, 170, 186, 300, 186, 300, 220, 170, 220),
        Rect(Look.Concrete, 200, Cope, 16, 128),
        Rect(Look.Concrete, 170, 170, 130, 16),
        Poly(Look.Rock, 216, 60, 232, 60, 232, 170, 216, 170),
        Circle(Look.Steel, 240, 164, 5),
        Line(Look.Line, 192, 120, 216, 120), Line(Look.Line, 192, 140, 216, 140),
        Level(Cope, "Top of wall"), Level(150, "Ground in front"), Level(186, "Founding level"),
        Text(250, 120, "Drain + filter"), Text(310, 80, "Surcharge"),
    ];

    private static List<Shape> CombiWall() =>
    [
        Poly(Look.Water, 0, Hw, 196, Hw, 196, 170, 0, 170),
        Poly(Look.Soil, 0, 170, 196, 170, 196, 220, 0, 220),
        Poly(Look.Fill, 210, Cope, 400, Cope, 400, 220, 210, 220),
        Rect(Look.Steel, 196, 50, 14, 162),
        Line(Look.Dash, 203, 50, 203, 212),
        Rect(Look.Steel, 214, 50, 4, 136),
        Rect(Look.Steel, 320, 50, 6, 60),
        Line(Look.Steel, 210, 64, 320, 64),
        Rect(Look.Concrete, 186, 34, 38, 22),
        Level(Cope, "Cope level"), Level(64, "Tie rod level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(170, "Dredge level"),
        Level(186, "Infill sheet toe"), Level(212, "King pile toe"),
        Text(230, 140, "King pile + infill"), Text(296, 100, "Anchor wall"),
    ];

    private static List<Shape> ReliefPlatform() =>
    [
        Poly(Look.Water, 0, Hw, 196, Hw, 196, 172, 0, 172),
        Poly(Look.Soil, 0, 172, 196, 172, 196, 220, 0, 220),
        Poly(Look.Fill, 202, Cope, 400, Cope, 400, 220, 202, 220),
        Rect(Look.Steel, 196, 56, 6, 154),
        Rect(Look.Concrete, 190, 40, 150, 22),
        Poly(Look.Steel, 240, 62, 246, 62, 270, 206, 264, 206),
        Rect(Look.Steel, 290, 62, 6, 144),
        Rect(Look.Steel, 324, 62, 6, 140),
        Level(40, "Platform top"), Level(62, "Platform soffit"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(172, "Dredge level"), Level(206, "Pile toe"),
        Text(232, 30, "Relieving platform"), Text(300, 130, "Bearing piles"),
    ];

    private static List<Shape> Dolphin() =>
    [
        Poly(Look.Water, 0, Hw, 400, Hw, 400, 170, 0, 170),
        Poly(Look.Soil, 0, 170, 400, 170, 400, 220, 0, 220),
        Line(Look.Line, 20, 30, 70, 30, 120, 60, 120, 110, 60, 128, 20, 128),
        Rect(Look.Steel, 190, 62, 6, 146), Rect(Look.Steel, 226, 62, 6, 146),
        Poly(Look.Steel, 250, 62, 256, 62, 284, 205, 278, 205),
        Rect(Look.Concrete, 172, 40, 96, 24),
        Rect(Look.Accent, 160, 44, 12, 46),
        Level(40, "Cap level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(170, "Seabed"), Level(208, "Pile toe"),
        Text(40, 112, "Vessel"), Text(130, 104, "Fender"),
    ];

    private static List<Shape> Trestle()
    {
        var shapes = new List<Shape>
        {
            Poly(Look.Water, 0, Hw, 330, Hw, 330, 124, 0, 176),
            Poly(Look.Soil, 0, 176, 330, 124, 400, 104, 400, 220, 0, 220),
            Rect(Look.Concrete, 10, 46, 380, 12),
            Rect(Look.Concrete, 370, 46, 30, 70),
        };
        foreach (var x in new double[] { 70, 170, 270 })
        {
            shapes.Add(Rect(Look.Concrete, x - 14, 58, 34, 10));
            shapes.Add(Rect(Look.Steel, x - 10, 68, 5, 140));
            shapes.Add(Rect(Look.Steel, x + 10, 68, 5, 140));
        }
        shapes.AddRange([Level(46, "Deck level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(205, "Pile toe"), Text(330, 40, "Abutment"), Text(120, 40, "Span")]);
        return shapes;
    }

    private static List<Shape> RoRo() =>
    [
        Poly(Look.Water, 0, 70, 160, 70, 160, 176, 0, 176),
        Poly(Look.Soil, 0, 176, 400, 176, 400, 220, 0, 220),
        Poly(Look.Fill, 300, 40, 400, 40, 400, 176, 160, 176, 160, 120),
        Poly(Look.Concrete, 160, 96, 300, 40, 300, 54, 160, 110),
        Line(Look.Line, 20, 54, 70, 54, 70, 100, 20, 100),
        Line(Look.Accent, 70, 70, 160, 96),
        Level(40, "Top of ramp"), Level(70, "Highest tide"), Level(118, "Lowest tide"), Level(176, "Seabed"),
        Text(190, 64, "Ramp gradient"), Text(26, 92, "Vessel"),
    ];

    private static List<Shape> Pontoon() =>
    [
        Poly(Look.Water, 0, Hw, 330, Hw, 330, 180, 0, 180),
        Poly(Look.Soil, 0, 180, 400, 180, 400, 220, 0, 220),
        Poly(Look.Fill, 330, Cope, 400, Cope, 400, 180, 330, 180),
        Rect(Look.Steel, 136, 24, 6, 184), Rect(Look.Steel, 236, 24, 6, 184),
        Rect(Look.Concrete, 112, 66, 156, 24),
        Line(Look.Accent, 268, 68, 330, Cope),
        Level(Cope, "Quay level"), Level(66, "Pontoon deck"), Level(Hw, "MHWS"), Level(130, "MLWS"), Level(180, "Seabed"),
        Text(280, 36, "Gangway"), Text(146, 22, "Guide piles"),
    ];

    private static List<Shape> DiaphragmWall() =>
    [
        Poly(Look.Water, 0, Hw, 196, Hw, 196, 168, 0, 168),
        Poly(Look.Soil, 0, 168, 196, 168, 196, 220, 0, 220),
        Poly(Look.Fill, 214, Cope, 400, Cope, 400, 220, 214, 220),
        Rect(Look.Concrete, 196, 36, 18, 176),
        Line(Look.Steel, 214, 58, 330, 112),
        Circle(Look.Steel, 336, 115, 7),
        Level(36, "Capping beam"), Level(58, "Anchor level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(168, "Dredge level"), Level(212, "Wall toe"),
        Text(260, 150, "Ground anchor"),
    ];

    private static List<Shape> Dredging() =>
    [
        Poly(Look.Water, 0, Hw, 300, Hw, 260, 140, 210, 175, 90, 175, 40, 140, 0, 140),
        Poly(Look.Soil, 0, 140, 40, 140, 90, 175, 210, 175, 260, 140, 300, 140, 400, 140, 400, 220, 0, 220),
        Poly(Look.Fill, 300, Hw, 330, Cope, 400, Cope, 400, 140, 260, 140),
        Line(Look.Dash, 40, 140, 260, 140),
        Line(Look.Dash, 84, 187, 216, 187),
        Level(Cope, "Reclamation level"), Level(Hw, "MHWS"), Level(Cd, "CD"), Level(140, "Existing seabed"), Level(175, "Dredge level"), Level(187, "Overdredge"),
        Text(120, 160, "Side slope"),
    ];

    private static List<Shape> CraneRails() =>
    [
        Poly(Look.Fill, 0, 75, 400, 75, 400, 220, 0, 220),
        Rect(Look.Concrete, 20, 60, 360, 15),
        Rect(Look.Concrete, 74, 60, 32, 50), Rect(Look.Concrete, 294, 60, 32, 50),
        Rect(Look.Steel, 84, 54, 12, 6), Rect(Look.Steel, 304, 54, 12, 6),
        Line(Look.Line, 90, 54, 110, 12, 290, 12, 310, 54),
        Line(Look.Line, 110, 12, 10, 12),
        Line(Look.Accent, 90, 140, 310, 140),
        Text(170, 136, "Rail gauge"), Text(30, 30, "Boom"),
        Level(60, "Top of rail"), Level(110, "Rail beam soffit"),
    ];

    private static List<Shape> Pavement() =>
    [
        Rect(Look.Concrete, 0, 50, 400, 7),
        Rect(Look.Rock, 0, 57, 400, 14),
        Rect(Look.Fill, 0, 71, 400, 22),
        Rect(Look.Soil, 0, 93, 400, 127),
        Rect(Look.Concrete, 180, 46, 26, 26),
        Circle(Look.Steel, 193, 120, 9),
        Line(Look.Accent, 60, 36, 160, 44), Line(Look.Accent, 330, 36, 226, 44),
        Text(20, 32, "Fall"), Text(340, 32, "Fall"), Text(215, 124, "Drain to outfall"),
        Level(50, "Finished level"), Level(93, "Formation"),
        Text(300, 67, "Base"), Text(300, 86, "Sub-base"), Text(300, 112, "Subgrade (CBR)"),
    ];

    /// <summary>The diagram as an SVG (for checks and anything outside WPF), in the same colours as the window.</summary>
    public static string Svg(Diagram d)
    {
        static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(Width)} {N(Height)}\" font-family=\"Segoe UI, Arial\" font-size=\"10\">");
        sb.Append($"<rect width=\"{N(Width)}\" height=\"{N(Height)}\" fill=\"#FFFFFF\"/>");
        foreach (var s in d.Shapes)
        {
            var (fill, stroke, dash) = Colors(s.Look);
            var p = s.Points;
            switch (s.Kind)
            {
                case "poly": sb.Append($"<polygon points=\"{Pairs(p)}\" fill=\"{fill}\" stroke=\"{stroke}\"/>"); break;
                case "path": sb.Append($"<polyline points=\"{Pairs(p)}\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"2\"{(dash ? " stroke-dasharray=\"5 3\"" : "")}/>"); break;
                case "rect": sb.Append($"<rect x=\"{N(p[0])}\" y=\"{N(p[1])}\" width=\"{N(p[2])}\" height=\"{N(p[3])}\" fill=\"{fill}\" stroke=\"{stroke}\"/>"); break;
                case "circle": sb.Append($"<circle cx=\"{N(p[0])}\" cy=\"{N(p[1])}\" r=\"{N(p[2])}\" fill=\"{fill}\" stroke=\"{stroke}\"/>"); break;
                case "level":
                    sb.Append($"<line x1=\"0\" y1=\"{N(p[0])}\" x2=\"{N(Width)}\" y2=\"{N(p[0])}\" stroke=\"#1D4ED8\" stroke-width=\"0.6\" stroke-dasharray=\"3 3\"/>");
                    sb.Append($"<text x=\"4\" y=\"{N(p[0] - 2)}\" fill=\"#1D4ED8\">{ChecklistDocx.Esc(s.Text)}</text>"); break;
                case "text": sb.Append($"<text x=\"{N(p[0])}\" y=\"{N(p[1])}\" fill=\"#334155\">{ChecklistDocx.Esc(s.Text)}</text>"); break;
            }
        }
        return sb.Append("</svg>").ToString();
    }

    private static string Pairs(double[] p) =>
        string.Join(" ", Enumerable.Range(0, p.Length / 2).Select(i => p[2 * i].ToString("0.#", CultureInfo.InvariantCulture) + "," + p[2 * i + 1].ToString("0.#", CultureInfo.InvariantCulture)));

    /// <summary>Fill, outline and dashed for a look, as #RRGGBB (the window uses the same).</summary>
    public static (string Fill, string Stroke, bool Dash) Colors(Look look) => look switch
    {
        Look.Water => ("#DBEAFE", "#93C5FD", false),
        Look.Soil => ("#E7D3B5", "#A8865A", false),
        Look.Fill => ("#F3E8C8", "#C9B27C", false),
        Look.Rock => ("#B8B2A7", "#6B6358", false),
        Look.Concrete => ("#CBD5E1", "#475569", false),
        Look.Steel => ("#334155", "#1E293B", false),
        Look.Dash => ("none", "#64748B", true),
        Look.Accent => ("#1D4ED8", "#1D4ED8", false),
        _ => ("none", "#334155", false),
    };
}
