#!/usr/bin/env python3
"""Catches page-to-server wiring mistakes that `dotnet build` cannot see.

The page is plain JavaScript and the server is C#, so nothing compiles them against each
other. A misspelled element id, a call to a route that does not exist, or an import of a
name that is not exported only shows up as an error in the browser. This checks:

  1. every  $('#id')  the scripts look up exists in index.html (or is created by a script);
  2. every /api/... URL the page calls matches a route the server maps (same verb);
  3. every  import {..} from './x.js'  points at a file that exports each name.

    python3 tools/check-web-wiring.py
"""
import glob
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = os.path.join(HERE, "..", "src", "AmwalECR.WebExample")
WWW = os.path.join(SITE, "wwwroot")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def scripts():
    return [os.path.join(WWW, "app.js")] + sorted(glob.glob(os.path.join(WWW, "js", "*.js")))


def check_ids(errors):
    html = read(os.path.join(WWW, "index.html"))
    known = set(re.findall(r'\bid="([^"]+)"', html))
    for path in scripts():
        text = read(path)
        # ids a script creates itself:  h('div', { id: 'x' })  /  el.id = 'x'
        known |= set(re.findall(r"""\bid\s*:\s*['"]([\w-]+)['"]""", text))
        known |= set(re.findall(r"""\.id\s*=\s*['"]([\w-]+)['"]""", text))
    for path in scripts():
        for m in re.finditer(r"""\$\(\s*['"]#([\w-]+)['"]""", read(path)):
            if m.group(1) not in known:
                errors.append(f"{os.path.relpath(path, WWW)}: looks up #{m.group(1)}, which index.html does not define")


def norm(route):
    route = re.sub(r"\$\{[^}]*\}", "{}", route)          # JS template  ${enc(id)}
    route = re.sub(r"\{[^}/]*\}", "{}", route)            # C# route     {id} {serial}
    return route.rstrip("/") or "/"


def server_routes():
    routes = set()
    for path in glob.glob(os.path.join(SITE, "**", "*.cs"), recursive=True):
        if os.sep + "obj" + os.sep in path or os.sep + "bin" + os.sep in path:
            continue
        text = read(path)
        groups = {m.group(1): m.group(2) for m in re.finditer(r'var\s+(\w+)\s*=\s*\w+\.MapGroup\(\s*"([^"]*)"', text)}
        for m in re.finditer(r'(\w+)\.Map(Get|Post|Put|Delete)\(\s*"([^"]*)"', text):
            target, verb, route = m.groups()
            prefix = groups.get(target, "")
            routes.add((verb.upper(), norm(prefix + route)))
    return routes


def check_api(errors):
    routes = server_routes()
    api = read(os.path.join(WWW, "js", "api.js"))
    calls = re.findall(r"""request\(\s*'(GET|POST|PUT|DELETE)'\s*,\s*([`'"])(/api[^`'"]*)\2""", api)
    calls += [(v, "'", u) for v, u in re.findall(r"""fetch\([^)]*?method:\s*'(GET|POST|PUT|DELETE)'[^)]*?['"`](/api[^'"`]*)""", api)]
    for verb, _, url in calls:
        if (verb, re.sub(r"(?<=[^/])\{\}$", "", norm(url).split("?")[0])) not in routes:
            errors.append(f"js/api.js: {verb} {url} matches no server route")
    # GET urls that are not built with request(): qr image, etc.
    for url in re.findall(r'`(/api/[^`]*)`|\'(/api/[^\']*)\'|"(/api/[^"]*)"', api):
        url = next(u for u in url if u)
        base = norm(url).split("?")[0]   # interpolations first: a ${...} may contain a "?"
        base = re.sub(r"(?<=[^/])\{\}$", "", base)      # a trailing ${query} is not part of the path
        if not any(base == r[1] for r in routes):
            errors.append(f"js/api.js: {url} matches no server route")
    return len(calls)


def check_imports(errors):
    exports = {}
    for path in scripts():
        text = read(path)
        names = set(re.findall(r"export\s+(?:async\s+)?(?:function\*?|const|let|class)\s+([\w$]+)", text))
        for group in re.findall(r"export\s*\{([^}]*)\}", text):
            names |= {n.strip().split(" as ")[-1] for n in group.split(",") if n.strip()}
        exports[os.path.abspath(path)] = names
    for path in scripts():
        for m in re.finditer(r"import\s*\{([^}]*)\}\s*from\s*['\"]([^'\"]+)['\"]", read(path)):
            target = os.path.abspath(os.path.join(os.path.dirname(path), m.group(2)))
            if target not in exports:
                errors.append(f"{os.path.relpath(path, WWW)}: imports {m.group(2)}, which does not exist")
                continue
            for name in (n.strip().split(" as ")[0] for n in m.group(1).split(",") if n.strip()):
                if name not in exports[target]:
                    errors.append(f"{os.path.relpath(path, WWW)}: imports {name} from {m.group(2)}, which does not export it")


def main():
    errors = []
    check_ids(errors)
    n = check_api(errors)
    check_imports(errors)
    print(f"{len(scripts())} scripts, {n} API calls, {len(server_routes())} server routes checked")
    for e in errors:
        print("ERROR", e)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
