# Debug Assistance

A RimWorld mod that adds a web-based Error Inspector and Hot Patch panel
for finding, understanding, and fixing mod errors without restarting the
game.

Open the Error Inspector from a new debug hotbar button, or by browsing
to `http://localhost:3294` (the port is configurable in mod settings, along
with an option to allow connections from other devices on your network). It
shows a filterable, sortable list of captured errors with per-frame mod
attribution, including which Harmony patches apply to each frame
individually. You can save the current set of errors to a file, and
load (merging or replacing) or delete a previously saved one. A toggle in
the list header (also available as a "Capture errors" checkbox in mod
settings) pauses capture without closing the inspector - already-captured
errors stay in place, and capture resumes as soon as you flip it back.
Errors logged directly to Unity rather than through RimWorld's own logging
(such as a texture dimension warning) aren't actionable from within this mod
and are excluded from capture by default; an "Ignore Unity-only errors"
setting lets you turn that back off.

Each error's detail view shows its inner-exception chain, if it has one, from
the root cause up to the exception that was actually thrown, each with its
own raw stack trace you can expand and copy.

Each stack frame, and each Harmony patch applied to it, has an inline
**Decompile** action that decompiles that method on the spot with syntax
highlighting and the crashing line highlighted and scrolled into view -
including the actual merged prefix/postfix/transpiler replacement Harmony
runs for a patched frame, so the highlighted line is correct even when the
crash happened inside that replacement rather than the original method's
own code. A **Decompile all** button decompiles every frame and patch on an
error at once in the background, with a progress bar.

A light/dark/system theme picker in the top bar uses the Catppuccin Latte
and Frappe color palettes.

The **Hot Patch** view (opened from the top bar, or from a **Patch this
method** button on any stack frame) lets you load a Harmony patch assembly
you've built yourself and apply it without restarting the game, with your
currently active patches always visible in a sidebar. Browse down through
an Assembly → Namespace → Type → Member picker (with a filter box, narrowing
to only signature-compatible methods once a patch type and target are
picked) to choose which of the assembly's methods is the Prefix, Postfix,
Transpiler, or Finalizer, pick the target method from any currently loaded
mod, apply it, and remove it again later. Reloading the same assembly after
rebuilding it reports how many previously applied patches from that file
were automatically removed first, and the view remembers the assembly path
and scaffold project location you last used. A non-static patch method -
the most common way to trip a cryptic "Invalid IL code" error from Harmony -
is rejected up front with a clear message; any other failure to apply
reports the specific instruction Harmony choked on and is also logged so it
shows up in the Error Inspector. There is no sandboxing - a bug in an
injected patch can crash or corrupt the game exactly like a bug in any
other Harmony patch.

A **Generate patch project** button in the Hot Patch view creates a
ready-to-build starter project (csproj, global usings, `.gitignore`, and a
stub with one example method per patch type) in a location you pick, so you
don't have to hand-assemble a project before writing a patch; when a target
method is already picked, the stub's Prefix and Postfix already match that
method's real parameters and return type instead of being generic
placeholders. The patch assembly path is filled in automatically once
you've built the generated project.

The file/folder browser used throughout offers one-click shortcuts to your
Mods folder and Steam Workshop folder, a clickable root entry in the
breadcrumb trail, and a path field that doubles as a jump-to box.

## Requirements

- [Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077)
- [ilyvion's Laboratory](https://github.com/ilyvion/ilyvion-laboratory/releases/latest)

## License

Licensed under either of

- Apache License, Version 2.0, ([LICENSE.Apache-2.0](LICENSE.Apache-2.0) or http://www.apache.org/licenses/LICENSE-2.0)
- MIT license ([LICENSE.MIT](LICENSE.MIT) or http://opensource.org/licenses/MIT)

at your option.

`SPDX-License-Identifier: Apache-2.0 OR MIT`

### Contribution

Unless you explicitly state otherwise, any contribution intentionally submitted
for inclusion in the work by you, as defined in the Apache-2.0 license, shall be
dual licensed as above, without any additional terms or conditions.
