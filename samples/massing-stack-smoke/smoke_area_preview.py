#! python 3
import System.Drawing
import Rhino
import Rhino.Geometry as rg

debug_lines = []

def log(message):
    debug_lines.append(str(message))

def as_list(value):
    if value is None:
        return []
    if isinstance(value, list) or isinstance(value, tuple):
        return list(value)
    return [value]

def color_lerp(t):
    t = max(0.0, min(1.0, float(t)))
    r = int(60 + 185 * t)
    g = int(145 + 80 * (1.0 - abs(t - 0.5) * 2.0))
    b = int(220 - 160 * t)
    return System.Drawing.Color.FromArgb(190, r, g, b)

def make_material(color):
    material = Rhino.Display.DisplayMaterial(color)
    material.Diffuse = color
    material.Transparency = max(0.0, min(1.0, 1.0 - (float(color.A) / 255.0)))
    return material

try:
    preview_curves = as_list(slab_curves)
    preview_points = as_list(centroids)
    area_values = [float(x) for x in as_list(areas)]
    names = as_list(level_names)
    total_value = float(total_area_m2) if total_area_m2 is not None else sum(area_values) / 1000000.0

    min_area = min(area_values) if area_values else 0.0
    max_area = max(area_values) if area_values else 0.0
    spread = max(1.0, max_area - min_area)

    colors = []
    materials = []
    area_labels = []
    vector_anchors = []
    preview_vectors = []
    for i, area in enumerate(area_values):
        t = (area - min_area) / spread
        color = color_lerp(t)
        colors.append(color)
        materials.append(make_material(color))
        name = names[i] if i < len(names) else "L{:02d}".format(i + 1)
        area_labels.append("{}  {:.0f} m2".format(name, area / 1000000.0))
        if i < len(preview_points):
            vector_anchors.append(preview_points[i])
            preview_vectors.append(rg.Vector3d(0, 0, 1200))

    total_text = "Total GFA: {:,.0f} m2".format(total_value)
    log("AREA_OK | min={:.2f} | max={:.2f} | labels={}".format(min_area / 1000000.0, max_area / 1000000.0, len(area_labels)))
    log(total_text)
except Exception as exc:
    preview_curves = []
    preview_points = []
    area_labels = []
    colors = []
    materials = []
    vector_anchors = []
    preview_vectors = []
    total_text = "Total GFA: 0 m2"
    log("AREA_ERROR | {}".format(exc))

dbg = "\n".join(debug_lines)
