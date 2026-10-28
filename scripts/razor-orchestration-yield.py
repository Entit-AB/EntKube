"""Rank Razor components by EXTRACTABLE ORCHESTRATION.

⚠️ THE FIRST VERSION OF THIS SCRIPT WAS WRONG, and wrong in a way worth keeping written down.
It scored a method as orchestration if it used >=2 injected services, or looped with >=2 catch
blocks. Checked against the tree, its top four candidates were all false positives:

  PortalDatabasesPanel.CreateDatabase   guard -> ONE service -> toast -> reload
  CustomerStatusDashboard.LoadData      already contract-adopted; reads only its own module
  IdentityTab.SwitchRealmTab            lazy-load per tab: 9 catches, 9 single service calls
  DatabaseTab.ExecuteBackupRestore      a form submit handler reading selected* fields

The nine catches in SwitchRealmTab were read as EVIDENCE OF orchestration when they are the
opposite: repetitive per-field error handling is the signature of a page, not of an operation.

THE SIGNAL THAT ACTUALLY DISCRIMINATES, taken from the one true positive
(PortalSecretsDetail.SyncDeploymentBindingsAsync, since extracted):

    an OPERATION  accumulates into a result and does not touch UI state
    PAGE CODE     assigns to loading flags, *Error strings and selected* fields

So a method is a candidate only if it uses >=2 domain services AND writes no UI state. That is
still a heuristic and will still need reading — but it stops flagging code that is already fine.
"""
import os, re, sys

UI_TYPES = ("ToastService", "IJSRuntime", "NavigationManager", "AuthenticationStateProvider",
            "ILogger", "IStringLocalizer", "PersistentComponentState", "IHttpContextAccessor")

# Assignments that mean "this method is driving a page", not "this method is an operation".
UI_WRITE = re.compile(
    r"\b(?:loading\w*|is\w*ing|\w*Error|\w*error|selected\w*|\w*Output|busy\w*|show\w*)\s*=\s*"
    r"|StateHasChanged\s*\(")

def analyse(path):
    t = open(path, encoding="utf-8", errors="replace").read()
    injects = re.findall(r"^@inject\s+([\w<>.]+)\s+(\w+)", t, re.M)
    names = {a for typ, a in injects if not any(u in typ for u in UI_TYPES)}
    if not names:
        return []
    m = re.search(r"@code\s*\{", t)
    if not m:
        return []

    out = []
    for p in re.split(r"\n    (?=(?:private|public|protected)\s)", t[m.start():]):
        sig = re.match(r"(?:private|public|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,\[\] .]+\s+(\w+)\s*\(", p)
        if not sig:
            continue
        used = {n for n in names if re.search(rf"\b{re.escape(n)}\s*\.", p)}
        ui_writes = len(UI_WRITE.findall(p))
        lines = p.count("\n") + 1
        if len(used) >= 2 and ui_writes == 0 and lines >= 15:
            out.append((lines, len(used), sig.group(1)))
    return out

root = sys.argv[1]
rows = []
for dp, _, fns in os.walk(root):
    for fn in fns:
        if fn.endswith(".razor"):
            ops = analyse(os.path.join(dp, fn))
            if ops:
                rows.append((sum(o[0] for o in ops), os.path.relpath(os.path.join(dp, fn), root), ops))
rows.sort(reverse=True)
print(f"{'lines':>6}  component / candidate operations (>=2 services, NO UI-state writes)")
for tot, p, ops in rows[:12]:
    print(f"{tot:>6}  {p}")
    for lines, svc, name in sorted(ops, reverse=True)[:4]:
        print(f"          {lines:>4} lines  {svc} services   {name}")
print(f"\ncomponents with any candidate: {len(rows)}; total candidate lines: {sum(r[0] for r in rows)}")
