# Task p41-gpu-acceleration: Vulkan and CUDA builds of llama-server, chosen in Settings

Branch `feat/gpu-acceleration` (based on `phase4`). Decision D-042 (owner, 2026-10-20). Backend only: the Settings UI
(`p41-settings`, later) draws the Acceleration control and calls what you expose. Parallel tasks: the design-parity
harness and the English default (App resources) — stay out of `src/Murmur.App/Resources` and `tests/Murmur.App.Tests/Design`.

## Scope
src/Murmur.Models (catalog items, platform detection inputs), src/Murmur.Intelligence (`LocalLlmOptions`, server start,
fallback), src/Murmur.Core/Settings (one new setting, approved below), src/Murmur.Capture.Windows (GPU vendor / Vulkan
presence detection, Windows-only), src/Murmur.Desktop (composition), tools/Murmur.ModelFetch + `.github/workflows/models.yml`
(pins for the new items), tests. Not the App views.

## Core contract (approved — nothing else in Core's public surface)
- `AppSettings.Acceleration` : `enum AiAcceleration { Cpu, Vulkan, Cuda }`, default `Cpu`.
- A small Core interface for what the UI needs: which accelerations this PC supports (`Cpu` always; `Vulkan` when a
  Vulkan loader is present; `Cuda` when an NVIDIA GPU is present) — e.g. `IAccelerationProbe` with a cheap, cached,
  allocation-light answer and a human-readable GPU name for the Settings row. Implemented in `Murmur.Capture.Windows`
  (DXGI adapter vendor id 0x10DE for NVIDIA, `vulkan-1.dll` loadable), a CPU-only implementation elsewhere.

## Spec
1. **Catalog**: two optional runtime items next to the CPU `llama-cpp` item, same `LlamaCppTag` (`b11716`), Windows:
   `llama-<tag>-bin-win-vulkan-x64.zip` and `llama-<tag>-bin-win-cuda-12.4-x64.zip` + `cudart-llama-bin-win-cuda-12.4-x64.zip`
   (check the exact asset names of that release; GitHub mirror as for the CPU build), each installed into its own
   directory under the models directory (e.g. `llama.cpp-vulkan/`, `llama.cpp-cuda/`) so the CPU build stays intact.
   SHA-256 pins like every other item; the `models.yml` workflow resolves and checks them (`--resolve-pins`). Linux:
   the Vulkan ubuntu build if the release has one; no CUDA on Linux.
2. **Choosing the server**: `LocalLlmOptions` gets the server path from the chosen acceleration; GPU builds add
   `-ngl 99` to the chat server (and to the embedding server only if it helps — measure or reason, write it down).
   Selecting an acceleration whose runtime is not installed reports "needs download" to the caller — the Settings task
   starts the download through the model manager; nothing downloads silently.
3. **Fallback**: if the GPU server fails to start or dies at load (driver missing, out of VRAM), fall back to the CPU build
   for that session, keep the user's choice, and surface a typed reason the UI can show ("GPU unavailable: …"). Never
   while recording (the existing AI-aware rules keep models unloaded during recording — keep that true).
4. **Live switch**: changing the acceleration takes effect for the next model load without restarting the app (the
   existing live-models mechanism, `AiAwareModelManager`); a loaded model is reloaded when idle.
5. Tests: catalog entries and install layout; options pick the right binary and args; fallback on a failing start (fake
   server); probe on non-Windows returns Cpu only; Windows probe tests with `[Trait("Category","Windows")]`.
6. Report (Ukrainian): asset names and sizes, what the probe detects, measured or expected speed-up, what CI must check.
