using Shared.UI;

namespace FieldMap.Map;

public static class MapStyles
{
    /// <summary>The map style for the current interface theme.</summary>
    public static MapStyle Current => UiMode.Theme switch
    {
        UiTheme.Daylight => MapStyle.Daylight,
        UiTheme.Night => MapStyle.Night,
        _ => MapStyle.Standard
    };
}
