---
name: release
description: Cut a MeuralManager release - up-front questions, dependency and model check, CHANGELOG/README, test hand-over, then PR, merge, tag and GitHub release.
disable-model-invocation: true
---

# Release

Stops only at: the up-front questions, a major-version question from the dependency check, and the test hand-over. Everything else runs without asking step by step.

## 1. Ask up front (one AskUserQuestion per prompt, then no more)

- List the changes since the last release (`git log <last tag>..HEAD`) and offer them as checkboxes for the release notes (max 4 per question; include "Minor fixes and updates.").
- Version: suggest minor for a new feature, patch for fixes only.
- README: concrete options (features to add, refresh screenshots, leave as is).
- Ideas Inbox: mention open MeuralManager ideas in one line; this release's built ones get ticked and moved to Done.

## 2. Checks

- `dotnet list MeuralManager.slnx package --outdated`, the base images in `Dockerfile`, the Actions in `.github/workflows/docker-publish.yml`, bundled JS under `wwwroot`. Bump patch/minor; ask about majors.
- Model check if the last one (see the model-check memory) was in an earlier month: read the official Anthropic and OpenAI model pages, update `DefaultClaudeModel` / `DefaultOpenAiModel` in `src/MeuralManager.Core/Models/AiSettings.cs` if a cheaper current vision model replaced them or one is retired. Update the memory's "Last check" line.

## 3. Do the work

- No version bump: the git tag is the version.
- Add `## X.Y.Z - YYYY-MM-DD` at the top of `CHANGELOG.md` (`### New` / `### Improved` / `### Fixed`, written for users).
- Update `README.md` as chosen. Public screenshots use demo data only.
- Commit on the release branch after checking staged files for secrets.

## 4. Hand over for testing

Nothing public yet. Stop any instance of mine, say "ready, start it in Visual Studio", list what was and wasn't tested, wait for the OK. Skip only if told to publish untested.

## 5. Publish after the OK

1. Push the branch, `gh pr create` (no session link or Claude Code footer).
2. `gh pr merge <n> --merge --match-head-commit <sha>` as its own command; confirm it merged. If GitHub says the head is out of date right after a push, close and reopen the PR.
3. `git checkout master && git pull --ff-only origin master`.
4. `git tag -a vX.Y.Z -m "X.Y.Z" && git push origin vX.Y.Z` (triggers the Docker publish workflow).
5. Extract the notes: `awk '/^## X\.Y\.Z /{f=1;next} /^## /{f=0} f' CHANGELOG.md > <scratchpad>/notes.md`, then `gh release create vX.Y.Z --title "X.Y.Z" --notes-file <scratchpad>/notes.md`. Title is the bare version.

## 6. Clean up

`git branch -d` merged branches locally and on the remote (never `-D`), `git fetch --prune`, report what was pruned. Update the next-release-plan memory's status line.
