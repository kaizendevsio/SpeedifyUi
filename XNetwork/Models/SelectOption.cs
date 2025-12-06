namespace XNetwork.Models;

/// <summary>
/// Represents an option in a dropdown selector.
/// </summary>
public class SelectOption
{
    /// <summary>
    /// The value to submit.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// The display label.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// Creates a new SelectOption.
    /// </summary>
    public SelectOption() { }

    /// <summary>
    /// Creates a new SelectOption with value and label.
    /// </summary>
    /// <param name="value">The value</param>
    /// <param name="label">The display label</param>
    public SelectOption(string value, string label)
    {
        Value = value;
        Label = label;
    }
}