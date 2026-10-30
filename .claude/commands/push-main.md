---
description: Push dev to main - squash-merge a dev -> main PR whose notes summarise the commits since the last push
disable-model-invocation: true
---

Bring `dev` to `main`: open a PR from `dev` into `main` with short notes summarising the new commits, squash-merge it, and resync `dev`. Releasing is not part of this command: the `Build, Test & Publish` workflow only runs by hand (Run workflow), so the push to `main` starts nothing, and versions are neither checked nor changed here. The squash commits' bodies on `main` keep the notes of each push, so the notes written here matter. Follow these steps in order and stop at the first check that fails, saying why. Stay on `dev` the whole time and never delete it: the same flow runs again next time. Run every git and gh command with the Bash tool from the repository root.

## 1. Checks

Stop and report if any of these fails:

- `git branch --show-current` prints `dev`.
- `git status --porcelain` prints nothing.
- After `git fetch origin`: `git rev-list --count origin/dev..dev` and `git rev-list --count dev..origin/dev` both print `0` (dev is pushed and up to date).
- `gh auth status` succeeds.
- `gh repo view --json deleteBranchOnMerge,squashMergeAllowed` shows `deleteBranchOnMerge` false (otherwise GitHub deletes `dev` on merge: ask the user to turn off "Automatically delete head branches") and `squashMergeAllowed` true.
- `gh api 'repos/{owner}/{repo}' --jq '.squash_merge_commit_title + " " + .squash_merge_commit_message'` prints `PR_TITLE PR_BODY` (otherwise the squash commit would not carry the PR's title and notes: ask the user to set the squash default commit message to "Pull request title and description").
- `git rev-list --count --no-merges <range>` is above `0` (there is something to push), where `<range>` is the new work since the last push, found as below.

**The new work since the last push.** Not `origin/main..dev`: `main` holds only the squash copies of earlier pushes, never `dev`'s own commits, so that range keeps listing every commit pushed before (the resync merge makes `main` reachable from `dev`, not the other way round), and notes written from it repeat earlier pushes. Instead, start at the last resync merge, the merge on `dev` whose second parent is the newest `main` commit it took in (`git merge-base origin/main dev`), so it is found whatever its message:

```
M=$(git merge-base origin/main dev)
BASE=$(git rev-list --first-parent --merges --parents dev | awk -v m="$M" '$3 == m { print $1; exit }')
```

`<range>` is `$BASE..dev`; when `BASE` is empty (`dev` was never resynced, e.g. the first push), it is `origin/main..dev`. Shell variables do not survive between Bash calls: note the value of `BASE` and write it out in later commands.
- `gh pr list --base main --head dev --state open` lists nothing (otherwise report the open PR and stop).

## 2. Title and notes

- Read `git log --no-merges --format='%s%n%b' <range>` (the range from step 1). Merge commits are resyncs, so they are left out.
- Notes, for people who use tauri-plugin-dotnet: at most six bullets of one short line each, about what changed for them (features, fixes, behaviour changes). Leave out tests, refactors, version bumps, CI and internal docs unless they matter to users. Plain markdown bullets: no heading, no commit hashes, no PR numbers.
- Title: one line of at most 60 characters summing the notes up.

## 3. Confirm

Ask with the AskUserQuestion tool: one single-select question, header `Push main`, whose text gives the number of commits in the range and the last line of `git diff --stat origin/main..dev` (a diff compares content, so `origin/main..dev` is right here) and asks how to push. Options, in this order:

- `Push as shown` (Recommended): "Create the PR and squash-merge it with these notes"; preview: the title, a blank line, then the notes.
- `Push without notes`: "Same title, empty notes: this push leaves no notes on main"; preview: the title alone.
- `Cancel`: "Create nothing".

The form also offers "Other", where the user types changes to the title or notes: apply them and ask again with the same question. Only `Push as shown` or `Push without notes` lets you continue (the latter makes the notes empty); `Cancel`, a dismissed form or anything unclear means stop without creating anything. If the tool is unavailable, show the same information as text and continue only on an explicit yes.

## 4. PR and squash merge

Write the notes to a file in your scratchpad directory (never in the repository; an empty file for no notes), then:

- `gh pr create --base main --head dev --title "<title>" --body-file <notes file>`, which prints the PR URL.
- `gh pr merge <PR URL> --squash`. Pass no `--subject` or `--body`: the squash commit takes the PR's title and description (the repository setting checked above), so the PR is the one place the notes live. Do not pass `--delete-branch`.

If the merge fails, report the error and leave the PR open; do not retry with other merge methods.

Then `git fetch origin` and check `git log -1 --format=%B origin/main`: its first line is the title (GitHub may append ` (#<PR number>)`) and the rest is the notes as confirmed (empty for no notes). If it differs, tell the user what the commit holds instead; do not try to rewrite `main`.

## 5. Resync dev

Right after the merge:

- `git fetch origin`
- `git merge -s ours origin/main -m "Sync dev with main"`
- `git push origin dev`

This records the squash commit in `dev`'s history without changing any file, and this merge is where the next push's range starts (step 1). Afterwards `git diff origin/main dev` must print nothing; if it does, report the difference.

## 6. Report

The PR URL, the squash commit on `main`, and that `dev` is synced and checked out. Remind the user that nothing was released: that is the `Build, Test & Publish` workflow, run by hand from the Actions tab.
