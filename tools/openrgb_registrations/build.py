"""Builds each family's own *Detect.cpp files with recorder.cpp and writes the
registrations OpenRGB's own detector objects make to registrations_dump.tsv
beside this file (#557). The dump is the fixture the tests hold
OpenRgbRegistrations.g.cs to: tools/gen_openrgb_registrations.py reads the
registrations from the source text, and this harness lets the compiler read
them.

Each family links into its own program, so a registration names its family.
The links use /FORCE:UNRESOLVED: a detector function refers to its family's
controller code, which is not built, and is never called. Only the
registration constructors run, before main.

Usage:
    python tools/openrgb_registrations/build.py [OPENRGB_ROOT] [FAMILY ...]

Named families are built alone and replace their own rows of the dump, which
keeps every other family's.

Needs the Visual Studio C++ tools.
"""
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
import gen_openrgb_registrations as gen  # noqa: E402
import openrgb_cpp as oc  # noqa: E402

DUMP = os.path.join(HERE, "registrations_dump.tsv")


VSWHERE = os.path.join(os.environ.get("ProgramFiles(x86)", ""), "Microsoft Visual Studio", "Installer", "vswhere.exe")


def vcvars():
    vswhere = VSWHERE
    if not os.path.exists(vswhere):
        sys.exit("vswhere.exe is not at %s. Install the Visual Studio C++ tools." % vswhere)
    path = subprocess.run([vswhere, "-latest", "-products", "*", "-property", "installationPath"],
                          capture_output=True, text=True).stdout.strip()
    bat = os.path.join(path, "VC", "Auxiliary", "Build", "vcvars64.bat")
    if not path or not os.path.exists(bat):
        sys.exit("vswhere.exe found no Visual Studio installation with C++ tools.")
    return bat


def include_dirs(root):
    """OpenRGB.pro:99-104, 136-162 and 334-342: every controller folder,
    then the common and Windows folders."""
    dirs = set()
    for dirpath, _, names in os.walk(os.path.join(root, "Controllers")):
        if any(n.endswith(".h") for n in names):
            dirs.add(dirpath)
    common = ["", "dependencies/ColorWheel", "dependencies/CRCpp", "dependencies/httplib", "dependencies/json",
              "dependencies/mdns", "dmiinfo", "hidapi_wrapper", "i2c_smbus", "i2c_tools", "interop", "net_port",
              "pci_ids", "scsiapi", "serial_port", "super_io", "AutoStart", "KeyboardLayoutManager", "RGBController",
              "qt", "SPDAccessor", "SuspendResume", "dependencies/stb", "dependencies/display-library/include",
              "dependencies/hidapi-hotplug-win/include", "dependencies/libusb-1.0.27/include",
              "dependencies/mbedtls-3.2.1/include", "dependencies/NVFC", "dependencies/PawnIO", "i2c_smbus/Windows",
              "wmi"]
    # No path may end in a backslash, which would escape its closing quote.
    return [os.path.normpath(d) for d in sorted(dirs) + [os.path.join(root, d) for d in common]]


def main():
    root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "..", "..", "..", "OpenRGB"))
    bat = vcvars()
    dm = oc.Source(root, gen.DETECTION_MANAGER)
    dm_macros = {k: v for k, v in dm.macros.items() if k.startswith("REGISTER_")}
    ctors = gen.constructors(dm)
    bus = oc.enum_values(oc.Source(root, gen.HIDAPI), "HID_API_BUS_UNKNOWN")
    incs = " ".join('/I"%s"' % d for d in include_dirs(root))
    # OpenRGB.pro:416-422.
    defines = "/D_MBCS /DWIN32 /D_CRT_SECURE_NO_WARNINGS /D_WINSOCK_DEPRECATED_NO_WARNINGS /DWIN32_LEAN_AND_MEAN " \
              "/DHID_HOTPLUG_ENABLED=1"
    work = tempfile.mkdtemp(prefix="padforge_registrations_")
    out = DUMP + ".new"
    if os.path.exists(out):
        os.remove(out)
    with open(out, "wb") as f:
        f.write(("openrgb\t%s\n" % oc.git_head(root)).encode("utf-8"))
    failed = []
    try:
        only = sys.argv[2:]
        for family in (only or gen.ALL_FAMILIES):
            regs = gen.read_family(root, family, dm, dm_macros, ctors, bus)
            files = sorted(set(r.invocation[0] for r in regs))
            fam_dir = os.path.join(work, family)
            os.makedirs(fam_dir)
            # The key names are linked in: a family's tables are globals
            # whose initializers read them, and those run before main too.
            srcs = " ".join('"%s"' % os.path.normpath(os.path.join(root, f))
                            for f in files + ["RGBController/RGBControllerKeyNames.cpp"])
            # The include list is longer than a command line may be, so cl
            # reads its arguments from a response file.
            rsp = os.path.join(fam_dir, "cl.rsp")
            with open(rsp, "w", encoding="utf-8") as f:
                f.write('/nologo /std:c++17 /utf-8 /EHsc /MD /O1 /W0 /bigobj %s %s /Fo"%s\\\\" "%s" %s /Fe"%s\\recorder.exe"'
                        % (defines, incs, fam_dir, os.path.join(HERE, "recorder.cpp"), srcs, fam_dir))
            cmd = ('call "%s" >nul && cl @"%s" /link /FORCE:UNRESOLVED >"%s\\build.log" 2>&1 && "%s\\recorder.exe" "%s" %s'
                   % (bat, rsp, fam_dir, fam_dir, out, family))
            # vcvars64.bat runs vswhere.exe by its bare name.
            env = dict(os.environ)
            env["PATH"] = env.get("PATH", "") + ";" + os.path.dirname(VSWHERE)
            # shell=True hands cmd.exe the line as written, quotes and all.
            r = subprocess.run(cmd, shell=True, env=env)
            if r.returncode != 0:
                failed.append(family)
                log = os.path.join(fam_dir, "build.log")
                text = open(log, encoding="mbcs", errors="replace").read() if os.path.exists(log) else ""
                errors = [l for l in text.splitlines() if " error " in l or "fatal" in l] or text.splitlines()[-15:]
                print("%s failed (%d):\n%s" % (family, r.returncode, "\n".join(errors)[:3000]))
            else:
                print("%s: %d files" % (family, len(files)))
    finally:
        shutil.rmtree(work, ignore_errors=True)
    if failed:
        sys.exit("failed: " + ", ".join(failed))
    with open(out, encoding="utf-8") as f:
        head, *rows = f.read().splitlines()

    def ported(row):
        """A family of which only some products are lit keeps their rows alone, as the
        generator keeps them (SOME_PRODUCTS)."""
        cells = row.split("\t")
        products = gen.SOME_PRODUCTS.get(cells[0])
        return products is None or (int(cells[3]), int(cells[4])) in products

    rows = [r for r in rows if ported(r)]
    if only and os.path.exists(DUMP):
        # A run of named families replaces their rows and keeps every other family's, in
        # the generator's family order.
        with open(DUMP, encoding="utf-8") as f:
            old_head, *old_rows = f.read().splitlines()
        if old_head != head:
            sys.exit("the dump was made at another OpenRGB commit (%s), so build every family" % old_head)
        order = {family: i for i, family in enumerate(gen.ALL_FAMILIES)}
        rows = sorted([r for r in old_rows if r.split("\t")[0] not in only] + rows,
                      key=lambda r: order.get(r.split("\t")[0], len(order)))
    with open(out, "wb") as f:
        f.write(("\n".join([head] + rows) + "\n").encode("utf-8"))
    os.replace(out, DUMP)
    print("wrote " + DUMP)


if __name__ == "__main__":
    main()
