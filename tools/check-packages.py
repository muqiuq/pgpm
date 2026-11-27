#!/usr/bin/env python3
"""Checks PGPM's built-in configuration packages (docs/03 §17): every PGPM/Packages/BuiltIn/*.pgpm.json.

    python3 -I tools/check-packages.py

1. Structure: each package against spec/config-package.schema.json. The schema is evaluated by the small JSON
   Schema (draft 2020-12) evaluator below – the keywords the schema uses, nothing else; a keyword it doesn't know is an
   error, so no rule of the schema is skipped silently. Duplicate keys in a JSON object are errors too.
2. Semantics, across all built-in packages: keys unique across packages; every reference resolves within the package
   or a package it requires; score inputs exist, have a numeric value and no cycles; rescale/normalise maxima are
   derivable; exactly one default status per item type; template default status among the allowed statuses; section
   defaultFor ⊆ appliesTo; gate status suggestions are project statuses the template allows; map elements on existing
   layers; saved views, matrix presets and field defaults fit the fields they name; icons exist in PrimeIcons.
3. Self-test: broken variants of the packages (a score cycle, a dangling reference, two default statuses, …) must each
   be rejected with the expected message – so the checks above can't rot into no-ops.

Python's standard library only. Exit code 1 with one line per problem: <file>: <path>: <message>.
"""
import copy
import json
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCHEMA_PATH = os.path.join(ROOT, "spec", "config-package.schema.json")
PACKAGE_DIR = os.path.join(ROOT, "PGPM", "Packages", "BuiltIn")
WEBUI_PACKAGE = os.path.join(ROOT, "PGPM.WebUI", "package.json")

# The icons of PrimeIcons 7.0.0, the version PGPM.WebUI/package.json pins (checked below, so this list follows it).
PRIMEICONS_VERSION = "7.0.0"
PRIMEICONS = set("""
address-book align-center align-justify align-left align-right amazon android angle-double-down angle-double-left
angle-double-right angle-double-up angle-down angle-left angle-right angle-up apple arrow-circle-down arrow-circle-left
arrow-circle-right arrow-circle-up arrow-down arrow-down-left arrow-down-left-and-arrow-up-right-to-center
arrow-down-right arrow-left arrow-right arrow-right-arrow-left arrow-up arrow-up-left arrow-up-right
arrow-up-right-and-arrow-down-left-from-center arrows-alt arrows-h arrows-v asterisk at backward ban barcode bars bell
bell-slash bitcoin bolt book bookmark bookmark-fill box briefcase building building-columns bullseye calculator
calendar calendar-clock calendar-minus calendar-plus calendar-times camera car caret-down caret-left caret-right
caret-up cart-arrow-down cart-minus cart-plus chart-bar chart-line chart-pie chart-scatter check check-circle
check-square chevron-circle-down chevron-circle-left chevron-circle-right chevron-circle-up chevron-down chevron-left
chevron-right chevron-up circle circle-fill circle-off circle-on clipboard clock clone cloud cloud-download
cloud-upload code cog comment comments compass copy credit-card crown database delete-left desktop directions
directions-alt discord dollar download eject ellipsis-h ellipsis-v envelope equals eraser ethereum euro
exclamation-circle exclamation-triangle expand external-link eye eye-slash face-smile facebook fast-backward
fast-forward file file-arrow-up file-check file-edit file-excel file-export file-import file-o file-pdf file-plus
file-word filter filter-fill filter-slash flag flag-fill folder folder-open folder-plus forward gauge gift github globe
google graduation-cap hammer hashtag headphones heart heart-fill history home hourglass id-card image images inbox
indian-rupee info info-circle instagram key language lightbulb link linkedin list list-check lock lock-open map
map-marker mars megaphone microchip microchip-ai microphone microsoft minus minus-circle mobile money-bill moon
objects-column palette paperclip pause pause-circle paypal pen-to-square pencil percentage phone pinterest play
play-circle plus plus-circle pound power-off prime print qrcode question question-circle receipt reddit refresh replay
reply save search search-minus search-plus send server share-alt shield shop shopping-bag shopping-cart sign-in
sign-out sitemap slack sliders-h sliders-v sort sort-alpha-down sort-alpha-down-alt sort-alpha-up sort-alpha-up-alt
sort-alt sort-alt-slash sort-amount-down sort-amount-down-alt sort-amount-up sort-amount-up-alt sort-down
sort-down-fill sort-numeric-down sort-numeric-down-alt sort-numeric-up sort-numeric-up-alt sort-up sort-up-fill
sparkles spinner spinner-dotted star star-fill star-half star-half-fill step-backward step-backward-alt step-forward
step-forward-alt stop stop-circle stopwatch sun sync table tablet tag tags telegram th-large thumbs-down
thumbs-down-fill thumbs-up thumbs-up-fill thumbtack ticket tiktok times times-circle trash trophy truck turkish-lira
twitch twitter undo unlock upload user user-edit user-minus user-plus users venus verified video vimeo volume-down
volume-off volume-up wallet warehouse wave-pulse whatsapp wifi window-maximize window-minimize wrench youtube
""".split())

# Built-in list columns a saved view may name besides field:<key> (docs/03 §17); sort uses the same ids.
COLUMNS_COMMON = {"key", "title", "status", "owner", "persons", "color", "created_at", "updated_at"}
COLUMNS = {
    "note": COLUMNS_COMMON - {"status"} | {"pinned", "links"},
    "idea": COLUMNS_COMMON | {"days_in_status", "links"},
    "lead": COLUMNS_COMMON | {"days_in_status", "organisation", "value", "probability", "decision_date",
                              "next_action", "next_action_due", "source"},
    "project": COLUMNS_COMMON | {"days_in_status", "links", "template", "start_date", "end_date"},
    "gate": {"project", "title", "status", "due_date", "decision", "decided_at", "decided_by", "dod_progress",
             "persons"},
}

NUMERIC_FIELD_TYPES = {"number", "rating", "select", "multiselect", "score"}
AXIS_FIELD_TYPES = {"number", "rating", "score"}


# --- JSON ------------------------------------------------------------------------------------------------------------

def load_json(path, text=None):
    """(value, errors): duplicate keys in an object and NaN/Infinity are errors, not silently accepted."""
    errors = []

    def pairs(items):
        seen = {}
        for k, v in items:
            if k in seen:
                errors.append("duplicate key %r in one object" % k)
            seen[k] = v
        return seen

    def constant(name):
        raise ValueError("%s is not JSON" % name)

    try:
        if text is None:
            with open(path, encoding="utf-8") as f:
                text = f.read()
        return json.loads(text, object_pairs_hook=pairs, parse_constant=constant), errors
    except (OSError, ValueError) as e:
        return None, errors + [str(e)]


def json_type(v):
    if v is None:
        return "null"
    if isinstance(v, bool):
        return "boolean"
    if isinstance(v, (int, float)):
        return "number"
    if isinstance(v, str):
        return "string"
    if isinstance(v, list):
        return "array"
    if isinstance(v, dict):
        return "object"
    return type(v).__name__


def is_integer(v):
    return json_type(v) == "number" and (isinstance(v, int) or (math.isfinite(v) and v == int(v)))


def json_equal(a, b):
    if json_type(a) != json_type(b):
        return False
    if isinstance(a, dict):
        return a.keys() == b.keys() and all(json_equal(a[k], b[k]) for k in a)
    if isinstance(a, list):
        return len(a) == len(b) and all(json_equal(x, y) for x, y in zip(a, b))
    return a == b


def show(v):
    return json.dumps(v, ensure_ascii=False)


# --- JSON Schema (the draft 2020-12 keywords the package schema uses) ------------------------------------------------

class Schema:
    ANNOTATIONS = {"$schema", "$id", "$comment", "$defs", "title", "description", "default", "examples"}
    KEYWORDS = ANNOTATIONS | {
        "$ref", "type", "const", "enum", "pattern", "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "required", "properties", "additionalProperties", "propertyNames", "minProperties",
        "maxProperties", "items", "prefixItems", "minItems", "maxItems", "uniqueItems", "allOf", "anyOf", "oneOf",
        "not", "if", "then", "else",
    }
    SUBSCHEMA = {"additionalProperties", "propertyNames", "items", "not", "if", "then", "else"}
    SUBSCHEMA_LIST = {"allOf", "anyOf", "oneOf", "prefixItems"}
    SUBSCHEMA_MAP = {"properties", "$defs"}

    def __init__(self, root):
        self.root = root
        self.patterns = {}
        self.problems = []
        self._audit(root, "#")

    def _audit(self, s, where):
        if isinstance(s, bool):
            return
        if not isinstance(s, dict):
            self.problems.append("%s: a schema must be an object or a boolean" % where)
            return
        for k, v in s.items():
            if k not in self.KEYWORDS:
                self.problems.append("%s: keyword %r is not supported by tools/check-packages.py" % (where, k))
            elif k in self.SUBSCHEMA:
                self._audit(v, where + "/" + k)
            elif k in self.SUBSCHEMA_LIST:
                for i, x in enumerate(v):
                    self._audit(x, "%s/%s/%d" % (where, k, i))
            elif k in self.SUBSCHEMA_MAP:
                for name, x in v.items():
                    self._audit(x, "%s/%s/%s" % (where, k, name))
            elif k == "$ref":
                try:
                    self.resolve(v)
                except (KeyError, ValueError):
                    self.problems.append("%s: $ref %r does not resolve" % (where, v))
            elif k == "pattern":
                try:
                    self.pattern(v)
                except re.error as e:
                    self.problems.append("%s: pattern %r: %s" % (where, v, e))

    def resolve(self, ref):
        if not ref.startswith("#/"):
            raise ValueError(ref)
        node = self.root
        for part in ref[2:].split("/"):
            node = node[part.replace("~1", "/").replace("~0", "~")]
        return node

    def pattern(self, p):
        if p not in self.patterns:
            # ECMA-262's $ ends the input; Python's would also match before a final newline.
            self.patterns[p] = re.compile(p[:-1] + r"\Z" if p.endswith("$") and not p.endswith("\\$") else p)
        return self.patterns[p]

    def errors(self, inst, schema=None, path=""):
        """[(path, message)] – empty when inst is valid."""
        out = []
        self._validate(inst, self.root if schema is None else schema, path, out)
        return out

    def valid(self, inst, schema):
        return not self.errors(inst, schema)

    def _validate(self, v, s, path, out):
        if s is True:
            return
        if s is False:
            out.append((path, "is not allowed here"))
            return
        if "$ref" in s:
            self._validate(v, self.resolve(s["$ref"]), path, out)
        t = json_type(v)
        if "type" in s:
            types = s["type"] if isinstance(s["type"], list) else [s["type"]]
            if not any(t == x or (x == "integer" and is_integer(v)) for x in types):
                out.append((path, "must be %s, not %s" % (" or ".join(types), t)))
                return
        if "const" in s and not json_equal(v, s["const"]):
            out.append((path, "must be %s" % show(s["const"])))
        if "enum" in s and not any(json_equal(v, x) for x in s["enum"]):
            out.append((path, "%s is not one of %s" % (show(v), ", ".join(show(x) for x in s["enum"]))))
        if t == "string":
            if "minLength" in s and len(v) < s["minLength"]:
                out.append((path, "must not be empty" if s["minLength"] == 1 else
                            "must have at least %d characters" % s["minLength"]))
            if "maxLength" in s and len(v) > s["maxLength"]:
                out.append((path, "must have at most %d characters" % s["maxLength"]))
            if "pattern" in s and not self.pattern(s["pattern"]).search(v):
                out.append((path, "%s does not match %s" % (show(v), s["pattern"])))
        if t == "number":
            if "minimum" in s and v < s["minimum"]:
                out.append((path, "must be ≥ %s" % s["minimum"]))
            if "maximum" in s and v > s["maximum"]:
                out.append((path, "must be ≤ %s" % s["maximum"]))
            if "exclusiveMinimum" in s and v <= s["exclusiveMinimum"]:
                out.append((path, "must be > %s" % s["exclusiveMinimum"]))
            if "exclusiveMaximum" in s and v >= s["exclusiveMaximum"]:
                out.append((path, "must be < %s" % s["exclusiveMaximum"]))
        if t == "object":
            for name in s.get("required", []):
                if name not in v:
                    out.append((path, "missing required property %r" % name))
            if "minProperties" in s and len(v) < s["minProperties"]:
                out.append((path, "must have at least %d properties" % s["minProperties"]))
            if "maxProperties" in s and len(v) > s["maxProperties"]:
                out.append((path, "must have at most %d properties" % s["maxProperties"]))
            props = s.get("properties", {})
            for name, x in v.items():
                sub = join(path, name)
                if "propertyNames" in s:
                    for _, msg in self.errors(name, s["propertyNames"]):
                        out.append((sub, "key " + msg))
                if name in props:
                    self._validate(x, props[name], sub, out)
                elif "additionalProperties" in s:
                    if s["additionalProperties"] is False:
                        out.append((sub, "unknown property"))
                    else:
                        self._validate(x, s["additionalProperties"], sub, out)
        if t == "array":
            if "minItems" in s and len(v) < s["minItems"]:
                out.append((path, "must have at least %d items" % s["minItems"]))
            if "maxItems" in s and len(v) > s["maxItems"]:
                out.append((path, "must have at most %d items" % s["maxItems"]))
            prefix = s.get("prefixItems", [])
            for i, x in enumerate(v):
                if i < len(prefix):
                    self._validate(x, prefix[i], "%s[%d]" % (path, i), out)
                elif "items" in s:
                    self._validate(x, s["items"], "%s[%d]" % (path, i), out)
            if s.get("uniqueItems"):
                for i in range(len(v)):
                    if any(json_equal(v[i], v[j]) for j in range(i)):
                        out.append(("%s[%d]" % (path, i), "%s appears twice" % show(v[i])))
        for x in s.get("allOf", []):
            self._validate(v, x, path, out)
        if "anyOf" in s and not any(self.valid(v, x) for x in s["anyOf"]):
            out.append((path, "matches none of the allowed shapes: " + " | ".join(
                "; ".join(m for _, m in self.errors(v, x)) for x in s["anyOf"])))
        if "oneOf" in s and sum(self.valid(v, x) for x in s["oneOf"]) != 1:
            out.append((path, "must match exactly one of the allowed shapes"))
        if "not" in s and self.valid(v, s["not"]):
            out.append((path, "is not allowed in this shape"))
        if "if" in s:
            branch = s.get("then") if self.valid(v, s["if"]) else s.get("else")
            if branch is not None:
                self._validate(v, branch, path, out)


def join(path, name):
    return "%s.%s" % (path, name) if path else name


# --- Semantics -------------------------------------------------------------------------------------------------------

COLLECTIONS = ["groups", "statuses", "sections", "fields", "templates", "roles", "savedViews", "mapTemplates",
               "matrixPresets"]
ENTITY = {"groups": "group", "statuses": "status", "sections": "section", "fields": "field", "templates": "template",
          "roles": "role", "savedViews": "view", "mapTemplates": "map template", "matrixPresets": "matrix preset"}


class Checker:
    def __init__(self, packages):
        """packages: {name: (file label, data)} – structurally valid."""
        self.packages = packages
        self.problems = []
        self.owner = {}  # key → package name
        self.pkg = None
        self.file = None

    def err(self, path, msg):
        self.problems.append("%s: %s: %s" % (self.file, path, msg))

    # visibility and lookups

    def scope(self, name, seen=None):
        """The package and everything it requires, transitively."""
        seen = set() if seen is None else seen
        if name in seen or name not in self.packages:
            return seen
        seen.add(name)
        for r in self.packages[name][1].get("requires", []):
            self.scope(r, seen)
        return seen

    def get(self, collection, key, path, expect=None):
        """The entity with this key if the current package may use it, else None with an error."""
        owner = self.owner.get(key)
        entity = self.packages[owner][1].get(collection, {}).get(key) if owner else None
        if entity is None:
            self.err(path, "unknown %s %r" % (expect or ENTITY[collection], key))
            return None
        if owner not in self.scope(self.pkg):
            self.err(path, "%r is defined in package %r, which %r does not require" % (key, owner, self.pkg))
            return None
        return entity

    def field(self, bare_key, path):
        return self.get("fields", "field:" + bare_key, path)

    def section_of(self, field):
        owner = self.owner.get(field["section"])
        return self.packages[owner][1]["sections"][field["section"]] if owner else None

    def applies_to(self, field):
        section = self.section_of(field)
        return set(section["appliesTo"]) if section else set()

    def each(self, collection):
        for name in sorted(self.packages):
            self.pkg, (self.file, data) = name, self.packages[name]
            for key, entity in data.get(collection, {}).items():
                yield key, entity, "%s.%s" % (collection, key)

    # the checks

    def run(self):
        self.check_packages()
        if self.problems:
            return self.problems
        for name in sorted(self.packages):
            self.pkg, (self.file, data) = name, self.packages[name]
            for collection in COLLECTIONS:
                for key in data.get(collection, {}):
                    if key in self.owner:
                        self.err("%s.%s" % (collection, key), "key is also defined by package %r" % self.owner[key])
                    else:
                        self.owner[key] = name
        self.check_groups()
        self.check_statuses()
        self.check_sections()
        self.check_fields()
        self.check_scores()
        self.check_templates()
        self.check_roles()
        self.check_views()
        self.check_map_templates()
        self.check_matrix_presets()
        return self.problems

    def check_packages(self):
        for name in sorted(self.packages):
            self.pkg, (self.file, data) = name, self.packages[name]
            if os.path.basename(self.file) != name + ".pgpm.json":
                self.err("name", "package %r must be in a file %s.pgpm.json" % (name, name))
            for i, r in enumerate(data.get("requires", [])):
                if r == name:
                    self.err("requires[%d]" % i, "a package can't require itself")
                elif r not in self.packages:
                    self.err("requires[%d]" % i, "unknown package %r" % r)
            seen, stack = set(), [(name, [name])]
            while stack:
                current, trail = stack.pop()
                for r in self.packages.get(current, (None, {}))[1].get("requires", []):
                    if r == name and len(trail) > 1:
                        self.err("requires", "cycle: %s" % " → ".join(trail + [r]))
                    elif r not in seen and r in self.packages and r != name:
                        seen.add(r)
                        stack.append((r, trail + [r]))

    def check_unique_names(self, collection, label, by=lambda e: ()):
        names = {}
        for key, entity, path in self.each(collection):
            n = (by(entity), entity["name"].strip().lower())
            if n in names:
                self.err(path + ".name", "%s %r is already used by %s" % (label, entity["name"], names[n]))
            names[n] = key

    def check_icon(self, entity, path):
        icon = entity.get("icon")
        if icon is not None and icon[3:] not in PRIMEICONS:
            self.err(path + ".icon", "%r is not a PrimeIcons %s icon" % (icon, PRIMEICONS_VERSION))

    def check_groups(self):
        self.check_unique_names("groups", "group name")

    def check_statuses(self):
        self.check_unique_names("statuses", "status name", lambda s: s["itemType"])
        defaults = {"idea": [], "lead": [], "project": []}
        for key, s, path in self.each("statuses"):
            if key.split(":", 1)[1].split(".", 1)[0] != s["itemType"]:
                self.err(path, "the key's item type differs from itemType %r" % s["itemType"])
            if s.get("isDefault"):
                defaults[s["itemType"]].append(key)
            self.check_icon(s, path)
        for item_type, keys in defaults.items():
            if len(keys) != 1:
                self.file = "packages"
                self.err("statuses", "item type %r needs exactly one default status across all packages, has %d%s"
                         % (item_type, len(keys), (" (" + ", ".join(keys) + ")") if keys else ""))

    def check_sections(self):
        for key, s, path in self.each("sections"):
            extra = set(s.get("defaultFor", [])) - set(s["appliesTo"])
            if extra:
                self.err(path + ".defaultFor", "%s not in appliesTo" % ", ".join(sorted(extra)))
            groups = set()
            for i, a in enumerate(s.get("access", [])):
                self.get("groups", a["group"], "%s.access[%d].group" % (path, i))
                if a["group"] in groups:
                    self.err("%s.access[%d]" % (path, i), "group %r listed twice" % a["group"])
                groups.add(a["group"])

    def check_value(self, field, value, path):
        """A field value (default value, template default) in the shape of field_values (data model §7.5)."""
        t, c = field["type"], field.get("config", {})
        if value is None:
            return
        if t in ("score", "person"):
            self.err(path, "%s fields have no default value" % t)
        elif t in ("text", "textarea", "markdown", "url", "email"):
            if not isinstance(value, str):
                self.err(path, "must be a string")
            elif t == "text" and "maxLength" in c and len(value) > c["maxLength"]:
                self.err(path, "is longer than the field's maxLength %d" % c["maxLength"])
        elif t == "bool":
            if not isinstance(value, bool):
                self.err(path, "must be true or false")
        elif t == "number":
            if json_type(value) != "number":
                self.err(path, "must be a number")
            elif (c.get("min") is not None and value < c["min"]) or (c.get("max") is not None and value > c["max"]):
                self.err(path, "%s is outside the field's min/max" % show(value))
        elif t in ("date", "datetime"):
            pattern = r"[0-9]{4}-[0-9]{2}-[0-9]{2}\Z" if t == "date" else r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{5,8}Z\Z"
            if not isinstance(value, str) or not re.match(pattern, value):
                self.err(path, "must be a %s string" % t)
        elif t in ("select", "multiselect"):
            ids = {o["id"] for o in c.get("options", []) if not o.get("archived")}
            values = [value] if t == "select" else value
            if t == "select" and not isinstance(value, str) or t == "multiselect" and not isinstance(value, list):
                self.err(path, "must be an option id" if t == "select" else "must be a list of option ids")
                return
            for v in values:
                if v not in ids:
                    self.err(path, "%s is not an option of the field" % show(v))
        elif t == "rating":
            lo, hi = c.get("min", 0), c.get("max", 5)
            if value == "na":
                if not c.get("allowNa"):
                    self.err(path, "\"na\" needs allowNa")
            elif not is_integer(value) or not lo <= value <= hi:
                na = " or \"na\"" if c.get("allowNa") else ""
                self.err(path, "must be an integer from %d to %d%s" % (lo, hi, na))

    def check_fields(self):
        for key, f, path in self.each("fields"):
            self.get("sections", f["section"], path + ".section")
            c = f.get("config", {})
            t = f["type"]
            if t in ("select", "multiselect"):
                ids = set()
                for i, o in enumerate(c["options"]):
                    if o["id"] in ids:
                        self.err("%s.config.options[%d].id" % (path, i), "option id %r used twice" % o["id"])
                    ids.add(o["id"])
            if t == "rating":
                lo, hi = c.get("min", 0), c.get("max", 5)
                if lo >= hi:
                    self.err(path + ".config", "min must be below max")
                for label in c.get("labels", {}):
                    if not lo <= int(label) <= hi:
                        self.err(path + ".config.labels." + label, "outside %d–%d" % (lo, hi))
            if t == "number" and c.get("min") is not None and c.get("max") is not None and c["min"] > c["max"]:
                self.err(path + ".config", "min is above max")
            if "defaultValue" in f:
                self.check_value(f, f["defaultValue"], path + ".defaultValue")

    def input_max(self, f, cap):
        """The maximum of a score input (docs/03 §16), or None when it can't be derived."""
        t, c = f["type"], f.get("config", {})
        if t == "number":
            m = c.get("max")
        elif t == "rating":
            m = c.get("max", 5)
        elif t == "select":
            points = [o["points"] for o in c["options"] if not o.get("archived") and "points" in o]
            m = max(points) if points else None
        elif t == "multiselect":
            options = [o for o in c["options"] if not o.get("archived")]
            if c.get("multiPoints", "count") == "count":
                m = len(options)
            else:
                m = sum(max(o.get("points", 0), 0) for o in options)
        elif t == "score":
            m = 100 if f["config"].get("normalize") else f["config"].get("max")
        else:
            m = None
        if cap is not None:
            m = cap if m is None else min(m, cap)
        return m

    def check_scores(self):
        graph = {}
        for key, f, path in self.each("fields"):
            if f["type"] != "score":
                continue
            c, me = f["config"], key[len("field:"):]
            graph[me] = list(c["inputs"])
            p = path + ".config"
            applies = self.applies_to(f)
            caps = c.get("cap", {})
            for name in caps:
                if name not in c["inputs"]:
                    self.err("%s.cap.%s" % (p, name), "is not an input of the score")
            maxima = []
            for i, name in enumerate(c["inputs"]):
                ip = "%s.inputs[%d]" % (p, i)
                if name == me:
                    self.err(ip, "a score can't be its own input")
                    continue
                g = self.field(name, ip)
                if g is None:
                    continue
                t, gc = g["type"], g.get("config", {})
                if t not in NUMERIC_FIELD_TYPES:
                    self.err(ip, "%r is a %s field, which has no numeric value" % (name, t))
                    continue
                if t == "select" or (t == "multiselect" and gc.get("multiPoints", "count") == "sum"):
                    missing = [o["id"] for o in gc["options"] if not o.get("archived") and "points" not in o]
                    if missing:
                        self.err(ip, "options of %r without points: %s" % (name, ", ".join(missing)))
                if not applies <= self.applies_to(g):
                    self.err(ip, "%r does not apply to %s, the score does" % (
                        name, ", ".join(sorted(applies - self.applies_to(g)))))
                maxima.append((name, self.input_max(g, caps.get(name))))
            needs_max = c.get("na") == "rescale" or c.get("normalize")
            if needs_max:
                if "max" not in c:
                    self.err(p, "%s needs 'max'" % ("na: rescale" if c.get("na") == "rescale" else "normalize"))
                for name, m in maxima:
                    if m is None:
                        self.err(p, "the maximum of input %r can't be derived (number fields need config.max)" % name)
                derivable = all(m is not None for _, m in maxima)
                if c["op"] == "sum" and c.get("na") == "rescale" and "max" in c and derivable:
                    total = sum(m for _, m in maxima)
                    if abs(total - c["max"]) > 1e-9:
                        self.err(p + ".max", "%s differs from the sum of the inputs' maxima, %s – rescale would "
                                 "scale complete answers too" % (show(c["max"]), show(total)))
        # cycles, over the scores of all packages
        state = {}

        def visit(node, trail):
            state[node] = "open"
            for nxt in graph.get(node, []):
                if nxt not in graph:
                    continue
                if state.get(nxt) == "open":
                    cycle = trail[trail.index(nxt):] + [nxt]
                    self.pkg = self.owner.get("field:" + node)
                    self.file = self.packages[self.pkg][0]
                    self.err("fields.field:%s.config.inputs" % node, "score cycle: %s" % " → ".join(cycle))
                elif nxt not in state:
                    visit(nxt, trail + [nxt])
            state[node] = "done"

        for node in sorted(graph):
            if node not in state:
                visit(node, [node])

    def project_status(self, key, path):
        s = self.get("statuses", key, path)
        if s is not None and s["itemType"] != "project":
            self.err(path, "%r is a status of %ss, not of projects" % (key, s["itemType"]))
            return None
        return s

    def check_templates(self):
        for key, t, path in self.each("templates"):
            self.check_icon(t, path)
            allowed = t.get("allowedStatuses", [])
            for i, s in enumerate(allowed):
                self.project_status(s, "%s.allowedStatuses[%d]" % (path, i))
            if "defaultStatus" in t:
                if self.project_status(t["defaultStatus"], path + ".defaultStatus") and allowed \
                        and t["defaultStatus"] not in allowed:
                    self.err(path + ".defaultStatus",
                             "%r is not among the template's allowed statuses" % t["defaultStatus"])
            sections = t.get("sections", [])
            for i, s in enumerate(sections):
                sec = self.get("sections", s, "%s.sections[%d]" % (path, i))
                if sec is not None and "project" not in sec["appliesTo"]:
                    self.err("%s.sections[%d]" % (path, i), "section %r does not apply to projects" % s)
            for fkey, value in t.get("fieldDefaults", {}).items():
                fp = "%s.fieldDefaults.%s" % (path, fkey)
                f = self.get("fields", fkey, fp)
                if f is None:
                    continue
                if f["section"] not in sections:
                    self.err(fp, "the field's section %r is not one of the template's sections" % f["section"])
                self.check_value(f, value, fp)
            groups = set()
            for i, share in enumerate(t.get("defaultShares", [])):
                self.get("groups", share["group"], "%s.defaultShares[%d].group" % (path, i))
                if share["group"] in groups:
                    self.err("%s.defaultShares[%d]" % (path, i), "group %r listed twice" % share["group"])
                groups.add(share["group"])
            keys, orders = set(), set()
            for i, m in enumerate(t.get("milestones", [])):
                mp = "%s.milestones[%d]" % (path, i)
                if m["key"] in keys:
                    self.err(mp + ".key", "milestone key %r used twice in the template" % m["key"])
                keys.add(m["key"])
                if m["sortOrder"] in orders:
                    self.err(mp + ".sortOrder", "sortOrder %d used twice in the template" % m["sortOrder"])
                orders.add(m["sortOrder"])
                for prop in ("onGoStatus", "onNoGoStatus"):
                    s = m.get(prop)
                    if s is None:
                        continue
                    if self.project_status(s, mp + "." + prop) and allowed and s not in allowed:
                        self.err(mp + "." + prop, "%r is not among the template's allowed statuses" % s)

    def check_roles(self):
        self.check_unique_names("roles", "role name")

    def check_view_field(self, view_type, ref, path):
        if view_type not in ("idea", "project"):
            self.err(path, "%s views have no fields" % view_type)
            return None
        f = self.get("fields", ref, path)
        if f is not None and view_type not in self.applies_to(f):
            self.err(path, "field %r does not apply to %ss" % (ref, view_type))
            return None
        return f

    def check_column(self, view_type, column, path):
        if column.startswith("field:"):
            self.check_view_field(view_type, column, path)
        elif column not in COLUMNS[view_type]:
            self.err(path, "unknown %s column %r (built-in: %s)" % (
                view_type, column, ", ".join(sorted(COLUMNS[view_type]))))

    def check_views(self):
        names = {}
        for key, v, path in self.each("savedViews"):
            vt, d = v["itemType"], v["definition"]
            n = (vt, v["name"].strip().lower())
            if v.get("isShared", True):
                if n in names:
                    self.err(path + ".name", "shared %s view %r is already defined by %s" % (vt, v["name"], names[n]))
                names[n] = key
            fp = path + ".definition.filters"
            filters = d.get("filters", {})
            for i, s in enumerate(filters.get("statuses", [])):
                sp = "%s.statuses[%d]" % (fp, i)
                if vt not in ("idea", "lead", "project"):
                    self.err(sp, "%s views have no item statuses" % vt)
                    break
                st = self.get("statuses", s, sp)
                if st is not None and st["itemType"] != vt:
                    self.err(sp, "%r is a status of %ss, the view lists %ss" % (s, st["itemType"], vt))
            if "categories" in filters and vt not in ("idea", "lead", "project"):
                self.err(fp + ".categories", "%s views have no status categories" % vt)
            for i, t in enumerate(filters.get("templates", [])):
                if vt not in ("project", "gate"):
                    self.err("%s.templates[%d]" % (fp, i), "only project and gate views filter by template")
                    break
                self.get("templates", t, "%s.templates[%d]" % (fp, i))
            for i, g in enumerate(filters.get("sharedWith", [])):
                self.get("groups", g, "%s.sharedWith[%d]" % (fp, i))
            if "decisions" in filters and vt != "gate":
                self.err(fp + ".decisions", "only gate views filter by decision")
            if "hasLink" in filters and vt == "gate":
                self.err(fp + ".hasLink", "gates have no links")
            for i, ff in enumerate(filters.get("fields", [])):
                self.check_field_filter(vt, ff, "%s.fields[%d]" % (fp, i))
            for i, c in enumerate(d.get("columns", [])):
                self.check_column(vt, c, "%s.definition.columns[%d]" % (path, i))
            for i, c in enumerate(d.get("sort", [])):
                self.check_column(vt, c.lstrip("-"), "%s.definition.sort[%d]" % (path, i))

    def check_field_filter(self, vt, ff, path):
        f = self.check_view_field(vt, ff["field"], path + ".field")
        if f is None:
            return
        t, op = f["type"], ff["op"]
        ok = {
            "eq": {"bool", "number", "rating", "select", "text", "url", "email"},
            "in": {"select", "multiselect"},
            "contains": {"text", "textarea", "markdown", "url", "email"},
            "range": {"number", "rating", "score", "date", "datetime"},
        }
        if op in ok and t not in ok[op]:
            self.err(path + ".op", "%r can't be used on a %s field" % (op, t))
            return
        if op == "eq":
            value = ff["value"]
            expected = {"bool": "boolean", "number": "number", "rating": "number"}.get(t, "string")
            if json_type(value) != expected and not (t == "rating" and value == "na"):
                self.err(path + ".value", "a %s field compares with a %s" % (t, expected))
            elif t == "select":
                self.check_value(f, value, path + ".value")
        if op == "in":
            ids = {o["id"] for o in f["config"]["options"]}
            for i, v in enumerate(ff["values"]):
                if v not in ids:
                    self.err("%s.values[%d]" % (path, i), "%s is not an option of %s" % (show(v), ff["field"]))
        if op == "range":
            for bound in ("min", "max"):
                if bound in ff:
                    want = "string" if t in ("date", "datetime") else "number"
                    if json_type(ff[bound]) != want:
                        self.err("%s.%s" % (path, bound), "must be a %s for a %s field" % (want, t))

    def check_settings(self, settings, path):
        for i, name in enumerate(settings.get("cardBadges", [])):
            self.field(name, "%s.cardBadges[%d]" % (path, i))

    def check_map_templates(self):
        for key, m, path in self.each("mapTemplates"):
            self.check_settings(m.get("settings", {}), path + ".settings")
            layers, orders = set(), set()
            for i, layer in enumerate(m["layers"]):
                if layer["key"] in layers:
                    self.err("%s.layers[%d].key" % (path, i), "layer key %r used twice" % layer["key"])
                if layer["sortOrder"] in orders:
                    self.err("%s.layers[%d].sortOrder" % (path, i), "sortOrder %d used twice" % layer["sortOrder"])
                layers.add(layer["key"])
                orders.add(layer["sortOrder"])
            keys, zs = set(), set()
            for i, e in enumerate(m["elements"]):
                ep = "%s.elements[%d]" % (path, i)
                if e["key"] in keys:
                    self.err(ep + ".key", "element key %r used twice" % e["key"])
                keys.add(e["key"])
                if e["layer"] not in layers:
                    self.err(ep + ".layer", "unknown layer %r (layers: %s)" % (e["layer"], ", ".join(sorted(layers))))
                if (e["layer"], e["z"]) in zs:
                    self.err(ep + ".z", "z %d used twice in layer %r" % (e["z"], e["layer"]))
                zs.add((e["layer"], e["z"]))
                for corner in ((e["x"] + e["width"]), (e["y"] + e["height"])):
                    if abs(corner) > 1000000:
                        self.err(ep, "reaches beyond ±1,000,000")

    def check_matrix_presets(self):
        for key, mp, path in self.each("matrixPresets"):
            self.check_settings(mp.get("settings", {}), path + ".settings")
            m, p = mp["matrix"], path + ".matrix"
            types = set(m.get("filter", {}).get("types", ["idea", "project"]))
            for axis in ("x", "y", "size"):
                if axis not in m:
                    continue
                ap = "%s.%s" % (p, axis)
                f = self.field(m[axis]["field"], ap + ".field")
                if f is not None:
                    if f["type"] not in AXIS_FIELD_TYPES:
                        self.err(ap + ".field", "%r is a %s field; axes need number, rating or score" % (
                            m[axis]["field"], f["type"]))
                    if not types <= self.applies_to(f):
                        self.err(ap + ".field", "%r does not apply to %s" % (
                            m[axis]["field"], ", ".join(sorted(types - self.applies_to(f)))))
                if axis != "size":
                    a = m[axis]
                    if a["min"] >= a["max"]:
                        self.err(ap, "min must be below max")
                    elif "split" in a and not a["min"] <= a["split"] <= a["max"]:
                        self.err(ap + ".split", "outside min–max")
            fl = m.get("filter", {})
            for i, s in enumerate(fl.get("statuses", [])):
                st = self.get("statuses", s, "%s.filter.statuses[%d]" % (p, i))
                if st is not None and st["itemType"] not in types:
                    self.err("%s.filter.statuses[%d]" % (p, i), "a %s status, the matrix shows %s" % (
                        st["itemType"], ", ".join(sorted(types))))
            for i, t in enumerate(fl.get("templates", [])):
                self.get("templates", t, "%s.filter.templates[%d]" % (p, i))
            for i, g in enumerate(fl.get("groups", [])):
                self.get("groups", g, "%s.filter.groups[%d]" % (p, i))


# --- Running ---------------------------------------------------------------------------------------------------------

def check(schema, packages):
    """packages: {file label: data} → problems."""
    problems, valid = [], {}
    for label, data in packages.items():
        errs = schema.errors(data)
        problems += ["%s: %s: %s" % (label, p or "(top level)", m) for p, m in errs]
        if not errs:
            if data["name"] in valid:
                problems.append("%s: name: package %r is also in %s" % (label, data["name"], valid[data["name"]][0]))
            valid[data["name"]] = (label, data)
    if problems:
        return problems + ["(semantic checks skipped until the structure is valid)"]
    return Checker(valid).run()


def summary(data):
    fields = data.get("fields", {}).values()
    milestones = [m for t in data.get("templates", {}).values() for m in t.get("milestones", [])]
    parts = [
        "%d statuses" % len(data.get("statuses", {})),
        "%d sections" % len(data.get("sections", {})),
        "%d fields (%d scores)" % (len(fields), sum(f["type"] == "score" for f in fields)),
        "%d templates" % len(data.get("templates", {})),
        "%d milestones (%d gates, %d DoD items)" % (
            len(milestones), sum(m["kind"] == "gate" for m in milestones),
            sum(len(m.get("dod", [])) for m in milestones)),
        "%d groups" % len(data.get("groups", {})),
        "%d roles" % len(data.get("roles", {})),
        "%d saved views" % len(data.get("savedViews", {})),
        "%d map templates (%d elements)" % (
            len(data.get("mapTemplates", {})), sum(len(m["elements"]) for m in data.get("mapTemplates", {}).values())),
        "%d matrix presets" % len(data.get("matrixPresets", {})),
    ]
    return "%s %s: %s" % (data["name"], data["version"], ", ".join(parts))


def self_test(schema, packages):
    """Broken variants of the packages, each of which must be rejected with the expected message."""
    core, ai = "core.pgpm.json", "innovation-ai.pgpm.json"

    def f(pkg_data, key):
        return pkg_data["fields"][key]

    cases = [
        ("score cycle", lambda p: (f(p[ai], "field:feasibility_index")["config"]["inputs"].append("impact_score"),
                                   f(p[ai], "field:impact_score")["config"]["inputs"].append("feasibility_index"),
                                   f(p[ai], "field:impact_score")["config"].update(max=85)),
         "score cycle"),
        ("score input not numeric", lambda p: f(p[ai], "field:impact_score")["config"]["inputs"]
         .__setitem__(0, "problem"),
         "has no numeric value"),
        ("score input unknown", lambda p: f(p[ai], "field:feasibility_index")["config"]["inputs"].append("nope"),
         "unknown field 'field:nope'"),
        ("select input without points", lambda p: f(p[ai], "field:time_to_proof_of_value")["config"]["options"][0]
         .pop("points"), "without points"),
        ("rescale max not derivable", lambda p: f(p[ai], "field:effective_gain_pct")["config"]
         .update(na="rescale", op="sum") or f(p[ai], "field:expected_gain_pct")["config"].pop("max"),
         "can't be derived"),
        ("rescale max wrong", lambda p: f(p[ai], "field:feasibility_index")["config"].update(max=60),
         "differs from the sum"),
        ("two default project statuses", lambda p: p[ai]["statuses"]["status:project.closed"].update(isDefault=True),
         "exactly one default status"),
        ("no default lead status", lambda p: p[core]["statuses"]["status:lead.new"].pop("isDefault"),
         "item type 'lead' needs exactly one default status"),
        ("template default status not allowed", lambda p: p[ai]["templates"]["template:ai_use_case"]
         .update(defaultStatus="status:project.draft"), "not among the template's allowed statuses"),
        ("gate suggestion not allowed", lambda p: p[ai]["templates"]["template:ai_use_case"]["milestones"][2]
         .update(onGoStatus="status:project.done"), "not among the template's allowed statuses"),
        ("gate suggestion idea status", lambda p: p[ai]["templates"]["template:ai_use_case"]["milestones"][2]
         .update(onNoGoStatus="status:idea.rejected"), "not of projects"),
        ("defaultFor beyond appliesTo", lambda p: p[ai]["sections"]["section:charter"].update(defaultFor=["idea"]),
         "not in appliesTo"),
        ("dangling section reference", lambda p: p[ai]["templates"]["template:ai_use_case"]["sections"]
         .append("section:nope"), "unknown section 'section:nope'"),
        ("reference to a package not required", lambda p: p[ai].update(requires=[]), "does not require"),
        ("unknown required package", lambda p: p[ai].update(requires=["core", "nope"]), "unknown package 'nope'"),
        ("element on a missing layer", lambda p: p[ai]["mapTemplates"]["maptemplate:crazy_8s"]["elements"][0]
         .update(layer="nope"), "unknown layer 'nope'"),
        ("key defined twice", lambda p: p[core]["statuses"].update(
            {"status:project.closed": dict(p[ai]["statuses"]["status:project.closed"])}), "also defined by package"),
        ("bad field key", lambda p: p[ai]["fields"].update({"field:Bad": dict(f(p[ai], "field:problem"))}),
         "does not match"),
        ("missing required property", lambda p: p[core]["statuses"]["status:idea.new"].pop("category"),
         "missing required property 'category'"),
        ("bad enum", lambda p: p[core]["statuses"]["status:idea.new"].update(category="paused"), "is not one of"),
        ("bad color", lambda p: p[core]["statuses"]["status:idea.new"].update(color="#ABCDEF"), "does not match"),
        ("unknown icon", lambda p: p[core]["statuses"]["status:idea.new"].update(icon="pi-unicorn"),
         "not a PrimeIcons"),
        ("milestone with status suggestion", lambda p: p[ai]["templates"]["template:ai_use_case"]["milestones"][1]
         .update(onGoStatus="status:project.mvp_pilot"), "must be null"),
        ("view status of another type", lambda p: p[ai]["savedViews"]["view:review_board_agenda"]["definition"]
         ["filters"].update(statuses=["status:project.prototype"]), "the view lists ideas"),
        ("view column unknown", lambda p: p[ai]["savedViews"]["view:running_experiments"]["definition"]["columns"]
         .append("field:rb_decision"), "does not apply to projects"),
        ("view in on unknown option", lambda p: p[ai]["savedViews"]["view:ai_system_inventory"]["definition"]["filters"]
         ["fields"].append({"field": "field:eu_ai_act_class", "op": "in", "values": ["huge"]}), "not an option"),
        ("matrix axis not numeric", lambda p: p[ai]["matrixPresets"]["matrixpreset:prioritization"]["matrix"]["x"]
         .update(field="problem"), "axes need number, rating or score"),
        ("field default of a foreign section", lambda p: p[ai]["templates"]["template:ai_ambition"]["fieldDefaults"]
         .update({"field:is_ai_system": True}), "not one of the template's sections"),
        ("field default not an option", lambda p: p[ai]["templates"]["template:ai_use_case"]["fieldDefaults"]
         .update({"field:make_or_buy": "steal"}), "not an option of the field"),
    ]
    failures = []
    base = check(schema, packages)
    if base:
        return ["self-test: the unchanged packages already have problems"], 0
    for name, mutate, expected in cases:
        variant = copy.deepcopy(packages)
        mutate(variant)
        problems = check(schema, variant)
        if not any(expected in p for p in problems):
            failures.append("self-test %r: expected a problem containing %r, got: %s" % (
                name, expected, "; ".join(problems) or "no problems"))
    _, dup = load_json(None, '{"statuses": {"status:idea.new": {}, "status:idea.new": {}}}')
    if not any("duplicate key 'status:idea.new'" in d for d in dup):
        failures.append("self-test 'duplicate JSON key': not detected")
    return failures, len(cases) + 1


def main():
    schema_data, errs = load_json(SCHEMA_PATH)
    if errs:
        print("FAIL: %s: %s" % (os.path.relpath(SCHEMA_PATH, ROOT), "; ".join(errs)))
        return 1
    schema = Schema(schema_data)
    problems = ["%s: %s" % (os.path.relpath(SCHEMA_PATH, ROOT), p) for p in schema.problems]

    webui, errs = load_json(WEBUI_PACKAGE)
    pinned = (webui or {}).get("dependencies", {}).get("primeicons")
    if pinned != PRIMEICONS_VERSION:
        problems.append("tools/check-packages.py: PGPM.WebUI pins primeicons %s, the icon list here is of %s – update "
                        "PRIMEICONS" % (pinned, PRIMEICONS_VERSION))

    files = sorted(f for f in os.listdir(PACKAGE_DIR) if f.endswith(".pgpm.json")) if os.path.isdir(PACKAGE_DIR) else []
    if not files:
        problems.append("%s: no *.pgpm.json" % os.path.relpath(PACKAGE_DIR, ROOT))
    packages = {}
    for name in files:
        data, errs = load_json(os.path.join(PACKAGE_DIR, name))
        problems += ["%s: %s" % (name, e) for e in errs]
        if data is not None:
            packages[name] = data
    if not problems:
        problems += check(schema, packages)
    if problems:
        for p in problems:
            print(p)
        print("FAIL: %d problem%s in the configuration packages" % (len(problems), "" if len(problems) == 1 else "s"))
        return 1
    for data in packages.values():
        print(summary(data))
    failures, count = self_test(schema, packages)
    if failures:
        for p in failures:
            print(p)
        print("FAIL: the checks of tools/check-packages.py missed %d broken variant%s" % (
            len(failures), "" if len(failures) == 1 else "s"))
        return 1
    print("self-test: %d broken variants rejected" % count)
    return 0


if __name__ == "__main__":
    sys.exit(main())
