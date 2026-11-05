"""Lists the docs pages' fork commit hashes that the shipped fork commit does
not contain, and prints every source line a page cites in the fork.

Usage:
    python tools/check_fork_refs.py DOCS_FOLDER FORK_CLONE [SHIPPED]

  DOCS_FOLDER  the folder of Markdown pages (the wiki folder of the docs site)
  FORK_CLONE   a clone of the fork the bundled builds come from
  SHIPPED      the commit the bundled builds carry (default: the clone's HEAD)

Run it on every take of a fork build and on every audit whose window holds one.

A rebase onto upstream gives every fork commit a new hash. A page that names a
fork commit, a line of a fork file or the fork's history goes stale at the next
take, and nothing else reads those sentences. Ten-character tokens that are not
commits in the clone (Dolphin, Linux, other repositories) are skipped.

OFF lines name a hash the shipped history does not hold, with the commit on that
history that has the same subject and author date. A person reads each: a feature
citation takes the twin, and a sentence naming the revision a release build
carries keeps its hash and says what the hash became.

LINE entries print each `file.c:N`, `file.h:N-M` or `file.c` line N citation
into the clone's files beside the cited lines at SHIPPED. A person judges whether
the lines still hold the code the sentence describes. A file name the clone holds
twice, or another project's file that shares a name with one here (main.cpp), is
printed too and read the same way.

Exit status: 0 when no page names a hash off the shipped history, 1 when one does.
Line citations are listed for reading and never change the status.
"""
import argparse
import os
import re
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")

HEX = re.compile(r"\b[0-9a-f]{10}\b")
CITE = re.compile(r"`([A-Za-z0-9_.]+\.(?:c|h|cpp))(?::(\d+)(?:-(\d+))?)?`(?:,? lines? (\d+)(?:-(\d+))?)?")


def pages(docs):
    for root, _, names in os.walk(docs):
        for name in sorted(names):
            if name.endswith(".md"):
                path = os.path.join(root, name)
                with open(path, encoding="utf-8") as f:
                    yield os.path.relpath(path, docs), f.read().splitlines()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("docs")
    parser.add_argument("clone")
    parser.add_argument("shipped", nargs="?", default="HEAD")
    args = parser.parse_args()
    if not os.path.isdir(args.docs):
        raise SystemExit(f"{args.docs} is not a folder")

    def git(*a):
        return subprocess.run(["git", "-C", args.clone, *a], capture_output=True, text=True,
                              encoding="utf-8", errors="replace")

    if git("rev-parse", "--verify", args.shipped + "^{commit}").returncode != 0:
        raise SystemExit(f"{args.shipped} is not a commit in {args.clone}")

    twins = {}
    for line in git("log", "--format=%h|%ad|%s", "--date=iso-strict", args.shipped).stdout.splitlines():
        h, ad, s = line.split("|", 2)
        twins.setdefault((s, ad), h)

    seen = {}
    for rel, lines in pages(args.docs):
        for ln, text in enumerate(lines, 1):
            for m in HEX.finditer(text):
                seen.setdefault(m.group(0), []).append(f"{rel}:{ln}")

    on = off = 0
    for h, where in sorted(seen.items()):
        if git("cat-file", "-e", h + "^{commit}").returncode != 0:
            continue
        if git("merge-base", "--is-ancestor", h, args.shipped).returncode == 0:
            on += 1
            continue
        off += 1
        ad, s = git("log", "-1", "--format=%ad|%s", "--date=iso-strict", h).stdout.strip().split("|", 1)
        print(f"OFF {h} -> {twins.get((s, ad), 'no twin')}  {s[:70]}")
        for w in where:
            print(f"    {w}")
    print(f"fork hashes on the shipped history: {on}, off it: {off}")

    paths = {}
    for p in git("ls-tree", "-r", "--name-only", args.shipped).stdout.splitlines():
        paths.setdefault(os.path.basename(p), []).append(p)
    texts = {}

    def cited(path, n):
        if path not in texts:
            texts[path] = git("show", f"{args.shipped}:{path}").stdout.split("\n")
        lines = texts[path]
        return lines[n - 1].strip()[:90] if 0 < n <= len(lines) else "<past end>"

    count = 0
    for rel, lines in pages(args.docs):
        for ln, text in enumerate(lines, 1):
            for m in CITE.finditer(text):
                a, b = m.group(2) or m.group(4), m.group(3) or m.group(5)
                if not a or m.group(1) not in paths:
                    continue
                count += 1
                src = paths[m.group(1)][0]
                print(f"LINE {rel}:{ln} {m.group(1)}:{a}{'-' + b if b else ''} [{src}]")
                print(f"    before: ...{text[max(0, m.start() - 90):m.start()]}")
                print(f"    {a}: {cited(src, int(a))}")
                if b:
                    print(f"    {b}: {cited(src, int(b))}")
    print(f"fork line citations: {count}")
    return 1 if off else 0


if __name__ == "__main__":
    sys.exit(main())
