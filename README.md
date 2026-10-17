# Murmur

Local-first meeting recorder for Windows (macOS next): mic + system audio + optional screen, on-device transcription with Parakeet, and AI search across recordings with Gemma. No cloud, no accounts.

> Status: pre-alpha, phase 0. See [docs/PLAN.md](docs/PLAN.md).

## Working with Claude Code

1. Drop your mockups into `docs/design/` (see its README).
2. Open the repo in Claude Code **on Windows** (capture APIs and the win-x64 AOT build only run there).
3. Run `/phase 0`. The architect plans the phase, waits for your OK, then delegates to the agents in `.claude/agents/`.
4. After the report, `/clear` and run `/phase 1`, and so on.

## License

Apache-2.0. Parakeet models are CC-BY-4.0, Gemma 4 is Apache-2.0.
