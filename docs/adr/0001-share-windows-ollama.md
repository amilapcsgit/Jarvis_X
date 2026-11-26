# Share the Windows Ollama instance

Jarvis_X uses the Windows user's Ollama instance at 127.0.0.1:11434 so an existing install and model store can be reused. Changing the model folder or RTX 2080 settings affects every client of that instance. The installer previews those changes and requests consent before applying them and restarting the Windows Ollama app. Jarvis changes the model in its conversation state and restarts its private OpenCode process only when new model registration requires it. Each run verifies that the chosen Ollama model is local.
