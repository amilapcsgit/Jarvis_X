# Jarvis_X glossary

- **Project session** is the time during which one project is selected in a running Jarvis app.
- **Run** is one prompt and its tool calls, including retries, in the selected mode.
- **Chat grant** authorizes the listed tools for one conversation, project, and mode. Desktop grants also identify the selected window and its process.
- **Command family** is a named development operation such as running .NET tests or an npm build.
- **Checkpoint** is a recoverable copy of the Blender scene from before a run's first edit.
- **Verified output** is a Blender file whose save completed and whose presence Jarvis checked.
- **Codex queue** holds Blender tasks that Codex submitted after the user enabled submissions in Jarvis.
- **Run trace** is the record of authorization, dispatch, execution and verification associated with a run.

- **Model selection** is the local Ollama tag or configured Inkling provider captured at the start of each run.
- **Shared Ollama instance** is the Windows Ollama process on loopback. Its model folder and server settings apply to all clients.
