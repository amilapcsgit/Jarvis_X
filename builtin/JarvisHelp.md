# Jarvis Help

## First launch
Choose Chat to talk to Jarvis Built-in. For Blender or coding, choose an installed Ollama model in Models.

## Ollama
Open Settings from the native toolbar. Set the path to Ollama Desktop or the Ollama CLI. Use Open Ollama Desktop and refresh the Models panel. Select a local tag and use Download if the task model is missing.

## Configuring Blender
Open Settings, browse to blender.exe, then choose Save and apply. Select Blender in the workspace, select an Ollama model, and open a project folder.

## Blender execution modes
Auto chooses native tools when the model supports them. Python fallback lets a local chat model write an edit that Jarvis executes through Blender. Native tools uses the Blender tool connector explicitly.

## Approvals and Emergency Stop
Review the tools listed in Allow this chat. Revoke clears the current grant. Emergency Stop terminates Jarvis-owned workers and revokes approvals. Save Blender work before stopping when possible.

## ComfyUI
Enter a local or LAN ComfyUI base URL in Settings and use Test ComfyUI. If the probe fails, confirm the server is running and reachable from this PC.

## Troubleshooting
Check executable paths and runtime logs in Settings. For Ollama port conflicts, check what listens on port 11434. A running shared Ollama instance uses its own model directory.
