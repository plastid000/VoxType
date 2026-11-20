using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using VoxType.Services;
using VoxType.UI;

namespace VoxType;

/// <summary>
/// Interaction logic for App.xaml.
/// Wires the tray icon, hotkeys, floating pill, audio capture, Whisper
/// inference and text injection together.
/// </summary>
public partial class App : System.Windows.Application
{
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private HotkeyService? _hotkeyService;
    private FloatingPillWindow? _floatingPill;
    private AudioService? _audioService;
    private WhisperService? _whisperService;
    private GeminiTranscriptionService? _geminiService;
    private TextInjectionService? _textInjectionService;

    private VoxLanguage _activeLanguage = VoxLanguage.EN;
    private bool _isPillVisible = true;
    private volatile bool _serviceReady = false;

    // True from the moment a PTT/manual stop begins until its finally block
    // has posted the final "Idle". Used to swallow stale "Idle" events from
    // the audio layer (a late RecordingStopped handler racing the 250 ms
    // NAudio flush timeout) while Whisper is still transcribing/pasting.
    private volatile bool _isSessionProcessing = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Tray-based app: no main window is shown by default.
        InitializeTrayIcon();
        InitializeServices();
        InitializeFloatingPill();

        // Start the whisper consumer and preload the default (EN) model
        // asynchronously so startup stays snappy.
        _ = InitializeModelAsync();
    }

    private async Task InitializeModelAsync()
    {
        _whisperService = new WhisperService();
        try
        {
            await _whisperService.InitializeAsync();
            _serviceReady = true;

            // Preload the default model so first dictation is instant and a
            // missing ggml-en.bin is reported up front.
            if (!await _whisperService.PrepareModelAsync(_activeLanguage))
            {
                ShowModelError(_activeLanguage,
                    _whisperService.GetModelError(_activeLanguage)
                        ?? new InvalidOperationException("unknown error"));
            }
        }
        catch (Exception ex)
        {
            _serviceReady = false;
            System.Diagnostics.Debug.WriteLine($"[VoxType] Whisper service failed to start: {ex.Message}");
            ShowModelError(_activeLanguage, ex);
        }
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "VoxType"
        };

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("Show Pill", null, OnShowPill);
        _contextMenu.Items.Add("Hide Pill", null, OnHidePill);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Exit", null, OnExit);

        _notifyIcon.ContextMenuStrip = _contextMenu;
        _notifyIcon.MouseClick += OnTrayIconClicked;
    }

    private void InitializeServices()
    {
        _audioService = new AudioService();
        _audioService.StatusChanged += OnAudioStatusChanged;
        _audioService.ErrorOccurred += OnAudioError;

        _textInjectionService = new TextInjectionService();

        // Online BN transcription (hybrid mode). IsConfigured is false when no
        // key is found; TranscribeAsync also returns null offline/on error, so
        // the local Whisper path is always the safety net.
        _geminiService = new GeminiTranscriptionService();
    }

    private void InitializeFloatingPill()
    {
        _floatingPill = new FloatingPillWindow();
        _floatingPill.StartStopButtonClick += OnPillStartStopClicked;
        _floatingPill.LanguageToggleClicked += OnPillLanguageToggleClicked;
        _floatingPill.PillHidden += OnPillHidden;

        // Global low-level keyboard hook (Win + Right Alt / Right Ctrl).
        // Installed on the WPF UI thread so the hook is serviced by the dispatcher.
        _hotkeyService = new HotkeyService(_floatingPill.Dispatcher);
        _hotkeyService.PttStarted += OnPttStarted;
        _hotkeyService.PttStopped += OnPttStopped;

        try
        {
            _hotkeyService.Register();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoxType] Failed to install keyboard hook: {ex.Message}");
        }

        // Handle window closing - hide instead of close.
        _floatingPill.Closing += (s, e) =>
        {
            e.Cancel = true;
            _floatingPill.Hide();
            _isPillVisible = false;
        };

        _floatingPill.Show();
    }

    /// <summary>
    /// PTT started (Win+Right Alt held = BN, Win+Right Ctrl held = EN).
    /// Shows the pill, syncs the badge, opens the mic IMMEDIATELY (never wait
    /// on the model load, so the user's first words are never clipped), and
    /// kicks the model load off in the background in parallel.
    /// </summary>
    private void OnPttStarted(VoxLanguage lang)
    {
        StartSession(lang);
    }

    /// <summary>
    /// PTT stopped (the held key was released): transcribe the ENTIRE
    /// recorded buffer as a single utterance and paste it.
    /// </summary>
    private async void OnPttStopped(VoxLanguage lang)
    {
        await StopSessionAsync(lang);
    }

    /// <summary>
    /// Shared start path for PTT and the pill's manual Start/Stop button.
    /// </summary>
    private void StartSession(VoxLanguage lang)
    {
        // Switch active language and sync the pill badge.
        _activeLanguage = lang;
        _floatingPill?.SetLanguage(LanguageToString(lang));

        if (!_isPillVisible)
        {
            _floatingPill?.Show();
            _isPillVisible = true;
        }

        _floatingPill?.SetListening(true);
        SetPillStatus("Listening");

        // Open the mic right away - do NOT wait for the model so the first
        // words are never clipped.
        _audioService?.StartRecording();

        // In parallel: load the requested model in the background when the
        // active model differs. StopSessionAsync awaits it before use.
        // BN hybrid mode: when a Gemini key IS configured, skip the local BN
        // preload - the online path handles transcription and the ggml-bn
        // model stays out of RAM. StopSessionAsync loads it on demand only if
        // Gemini fails (offline / no key / API error).
        bool useLocalModel = lang != VoxLanguage.BN || _geminiService?.IsConfigured != true;
        if (useLocalModel && _whisperService is { } ws && _serviceReady && ws.LoadedLanguage != lang)
        {
            _ = ws.PrepareModelAsync(lang);
        }
    }

    /// <summary>
    /// Shared stop path for PTT and the pill's manual Start/Stop button:
    /// stop the mic, transcribe the full buffer in one call, inject the text.
    /// The pill shows "Processing" from the very first line until the paste
    /// actually finishes - the ONLY place it returns to Idle is the finally
    /// block below.
    /// </summary>
    private async Task StopSessionAsync(VoxLanguage lang)
    {
        // Set Processing IMMEDIATELY - before stopping the mic, waiting for
        // the model, transcribing, or pasting - so nothing can flash the pill
        // back to Idle while Whisper is still working. The flag marks this
        // window so stale audio-layer "Idle" events (a spontaneous-stop race
        // in RecordingStopped) are ignored until the session truly ends.
        _isSessionProcessing = true;
        SetPillStatus("Processing...");

        try
        {
            // Stop the mic and wait for NAudio to flush the final buffer.
            float[] audio;
            if (_audioService is { } audioSvc)
            {
                audio = await audioSvc.StopRecordingAndGetAudioAsync();
            }
            else
            {
                audio = Array.Empty<float>();
            }

            _floatingPill?.SetListening(false);

            // Empty / too short (accidental tap) -> Idle (via finally).
            if (audio.Length == 0) return;

            WhisperService? ws = _whisperService;
            string? text = null;

            // BN hybrid mode: try the online Gemini API FIRST. It returns
            // null when no key is configured, the machine is offline, or the
            // request fails/times out - that null is the fallback signal.
            // EN stays 100% offline/local.
            if (lang == VoxLanguage.BN && _geminiService is { } gemini && gemini.IsConfigured)
            {
                text = await gemini.TranscribeAsync(audio);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    System.Diagnostics.Debug.WriteLine("[VoxType] BN transcribed online via Gemini.");
                }
                else
                {
                    text = null; // offline / no key / API error -> local Whisper
                }
            }

            // Local Whisper path: always for EN, automatic fallback for BN.
            if (text == null)
            {
                // Service not ready -> Idle (via finally).
                if (ws is null || !_serviceReady) return;

                // Wait for any in-flight PrepareModelAsync from StartSession to
                // finish. PrepareModelAsync is a no-op when the model is already
                // loaded and serializes on an internal lock, so awaiting it here
                // both waits for the background load and guarantees the right
                // model before transcription.
                bool ok = await ws.PrepareModelAsync(lang);
                if (!ok)
                {
                    // Error is surfaced via balloon; pill goes Idle via finally.
                    ShowModelError(lang, ws.GetModelError(lang)
                        ?? new InvalidOperationException("unknown error"));
                    return;
                }

                // Transcribe the ENTIRE buffer as a single utterance.
                text = await ws.TranscribeAsync(audio, lang);
            }

            // Ensure Alt/Ctrl is physically released and focus/clipboard are
            // settled before we paste.
            await Task.Delay(50);

            if (!string.IsNullOrWhiteSpace(text))
            {
                // The pill stays "Processing" until this paste completes.
                if (await _textInjectionService!.InjectTextAsync(text!))
                {
                    System.Diagnostics.Debug.WriteLine($"[VoxType] Injected: \"{text}\"");
                }
            }
            else if (ws is not null && ws.GetModelError(lang) is { } error)
            {
                ShowModelError(lang, error);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoxType] Session transcription failed: {ex.Message}");
        }
        finally
        {
            // The ONLY Idle transition: reached after the paste completes,
            // after a model error was surfaced, or when there was no audio.
            // Release the flag only AFTER posting Idle so a very late stale
            // "Idle" from the audio layer is still swallowed.
            SetPillStatus("Idle");
            _isSessionProcessing = false;
        }
    }

    private void OnPillStartStopClicked()
    {
        if (_audioService?.IsRecording == true)
        {
            _ = StopSessionAsync(_activeLanguage);
        }
        else
        {
            StartSession(_activeLanguage);
        }
    }

    // Clicking the badge cycles EN <-> BN and sets the language used by the
    // next PTT / manual session (no recording starts on its own anymore).
    private void OnPillLanguageToggleClicked()
    {
        _activeLanguage = WhisperService.MapLanguage(_floatingPill?.CurrentLanguage);
        _floatingPill?.SetLanguage(LanguageToString(_activeLanguage));
    }

    private void OnPillHidden()
    {
        _isPillVisible = false;
        // Stop microphone capture if it is still running and process it.
        if (_audioService?.IsRecording == true)
        {
            _ = StopSessionAsync(_activeLanguage);
        }
    }

    private void OnAudioStatusChanged(string status)
    {
        // While a stop session is in flight, a late RecordingStopped handler
        // (NAudio flush took longer than the 250 ms wait) can still surface
        // "Idle". The session owns the pill status until its finally block, so
        // swallow that stale Idle. "Listening" never arrives mid-session.
        if (_isSessionProcessing && status == "Idle") return;
        SetPillStatus(status);
    }

    private void OnAudioError(string error)
    {
        System.Diagnostics.Debug.WriteLine($"[VoxType] Audio error: {error}");
    }

    private void SetPillStatus(string status)
    {
        if (_floatingPill is { } pill)
        {
            try
            {
                // All pill status updates are marshaled to the pill's dispatcher
                // via InvokeAsync (non-blocking, safe from any thread, and a
                // shutting-down dispatcher simply drops the queued work).
                pill.Dispatcher.InvokeAsync(() => pill.SetStatus(status));
            }
            catch (Exception)
            {
                // Dispatcher may be shutting down; ignore.
            }
        }
    }

    /// <summary>
    /// Shows an error on the pill + a tray balloon. The "model missing"
    /// wording is only used for FileNotFoundException; any other failure
    /// shows the real error (title "VoxType - Error" + actual message).
    /// </summary>
    private void ShowModelError(VoxLanguage lang, Exception error)
    {
        string label = LanguageToString(lang);
        bool missing = error is FileNotFoundException;
        string title = missing ? $"VoxType - {label} model missing" : "VoxType - Error";
        System.Diagnostics.Debug.WriteLine($"[VoxType] Model error ({label}): {error}");

        try
        {
            _notifyIcon?.ShowBalloonTip(4000, title, error.Message, ToolTipIcon.Error);
        }
        catch
        {
            // Ignore balloon failures.
        }

        SetPillStatus(missing ? $"{label} model missing" : "Error");
    }

    private static string LanguageToString(VoxLanguage lang) => lang == VoxLanguage.BN ? "BN" : "EN";

    private void OnShowPill(object? sender, EventArgs e)
    {
        _floatingPill?.Show();
        _isPillVisible = true;
    }

    private void OnHidePill(object? sender, EventArgs e)
    {
        _floatingPill?.Hide();
        _isPillVisible = false;
    }

    private void OnTrayIconClicked(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_isPillVisible)
            {
                _floatingPill?.Hide();
                _isPillVisible = false;
            }
            else
            {
                _floatingPill?.Show();
                _isPillVisible = true;
            }
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose all resources: stop audio first so no chunks are queued
        // while the model is being torn down.
        _hotkeyService?.Dispose();
        _audioService?.Dispose();
        _whisperService?.Dispose();
        _geminiService?.Dispose();

        _notifyIcon?.Dispose();
        _contextMenu?.Dispose();

        base.OnExit(e);
    }
}