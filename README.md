<div align="center">

# 🎙️ VoxType

**Offline-First & Hybrid Bilingual (Bangla + English) Voice Typing for Windows**

[![.NET 10](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D6?style=for-the-badge&logo=windows)](https://www.microsoft.com/windows)
[![Speech Engine](https://img.shields.io/badge/Engine-Whisper.cpp%20%2B%20Gemini-00C853?style=for-the-badge)](#architecture--tech-stack)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=for-the-badge)](LICENSE)

*Hold a hotkey, speak naturally in English or Bangla, release — and watch crisp text appear directly at your cursor in any Windows app.*

</div>

---

## ✨ Why VoxType?

Most voice-typing tools for Windows either require a paid cloud subscription, struggle heavily with Bengali conjuncts (**যুক্তবর্ণ**), or lock you inside a single browser tab. **VoxType** is built from the ground up as a lightweight, native C# / WPF desktop utility that works **system-wide** across VS Code, Notepad, Chrome, Word, Discord, or any text field.

### 🚀 Key Features

- **⚡ Instant Push-to-Talk (Hold-to-Talk):** Hold your hotkey to speak, release to transcribe and inject. No awkward voice-activity cutoffs or accidental background triggers.
- **🌐 True Bilingual Engine (`EN` + `BN`):**
  - **English (`EN`):** Runs **100% offline** using `Whisper.net` (`medium.en`) with Beam Search and custom vocabulary prompting for technical terms, names, and natural punctuation.
  - **Bangla (`BN`) — Smart Hybrid Mode:** Uses **Gemini 2.5 Flash** (free tier) when online for near-perfect Bengali spelling, conjuncts (**যুক্তবর্ণ**), and Banglish — with **automatic offline fallback** to a local fine-tuned **BanglaASR** Whisper model when disconnected.
- **🎯 System-Wide Text Injection:** Types directly into the currently focused window using the Win32 `SendInput` API with reliable Unicode clipboard fallback.
- **🎚️ Built-in Audio Enhancement:** Automatic Peak Audio Normalization boosts quiet laptop microphones cleanly without amplifying background silence.
- **💊 Minimal Floating Pill & System Tray:** Real-time visual feedback (`Idle` → `Listening` → `Transcribing...`) without stealing window focus, plus seamless background operation.
- **🔒 Privacy-First:** Zero telemetry, zero accounts, and 100% local settings & history.

---

## ⌨️ Default Shortcuts

| Action | Shortcut (Hold & Release) | Engine |
| :--- | :--- | :--- |
| **English Dictation (`EN`)** | `Win` + `Right Ctrl` | Local Offline `Whisper.net` |
| **Bangla / Banglish (`BN`)** | `Win` + `Right Alt` | Hybrid (`Gemini 2.5 Flash` → Offline `BanglaASR` fallback) |

> *Powered by a low-level Windows keyboard hook (`WH_KEYBOARD_LL`) with Start Menu suppression so holding `Win` never triggers the Start Menu.*

---

## 🛠️ Architecture & Tech Stack

| Layer | Technology |
| :--- | :--- |
| **Framework & UI** | C# / .NET 10 LTS, WPF (XAML) |
| **Audio Capture** | NAudio (16kHz Mono PCM + Peak Normalization) |
| **Offline Speech Engine** | `Whisper.net` (`whisper.cpp` native bindings) |
| **Optional Online BN Boost** | Google Gemini 2.5 Flash REST API (Zero-cost free tier) |
| **Windows Integration** | Win32 `WH_KEYBOARD_LL` Hook & `SendInput` API |
| **Local Storage** | JSON Settings & SQLite Dictation History |

---

## 📦 Getting Started

### Prerequisites

- Windows 10 or Windows 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 1. Clone the Repository

```powershell
git clone https://github.com/YOUR_USERNAME/VoxType.git
cd VoxType
```

### 2. Download Offline Speech Models

Model binaries are excluded from Git to keep the repository lightweight. Create a `models_bin` folder in the project root and place your GGML models inside:

```text
VoxType/
└── models_bin/
    ├── ggml-en.bin   # English model (Recommended: ggml-medium.en.bin)
    └── ggml-bn.bin   # Bangla offline fallback (Recommended: BanglaASR GGML or large-v3-turbo)
```

Download the recommended English model directly via PowerShell:

```powershell
mkdir models_bin -Force
curl.exe -L "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.en.bin" -o "models_bin\ggml-en.bin"
```

### 3. (Optional) Enable Hybrid Mode for Bangla

To unlock ultra-fast, high-accuracy Bengali conjuncts and Banglish transcription via the free Gemini API, set your API key as a Windows User Environment Variable:

```powershell
[Environment]::SetEnvironmentVariable("GEMINI_API_KEY", "YOUR_GEMINI_API_KEY", "User")
```

*Restart your terminal after setting the variable. If no key is set or you are offline, VoxType automatically falls back to `models_bin\ggml-bn.bin`.*

### 4. Build & Run

```powershell
dotnet build
dotnet run
```

### 5. Publish Standalone Release `.exe`

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o ".\VoxType_Release"
```

Copy your `models_bin` folder into `VoxType_Release` and launch `VoxType.exe`. To auto-start with Windows, place a shortcut to `VoxType.exe` inside `shell:startup`.

---

## 🗺️ Roadmap

- [x] Low-level Push-to-Talk global hotkeys (`Win + RCtrl` / `Win + RAlt`)
- [x] Offline English dictation (`Whisper.net` + Beam Search + Custom Prompts)
- [x] Offline Bangla fallback (`BanglaASR` fine-tuned GGML)
- [x] Hybrid online Bangla mode (`Gemini 2.5 Flash` with automatic offline fallback)
- [x] Floating status pill (`Idle` / `Listening` / `Transcribing...`) & Peak Audio Normalization
- [ ] 7-Page Dark Dashboard UI (Home, Language, Microphone, Model Manager, SQLite History, Settings, About)
- [ ] In-app model downloader & microphone level meter
- [ ] Windows Installer (Inno Setup)

---

## 🔐 Privacy & Security

- **Offline by Default:** English dictation and offline Bangla fallback run entirely on your local CPU — no audio leaves your machine.
- **Safe API Key Handling:** When Hybrid Mode is enabled, your `GEMINI_API_KEY` is read from your local environment variable and is never committed to source control.

---

## 👨‍💻 Author & License

Built with ❤️ by **Hasan Rahat** ([MR Tech BD](https://github.com/plastid000)).

This project is open-source and licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.