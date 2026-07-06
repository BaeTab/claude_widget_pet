extends Node3D
# ============================================================================
# Claude Pet Overlay — Phase 1 (Model A window + IPC layer over localhost TCP)
#
# Phase 0 proved the risky windowing tech (see 3D_OVERLAY_DESIGN.md §3):
#   - per-pixel transparent, borderless, always-on-top, no-focus window
#   - mouse passthrough so empty corners click through to the desktop
#   - the WINDOW itself roams (window_set_position) — Model A sidesteps the
#     Windows passthrough render-clipping caveat.
#
# Phase 1 ADDS a thin IPC layer so the WPF "brain" drives the "face":
#   - transport: a localhost TCP socket. WPF listens on 127.0.0.1:<port> and
#     passes <port> to us via the user cmdline arg `--ipc-port=<port>`. We
#     connect as a client (StreamPeerTCP). Chosen over stdin/stdout because
#     Godot 4.7 has no non-blocking stdin read for a windowed process on
#     Windows (OS.read_string_from_stdin() blocks the frame loop). See §4.
#   - protocol: line-delimited JSON, polled non-blocking each frame.
#       WPF -> engine: state / emote / roam / config / say / anchor / shutdown
#       engine -> WPF: hello (handshake) / click / error / bye
#   - robustness: malformed line -> log to stderr + ignore; lost connection ->
#     keep rendering (never crash); no --ipc-port -> standalone spike behavior.
#
# The prior-phase window/roam/passthrough/look logic below is UNCHANGED; the
# IPC layer only ADDS on top of it (and yields animation control to WPF state
# once a `state` command has been received).
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

# --- IPC ----------------------------------------------------------------------
const IPC_VER := "0.1.0"

const PetLook = preload("res://pet_look.gd")   # shared visual setup (lights/env/materials/camera)

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

# --- IPC state ----------------------------------------------------------------
var _ipc_enabled := false        # true only when a --ipc-port arg was given
var _sock: StreamPeerTCP = null
var _ipc_port := 0
var _hello_sent := false
var _ipc_connected := false
var _ipc_buf := PackedByteArray()

# --- WPF-driven expression state ---------------------------------------------
var _clip := {}                  # semantic name -> actual clip name in the glb
var _have_ipc_state := false     # once true, WPF `state` drives the base clip
var _base_state := "idle"        # idle | working | waiting | ended
var _current_base_clip := ""
var _emote_active := false
var _emote_timer := 0.0
var _roam_mode := "wander"       # wander | follow_cursor | stay
var _cfg_scale := 1.0


func _ready() -> void:
	# ---- 1. enforce the transparent / topmost / no-focus window at runtime ----
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_ALWAYS_ON_TOP, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	get_viewport().transparent_bg = true
	DisplayServer.window_set_size(WIN_SIZE)

	# ---- 2. environment + lights (shared look, see pet_look.gd) ---------------
	add_child(PetLook.make_environment())
	for _light in PetLook.make_lights():
		add_child(_light)

	# ---- 3. load the imported glb, play "idle" --------------------------------
	var pet_scene: PackedScene = load("res://assets/claude_pet.glb")
	if pet_scene == null:
		push_error("claude_pet.glb failed to load — was --import run?")
	else:
		var pet := pet_scene.instantiate()
		add_child(pet)
		PetLook.tune_materials(pet)   # re-author clearcoat + subsurface (lost on glTF import)
		_anim = _find_anim(pet)
		if _anim:
			_idle_clip = _match_clip(["idle"])
			_walk_clip = _match_clip(["walk"])
			_build_clip_map()
			printerr("[ipc] ANIM clips: ", _anim.get_animation_list())
			if _idle_clip != "":
				_anim.play(_idle_clip)
		# ---- 4. frame the character with a camera -------------------------
		_cam = Camera3D.new()
		add_child(_cam)
		PetLook.frame_camera(_cam, pet)   # low-ish front-left 3/4 hero framing

	# ---- 5. mouse passthrough: only the footprint ellipse receives clicks -----
	_apply_passthrough()

	# ---- 6. roaming init ------------------------------------------------------
	_usable = DisplayServer.screen_get_usable_rect(DisplayServer.window_get_current_screen())
	_win_pos = Vector2(DisplayServer.window_get_position())
	_win_pos = Vector2(_usable.position) + (Vector2(_usable.size) - Vector2(WIN_SIZE)) * 0.5
	DisplayServer.window_set_position(Vector2i(_win_pos))
	_enter_idle()
	_idle_timer = 0.4   # start wandering almost immediately
	print("READY  usable=", _usable, "  start_pos=", Vector2i(_win_pos))

	# ---- 7. IPC: connect back to the WPF host if a port was passed ------------
	_ipc_setup()


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


# Map semantic animation names to whatever the glb actually shipped (Phase 2
# baked idle/walk/sleep/celebrate/worried). Substring match tolerates prefixes
# like "Armature|idle". Missing clips resolve to "" and gracefully fall back.
func _build_clip_map() -> void:
	_clip = {
		"idle": _match_clip(["idle"]),
		"walk": _match_clip(["walk", "work"]),
		"sleep": _match_clip(["sleep", "sleeps", "nap"]),
		"celebrate": _match_clip(["celebrate", "celebr", "happy", "cheer", "jump"]),
		"worried": _match_clip(["worried", "worry", "sad", "concern"]),
		"greet": _match_clip(["greet", "wave", "hello"]),
		"wake": _match_clip(["wake", "greet", "wave"]),
	}


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
	# Only drive the clip from roaming while WPF has NOT taken over via `state`.
	if not _have_ipc_state and not _emote_active \
			and _anim and _idle_clip != "" and _anim.current_animation != _idle_clip:
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
	if not _have_ipc_state and not _emote_active \
			and _anim and _walk_clip != "" and _anim.current_animation != _walk_clip:
		_anim.play(_walk_clip)


func _process(delta: float) -> void:
	# IPC is polled first so commands apply before this frame's motion/anim.
	_ipc_poll()
	_update_emote(delta)

	match _state:
		State.IDLE:
			if _roam_mode == "stay":
				pass  # planted: no wandering
			elif _roam_mode == "follow_cursor":
				_follow_cursor_step(delta)
			else:  # wander (default / spike behavior)
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


func _follow_cursor_step(delta: float) -> void:
	var mp := Vector2(DisplayServer.mouse_get_position())
	_target = mp - Vector2(WIN_SIZE) * 0.5
	_win_pos = _win_pos.lerp(_target, 1.0 - exp(-MOVE_SPEED * delta))
	DisplayServer.window_set_position(Vector2i(_win_pos))


# --- character click → IPC event ---------------------------------------------
func _input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
		# Passthrough already limits clicks to the footprint, but double-check
		# against the ellipse so we only fire on the pet, not stray edge pixels.
		if _inside_footprint(event.position):
			print("CLICK character")
			_send({"type": "click", "target": "character"})


# ============================================================================
# IPC layer
# ============================================================================
func _ipc_setup() -> void:
	# WPF passes `--ipc-port=<port>` after the `--` separator; it arrives via
	# OS.get_cmdline_user_args(). No arg → standalone spike (IPC disabled).
	for a in OS.get_cmdline_user_args():
		if a.begins_with("--ipc-port="):
			_ipc_port = int(a.substr("--ipc-port=".length()))
		elif a.begins_with("--ipc-port"):
			# tolerate "--ipc-port <n>" split form defensively
			var tail := a.strip_edges()
			if tail.is_valid_int():
				_ipc_port = int(tail)
	if _ipc_port <= 0:
		printerr("[ipc] no --ipc-port arg; running standalone (IPC disabled)")
		return
	_ipc_enabled = true
	_sock = StreamPeerTCP.new()
	var err := _sock.connect_to_host("127.0.0.1", _ipc_port)
	if err != OK:
		printerr("[ipc] connect_to_host failed err=", err, " port=", _ipc_port)
		_ipc_enabled = false
		_sock = null
		return
	printerr("[ipc] connecting to 127.0.0.1:", _ipc_port)


func _ipc_poll() -> void:
	if not _ipc_enabled or _sock == null:
		return
	_sock.poll()
	var st := _sock.get_status()
	if st == StreamPeerTCP.STATUS_CONNECTED:
		if not _hello_sent:
			_hello_sent = true
			_ipc_connected = true
			_sock.set_no_delay(true)
			_send({"type": "hello", "pid": OS.get_process_id(), "ver": IPC_VER})
			printerr("[ipc] connected — hello sent (pid=", OS.get_process_id(), ")")
		var n := _sock.get_available_bytes()
		if n > 0:
			var res := _sock.get_data(n)   # [error, PackedByteArray]
			if res[0] == OK:
				_ipc_buf.append_array(res[1])
				_drain_lines()
			else:
				printerr("[ipc] get_data error=", res[0])
	elif st == StreamPeerTCP.STATUS_ERROR:
		if _ipc_connected:
			printerr("[ipc] connection lost (STATUS_ERROR) — still rendering")
		_ipc_connected = false
		_ipc_enabled = false   # stop polling a dead socket; keep the pet alive
	# STATUS_CONNECTING / STATUS_NONE: wait for the next frame.


func _drain_lines() -> void:
	var start := 0
	var i := 0
	while i < _ipc_buf.size():
		if _ipc_buf[i] == 10:   # '\n'
			var chunk := _ipc_buf.slice(start, i)   # end-exclusive
			_handle_line(chunk.get_string_from_utf8())
			start = i + 1
		i += 1
	if start > 0:
		_ipc_buf = _ipc_buf.slice(start)


func _handle_line(raw: String) -> void:
	var s := raw.strip_edges()
	if s == "":
		return
	var json := JSON.new()
	var perr := json.parse(s)
	if perr != OK:
		printerr("[ipc] malformed line ignored: ", s)
		return
	var d = json.data
	if typeof(d) != TYPE_DICTIONARY:
		printerr("[ipc] non-object line ignored: ", s)
		return
	_handle_command(d)


func _handle_command(d: Dictionary) -> void:
	var t := String(d.get("type", ""))
	match t:
		"state":
			var v := String(d.get("value", "idle"))
			var urgent := bool(d.get("urgent", false))
			_have_ipc_state = true
			_base_state = v
			_play_base()
			printerr("[ipc] applied state=", v, " urgent=", urgent, " clip=", _current_base_clip)
		"emote":
			var v := String(d.get("value", ""))
			_emote(v)
		"roam":
			_roam_mode = String(d.get("mode", "wander"))
			printerr("[ipc] applied roam=", _roam_mode)
		"config":
			_apply_config(d)
		"say":
			# Speech bubble rendering is a Phase 2 item; ack so it's not "unknown".
			printerr("[ipc] say (bubble deferred to Phase 2): ", String(d.get("text", "")))
		"anchor":
			printerr("[ipc] anchor ignored (Model A moves the window itself)")
		"shutdown":
			printerr("[ipc] shutdown received — quitting")
			_send({"type": "bye"})
			get_tree().quit()
		_:
			printerr("[ipc] unknown command type ignored: ", t)


# Base (resting) animation for the current WPF-driven state.
func _play_base() -> void:
	if _emote_active or _anim == null:
		return
	var clip: String = ""
	match _base_state:
		"working": clip = _clip.get("walk", "")
		"waiting": clip = _clip.get("worried", "")
		"ended": clip = _clip.get("sleep", "")
		_: clip = _clip.get("idle", "")
	if clip == "":
		clip = _idle_clip
	_current_base_clip = clip
	if clip != "" and _anim.current_animation != clip:
		_anim.play(clip)


# One-shot expression; returns to the base-state clip when it elapses.
func _emote(v: String) -> void:
	if _anim == null:
		return
	var clip: String = _clip.get(v, "")
	if clip == "":
		printerr("[ipc] emote clip not found: ", v)
		return
	_emote_active = true
	_anim.play(clip)
	var a := _anim.get_animation(clip)
	_emote_timer = a.length if a != null else 1.2
	printerr("[ipc] applied emote=", v, " clip=", clip, " len=", _emote_timer)


func _update_emote(delta: float) -> void:
	if not _emote_active:
		return
	_emote_timer -= delta
	if _emote_timer <= 0.0:
		_emote_active = false
		_play_base()


func _apply_config(d: Dictionary) -> void:
	if d.has("scale"):
		_cfg_scale = float(d["scale"])
	printerr("[ipc] applied config scale=", _cfg_scale,
		" theme=", String(d.get("theme", "")),
		" speech=", bool(d.get("speech", true)))


func _send(msg: Dictionary) -> void:
	if _sock == null or _sock.get_status() != StreamPeerTCP.STATUS_CONNECTED:
		return
	var line := JSON.stringify(msg) + "\n"
	_sock.put_data(line.to_utf8_buffer())
