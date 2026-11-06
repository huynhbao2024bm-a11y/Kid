# KID

KID is a local MCP server for Windows desktop accessibility. An MCP client starts it when needed, and an AI agent can use its tools to inspect and interact with the foreground application. You do not need to open a separate KID window.

KID uses Windows UI Automation. It can only work with controls that an application exposes through that accessibility interface.

## Requirements

- Windows 10 or later
- .NET 10 SDK to build or install from source
- An MCP client that supports local stdio servers

## Install

In PowerShell, from the project folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The installer publishes a self-contained Windows executable to `%LOCALAPPDATA%\Programs\KID` and registers it with detected Codex and Claude Code installations. Restart the client after installation.

To select a client explicitly:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Clients Codex
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Clients Claude
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Clients Both
```

`Both` requires both client command-line tools to be installed. Use `-SkipClientRegistration` to publish KID without registering a client.

The client skill is in `.agents/skills/kid-accessibility/SKILL.md`. The installer copies it for selected clients. Updates retain the previous executable for rollback and take effect when clients reconnect.

## Configure other MCP clients

KID uses the standard MCP stdio transport. For example, add the following to a VS Code `.vscode\mcp.json` file after installing KID:

```json
{
  "servers": {
    "kid": {
      "type": "stdio",
      "command": "${env:LOCALAPPDATA}\\Programs\\KID\\Kid.Mcp.exe",
      "args": []
    }
  }
}
```

Other clients may use a different configuration format. Use the installed executable as the command and configure the server to use stdio.

## Tools

- `project_graph` maps a local source tree to a compact module graph. Call it with an absolute project `root`, optionally a relative `scope`, a `depth` of 1–3, and a `maxChars` budget of 1000–16000.
- `list_open_windows` lists visible application windows.
- `read_window` reads a window without changing focus and returns compact lines with short-lived element references. Use `rootRef`, `skip`, and `includeOffscreen` to inspect a subtree or long list. Each line includes depth to preserve hierarchy. A fresh read invalidates older references for that window.
- `inspect_element` reads one referenced control's supported patterns, state and bounded text. Set `includeGeometry: true` for physical screen bounds and a UI Automation clickable point when available. Omit text with `maxTextLength: 0`.
- `act_on_element` invokes, focuses, selects, expands, collapses, sets text or range values, scrolls, or scrolls an item into view. It verifies the expected foreground window and state where supported. Toggle requests specify the desired boolean state.
- `focus_window` brings a listed window to the foreground.
- `read_active_window` reads the foreground window's accessible controls and exposed text.
- `focus_accessible_element` focuses a control by exact name or automation ID.
- `activate_accessible_element` invokes, selects, toggles, expands, or collapses a control.
- `set_accessible_text` and `set_focused_text` set text in editable controls.
- `press_keys` requires `expectedWindowHandle` and sends supported keys or combinations. It rejects password fields, validates every chord before sending any input, and checks the target between chords. Windows can still change focus between a check and input delivery.

Element searches require an exact match. If the accessibility tree is incomplete or more than one control matches, KID does not choose a control for you.

Prefer references when names repeat or a window has a large tree. References are tied to the observed window, process, runtime identity, name, ID and type. They expire after five minutes and are invalidated by KID mutations in that window. Always verify application results after an invoke or keyboard operation. A reported error may follow a partial action, so inspect before retrying.

Compact reads return at most 300 elements and 32,000 characters, visit at most 3,000 nodes, and stop between provider calls after three seconds. Password subtrees are excluded. URL parameters with common secret names are redacted in names and text. Other private data may still be present. MCP calls are serialized, and diagnostic logging goes to stderr so stdout remains valid protocol data.

The project graph groups files by directory and returns module rows (`M`), static dependency rows (`E`), and scan/output limits. Counts on links represent distinct source files. A zero-count module can be a dependency outside the selected scope. It recognizes .NET project links and common local Python and JavaScript/TypeScript imports. Use a narrower scope if the overview is truncated. The scanner skips hidden entries, links, and common build and dependency folders. It scans selected source types without executing project code or returning source bodies. Git ignore rules and runtime dependencies are outside this scanner's coverage.

## Build

```powershell
dotnet build Kid.slnx
```

To run the server from source for development:

```powershell
dotnet run --project src\Kid.Mcp\Kid.Mcp.csproj
```

The server communicates over standard input and output. Normally, let the MCP client start it.

## Verify on a live application

Use `list_open_windows` and `read_window` on an application you can operate. Try a reversible navigation action by element reference and read the window again to verify the new state. For example, Docker Desktop navigation and its local image filter can be checked without changing Docker resources. Clear the filter and restore the original view afterward.

### Response size on Docker Desktop

One comparison on the same live Images window used a 120-element limit for both calls.

| Read | Response text characters |
| --- | ---: |
| `read_active_window` | 23,368 |
| `read_window` | 7,324 |

The compact response used **68.7% fewer characters**. It used `maxDepth: 12` and `maxTextLength: 160`. Both responses returned 120 elements and reported truncation. The tools can select different elements, and the measurement counts characters. Repeat the comparison on your own target window with the same limits and record sizes, element counts, and truncation flags. The published measurement contains aggregate values only.

## Limitations

- KID works only on Windows and only with applications that expose useful controls through Windows UI Automation.
- Compact reads can inspect a visible background window. Mutations require the intended foreground window. Concurrent work with independent mouse and keyboard input requires an isolated interactive environment, such as a dedicated Windows VM.
- Custom-drawn controls, canvas applications and providers with incomplete accessibility data can expose little or no useful structure. KID reports these limits. Browser DOM tools remain useful for web content.
- The KID skill routes to a separate window capture through Computer Use only after a complete structural read of the target area returns no usable elements or text.
- Traversal deadlines are cooperative. A blocked UI Automation provider call can exceed the deadline. KID does not isolate providers in separate worker processes.
- Authorization for consequential actions comes from the user's request and the calling agent. KID's mechanical guards do not determine user intent or detect every secret.
- Keyboard input is sent through Windows globally. A focus change can occur between the foreground check and the input being sent.
- MCP clients decide when to call KID tools. Registering the server does not guarantee that an agent will choose to use it.

## License

KID is licensed under the MIT License. See [LICENSE](LICENSE).
