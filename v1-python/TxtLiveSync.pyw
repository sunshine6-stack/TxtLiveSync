#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
TxtLiveSync —— 轻量级 TXT 实时同步查看 / 编辑器

设计约束（对应需求文档）：
  * 单文件、纯 Python 标准库（tkinter），无第三方依赖
  * 打开普通 txt 无延迟；主线程 400ms 轮询 mtime，不阻塞 UI
  * 外部变更默认静默同步（即使本地有未保存修改，也直接覆盖）——这是刻意的数据丢失行为，
    不要再加"保护性弹窗"，需求写死了。
  * 可选设置：外部变更时弹窗确认（默认关，持久化到 %APPDATA%）
  * 自保存写入后记录指纹，监视器不会误判为外部变更
  * UTF-8 / GBK（含 BOM、UTF-16 BOM）容错解码，保存保持原编码
  * CRLF / LF 原样保留；未改动正文时保存为逐字节回写
"""

from __future__ import annotations

import codecs
import hashlib
import json
import os
import sys
import time
import traceback
from dataclasses import dataclass
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

APP_NAME = "TxtLiveSync"
APP_TITLE = "TxtLiveSync — TXT 实时同步编辑器"
POLL_MS = 400                 # 需求 11：300–500ms
DEFAULT_NEWLINE = "\r\n"      # Windows 新建文件默认

# ---------------------------------------------------------------- 配置持久化


def config_dir() -> Path:
    base = os.environ.get("APPDATA") or str(Path.home() / ".config")
    return Path(base) / APP_NAME


def config_path() -> Path:
    return config_dir() / "config.json"


DEFAULT_CONFIG = {
    "confirm_on_external_change": False,   # 需求 4：默认关（静默同步）
    "last_path": "",
    "geometry": "",
    "zoomed": False,
    "word_wrap": False,
}


def load_config() -> dict:
    cfg = dict(DEFAULT_CONFIG)
    try:
        with open(config_path(), "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            for k in DEFAULT_CONFIG:
                if k in data:
                    cfg[k] = data[k]
    except (OSError, ValueError):
        pass
    return cfg


def save_config(cfg: dict) -> None:
    try:
        config_dir().mkdir(parents=True, exist_ok=True)
        tmp = config_path().with_suffix(".json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
        os.replace(tmp, config_path())
    except OSError:
        pass


# ---------------------------------------------------------------- 编解码 / 换行


def normalize_newlines(text: str) -> str:
    """界面内部统一使用 LF。"""
    if "\r" not in text:
        return text
    return text.replace("\r\n", "\n").replace("\r", "\n")


def detect_newline(raw: str) -> str | None:
    """返回文件主要使用的换行符（None 表示全文没有换行）。"""
    crlf = raw.count("\r\n")
    lf = raw.count("\n") - crlf
    cr = raw.count("\r") - crlf
    if crlf == 0 and lf == 0 and cr == 0:
        return None
    if crlf >= lf and crlf >= cr:
        return "\r\n"
    if cr >= lf:
        return "\r"
    return "\n"


def bom_bytes(encoding: str) -> bytes:
    return {
        "utf-8": codecs.BOM_UTF8,
        "utf-16-le": codecs.BOM_UTF16_LE,
        "utf-16-be": codecs.BOM_UTF16_BE,
    }.get(encoding, b"")


def decode_bytes(data: bytes) -> tuple[str, str, bool, bool]:
    """把字节解码为文本。返回 (text, encoding, has_bom, lossy)。

    lossy=True 表示遇到过无法解码的字节，用替换字符兜底显示（需求 8：容错显示而非退出）。
    """
    body, encoding, has_bom = data, None, False
    if data.startswith(codecs.BOM_UTF8):
        body, encoding, has_bom = data[3:], "utf-8", True
    elif data.startswith(codecs.BOM_UTF16_LE):
        body, encoding, has_bom = data[2:], "utf-16-le", True
    elif data.startswith(codecs.BOM_UTF16_BE):
        body, encoding, has_bom = data[2:], "utf-16-be", True

    if encoding:
        try:
            return body.decode(encoding), encoding, has_bom, False
        except UnicodeDecodeError:
            return body.decode(encoding, "replace"), encoding, has_bom, True

    for enc in ("utf-8", "gbk"):
        try:
            return body.decode(enc), enc, False, False
        except UnicodeDecodeError:
            continue
    return body.decode("gbk", "replace"), "gbk", False, True


@dataclass
class TextFile:
    """一份磁盘文本的快照。"""

    path: str
    text: str          # LF 规范化后的内容（与编辑区一致）
    raw: str           # 原样解码文本（保留原换行）
    encoding: str
    bom: bool
    newline: str
    size: int
    mtime_ns: int
    lossy: bool = False

    @property
    def sig(self) -> tuple[int, int]:
        return (self.mtime_ns, self.size)

    @property
    def name(self) -> str:
        return os.path.basename(self.path)


def build_text_file(path: str, data: bytes, st: os.stat_result | None = None) -> TextFile:
    raw, encoding, has_bom, lossy = decode_bytes(data)
    if st is None:
        st = os.stat(path)
    return TextFile(
        path=path,
        text=normalize_newlines(raw),
        raw=raw,
        encoding=encoding,
        bom=has_bom,
        newline=detect_newline(raw) or DEFAULT_NEWLINE,
        size=st.st_size,
        mtime_ns=st.st_mtime_ns,
        lossy=lossy,
    )


def read_text_file(path: str) -> TextFile:
    with open(path, "rb") as f:
        data = f.read()
    return build_text_file(path, data)


def build_save_bytes(tf: TextFile, text_lf: str) -> tuple[bytes, str]:
    """按原编码 / 原换行生成待写入字节。

    规则：正文没有改动时逐字节回写原始文本，保证编码、换行、BOM 一点不改（需求 9）。
    """
    text_lf = normalize_newlines(text_lf)
    if text_lf == normalize_newlines(tf.raw):
        raw = tf.raw
    elif tf.newline == "\r\n":
        raw = text_lf.replace("\n", "\r\n")
    elif tf.newline == "\r":
        raw = text_lf.replace("\n", "\r")
    else:
        raw = text_lf

    data = raw.encode(tf.encoding)          # 可能抛 UnicodeEncodeError，由调用方处理
    if tf.bom:
        data = bom_bytes(tf.encoding) + data
    return data, raw


def encoding_label(tf: TextFile | None) -> str:
    if tf is None:
        return ""
    label = tf.encoding.upper().replace("UTF-8", "UTF-8").replace("UTF-16-LE", "UTF-16 LE")
    if tf.bom:
        label += " BOM"
    if tf.lossy:
        label += "(容错)"
    return label


def newline_label(tf: TextFile | None) -> str:
    if tf is None:
        return ""
    return {"\r\n": "CRLF", "\n": "LF", "\r": "CR"}.get(tf.newline, "无换行")


# ---------------------------------------------------------------- 同步引擎


@dataclass
class Event:
    kind: str            # missing / recovered / reload / prompt
    message: str = ""
    loaded: TextFile | None = None


class SyncEngine:
    """纯粹的变更检测逻辑（不依赖 tkinter，便于无界面测试）。

    判定优先级：
      1. stat 失败            -> missing（只报一次，继续等待恢复）
      2. sig 没变             -> 什么都不做
      3. 内容是自己刚写的      -> 忽略（需求 5：自保存不误触发）
      4. 内容与编辑区一致      -> 只更新 sig（touch / 同内容另存）
      5. 其余                 -> reload（静默覆盖）或 prompt（弹窗确认）
    """

    def __init__(self) -> None:
        self.file: TextFile | None = None
        self.sig: tuple[int, int] | None = None
        self.pending_sig: tuple[int, int] | None = None   # 用户选择"保留本地"后忽略的版本
        self.awaiting_sig: tuple[int, int] | None = None  # 已弹窗、等待用户回答的版本
        self.missing = False
        self._own_digest: str | None = None

    # -- 生命周期 --

    def attach(self, tf: TextFile) -> None:
        self.file = tf
        self.sig = tf.sig
        self.pending_sig = None
        self.awaiting_sig = None
        self.missing = False
        self._own_digest = None

    def note_own_write(self, path: str, data: bytes) -> None:
        """本工具保存后调用：把新状态记为"已知状态"，监视器不会当成外部变更。"""
        st = os.stat(path)
        self.sig = (st.st_mtime_ns, st.st_size)
        self._own_digest = hashlib.sha1(data).hexdigest()
        self.missing = False
        self.pending_sig = None
        self.awaiting_sig = None
        if self.file is not None:
            self.file.mtime_ns = st.st_mtime_ns
            self.file.size = st.st_size

    def accept(self, tf: TextFile) -> None:
        """用户确认重新加载，采纳外部版本。"""
        self.attach(tf)

    def keep_local(self, sig: tuple[int, int]) -> None:
        """用户选择保留本地内容：记住这个外部版本，不再重复弹窗。"""
        self.pending_sig = sig
        self.awaiting_sig = None

    # -- 轮询 --

    def poll(self, get_text, confirm: bool) -> Event | None:
        tf = self.file
        if tf is None:
            return None
        path = tf.path

        try:
            st = os.stat(path)
        except (FileNotFoundError, NotADirectoryError):
            if not self.missing:
                self.missing = True
                return Event("missing", "文件已被删除或重命名，正在等待恢复…")
            return None
        except OSError as exc:
            if not self.missing:
                self.missing = True
                reason = getattr(exc, "strerror", None) or exc
                return Event("missing", f"文件暂时不可读（{reason}），正在等待恢复…")
            return None

        sig = (st.st_mtime_ns, st.st_size)
        was_missing = self.missing
        if was_missing:
            self.missing = False
        if sig == self.sig:
            return Event("recovered", "文件已恢复。") if was_missing else None

        if confirm and (sig == self.pending_sig or sig == self.awaiting_sig):
            # 用户已拒绝这个版本、或正在等用户回答：连内容都不用读
            return Event("recovered", "文件已恢复（保留本地内容）。") if was_missing else None

        # 读内容做二次判定
        try:
            with open(path, "rb") as f:
                data = f.read()
        except OSError as exc:
            self.missing = True
            reason = getattr(exc, "strerror", None) or exc
            return Event("missing", f"读取失败（{reason}），正在等待恢复…")

        if self._own_digest and hashlib.sha1(data).hexdigest() == self._own_digest:
            # 自己刚写入、但文件时间戳还没刷新的兜底判断
            self._own_digest = None
            self.sig = sig
            if was_missing:
                return Event("recovered", "文件已恢复。")
            return None

        new_tf = build_text_file(path, data, st)
        if new_tf.text == normalize_newlines(get_text()):
            # 内容没变（只被 touch、或被其他程序原样另存）
            self.sig = sig
            self.file = new_tf
            return Event("recovered", "文件已恢复。") if was_missing else None

        if confirm:
            self.awaiting_sig = sig
            return Event("prompt", "检测到外部修改，是否重新加载？", new_tf)

        self.pending_sig = None
        self.awaiting_sig = None
        self._own_digest = None
        self.sig = sig
        self.file = new_tf
        return Event("reload", "已同步外部修改。", new_tf)


# ---------------------------------------------------------------- 图形界面

COLOR_OK = "#1a7f37"
COLOR_WARN = "#b45309"
COLOR_ERR = "#b91c1c"
COLOR_MUTED = "#6b7280"


class App:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.cfg = load_config()
        self.engine = SyncEngine()
        self.current: TextFile | None = None
        self.dirty = False
        self._prompt_open = False
        self._last_dir = os.path.dirname(self.cfg.get("last_path") or "") or str(Path.home())

        self.root.title(APP_TITLE)
        self.root.geometry("980x660")
        self.root.minsize(420, 260)
        try:
            self.root.iconname(APP_NAME)
        except tk.TclError:
            pass

        self._build_ui()
        self._bind_keys()
        self._restore_state()
        self._apply_wrap()
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self._schedule_poll()

    # -- 界面构建 --

    def _build_ui(self) -> None:
        self._build_menu()

        bar = ttk.Frame(self.root, padding=(8, 6))
        bar.pack(side="top", fill="x")
        ttk.Button(bar, text="打开文件", command=self.open_file_dialog).pack(side="left")
        ttk.Button(bar, text="保存", command=self.save_file).pack(side="left", padx=(6, 0))
        ttk.Button(bar, text="从磁盘重新加载", command=self.reload_from_disk).pack(side="left", padx=(6, 0))

        self.hint = ttk.Label(bar, text="可拖拽 .txt 文件到窗口打开", foreground=COLOR_MUTED)
        self.hint.pack(side="right")

        # 状态栏必须在"可扩展"的正文之前 pack，否则会被挤出窗口
        status = ttk.Frame(self.root, padding=(10, 4))
        status.pack(side="bottom", fill="x")
        self.status_var = tk.StringVar(value="就绪")
        self.meta_var = tk.StringVar(value="未打开文件")
        self.status_label = ttk.Label(status, textvariable=self.status_var, foreground=COLOR_MUTED)
        self.status_label.pack(side="left")
        ttk.Label(status, textvariable=self.meta_var, foreground=COLOR_MUTED).pack(side="right")

        ttk.Separator(self.root, orient="horizontal").pack(side="bottom", fill="x")

        body = ttk.Frame(self.root)
        body.pack(side="top", fill="both", expand=True)

        self.text = tk.Text(
            body,
            undo=True,
            maxundo=-1,
            autoseparators=True,
            wrap="none",
            font=("Consolas", 11),
            bg="#ffffff",
            fg="#1f2328",
            insertbackground="#1f2328",
            selectbackground="#cfe3ff",
            relief="flat",
            highlightthickness=0,
            padx=10,
            pady=8,
        )
        self.ysb = ttk.Scrollbar(body, orient="vertical", command=self.text.yview)
        self.xsb = ttk.Scrollbar(body, orient="horizontal", command=self.text.xview)
        self.text.configure(yscrollcommand=self.ysb.set, xscrollcommand=self.xsb.set)
        self.text.grid(row=0, column=0, sticky="nsew")
        self.ysb.grid(row=0, column=1, sticky="ns")
        self.xsb.grid(row=1, column=0, sticky="ew")
        body.rowconfigure(0, weight=1)
        body.columnconfigure(0, weight=1)

        self.text.bind("<<Modified>>", self._on_modified)

        self._setup_dnd()

    def _build_menu(self) -> None:
        menubar = tk.Menu(self.root)

        m_file = tk.Menu(menubar, tearoff=0)
        m_file.add_command(label="打开文件…", accelerator="Ctrl+O", command=self.open_file_dialog)
        m_file.add_command(label="保存", accelerator="Ctrl+S", command=self.save_file)
        m_file.add_command(label="另存为…", accelerator="Ctrl+Shift+S", command=self.save_file_as)
        m_file.add_separator()
        m_file.add_command(label="从磁盘重新加载", accelerator="F5", command=self.reload_from_disk)
        m_file.add_separator()
        m_file.add_command(label="退出", command=self.on_close)
        menubar.add_cascade(label="文件", menu=m_file)

        m_view = tk.Menu(menubar, tearoff=0)
        self.wrap_var = tk.BooleanVar(value=bool(self.cfg.get("word_wrap")))
        m_view.add_checkbutton(label="自动换行", variable=self.wrap_var, command=self._apply_wrap)
        menubar.add_cascade(label="视图", menu=m_view)

        m_set = tk.Menu(menubar, tearoff=0)
        self.confirm_var = tk.BooleanVar(value=bool(self.cfg.get("confirm_on_external_change")))
        m_set.add_checkbutton(
            label="外部变更时弹窗确认",
            variable=self.confirm_var,
            command=self._toggle_confirm,
        )
        menubar.add_cascade(label="设置", menu=m_set)

        m_help = tk.Menu(menubar, tearoff=0)
        m_help.add_command(label="使用说明", command=self.show_help)
        m_help.add_command(label="关于", command=self.show_about)
        menubar.add_cascade(label="帮助", menu=m_help)

        self.root.config(menu=menubar)

    def _bind_keys(self) -> None:
        self.root.bind_all("<Control-o>", lambda e: (self.open_file_dialog(), "break")[1])
        self.root.bind_all("<Control-s>", lambda e: (self.save_file(), "break")[1])
        self.root.bind_all("<Control-Shift-S>", lambda e: (self.save_file_as(), "break")[1])
        self.root.bind_all("<F5>", lambda e: (self.reload_from_disk(), "break")[1])

    def _setup_dnd(self) -> None:
        """拖拽打开：若装了 tkinterdnd2 就启用，未安装也不影响运行（需求 2 的"更佳"项）。"""
        try:
            self.text.drop_target_register  # type: ignore[attr-defined]
            from tkinterdnd2 import DND_FILES  # type: ignore

            self.text.drop_target_register(DND_FILES)
            self.text.dnd_bind("<<Drop>>", self._on_drop)
        except Exception:
            pass

    # -- 状态与元信息 --

    def _set_status(self, msg: str, level: str = "muted") -> None:
        colors = {"ok": COLOR_OK, "warn": COLOR_WARN, "err": COLOR_ERR, "muted": COLOR_MUTED}
        self.status_var.set(msg)
        self.status_label.configure(foreground=colors.get(level, COLOR_MUTED))

    def _update_meta(self) -> None:
        tf = self.current
        self._update_title()
        if tf is None:
            self.meta_var.set("未打开文件")
            return
        parts = [newline_label(tf), encoding_label(tf)]
        if self.dirty:
            parts.append("未保存")
        else:
            parts.append("已保存")
        parts.append(f"{tf.size} 字节")
        self.meta_var.set(" · ".join(p for p in parts if p))

    def _update_title(self) -> None:
        mark = " *" if self.dirty else ""
        if self.current is None:
            self.root.title(f"未命名{mark} — {APP_NAME}")
            return
        self.root.title(f"{self.current.name}{mark} — {APP_NAME}")

    def _buffer_text(self) -> str:
        return self.text.get("1.0", "end-1c")

    def _on_modified(self, _event=None) -> None:
        if not self.text.edit_modified():
            return
        self.text.edit_modified(False)
        self._refresh_dirty()

    def _refresh_dirty(self) -> None:
        base = self.current.text if self.current is not None else ""
        dirty = self._buffer_text() != base
        if dirty != self.dirty:
            self.dirty = dirty
            self._update_meta()

    # -- 打开 / 载入 --

    def open_file_dialog(self) -> None:
        path = filedialog.askopenfilename(
            title="打开文本文件",
            initialdir=self._last_dir,
            filetypes=[("文本文件", "*.txt"), ("所有文件", "*.*")],
        )
        if path:
            self.open_path(path)

    def _on_drop(self, event) -> None:
        try:
            items = self.root.tk.splitlist(event.data)
        except Exception:
            items = [event.data]
        if items:
            self.open_path(items[0].strip("{}"))

    def open_path(self, path: str, quiet: bool = False) -> None:
        path = os.path.abspath(path)
        if self.current is not None and os.path.abspath(self.current.path) == path and not self.dirty:
            self._set_status("这个文件已经打开了。", "muted")
            return
        if not self._maybe_save():
            return
        try:
            tf = read_text_file(path)
        except OSError as exc:
            if not quiet:
                messagebox.showerror("打开失败", f"无法读取文件：\n{path}\n\n{exc}")
            self._set_status("打开失败。", "err")
            return

        self._last_dir = os.path.dirname(path)
        self.engine.attach(tf)
        self._load_into_ui(tf)
        self.cfg["last_path"] = path
        save_config(self.cfg)

        msg = f"已打开 {tf.name}"
        if tf.lossy:
            self._set_status(msg + "（存在无法识别字符，已容错显示）", "warn")
        else:
            self._set_status(msg, "ok")

    def _load_into_ui(self, tf: TextFile) -> None:
        first = self.text.yview()[0]
        self.text.configure(state="normal")
        self.text.delete("1.0", "end")
        self.text.insert("1.0", tf.text)
        self.text.edit_reset()
        self.text.edit_modified(False)
        self.text.yview_moveto(first)
        self.current = tf
        self.dirty = False
        self.hint.configure(text=tf.path)
        self._update_meta()
        self._update_title()

    def reload_from_disk(self) -> None:
        if self.current is None:
            return
        if not self._maybe_save():
            return
        try:
            tf = read_text_file(self.current.path)
        except OSError as exc:
            self._set_status(f"重新加载失败：{exc}", "err")
            messagebox.showerror("重新加载失败", f"文件可能已被删除或不可读：\n{exc}")
            return
        self.engine.attach(tf)
        self._load_into_ui(tf)
        self._set_status("已从磁盘重新加载。", "ok")

    # -- 保存 --

    def save_file(self) -> bool:
        if self.current is None:
            return self.save_file_as()
        tf = self.current
        text_lf = self._buffer_text()
        try:
            data, raw = build_save_bytes(tf, text_lf)
        except UnicodeEncodeError:
            answer = messagebox.askyesno(
                "编码无法表示",
                f"当前文件编码为 {tf.encoding.upper()}，无法表示你输入的某些字符。\n\n"
                "是否改用 UTF-8 保存？（文件编码会变更）",
            )
            if not answer:
                self._set_status("保存已取消。", "warn")
                return False
            tf.encoding = "utf-8"
            tf.bom = False
            try:
                data, raw = build_save_bytes(tf, text_lf)
            except UnicodeEncodeError as exc:
                messagebox.showerror("保存失败", str(exc))
                return False

        try:
            with open(tf.path, "wb") as f:
                f.write(data)
        except OSError as exc:
            messagebox.showerror("保存失败", f"无法写入文件：\n{tf.path}\n\n{exc}")
            self._set_status("保存失败。", "err")
            return False

        tf.raw = raw
        tf.text = normalize_newlines(raw)
        tf.lossy = False
        self.engine.note_own_write(tf.path, data)   # 需求 5：标记为自身写入
        self.text.edit_modified(False)
        self.dirty = False
        self._update_meta()
        self._set_status(f"已保存 {time.strftime('%H:%M:%S')}", "ok")
        return True

    def save_file_as(self) -> bool:
        if self.current is None:
            path = filedialog.asksaveasfilename(
                title="另存为",
                initialdir=self._last_dir,
                defaultextension=".txt",
                filetypes=[("文本文件", "*.txt"), ("所有文件", "*.*")],
            )
            if not path:
                return False
            tf = TextFile(
                path=os.path.abspath(path),
                text="",
                raw="",
                encoding="utf-8",
                bom=False,
                newline=DEFAULT_NEWLINE,
                size=0,
                mtime_ns=0,
            )
            self.current = tf
            self.engine.attach(tf)
            return self.save_file()

        path = filedialog.asksaveasfilename(
            title="另存为",
            initialdir=os.path.dirname(self.current.path),
            initialfile=self.current.name,
            defaultextension=".txt",
            filetypes=[("文本文件", "*.txt"), ("所有文件", "*.*")],
        )
        if not path:
            return False
        path = os.path.abspath(path)

        old = self.current
        text_lf = self._buffer_text()
        new_tf = TextFile(
            path=path,
            text=normalize_newlines(text_lf),
            raw=normalize_newlines(text_lf),
            encoding=old.encoding,
            bom=old.bom,
            newline=old.newline,
            size=0,
            mtime_ns=0,
        )
        try:
            data, raw = build_save_bytes(new_tf, text_lf)
            with open(path, "wb") as f:
                f.write(data)
        except UnicodeEncodeError:
            new_tf.encoding, new_tf.bom = "utf-8", False
            data, raw = build_save_bytes(new_tf, text_lf)
            with open(path, "wb") as f:
                f.write(data)
        except OSError as exc:
            messagebox.showerror("另存为失败", str(exc))
            return False

        new_tf.raw = raw
        new_tf.text = normalize_newlines(raw)
        self.current = new_tf
        self.engine.attach(new_tf)
        self.engine.note_own_write(path, data)
        self.text.edit_modified(False)
        self.dirty = False
        self.hint.configure(text=path)
        self.cfg["last_path"] = path
        save_config(self.cfg)
        self._update_meta()
        self._set_status("已另存为 " + os.path.basename(path), "ok")
        return True

    # -- 轮询外部变更 --

    def _schedule_poll(self) -> None:
        self.root.after(POLL_MS, self._poll_tick)

    def _poll_tick(self) -> None:
        try:
            self._check_external()
        except Exception:
            log_exception()
        finally:
            self._schedule_poll()

    def _check_external(self) -> None:
        if self.current is None or self._prompt_open:
            return
        ev = self.engine.poll(self._buffer_text, bool(self.confirm_var.get()))
        if ev is None:
            return
        if ev.kind == "missing":
            self._set_status(ev.message, "err")
        elif ev.kind == "recovered":
            self._set_status(ev.message, "ok")
        elif ev.kind == "reload":
            lost_local = self.dirty
            self._load_into_ui(ev.loaded)
            if lost_local:
                self._set_status("已同步外部修改（本地未保存的修改已被外部内容覆盖）", "warn")
            else:
                self._set_status("已同步外部修改。", "ok")
        elif ev.kind == "prompt":
            self._ask_reload(ev.loaded)

    def _ask_reload(self, tf: TextFile) -> None:
        self._prompt_open = True
        try:
            yes = messagebox.askyesno(
                "检测到外部修改",
                f"「{tf.name}」已被其他程序修改。\n\n是否重新加载外部内容？\n\n"
                "是：用磁盘上的内容替换当前编辑区\n"
                "否：保留当前编辑区内容（下次保存会覆盖外部修改）",
                parent=self.root,
            )
        finally:
            self._prompt_open = False
        if yes:
            self.engine.accept(tf)
            self._load_into_ui(tf)
            self._set_status("已重新加载外部内容。", "ok")
        else:
            self.engine.keep_local(tf.sig)
            self._set_status("已忽略这次外部修改，保留本地内容。", "warn")

    # -- 设置 / 其它 --

    def _toggle_confirm(self) -> None:
        self.cfg["confirm_on_external_change"] = bool(self.confirm_var.get())
        save_config(self.cfg)
        state = "开启" if self.cfg["confirm_on_external_change"] else "关闭（静默同步）"
        self._set_status(f"外部变更时弹窗确认：{state}", "muted")

    def _apply_wrap(self) -> None:
        wrap = bool(self.wrap_var.get())
        self.text.configure(wrap="word" if wrap else "none")
        if wrap:
            self.xsb.grid_remove()
        else:
            self.xsb.grid()
        self.cfg["word_wrap"] = wrap
        save_config(self.cfg)

    def show_help(self) -> None:
        messagebox.showinfo(
            "使用说明",
            "打开：文件 → 打开文件（Ctrl+O），或把 .txt 拖进窗口\n"
            "保存：Ctrl+S；另存为：Ctrl+Shift+S\n"
            "重新加载：F5\n\n"
            "外部变更默认静默同步：其他程序改动文件后，编辑区自动换成磁盘内容。\n"
            "本地有未保存修改时也会被覆盖，请注意。\n\n"
            "想改成先询问：设置 → 外部变更时弹窗确认。\n"
            "保存时保持原编码（UTF-8 / GBK）与原换行（CRLF / LF）。",
        )

    def show_about(self) -> None:
        messagebox.showinfo(
            "关于",
            f"{APP_NAME}\n\n轻量级 TXT 实时同步编辑器\n"
            f"Python {sys.version.split()[0]} + tkinter，纯标准库\n"
            f"轮询间隔 {POLL_MS} ms",
        )

    # -- 窗口状态 --

    def _restore_state(self) -> None:
        geo = self.cfg.get("geometry") or ""
        if geo:
            try:
                self.root.geometry(geo)
                self.root.update_idletasks()
                x, y = self.root.winfo_x(), self.root.winfo_y()
                sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
                if x < -50 or y < -50 or x > sw - 80 or y > sh - 80:
                    self.root.geometry("")     # 位置不合理就回默认
            except tk.TclError:
                pass
        if self.cfg.get("zoomed"):
            try:
                self.root.state("zoomed")
            except tk.TclError:
                pass

        last = self.cfg.get("last_path") or ""
        if last and os.path.isfile(last):
            self.open_path(last, quiet=True)
        else:
            self._set_status("就绪 · 打开一个 .txt 开始", "muted")

    def on_close(self) -> None:
        if not self._maybe_save():
            return
        try:
            zoomed = self.root.state() == "zoomed"
        except tk.TclError:
            zoomed = False
        self.cfg["zoomed"] = zoomed
        if not zoomed:
            self.cfg["geometry"] = self.root.geometry()
        if self.current is not None:
            self.cfg["last_path"] = self.current.path
        save_config(self.cfg)
        self.root.destroy()

    def _maybe_save(self) -> bool:
        self._refresh_dirty()
        if not self.dirty:
            return True
        name = self.current.name if self.current else "未命名"
        answer = messagebox.askyesnocancel(
            "有未保存的修改",
            f"「{name}」有未保存的修改，是否保存？",
            parent=self.root,
        )
        if answer is None:
            return False
        if answer:
            return self.save_file()
        return True


# ---------------------------------------------------------------- 入口与兜底


def log_exception() -> None:
    try:
        config_dir().mkdir(parents=True, exist_ok=True)
        with open(config_dir() / "error.log", "a", encoding="utf-8") as f:
            f.write(f"\n[{time.strftime('%Y-%m-%d %H:%M:%S')}]\n")
            f.write(traceback.format_exc())
    except OSError:
        pass


def main() -> None:
    root = tk.Tk()
    try:
        style = ttk.Style(root)
        if "vista" in style.theme_names():
            style.theme_use("vista")
    except tk.TclError:
        pass

    def report_exception(exc_type, exc_value, exc_tb):
        log_exception()
        try:
            messagebox.showerror("出错了", f"{exc_type.__name__}: {exc_value}\n\n详情已写入日志。")
        except Exception:
            pass

    sys.excepthook = report_exception
    app = App(root)
    # 支持命令行传文件（把 .txt 拖到 启动.bat 上，或做"打开方式"关联）
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    if args and os.path.isfile(args[0]):
        app.open_path(args[0], quiet=True)
    root.mainloop()


if __name__ == "__main__":
    main()
