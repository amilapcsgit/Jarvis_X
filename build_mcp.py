"""Run named development commands in Jarvis's selected project."""
import json
import os
from pathlib import Path
import shutil
import subprocess

from mcp.server.fastmcp import FastMCP

mcp = FastMCP("Jarvis Build")
scope_file = Path(os.environ["JARVIS_RUN_SCOPE"])

# Each command family has a fixed argv. Project scripts run with the user's privileges.
COMMANDS = {
    "git_status": ("git.exe", "status", "--short"),
    "git_diff": ("git.exe", "--no-pager", "diff", "--no-ext-diff", "--no-textconv", "HEAD", "--"),
    "dotnet_build": ("dotnet.exe", "build", "--no-restore"),
    "dotnet_test": ("dotnet.exe", "test", "--no-restore"),
    "dotnet_run": ("dotnet.exe", "run", "--no-restore"),
    "dotnet_restore": ("dotnet.exe", "restore"),
    "python_test": ("python.exe", "-m", "unittest", "discover"),
    "python_pytest": ("python.exe", "-m", "pytest"),
}
NPM_SCRIPTS = {"npm_build": "build", "npm_test": "test", "npm_start": "start"}


@mcp.tool()
def run(family: str) -> dict:
    """Run a named command in the selected Build project: git_status, git_diff, dotnet_build,
    dotnet_test, dotnet_run, dotnet_restore, python_test, python_pytest, npm_build, npm_test, npm_start."""
    scope = json.loads(scope_file.read_text(encoding="utf-8"))
    if scope.get("Agent") != "build":
        raise RuntimeError("There is no active Build run.")
    project = Path(scope["Project"]).resolve(strict=True)
    command = COMMANDS.get(family)
    if family in NPM_SCRIPTS:
        node = shutil.which("node.exe")
        if not node:
            raise RuntimeError("Node.js is missing.")
        cli = Path(node).parent / "node_modules" / "npm" / "bin" / "npm-cli.js"
        if not cli.is_file():
            raise RuntimeError("Node's npm CLI was not found.")
        command = (node, str(cli), "run", NPM_SCRIPTS[family])
    if not command:
        raise ValueError("Unsupported command family.")
    executable = shutil.which(command[0])
    if not executable:
        raise RuntimeError(f"Required executable is missing: {command[0]}")
    with subprocess.Popen((executable, *command[1:]), cwd=project, stdin=subprocess.DEVNULL,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW) as child:
        try:
            output, _ = child.communicate(timeout=50)
        except subprocess.TimeoutExpired:
            subprocess.run(("taskkill.exe", "/PID", str(child.pid), "/T", "/F"),
                           stdin=subprocess.DEVNULL, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
            raise RuntimeError("Command timed out after 50 seconds. Its process tree was stopped.")
    result = output[-16000:].decode(errors="replace")
    if child.returncode:
        raise RuntimeError(f"{family} exited with code {child.returncode}\n{result}")
    return {"family": family, "exit_code": child.returncode, "output": result}


if __name__ == "__main__":
    mcp.run()
