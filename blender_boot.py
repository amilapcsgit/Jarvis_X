"""Start the bundled bridge in Jarvis's factory Blender session."""
import os
import sys
from pathlib import Path
import bpy

sys.path.insert(0, str(Path(__file__).parent / 'runtime' / 'blender-scripts' / 'addons'))
import blender_mcp

os.environ['DISABLE_TELEMETRY'] = 'true'
blender_mcp.register()
bpy.context.scene.blendermcp_port = 19876
bpy.context.preferences.view.show_splash = False
