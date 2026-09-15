# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A "Replace" patch type in the Hot Patch view, alongside Prefix/Postfix/Transpiler/Finalizer: pick a method from a loaded (e.g. rebuilt, bug-fixed) assembly with the same signature as a target method, and it runs completely in place of the target's own body — no need to hand-write a prefix that skips the original and copies out the result yourself.
- Checkboxes on the Hot Patch view's active patches list, with a "Select all" toggle and a "Remove selected" button, so multiple patches can be removed together with a single confirmation instead of one at a time.

### Changed

- The Hot Patch view's active patches list is easier to scan when many patches are applied: each entry now shows a color-coded badge for the patch type, the target method as the prominent line, and the patch method/source assembly on a smaller line below it, with long names truncated (hover to see the full name) instead of wrapping across several lines.

### Fixed

- In the Hot Patch view's "Patch method" browser, navigating back up to a type or namespace after changing the target method (or Prefix/Postfix/etc.) no longer shows the old, now-outdated list of compatible types and methods.
- Applying a hot patch after reloading the same patch assembly several times in a row no longer sometimes fails with "Unexpected null in ..." (or silently applies against a stale, previously-loaded version of your patch method).
- In the Error Inspector's stack frame listing, each applied patch now shows its fully qualified type and method name instead of just the method name.

## [0.1.0] - 2026-09-10

### Added

- An Error Inspector for browsing captured errors, with inline decompilation of stack frames and applied Harmony patches.
- A Hot Patch view for hot-loading assemblies with patches and applying them live, without restarting the game, as well as a button to scaffold a ready-to-build patch project for a given target method.

[Unreleased]: https://github.com/ilyvion/debug-assistance/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ilyvion/debug-assistance/releases/tag/v0.1.0
