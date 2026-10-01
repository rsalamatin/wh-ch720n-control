namespace HeadphoneControl.Protocol.Devices;

/// <summary>Settings written as one value, so a newer edit of a group replaces an older one not yet sent.</summary>
public enum SettingGroup
{
    NoiseControl,

    /// <summary>Both the preset and the custom curve.</summary>
    Equalizer,

    Dsee,
}

public enum EditPacing
{
    Immediate,

    /// <summary>Sent after the edit debounce, so a burst (e.g. a slider drag) sends only its last value.</summary>
    Debounced,
}

public enum EditOutcome
{
    /// <summary>The device acknowledged the edit.</summary>
    Applied,

    /// <summary>A newer edit of the same group replaced this one, so nothing was sent.</summary>
    Superseded,
}
