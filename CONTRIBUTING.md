# Contributing

Keystone-Desktop is in early development. The core framework / software foundation works, but there's a lot left to build — see the README for current status.

Right now I'm the only person working on this, and I'm looking for contributors. If something interests you, open an issue or start a discussion. For anything beyond small fixes, it's worth talking through the approach first since the architecture is still settling in places.

## Where Help Is Needed

**Platform ports** — Keystone is macOS-only right now. A Windows port would be a significant contribution — the rendering pipeline, window system, and process model all need platform-specific implementations. I'll likely get to Linux eventually, but Windows is not on my roadmap.

**Documentation** — The docs need work. If you understand a part of the system and can write clearly about it, that's valuable. Corrections, expansions, examples, and new guides are all welcome.

**Tooling** - SDK and other base tooling is useable, but needs to be more comprehensive. Keystone.Toolkit particularly should be expanded if they do opt for native chrome. The Tab/Bind system needs work. 

**Optimizations** - for the most part the performance is stable, expectable, and scales somehwat between linearly/logarithmically

**Ecosystem** - If you can contribute any outside / ecosystem tools, or use keystone_desktop yourself for something 

## Getting Started

1. Fork the repo
2. Check the [docs/](docs/) for an overview of the architecture
2a. (Optional) Utilize Claude (Opus) or another frontier model, the codebase is still small enough that it can read most/all of the source in one context window and still have some room to reason about it.  
3. Open an issue describing what you'd like to work on
3a. you can also reach me on discord  
4. Submit a PR when ready

## Pre-PR Checks

Run local checks before opening a PR:

```bash
./tools/preflight.sh
```

## Current Build From Source Requirements

- .NET 10 SDK
- macOS 15+ (Apple Silicon)
- Bun
- Rust toolchain
