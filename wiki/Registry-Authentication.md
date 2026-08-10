# Registry Authentication

```
pull access denied for example/private-image, repository does not exist or may require 'docker login'
```

wip recognizes this class of failure and prints:

```
The container registry rejected the request.

Try logging in with:

  wslc registry login -u <username> docker.io
```

## What triggers the hint

Any of these in the failed command's output, case-insensitively:

- `pull access denied`
- `insufficient_scope`
- `authorization failed`

## Fix

```bash
wslc registry login -u <username> docker.io
```

You'll be prompted for a password or token. For other registries, name them explicitly:

```bash
wslc registry login -u <username> ghcr.io
wslc registry login -u AWS <account>.dkr.ecr.<region>.amazonaws.com
```

Then retry:

```bash
wip up -d
```

Note this is `wslc registry login`, **not** `docker login` — a Docker credential store on the same
machine doesn't carry over.

## Use a token, not your password

| Registry | Credential |
|---|---|
| Docker Hub | an access token from Account Settings → Security |
| GitHub Container Registry | a PAT with `read:packages` |
| GitLab | a deploy token or PAT with `read_registry` |
| AWS ECR | `aws ecr get-login-password` |

## When login isn't the problem

`pull access denied` is what registries return for both "you're not authorized" and "it doesn't
exist" — they deliberately don't distinguish, to avoid leaking whether a private repository exists.
So check the obvious things too:

- **Typo in the image name.** `wip config` prints the resolved image; read it.
- **Wrong tag.** The repository exists, that tag doesn't.
- **Wrong registry.** `myapp:dev` means Docker Hub. A GHCR image is `ghcr.io/owner/myapp:dev`.
- **Genuinely no access.** For an org registry, your account may need to be granted it.

## In CI

Log in before any wip command that pulls:

```bash
echo "$REGISTRY_TOKEN" | wslc registry login -u "$REGISTRY_USER" --password-stdin ghcr.io
wip up -d
```

(Use whatever stdin-password flag your `wslc` build supports; avoid putting the token in the
command line, where it lands in process listings and logs.)

See [Using wip in CI](Using-wip-in-CI).

## Related

- [wip up](wip-up)
- [wip build](wip-build)
- [Secret Masking](Secret-Masking)
