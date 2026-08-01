# Agent Instructions

## Working rules

- Don't reiterate AGENTS.md related stuff in plans, you will be reading AGENTS.md anyway.

## Build/Test discipline

- Validate code changes before committing when practical.
- After committing, run `./publish.ps1 local`. It stops the previous local ClypDat process and launches the new build.
- The app embeds `git rev-parse HEAD`; publishing after the commit ensures its displayed hash matches that commit.

## Commits

- Commit each completed change unless told otherwise. Never commit `AGENTS.md` or `publish.ps1` unless explicitly requested.
- Do not Co-Author commits.

## Releases

- Ask for a version or bump intent before a release. `.github/workflows/release.yml` is the source of truth; tags use `v<version>`.
- If the user asks you to release and does not specify a version number, ask what version number to use.
- Before starting the release action, set `<Version>` in `native/src/ClypDat.App/ClypDat.App.csproj` to that release version (without the `v`) and commit/push it. Verify the published `ClypDat.exe` FileVersion matches the release version; otherwise installed users will keep receiving an update prompt.
- Release by making sure everything local is pushed, then run the GitHub action and wait for it to finish.
- Once the GitHub action is complete, update the release with patch notes based only on what changed from the last version, not the bug-fixing steps in between that the user never experienced.
- Write patch notes as two markdown sections, `## What's New` and `## Fixes`, each with `- ` bullets. The in-app updater (CreateUpdateDialog, MainWindow.axaml.cs) parses these headings into two separate columns (AppUpdateService.ExtractCategorizedNotes) - a release body without them just dumps every bullet into What's New. Skip a section entirely if it has nothing to say (e.g. a pure-feature release needs no `## Fixes`).

## Git Workflow

- Make every change in a turn ONE commit. Don't split a turn's work across several commits, even when it touches unrelated areas - batch it all and commit once at the end.
- After making any code changes, create that commit before finishing the task.
- Push immediately after every commit, without asking.
- After each commit, tell the user the commit hash and message.
- Run the relevant validation checks before committing when practical.
- If validation cannot be run, mention that in the final response and still commit the completed code changes unless the user asks otherwise.
- Do not include unrelated user changes in the commit.
- Use a concise commit message that describes the change.
- Do not add a "Co-Authored-By: Claude" (or any AI co-author) trailer to commit messages.

## Boundaries

- Never take control of the user's mouse or keyboard (no simulated clicks, cursor moves, or key presses).
- Never take screenshots of the user's screen or windows.
- Verify work by building, reading logs, and reasoning about the code instead. If something can only be confirmed visually, say so and let the user check it.

# Skills
- Always use $caveman ultra.
