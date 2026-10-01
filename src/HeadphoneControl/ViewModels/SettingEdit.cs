using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.ViewModels;

// The value is captured when the user makes the edit, so a device snapshot arriving before the send can't change it.
public sealed record SettingEdit<T>(T Value, EditPacing Pacing);
