# Release Tagging

## Trigger & Safety

- Pushing any `v*` tag (`vX.Y.Z`, e.g., `v0.4.0`) triggers `.github/workflows/release.yaml`, which runs CI, builds `standalone` and `lite` Windows x64 executables, and immediately publishes a public GitHub Release (not a draft).
- Never push a `v*` tag or force-update an existing release tag unless the user explicitly asks to publish a release.

## Before Tagging

- Update `<Version>X.Y.Z</Version>` in `WarpscoutGui.csproj` and commit/push before creating the tag.
- Use Conventional Commits (`feat:`, `fix:`, `build:`, `ci:`, `docs:`, `test:`). `docs:` commits are excluded from release notes unless the release has only `docs:` commits.
- Keep Russian and English UI strings up to date in `Strings.cs` — its diff is prioritized first when generating release notes.

## Standard Release (Auto-Generated Bilingual Notes)

Create an annotated tag with a single-line subject and **no body**, then push it:

```bash
git tag -a vX.Y.Z -m "vX.Y.Z"
git push origin vX.Y.Z
```

- Always use annotated tags (`-a`), not lightweight tags.
- Leaving the tag body empty tells `.github/scripts/generate_release_notes.py` to generate RU + EN notes wrapped in `<!-- changelog:ru -->` and `<!-- changelog:en -->` markers (used for in-app update parsing).

## Manual Override

- To bypass AI generation and provide custom notes, add a body paragraph:
  ```bash
  git tag -a vX.Y.Z -m "vX.Y.Z" -m "<notes>"
  ```
- Unmarked manual bodies are wrapped automatically in `<!-- changelog:all --> ... <!-- /changelog:all -->`.
- For bilingual manual notes, wrap each language block explicitly with `<!-- changelog:ru --> ... <!-- /changelog:ru -->` and `<!-- changelog:en --> ... <!-- /changelog:en -->`.
- Do not place `<!-- ... -->` comments inside bullet items.
