extends Node3D
# ============================================================================
# Claude Pet Overlay — Phase 0 spike (Model A: small transparent roaming window)
#
# Proves the risky windowing tech for a 3D desktop-pet overlay on Windows:
#   - per-pixel transparent, borderless, always-on-top, no-focus window
#   - mouse passthrough so empty corners click through to the desktop
#   - the WINDOW itself roams (window_set_position) — Model A avoids the
#     Windows passthrough render-clipping caveat (see 3D_OVERLAY_DESIGN.md §3):
#     on Windows the area OUTSIDE the passthrough polygon is NOT rendered, so a
#     fullscreen overlay would clip effects. A small window that moves sidesteps it.
# ============================================================================

# --- window / footprint tuning ------------------------------------------------
const WIN_SIZE := Vector2i(240, 260)
# Character footprint (window-local px): clicks inside this ellipse hit the pet,
# everything outside passes through to the desktop.
const FOOT_CENTER := Vector2(120.0, 148.0)
const FOOT_RADII  := Vector2(96.0, 104.0)

# --- roaming tuning -----------------------------------------------------------
const MOVE_SPEED := 4.0          # exponential ease rate toward the target
const ARRIVE_DIST := 3.0         # px: considered "arrived"
const IDLE_MIN := 2.0            # s to dwell before wandering again
const IDLE_MAX := 5.0
const TASKBAR_MARGIN := 4        # keep off the very edge

enum State { IDLE, WALK }

var _anim: AnimationPlayer
var _cam: Camera3D
var _win_pos: Vector2           # float mirror of the window's top-left
var _target: Vector2
var _state: int = State.IDLE
var _idle_timer: float = 0.0
var _usable: Rect2i
var _idle_clip := ""
var _walk_clip := ""


func _ready() -> void:
	# ---- 1. enforce the transparent / topmost / no-focus window at runtime ----
	# Project settings request these, but we assert them in code too (the spike's
	# whole point is that these APIs actually take effect on Windows).
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_ALWAYS_ON_TOP, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	get_viewport().transparent_bg = true
	DisplayServer.window_set_size(WIN_SIZE)

	# ---- 2. lights (no WorldEnvironment → background stays transparent) --------
	var key := DirectionalLight3D.new()
	key.rotation_degrees = Vector3(-45, -35, 0)
	key.light_energy = 1.6
	add_child(key)
	var fill := DirectionalLight3D.new()
	fill.rotation_degrees = Vector3(-20, 150, 0)
	fill.light_energy = 0.6
	add_child(fill)
	var rim := OmniLight3D.new()      # soft top fill so the model never reads black
	rim.position = Vector3(0, 3, 2)
	rim.light_energy = 1.0
	rim.omni_range = 12.0
	add_child(rim)

	# ---- 3. load the imported glb, play "idle" --------------------------------
	var pet_scene: PackedScene = load("res://assets/claude_pet.glb")
	if pet_scene == null:
		push_error("claude_pet.glb failed to load — was --import run?")
	else:
		var pet := pet_scene.instantiate()
		add_child(pet)
		_anim = _find_anim(pet)
		if _anim:
			_idle_clip = _match_clip(["idle"])
			_walk_clip = _match_clip(["walk"])
			print("ANIM clips: ", _anim.get_animation_list())
			if _idle_clip != "":
				_anim.play(_idle_clip)
		# ---- 4. frame the character with a camera -------------------------
		_cam = Camera3D.new()
		add_child(_cam)
		_frame(pet)

	# ---- 5. mouse passthrough: only the footprint ellipse receives clicks -----
	# WINDOWS CAVEAT (design §3): the region OUTSIDE this polygon is not rendered
	# on Windows. In Model A the window is tiny and the pet fills it, so this is
	# fine; corners are empty anyway and now click through to the desktop.
	_apply_passthrough()

	# ---- 6. roaming init ------------------------------------------------------
	_usable = DisplayServer.screen_get_usable_rect(DisplayServer.window_get_current_screen())
	_win_pos = Vector2(DisplayServer.window_get_position())
	# start centered-ish on the primary usable area
	_win_pos = Vector2(_usable.position) + (Vector2(_usable.size) - Vector2(WIN_SIZE)) * 0.5
	DisplayServer.window_set_position(Vector2i(_win_pos))
	_enter_idle()
	_idle_timer = 0.4   # spike: start wandering almost immediately
	print("READY  usable=", _usable, "  start_pos=", Vector2i(_win_pos))


# --- helpers -----------------------------------------------------------------
func _find_anim(n: Node) -> AnimationPlayer:
	if n is AnimationPlayer:
		return n
	for c in n.get_children():
		var r := _find_anim(c)
		if r:
			return r
	return null


func _match_clip(keys: Array) -> String:
	if _anim == null:
		return ""
	for name in _anim.get_animation_list():
		var low := String(name).to_lower()
		for k in keys:
			if low.find(k) != -1:
				return name
	return ""


# Fit the camera to the combined AABB of every VisualInstance3D under the model,
# so framing is robust to the model's real (unknown) scale.
func _frame(root: Node) -> void:
	var aabb := _scene_aabb(root, Transform3D.IDENTITY)
	if aabb.size == Vector3.ZERO:
		_cam.position = Vector3(0, 1, 3)
		_cam.look_at(Vector3(0, 0.5, 0), Vector3.UP)
		return
	var center := aabb.position + aabb.size * 0.5
	var extent: float = max(aabb.size.x, aabb.size.y) * 0.5
	var fov_rad := deg_to_rad(_cam.fov)
	var dist: float = (extent / tan(fov_rad * 0.5)) * 1.5 + aabb.size.z
	_cam.position = center + Vector3(0, aabb.size.y * 0.08, dist)
	_cam.look_at(center, Vector3.UP)


func _scene_aabb(n: Node, xform: Transform3D) -> AABB:
	var out := AABB()
	var has := false
	var t := xform
	if n is Node3D:
		t = xform * (n as Node3D).transform
	if n is VisualInstance3D:
		var local: AABB = (n as VisualInstance3D).get_aabb()
		var world := t * local
		out = world
		has = true
	for c in n.get_children():
		var sub := _scene_aabb(c, t)
		if sub.size != Vector3.ZERO:
			if has:
				out = out.merge(sub)
			else:
				out = sub
				has = true
	return out


func _apply_passthrough() -> void:
	var poly := PackedVector2Array()
	var steps := 20
	for i in steps:
		var a := TAU * float(i) / float(steps)
		poly.append(FOOT_CENTER + Vector2(cos(a) * FOOT_RADII.x, sin(a) * FOOT_RADII.y))
	DisplayServer.window_set_mouse_passthrough(poly)


func _inside_footprint(p: Vector2) -> bool:
	var d := (p - FOOT_CENTER)
	return (d.x * d.x) / (FOOT_RADII.x * FOOT_RADII.x) + (d.y * d.y) / (FOOT_RADII.y * FOOT_RADII.y) <= 1.0


# --- roaming state machine ---------------------------------------------------
func _enter_idle() -> void:
	_state = State.IDLE
	_idle_timer = randf_range(IDLE_MIN, IDLE_MAX)
	if _anim and _idle_clip != "" and _anim.current_animation != _idle_clip:
		_anim.play(_idle_clip)


func _enter_walk() -> void:
	_state = State.WALK
	# refresh usable rect (handles taskbar / monitor changes)
	_usable = DisplayServer.screen_get_usable_rect(DisplayServer.window_get_current_screen())
	var min_x := _usable.position.x + TASKBAR_MARGIN
	var min_y := _usable.position.y + TASKBAR_MARGIN
	var max_x := _usable.position.x + _usable.size.x - WIN_SIZE.x - TASKBAR_MARGIN
	var max_y := _usable.position.y + _usable.size.y - WIN_SIZE.y - TASKBAR_MARGIN
	_target = Vector2(randi_range(min_x, max(min_x, max_x)), randi_range(min_y, max(min_y, max_y)))
	if _anim and _walk_clip != "" and _anim.current_animation != _walk_clip:
		_anim.play(_walk_clip)


func _process(delta: float) -> void:
	match _state:
		State.IDLE:
			_idle_timer -= delta
			if _idle_timer <= 0.0:
				_enter_walk()
		State.WALK:
			_win_pos = _win_pos.lerp(_target, 1.0 - exp(-MOVE_SPEED * delta))
			DisplayServer.window_set_position(Vector2i(_win_pos))
			if _win_pos.distance_to(_target) <= ARRIVE_DIST:
				_win_pos = _target
				DisplayServer.window_set_position(Vector2i(_win_pos))
				_enter_idle()


# --- character click (stands in for future IPC "open HUD") -------------------
func _input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		# Passthrough already limits clicks to the footprint, but double-check
		# against the ellipse so we only fire on the pet, not stray edge pixels.
		if _inside_footprint(event.position):
			print("CLICK character")   # → future: emit {"type":"click","target":"character"} over stdout IPC
