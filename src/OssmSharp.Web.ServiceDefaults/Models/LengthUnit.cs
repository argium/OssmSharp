namespace OssmSharp.Web.ServiceDefaults.Models;

/// <summary>
/// Identifies the unit used to enter and display physical lengths in the user interface.
/// Configured lengths are always expressed in centimetres regardless of this choice.
/// </summary>
public enum LengthUnit
{
    /// <summary>Lengths are entered and displayed in centimetres.</summary>
    Centimetres,

    /// <summary>Lengths are entered and displayed in inches.</summary>
    Inches,
}
