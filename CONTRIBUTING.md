# Contributing

Keystone is in early development. The core runtime works, but there's a lot left to build — see the README for current status.

Right now I'm the only person working on this, and I'm looking for contributors. If something interests you, open an issue or start a discussion. For anything beyond small fixes, it's worth talking through the approach first since the architecture is still settling in places.

## Where Help Is Needed

**Platform ports** — Keystone is macOS-only right now. A Windows port would be a significant contribution — the rendering pipeline, window system, and process model all need platform-specific implementations. I'll likely get to Linux eventually, but Windows is not on my roadmap.

**Documentation** — The docs need work. If you understand a part of the system and can write clearly about it, that's valuable. Corrections, expansions, examples, and new guides are all welcome.

## Getting Started

1. Fork the repo
2. Check the [docs/](docs/) for an overview of the architecture
3. Open an issue describing what you'd like to work on
4. Submit a PR when ready

## Current Build Requirements

- .NET 10 SDK
- macOS 15+ (Apple Silicon)
- Bun
- Rust toolchain
