using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HeadphoneControl.ViewModels;

namespace HeadphoneControl.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private DiagnosticsWindow? _diagnosticsWindow;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FitToWorkingArea();
        SizeChanged += (_, _) => FitToWorkingArea();
        ScalingChanged += (_, _) => FitToWorkingArea();
        LayoutUpdated += (_, _) => UpdateMinHeight();
    }

    // The window can't be made shorter than its content: the fixed rows plus the scroller content's natural height
    // (not its extent, which stretches to the viewport). Only content taller than the screen (MaxHeight) scrolls.
    private void UpdateMinHeight()
    {
        if (ContentScroller.Content is not Control content)
        {
            return;
        }

        var fixedRows = RootPanel.Bounds.Height - ContentScroller.Bounds.Height;
        var needed = Math.Ceiling(fixedRows + content.DesiredSize.Height);
        var minHeight = double.IsFinite(MaxHeight) ? Math.Min(needed, MaxHeight) : needed;
        if (minHeight > 0 && Math.Abs(minHeight - MinHeight) >= 1)
        {
            MinHeight = minHeight;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Resizing the window by hand turns SizeToContent off; the next big layout change turns it back on.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsNoiseSectionExpanded) or nameof(MainViewModel.IsSoundSectionExpanded)
            or nameof(MainViewModel.IsConnected) or nameof(MainViewModel.IsAlertVisible) or nameof(MainViewModel.IsAmbientMode))
        {
            SizeToContent = SizeToContent.Height;
        }
    }

    // Grow with the content but stay on screen: cap the client height to the working area minus the title bar, and
    // move the window up when growing would push its bottom edge past the taskbar.
    private void FitToWorkingArea()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen || FrameSize is not { } frame)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var titleBarHeight = Math.Max(0, frame.Height - ClientSize.Height);
        MaxHeight = (area.Height / scaling) - titleBarHeight;
        UpdateMinHeight();

        var bottom = Position.Y + (int)Math.Ceiling(frame.Height * scaling);
        if (bottom > area.Bottom)
        {
            Position = new PixelPoint(Position.X, Math.Max(area.Y, Position.Y - (bottom - area.Bottom)));
        }
    }

    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is not null)
        {
            if (_diagnosticsWindow.WindowState == WindowState.Minimized)
            {
                _diagnosticsWindow.WindowState = WindowState.Normal;
            }

            _diagnosticsWindow.Activate();
            return;
        }

        if (_viewModel is null)
        {
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow { DataContext = _viewModel.Diagnostics };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show(this);
    }
}
