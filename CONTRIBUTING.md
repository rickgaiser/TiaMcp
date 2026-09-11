# Contributing

TiaMcp is licensed AGPL-3.0 (see [LICENSE](LICENSE)). Before any pull request can be merged, you need to sign the [CLA](CLA.md) — it grants the maintainer the rights needed to also offer a separately-licensed commercial edition, on top of your rights under AGPL-3.0.

## Process

1. Open your pull request as normal.
2. The CLA Assistant bot comments with a link to sign.
3. Follow the link and sign electronically (GitHub login only, no account needed elsewhere).
4. Once signed, the bot re-checks automatically — no need to re-sign for future PRs.

## Maintainer setup (one-time)

[.github/workflows/cla.yml](.github/workflows/cla.yml) uses [contributor-assistant/github-action](https://github.com/contributor-assistant/github-action). Before it will work:

1. Replace `<owner>/<repo>` in `cla.yml` with the real GitHub `owner/repo`.
2. Create a GitHub PAT (classic, `repo` scope) and add it as a repository secret named `CLA_ASSISTANT_TOKEN` — the default `GITHUB_TOKEN` isn't sufficient for the bot to write the signatures file back to the repo.
3. Signatures are stored in `signatures/version1/cla.json` on the branch configured in `cla.yml`, committed by the bot itself.
