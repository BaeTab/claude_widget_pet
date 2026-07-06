extends Node3D
# ============================================================================
# Offscreen QA capture — renders the SAME PetLook look as the live overlay into
# a PNG so the visual can be judged without the physical desktop. Not shipped.
#
# Transparent viewport (mirrors the real overlay). We save the raw RGBA capture
# AND a version composited over a neutral checker so (a) alpha is provable and
# (b) the pastel colours are judgeable against a mid-grey studio-like ground.
# ============================================================================

const PetLook = preload("res://pet_look.gd")
const OUT_DIR := "C:/temp/gradle-tmp/claude/godot-look/"
const CAP := Vector2i(560, 600)


func _ready() -> void:
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_TRANSPARENT, true)
	DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
	DisplayServer.window_set_size(CAP)
	get_viewport().transparent_bg = true

	add_child(PetLook.make_environment())
	for L in PetLook.make_lights():
		add_child(L)

	var ps: PackedScene = load("res://assets/claude_pet.glb")
	var pet := ps.instantiate()
	add_child(pet)
	PetLook.tune_materials(pet)

	var anim := _find_anim(pet)
	if anim:
		var clip := ""
		for nm in anim.get_animation_list():
			if String(nm).to_lower().find("idle") != -1:
				clip = nm
		if clip != "":
			anim.play(clip)
			anim.seek(0.35, true)

	var cam := Camera3D.new()
	add_child(cam)
	cam.current = true
	PetLook.frame_camera(cam, pet)

	# let shadows / SSAO / SSS resolve
	for i in 12:
		await get_tree().process_frame
	await RenderingServer.frame_post_draw
	await get_tree().process_frame

	var img := get_viewport().get_texture().get_image()
	var w := img.get_width()
	var h := img.get_height()

	# transparency + exposure diagnostics
	var corner := img.get_pixel(3, 3)
	var cen := img.get_pixel(int(w * 0.5), int(h * 0.42))
	print("CAP ", w, "x", h,
		"  corner_alpha=", corner.a,
		"  center_alpha=", cen.a,
		"  center_rgb=(", cen.r, ",", cen.g, ",", cen.b, ")")

	img.save_png(OUT_DIR + "transparent_after.png")

	var bg := _checker(w, h)
	bg.blend_rect(img, Rect2i(0, 0, w, h), Vector2i.ZERO)
	bg.save_png(OUT_DIR + "onbg_after.png")
	print("SAVED ", OUT_DIR, "onbg_after.png")

	get_tree().quit()


func _checker(w: int, h: int) -> Image:
	var img := Image.create(w, h, false, Image.FORMAT_RGBA8)
	var s := 40
	var a := Color(0.62, 0.64, 0.68)
	var b := Color(0.52, 0.54, 0.58)
	for y in h:
		var yb := int(y / s)
		for x in w:
			img.set_pixel(x, y, a if ((int(x / s) + yb) % 2 == 0) else b)
	return img


func _find_anim(n: Node) -> AnimationPlayer:
	if n is AnimationPlayer:
		return n
	for c in n.get_children():
		var r := _find_anim(c)
		if r:
			return r
	return null
