# Agent notes

- No legacy aliases. When a name changes, the old name is removed, never kept for compatibility. Local data is renamed by hand once.
- Find Lazarette with the single rule in Lazarette's docs/INTEGRATION.md (LAZARETTE_PATH, a sibling Lazarette folder, then %USERPROFILE%\Projects\Lazarette) and call only its current function names.
- Commit only as the configured git identity. Don't set user.name or user.email, don't pass --author, and don't add Co-Authored-By or "Generated with" lines or name any tool in commit messages or PR text.
- Windows PowerShell 5.1 syntax in .ps1 files.
