# Repository Agent Rules & Behavioral Directives

## 1. MANDATORY WORKFLOW DIRECTIVE: QA Audit & Verification
- **Every Code/UI Modification**:
  - Conduct a formal QA Audit & Verification for functional bugs, UI clipping, thread safety, process locking, and disk space safety.
  - Run `dotnet test` to ensure 100% test suite pass before concluding any task.

## 2. MANDATORY USER GUIDE SYNCHRONIZATION
- **Synchronous Guide Updates**:
  - Immediately after any feature update or fix, update `USER_GUIDE.md` in the repository root.
  - ALWAYS copy/publish the updated `USER_GUIDE.md` to `G:\Downloads\Emulator Auto Updater\USER_GUIDE.md`.
  - Maintain clear, structured, user-friendly language in `USER_GUIDE.md`.
