"""Rank Razor components by EXTRACTABLE ORCHESTRATION, not by @code line count.

The signal learned from PortalSecretsDetail: an operation worth extracting either calls
SEVERAL distinct injected services, or loops over things with its own failure handling.
A thin wrapper calls ONE service and shows a toast — that is page code and should stay.
"""
import os, re, sys

def analyse(path):
    t = open(path, encoding="utf-8", errors="replace").read()
    injects = re.findall(r"^@inject\s+([\w<>.]+)\s+(\w+)", t, re.M)
    # ⚠️ UI plumbing is not a domain service. Counting Toasts/JS/Nav/Auth as collaborators
    # inflated every thin wrapper into "orchestration" on the first run.
    UI = ("ToastService","IJSRuntime","NavigationManager","AuthenticationStateProvider",
          "ILogger","IStringLocalizer","PersistentComponentState","IHttpContextAccessor")
    names = {alias for typ, alias in injects if not any(u in typ for u in UI)}
    if not names:
        return None
    m = re.search(r"@code\s*\{", t)
    if not m:
        return None
    body = t[m.start():]

    # split into methods on the common indentation
    parts = re.split(r"\n    (?=(?:private|public|protected)\s)", body)
    ops = []
    for p in parts:
        sig = re.match(r"(?:private|public|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\[\] .]+\s+(\w+)\s*\(", p)
        if not sig:
            continue
        used = {n for n in names if re.search(rf"\b{re.escape(n)}\s*\.", p)}
        loops = len(re.findall(r"\bforeach\s*\(", p))
        catches = len(re.findall(r"\bcatch\s*\(", p))
        lines = p.count("\n") + 1
        # orchestration: several services, or a loop that handles its own failures
        score = (len(used) >= 2) + (loops >= 1 and catches >= 2)
        if score and lines >= 15:
            ops.append((lines, len(used), loops, catches, sig.group(1)))
    return ops

root = sys.argv[1]
rows = []
for dp, _, fns in os.walk(root):
    for fn in fns:
        if fn.endswith(".razor"):
            p = os.path.join(dp, fn)
            ops = analyse(p)
            if ops:
                rows.append((sum(o[0] for o in ops), p, ops))
rows.sort(reverse=True)
print(f"{'lines':>6}  component / candidate operations")
for tot, p, ops in rows[:12]:
    print(f"{tot:>6}  {os.path.relpath(p, root)}")
    for lines, svc, loops, catches, name in sorted(ops, reverse=True)[:4]:
        print(f"          {lines:>4} lines  {svc} services  {loops} loops  {catches} catches   {name}")
print(f"\ncomponents with any candidate: {len(rows)}; total candidate lines: {sum(r[0] for r in rows)}")
