# CLAUDE.md

The project instructions live in the Copilot instructions file; read and follow them:

@.github/copilot-instructions.md

## Builds
Builds, tests and image builds run on the build box, never locally.
Put `bb` in front of the command: `bb dotnet test`, `bb npm run build`.
`bb` copies the working directory there first, so uncommitted changes come along.
