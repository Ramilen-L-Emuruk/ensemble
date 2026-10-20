---
name: worktree-workflow
description: Create, use, and clean up Git worktrees for isolated repository changes without losing uncommitted work.
---

# Worktree Workflow

Use this skill when work needs an isolated checkout or when coordinating independent changes. Do not create a worktree merely for a small, single-file edit.

1. Inspect the current Git root, branch, existing worktrees, and working-tree state before creating anything.
2. Create a clearly named branch and worktree with Git. Keep project-local worktrees under `.codex/worktrees/` only when the target repository's `.gitignore` includes that path; otherwise use the repository's documented location. Do not rely on Claude-specific worktree commands or undocumented tool names.
3. Run commands in the intended worktree and report its path and branch when they matter to later steps.
4. Before cleanup, verify the exact worktree-to-branch mapping with `git worktree list`. Remove only the named, clean, merged worktree after confirmation when cleanup affects user work.
   - On Windows, a folder inside the worktree (such as `node_modules`, build output, or a dependency cache) may be a junction to the main checkout. `git worktree remove` follows junctions and deletes the target's contents. On other operating systems this step does not apply. On Windows, remove the worktree only through this PowerShell block, run from the repository root. It stops without removing anything if any step fails:

     ```powershell
     $target = '<worktree path>'
     try {
       if (-not (Test-Path -LiteralPath $target)) {
         "ALREADY-GONE: $target"
       } else {
         $wt = (Resolve-Path -LiteralPath $target -ErrorAction Stop).Path
         if ((Get-Item -LiteralPath $wt -Force -ErrorAction Stop).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
           throw "The worktree root itself is a junction: $wt"
         }
         Get-ChildItem -LiteralPath $wt -Recurse -Force -Directory -Attributes ReparsePoint -ErrorAction Stop |
           ForEach-Object { [System.IO.Directory]::Delete($_.FullName, $false) }
         $left = @(Get-ChildItem -LiteralPath $wt -Recurse -Force -Directory -Attributes ReparsePoint -ErrorAction Stop)
         if ($left.Count -gt 0) {
           "JUNCTION-STILL-THERE (worktree not removed):"; $left.FullName
         } else {
           git worktree remove $wt
           if ($LASTEXITCODE -eq 0) { "WORKTREE-REMOVED: $wt" } else { "WORKTREE-REMOVE-FAILED (exit code $LASTEXITCODE)" }
         }
       }
     } catch {
       "CHECK-FAILED (worktree not removed): $($_.Exception.Message)"
     }
     ```

     - Read the last line of output. The worktree was removed only if it says `WORKTREE-REMOVED`; `ALREADY-GONE` means nothing exists at that path; it is normal only if `git worktree list` no longer shows the worktree either (a mistyped path or running from the wrong directory also prints it). Treat anything else, including no output, as not removed, and report it instead of forcing removal.
     - Directory junctions are found by their ReparsePoint attribute, including dangling ones. Windows PowerShell 5.1 does not descend into junctions while recursing, so no depth limit is needed. File links are left to Git, which removes them safely.
     - `[System.IO.Directory]::Delete(<absolute path>, $false)` removes only the link, not its target. Do not substitute recursive deletion.
     - Afterwards confirm that the main checkout's copy (for example `node_modules/.bin`) still exists.
5. Never use forced removal or branch deletion to bypass uncommitted changes or unmerged commits.
