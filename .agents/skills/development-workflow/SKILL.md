---
name: development-workflow
description: Plan and implement a non-trivial repository change with evidence-based scoping, tests, review, and proportionate verification.
---

# Development Workflow

Use this skill for multi-step features, defects, refactors, or configuration changes. Do not use it for a one-line factual answer or a trivial isolated edit.

1. Inspect the repository before choosing an approach. Reuse established patterns, trace callers and configuration when removal or replacement is proposed, and verify library behavior from primary documentation when it matters.
2. For work with multiple phases or architectural impact, make a concise plan with affected files, risks, validation, and any decision that requires user input. Treat the confirmed scope and completion criteria as binding; explain the evidence and obtain confirmation before reducing them. Use `code-explorer`, `implementation-planner`, or `architect` when the work can be independently investigated.
   - Back every number or impact in a proposal or option with a measured value or a `file:line` reference; otherwise mark it as unverified. Do not build options on unconfirmed premises.
   - Before proposing to drop, limit, or narrow a feature (including narrowing what is fetched to save requests), investigate how to keep it. Always include and recommend the design-correct fix; scope, conflicts, and effort may be described but are not reasons to recommend a weaker option.
   - Do not treat an unanswered question or a counter-question as approval. When writing that a user-visible behavior is intentionally excluded, add the date the user approved it; if there is none, record it as undecided. In any list of decisions, quote the user's own words or chosen option for each entry; an entry without one is undecided. Treat excluding, narrowing, or deferring as a decision to ask about, and do not embed unapproved premises in the options.
   - If the change adds or rewords user-facing product text, show the exact text and ask before implementing it, not only when a review finding prompts it.
3. Implement the smallest change that satisfies the confirmed requirement. Add or update tests that demonstrate the intended behavior and relevant failure cases.
4. Run the appropriate build, tests, lint, static checks, and focused reproduction. When user-visible runtime behavior changes, perform a safe behavior-level check as well. State what was run, what was not run, and why.
5. For consequential changes, request read-only review from `code-reviewer`; use `adversarial-reviewer`, `documentation-reviewer`, `java-reviewer`, or `verifier` only when their focus applies. Fix actionable findings, then revalidate the affected paths. Stop once the proportionate review is complete; report residual risks instead of starting an unbounded review loop. Parallelize only independent read-only work.
   - If addressing a review finding would add or reword user-facing product text (UI labels and notes, notifications, error messages, spoken output, user-facing data fields) while the behavior already matches the specification, ask the user before implementing it. Show the exact text and include the option of leaving the text unchanged. If the user declines, treat the finding as a known limitation and record that decision in the commit message; a critical finding must still be resolved another way or escalated to the user.
   - Bug fixes are not covered by this rule: correcting wrong displayed values, typos, or factually wrong fixed text proceeds as part of the fix. Documentation such as README and specifications is also out of scope.
6. Update user-facing documentation, configuration references, and comments when behavior or usage changed.

Do not commit, push, merge, publish, add production dependencies, or make external changes unless the user has authorized that action.
