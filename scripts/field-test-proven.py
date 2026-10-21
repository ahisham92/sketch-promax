#!/usr/bin/env python3
"""Writes src/SketchProMax/Data/field-test-proven.json: the field-test rows a real Revit run already proved.

Usage (from the repository root):
    python3 scripts/field-test-proven.py --commit 7b41c75 "<report Revit 2024>.md" "<report Revit 2025>.md"

Each report is a field test's Markdown report (one per Revit year), written by the version tagged at --commit. A row
is "proven" for that Revit year when it passed in the report AND its code has not changed between --commit and HEAD.

"Code unchanged" is INFERRED, file by file (a heuristic, not a proof):
  * the source file that defines the tool (the IRevitTool whose Name is the row's tool), plus the helper files that
    live with it (same folder, file name starting with the same stem, e.g. Carbon* for CarbonTools.cs), show no
    `git diff` between --commit and HEAD;
  * the tool's self-test case (the Nth `new("tool", ...)` in SelfTest.cs for row "tool #N") reads the same at both
    commits (comments and whitespace ignored), and so do the cases and files of the tools its Setup runs.
Shared code the tool calls elsewhere (helpers in other files, the Revit wrapper, the test project the field test
builds) is not compared. Rows that guard a bug log row (Bug = N) are never listed: bug checks always run.

Manual (checklist) features: a feature is "unchanged since the baseline" when every source reference in MANUAL_CODE
below exists at --commit and is the same at HEAD ("path" for a file or folder, "path#Name" for one class or method).
The test checklist then shows it as unchanged instead of asking for it again. Features not in MANUAL_CODE are new or
changed and stay on the list.

Python 3 standard library only. The JSON holds no file paths (the reports' paths name a PC's user).
"""
import argparse
import json
import os
import re
import subprocess
import sys

SRC = "src/SketchProMax"
SELFTEST = SRC + "/Tools/SelfTest.cs"
OUT = SRC + "/Data/field-test-proven.json"

# Manual checklist features whose code existed at the baseline: the source that implements each (what a person checks).
# A feature not listed here is treated as new or changed since the baseline.
MANUAL_CODE = {
    "help": [SRC + "/Commands/RibbonCommands.cs#OpenUrl", SRC + "/Commands/RibbonCommands.cs#HelpUrl"],
    "claude.open": [SRC + "/Commands/RibbonCommands.cs#OpenClaude", SRC + "/Commands/RibbonCommands.cs#Start",
                    SRC + "/Commands/RibbonCommands.cs#Launch"],
    "ribbon.status": [SRC + "/Commands/RibbonCommands.cs#Status", SRC + "/Commands/RibbonCommands.cs#StarterPrompt",
                      SRC + "/Commands/RibbonCommands.cs#CopyPrompt", SRC + "/Commands/RibbonCommands.cs#Describe"],
    "bugs.ui": [SRC + "/Bugs/BugReport.cs", SRC + "/Bugs/ChatsAndBugsExport.cs", SRC + "/Tools/ProjectPackTools.cs"],
    "exports.ui": ["tool:export_pdf", "tool:export_drawing_list", "tool:export_quantities"],
    "carbon.real": ["tool:embodied_carbon", "tool:carbon_compare", "tool:carbon_view", SRC + "/Data/carbon-factors.csv"],
    "ve.design_option": ["tool:ve_apply"],
    "ve.real": ["tool:ve_opportunities", "tool:ve_compare_options", "tool:ve_register"],
    "rebar2d.real": ["tool:place_additional_reinforcement", "tool:place_mesh_symbols", "tool:place_column_section_details"],
    "model.real_hosting": ["tool:place_void_cuts", "tool:find_clashes"],
    "bug.titleblock_params": ["tool:copy_from_file"],
    "bug.newer_link": ["tool:link_model"],
    "bug.always_load": ["src/SketchProMax.Loader", SRC + "/Update"],
    "matchlines.trim": ["tool:trim_matchlines"],
}

# Rows the field test runs outside the self-test cases (batch, probes): never listed, they are not compared here.
NOT_CASES = {"batch_run_recipe", "run_self_test", "self_test_status", "run_field_test"}


def git(*args, check=True):
    r = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8")
    if check and r.returncode != 0:
        sys.exit(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r


def show(commit, path):
    r = git("show", f"{commit}:{path}", check=False)
    return r.stdout if r.returncode == 0 else None


def head_text(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except OSError:
        return None


def unchanged(commit, path):
    """The file or folder exists at commit and has no diff to HEAD (committed or in the working tree)."""
    if git("cat-file", "-e", f"{commit}:{path}", check=False).returncode != 0:
        return False
    return git("diff", "--quiet", commit, "--", path, check=False).returncode == 0


# ---- Reports ---------------------------------------------------------------------------------------------------------

def parse_report(path):
    with open(path, encoding="utf-8") as f:
        text = f.read()
    year = re.search(r"^- Revit: .*\((\d{4}), build", text, re.M)
    version = re.search(r"^- Sketch ProMax: ([0-9.]+)", text, re.M)
    date = re.search(r"^- Date: (\d{4}-\d{2}-\d{2})", text, re.M)
    if not (year and version and date):
        sys.exit(f"{os.path.basename(path)}: not a field test report (no Revit year, version or date)")
    rows, heading, in_table = [], None, False
    for line in text.splitlines():
        if line.startswith("## "):
            heading, in_table = line[3:].strip(), False
            continue
        if line.startswith("| Tool | Status |"):
            in_table = True
            continue
        m = re.match(r"^\| (.+?) \| \**(pass|fail|skip|manual|proven)\** \|", line)
        if in_table and m:
            rows.append((m.group(1).strip(), m.group(2)))
    return {"revit": year.group(1), "version": version.group(1), "date": date.group(1), "rows": rows}


# ---- Source ----------------------------------------------------------------------------------------------------------

def strip_code(text):
    """Comments and whitespace out, strings kept: what the compiler sees."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        if text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        if c == '"' or text.startswith('@"', i) or text.startswith('$"', i) or text.startswith('$@"', i):
            j = text.index('"', i)
            verbatim = "@" in text[i:j]
            k = j + 1
            while k < n:
                if verbatim and text.startswith('""', k):
                    k += 2
                    continue
                if not verbatim and text[k] == "\\":
                    k += 2
                    continue
                if text[k] == '"':
                    break
                k += 1
            out.append(text[i:k + 1])
            i = k + 1
            continue
        out.append(c)
        i += 1
    return re.sub(r"\s+", " ", "".join(out)).strip()


def balanced_end(text, start):
    """Index just past the expression that starts at start: ends at a ',' or ']' or ';' at depth 0 (strings skipped)."""
    depth, i, n, opened = 0, start, len(text), False
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            i = text.find("\n", i)
            i = n if i < 0 else i
            continue
        if c == '"':
            verbatim = i > 0 and text[i - 1] == "@" or text[max(0, i - 2):i] in ("$@", "@$")
            k = i + 1
            while k < n:
                if verbatim and text.startswith('""', k):
                    k += 2
                    continue
                if not verbatim and text[k] == "\\":
                    k += 2
                    continue
                if text[k] == '"':
                    break
                k += 1
            i = k + 1
            continue
        if c == "'" and i + 2 < n:
            k = text.find("'", i + 1 + (1 if text[i + 1] == "\\" else 0))
            i = k + 1
            continue
        if c in "([{":
            depth += 1
            opened = True
        elif c in ")]}":
            if depth == 0:
                return i
            depth -= 1
        elif c in ",;" and depth == 0 and opened:
            return i
        i += 1
    return n


def cases(text):
    """{tool: [case text, ...]} in source order (SelfTest.AllCases concatenates its lists in source order)."""
    found = {}
    if text is None:
        return found
    for m in re.finditer(r'\bnew\("([a-z0-9_]+)",', text):
        end = balanced_end(text, m.start())
        found.setdefault(m.group(1), []).append(text[m.start():end])
    return found


def symbol(text, name):
    """One class, record or method body by name (None when missing)."""
    if text is None:
        return None
    m = re.search(r"\b(class|record|struct)\s+" + re.escape(name) + r"\b", text) or \
        re.search(r"^[ \t]*(?:(?:public|internal|private|protected|static|async|override|sealed)\s+)+[^\n=;]*?\b" + re.escape(name) + r"\s*\(", text, re.M)
    if not m:
        # A field or property: its declaration up to the semicolon.
        f = re.search(r"^[ \t]*(?:(?:public|internal|private|protected|static|const|readonly)\s+)+[^\n(]*?\b" + re.escape(name) + r"\s*(?:=>|=|\{)", text, re.M)
        return text[f.start():text.find(";", f.end()) + 1] if f else None
    brace, arrow = text.find("{", m.end()), text.find("=>", m.end())
    if arrow >= 0 and (brace < 0 or arrow < brace):
        return text[m.start():text.find(";", arrow) + 1]
    depth = 0
    for i in range(brace, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[m.start():i + 1]
    return text[m.start():]


def tool_files():
    """{tool name: [defining file]} from the IRevitTool classes at HEAD."""
    files = git("ls-files", SRC).stdout.split()
    tools = {}
    for path in files:
        if not path.endswith(".cs"):
            continue
        for name in re.findall(r'string Name\s*=>\s*"([a-z0-9_]+)"', head_text(path) or ""):
            tools.setdefault(name, []).append(path)
    return tools, files


def helpers(path, all_files, commit):
    """Helper files living with a tool file: same folder, name starting with the tool file's stem (4+ letters)."""
    folder, base = os.path.split(path)
    stem = re.sub(r"(Tools)?\.cs$", "", base)
    if len(stem) < 4:
        return []
    old = git("ls-tree", "--name-only", f"{commit}:{folder}", check=False).stdout.split()
    names = {os.path.basename(f) for f in all_files if os.path.dirname(f) == folder} | set(old)
    return sorted(os.path.join(folder, n) for n in names if n != base and n.startswith(stem) and n.endswith(".cs"))


class Checker:
    def __init__(self, commit):
        self.commit = commit
        self.tools, self.files = tool_files()
        self.old_cases, self.new_cases = cases(show(commit, SELFTEST)), cases(head_text(SELFTEST))
        self._files = {}

    def files_same(self, tool):
        if tool not in self._files:
            defs = self.tools.get(tool, [])
            group = sorted({f for d in defs for f in [d, *helpers(d, self.files, self.commit)]})
            self._files[tool] = bool(defs) and all(unchanged(self.commit, f) for f in group)
        return self._files[tool]

    def case(self, which, tool, nth):
        lst = which.get(tool, [])
        return lst[nth - 1] if 0 < nth <= len(lst) else None

    def why_not(self, label, seen=None):
        """None when the row's code is unchanged; else why it must run again."""
        seen = seen or set()
        if label in seen:
            return None
        seen.add(label)
        m = re.match(r"^([a-z0-9_]+)(?: #(\d+))?$", label)
        if not m:
            return "not a self-test case row"
        tool, nth = m.group(1), int(m.group(2) or 1)
        if tool in NOT_CASES:
            return "run by the field test itself, not compared"
        new, old = self.case(self.new_cases, tool, nth), self.case(self.old_cases, tool, nth)
        if new is None:
            return "no such case any more"
        if re.search(r"\bBug\s*=\s*\d", new):
            return "bug check: always runs"
        if old is None or strip_code(old) != strip_code(new):
            return "its field-test case changed"
        if not self.files_same(tool):
            return "its source changed" if tool in self.tools else "no tool source found"
        setup = re.search(r"Setup:\s*\[([^\]]*)\]", new)
        for ref in re.findall(r'"([a-z0-9_]+(?:#\d+)?)"', setup.group(1) if setup else ""):
            name, _, n = ref.partition("#")
            why = self.why_not(name if not n or n == "1" else f"{name} #{n}", seen)
            if why:
                return f"its setup step {ref} changed ({why})"
        return None

    def ref_same(self, ref):
        if ref.startswith("tool:"):
            return self.files_same(ref[5:])
        path, _, name = ref.partition("#")
        if not name:
            return unchanged(self.commit, path)
        old, new = symbol(show(self.commit, path), name), symbol(head_text(path), name)
        return old is not None and new is not None and strip_code(old) == strip_code(new)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("reports", nargs="+", help="field test reports (Markdown), one per Revit year")
    ap.add_argument("--commit", required=True, help="the commit the reports' version was built from")
    ap.add_argument("--out", default=OUT)
    ap.add_argument("--verbose", action="store_true", help="print why each passed row must run again")
    a = ap.parse_args()

    check = Checker(a.commit)
    baselines, version = [], None
    for path in a.reports:
        r = parse_report(path)
        version = version or r["version"]
        proven, again = [], {}
        for label, status in r["rows"]:
            if status != "pass":
                continue
            why = check.why_not(label)
            if why is None:
                proven.append(label)
            else:
                again[label] = why
        baselines.append({"revit": r["revit"], "date": r["date"], "version": r["version"], "proven": sorted(set(proven))})
        print(f"Revit {r['revit']}: {len(r['rows'])} rows, {sum(1 for _, s in r['rows'] if s == 'pass')} passed, {len(set(proven))} proven")
        if a.verbose:
            for label, why in sorted(again.items()):
                print(f"  runs again: {label}: {why}")

    coverage = head_text(SRC + "/Tools/FieldTestCoverage.cs") or ""
    manual = []
    for key, refs in sorted(MANUAL_CODE.items()):
        if not re.search(r'new\("' + re.escape(key) + r'",[^\n]*How\.Manual', coverage):
            sys.exit(f"MANUAL_CODE names {key}, which is not a manual feature in FieldTestCoverage.cs")
        if all(check.ref_same(ref) for ref in refs):
            manual.append(key)
    print(f"Manual features unchanged since {version}: {len(manual)} of {len(MANUAL_CODE)} listed ({', '.join(manual) or 'none'})")

    data = {
        "about": "Field-test rows a real Revit run proved, with code unchanged since (inferred file by file by "
                 "scripts/field-test-proven.py: a heuristic). Regenerate after a new full field test.",
        "commit": git("rev-parse", "--short", a.commit).stdout.strip(),
        "version": version,
        "baselines": sorted(baselines, key=lambda b: b["revit"]),
        "manual_unchanged": manual,
    }
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2)
        f.write("\n")
    print(f"Wrote {a.out}")


if __name__ == "__main__":
    main()
