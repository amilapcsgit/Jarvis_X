"""Run a bounded tool-free modeling conversation against a local inference server."""
import asyncio
import base64
import json
import re
import urllib.request


def run(request, inspect, edit, capture, save, put_parts=None):
    structured = request.get("structured", False)
    def emit(kind, value):
        print(json.dumps({"kind": kind, "value": value}), flush=True)

    def ask(prompt, images, json_output=False):
        message = {"role": "user", "content": prompt}
        if images:
            message["images"] = images
        body = {"model": request["model"], "stream": False, "messages": [message]}
        if structured:
            body["think"] = False
            body["options"] = {"num_predict": 1200}
        if json_output:
            xyz = {"type": "array", "items": {"type": "number"}, "minItems": 3, "maxItems": 3}
            properties = {name: xyz for name in ("location", "size", "rotation", "color")}
            properties.update(name={"type": "string"}, shape={"enum": ["box", "cylinder", "wedge", "mesh"]},
                              bevel={"type": "number"}, vertices={"type": "array", "items": xyz},
                              faces={"type": "array", "items": {"type": "array", "items": {"type": "integer"}}})
            body["format"] = {"type": "object", "required": ["parts"], "properties": {
                "question": {"type": "string"}, "save_name": {"type": "string"},
                "parts": {"type": "array", "maxItems": 24, "items": {"type": "object",
                    "required": ["name", "shape", "location", "size", "color"], "properties": properties}}
            }}
        url = "http://127.0.0.1:11434/api/chat"
        if request["inkling"]:
            url = "http://127.0.0.1:8080/v1/chat/completions"
            message.pop("images", None)
            message["content"] = [{"type": "text", "text": prompt}] + [
                {"type": "image_url", "image_url": {"url": "data:image/png;base64," + data}} for data in images]
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        call = urllib.request.Request(url, json.dumps(body).encode(), {"Content-Type": "application/json"})
        with opener.open(call, timeout=180) as response:
            answer = json.load(response)
        return (answer["choices"][0]["message"] if request["inkling"] else answer["message"])["content"].strip()

    images = []
    if request["reference"] and request["vision"]:
        with open(request["reference"], "rb") as source:
            images.append(base64.b64encode(source.read()).decode())
    initial = inspect()
    brief = request["brief"]
    context = f"Brief: {brief}\nPrevious exchange: {request['history']}\nScene: {initial}"
    instructions = ("Create or refine editable geometry in visible Blender. Preserve unrelated objects. "
                    "Return exactly one fenced python block, or QUESTION followed by a necessary design question. "
                    "Use stable object names. Prefer bpy.data.meshes.new and mesh.from_pydata for custom geometry. For simple primitives use primitive_cube_add(size=1) or primitive_cone_add(vertices=16, radius1=1, radius2=0, depth=2), then read bpy.context.object and set its dimensions. Jarvis handles saves and captures. Never save or clear the scene.\n")
    shape_constraint = re.search(r"\b(?:from|out of)\s+(?:exactly\s+)?(?:[1-9]|one|two|three|four|five|six)\s+(cylinders|boxes|wedges)\b", brief, re.I) if structured else None
    required_shape = {"cylinders": "cylinder", "boxes": "box", "wedges": "wedge"}[shape_constraint[1].lower()] if shape_constraint else None
    if structured:
        instructions = ("Create or refine a Blender asset with editable parts. Return JSON containing either question "
                        "or a parts array. For a design question use an empty parts array and a question. Each part has name, shape (box, cylinder, wedge, or mesh), location, size, "
                        "rotation in degrees, color RGB 0 to 1, optional bevel, and vertices/faces for mesh. "
                        "Use stable names from the previous exchange. Include save_name only when the user requests a save. ")
    if required_shape:
        instructions += f"Every requested part must use shape {required_shape}. "
    repaired = False
    for attempt in range(3):
        answer = ask(instructions + context, images, structured)
        before = inspect()
        script = answer
        save_name = "Jarvis Model"
        try:
            if structured:
                proposal = json.loads(answer)
                if not isinstance(proposal, dict):
                    raise ValueError("Expected a JSON object.")
                if isinstance(proposal.get("question"), str) and proposal["question"].strip():
                    emit("question", proposal["question"])
                    return
                parts = proposal.get("parts")
                if not isinstance(parts, list) or not parts:
                    raise ValueError("Provide at least one editable part.")
                if required_shape and any(not isinstance(part, dict) or part.get("shape") != required_shape for part in parts):
                    raise ValueError(f"Every part must be a {required_shape}, as requested.")
                wants_save = re.search(r"\b(?:save|export)\b|\.blend\b", brief, re.I)
                save_name = (proposal.get("save_name") or "Jarvis Model") if wants_save else None
                if isinstance(save_name, str):
                    save_name = re.sub(r"\.blend$", "", save_name.strip(), flags=re.I)
                if save_name is not None and (not isinstance(save_name, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9 _-]{0,70}", save_name)):
                    raise ValueError("Choose a simple file name.")
                emit("script", script)
                put_parts("Jarvis " + request.get("collection", "Model"), parts)
            else:
                if answer.startswith("QUESTION ") or (answer.endswith("?") and "```" not in answer):
                    emit("question", answer.removeprefix("QUESTION "))
                    return
                blocks = re.findall(r"```(?:python|py)[ \t]*\r?\n(.+?)\r?\n```", answer, re.S | re.I)
                if len(blocks) != 1 or answer.count("```") != 2:
                    raise ValueError("Expected one fenced Python script or a design question. No code dispatched.")
                script = blocks[0]
                emit("script", script)
                edit(script)
        except Exception as error:
            current = inspect()
            if any(word in str(error).lower() for word in ("timeout", "timed out", "connection", "no response")):
                raise RuntimeError(f"Uncertain edit outcome. Scene inspected. {error}") from error
            if repaired:
                raise
            repaired = True
            context += f"\nFailed script: {script}\nRepair this error once: {error}\nCurrent scene: {current}"
            emit("repair", str(error))
            continue
        emit("edited", "Blender executed the script.")
        after = inspect()
        preview = asyncio.run(capture())
        for item in getattr(preview, "content", preview if isinstance(preview, list) else [preview]):
            data = getattr(item, "data", None)
            if data:
                encoded = base64.b64encode(data).decode() if isinstance(data, bytes) else data
                emit("image", "data:image/png;base64," + encoded)
                if request["vision"]:
                    images = images[:int(bool(request["reference"]))] + [encoded]
                break
        else:
            raise RuntimeError("Blender returned no viewport image.")
        verdict = ask(f"{context}\nBefore: {before}\nAfter: {after}\nCompare the requested result with the evidence. "
                      "Reply DONE followed by the verified changes, or REFINE followed by the missing change.", images)
        if verdict.startswith("DONE") and initial != after:
            if save_name:
                verdict += "\n" + save(save_name)["output"]
            emit("done", (verdict.removeprefix("DONE").strip() or "Blender scene updated."))
            return
        context += f"\nCurrent scene: {after}\nRequired refinement: {verdict}"
    raise RuntimeError("Could not verify the requested change within the edit limit.")
