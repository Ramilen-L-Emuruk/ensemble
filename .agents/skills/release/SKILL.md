---
name: release
description: Coordinate an explicitly requested release, merge to the default branch, version update, tag, or push with safe preflight checks.
---

# Release

Use this skill only when the user explicitly requests a release, merge, tag, or push. Finishing implementation is not authorization to release.

1. Read the target repository's release commands, default branch, versioning convention, deployment behavior, and required validation. Stop for clarification if they are not defined.
2. Verify the current branch, a clean working tree, the remote tracking state, intended release candidates, and the latest remote default branch before changing Git state.
3. Present the planned merge, validation, version update, tag, and push steps when the request does not make those operations unambiguous. Obtain confirmation before actions that alter shared history, publish artifacts, or deploy.
4. Merge without rewriting history, run the required integration validation, then perform versioning and tagging only if validation passes.
   - Version commands such as `npm version` require a clean working tree on their own. If the user allowed specific local-only files to stay modified, set aside only those files right before versioning (`git stash push -m "<unique-tag>: <file>" -- <file>`). The stash is shared by every worktree and every agent working in the repository, so never use `git stash pop`: look up your entry's SHA with `git stash list --format='%H %gs'` and stop if no entry carries your tag. After versioning (even if it fails), restore with `git stash apply <sha>`, confirm that `git status --porcelain` matches the state before, then re-find the entry's current `stash@{n}` by its tag and drop it. If the apply conflicts or the status does not match, do not drop; the entry is still there, so report the state. If the SHA printed by `git stash drop` differs from yours, another entry was dropped: read its subject with `git log -1 --format=%s <printed SHA>`, put it back with `git stash store -m "<that subject>" <printed SHA>`, drop your own entry again after re-finding it, and report. Put a separator after the tag (`<unique-tag>:`) when searching, and stop if the search matches no entry or more than one. Never stash without a path.
5. Fetch again immediately before push. If the repository is public, read the outgoing diff, commit messages, and author and committer fields for personal information before pushing, and run any project-provided check without bypassing it. If push or CI fails, do not force-push, recreate version tags, or discard the resulting state. Report the exact state and await direction.
   - If the project has artifacts that a push does not deploy (a server running on another machine, firmware, separately distributed tools), record the remote default branch's commit before pushing; after a push it already points to the new commit, so comparing against it always shows no change. After a successful push, check whether those artifacts (and the shared files and dependency definitions they load) changed since the recorded commit. If they did, ask whether to deploy them now or later; record a deferred deployment as a follow-up task. Before deploying, confirm that the target has no changes that exist only there.
6. Remove only confirmed, merged worktrees (detach Windows junctions first, as in `worktree-workflow`) or branches. Never use forced branch deletion or recursive removal against an unverified path.

Use `release-manager` for release preflight and coordination. Preserve any project-specific release lock only when its mechanism is documented and usable in Codex.
