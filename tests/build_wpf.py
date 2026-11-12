"""构建 WPF 版 exe（PowerShell 工具拦 dotnet，故经 Python 调用）。

两种发布形态：

  python tests/build_wpf.py                 框架依赖（默认）：dist-wpf\\TxtLiveSync.exe，约 300 KB
                                            需要目标机器装 .NET Desktop Runtime 8/9/10（RollForward=LatestMajor）
  python tests/build_wpf.py --standalone    自包含：dist-wpf-standalone\\TxtLiveSync.exe，约 60 MB
                                            目标机器什么都不用装，拷贝即用
  python tests/build_wpf.py --both          两个都出

用 --no-incremental 强制重编译：XAML 改动偶尔不触发重编，产物字节数不变会让人误判。
不再手工删 bin/obj —— 安全删除策略会拦住大目录的 rmtree，交给 msbuild 自己处理。
"""
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROJ = ROOT / "TxtLiveSync.Wpf" / "TxtLiveSync.Wpf.csproj"
OUT_FW = ROOT / "dist-wpf"
OUT_SC = ROOT / "dist-wpf-standalone"
LOG = pathlib.Path(__file__).resolve().parent / "build_wpf.txt"

lines = []


def run(args):
    r = subprocess.run(args, capture_output=True, cwd=str(ROOT))
    out = (r.stdout + r.stderr).decode("utf-8", "replace")
    lines.append(f"$ {' '.join(args)}\n  rc={r.returncode}")
    for ln in out.strip().splitlines()[-25:]:
        lines.append("  " + ln)
    return r.returncode


def report(title, folder):
    if not folder.exists():
        lines.append(f"{title}: 目录不存在 {folder}")
        return None
    exes = sorted(folder.glob("*.exe"))
    lines.append(f"{title} output:")
    for p in sorted(folder.iterdir()):
        lines.append(f"  {p.name}  {p.stat().st_size} B")
    return str(exes[0]) if exes else None


def build_framework():
    code = run([
        "dotnet", "build", str(PROJ),
        "-c", "Release", "-r", "win-x64",
        "--self-contained", "false",
        "--no-incremental",
    ])
    if code == 0:
        code = run([
            "dotnet", "publish", str(PROJ),
            "-c", "Release", "-r", "win-x64",
            "--self-contained", "false",
            "--no-build",
            "-p:PublishSingleFile=true",
            "-p:DebugType=none",
            "-o", str(OUT_FW),
        ])
    return code


def build_standalone():
    code = run([
        "dotnet", "publish", str(PROJ),
        "-c", "Release", "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:DebugType=none",
        "-p:EnableCompressionInSingleFile=true",
        "-o", str(OUT_SC),
    ])
    return code


mode = "framework"
for arg in sys.argv[1:]:
    if arg in ("--standalone", "--self-contained"):
        mode = "standalone"
    elif arg in ("--both", "--all"):
        mode = "both"

code = 0
exes = []

if mode in ("framework", "both"):
    code = build_framework()
    if code == 0:
        e = report("framework-dependent", OUT_FW)
        if e:
            exes.append(("framework", e))

if code == 0 and mode in ("standalone", "both"):
    code = build_standalone()
    if code == 0:
        e = report("self-contained", OUT_SC)
        if e:
            exes.append(("standalone", e))

for kind, path in exes:
    lines.append(f"EXE[{kind}]={path}")

lines.append("RESULT=" + ("BUILD_OK" if code == 0 else "BUILD_FAILED"))
text = "\n".join(lines)
LOG.write_text(text, encoding="utf-8")
print(text)
sys.exit(0 if code == 0 else 1)
