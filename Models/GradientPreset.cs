using System.Windows.Media;

namespace YorteImpact.Models;

/// <summary>A named two-stop linear gradient offered by the color picker.</summary>
public sealed record GradientPreset(string Name, Color Start, Color End);
