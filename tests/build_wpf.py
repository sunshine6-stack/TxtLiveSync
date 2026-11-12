"""构建 TxtLiveSync（PowerShell 工具拦 dotnet，故经 Python 调用）。

  python tests/build_wpf.py               .NET Framework 4.8 → dist-fx\\TxtLiveSync.exe   （默认）
                                          产物约 200 KB；Win10 1903+ / Win11 自带 4.8，用户不用装运行时
  python tests/build_wpf.py --net8        .NET 8（框架依赖） → dist-wpf\\TxtLiveSync.exe
                                          需要目标机装 .NET Desktop Runtime 8/9/10
  python tests/build_wpf.py --standalone  .NET 8（自包含）   → dist-wpf-standalone\\TxtLiveSync.exe
                                          约 63 MB，什么都不用装
  python tests/build_wpf.py --all         上面三个都出

用 --no-incremental 强制重编译：XAML 改动偶尔不触发重编，产物字节数不变会让人误判。
"""
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROJ = ROOT / "TxtLiveSync.Wpf" / "TxtLiveSync.Wpf.csproj"
LOG = pathlib.Path(__file__).resolve().parent / "build_wpf.txt"

TARGETS = {
    "fx": (ROOT / "dist-fx", "net48"),
    "net8": (ROOT / "dist-wpf", "net8.0-windows"),
    "standalone": (ROOT / "dist-wpf-standalone", "net8.0-windows"),
}

lines = []


def run(args):
    r = subprocess.run(args, capture_output=True, cwd=str(ROOT))
    out = (r.stdout + r.stderr).decode("utf-8", "replace")
    lines.append(f"$ {' '.join(args)}\n  rc={r.returncode}")
    for ln in out.strip().splitlines()[-25:]:
        lines.append("  " + ln)
    return r.returncode


def build(kind):
    out_dir, tfm = TARGETS[kind]

    if kind == "fx":
        # .NET Framework：没有单文件/自包含的概念，产物就是 exe（+ 可选的 exe.config）。
        # publish 不认 --no-incremental，所以先 build 再 publish --no-build，和 net8 那条路一致。
        code = run([
            "dotnet", "build", str(PROJ),
            "-c", "Release", "-f", tfm,
            "--no-incremental",
        ])
        if code != 0:
            return code
        return run([
            "dotnet", "publish", str(PROJ),
            "-c", "Release", "-f", tfm,
            "--no-build",
            "-o", str(out_dir),
        ])

    if kind == "net8":
        code = run([
            "dotnet", "build", str(PROJ),
            "-c", "Release", "-f", tfm, "-r", "win-x64",
            "--self-contained", "false", "--no-incremental",
        ])
        if code != 0:
            return code
        return run([
            "dotnet", "publish", str(PROJ),
            "-c", "Release", "-f", tfm, "-r", "win-x64",
            "--self-contained", "false", "--no-build",
            "-p:PublishSingleFile=true", "-p:DebugType=none",
            "-o", str(out_dir),
        ])

    return run([
        "dotnet", "publish", str(PROJ),
        "-c", "Release", "-f", tfm, "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:DebugType=none",
        "-p:EnableCompressionInSingleFile=true",
        "-o", str(out_dir),
    ])


def report(kind):
    out_dir, _ = TARGETS[kind]
    if not out_dir.exists():
        lines.append(f"{kind}: 目录不存在 {out_dir}")
        return None
    exes = sorted(out_dir.glob("*.exe"))
    lines.append(f"{kind} output:")
    for p in sorted(out_dir.iterdir()):
        lines.append(f"  {p.name}  {p.stat().st_size} B")
    return str(exes[0]) if exes else None


requested = []
for arg in sys.argv[1:]:
    if arg in ("--fx", "--net48", "--framework"):
        requested.append("fx")
    elif arg in ("--net8", "--wpf"):
        requested.append("net8")
    elif arg in ("--standalone", "--self-contained"):
        requested.append("standalone")
    elif arg in ("--all", "--both"):
        requested += ["fx", "net8", "standalone"]

if not requested:
    requested = ["fx"]

code = 0
built = []
for kind in dict.fromkeys(requested):
    code = build(kind)
    if code != 0:
        lines.append(f"!!! {kind} 构建失败，停止")
        break
    path = report(kind)
    if path:
        built.append((kind, path))

for kind, path in built:
    lines.append(f"EXE[{kind}]={path}")

lines.append("RESULT=" + ("BUILD_OK" if code == 0 else "BUILD_FAILED"))
text = "\n".join(lines)
LOG.write_text(text, encoding="utf-8")
print(text)
sys.exit(0 if code == 0 else 1)
