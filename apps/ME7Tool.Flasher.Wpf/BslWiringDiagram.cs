using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static ME7Tool.Core.Common.Lang;

namespace ME7Tool.Flasher.Wpf;

/// <summary>
/// Desenho esquemático da centralina (vista de cima, etiqueta em baixo à direita) com as ligações do modo boot:
/// +12V, massa, K-line e o pino de boot à massa. Os conectores imitam os reais (dois blocos de pinos em pares de
/// colunas); as posições dos pinos destacados vêm de fotos e são aproximadas — o que conta é o número.
/// </summary>
public sealed class BslWiringDiagram : Canvas
{
    public enum EcuModel { Me731Me31, Me73H4 }

    private static readonly Brush Housing = Frozen(0x3A, 0x3F, 0x45), HousingEdge = Frozen(0x70, 0x76, 0x7E);
    private static readonly Brush Shell = Frozen(0x2E, 0x31, 0x35), ShellEdge = Frozen(0x8A, 0x90, 0x98);
    private static readonly Brush Insert = Frozen(0x1C, 0x1E, 0x21), PinBrass = Frozen(0xA8, 0x8E, 0x5C);
    private static readonly Brush Fin = Frozen(0x55, 0x5B, 0x62);
    private static readonly Brush Red = Frozen(0xE0, 0x40, 0x40), Green = Frozen(0x3C, 0xC8, 0x5A);
    private static readonly Brush Gnd = Frozen(0x10, 0x10, 0x10), GndEdge = Frozen(0xE0, 0xE0, 0xE0);
    private static readonly Brush Text = Frozen(0xE0, 0xE0, 0xE0), Dim = Frozen(0xA0, 0xA0, 0xA0);

    // grelha dos pinos: colunas (x) e linhas (y) de cada conector
    private static readonly double[] RightCols = { 227, 239, 263, 275 };
    private static readonly double[] LeftCols = { 72, 84, 118, 130 };
    private const double RightRow0 = 82, LeftRow0 = 102, Pitch = 11;
    private const int RightRows = 16, LeftRows = 16;

    private EcuModel _model;

    public BslWiringDiagram()
    {
        Width = 630; Height = 345;
        Draw();
    }

    public EcuModel Model
    {
        get => _model;
        set { _model = value; Draw(); }
    }

    // numeração gravada nos conectores (4 colunas × 16): no direito a coluna da direita é 1–16 de baixo para cima,
    // depois 17–32, 33–48 e 49–64; no esquerdo a coluna da esquerda é 1–16 de cima para baixo, depois 17–32 …
    private static (double, double) R(int pin) => (RightCols[3 - (pin - 1) / 16], RightRow0 + (15 - (pin - 1) % 16) * Pitch);
    private static (double, double) L(int pin) => (LeftCols[(pin - 1) / 16], LeftRow0 + ((pin - 1) % 16) * Pitch);

    private void Draw()
    {
        Children.Clear();

        // caixa com as 4 orelhas de fixação
        Add(new Rectangle { Width = 300, Height = 300, RadiusX = 22, RadiusY = 22, Fill = Housing, Stroke = HousingEdge, StrokeThickness = 2 }, 20, 10);
        foreach (var (x, y) in new[] { (38.0, 28.0), (302.0, 28.0), (38.0, 292.0), (302.0, 292.0) })
            Add(new Ellipse { Width = 26, Height = 26, Fill = Brushes.Transparent, Stroke = HousingEdge, StrokeThickness = 3 }, x - 13, y - 13);

        // dissipador entre os conectores
        for (double y = 120; y <= 262; y += 9)
            Children.Add(new Line { X1 = 162, Y1 = y, X2 = 200, Y2 = y, Stroke = Fin, StrokeThickness = 4 });

        DrawConnector(52, 76, 98, LeftCols, LeftRow0, LeftRows);
        DrawConnector(207, 56, 90, RightCols, RightRow0, RightRows);
        // números de início/fim de coluna, como estão gravados no plástico
        for (int c = 0; c < 4; c++)
        {
            ColNumber((1 + 16 * c).ToString(), LeftCols[c], LeftRow0 - 17);
            ColNumber((16 + 16 * c).ToString(), LeftCols[c], LeftRow0 + 15 * Pitch + 5);
            ColNumber((1 + 16 * (3 - c)).ToString(), RightCols[c], RightRow0 + 15 * Pitch + 5);
            ColNumber((16 + 16 * (3 - c)).ToString(), RightCols[c], RightRow0 - 17);
        }

        // etiqueta
        Add(new Rectangle { Width = 96, Height = 24, Fill = Frozen(0x5A, 0x5F, 0x66), Stroke = HousingEdge }, 204, 276);
        Add(new TextBlock { Text = T("etiqueta", "label"), Foreground = Dim, FontSize = 10 }, 234, 280);
        Add(new TextBlock { Text = T("conector esquerdo", "left connector"), Foreground = Dim, FontSize = 10 }, 60, 54);
        Add(new TextBlock { Text = T("conector direito", "right connector"), Foreground = Dim, FontSize = 10 }, 214, 34);

        if (_model == EcuModel.Me731Me31)
        {
            Pins(Red, R(15), R(16), R(32), R(47), R(48));
            Label(Red, "+12V: 15, 16, 32, 47, 48", R(16), 40);
            Pins(Green, R(53));
            Label(Green, "K-line: 53", R(53), 120);
            Pins(Gnd, R(20));
            Label(GndEdge, T("Boot: 20 → massa", "Boot: 20 → ground"), R(20), 160);
            Pins(Gnd, R(49));
            Label(GndEdge, T("Massa: 49", "Ground: 49"), R(49), 200);
        }
        else
        {
            Pins(Red, R(17), R(18), R(33), R(51));
            Label(Red, "+12V: 17, 18, 33, 51", R(17), 236);
            Pins(Green, R(2));
            Label(Green, "K-line: 2", R(2), 156);
            Pins(Gnd, R(5));
            Label(GndEdge, T("Massa: 5", "Ground: 5"), R(5), 116);
            Pins(Gnd, L(20));
            Label(GndEdge, T("Boot: 20 → massa (conector esquerdo)", "Boot: 20 → ground (left connector)"), L(20), 70);
            Add(new Ellipse { Width = 34, Height = 34, Stroke = GndEdge, StrokeThickness = 3, Fill = Brushes.Transparent }, 302 - 17, 292 - 17);
            Label(GndEdge, T("Massa: caixa", "Ground: case"), (302, 292), 290);
        }

        double ly = 326;
        LegendDot(Red, "+12V", 345, ly);
        LegendDot(Gnd, T("massa", "ground"), 405, ly);
        LegendDot(Green, "K-line", 475, ly);
    }

    /// <summary>Conector: casca com patilhas em cima e em baixo, inserto escuro e pinos retangulares em dois blocos.</summary>
    private void DrawConnector(double x, double y, double w, double[] cols, double row0, int rows)
    {
        double h = (rows - 1) * Pitch + 52;
        // patilhas de encaixe
        Add(new Rectangle { Width = w * 0.42, Height = 10, RadiusX = 3, RadiusY = 3, Fill = Shell, Stroke = ShellEdge }, x + w * 0.29, y - 7);
        Add(new Rectangle { Width = w * 0.42, Height = 10, RadiusX = 3, RadiusY = 3, Fill = Shell, Stroke = ShellEdge }, x + w * 0.29, y + h - 3);
        // casca e inserto
        Add(new Rectangle { Width = w, Height = h, RadiusX = 8, RadiusY = 8, Fill = Shell, Stroke = ShellEdge, StrokeThickness = 2 }, x, y);
        Add(new Rectangle { Width = w - 14, Height = h - 14, RadiusX = 4, RadiusY = 4, Fill = Insert }, x + 7, y + 7);
        // separador entre os dois blocos de pinos
        double mid = (cols[1] + cols[2]) / 2;
        Children.Add(new Line { X1 = mid, Y1 = y + 12, X2 = mid, Y2 = y + h - 12, Stroke = Shell, StrokeThickness = 3 });
        // pinos
        foreach (double cx in cols)
            for (int r = 0; r < rows; r++)
                Add(new Rectangle { Width = 7, Height = 5, RadiusX = 1, RadiusY = 1, Fill = PinBrass }, cx - 3.5, row0 + r * Pitch - 2.5);
    }

    private void ColNumber(string text, double x, double y)
        => Add(new TextBlock { Text = text, Foreground = Dim, FontSize = 8, Width = 16, TextAlignment = TextAlignment.Center }, x - 8, y);

    private void Pins(Brush fill, params (double X, double Y)[] at)
    {
        foreach (var (x, y) in at)
            Add(new Rectangle { Width = 11, Height = 9, RadiusX = 2, RadiusY = 2, Fill = fill, Stroke = fill == Gnd ? GndEdge : Brushes.White, StrokeThickness = 1.5 }, x - 5.5, y - 4.5);
    }

    /// <summary>Linha do pino até ao texto, à direita da centralina.</summary>
    private void Label(Brush color, string text, (double X, double Y) from, double labelY)
    {
        const double tx = 345;
        Children.Add(new Polyline
        {
            Points = new PointCollection { new(from.X + 6, from.Y), new(328, labelY + 9), new(tx - 4, labelY + 9) },
            Stroke = color == Gnd ? GndEdge : color, StrokeThickness = 1.2, Opacity = 0.85,
        });
        Add(new TextBlock { Text = text, Foreground = color == GndEdge ? Text : color, FontSize = 13, FontWeight = FontWeights.SemiBold }, tx, labelY);
    }

    private void LegendDot(Brush fill, string text, double x, double y)
    {
        Add(new Rectangle { Width = 11, Height = 9, RadiusX = 2, RadiusY = 2, Fill = fill, Stroke = fill == Gnd ? GndEdge : Brushes.White, StrokeThickness = 1 }, x, y + 3);
        Add(new TextBlock { Text = text, Foreground = Dim, FontSize = 11 }, x + 15, y);
    }

    private void Add(UIElement e, double x, double y)
    {
        SetLeft(e, x); SetTop(e, y);
        Children.Add(e);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
