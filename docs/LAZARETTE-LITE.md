# Halyard with Lazarette Lite

Halyard can read its GitLab and GitHub tokens from Lazarette Lite instead of
full Lazarette. Point `LAZARETTE_PATH` at the Lite folder (the one containing
`lib\`) in `config\halyard.env`, and create the items Halyard expects in Lite:

- `HALYARD_LAZARETTE_ITEM_GitLab` (for example `gitlab`) becomes `GITLAB_TOKEN`
- `HALYARD_LAZARETTE_ITEM_GitHub` (for example `github`) becomes `GITHUB_TOKEN`

Unset item names are skipped, exactly as with full Lazarette. Halyard reads
secrets by name only; it never uses `-Field`.

## What Lite does not do

- No custom fields, no TOTP, no attachments, no item history. If you need any
  of those, use full Lazarette.
- Lite's unlock is a no-op success: there is no passphrase or authenticator
  gate. Any process running as your Windows user can read the Lite items, so
  the passphrase + TOTP gate full Lazarette gives you is dropped when you fall
  back to Lite.
- One secret per item, at most 2,560 bytes (about 1,280 characters). Larger
  secrets stay in full Lazarette.
- Lite stores items under the `lazarette-lite/` prefix in Windows Credential
  Manager. Items in a full Lazarette vault are not visible to Lite; re-enter
  the secrets in Lite (or import them with Lite's CSV migration) before
  switching `LAZARETTE_PATH`.

## What does not change

- Model calls still go to Fairlead with the `Fairlead/Halyard` key in
  Credential Manager. That key is untouched by this fallback and has no
  environment-variable fallback.
- The item names, the `HALYARD_LAZARETTE_ITEM_*` contract, and the
  `LAZARETTE_*` result codes are unchanged. A Lite-shaped provider is treated
  as unlocked once its unlock call returns true, because Lite has no vault
  state to test; a real vault is still tested with
  `Test-LazaretteVaultUnlocked` exactly as before.