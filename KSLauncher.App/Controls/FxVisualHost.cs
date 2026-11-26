using System.Windows;
using System.Windows.Media;

namespace KSLauncher.App;

public sealed class FxVisualHost : FrameworkElement
{
    private readonly List<FxParticle> _particles = new();
    private readonly Random _rng = new();
    private bool _isRendering;
    private Pen? _cachedSinglePen;
    private Brush? _cachedSingleBrush;
    private Color _lastColor1;
    private Color _lastColor2;
    private bool _lastGradient;

    public string EffectType { get; private set; } = "none";
    public int ParticleCount { get; private set; } = 60;
    public double SpeedFactor { get; private set; } = 1.0;
    public Color Color1 { get; private set; } = Color.FromRgb(147, 51, 234);
    public Color Color2 { get; private set; } = Color.FromRgb(6, 182, 212);
    public bool IsGradient { get; private set; } = true;

    private sealed class FxParticle
    {
        public double X;
        public double Y;
        public double Speed;
        public double Length;
        public double Size;
        public double Alpha;
        public double Phase;
        public double ColorRatio; // 0.0 = Color1, 1.0 = Color2
    }

    public FxVisualHost()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => StartLoop();
        Unloaded += (_, _) => StopLoop();
        SizeChanged += (_, _) => ResetParticles();
    }

    public void Configure(string effectType, int particleCount, double speedFactor, Color color1, Color color2, bool isGradient)
    {
        EffectType = effectType;
        ParticleCount = Math.Clamp(particleCount, 15, 200);
        SpeedFactor = Math.Clamp(speedFactor, 0.3, 3.0);
        Color1 = color1;
        Color2 = color2;
        IsGradient = isGradient;

        InvalidateBrushes();
        ResetParticles();

        if (EffectType == "none")
        {
            StopLoop();
            InvalidateVisual();
        }
        else
        {
            StartLoop();
        }
    }

    public void UpdateColors(Color color1, Color color2, bool isGradient)
    {
        Color1 = color1;
        Color2 = color2;
        IsGradient = isGradient;
        InvalidateBrushes();
    }

    private void InvalidateBrushes()
    {
        _lastColor1 = Color1;
        _lastColor2 = Color2;
        _lastGradient = IsGradient;

        _cachedSingleBrush = new SolidColorBrush(Color1);
        _cachedSingleBrush.Freeze();
        _cachedSinglePen = new Pen(_cachedSingleBrush, 1.5);
        _cachedSinglePen.Freeze();
    }

    private void StartLoop()
    {
        if (_isRendering || EffectType == "none") return;
        CompositionTarget.Rendering += OnCompositionRendering;
        _isRendering = true;
    }

    private void StopLoop()
    {
        if (!_isRendering) return;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _isRendering = false;
    }

    private void ResetParticles()
    {
        _particles.Clear();
        double w = Math.Max(ActualWidth, 800);
        double h = Math.Max(ActualHeight, 600);

        for (int i = 0; i < ParticleCount; i++)
        {
            _particles.Add(CreateRandomParticle(w, h, true));
        }
    }

    private FxParticle CreateRandomParticle(double w, double h, bool randomY = false)
    {
        return new FxParticle
        {
            X = _rng.NextDouble() * w,
            Y = randomY ? _rng.NextDouble() * h : -25 - _rng.NextDouble() * 50,
            Speed = 4.0 + _rng.NextDouble() * 7.0,
            Length = 16.0 + _rng.NextDouble() * 26.0,
            Size = 2.0 + _rng.NextDouble() * 3.5,
            Alpha = 0.25 + _rng.NextDouble() * 0.65,
            Phase = _rng.NextDouble() * Math.PI * 2,
            ColorRatio = _rng.NextDouble()
        };
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (EffectType == "none" || ActualWidth <= 0 || ActualHeight <= 0) return;

        double w = ActualWidth;
        double h = ActualHeight;

        foreach (var p in _particles)
        {
            p.Phase += 0.035;

            switch (EffectType)
            {
                case "rain":
                    // Дождь падает с небольшим наклоном под углом
                    p.Y += p.Speed * 2.2 * SpeedFactor;
                    p.X += (p.Speed * 0.35) * SpeedFactor;
                    if (p.Y > h + p.Length)
                    {
                        p.Y = -p.Length - _rng.NextDouble() * 20;
                        p.X = _rng.NextDouble() * (w + 100) - 50;
                    }
                    if (p.X > w + 50) p.X = -30;
                    break;

                case "snow":
                    // Снежинки плавно покачиваются из стороны в сторону
                    p.Y += (p.Speed * 0.45 + 0.8) * SpeedFactor;
                    p.X += Math.Sin(p.Phase) * 0.9 * SpeedFactor;
                    if (p.Y > h + 10)
                    {
                        p.Y = -15;
                        p.X = _rng.NextDouble() * w;
                    }
                    break;

                case "sparks":
                    // Искры плавно поднимаются снизу вверх
                    p.Y -= (p.Speed * 0.6 + 0.5) * SpeedFactor;
                    p.X += Math.Cos(p.Phase) * 0.6 * SpeedFactor;
                    p.Alpha = 0.2 + 0.6 * Math.Abs(Math.Sin(p.Phase));
                    if (p.Y < -20)
                    {
                        p.Y = h + 10;
                        p.X = _rng.NextDouble() * w;
                    }
                    break;

                case "matrix":
                    // Матричные вертикальные потоки
                    p.Y += (p.Speed * 1.8 + 2.0) * SpeedFactor;
                    if (p.Y > h + p.Length)
                    {
                        p.Y = -p.Length;
                        p.X = Math.Floor(_rng.NextDouble() * (w / 18)) * 18;
                    }
                    break;
            }
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (EffectType == "none" || ActualWidth <= 0 || ActualHeight <= 0) return;

        double w = ActualWidth;
        double h = ActualHeight;

        // Если количество частиц изменилось
        if (_particles.Count != ParticleCount)
        {
            ResetParticles();
        }

        foreach (var p in _particles)
        {
            Brush particleBrush;
            Pen particlePen;

            if (IsGradient)
            {
                // Плавное интерполирование цвета между Color1 и Color2
                var blended = BlendColor(Color1, Color2, p.ColorRatio, (byte)(p.Alpha * 255));
                var solid = new SolidColorBrush(blended);
                solid.Freeze();
                particleBrush = solid;
                particlePen = new Pen(solid, EffectType == "rain" ? 1.6 : 1.2);
                particlePen.Freeze();
            }
            else
            {
                var col = Color.FromArgb((byte)(p.Alpha * 255), Color1.R, Color1.G, Color1.B);
                var solid = new SolidColorBrush(col);
                solid.Freeze();
                particleBrush = solid;
                particlePen = new Pen(solid, EffectType == "rain" ? 1.6 : 1.2);
                particlePen.Freeze();
            }

            switch (EffectType)
            {
                case "rain":
                    // Линия капли дождя под углом с градиентным хвостом
                    if (IsGradient)
                    {
                        // Капли дождя: сверху Color1 переходит в Color2 снизу!
                        var rainGrad = new LinearGradientBrush(
                            Color.FromArgb((byte)(p.Alpha * 70), Color1.R, Color1.G, Color1.B),
                            Color.FromArgb((byte)(p.Alpha * 255), Color2.R, Color2.G, Color2.B),
                            new Point(0, 0), new Point(0, 1));
                        rainGrad.Freeze();
                        var rainPen = new Pen(rainGrad, 1.6);
                        rainPen.Freeze();
                        dc.DrawLine(rainPen, new Point(p.X, p.Y), new Point(p.X + p.Length * 0.18, p.Y + p.Length));
                    }
                    else
                    {
                        dc.DrawLine(particlePen, new Point(p.X, p.Y), new Point(p.X + p.Length * 0.18, p.Y + p.Length));
                    }
                    break;

                case "snow":
                    // Мягкая круглая снежинка
                    dc.DrawEllipse(particleBrush, null, new Point(p.X, p.Y), p.Size, p.Size);
                    break;

                case "sparks":
                    // Искры с легким ореолом
                    dc.DrawEllipse(particleBrush, null, new Point(p.X, p.Y), p.Size * 0.8, p.Size * 0.8);
                    break;

                case "matrix":
                    // Вертикальная черта матрицы
                    dc.DrawLine(particlePen, new Point(p.X, p.Y), new Point(p.X, p.Y + p.Length * 0.7));
                    break;
            }
        }
    }

    private static Color BlendColor(Color c1, Color c2, double ratio, byte alpha)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        byte r = (byte)Math.Round(c1.R * (1 - ratio) + c2.R * ratio);
        byte g = (byte)Math.Round(c1.G * (1 - ratio) + c2.G * ratio);
        byte b = (byte)Math.Round(c1.B * (1 - ratio) + c2.B * ratio);
        return Color.FromArgb(alpha, r, g, b);
    }
}
