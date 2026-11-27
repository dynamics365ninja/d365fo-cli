# d365fo-cli

AI-native CLI and MCP server for Dynamics 365 Finance & Operations X++ development.

```sh
npm install -g d365fo-cli
d365fo init       # find PackagesLocalDirectory and write the configuration
d365fo doctor     # what is still missing

d365fo skills install <your X++ repository> --for claude   # or copilot, all
d365fo update     # to the latest release; --check only looks, --to 1.2.3 picks a version
```

An update succeeds while an editor or the daemon still runs the previous version. Restart them
(`d365fo daemon stop`) to move them to the new one.

Two commands are installed: `d365fo` and `d365fo-cli-mcp` (the MCP server).

npm picks the package with the binaries for your system (`win32-x64`, `linux-x64`,
`darwin-x64`, `darwin-arm64`). They are self-contained: no .NET installation is needed. The
Windows package carries the metadata bridge, which uses the .NET Framework 4.8 a D365FO VM
already has. Configuration and the index live outside the package, so an update keeps them.

On Windows, `d365fo` is an npm command shim. An argument with line breaks or `%` does not
survive `cmd.exe`; call it from PowerShell or Git Bash, or pass such text through a file.

Documentation: https://github.com/dynamics365ninja/d365fo-cli
