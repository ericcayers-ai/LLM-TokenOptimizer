# Contributing to TokenOptimizer

Thanks for considering a contribution. This project is a Windows desktop app
(C# / Avalonia) plus a companion VS Code extension. The notes below are
enough to get a change built, tested, and submitted.

## Before you start

- Check open [issues](../../issues) and [pull requests](../../pulls) so you
  are not duplicating work in flight.
- For anything larger than a small fix, open an issue first describing what
  you want to change and why. That saves a rewrite if the approach needs
  adjusting before code gets written.
- By contributing, you agree your changes are licensed under this project's
  [MIT license](LICENSE).

## Development setup

Prerequisites:

- **.NET 10 SDK** - [dotnet.microsoft.com](https://dotnet.microsoft.com/download)
- **Node.js + npm** - only needed for the VS Code extension or MSI build

```powershell
git clone https://github.com/<owner>/LLM-TokenOptimizer.git
cd LLM-TokenOptimizer/app
dotnet build TokenOptimizer.slnx
dotnet run --project src\TokenOptimizer.App
```

## Running tests

```powershell
cd app
dotnet test TokenOptimizer.slnx
```

Add or update tests alongside any behavior change. .NET tests live under
`app/tests/TokenOptimizer.Core.Tests`, `app/tests/TokenOptimizer.Providers.Tests`,
and `app/tests/TokenOptimizer.App.Tests`, mirroring the folder layout of `app/src`.
The VS Code extension has its own suite (`npm test` inside `vscode-extension/`),
and the `freetoken_local` Python package runs offline tests via
`python -m unittest discover -s freetoken_local/tests` from the repo root.

## Code style

- Match the existing style in the file you're editing over any personal
  preference - this codebase favors small, focused types with doc comments
  that explain *why* a design decision was made, not what the code
  obviously does.
- Prefer editing an existing file over creating a new one.
- Keep changes scoped to what the issue or PR describes. Unrelated cleanup
  belongs in its own PR.

## Submitting a pull request

1. Fork the repo and create a branch from `main`.
2. Make your change, with tests where behavior changed.
3. Run `dotnet build TokenOptimizer.slnx` and `dotnet test TokenOptimizer.slnx`
   from `app/` and confirm both are clean.
4. Fill in the pull request template - it asks what changed and how you
   verified it.
5. Link the issue your PR addresses, if one exists.

A maintainer will review and may ask for changes before merging. Please be
patient; this is not a large team.

## Reporting bugs and requesting features

Use the issue templates under **New Issue** - they ask for the information
needed to reproduce a bug or evaluate a feature request without a
back-and-forth.

## Code of Conduct

This project follows a [Code of Conduct](CODE_OF_CONDUCT.md). Participating
means agreeing to abide by it.
