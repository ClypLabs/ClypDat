namespace ClypDat.Core.Settings;

public sealed record TimedVideoEffect
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool Visible { get; init; } = true;
    public double Start { get; init; }
    public double End { get; init; } = 3;
    public double X { get; init; } = .1;
    public double Y { get; init; } = .75;
    public double Width { get; init; } = .8;
    public double Height { get; init; } = .2;
    public string Text { get; init; } = "Text";
    public string Font { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 48;
    public bool Bold { get; init; }
    public string Colour { get; init; } = "#FFFFFF";
    public double Outline { get; init; } = 2;
    public string Background { get; init; } = "#000000";
    public double BackgroundOpacity { get; init; }
    public string Alignment { get; init; } = "Center";
    public double Strength { get; init; } = 20;
    /// <summary>Blur outline inside the box: Rectangle, Rounded or Ellipse.</summary>
    public string Shape { get; init; } = "Rectangle";
}
