"""
build_pet.py (v2) — Procedurally builds the Claude Widget 가재(crayfish) mascot in 3D
at collectible-figure quality and exports it as glTF (.glb) for the Godot overlay,
plus beauty preview renders.

v2 upgrades over v1 (character silhouette / layout is UNCHANGED — user-approved):
    - SSS + coat vinyl materials, wet-look glossy eyes (Blender 4.5 Principled inputs)
    - GPU Cycles (OPTIX→CUDA→CPU fallback), 256 samples + denoise, Standard view
    - 4-light studio rig (Key/Fill/Rim/Bounce) + soft-shadow shadow-catcher floor
    - higher poly density spheres (shade smooth)
    - 3D bipyramid "gem" star (was a flat sliver from the side)
    - eye-socket rim + arm-root blend spheres to clean part seams
    - extra face close-up render for eye-quality review

Run headless (set TMP/TEMP=C:\\temp\\gradle-tmp first — antivirus workaround):
    blender --background --python build_pet.py

Outputs (next to this script, in assets/character3d/):
    claude_pet.blend        Blender source
    claude_pet.glb          glTF binary for Godot (mesh + materials + "Idle" node anim)
    claude_pet_hero.png     front 3/4 beauty render (transparent bg + contact shadow)
    claude_pet_front.png    front
    claude_pet_side.png     profile — shows a claw + the raised tail fan + the gem star
    claude_pet_back.png     back 3/4 — shows the tail fan
    claude_pet_face.png     face close-up (eye-quality QA)

Design matches the WPF 2D character (Claude theme):
    body   #D67350 / belly #E8A07E     limbs #CE6A4C / dark #A9502F
    blush  #FFB6C1   eyes white/#241F1F   mouth #8B4513   star #FFD700
"""

import bpy, bmesh, math, os

# ----------------------------------------------------------------------------- setup
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
if not OUT_DIR:
    OUT_DIR = os.getcwd()
os.makedirs(OUT_DIR, exist_ok=True)

# Wipe the default scene (cube/camera/light) so we start clean.
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
coll = scene.collection


def srgb_to_linear(c):
    """Blender stores Base Color in linear space; convert an sRGB 0..1 channel."""
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hexlin(h):
    h = h.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
    return (srgb_to_linear(r), srgb_to_linear(g), srgb_to_linear(b), 1.0)


def material(name, hex_color, roughness=0.5, metallic=0.0,
             coat=0.0, coat_rough=0.03,
             sss=0.0, sss_radius=(1.0, 0.2, 0.1), sss_scale=0.3,
             emit_hex=None, emit_strength=0.0):
    """Principled BSDF with guarded 4.5 input names (Coat/Subsurface/Emission)."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    ins = bsdf.inputs
    ins["Base Color"].default_value = hexlin(hex_color)
    ins["Roughness"].default_value = roughness
    ins["Metallic"].default_value = metallic
    if coat > 0.0:
        if "Coat Weight" in ins:
            ins["Coat Weight"].default_value = coat
        if "Coat Roughness" in ins:
            ins["Coat Roughness"].default_value = coat_rough
    if sss > 0.0:
        if "Subsurface Weight" in ins:
            ins["Subsurface Weight"].default_value = sss
        if "Subsurface Radius" in ins:
            ins["Subsurface Radius"].default_value = sss_radius
        if "Subsurface Scale" in ins:
            ins["Subsurface Scale"].default_value = sss_scale
    if emit_hex is not None and "Emission Color" in ins:
        ins["Emission Color"].default_value = hexlin(emit_hex)
        ins["Emission Strength"].default_value = emit_strength
    return mat


# ------------------------------------------------------------------------- materials
# Body/Belly: vinyl-figure coat + gentle render-only SSS for a soft, alive read.
M_BODY   = material("Body",   "#D67350", roughness=0.40, coat=0.30, coat_rough=0.12,
                    sss=0.10, sss_radius=(0.9, 0.35, 0.2), sss_scale=0.3)
M_BELLY  = material("Belly",  "#E8A07E", roughness=0.40, coat=0.30, coat_rough=0.12,
                    sss=0.10, sss_radius=(0.9, 0.35, 0.2), sss_scale=0.3)
M_LIMB   = material("Limb",   "#CE6A4C", roughness=0.45, coat=0.25)
M_LIMBDK = material("LimbDk", "#A9502F", roughness=0.50, coat=0.20)
M_RIM    = material("EyeRim", "#BE5A40", roughness=0.45, coat=0.20)   # socket eyeliner
M_WHITE  = material("EyeWhite", "#FFFFFF", roughness=0.12, coat=1.0, coat_rough=0.03)
M_PUPIL  = material("Pupil",  "#241F1F", roughness=0.15, coat=1.0, coat_rough=0.02)
M_BLUSH  = material("Blush",  "#FFB6C1", roughness=0.60, emit_hex="#FFB6C1", emit_strength=0.15)
M_MOUTH  = material("Mouth",  "#8B4513", roughness=0.50)
M_STAR   = material("Star",   "#FFD700", roughness=0.22, metallic=0.35,
                    emit_hex="#FFE45A", emit_strength=3.5)

# ------------------------------------------------------------------------------ root
root = bpy.data.objects.new("ClaudePet", None)   # empty; everything parents here
root.empty_display_size = 0.4
coll.objects.link(root)


def parent(obj):
    obj.parent = root
    obj.matrix_parent_inverse = root.matrix_world.inverted()


def sphere(name, loc, scale, mat, seg=48, ring=28):
    """Smooth UV sphere. Big hero parts pass higher seg/ring; tiny parts pass lower."""
    bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=ring, radius=1.0, location=loc)
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = scale
    for p in obj.data.polygons:
        p.use_smooth = True
    obj.data.materials.append(mat)
    parent(obj)
    return obj


def tube(name, pts, bevel, mat, res=12):
    """A smooth tube through 3D control points (mouth smile here)."""
    cu = bpy.data.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    cu.bevel_depth = bevel
    cu.bevel_resolution = 4
    cu.resolution_u = res
    sp = cu.splines.new("BEZIER")
    sp.bezier_points.add(len(pts) - 1)
    for bp, (x, y, z) in zip(sp.bezier_points, pts):
        bp.co = (x, y, z)
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    obj = bpy.data.objects.new(name, cu)
    obj.data.materials.append(mat)
    coll.objects.link(obj)
    parent(obj)
    return obj


def star_gem(name, loc, radius, mat, points=4, inner=0.42, depth=0.10, rot=(0, 0, 0)):
    """3D bipyramid star: the n-point silhouette (XZ plane) pulled to a front (-Y) and
    a back (+Y) apex so it reads as a faceted gem from every angle. Flat-shaded + bevel
    for crisp edge highlights (a smooth bipyramid would look like a blob)."""
    n = points * 2
    verts = []
    for i in range(n):
        ang = math.pi / 2 + i * math.pi / points
        r = radius if i % 2 == 0 else radius * inner
        verts.append((math.cos(ang) * r, 0.0, math.sin(ang) * r))
    fi = len(verts); verts.append((0.0, -depth, 0.0))   # front apex (toward camera, -Y)
    bi = len(verts); verts.append((0.0,  depth, 0.0))   # back apex (+Y)
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append((i, j, fi))
        faces.append((j, i, bi))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    bm = bmesh.new()                      # make normals point outward consistently
    bm.from_mesh(mesh)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    obj.location = loc
    obj.rotation_euler = rot
    obj.data.materials.append(mat)
    coll.objects.link(obj)
    bev = obj.modifiers.new("Bevel", "BEVEL")
    bev.width = 0.015
    bev.segments = 2
    parent(obj)
    return obj


# A cute round chibi blob mascot: big plump body, huge sparkly eyes, rosy cheeks,
# tiny stub arms + little feet, and the Claude sparkle star floating above.
# Keeps the Claude orange palette + star for brand continuity. FRONT = -Y.

# =========================================================================== BODY
# Big round body, a touch taller than wide, bottom resting near the ground.
sphere("Body", (0, 0, 1.08), (1.04, 1.0, 1.12), M_BODY, seg=64, ring=40)
# Lighter tummy patch on the lower front for a soft two-tone belly.
sphere("Belly", (0, -0.66, 0.86), (0.72, 0.36, 0.80), M_BELLY, seg=64, ring=40)

# =========================================================================== FACE
# BIG eyes (cuteness = large eyes with big pupils). The pupil/highlight are flattened
# in Y and embedded into the white so only a shallow cap shows on the eye's curved
# surface — otherwise they read as creepy protruding googly balls.
for side in (-1, 1):
    # socket rim: a slightly larger, darker sphere set just BEHIND the white so only a
    # thin darker ring peeks around the eye — reads as an eye-socket / soft eyeliner.
    sphere(f"EyeRim{side}",   (0.35 * side, -0.78, 1.18), (0.345, 0.28, 0.418), M_RIM, seg=40, ring=24)
    sphere(f"EyeWhite{side}", (0.35 * side, -0.80, 1.18), (0.33, 0.28, 0.40), M_WHITE, seg=56, ring=36)
    # pupil: big & clearly visible, but flattened in Y so its front cap pokes only
    # ~0.05 past the white surface (sits ON the curved eye, not a protruding ball)
    sphere(f"Pupil{side}",    (0.37 * side, -1.00, 1.13), (0.185, 0.13, 0.235), M_PUPIL, seg=48, ring=30)
    # shine dot resting on the upper-outer pupil
    sphere(f"Hi{side}",       (0.45 * side, -1.12, 1.24), (0.052, 0.045, 0.058), M_WHITE, seg=24, ring=14)
    # rosy cheeks
    sphere(f"Blush{side}",    (0.66 * side, -0.82, 0.86), (0.22, 0.11, 0.15), M_BLUSH, seg=28, ring=16)

# Small gentle smile just below the eyes.
tube("Mouth",
     [(-0.20, -0.98, 0.86), (0.0, -1.02, 0.79), (0.20, -0.98, 0.86)],
     bevel=0.030, mat=M_MOUTH)

# =========================================================================== STAR (Claude sparkle)
# 3D bipyramid gem, slightly embedded into the head, jaunty tilt.
star_gem("Star", (0.0, -0.16, 2.34), radius=0.40, mat=M_STAR, points=4,
         depth=0.13, rot=(0, math.radians(8), 0))
# little glow bead under the star
sphere("StarGlow", (0.0, -0.10, 2.34), (0.12, 0.05, 0.12), M_STAR, seg=24, ring=14)

# =========================================================================== ARMS + PINCER CLAWS
for side in (-1, 1):
    # small body-colored blend sphere hides the arm/body seam
    sphere(f"Shoulder{side}", (0.78 * side, -0.14, 0.76), (0.24, 0.22, 0.24), M_BODY, seg=40, ring=24)
    a = sphere(f"Arm{side}", (0.94 * side, -0.16, 0.74), (0.26, 0.22, 0.19), M_LIMB)
    a.rotation_euler = (0, 0, math.radians(-18 * side))
    # open pincer: lower palm (bigger) + upper finger, with a gap between the jaws
    palm = sphere(f"Palm{side}",   (1.26 * side, -0.26, 0.58), (0.23, 0.17, 0.15), M_LIMB)
    palm.rotation_euler = (0, 0, math.radians(-26 * side))
    fing = sphere(f"Finger{side}", (1.33 * side, -0.28, 0.80), (0.16, 0.12, 0.105), M_LIMB)
    fing.rotation_euler = (0, 0, math.radians(-30 * side))

# =========================================================================== LITTLE FEET
for side in (-1, 1):
    sphere(f"Foot{side}", (0.40 * side, -0.34, 0.10), (0.34, 0.44, 0.20), M_LIMB)

# =========================================================================== CRAYFISH TAIL (back = +Y)
# short abdomen segments curling back and slightly UP, ending in a lifted fan.
sphere("TailSeg1", (0.0, 0.82, 0.56), (0.44, 0.30, 0.34), M_BODY, seg=56, ring=32)
sphere("TailSeg2", (0.0, 1.04, 0.60), (0.34, 0.25, 0.28), M_LIMB)
sphere("TailSeg3", (0.0, 1.20, 0.66), (0.27, 0.20, 0.23), M_LIMB)
# fan blades (telson + uropods): paddles splayed around the raised tail tip, tilted up.
for a in (-36, -18, 0, 18, 36):
    r = math.radians(a)
    bx = math.sin(r) * 0.30
    by = 1.34 + math.cos(r) * 0.18
    bl = sphere(f"Fan{a}", (bx, by, 0.72), (0.06, 0.26, 0.20), M_LIMBDK, seg=32, ring=20)
    bl.rotation_euler = (math.radians(-22), 0.0, -r)

# =========================================================================== IDLE ANIMATION
# Gentle whole-body bob + sway, exported as a glTF node animation named "Idle".
scene.frame_start, scene.frame_end = 1, 48
root.rotation_mode = "XYZ"
key = {1: (0.0, 0.0), 24: (0.10, math.radians(2.5)), 48: (0.0, 0.0)}
for f, (dz, rz) in key.items():
    root.location = (0.0, 0.0, dz)
    root.rotation_euler = (0.0, 0.0, rz)
    root.keyframe_insert("location", index=2, frame=f)
    root.keyframe_insert("rotation_euler", index=2, frame=f)
if root.animation_data and root.animation_data.action:
    root.animation_data.action.name = "Idle"
    for fc in root.animation_data.action.fcurves:
        for kp in fc.keyframe_points:
            kp.interpolation = "BEZIER"

# =========================================================================== LIGHTING (render-only)
def area_light(name, loc, energy, size, color=(1, 1, 1)):
    ld = bpy.data.lights.new(name, "AREA")
    ld.energy = energy
    ld.size = size
    ld.color = color
    ob = bpy.data.objects.new(name, ld)
    ob.location = loc
    coll.objects.link(ob)
    tc = ob.constraints.new("TRACK_TO")     # aim at the character
    tc.target = root
    return ob

# warm key (upper-left-front), cool fill (right), rim (upper-back), soft warm bounce (below)
area_light("Key",    (-4.0, -5.0, 6.5), energy=1500, size=7, color=(1.0, 0.94, 0.85))
area_light("Fill",   (5.2, -3.2, 2.6),  energy=400,  size=6, color=(0.85, 0.90, 1.0))
area_light("Rim",    (0.0, 5.5, 6.2),   energy=800,  size=6, color=(0.95, 0.97, 1.0))
area_light("Bounce", (0.0, -2.0, -3.0), energy=150,  size=8, color=(1.0, 0.92, 0.84))

# soft neutral world ambient (bg stays transparent in the PNGs via film_transparent)
world = bpy.data.worlds.new("W")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.62, 0.66, 0.72, 1.0)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.25
scene.world = world

# shadow-catcher floor: catches a soft contact shadow, invisible otherwise. Placed just
# below the lowest geometry (feet ~ z=-0.10) and DELETED before glTF export.
bpy.ops.mesh.primitive_plane_add(size=40, location=(0.0, 0.0, -0.11))
plane = bpy.context.active_object
plane.name = "ShadowCatcher"
plane.is_shadow_catcher = True

# =========================================================================== GPU
def enable_gpu():
    """OPTIX -> CUDA -> HIP/oneAPI -> CPU. Never raises; returns chosen backend name."""
    try:
        cprefs = bpy.context.preferences.addons["cycles"].preferences
    except Exception:
        scene.cycles.device = "CPU"
        return "CPU"
    for backend in ("OPTIX", "CUDA", "HIP", "ONEAPI"):
        try:
            cprefs.compute_device_type = backend
        except Exception:
            continue
        try:
            cprefs.refresh_devices()
        except Exception:
            try:
                cprefs.get_devices()
            except Exception:
                pass
        gpu_devs = [d for d in cprefs.devices if getattr(d, "type", "") == backend]
        if gpu_devs:
            for d in cprefs.devices:
                d.use = (getattr(d, "type", "") != "CPU")
            try:
                scene.cycles.device = "GPU"
                return backend
            except Exception:
                pass
    scene.cycles.device = "CPU"
    return "CPU"

# =========================================================================== CAMERA + RENDER
cam_data = bpy.data.cameras.new("Cam")
cam = bpy.data.objects.new("Cam", cam_data)
coll.objects.link(cam)
scene.camera = cam
look = bpy.data.objects.new("Look", None)
coll.objects.link(look)
ctrack = cam.constraints.new("TRACK_TO")
ctrack.target = look

scene.render.engine = "CYCLES"
backend = enable_gpu()
GPU = (scene.cycles.device == "GPU")
try:
    scene.cycles.samples = 256 if GPU else 96
    scene.cycles.use_denoising = True
except Exception:
    pass
scene.render.film_transparent = True
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "Standard"   # keep pastel purity (no Filmic)
scene.frame_set(1)

print("RENDER_DEVICE", backend, "samples", scene.cycles.samples)

# per-shot camera loc, look target, resolution, lens
shots = [
    dict(name="claude_pet_hero.png",  cam=(-3.6, -5.2, 2.6),  look=(0.0, 0.0, 0.82),  res=(1200, 1400), lens=52),
    dict(name="claude_pet_front.png", cam=(0.0, -6.4, 1.25),  look=(0.0, 0.0, 0.82),  res=(1200, 1400), lens=52),
    dict(name="claude_pet_side.png",  cam=(-6.4, 0.2, 1.7),   look=(0.0, 0.0, 1.02),  res=(1200, 1400), lens=52),
    dict(name="claude_pet_back.png",  cam=(3.4, 4.8, 2.7),    look=(0.0, 0.0, 1.05),  res=(1200, 1400), lens=52),
    dict(name="claude_pet_face.png",  cam=(-1.2, -3.3, 1.55), look=(0.0, -0.7, 1.14), res=(1200, 900),  lens=72),
]
for s in shots:
    cam.location = s["cam"]
    look.location = s["look"]
    cam_data.lens = s["lens"]
    scene.render.resolution_x, scene.render.resolution_y = s["res"]
    scene.render.filepath = os.path.join(OUT_DIR, s["name"])
    bpy.ops.render.render(write_still=True)

# =========================================================================== POLY COUNT
def total_tris():
    dg = bpy.context.evaluated_depsgraph_get()
    tris = 0
    for ob in scene.objects:
        if ob.type != "MESH" or ob.name == "ShadowCatcher":
            continue
        ev = ob.evaluated_get(dg)
        me = ev.to_mesh()
        me.calc_loop_triangles()
        tris += len(me.loop_triangles)
        ev.to_mesh_clear()
    return tris

tri_count = total_tris()

# =========================================================================== EXPORT
# Drop the shadow-catcher so the GLB contains only the character (+ empties).
bpy.data.objects.remove(plane, do_unlink=True)

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT_DIR, "claude_pet.blend"))
glb_path = os.path.join(OUT_DIR, "claude_pet.glb")
bpy.ops.export_scene.gltf(
    filepath=glb_path,
    export_format="GLB",
    export_apply=True,          # bake the Bevel (star) / any modifiers
    export_animations=True,
    export_animation_mode="ACTIONS",
    use_selection=False,
)
glb_mb = os.path.getsize(glb_path) / (1024 * 1024)
print("GLB_SIZE_MB {:.2f}".format(glb_mb))
print("TRI_COUNT", tri_count)
print("BUILD_PET_DONE", OUT_DIR)
