using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace VoxType.Services;

/// <summary>
/// Continuous Push-to-Talk audio capture. While recording, every 16 kHz mono
/// float sample is appended to a growing buffer; StopRecordingAndGetAudioAsync()
/// stops the mic (waiting for NAudio to flush the final capture buffer) and
/// returns the ENTIRE buffer as a single utterance. No VAD, no silence cutting,
/// no hard cut - the caller decides when to stop.
/// </summary>
public class AudioService : IDisposable
{
    private const int SampleRate = 16000;  // 16 kHz
    private const int BitsPerSample = 16;  // 16-bit
    private const int ChannelCount = 1;    // Mono

    // Safety guard: cap the buffer at 60 seconds so a stuck key can never
    // fill memory.
    private const int MaxSamples = SampleRate * 60; // 960,000 samples
    // Recordings shorter than this (0.1 s) are accidental taps -> ignored.
    private const int MinSamples = 1600;

    // Set by StopRecordingAndGetAudioAsync() while an intentional stop is in
    // progress; the RecordingStopped handler completes it once NAudio has
    // flushed the final DataAvailable buffer.
    private volatile TaskCompletionSource? _stoppedTcs;

    private readonly object _bufferLock = new();
    private readonly List<float> _samples = new();

    private WaveIn? _waveIn;
    private bool _isRecording = false;
    private bool _disposed = false;

    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Opens the mic and starts appending every sample to the buffer.
    /// The previous buffer contents (if any) are discarded.
    /// </summary>
    public void StartRecording()
    {
        if (_isRecording) return;

        lock (_bufferLock)
        {
            _samples.Clear();
        }

        _waveIn = new WaveIn
        {
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, ChannelCount),
            BufferMilliseconds = 50
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            _waveIn.StartRecording();
            _isRecording = true;
            StatusChanged?.Invoke("Listening");
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Failed to start recording: {ex.Message}");
            _isRecording = false;
            CleanupWaveIn();
        }
    }

    /// <summary>
    /// Stops the mic, waits (up to 250 ms) for NAudio to flush the final
    /// capture buffer, releases the device, and returns the ENTIRE recorded
    /// buffer as one float[] utterance. Returns an empty array when the
    /// recording is shorter than 0.1 s (accidental tap).
    /// </summary>
    public async Task<float[]> StopRecordingAndGetAudioAsync()
    {
        if (_isRecording)
        {
            _isRecording = false;

            if (_waveIn != null)
            {
                try
                {
                    // Completed by the RecordingStopped handler once NAudio has
                    // fully stopped (after raising the final DataAvailable).
                    _stoppedTcs = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    _waveIn.StopRecording();

                    // Wait (up to 250 ms) for the capture thread to stop so the
                    // last buffer is appended to _samples before we read it.
                    await Task.WhenAny(_stoppedTcs.Task, Task.Delay(250));
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke($"Failed to stop recording: {ex.Message}");
                }
                // Release the capture device NOW. NAudio keeps the device handle
                // open until Dispose, so skipping this leaked a device on every
                // Start/Stop cycle. (RecordingStopped cleanup is idempotent.)
                CleanupWaveIn();
            }
        }

        float[] result;
        lock (_bufferLock)
        {
            if (_samples.Count < MinSamples)
            {
                // Too short to be a real utterance -> ignore.
                _samples.Clear();
                result = Array.Empty<float>();
            }
            else
            {
                result = _samples.ToArray();
                _samples.Clear();
            }
        }
        _stoppedTcs = null;

        // Peak audio normalization
        if (result.Length > 0)
        {
            float maxAbs = 0f;
            foreach (float sample in result)
            {
                float abs = Math.Abs(sample);
                if (abs > maxAbs) maxAbs = abs;
            }

            if (maxAbs > 0.01f && maxAbs < 0.9f)
            {
                float scale = 0.9f / maxAbs;
                for (int i = 0; i < result.Length; i++)
                {
                    float normalized = result[i] * scale;
                    if (normalized > 1f) normalized = 1f;
                    if (normalized < -1f) normalized = -1f;
                    result[i] = normalized;
                }
            }
        }

        double durationMs = result.Length * 1000.0 / SampleRate;
        System.Diagnostics.Debug.WriteLine(
            $"[VoxType] Recording stopped: {result.Length} samples ({durationMs:F0} ms)");

        return result;
    }

    public bool IsRecording => _isRecording;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Guard on _waveIn (NOT _isRecording) so the FINAL flush buffer that
        // NAudio raises after StopRecording() is still appended - _isRecording
        // is already false by the time that buffer arrives.
        if (_waveIn == null) return;

        try
        {
            int bytesPerSample = _waveIn.WaveFormat.BitsPerSample / 8;
            int sampleCount = e.BytesRecorded / bytesPerSample;
            if (sampleCount <= 0) return;

            // Append every sample - no VAD, no trimming. The 60 s safety cap
            // prevents a stuck key from filling memory.
            lock (_bufferLock)
            {
                for (int i = 0; i < sampleCount && _samples.Count < MaxSamples; i++)
                {
                    _samples.Add(BitConverter.ToInt16(e.Buffer, i * bytesPerSample) / 32768.0f);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Error processing audio: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            ErrorOccurred?.Invoke($"Recording stopped with error: {e.Exception.Message}");
        }

        // An intentional stop (StopRecordingAndGetAudioAsync) set _stoppedTcs;
        // complete it so the caller can proceed. A spontaneous stop leaves it
        // null.
        bool intentionalStop = _stoppedTcs != null;
        _stoppedTcs?.TrySetResult();

        // NAudio can stop capture spontaneously (driver hiccup, device reset).
        // Reset state so a later StartRecording() works instead of
        // early-returning with the mic permanently dead.
        _isRecording = false;
        CleanupWaveIn();

        // Only surface Idle on a spontaneous stop. On an intentional stop the
        // caller (App) owns the pill status and is already showing "Processing",
        // so firing Idle here would briefly clobber it.
        if (!intentionalStop)
        {
            StatusChanged?.Invoke("Idle");
        }
    }

    private void CleanupWaveIn()
    {
        if (_waveIn == null) return;

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _isRecording = false;
        CleanupWaveIn();
        _disposed = true;
    }
}