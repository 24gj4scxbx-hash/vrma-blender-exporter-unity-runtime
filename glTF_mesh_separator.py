#!/usr/bin/env python3
"""
glTF Mesh Separator - VRM/glTF Multi-Submesh Splitter (PoC v0.2)
=================================================================
Splits a single glTF/VRM mesh containing N sub-primitives into N
independent meshes. BlendShape (morph targets), bone weights, UVs,
and normals are fully preserved through vertex index remapping.

Typical use case:
    A character face mesh that ships as 1 mesh with 8 submeshes
    -> 8 standalone meshes, each retaining all 117 BlendShapes,
       enabling per-submesh GPU depth sorting and material control.

Usage (CLI):
    python glTF_mesh_separator.py <input.vrm> [output.vrm | --analyze]

If output is omitted, "<input>_separated.<ext>" is written next to
the input file.

License: AGPL-3.0-or-later
"""

import sys, json, struct, copy, os

# glTF component-type constants (per spec).
COMP_SIZE = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
COMP_FMT = {5120: 'b', 5121: 'B', 5122: 'h', 5123: 'H', 5125: 'I', 5126: 'f'}
TYPE_N = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}


def read_glb(path):
    """Read a binary glTF (.glb / .vrm) and return (json_dict, binary_buffer)."""
    with open(path, 'rb') as f:
        assert f.read(4) == b'glTF'
        ver = struct.unpack('<I', f.read(4))[0]
        total = struct.unpack('<I', f.read(4))[0]
        jlen = struct.unpack('<I', f.read(4))[0]; f.read(4)
        gltf = json.loads(f.read(jlen).decode('utf-8'))
        buf = bytearray()
        if f.tell() < total:
            blen = struct.unpack('<I', f.read(4))[0]; f.read(4)
            buf = bytearray(f.read(blen))
    return gltf, buf


def write_glb(path, gltf, buf):
    """Write the JSON + BIN pair back as a binary glTF container."""
    jb = json.dumps(gltf, ensure_ascii=False).encode('utf-8')
    while len(jb) % 4: jb += b' '
    bd = bytes(buf)
    while len(bd) % 4: bd += b'\x00'
    total = 12 + 8 + len(jb) + 8 + len(bd)
    with open(path, 'wb') as f:
        f.write(b'glTF'); f.write(struct.pack('<I', 2)); f.write(struct.pack('<I', total))
        f.write(struct.pack('<I', len(jb))); f.write(b'JSON'); f.write(jb)
        f.write(struct.pack('<I', len(bd))); f.write(b'BIN\x00'); f.write(bd)
    print(f"[SAVED] {path} ({total:,} bytes)")


def read_acc(gltf, buf, ai):
    """Decode an accessor at index `ai` into a list of element-tuples."""
    a = gltf['accessors'][ai]; bv = gltf['bufferViews'][a['bufferView']]
    ct, at, n = a['componentType'], a['type'], a['count']
    nc = TYPE_N[at]; cs = COMP_SIZE[ct]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    stride = bv.get('byteStride', cs * nc); fmt = COMP_FMT[ct]
    r = []
    for i in range(n):
        o = off + i * stride
        e = [struct.unpack_from(f'<{fmt}', buf, o + j * cs)[0] for j in range(nc)]
        r.append(e)
    return r, at, ct


def add_acc(gltf, buf, data, ct, at, minmax=False):
    """Append `data` to the binary buffer and register a new accessor + bufferView."""
    while len(buf) % 4: buf.append(0)
    start = len(buf); fmt = COMP_FMT[ct]; nc = TYPE_N[at]
    for e in data:
        for j in range(nc): buf.extend(struct.pack(f'<{fmt}', e[j]))
    length = len(buf) - start

    bvi = len(gltf['bufferViews'])
    gltf['bufferViews'].append({'buffer': 0, 'byteOffset': start, 'byteLength': length})

    ai = len(gltf['accessors'])
    acc = {'bufferView': bvi, 'componentType': ct, 'count': len(data), 'type': at}
    if minmax and at in ('VEC3', 'VEC2', 'VEC4') and data:
        acc['min'] = [min(d[j] for d in data) for j in range(nc)]
        acc['max'] = [max(d[j] for d in data) for j in range(nc)]
    gltf['accessors'].append(acc)
    return ai


def separate_mesh(gltf, buf, mi):
    """Split mesh `mi` into one mesh per primitive. Returns the new mesh dicts."""
    mesh = gltf['meshes'][mi]; prims = mesh['primitives']
    name = mesh.get('name', f'M{mi}')
    if len(prims) <= 1: return []

    print(f"\n  SEPARATING: '{name}' ({len(prims)} sub)")

    # Read every original vertex attribute once (shared across primitives).
    attrs = prims[0]['attributes']
    adata = {}; ameta = {}
    for an, ai in attrs.items():
        d, at, ct = read_acc(gltf, buf, ai)
        adata[an] = d; ameta[an] = (at, ct)
    vtotal = len(adata['POSITION'])

    # Read BlendShape (morph) targets if present.
    tdata = []; has_t = len(prims[0].get('targets', [])) > 0
    if has_t:
        nt = len(prims[0]['targets'])
        print(f"  Reading {nt} BlendShape targets...")
        for ti, t in enumerate(prims[0]['targets']):
            td = {}
            for an, ai in t.items():
                d, at, ct = read_acc(gltf, buf, ai)
                td[an] = (d, at, ct)
            tdata.append(td)

    tnames = mesh.get('extras', {}).get('targetNames', [])
    results = []

    for pi, p in enumerate(prims):
        mat = p.get('material')
        idx_raw, _, ict = read_acc(gltf, buf, p['indices'])
        indices = [i[0] for i in idx_raw]

        # Build a remap table: only keep vertices actually referenced by this primitive.
        used = sorted(set(indices))
        o2n = {o: n for n, o in enumerate(used)}
        nv = len(used); nt_count = len(indices) // 3

        # Remap vertex attributes.
        new_a = {}
        for an, d in adata.items():
            new_a[an] = [d[o] for o in used]

        # Remap indices into the new vertex space.
        new_idx = [[o2n[i]] for i in indices]

        # Remap BlendShape (morph) data per target.
        new_t = []
        if has_t:
            for td in tdata:
                nt_d = {}
                for an, (d, at, ct) in td.items():
                    nt_d[an] = ([d[o] for o in used], at, ct)
                new_t.append(nt_d)

        # Materialize new accessors for this primitive.
        new_attrs = {}
        for an, d in new_a.items():
            at, ct = ameta[an]
            new_attrs[an] = add_acc(gltf, buf, d, ct, at, minmax=(an == 'POSITION'))

        ict2 = 5123 if nv <= 65535 else 5125
        if nv > 65535: print(f"  [!] Prim {pi}: {nv} verts > 65535 -> 32bit idx")
        ia = add_acc(gltf, buf, new_idx, ict2, 'SCALAR')

        new_ta = []
        for td in new_t:
            ta = {}
            for an, (d, at, ct) in td.items():
                ta[an] = add_acc(gltf, buf, d, ct, at)
            new_ta.append(ta)

        np = {'attributes': new_attrs, 'indices': ia}
        if mat is not None: np['material'] = mat
        if new_ta: np['targets'] = new_ta

        nm = {'name': f"{name}_sub{pi}", 'primitives': [np]}
        if tnames: nm['extras'] = {'targetNames': tnames}
        results.append(nm)

        bs_str = f", {len(new_t)} BS" if new_t else ""
        print(f"  [{pi}] '{nm['name']}' -> {nv}v, {nt_count}t{bs_str}")

    return results


def update_refs(gltf, old_mi, new_start, new_count):
    """Repoint nodes / scene roots / VRM blendShape binds from `old_mi` to the new meshes."""
    # Walk nodes: any node referencing the old mesh is rewired, plus siblings cloned for the rest.
    for ni, n in enumerate(gltf.get('nodes', [])):
        if n.get('mesh') == old_mi:
            n['mesh'] = new_start  # First separated mesh stays in place.
            new_nodes = []
            for i in range(1, new_count):
                nn = copy.deepcopy(n)
                nn['mesh'] = new_start + i
                nn['name'] = gltf['meshes'][new_start + i]['name']
                nni = len(gltf['nodes'])
                gltf['nodes'].append(nn)
                new_nodes.append(nni)
            # Append the cloned nodes to the same parent's children list (or scene roots).
            for pn in gltf.get('nodes', []):
                ch = pn.get('children', [])
                if ni in ch:
                    ch.extend(new_nodes); break
            for sc in gltf.get('scenes', []):
                sn = sc.get('nodes', [])
                if ni in sn:
                    sn.extend(new_nodes)
            break

    # VRM 0.x BlendShape groups: duplicate each bind across the new meshes.
    if 'extensions' in gltf and 'VRM' in gltf['extensions']:
        groups = gltf['extensions']['VRM'].get('blendShapeMaster', {}).get('blendShapeGroups', [])
        for g in groups:
            nb = []
            for b in g.get('binds', []):
                if b.get('mesh') == old_mi:
                    for i in range(new_count):
                        nb2 = copy.deepcopy(b)
                        nb2['mesh'] = new_start + i
                        nb.append(nb2)
                else:
                    nb.append(b)
            g['binds'] = nb


def analyze(gltf):
    """Return [(mesh_index, name, prim_count, max_blendshape_count), ...] for every multi-prim mesh."""
    targets = []
    for mi, m in enumerate(gltf.get('meshes', [])):
        ps = m.get('primitives', [])
        if len(ps) > 1:
            bs = max(len(p.get('targets', [])) for p in ps)
            targets.append((mi, m.get('name', ''), len(ps), bs))
    return targets


def main():
    if len(sys.argv) < 2:
        print("Usage: glTF_mesh_separator.py <input.vrm> [output.vrm | --analyze]")
        print("       (relative paths are resolved from the script's own folder)")
        return

    # Resolve paths relative to the script location so the .bat shortcut works
    # without the user setting a working directory.
    script_dir = os.path.dirname(os.path.abspath(__file__))

    inp = sys.argv[1]
    if not os.path.isabs(inp):
        inp = os.path.join(script_dir, inp)

    aonly = '--analyze' in sys.argv
    out = None
    for a in sys.argv[2:]:
        if not a.startswith('--'): out = a
    if out and not os.path.isabs(out):
        out = os.path.join(script_dir, out)
    if not out and not aonly:
        b, e = os.path.splitext(inp)
        out = f"{b}_separated{e}"

    print(f"Loading: {inp}")
    gltf, buf = read_glb(inp)
    targets = analyze(gltf)

    orig_buf_size = len(buf)
    print(f"Meshes: {len(gltf['meshes'])}, Targets: {len(targets)}, Buf: {orig_buf_size:,}B")
    for mi, n, pc, bs in targets:
        print(f"  -> [{mi}] '{n}': {pc} sub, {bs} BS")

    if aonly: print("\nAnalysis mode."); return
    if not targets: print("Nothing to do!"); return

    print(f"\n{'#'*50}\n  EXECUTING SEPARATION\n{'#'*50}")

    for mi, n, pc, bs in targets:
        nms = separate_mesh(gltf, buf, mi)
        if nms:
            start = len(gltf['meshes'])
            for nm in nms: gltf['meshes'].append(nm)
            update_refs(gltf, mi, start, len(nms))
            print(f"  * '{n}' -> {len(nms)} meshes (start={start})")

    gltf['buffers'][0]['byteLength'] = len(buf)

    print(f"\n[RESULT] {len(gltf['meshes'])} meshes, buf {orig_buf_size:,} -> {len(buf):,} bytes")
    write_glb(out, gltf, buf)
    print(f"\n[DONE] {out}")


if __name__ == '__main__':
    main()
