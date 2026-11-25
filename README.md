# LocalDesk

LocalDesk is a Windows desktop workspace for local AI models. It connects to Ollama and includes chat, project search, reviewable file and command tools, and model management.

## LocalDesk 1.3 beta

- Press Ctrl+K and choose **Find a model that fits** for benchmark-backed context suggestions or labeled weight-size estimates. Incomplete project runs can suggest a compatible alternative before you approve a handoff.
- **Ask a document folder** searches Markdown, text, DOCX, and PDFs with selectable text. Numbered source cards show the matching page, paragraph, or lines. The app refreshes changed files when you ask again. The document search uses term matching.
- **Model connections** accepts an Ollama LAN address or an OpenAI-compatible text-chat server. The app shows the endpoint that receives prompts. OpenAI-compatible mode has no model-memory telemetry.
- **Project MCP tools** saves a loopback HTTP MCP endpoint per project and prompts before each manually selected tool advertised as read-only. You can inspect its result and put it in your chat draft.
- **Dictate** uses an installed Windows desktop speech recognizer. **Read aloud** uses an installed Windows desktop voice. Use Ctrl+K → **Stop reading aloud** to stop playback.

## Start with the portable beta

Extract the Windows x64 ZIP to a writable folder and open `LocalDesk.exe`. Install [Ollama for Windows](https://ollama.com/download/windows) if needed. Use **Model setup** to select an installed model or download the suggested 2.5 GB starter. The portable package includes .NET and the Cline CLI used by Project agent.

- **Chat** is the default for new installations. Choose **Open a project** to work in a folder, or **Try a sample project** to practice on a separate workspace created by LocalDesk.
- **Review changes and checks** opens the latest project result, with verified files, diffs, and check outcomes. **Hand off to another model** is available after an incomplete project run and requires choosing and confirming a model.
- **Projects** stores a model, instructions, pinned files, checks, and approval preferences for each saved folder. **Settings** controls the theme, chat text size, activity pane, and defaults for new chats.
- **Settings → Advanced and memory budgets** applies optional RAM and VRAM budgets per model. LocalDesk uses Ollama’s reported total model memory and VRAM and treats the difference as estimated RAM. It unloads an over-budget model before inference starts. The check happens after preload, so memory can briefly exceed a budget. Zero disables that budget.

## Build and run

1. Install the .NET 9 SDK and Ollama, then start Ollama.
2. From the repository root, run `dotnet build LocalDesk.csproj`.
3. Run `dotnet run --project LocalDesk.csproj` and choose an installed model. For project work, put the Windows Cline CLI in runtime/cline.exe beside the running executable and choose the project folder in the app.

Licensed under the [MIT License](LICENSE). See [Third-party notices](ThirdPartyNotices.txt) for dependencies.

## Workspace and background runs

- Minimize LocalDesk while a request runs. Windows can notify you when it finishes, needs approval, or has gone three minutes without reported progress. Click the notification to return to the run. Change alerts in **Settings**. Closing LocalDesk stops the active run and saves available recovery information.
- Open **Mini-window** in the sidebar or press **Ctrl+M**. The independent window shows the active phase, elapsed time, generation rate, queued requests, context usage and any waiting approval. It stays available while LocalDesk is minimized. Toggle **Pinned** to change whether it stays above other apps. Use **Open run**, **Review approval**, or **Open result** to return to the relevant chat.
- Choose **Review results** on a project run to see verified changes, diffs, independent checks and output files. **Open in editor** uses Visual Studio Code if installed in a standard location, or Notepad.
- Chat renders Markdown, tables and code blocks. Use **Copy code** or **Jump to latest** while reading a long conversation.
- Press **Ctrl+K** for actions, saved projects, chats, models and the latest result. Reopening LocalDesk restores the selected chat, its saved draft and attachments, and the Activity panel width.
- Use **Queue** beside the message box to save the draft as a separate job with its own project and model profile. Open **Run queue** to start, pause, reorder or remove pending jobs. Removing a job leaves its draft in history. Only one job runs at a time. The queue pauses on an incomplete job and after restarting the app.

## Web research in Project agent

- On an eligible search request, choose **Allow searches in this chat** to remember approval for that conversation. It covers standard Google, Bing and DuckDuckGo search queries. Approve individual page visits separately. Click **Web search allowed · Revoke** beside the project controls to clear the permission.
- Choose **Approve all in this chat** in a tool review to allow future tool calls from that chat, including commands and edits. **Tools approved · Revoke** restores individual confirmations. The permission expires when you exit LocalDesk. Project-branch constraints apply before approval.
- Project agent can make up to five batches of web requests per turn. If it ends without a visible answer, LocalDesk requests a final response once using the existing tool results with tools disabled. If no answer comes back, the result is marked incomplete.

## History, export and Token Guard

- **Browse history** opens searchable chats grouped by date, with pinned chats at the top. **Export** writes the selected conversation to a Markdown file. The command palette can also export the current chat.
- A mint highlight animates below the status while LocalDesk is working. Streaming text refreshes in batches to reduce UI work on long replies.
- Token Guard replaces identical file-read results with a short reference when the earlier result is still in context. Changed content, different read ranges and command results keep their own entries. Enable or disable it in **Settings**. **Run diagnostics** reports the estimated input removed across that run's model requests.
- During a Project agent run, automatic compaction carries forward your original request, acceptance criteria, saved next step and attachment paths alongside the generated notes.

## Acceptance checks, research and performance

- **Run setup** lets you record acceptance criteria, choose a text assertion or a filtered .NET test, and pause before the first proposed edit or command. **Pause here** saves the run. Use **Edit next step** before resuming or handing it off. Results show the requested criteria and the check that actually ran.
- **Projects → Permission presets** can approve supported edits inside that saved source folder and LocalDesk's configured .NET build and test. Arbitrary commands use the chat's normal tool approvals.
- Press **Ctrl+K** for **Context breakdown and compaction preview**. The breakdown estimates the input categories and output reserve. Previewed notes can be edited before they replace older model context.
- **Benchmark GPU context profiles** probes the selected model at chosen context sizes. It records load time, first-token latency for two identical prompts, throughput, and Ollama-reported model VRAM. Use the measurements to choose a default for that model.
- **Search the web and add citations** supports Wikipedia search and general web search using a Brave Search API key for the session. Select results to put URLs and snippets in your draft. Use the source button to open the full page.
- **Search project files and notes** uses a bounded offline index for the chosen source folder. A new search refreshes changed files. Preview the cited lines or add selected excerpts to your draft.

Open LocalDesk.exe. Keep this folder and its runtime subfolder together.

- **Startup:** models load on demand by default. Enable **Load a model at startup** in Settings if preferred. This does not unload a model another Ollama client already has running.
- **Models:** discover installed Ollama models, download a model by its Ollama tag, or import local GGUF files. For vision imports, select the matching projector alongside the model. Downloads show progress and can be cancelled and resumed by retrying. Import with a new name to preserve existing models. Select a model in the library to see what it is best for and whether Ollama reports image input and project tools.
- **New chats:** start in Chat mode unless you select Project agent by default in Settings. Existing chats keep their selected model, mode, and project folder. Project agent requires a folder.
- **Attachments:** drag files into the window or use **+ File**. Project agent receives original file paths, including files outside its working folder. Text/code also gets a saved preview; images are sent visually to compatible models.
- **Project agent:** can edit files and run commands with your Windows account. Ask it to change the files you intend. It does not automatically rebuild a separately packaged application unless requested. It is instructed to narrow searches to relevant files rather than scan your entire Documents folder.
- **Results:** compact green cards show Outcome, Checks and Remaining. Long summaries and earlier response details can be expanded. Existing output files remain linked; paths are available on hover.
- **Stop and time limits:** Stop cancels a request; existing edits remain. Choose a preset, custom limit or No time limit.
- **History:** saved locally in `data/chats.json`; attachments and agent sessions are also in `data`. Updating the program preserves this directory.

Context and output defaults depend on the model. Adjust them under **Profile**. The portable release includes .NET. Ollama is required. Downloading models requires an internet connection and installed models can run offline.

## Updates

Model files are fixed downloads. They do not expire or require routine updates. A newer model is an optional separate download; test it before replacing a working model. Your existing models can keep running offline.

Ollama, the bundled Cline agent and this GUI are separate software components. Ollama can be updated using its official installer. This GUI and its bundled agent have no automatic updater; ask for a rebuilt version when you want changes. Keep a copy of the entire data folder before replacing program files, and retain data when updating. The included source archive allows future changes.

## Verification

On this PC: both GUI model choices loaded successfully; 35B answered a chat request. The 9B backend recognized the attached colored-shape test image in both Chat and Project agent, and Project agent actually read, patched and verified a disposable file. Earlier isolated Achievement Labs style UI tests are described in the accompanying test report. These checks do not guarantee that every autonomous task will finish correctly.
## Generation speed

The footer shows a live estimated output rate (≈), then a final tokens-per-second rate and output token count. Chat uses Ollama's reported generation token count and duration at completion. Agent mode shows the latest model call's estimated generation rate, including generated tool calls, using streamed output timing and server token usage. It excludes initial loading/prompt processing and time spent executing tools. ≈ also marks token counts if the server did not supply usage. Very short agent replies may show no rate because there is too little output to measure.


## Custom token limits and recovery

Use Tokens beside Image to change limits for the current chat. Context accepts 4,096–262,144 tokens; output accepts 256–32,768 and must be less than half the context. These are configurable limits, not a promise that every setting fits GPU/RAM. Defaults are now 32,768 context and 8,192 output. Applying starts a fresh agent session and retains visible chat history. The selected model and limits are now passed to Cline explicitly through ACP. A rejected inference request containing incomplete tool arguments is retried once; partially delivered streams and already executed tools are not replayed. Always select the actual source project folder before making a request.


## Template compatibility and effective context

The bridge now merges system/developer instructions at the beginning and supplies a neutral continuation turn when helper requests contain no ordinary user message. Existing tool-call/result history and image messages are retained. Rejected missing-user requests receive at most one formatting retry, before any tool execution. This handles Qwen's No user query found template guard.

LocalDesk now uses two small runtime model aliases (localdesk-qwen35-9b:current and localdesk-qwen36-35b:current). They share the installed model weights and retain vision support. Their stored num_ctx is updated when Tokens is applied, so OpenAI-compatible agent requests use the selected context rather than silently returning to a base model's 32K default. The original Qwen model tags remain available.


## LocalDesk 1.1 interface

Chat and Activity are separate panes. Drag the vertical divider to resize them. Completed replies have a green summary card; stopped or failed requests use an amber card. New activity and summaries are saved with each chat.

Summary file buttons appear for existing files mentioned with paths or reported by edit tools. Click to reveal the file in Explorer. You can also drag a summary file button into a message.

Drop files onto LocalDesk or use + File. Images keep visual input support. Text/code files up to 1 MB are copied into chat storage and supplied as bounded previews (12,000 characters per file; 24,000 total per message). Up to 10 attachments are accepted per message. Other formats are file references in Project agent mode; they are not automatically decoded in Chat. Image limit remains 20 MB per image. File references point to originals, while text previews are saved snapshots.

LocalDesk now has its own mint-and-charcoal icon in the executable, window and taskbar.


## Longer time limits

The time menu now offers 5, 15, 30, 60, 120 and 240 minutes, Custom (1–1,440 minutes), and No time limit. The choice is saved per chat and applies to the next request. Stop always remains available. The selector is disabled while working so it cannot imply a running timer was changed. Time limits and context/output limits are separate. Reaching a configured timer now reports that reason explicitly.


## Context meter and compaction

The context meter shows the latest request's used tokens out of the selected context capacity, including system instructions, tool definitions and generated output. Server usage is shown when a call completes; ≈ marks estimates before/during generation or imported older usage. Cached prompt tokens are not added again as a separate total.

LocalDesk automatically summarizes older context around 72% of capacity, or earlier to reserve output space. It preserves recent complete tool-call/result groups and reuses carry-forward notes. A rejected context-overflow request gets one compact-and-retry before any response or tool call is delivered. Very large system/tool definitions or a single oversized result may still require smaller output or a larger context.

Compact works while idle. It summarizes the saved agent session where available and starts a fresh working session from those notes. Full visible messages and project files are retained. A Notes counter means only the new carry-forward notes can be estimated yet; system/tool overhead is added on the next request. Summaries are lossy: older image pixels are not resent, so reattach an image if its precise details are needed. Failed/stopped compaction retains original context.


## Project verification and resume

Project agent result cards show the working folder and file changes observed on disk. Expand **Verified changes and diffs** for paths and short text previews. The default Documents workspace checks recently attached files. Explicit project folders also check supported text/source files and executables within a bounded scan. Files mentioned by the model are listed separately.

Use **Build check…** beside the working folder to select a .NET project or solution for this chat. After the agent finishes, LocalDesk runs a Release build and shows the actual exit code, log and any executable outputs it can identify.

When LocalDesk sees several identical tool calls or consecutive tool failures, it pauses and records the reason. **Resume from here** starts a fresh agent session with the request, its attachments and the verified changes from the stopped run.


## Model storage and quick actions

Open **Models > Storage** to choose an empty folder on a local drive. Quit the Ollama tray app first if LocalDesk asks. The destination needs free space for a verified copy. LocalDesk checks each file and starts a temporary Ollama instance to confirm the copied catalog before changing the Windows user model folder. Choose the backup checkbox if you want to retain the old copy after the switch.

Press **Ctrl+K** to find actions or **Ctrl+N** to start a chat. Use **Hide activity** for a wider conversation pane.

## Branch apply, recovery, project context and live memory

Use **Apply branch changes** on a project branch created with this version. Select the text files to apply after reviewing the diff. LocalDesk blocks original files that changed since branching and keeps backups of replaced or removed originals. Branches created earlier have no comparison baseline.

**Resume saved request** appears in a chat with an interrupted project run. LocalDesk rechecks the file baseline and carries forward the last recorded tool and any saved build result. An already finished agent turn can resume directly at unfinished checks. Dismiss clears the saved restart point and retains the chat and project files.

In **Projects**, use **Instructions and pinned files** to save project conventions and reference files. **Memory** beside the context meter shows physical RAM, NVIDIA VRAM, and Ollama model placement estimates.

## Source search, repair and focused verification

Project agent looks for source text and symbol references, including click handlers and Avalonia bindings. It supplies small source excerpts and line numbers as context.

In **Projects**, open **Focused verification** to save exact text that a source file should contain or omit, or a filter for an existing VSTest .NET test project. A filtered test check requires at least one recorded pass. Builds, tests and proposed tool actions continue to require approval.

When an independent check fails, LocalDesk can offer up to two repair attempts. The model receives a short excerpt of the failed check. Choose **Hand off to another model** on the latest incomplete result to send the original request, verified changes and check feedback to another installed model in a fresh agent session.
