using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoxType.Services;

/// <summary>
/// Hybrid transcriber using Google Gemini API for Bengali (BN) with fallback to local Whisper.
/// Returns null when no API key is configured, API call fails, or network is unavailable.
/// </summary>
public class GeminiTranscriptionService : IDisposable
{
    private readonly string? _apiKey;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public GeminiTranscriptionService()
    {
        _apiKey = LoadApiKey();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    /// <summary>
    /// Reads the Gemini API key from GEMINI_API_KEY environment variable or gemini_key.txt file.
    /// Returns null if not configured or empty.
    /// </summary>
    private static string? LoadApiKey()
    {
        // Try environment variable first
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            return key.Trim();
        }

        // Try local file next to executable
        string exePath = AppContext.BaseDirectory;
        string filePath = Path.Combine(exePath, "gemini_key.txt");
        if (File.Exists(filePath))
        {
            try
            {
                key = File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key.Trim();
                }
            }
            catch
            {
                // Ignore file read errors
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if a valid API key is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    /// <summary>
    /// Transcribes Bengali audio using Gemini API.
    /// Returns null if API key is missing, request fails, or any network error occurs.
    /// </summary>
    /// <param name="audio">16kHz mono float[] samples in range [-1, 1]</param>
    /// <returns>Transcribed text or null on failure</returns>
    public async Task<string?> TranscribeAsync(float[] audio)
    {
        // Return null immediately if no API key
        if (string.IsNullOrEmpty(_apiKey))
        {
            return null;
        }

        try
        {
            // Convert float[] to 16-bit PCM WAV bytes, then Base64-encode.
            byte[] wavBytes = ConvertFloatToWavBytes(audio);
            string base64Wav = Convert.ToBase64String(wavBytes);

            var jsonContent = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new object[]
                        {
                            new { text = "Transcribe this Bengali (or Banglish) audio accurately with proper Bengali spelling, conjuncts (যুক্তবর্ণ), and punctuation. Return ONLY the transcribed text, nothing else." },
                            new { inline_data = new { mime_type = "audio/wav", data = base64Wav } }
                        }
                    }
                }
            };

            string json = JsonSerializer.Serialize(jsonContent);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey}";

            // POST request (8-second timeout enforced on the HttpClient).
            HttpResponseMessage response = await _httpClient.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string responseJson = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            // candidates[0].content.parts[0].text
            if (root.TryGetProperty("candidates", out JsonElement candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0 &&
                candidates[0].TryGetProperty("content", out JsonElement contentElem) &&
                contentElem.TryGetProperty("parts", out JsonElement parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0 &&
                parts[0].TryGetProperty("text", out JsonElement textElem))
            {
                return textElem.GetString()?.Trim();
            }

            return null;
        }
        catch (Exception)
        {
            // Any network / HTTP / timeout / parse error -> silent fallback.
            return null;
        }
    }


    /// <summary>
    /// Converts 16kHz mono float[] samples (range -1 to 1) into a complete
    /// 16-bit PCM WAV byte array (RIFF header + data).
    /// </summary>
    private static byte[] ConvertFloatToWavBytes(float[] samples)
    {
        const int sampleRate = 16000;
        const int bitsPerSample = 16;
        const int channels = 1;
        const int byteRate = sampleRate * channels * bitsPerSample / 8;
        const int blockAlign = channels * bitsPerSample / 8;

        int sampleCount = samples.Length;
        int dataSize = sampleCount * 2; // 16-bit = 2 bytes per sample
        int fileSize = 44 + dataSize;   // standard 44-byte WAV header

        byte[] wav = new byte[fileSize];

        // RIFF header
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        WriteLittleEndianInt(wav, 4, fileSize - 8);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';

        // fmt sub-chunk (PCM)
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        WriteLittleEndianInt(wav, 16, 16);                 // sub-chunk size
        WriteLittleEndianShort(wav, 20, 1);                // PCM audio format
        WriteLittleEndianShort(wav, 22, (ushort)channels); // 1 (mono)
        WriteLittleEndianInt(wav, 24, sampleRate);         // 16000 Hz
        WriteLittleEndianInt(wav, 28, byteRate);           // bytes per second
        WriteLittleEndianShort(wav, 32, (ushort)blockAlign);
        WriteLittleEndianShort(wav, 34, (ushort)bitsPerSample);

        // data sub-chunk
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        WriteLittleEndianInt(wav, 40, dataSize);

        // float -> 16-bit PCM (clamped to [-1, 1])
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = samples[i];
            if (sample > 1f) sample = 1f;
            else if (sample < -1f) sample = -1f;

            short pcm = (short)(sample * short.MaxValue);
            wav[44 + i * 2] = (byte)(pcm & 0xFF);
            wav[45 + i * 2] = (byte)((pcm >> 8) & 0xFF);
        }

        return wav;
    }

    private static void WriteLittleEndianInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteLittleEndianShort(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }
}
