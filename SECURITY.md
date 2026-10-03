# Security policy

## Reporting a vulnerability

Please do **not** open a public issue for security problems. Use GitHub's private vulnerability reporting
(*Security* tab → *Report a vulnerability*) on this repository. You will get an answer within a week.

Examples of what counts:

- a way to read the stored AES key or the MCP token from outside the user's Windows account;
- the MCP server answering anything other than `127.0.0.1`, or accepting requests without the token;
- a crafted pak, package or project file that makes ScumStudio run code or write outside the chosen folders.

## What ScumStudio never does

- It never contacts a server of its own; the only network listener is the optional MCP server on `127.0.0.1`.
- It never touches the running game or its memory, and never patches the game or server executables.
- It never writes the AES key to a project, a log, a report or a mod.

## Supported versions

Only the latest release receives fixes.
