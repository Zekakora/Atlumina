namespace MyAlbum_App.ViewModels;

/// <summary>An entry in the minimum-rating filter combo.</summary>
public sealed class RatingOption
{
    public string Label { get; }
    public int Value { get; }

    public RatingOption(string label, int value)
    {
        Label = label;
        Value = value;
    }
}
