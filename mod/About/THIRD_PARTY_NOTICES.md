# Third-party notices

This mod contains original code licensed under the MIT license (see `LICENSE.txt`).

It can download and run third-party software at the user's request. The mod does not bundle
the model weights or the llama.cpp binaries; they are fetched from their upstream releases.

## llama.cpp

- Project: https://github.com/ggml-org/llama.cpp
- Release used by the installer: https://github.com/ggml-org/llama.cpp/releases/tag/b12069
- License: MIT. The full text is in `MIT-llama.cpp.txt`.
- The installer verifies the SHA256 of the downloaded archive before extracting it.

## Clef and Clef Flash

- Models: https://huggingface.co/Cloudflare/clef and https://huggingface.co/Cloudflare/clef-flash
- License: Apache-2.0. The full text is in `Apache-2.0.txt`.
- GGUF conversions used by the installer:
  - https://huggingface.co/bartowski/Cloudflare_clef-GGUF
  - https://huggingface.co/bartowski/Cloudflare_clef-flash-GGUF
- The installer verifies the size and SHA256 of each model after download.

## Downloaded files

llama.cpp, from the `b12069` release:

- Windows x64 (Vulkan), `llama-b12069-bin-win-vulkan-x64.zip` — SHA256
  `e1b1c4e5481fe88e3f49f97908b53bb7699ad8aa3f6332a58f944e7a8f068b33`
- Linux x64 (Vulkan), `llama-b12069-bin-ubuntu-vulkan-x64.tar.gz` — SHA256
  `8001b930cc5e7cb500193dbdbea762a7f808ad2996ae12440e7af5020c94a4da`
- macOS arm64, `llama-b12069-bin-macos-arm64.tar.gz` — SHA256
  `91023146fab641e15d429c84002bf2e614a7a127992eedb443872fb23ba69439`
- macOS x64, `llama-b12069-bin-macos-x64.tar.gz` — SHA256
  `7f1e6a7ce371c582ad8a9d9abed7d0f96a2a8060d4679109897cb3b98c181620`

Clef models:

- `Cloudflare_clef-Q4_K_M.gguf` — SHA256
  `f1e2cc7f36893b52f773cc01f0ed351abab17c510502423bc4d2f5782a93cebb`
- `Cloudflare_clef-flash-Q8_0.gguf` — SHA256
  `55c222dfc9f5760d06015ee6d6452ea45113bb6f333178c345c489dbc134bcfe`

The models and binaries remain subject to their own licenses. This mod's installer only
downloads them from the official upstream locations listed above.
