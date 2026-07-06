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

import bpy, bmesh, math, os, json, struct
from mathutils import Matrix, Euler, Vector

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
M_BROW   = material("Brow",   "#8B4513", roughness=0.48, coat=0.15)   # worried eyebrows
M_WMOUTH = material("WorryMouth", "#7A3B10", roughness=0.55)          # worried open-oval mouth
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

# Small gentle smile just below the eyes.  (rides bone `face_happy`)
tube("Mouth",
     [(-0.20, -0.98, 0.86), (0.0, -1.02, 0.79), (0.20, -0.98, 0.86)],
     bevel=0.030, mat=M_MOUTH)

# =========================================================================== WORRIED FACE (rides bone `face_worried`)
# Hidden by default (face_worried bone scaled ~0 everywhere except the `worried` clip).
# Two angled eyebrows (inner corners raised = the classic anxious/worried brow) + a small
# dark open-oval "O" mouth. Authored at their true on-face positions so they read correctly
# the instant face_worried scales back to 1.
for side in (-1, 1):
    # thin tapered brow bar just above each eye, tilted so the INNER end lifts (worried)
    br = sphere(f"Brow{side}", (0.33 * side, -0.95, 1.50), (0.185, 0.052, 0.058), M_BROW,
                seg=28, ring=16)
    br.rotation_euler = (0, math.radians(22 * side), 0)   # +Y-rot lifts inner end on +X side
# small downturned/open-oval mouth: a vertical dark ellipsoid = an "o" of concern
sphere("WMouth", (0.0, -1.00, 0.80), (0.105, 0.07, 0.14), M_WMOUTH, seg=32, ring=20)

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

# =========================================================================== ARMATURE + RIG
# Rigid bone-parenting: the mascot is a set of SEPARATE ellipsoid objects (not one skinned
# mesh), so each part is parented to exactly one bone and rides it rigidly. We recompute
# matrix_parent_inverse from the bone's TAIL matrix so nothing jumps from its authored
# rest position. Bones (9 pose bones + 3 scale-only face/star bones = 12):
#   root -> body -> {L_arm, R_arm, tail_base->tail_mid->tail_tip,
#                    face_happy, face_worried, star}, root -> {L_foot, R_foot}
arm_data = bpy.data.armatures.new("PetArmature")
arm = bpy.data.objects.new("PetArmature", arm_data)
coll.objects.link(arm)

for o in bpy.context.view_layer.objects:
    o.select_set(False)
bpy.context.view_layer.objects.active = arm
arm.select_set(True)
try:
    bpy.ops.object.mode_set(mode="EDIT")
except RuntimeError:
    with bpy.context.temp_override(active_object=arm, selected_objects=[arm]):
        bpy.ops.object.mode_set(mode="EDIT")

eb = arm_data.edit_bones
def mkbone(name, head, tail, parent=None):
    b = eb.new(name)
    b.head = head
    b.tail = tail
    if parent is not None:
        b.parent = parent
    return b

b_root  = mkbone("root",      (0.0,  0.0,  0.00), (0.0,  0.0,  0.35))
b_body  = mkbone("body",      (0.0,  0.0,  0.45), (0.0,  0.0,  1.72), b_root)
b_larm  = mkbone("L_arm",     (0.78, -0.14, 0.78), (1.34, -0.24, 0.62), b_body)
b_rarm  = mkbone("R_arm",     (-0.78, -0.14, 0.78), (-1.34, -0.24, 0.62), b_body)
b_tbas  = mkbone("tail_base", (0.0,  0.58, 0.54), (0.0,  0.92, 0.58), b_body)
b_tmid  = mkbone("tail_mid",  (0.0,  0.92, 0.58), (0.0,  1.12, 0.63), b_tbas)
b_ttip  = mkbone("tail_tip",  (0.0,  1.12, 0.63), (0.0,  1.42, 0.74), b_tmid)
b_lfoot = mkbone("L_foot",    (0.40, -0.30, 0.26), (0.40, -0.40, 0.03), b_root)
b_rfoot = mkbone("R_foot",    (-0.40, -0.30, 0.26), (-0.40, -0.40, 0.03), b_root)
# face-swap + star bones (children of body). Scale-only bones: scaling one to ~0 hides all
# of its bone-parented children (exports as glTF node scale). face_happy carries the smile,
# face_worried carries brows+worried mouth, star carries the sparkle gem for its own pulse.
b_fhap  = mkbone("face_happy",   (0.0, -0.60, 0.95), (0.0, -0.98, 0.95), b_body)
b_fwor  = mkbone("face_worried", (0.0, -0.60, 1.12), (0.0, -0.98, 1.12), b_body)
b_star  = mkbone("star",         (0.0, -0.13, 2.34), (0.0, -0.13, 2.70), b_body)

try:
    bpy.ops.object.mode_set(mode="OBJECT")
except RuntimeError:
    with bpy.context.temp_override(active_object=arm, selected_objects=[arm]):
        bpy.ops.object.mode_set(mode="OBJECT")
bpy.context.view_layer.update()

def bone_for(n):
    if n.startswith("Fan"):  return "tail_tip"
    if n == "TailSeg1":      return "tail_base"
    if n == "TailSeg2":      return "tail_mid"
    if n == "TailSeg3":      return "tail_tip"
    if n.startswith(("Arm", "Palm", "Finger")):
        return "R_arm" if n.endswith("-1") else "L_arm"
    if n.startswith("Foot"):
        return "R_foot" if n.endswith("-1") else "L_foot"
    if n == "Mouth":                       return "face_happy"    # the smile
    if n.startswith(("Brow", "WMouth")):   return "face_worried"  # brows + worried "o"
    if n.startswith("Star"):               return "star"          # Star + StarGlow (own pulse)
    return "body"   # Body, Belly, Eye*, Pupil*, Hi*, Blush*, Shoulder*

parts = [o for o in list(coll.objects) if o.type in ("MESH", "CURVE")]
for o in parts:
    bn = bone_for(o.name)
    bone = arm.data.bones[bn]
    o.parent = arm
    o.parent_type = "BONE"
    o.parent_bone = bn
    # a bone-parented child lives in the space of the bone's TAIL; invert that so the
    # part's authored matrix_basis renders exactly where it was placed (no jump).
    tail_mat = bone.matrix_local @ Matrix.Translation((0.0, bone.length, 0.0))
    o.matrix_parent_inverse = (arm.matrix_world @ tail_mat).inverted()

# old root empty is no longer a parent; keep it only as a light-aim target and strip any
# animation off it (all motion now lives on the armature's actions).
root.animation_data_clear()

# print rest-space bone axes (armature==world) so animation signs can be calibrated
for bn in ("root", "body", "L_arm", "R_arm", "L_foot", "R_foot", "tail_base", "tail_tip"):
    b = arm.data.bones[bn]
    print("BONEAXES", bn,
          "X", tuple(round(v, 2) for v in b.x_axis),
          "Y", tuple(round(v, 2) for v in b.y_axis),
          "Z", tuple(round(v, 2) for v in b.z_axis))

# =========================================================================== ANIMATIONS
# Five separate Actions, each stashed to its own NLA track + fake_user so the glTF ACTIONS
# exporter reliably emits one glTF animation per action. Pose bones use XYZ euler; for the
# two vertical bones (root, body): local X = world +X, local Y = world +Z, local Z = world -Y
# (verified from the BONEAXES print) — so rot.x = pitch, rot.y = yaw(spin), rot.z = side-tilt.
arm.animation_data_create()
PB = arm.pose.bones
for pb in PB:
    pb.rotation_mode = "XYZ"
# ---- world-space authoring ----------------------------------------------------------
# Bone-local axes (esp. arms/feet) have non-obvious auto-roll, so we author every key in
# INTUITIVE WORLD space and convert into the bone's rest frame via its rest matrix:
#   rot = (pitch_X, roll_Y, yaw_Z) in DEGREES about world axes, pivoting on the bone head
#   loc = (dx, dy, dz) world-space displacement in Blender units
#   scale stays bone-local (fine for the vertical body: idx1 == height == world Z)
# Sign map (world): +X right, -Y front(camera), +Z up. +pitch leans top toward front,
# +roll tilts top toward +X, +yaw spins CCW-from-top. Arms point outward along +/-X.
def _restrot(pb):
    return pb.bone.matrix_local.to_3x3()

def w2l_rot(pb, deg_xyz):
    Mr = _restrot(pb)
    Rw = Euler([math.radians(a) for a in deg_xyz], "XYZ").to_matrix()
    return (Mr.transposed() @ Rw @ Mr).to_euler("XYZ")

def w2l_loc(pb, vec):
    return _restrot(pb).transposed() @ Vector(vec)

def rest_pose():
    for pb in PB:
        pb.location = (0.0, 0.0, 0.0)
        pb.rotation_euler = (0.0, 0.0, 0.0)
        pb.scale = (1.0, 1.0, 1.0)

def kf(name, frame, loc=None, rot=None, scale=None):
    pb = PB[name]
    if loc is not None:
        pb.location = w2l_loc(pb, loc)
        pb.keyframe_insert("location", frame=frame)
    if rot is not None:
        pb.rotation_euler = w2l_rot(pb, rot)
        pb.keyframe_insert("rotation_euler", frame=frame)
    if scale is not None:
        pb.scale = scale
        pb.keyframe_insert("scale", frame=frame)

_HIDE = (0.001, 0.001, 0.001)
_SHOW = (1.0, 1.0, 1.0)

def face_neutral(f0, f1):
    """Happy smile ON, worried brows/mouth OFF — keyed flat across [f0,f1] so a clip fully
    OWNS the face state regardless of what clip played before it (glTF players keep the last
    value on channels a new clip doesn't touch)."""
    kf("face_happy",   f0, scale=_SHOW); kf("face_happy",   f1, scale=_SHOW)
    kf("face_worried", f0, scale=_HIDE); kf("face_worried", f1, scale=_HIDE)

def star_steady(f0, f1):
    kf("star", f0, scale=_SHOW); kf("star", f1, scale=_SHOW)

def begin(name):
    rest_pose()
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    arm.animation_data.action = act
    return act

def finish(act, start=1):
    for fc in act.fcurves:
        for kpn in fc.keyframe_points:
            kpn.interpolation = "BEZIER"
    tr = arm.animation_data.nla_tracks.new()
    tr.name = act.name
    tr.strips.new(act.name, int(start), act)
    arm.animation_data.action = None

# --- idle (1-48 loop): body bob + breathing squash + tiny arm sway ------------------
act = begin("idle")
kf("body", 1,  loc=(0, 0, 0.0),  scale=(1.00, 1.00, 1.00))
kf("body", 24, loc=(0, 0, 0.05), scale=(1.03, 0.96, 1.03))
kf("body", 48, loc=(0, 0, 0.0),  scale=(1.00, 1.00, 1.00))
# tiny opposite up/down arm float (raise = -Y-rot on L, +Y-rot on R)
kf("L_arm", 1, rot=(0, 0, 0));  kf("L_arm", 24, rot=(0, -8, 0)); kf("L_arm", 48, rot=(0, 0, 0))
kf("R_arm", 1, rot=(0, 0, 0));  kf("R_arm", 24, rot=(0,  8, 0)); kf("R_arm", 48, rot=(0, 0, 0))
face_neutral(1, 48); star_steady(1, 48)
finish(act)

# --- walk (1-28 loop): double body-bob + forward lean, feet alternate, arms swing opp -
act = begin("walk")
for f, y in ((1, 0.0), (7, 0.06), (14, 0.0), (21, 0.06), (28, 0.0)):
    kf("body", f, loc=(0, 0, y), rot=(10, 0, 0))        # constant slight forward lean + bob
# feet: step = lift up (+Z) and swing forward (-Y); planted foot stays at rest
kf("L_foot", 1, loc=(0, 0, 0));  kf("L_foot", 7, loc=(0, -0.10, 0.14)); kf("L_foot", 14, loc=(0, 0, 0)); kf("L_foot", 28, loc=(0, 0, 0))
kf("R_foot", 1, loc=(0, 0, 0));  kf("R_foot", 14, loc=(0, 0, 0)); kf("R_foot", 21, loc=(0, -0.10, 0.14)); kf("R_foot", 28, loc=(0, 0, 0))
# arms swing opposite the legs (forward/back about world Z, i.e. yaw of the arm)
kf("L_arm", 1, rot=(0, 0,  22)); kf("L_arm", 14, rot=(0, 0, -22)); kf("L_arm", 28, rot=(0, 0,  22))
kf("R_arm", 1, rot=(0, 0,  22)); kf("R_arm", 14, rot=(0, 0, -22)); kf("R_arm", 28, rot=(0, 0,  22))
face_neutral(1, 28); star_steady(1, 28)
finish(act)

# --- sleep (1-64 loop): settle lower, side-tilt, slow deep breathing, arms droop -------
act = begin("sleep")
kf("body", 1,  loc=(0, 0, -0.14), rot=(0, 16, 0), scale=(1.00, 1.00, 1.00))
kf("body", 32, loc=(0, 0, -0.17), rot=(0, 16, 0), scale=(1.05, 0.92, 1.05))   # deep inhale
kf("body", 64, loc=(0, 0, -0.14), rot=(0, 16, 0), scale=(1.00, 1.00, 1.00))
# arms droop down along the body (lower = +Y-rot on L, -Y-rot on R)
kf("L_arm", 1, rot=(0,  58, 0)); kf("L_arm", 64, rot=(0,  58, 0))
kf("R_arm", 1, rot=(0, -58, 0)); kf("R_arm", 64, rot=(0, -58, 0))
face_neutral(1, 64); star_steady(1, 64)
finish(act)

# --- celebrate (1-36): crouch -> hop + stretch -> land squash, arms up, root yaw spin --
act = begin("celebrate")
kf("root", 1,  loc=(0, 0, 0.0),   rot=(0, 0, 0))
kf("root", 5,  loc=(0, 0, -0.07), rot=(0, 0, 0))       # crouch
kf("root", 14, loc=(0, 0, 0.42),  rot=(0, 0, 18))      # apex + yaw spin (world Z)
kf("root", 22, loc=(0, 0, 0.0),   rot=(0, 0, 30))      # land
kf("root", 28, loc=(0, 0, -0.05), rot=(0, 0, 30))
kf("root", 36, loc=(0, 0, 0.0),   rot=(0, 0, 30))
kf("body", 1,  scale=(1.00, 1.00, 1.00))
kf("body", 5,  scale=(1.08, 0.86, 1.08))               # anticipation squash
kf("body", 14, scale=(0.90, 1.18, 0.90))               # stretch at apex (star pulses up w/ body)
kf("body", 22, scale=(1.12, 0.84, 1.12))               # land squash
kf("body", 30, scale=(0.98, 1.03, 0.98))
kf("body", 36, scale=(1.00, 1.00, 1.00))
# arms thrown UP (raise = -Y on L, +Y on R)
kf("L_arm", 1, rot=(0, 0, 0)); kf("L_arm", 12, rot=(0, -80, 0)); kf("L_arm", 30, rot=(0, -80, 0)); kf("L_arm", 36, rot=(0, 0, 0))
kf("R_arm", 1, rot=(0, 0, 0)); kf("R_arm", 12, rot=(0,  80, 0)); kf("R_arm", 30, rot=(0,  80, 0)); kf("R_arm", 36, rot=(0, 0, 0))
face_neutral(1, 36)
# star sparkle pulse — its OWN scale channel, peaking at the hop apex, decoupled from the
# body squash/stretch keys above (which pinch the body in on the same frames)
kf("star", 1,  scale=(1.00, 1.00, 1.00))
kf("star", 6,  scale=(1.05, 1.05, 1.05))
kf("star", 14, scale=(1.35, 1.35, 1.35))   # apex sparkle
kf("star", 24, scale=(1.00, 1.00, 1.00))
kf("star", 36, scale=(1.00, 1.00, 1.00))
finish(act)

# --- worried (1-40 loop): pull back + shrink + tilt, arms tuck, fast small tremble -----
act = begin("worried")
kf("body", 1,  scale=(0.90, 0.90, 0.90), rot=(-8, 8, 0))   # lean back + shrink + tilt
kf("body", 40, scale=(0.90, 0.90, 0.90), rot=(-8, 8, 0))
# arms tuck: swing forward (-yaw on L / +yaw on R) and lower a touch
kf("L_arm", 1, rot=(0, 24, -30)); kf("L_arm", 40, rot=(0, 24, -30))
kf("R_arm", 1, rot=(0, -24, 30)); kf("R_arm", 40, rot=(0, -24, 30))
# fast small left/right tremble on the root, pulled back (+Y)
tv = 0.014
for f in range(1, 41, 2):
    s = tv if (f // 2) % 2 == 0 else -tv
    kf("root", f, loc=(s, 0.07, 0.0))
kf("root", 40, loc=(0.0, 0.07, 0.0))
# FACE SWAP: hide the smile, reveal brows + worried "o" mouth for the whole clip (flat keys
# so every rendered/played frame shows the worried face)
kf("face_happy",   1, scale=_HIDE); kf("face_happy",   40, scale=_HIDE)
kf("face_worried", 1, scale=_SHOW); kf("face_worried", 40, scale=_SHOW)
star_steady(1, 40)
finish(act)

# keep a sensible playback range; ACTIONS export uses each action's own frame_range
scene.frame_start, scene.frame_end = 1, 64
arm.animation_data.action = None

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

# beauty shots pose the character at REST: no active action + all NLA tracks muted so the
# stashed clips don't drive the rig. (The authored silhouette == the approved v2 look.)
for t in arm.animation_data.nla_tracks:
    t.mute = True
arm.animation_data.action = None
# rest defaults the face/star bones to scale 1, which would show BOTH faces at once — force
# the happy face and hide the worried set for every beauty/QA still.
PB["face_happy"].scale   = _SHOW
PB["face_worried"].scale = _HIDE
PB["star"].scale         = _SHOW
scene.frame_set(1)
bpy.context.view_layer.update()

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

# =========================================================================== ANIM RENDERS
# Pose the armature to each non-idle action at a representative frame and shoot a QA still
# (front 3/4, same lighting). Drive the pose by assigning the action directly (NLA muted).
def pose_action(act_name, frame):
    for t in arm.animation_data.nla_tracks:
        t.mute = True
    arm.animation_data.action = bpy.data.actions.get(act_name)
    scene.frame_set(frame)

anim_shots = [
    dict(name="claude_pet_anim_walk.png",      act="walk",      frame=7),
    dict(name="claude_pet_anim_sleep.png",     act="sleep",     frame=32),
    # celebrate frame 14 = hop apex: root lifts +0.42 and the star pulses to 1.35, so pull
    # the camera back + aim higher to keep the whole airborne pose AND the pulsing star in frame
    dict(name="claude_pet_anim_celebrate.png", act="celebrate", frame=14,
         cam=(-4.4, -6.4, 3.5), look=(0.0, 0.0, 1.55), lens=48),
    dict(name="claude_pet_anim_worried.png",   act="worried",   frame=20),
]
for a in anim_shots:
    pose_action(a["act"], a["frame"])
    cam.location = a.get("cam", (-3.6, -5.2, 2.6))
    look.location = a.get("look", (0.0, 0.0, 0.92))
    cam_data.lens = a.get("lens", 52)
    scene.render.resolution_x, scene.render.resolution_y = 720, 820
    scene.render.filepath = os.path.join(OUT_DIR, a["name"])
    bpy.ops.render.render(write_still=True)

# restore rig for a clean export: no active action, all NLA tracks live (unmuted)
arm.animation_data.action = None
for t in arm.animation_data.nla_tracks:
    t.mute = False
scene.frame_set(1)

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

# =========================================================================== VERIFY GLB
# Parse the binary glTF ourselves (do NOT trust the export log): 12-byte header, then the
# first chunk (must be JSON), json.loads it, and assert exactly the 5 expected animations.
def glb_json(path):
    with open(path, "rb") as f:
        magic, ver, total = struct.unpack("<III", f.read(12))
        assert magic == 0x46546C67, "not a glTF binary (bad magic)"
        clen, ctype = struct.unpack("<II", f.read(8))
        assert ctype == 0x4E4F534A, "first chunk is not JSON"
        return json.loads(f.read(clen))

gj = glb_json(glb_path)
anim_names = [a.get("name", "") for a in gj.get("animations", [])]
print("GLB_ANIMATIONS", anim_names)
for a in bpy.data.actions:
    fr = a.frame_range
    print("ACTION_RANGE", a.name, int(round(fr[0])), int(round(fr[1])))
expected = {"idle", "walk", "sleep", "celebrate", "worried"}
assert set(anim_names) == expected and len(anim_names) == 5, \
    "ANIM VERIFY FAILED -> got %r (expected %r)" % (anim_names, sorted(expected))
print("GLB_NODES", len(gj.get("nodes", [])), "MESHES", len(gj.get("meshes", [])))

print("GLB_SIZE_MB {:.2f}".format(glb_mb))
print("TRI_COUNT", tri_count)
print("BUILD_PET_DONE", OUT_DIR)
