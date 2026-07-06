extends RefCounted
# (referenced via `const PetLook = preload("res://pet_look.gd")` — no global
# class_name so it works without a rebuilt editor class cache in headless runs.)
# ============================================================================
# Shared VISUAL setup for the Claude pet overlay.
#
# Single source of truth for lights / environment / materials / camera so the
# live overlay (main.gd) and the offscreen QA capture (verify.gd) render an
# IDENTICAL look. This module never touches windowing / roaming / passthrough.
#
# Intent: approach the Blender Cycles beauty render — warm key + cool fill +
# cool rim + warm bounce, soft shadows, gentle ambient, vinyl-figure clearcoat
# sheen, warm subsurface glow in the body, wet glossy eyes, glowing star.
# The glTF importer drops KHR clearcoat (imports clearcoat_enabled=false) and
# never carries subsurface at all, so we re-author both here at runtime.
# ============================================================================

# ---------------------------------------------------------------- environment
static func make_environment() -> WorldEnvironment:
	var env := Environment.new()
	# Transparent-friendly background: the project clear color is (0,0,0,0), so
	# BG_CLEAR_COLOR + viewport.transparent_bg keeps per-pixel transparency.
	env.background_mode = Environment.BG_CLEAR_COLOR
	# Ambient from a flat color (no sky needed) so shadow sides never read black.
	# Cool neutral, mirroring the Blender world (0.62,0.66,0.72).
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	# Warm coral ambient fakes Cycles' warm bounced GI: it deepens/warms the body
	# instead of a cool grey wash that desaturated the coral into milky peach.
	env.ambient_light_color = Color(0.60, 0.47, 0.41)
	env.ambient_light_energy = 0.55
	env.ambient_light_sky_contribution = 0.0
	# Mild Filmic tonemap for a soft filmic roll-off on the highlights. (AgX
	# muddied the warm pastel; Filmic keeps the body bright and saturated.)
	env.tonemap_mode = Environment.TONE_MAPPER_FILMIC
	env.tonemap_exposure = 1.0
	env.tonemap_white = 6.0
	# Subtle contact occlusion in the crevices (under arms, eye sockets, star).
	env.ssao_enabled = true
	env.ssao_radius = 0.35
	env.ssao_intensity = 1.3
	env.ssao_power = 1.5
	env.ssao_detail = 0.5
	env.ssao_light_affect = 0.15
	var we := WorldEnvironment.new()
	we.environment = env
	return we


# --------------------------------------------------------------------- lights
# All directional (scale-independent). A DirectionalLight3D shines along its
# local -Z; the pet faces +Z (toward the camera). Angular distance softens the
# sun-shadow penumbra.
static func make_lights() -> Array:
	var out: Array = []

	# KEY — warm, upper-front-left, the only shadow caster.
	var key := DirectionalLight3D.new()
	key.name = "Key"
	key.rotation_degrees = Vector3(-48, 28, 0)
	key.light_color = Color(1.0, 0.945, 0.86)
	key.light_energy = 2.35
	key.shadow_enabled = true
	key.light_angular_distance = 3.5          # soft penumbra
	key.shadow_bias = 0.04
	key.shadow_normal_bias = 1.5
	key.shadow_blur = 1.4
	out.append(key)

	# FILL — cool, front-right, no shadow, lifts the right/shadow side.
	var fill := DirectionalLight3D.new()
	fill.name = "Fill"
	fill.rotation_degrees = Vector3(-14, -38, 0)
	fill.light_color = Color(0.80, 0.87, 1.0)
	fill.light_energy = 0.55
	fill.shadow_enabled = false
	out.append(fill)

	# RIM — cool, from behind-above, grazes the silhouette for a bright edge.
	var rim := DirectionalLight3D.new()
	rim.name = "Rim"
	rim.rotation_degrees = Vector3(-118, 190, 0)
	rim.light_color = Color(0.90, 0.95, 1.0)
	rim.light_energy = 1.6
	rim.shadow_enabled = false
	out.append(rim)

	# BOUNCE — warm, up from below-front, fakes floor bounce into the belly.
	var bounce := DirectionalLight3D.new()
	bounce.name = "Bounce"
	bounce.rotation_degrees = Vector3(58, 10, 0)
	bounce.light_color = Color(1.0, 0.88, 0.78)
	bounce.light_energy = 0.55
	bounce.shadow_enabled = false
	out.append(bounce)

	return out


# ------------------------------------------------------------------ materials
# Re-author clearcoat + subsurface (both lost on glTF import) per material name.
# One duplicated material per name is shared across all instances that use it.
static func tune_materials(pet: Node) -> void:
	var cache: Dictionary = {}
	_tune_walk(pet, cache)


static func _tune_walk(n: Node, cache: Dictionary) -> void:
	if n is MeshInstance3D:
		var mi := n as MeshInstance3D
		var mesh := mi.mesh
		if mesh:
			for i in mesh.get_surface_count():
				var src := mi.get_active_material(i)
				if src == null or not (src is StandardMaterial3D):
					continue
				var key: String = (src as StandardMaterial3D).resource_name
				if not cache.has(key):
					cache[key] = _make_variant(src as StandardMaterial3D, key)
				if cache[key] != null:
					mi.set_surface_override_material(i, cache[key])
	for c in n.get_children():
		_tune_walk(c, cache)


static func _make_variant(src: StandardMaterial3D, mat_name: String) -> StandardMaterial3D:
	var m := src.duplicate() as StandardMaterial3D
	match mat_name:
		"Body", "Belly":
			_saturate(m, 1.12)            # push the coral back toward the Cycles hero
			_coat(m, 0.35, 0.10)          # glossy vinyl shell
			_sss(m, 0.15)                 # warm subsurface glow (kept low so coral stays saturated)
		"Limb", "LimbDk", "EyeRim":
			_saturate(m, 1.12)
			_coat(m, 0.28, 0.14)
			_sss(m, 0.09)
		"EyeWhite":                       # whites + specular hi-dots: wet gloss
			m.roughness = 0.06
			_coat(m, 1.0, 0.02)
		"Pupil":                          # dark wet pupils
			m.roughness = 0.08
			_coat(m, 1.0, 0.02)
		"Star":                           # keep the glow, add gem sheen
			_coat(m, 0.7, 0.05)
			m.emission_enabled = true
			m.emission_energy_multiplier = max(m.emission_energy_multiplier, 2.6)
		"Blush":                          # leave the soft emissive cheeks as-is
			pass
		"Brow":
			_coat(m, 0.2, 0.2)
		_:
			pass
	return m


static func _saturate(m: StandardMaterial3D, factor: float) -> void:
	var c := m.albedo_color
	m.albedo_color = Color.from_hsv(c.h, min(1.0, c.s * factor), c.v, c.a)


static func _coat(m: StandardMaterial3D, amount: float, rough: float) -> void:
	m.clearcoat_enabled = true
	m.clearcoat = amount
	m.clearcoat_roughness = rough


static func _sss(m: StandardMaterial3D, strength: float) -> void:
	m.subsurf_scatter_enabled = true
	m.subsurf_scatter_strength = strength
	m.subsurf_scatter_skin_mode = false
	m.subsurf_scatter_transmittance_enabled = true
	m.subsurf_scatter_transmittance_color = Color(0.95, 0.45, 0.30)   # warm red-orange
	m.subsurf_scatter_transmittance_depth = 0.22
	m.subsurf_scatter_transmittance_boost = 0.15


# --------------------------------------------------------------------- camera
# Low-ish front-left 3/4 hero framing, robust to the model's real scale via its
# combined AABB. Aims slightly above center so the big face reads well.
static func frame_camera(cam: Camera3D, pet: Node) -> void:
	cam.fov = 34.0
	var aabb := scene_aabb(pet, Transform3D.IDENTITY)
	if aabb.size == Vector3.ZERO:
		cam.position = Vector3(-1.1, 1.2, 3.4)
		cam.look_at(Vector3(0, 0.7, 0), Vector3.UP)
		return
	var center := aabb.position + aabb.size * 0.5
	var look_target := center + Vector3(0.0, aabb.size.y * 0.10, 0.0)
	var extent: float = max(aabb.size.x, aabb.size.y) * 0.5
	var dist: float = (extent / tan(deg_to_rad(cam.fov) * 0.5)) * 1.28 + aabb.size.z * 0.5
	var dir := Vector3(-0.40, 0.24, 0.885).normalized()   # front, left, slightly high
	cam.position = look_target + dir * dist
	cam.look_at(look_target, Vector3.UP)


static func scene_aabb(n: Node, xform: Transform3D) -> AABB:
	var out := AABB()
	var has := false
	var t := xform
	if n is Node3D:
		t = xform * (n as Node3D).transform
	if n is VisualInstance3D:
		var local: AABB = (n as VisualInstance3D).get_aabb()
		out = t * local
		has = true
	for c in n.get_children():
		var sub := scene_aabb(c, t)
		if sub.size != Vector3.ZERO:
			if has:
				out = out.merge(sub)
			else:
				out = sub
				has = true
	return out
