import json, shutil, sys
NICHES = [
    {"type": "niche", "name": "Nika_sprcha_1", "wall": "+z", "along": 0.20, "w": 0.30, "h": 0.30, "y": 0.75, "d": 0.10},
    {"type": "niche", "name": "Nika_sprcha_2", "wall": "+z", "along": 0.20, "w": 0.30, "h": 0.30, "y": 1.20, "d": 0.10},
    {"type": "niche", "name": "Nika_sprcha_3", "wall": "+z", "along": 0.20, "w": 0.30, "h": 0.30, "y": 1.65, "d": 0.10},
    {"type": "niche", "name": "Nika_pod_oknem", "wall": "-z", "along": 0.465, "w": 1.943, "h": 0.30, "y": 1.05, "d": 0.10},
]
NOTE = (" Niky v koupelne 3BF3 (hruby otvor ve zdivu, vysky od hrube podlahy): ve sprse 3x 30x30 na stene se dvermi"
        " 20 cm od rohu u sprchy, spodni hrany +75/+120/+165; pod oknem 194,3x30 v liciti s ostenim, spodni hrana +105;"
        " hloubka 10 cm (predpoklad).")
for p in sys.argv[1:]:
    shutil.copyfile(p, p + '.bak9')
    spec = json.load(open(p, encoding='utf-8'))
    room = next(r for r in spec['rooms'] if r['room'].upper().endswith('3BF3'))
    room['items'] = [i for i in room['items'] if i.get('type') != 'niche'] + NICHES
    if 'Niky v koupelne' not in spec.get('note', ''): spec['note'] = spec.get('note', '') + NOTE
    with open(p, 'w', encoding='utf-8') as f: json.dump(spec, f, indent=2, ensure_ascii=False)
    print('updated', p)
