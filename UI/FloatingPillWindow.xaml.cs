using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace VoxType.UI;

/// <summary>
/// Interaction logic for FloatingPillWindow.
/// A sleek, dark-mode, frameless, always-on-top floating pill window.
/// Language modes: EN, BN (clicks toggle).
/// </summary>
public partial class FloatingPillWindow : Window
{
    // Win32 API for WS_EX_NOACTIVATE
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EX_STYLE = -16;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    // Window state
    private bool _isListening = false;
    private string _currentLanguage = "EN";
    private string _currentStatus = "Idle";
    private readonly string[] _languages = { "EN", "BN" };

    public FloatingPillWindow()
    {
        InitializeComponent();
        InitializeWindow();
    }

    private void InitializeWindow()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        double pillWidth = 280;
        double pillHeight = 80;

        double left = (screenWidth - pillWidth) / 2;
        double top = screenHeight - pillHeight - 40;

        this.Left = left;
        this.Top = top;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EX_STYLE);
        exStyle |= WS_EX_NOACTIVATE;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EX_STYLE, exStyle);
    }

    // Drag from the root container. Buttons and the language toggle mark their
    // own MouseLeftButtonDown as handled, so only clicks on the pill chrome
    // reach here and start the modal drag.
    private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            this.DragMove();
        }
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggling goes through SetListening so the status transition rules
        // (never clobber "Transcribing...") live in exactly one place.
        SetListening(!_isListening);
        StartStopButtonClick?.Invoke();
    }

    // Cycle EN <-> BN
    private void LanguageToggle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        int currentIndex = Array.IndexOf(_languages, _currentLanguage);
        int nextIndex = (currentIndex + 1) % _languages.Length;
        _currentLanguage = _languages[nextIndex];
        LanguageToggle.Text = _currentLanguage;
        LanguageToggleClicked?.Invoke();

        // Consume the event so the pill does not start dragging.
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Hide();
        PillHidden?.Invoke();
    }

    /// <summary>
    /// Sets the pill status text + indicator dot. Safe from any thread: the
    /// update is marshaled to the WPF dispatcher. All processing-family
    /// statuses ("Processing", "Processing...", "Transcribing...") are
    /// normalized to the clear "Transcribing..." label so a string mismatch
    /// can never silently fall back to Idle.
    /// </summary>
    public void SetStatus(string status)
    {
        string normalized = NormalizeStatus(status);
        RunOnUi(() =>
        {
            _currentStatus = normalized;
            StatusText.Text = normalized;
            UpdateIndicator(normalized);
        });
    }

    private static string NormalizeStatus(string status) => status switch
    {
        "Processing" => "Transcribing...",
        "Processing..." => "Transcribing...",
        "Transcribing" => "Transcribing...",
        "Transcribing..." => "Transcribing...",
        _ => status
    };

    /// <summary>
    /// Runs the action on the WPF dispatcher thread: inline when already on
    /// it, Dispatcher.InvokeAsync otherwise.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.InvokeAsync(action);
        }
    }

    public void SetListening(bool listening)
    {
        RunOnUi(() =>
        {
            _isListening = listening;
            StartStopButton.Content = listening ? "■" : "▶";

            if (listening)
            {
                SetStatus("Listening");
            }
            else if (_currentStatus == "Listening")
            {
                // Capture stopped and nothing else owns the status yet -> fall
                // back to Idle. When the caller has already moved us to
                // "Transcribing..." (PTT stop), the session owner returns the
                // pill to Idle only after transcription + paste finish, so this
                // must NOT clobber the processing status.
                SetStatus("Idle");
            }
        });
    }

    public void SetLanguage(string language)
    {
        RunOnUi(() =>
        {
            if (language is not null && language != _currentLanguage)
            {
                _currentLanguage = language;
                LanguageToggle.Text = language;
            }
        });
    }

    private void UpdateIndicator(string status)
    {
        System.Windows.Media.Brush brush;
        switch (status)
        {
            case "Listening":
                brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 200, 83));
                break;
            case "Transcribing...":
            case "Processing":
            case "Processing...":
                // Bright blue dot while Whisper is transcribing / pasting.
                brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 140, 255));
                break;
            case "Error":
            case "BN model missing":
            case "EN model missing":
            case "Model Missing":
                brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 69, 58)); // red
                break;
            case "Idle":
            default:
                brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 133));
                break;
        }
        StatusIndicator.Fill = brush;
    }

    public string CurrentLanguage => _currentLanguage;

    // Events for external handlers
    public event Action? StartStopButtonClick;
    public event Action? LanguageToggleClicked;
    public event Action? PillHidden;
}