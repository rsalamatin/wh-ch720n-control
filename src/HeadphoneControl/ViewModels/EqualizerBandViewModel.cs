using CommunityToolkit.Mvvm.ComponentModel;

namespace HeadphoneControl.ViewModels;

public sealed partial class EqualizerBandViewModel : ObservableObject
{
    public EqualizerBandViewModel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        Label = label;
    }

    public string Label { get; }

    public int Minimum => -10;

    public int Maximum => 10;

    [ObservableProperty]
    public partial int Value { get; set; }
}
