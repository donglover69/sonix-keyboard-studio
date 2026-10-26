# Converts the stock layouts/rgb-keyboard.xml into the app's layout JSON: layout2json.py rgb-keyboard.xml out.json
import sys, re, json, html
xml = open(sys.argv[1], encoding='utf-8').read()
kb = re.search(r'<Keyboard [^>]*width="(\d+)" height="(\d+)"', xml)
keys = []
NAMES = {"PRINT": "PrtSc", "SCROOL": "ScrLk", "pause": "Pause", "insert": "Ins", "home": "Home", "page up": "PgUp", "del": "Del", "end": "End", "page down": "PgDn", "Shift_L": "Shift", "Shift_R": "Shift", "Ctrl_L": "Ctrl", "Ctrl_R": "Ctrl", "Alt_L": "Alt", "Alt_R": "Alt", "Win_L": "Win", "//": "/"}
for m in re.finditer(r'<key code="([^"]+)" name="([^"]*)" desc="[^"]*" key_index="(\d+)" light_index="(\d+)" icon="[^"]*" row_col="([^"]+)" rect_left="(\d+)" rect_top="(\d+)" rect_width="(\d+)" rect_height="(\d+)"', xml):
    code, name, ki, li, rc, x, y, w, h = m.groups()
    name = html.unescape(name)
    keys.append({"name": NAMES.get(name, name), "led": int(li), "code": int(code, 16), "x": int(x), "y": int(y), "w": int(w), "h": int(h)})
out = {"width": int(kb.group(1)), "height": int(kb.group(2)), "keys": keys}
json.dump(out, open(sys.argv[2], 'w', encoding='utf-8'), indent=0, ensure_ascii=False)
print(len(keys), "keys ->", sys.argv[2])
