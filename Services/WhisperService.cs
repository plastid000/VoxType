using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Whisper.net;

namespace VoxType.Services;

public enum VoxLanguage
{
    EN,
    BN
}

/// <summary>
/// Dual-model offline speech recognition (EN + BN ggml models).
/// Only ONE model lives in RAM at a time: the requested model is loaded
/// on-demand and the previous factory/processor are disposed before the
/// new one is created. A SemaphoreSlim serializes model swaps and
/// inference so concurrent calls wait cleanly.
/// </summary>
public class WhisperService : IDisposable
{
    private const string ModelFolderName = "models_bin";
    private const string EnglishModelFile = "ggml-en.bin";
    private const string BengaliModelFile = "ggml-bn.bin";
    private const string BengaliPrompt = "আসসাজামু আজাইকুম, আমি রাহাত, র্ংপুর থেকে দেখাছি। এমআর टেক বিডি, ভক্সटাইপ, জেমিনি এব্ং কোডি্ং নিযে কোথা বজিচই।";
    private const string EnglishPrompt = "Hello, I am Rahat from MR Tech BD, Rangpur. I use Google Gemini, VoxType, Cline, VS Code, GitHub, C#, WPF, Python, and Whisper.";

    private static readonly int ThreadCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly Channel<ChunkRequest> _channel;
    private readonly ConcurrentDictionary<VoxLanguage, Exception> _modelErrors = new();
    private readonly CancellationTokenSource _cts = new();

    private WhisperFactory? _factory;      // guarded by _modelLock
    private WhisperProcessor? _processor;  // guarded by _modelLock
    private VoxLanguage? _loadedLanguage;  // guarded by _stateLock
    private CancellationTokenSource _inferenceCts = new(); // cancelled on language switch / stop
    private long _generation;              // bumped on cancel; stale chunks are skipped
    private Task? _consumerTask;
    private volatile bool _initialized;
    private bool _disposed;

    private sealed record ChunkRequest(float[] Samples, VoxLanguage Language, TaskCompletionSource<string> Completion, long Generation);

    public WhisperService() => _channel = Channel.CreateUnbounded<ChunkRequest>();

    public bool IsReady => _initialized;

    public VoxLanguage? LoadedLanguage
    {
        get { lock (_stateLock) return _loadedLanguage; }
    }

    public static VoxLanguage MapLanguage(string? pillLanguage) =>
        string.Equals(pillLanguage?.Trim(), "BN", StringComparison.OrdinalIgnoreCase) ? VoxLanguage.BN : VoxLanguage.EN;

    /// <summary>
    /// Resolves a model file from the output directory or the repo root (dev fallback).
    /// Returns null when the file is missing.
    /// </summary>
    public static string? ResolveModelPath(VoxLanguage language)
    {
        string file = language == VoxLanguage.BN ? BengaliModelFile : EnglishModelFile;
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, ModelFolderName, file) };
        AddAncestorCandidates(AppContext.BaseDirectory, file, candidates);
        AddAncestorCandidates(Directory.GetCurrentDirectory(), file, candidates);

        foreach (string path in candidates)
        {
            try
            {
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            catch
            {
                // Skip invalid candidate paths.
            }
        }
        return null;
    }

    private static void AddAncestorCandidates(string start, string file, List<string> candidates)
    {
        var dir = new DirectoryInfo(start);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, ModelFolderName, file));
            dir = dir.Parent;
        }
    }

    /// <summary>
    /// Starts the sequential inference consumer. Models are still loaded on demand.
    /// </summary>
    public Task InitializeAsync()
    {
        return Task.Run(() =>
        {
            if (_initialized) return;
            _initialized = true;
            _consumerTask = Task.Run(ConsumeLoopAsync);
            System.Diagnostics.Debug.WriteLine("[VoxType] Whisper service started (models load on demand).");
        });
    }

    /// <summary>
    /// Cancels any in-flight inference and drops all queued chunks so the
    /// current model stops working immediately (used before a language
    /// switch or when dictation stops).
    /// </summary>
    public void CancelActiveWork()
    {
        try
        {
            _inferenceCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down; ignore.
        }

        // Bump the generation so the (single) consumer skips every queued
        // chunk stamped with the old generation. Channels are single-reader,
        // so the queue must NOT be drained from this thread.
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Cancels in-flight work, then loads the model for <paramref name="language"/>
    /// on a background thread (never the UI thread). Returns true on success.
    /// </summary>
    public async Task<bool> PrepareModelAsync(VoxLanguage language)
    {
        if (_disposed) return false;

        CancelActiveWork();

        // The SAME _modelLock that guards inference (TranscribeCoreAsync for
        // TranscribeAsync), so a model is NEVER disposed while ProcessAsync
        // is still running.
        await _modelLock.WaitAsync(_cts.Token);
        try
        {
            // Heavy work (reading a multi-GB ggml file + model init) on a
            // thread-pool thread so the WPF Dispatcher is never blocked.
            // DisposeModelLockedAsync uses processor.DisposeAsync() so even
            // a late-finishing inference is waited for instead of throwing
            // "Cannot dispose while processing".
            await Task.Run(() => LoadModelLockedAsync(language));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ReportModelError(language, ex);
            return false;
        }
        finally
        {
            // Always install a fresh token (even after a failed load) so
            // future chunks never run against a cancelled token.
            var stale = _inferenceCts;
            _inferenceCts = new CancellationTokenSource();
            try { stale.Dispose(); } catch { }

            _modelLock.Release();
        }
    }

    public Exception? GetModelError(VoxLanguage language) =>
        _modelErrors.TryGetValue(language, out Exception? error) ? error : null;

    /// <summary>
    /// Queues a chunk for sequential transcription. Never blocks the calling thread.
    /// </summary>
    public Task<string> TranscribeAsync(float[] samples, VoxLanguage language)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        long gen = Interlocked.Read(ref _generation);
        var request = new ChunkRequest(samples, language, completion, gen);
        System.Diagnostics.Debug.WriteLine(
            $"[VoxType] Chunk queued: {samples.Length} samples, language={language}, gen={gen}");
        if (!_channel.Writer.TryWrite(request))
        {
            completion.TrySetResult(string.Empty);
        }
        return completion.Task;
    }

    private async Task ConsumeLoopAsync()
    {
        // Outer guard: the consumer task must NEVER die, no matter what
        // ReadAllAsync or any chunk throws.
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                // Inner guard: one bad chunk must never kill the loop.
                try
                {
                    // Skip chunks queued before the latest cancel (switch/stop).
                    if (request.Generation != Interlocked.Read(ref _generation))
                    {
                        System.Diagnostics.Debug.WriteLine("[VoxType] Chunk dropped (stale generation).");
                        request.Completion.TrySetResult(string.Empty);
                        continue;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"[VoxType] Chunk dequeued: {request.Samples.Length} samples, language={request.Language}");

                    string text = await TranscribeCoreAsync(request.Samples, request.Language);
                    System.Diagnostics.Debug.WriteLine(
                        text.Length == 0
                            ? "[VoxType] Transcription complete: (empty)"
                            : $"[VoxType] Transcription complete: \"{text}\"");
                    request.Completion.TrySetResult(text);
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[VoxType] Chunk cancelled (language switch or stop).");
                    request.Completion.TrySetResult(string.Empty);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VoxType] Inference error: {ex}");
                    request.Completion.TrySetResult(string.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[VoxType] Consumer loop cancelled (shutdown).");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VoxType] Consumer loop exited unexpectedly: {ex}");
        }
    }

    /// <summary>
    /// Loads (or keeps) the model for <paramref name="language"/>. Caller must
    /// hold <c>_modelLock</c>. Disposes the previous model (asynchronously)
    /// before loading the new one so only one ggml model occupies RAM at a
    /// time.
    /// </summary>
    private async Task LoadModelLockedAsync(VoxLanguage language)
    {
        if (_loadedLanguage == language && _factory != null && _processor != null)
        {
            return; // already the active model
        }

        // Dispose the previously loaded model (factory owns the native
        // context). Async dispose waits for any late-finishing inference.
        await DisposeModelLockedAsync();

        string? path = ResolveModelPath(language)
            ?? throw new FileNotFoundException(
                $"Whisper model '{ModelFolderName}/{(language == VoxLanguage.BN ? BengaliModelFile : EnglishModelFile)}' not found.");

        var factory = WhisperFactory.FromPath(path);
        var builder = factory.CreateBuilder().WithThreads(ThreadCount);

        if (language == VoxLanguage.BN)
        {
            // Default sampling strategy is Greedy (fastest for fine-tuned BN model)
            builder = builder.WithLanguage("bn").WithPrompt(BengaliPrompt);
        }
        else
        {
            builder = builder.WithLanguage("en")
                .WithPrompt(EnglishPrompt)
                .WithBeamSearchSamplingStrategy(b => b.WithBeamSize(2));
        }

        _factory = factory;
        _processor = builder.Build();

        lock (_stateLock)
        {
            _loadedLanguage = language;
        }
        _modelErrors.TryRemove(language, out _);

        System.Diagnostics.Debug.WriteLine(
            $"[VoxType] {(language == VoxLanguage.BN ? "Bangla" : "English")} model loaded from {path}");
    }

    /// <summary>
    /// Releases the current factory + processor. Caller must hold <c>_modelLock</c>.
    /// ALWAYS uses <c>DisposeAsync</c> for the processor: the synchronous
    /// <c>Dispose()</c> throws "Cannot dispose while processing" when a
    /// ProcessAsync call is still finishing, whereas <c>DisposeAsync()</c>
    /// waits for it.
    /// </summary>
    private async Task DisposeModelLockedAsync()
    {
        WhisperProcessor? processor = _processor;
        WhisperFactory? factory = _factory;
        _processor = null;
        _factory = null;
        lock (_stateLock)
        {
            _loadedLanguage = null;
        }

        if (processor != null)
        {
            try
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VoxType] Processor dispose failed: {ex.Message}");
            }
        }
        factory?.Dispose();
    }

    private void ReportModelError(VoxLanguage language, Exception error)
    {
        _modelErrors[language] = error;
        System.Diagnostics.Debug.WriteLine($"[VoxType] Model error ({language}): {error.Message}");
    }

    private async Task<string> TranscribeCoreAsync(float[] samples, VoxLanguage language)
    {
        // Hold the SAME _modelLock that PrepareModelAsync uses for
        // swap/dispose, across the entire inference, so a model can never be
        // disposed while ProcessAsync is still running.
        await _modelLock.WaitAsync(_cts.Token);
        try
        {
            await LoadModelLockedAsync(language);
            WhisperProcessor processor = _processor!;

            // Use the inference token (cancelled on language switch/stop) so a
            // switch aborts this work immediately and frees the model lock.
            var token = _inferenceCts.Token;
            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, token))
            {
                sb.Append(segment.Text);
            }
            return CleanTranscription(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or WhisperModelLoadException)
        {
            ReportModelError(language, ex);
            return string.Empty;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    // Known Whisper hallucinations / non-speech artifacts.
    private static readonly Regex _artifacts = new(
        @"\[\s*BLANK_AUDIO\s*\]|\[\s*SILENCE\s*\]|\[\s*MUSIC\s*\]|\[\s*NOISE\s*\]|\[\s*APPLAUSE\s*\]|\(?Music\)?|\(?Applause\)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Strips known artifacts and collapses whitespace; empty when nothing usable.
    /// </summary>
    public static string CleanTranscription(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string cleaned = _artifacts.Replace(raw, " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? string.Empty : cleaned;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initialized = false;

        _channel.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _inferenceCts.Cancel(); } catch { } // abort any in-flight inference

        try
        {
            _consumerTask?.Wait(3000);
        }
        catch
        {
            // Best effort shutdown of the consumer.
        }

        // Take the SAME model lock that inference (TranscribeCoreAsync)
        // holds, then dispose the model asynchronously: DisposeAsync() waits
        // for any still-finishing ProcessAsync instead of throwing "Cannot
        // dispose while processing".
        bool lockTaken = false;
        try
        {
            lockTaken = _modelLock.Wait(5000);
            if (lockTaken)
            {
                Task disposeTask = DisposeModelLockedAsync();
                Task.WaitAny(disposeTask, Task.Delay(10000));
            }
        }
        catch
        {
            // Best effort model teardown at shutdown.
        }
        finally
        {
            if (lockTaken) _modelLock.Release();
        }

        _modelLock.Dispose();
        try { _inferenceCts.Dispose(); } catch { }
        _cts.Dispose();
    }
}