extends SceneTree
## Worker for tools/transcode_textures_astc.py. Reads a job list, re-encodes each block-compressed
## Godot texture (.ctex holding S3TC or BPTC data) as ASTC 4x4, and reports how far the result is
## from the decoded original. It reads and writes only the absolute paths it is given.

const HEADER_BYTES := 36  # magic, version, width, height, flags, mipmap limit, 3 reserved words
const SOURCE_FORMATS := [Image.FORMAT_DXT1, Image.FORMAT_DXT3, Image.FORMAT_DXT5, Image.FORMAT_BPTC_RGBA]


func _initialize() -> void:
	var arguments := OS.get_cmdline_user_args()
	if arguments.size() != 2:
		push_error("usage: -- <jobs.json> <results.jsonl>")
		quit(2)
		return
	var jobs: Array = JSON.parse_string(FileAccess.get_file_as_string(arguments[0]))
	var results := FileAccess.open(arguments[1], FileAccess.WRITE)
	var failed := false
	for job in jobs:
		var result := transcode(job["source"], job["output"])
		result["source"] = job["source"]
		results.store_line(JSON.stringify(result))
		results.flush()
		failed = failed or result.has("error")
	quit(1 if failed else 0)


func transcode(source: String, output: String) -> Dictionary:
	var bytes := FileAccess.get_file_as_bytes(source)
	if bytes.size() < HEADER_BYTES + 16 or bytes.slice(0, 4).get_string_from_ascii() != "GST2" or bytes.decode_u32(4) != 1:
		return {"error": "not a version 1 texture container"}
	var data_format := bytes.decode_u32(HEADER_BYTES)
	var width := bytes.decode_u16(HEADER_BYTES + 4)
	var height := bytes.decode_u16(HEADER_BYTES + 6)
	var mipmaps := bytes.decode_u32(HEADER_BYTES + 8)
	var format := bytes.decode_u32(HEADER_BYTES + 12)
	if data_format != 0 or not SOURCE_FORMATS.has(format):
		return {"error": "unsupported data format %d, image format %d" % [data_format, format]}
	var original := Image.create_from_data(width, height, mipmaps > 0, format, bytes.slice(HEADER_BYTES + 16))
	if original == null or original.is_empty():
		return {"error": "image data does not match its header"}
	var decoded: Image = original.duplicate()
	if decoded.decompress() != OK:
		return {"error": "could not decode the source"}
	var encoded: Image = decoded.duplicate()
	if encoded.compress(Image.COMPRESS_ASTC, Image.COMPRESS_SOURCE_GENERIC, Image.ASTC_FORMAT_4x4) != OK or encoded.get_format() != Image.FORMAT_ASTC_4x4:
		return {"error": "ASTC encoding failed"}
	var check: Image = encoded.duplicate()
	if check.decompress() != OK:
		return {"error": "could not decode the ASTC result"}
	check.convert(decoded.get_format())
	var metrics := decoded.compute_image_metrics(check, false)
	var container := bytes.slice(0, HEADER_BYTES)
	container.resize(HEADER_BYTES + 16)
	container.encode_u32(HEADER_BYTES, 0)
	container.encode_u16(HEADER_BYTES + 4, width)
	container.encode_u16(HEADER_BYTES + 6, height)
	container.encode_u32(HEADER_BYTES + 8, mipmaps)
	container.encode_u32(HEADER_BYTES + 12, Image.FORMAT_ASTC_4x4)
	container.append_array(encoded.get_data())
	var file := FileAccess.open(output, FileAccess.WRITE)
	if file == null:
		return {"error": "cannot write the output"}
	file.store_buffer(container)
	file.close()
	return {"width": width, "height": height, "mipmaps": mipmaps, "sourceFormat": format, "psnr": metrics["peak_snr"],
		"maxDifference": metrics["max"], "bytes": container.size()}
