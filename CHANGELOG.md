# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Probes: on the Hot Patch view, pick a method and click 'Add probe' to record who calls it and from where, without needing an error to happen. Any stack frame shown in an error or a probe hit also gets its own 'Probe this method' button for the same purpose. Each unique call stack that invokes the method is captured once, with a running count of total and unique calls shown in an 'Active probes' panel that keeps itself up to date while you're on the Hot Patch view; a probe stops tracking automatically once it's recorded a very large number of calls, to keep the overhead bounded. Captured probe hits get their own 'Probes' section, where they can be decompiled, used to jump into Hot Patch on any frame, and turned into an AI prompt, the same as captured errors. Errors, Hot Patch, and Probes are all presented as equal, side-by-side pages via a shared set of tabs.
- A 'Replace' patch type in the Hot Patch view, alongside Prefix/Postfix/Transpiler/Finalizer: pick a method from a loaded (e.g. rebuilt, bug-fixed) assembly with the same signature as a target method, and it runs completely in place of the target's own body — no need to hand-write a prefix that skips the original and copies out the result yourself.
- Checkboxes on the Hot Patch view's active patches list, with a 'Select all' toggle and a 'Remove selected' button, so multiple patches can be removed together with a single confirmation instead of one at a time.

### Changed

- The Hot Patch view's active patches list is easier to scan when many patches are applied: each entry now shows a color-coded badge for the patch type, the target method as the prominent line, and the patch method/source assembly on a smaller line below it, with long names truncated (hover to see the full name) instead of wrapping across several lines.
- The Errors, Hot Patch, and Probes pages now share the exact same header layout: every page shows its own subheader alongside the shared tabs, and the theme picker is now available from the Hot Patch and Probes pages too, not just Errors.

### Fixed

- Generating a patch project for a method that references types from another mod no longer produces a project that fails to build: the scaffolded .csproj now automatically references that mod's assembly, and publicizes it if the referenced types are internal or private.
- In the Hot Patch view's 'Patch method' browser, navigating back up to a type or namespace after changing the target method (or Prefix/Postfix/etc.) no longer shows the old, now-outdated list of compatible types and methods.
- Applying a hot patch after reloading the same patch assembly several times in a row no longer sometimes fails with 'Unexpected null in ...' (or silently applies against a stale, previously-loaded version of your patch method).
- In the Error Inspector's stack frame listing, each applied patch now shows its fully qualified type and method name instead of just the method name.

## [0.1.0] - 2026-09-10

### Added

- An Error Inspector for browsing captured errors, with inline decompilation of stack frames and applied Harmony patches.
- A Hot Patch view for hot-loading assemblies with patches and applying them live, without restarting the game, as well as a button to scaffold a ready-to-build patch project for a given target method.

[Unreleased]: https://github.com/ilyvion/debug-assistance/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ilyvion/debug-assistance/releases/tag/v0.1.0
