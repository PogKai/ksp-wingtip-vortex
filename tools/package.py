"""Build the release DLL and package WingtipVortex as a KSP mod zip.

    python tools/package.py                  package the committed state (the normal case)
    python tools/package.py --allow-dirty    package the working tree, for a release candidate
    python tools/package.py --update-plugin  also copy the built DLL over Plugins/WingtipVortex.dll,
                                             which the repository tracks

Writes dist/WingtipVortex_<version>.zip in the layout KSP mods are installed in:

    GameData/WingtipVortex/Plugins/WingtipVortex.dll
    GameData/WingtipVortex/Source/WingtipVortex.cs, Source/WingVapor/*.cs
    GameData/WingtipVortex/README.md, CHANGELOG.md, license.md
    GameData/WingtipVortex/docs/*.md, docs/images/*

then reads the zip back and checks that the DLL inside is the one just built. Refuses to run when

  * the version in Source/WingtipVortex.cs (ModVersion), in the local project's AssemblyInfo.cs and
    in the top heading of CHANGELOG.md disagree, or the top heading is still "Unreleased",
  * tracked files have uncommitted changes (unless --allow-dirty), so a release is always a commit,
  * the local project's copy of WingtipVortex.cs has drifted from Source/WingtipVortex.cs.

The DLL is built from the local Visual Studio project, WingtipVertex/WingtipVertex.csproj (not
tracked: its references point at a local KSP install), with the assembly renamed to WingtipVortex.
"""
import hashlib
import os
import re
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CSPROJ = os.path.join(ROOT, "WingtipVertex", "WingtipVertex.csproj")
DIST = os.path.join(ROOT, "dist")
BUILD = os.path.join(DIST, "build")
NAME = "WingtipVortex"


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def run(*cmd):
    return subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)


def main():
    allow_dirty = "--allow-dirty" in sys.argv
    update_plugin = "--update-plugin" in sys.argv

    modversion = re.search(r'ModVersion = "([^"]+)"', read(os.path.join(ROOT, "Source", "WingtipVortex.cs")))
    assembly = re.search(r'AssemblyVersion\("(\d+\.\d+\.\d+)\.0"\)', read(os.path.join(ROOT, "WingtipVertex", "Properties", "AssemblyInfo.cs")))
    heading = re.search(r"^## (\S+)", read(os.path.join(ROOT, "CHANGELOG.md")), re.M)
    found = [m and m.group(1) for m in (modversion, assembly, heading)]
    if not (found[0] and found[0] == found[1] == found[2]) or not re.fullmatch(r"\d+\.\d+\.\d+", found[0] or ""):
        sys.exit("versions must agree and be numeric: ModVersion %s, AssemblyInfo %s, top of CHANGELOG %s" % tuple(found))
    version = found[0]

    if read(os.path.join(ROOT, "Source", "WingtipVortex.cs")) != read(os.path.join(ROOT, "WingtipVertex", "WingtipVortex.cs")):
        sys.exit("WingtipVertex/WingtipVortex.cs differs from Source/WingtipVortex.cs: bring them in step first")

    dirty = run("git", "status", "--porcelain", "--untracked-files=no").stdout.strip()
    if dirty and not allow_dirty:
        sys.exit("uncommitted changes to tracked files, so this would not be a release of a commit:\n" + dirty +
                 "\ncommit them, or use --allow-dirty for a release candidate")

    print("building %s %s (Release)" % (NAME, version))
    os.makedirs(BUILD, exist_ok=True)
    result = run("dotnet", "build", CSPROJ, "-c", "Release", "-nologo", "-v", "q",
                 "-p:AssemblyName=" + NAME, "-p:OutputPath=" + BUILD + os.sep)
    if result.returncode != 0:
        sys.exit(result.stdout + result.stderr)
    dll = os.path.join(BUILD, NAME + ".dll")

    root = "GameData/" + NAME + "/"
    files = {root + "Plugins/" + NAME + ".dll": dll}
    for doc in ("README.md", "CHANGELOG.md", "license.md"):
        files[root + doc] = os.path.join(ROOT, doc)
    for sub in ("docs", os.path.join("docs", "images")):
        folder = os.path.join(ROOT, sub)
        for name in sorted(os.listdir(folder)):
            if os.path.isfile(os.path.join(folder, name)):
                files[root + sub.replace(os.sep, "/") + "/" + name] = os.path.join(folder, name)
    files[root + "Source/WingtipVortex.cs"] = os.path.join(ROOT, "Source", "WingtipVortex.cs")
    vapor = os.path.join(ROOT, "Source", "WingVapor")
    for name in sorted(os.listdir(vapor)):
        if name.endswith(".cs"):
            files[root + "Source/WingVapor/" + name] = os.path.join(vapor, name)

    os.makedirs(DIST, exist_ok=True)
    out = os.path.join(DIST, "%s_%s.zip" % (NAME, version))
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for arc, path in files.items():
            info = zipfile.ZipInfo(arc, date_time=(2026, 1, 1, 0, 0, 0))   # same bytes for the same inputs
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(path, "rb") as f:
                z.writestr(info, f.read())

    with zipfile.ZipFile(out) as z:
        names = z.namelist()
        bad = [n for n in names if re.search(r"(^|/)(bin|obj|dist|tools|Tests)/|\.pdb$|\\", n)]
        if bad or any(not n.startswith(root) for n in names):
            sys.exit("unexpected files in the package: " + ", ".join(bad or names))
        with open(dll, "rb") as f:
            if z.read(root + "Plugins/" + NAME + ".dll") != f.read():
                sys.exit("the DLL in the zip is not the one just built")
        if ("## " + version) not in z.read(root + "CHANGELOG.md").decode("utf-8"):
            sys.exit("changelog does not mention " + version)

    if update_plugin:
        target = os.path.join(ROOT, "Plugins", NAME + ".dll")
        with open(dll, "rb") as src, open(target, "wb") as dst:
            dst.write(src.read())
        print("updated Plugins/%s.dll" % NAME)

    with open(out, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    print("wrote %s (%s bytes)" % (os.path.relpath(out, ROOT), format(os.path.getsize(out), ",")))
    print("sha256 " + digest)
    for n in names:
        print("  " + n)


if __name__ == "__main__":
    main()
