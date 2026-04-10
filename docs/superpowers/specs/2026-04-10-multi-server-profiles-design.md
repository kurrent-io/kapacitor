# Multi-Server Profiles

Support consultants (and other users) who work across multiple organizations, each with its own Kapacitor server instance.

## Problem

The CLI currently supports a single `server_url` in `~/.config/kapacitor/config.json`. A consultant working for multiple customers must manually reconfigure the CLI each time they switch contexts, risking sessions being sent to the wrong server.

## Design

### Config structure (v2)

The flat v1 config is replaced with a profile-based structure. Each profile holds the full set of config fields. A `profile_bindings` map ties repo paths to profiles for explicit overrides.

```json
{
  "version": 2,
  "active_profile": "default",
  "profiles": {
    "default": {
      "server_url": "https://my-server.example.com",
      "daemon": { "name": "my-machine", "max_agents": 2 },
      "default_visibility": "org_public",
      "update_check": true,
      "excluded_repos": []
    },
    "contoso": {
      "server_url": "https://contoso.kapacitor.io",
      "daemon": { "name": "consulting-box", "max_agents": 1 },
      "default_visibility": "private",
      "update_check": true,
      "excluded_repos": [],
      "remotes": ["github.com/contoso/*", "github.com/contoso-labs/*"]
    }
  },
  "profile_bindings": {
    "/Users/alexey/dev/contoso/new-project": "contoso"
  }
}
```

### Profile resolution chain

The CLI resolves which profile to use via a strict priority chain:

1. `--server-url` CLI flag — bypasses profiles entirely, uses the URL directly
2. `KAPACITOR_URL` env var — bypasses profiles entirely
3. `KAPACITOR_PROFILE` env var — explicit profile name for the shell session
4. `.kapacitor.json` in repo — committed, shared with team (see below)
5. Git remote match against profile `remotes` patterns
6. Repo path match in `profile_bindings` (written by `kapacitor use`)
7. `active_profile` global fallback (defaults to `"default"`)

Steps 1-2 return a server URL without a full profile. Steps 3-7 resolve to a named profile with full config.

`active_profile` is purely a fallback. It does not override repo-level signals (steps 4-6).

### Repo-level config (`.kapacitor.json`)

A file committed to the repo root so team members auto-route to the correct server:

```json
{
  "profile": "contoso",
  "server_url": "https://contoso.kapacitor.io"
}
```

Both fields are stored so the CLI can bootstrap a missing profile in the future.

Resolution logic:
- Local profile with matching name and URL exists: use it
- Profile name exists but URL doesn't match: warn (stale config)
- Profile name doesn't exist: prompt to run setup (future: auto-create from the URL)

### Git remote matching

Profile `remotes` are glob patterns matched against normalized repo remote URLs.

Normalization:
- Strip protocol (`https://`, `git@`, `ssh://`)
- Strip `.git` suffix
- Normalize SSH colon syntax (`github.com:org/repo` becomes `github.com/org/repo`)
- Strip auth tokens/usernames

So `https://github.com/contoso/foo.git`, `git@github.com:contoso/foo`, and `ssh://github.com/contoso/foo.git` all normalize to `github.com/contoso/foo`.

Conflict handling:
- Exactly one profile matches: use it
- Multiple profiles match: error listing the conflicting profiles, suggest `.kapacitor.json` to disambiguate
- No match: fall through to next resolution step

### Commands

- `kapacitor profile add <name> --server-url <url> [--remote "pattern"]` — create a new profile
- `kapacitor profile list` — show all profiles, mark which is active for the current context
- `kapacitor profile remove <name>` — delete a profile (cannot remove `default`)
- `kapacitor profile show [name]` — show effective config for a profile, or the currently resolved profile
- `kapacitor use <name> [--global]` — in a repo: write to `profile_bindings`; outside repo or with `--global`: set `active_profile`
- `kapacitor use <name> --save` — also write `.kapacitor.json` to the repo root

### Backward compatibility & migration

When the CLI reads `config.json`:
- No `version` field: v1 (old flat format). Migrate in-place:
  - Move `server_url`, `daemon`, `default_visibility`, `update_check`, `excluded_repos` into `profiles.default`
  - Set `active_profile: "default"`, `profile_bindings: {}`, `version: 2`
  - Write back
- `version: 2`: use as-is

Code changes:
- Replace `AppConfig.ResolveServerUrl()` with a `ProfileResolver` that walks the resolution chain and returns the full resolved profile
- All existing call sites get identical behavior (resolve to `default` profile since no other profiles exist after migration)

The `setup` command:
- First run (no config): creates v2 config with `default` profile, same interactive flow as today
- Existing v1 config: migrates first, then edits `default` profile

A user who never creates a second profile sees no behavior change.
