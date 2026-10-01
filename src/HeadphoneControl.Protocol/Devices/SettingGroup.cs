namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// Settings that are written as one value. A newer edit of a group carries its complete value, so it replaces any
/// older edit of that group that has not been sent yet.
/// </summary>
public enum SettingGroup
{
    NoiseControl,

    /// <summary>The preset and the custom curve: both set the whole equalizer.</summary>
    Equalizer,

    Dsee,
}

public enum EditPacing
{
    /// <summary>Sent as soon as earlier operations finish, e.g. a click.</summary>
    Immediate,

    /// <summary>Sent only after the edit debounce, e.g. one step of a slider drag, so a burst sends only its last value.</summary>
    Debounced,
}
