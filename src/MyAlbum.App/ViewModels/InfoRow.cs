namespace MyAlbum_App.ViewModels;

/// <summary>A single icon+label+value row rendered in the right panel's info section.</summary>
public sealed record InfoRow(string Glyph, string Label, string Value);
