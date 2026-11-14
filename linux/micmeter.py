#!/usr/bin/env python3
# MicMeter (Fedora/Linux) - Mikrofon seviye göstergesi
# Sağ altta, her zaman üstte duran küçük şerit. Ses arttıkça parlaklık artar.
# Gereken: python3-gobject, gtk3, python3-cairo ve parec / pw-record / arecord'dan biri.

import os
import sys

# Wayland'da uygulamalar kendini konumlandıramaz ve "her zaman üstte" olamaz.
# XWayland (x11) üzerinden çalışınca GNOME/KDE ikisine de izin veriyor.
os.environ.setdefault("GDK_BACKEND", "x11")

import array
import json
import math
import shutil
import signal
import subprocess

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402
import cairo  # noqa: E402,F401

APP = "micmeter"
CONFIG_DIR = os.path.join(os.path.expanduser("~"), ".config", APP)
CONFIG_FILE = os.path.join(CONFIG_DIR, "config.json")
AUTOSTART_FILE = os.path.join(os.path.expanduser("~"), ".config", "autostart", APP + ".desktop")

RATE = 16000
BLOCK = 640            # 40 ms
MIN_DB = -60.0         # bunun altı "sessiz"
SEGS = 22
SIZES = [("Mini", 124, 14), ("Küçük", 176, 22), ("Normal", 270, 44)]
NUM_GAIN = 2000       # 1-1000 ölçeği: RMS 0.0005 (çok hafif ses) = 1, RMS 0.5 = 1000


# ---------------- Ayarlar ----------------
def load_cfg():
    try:
        with open(CONFIG_FILE, encoding="utf-8") as f:
            c = json.load(f)
    except Exception:
        c = {}
    c.setdefault("size", 0)
    c.setdefault("source", None)
    c.setdefault("home", None)    # [x, y] — çift tıklayınca dönülecek yer
    if c["size"] not in (0, 1, 2):
        c["size"] = 0
    return c


def save_cfg(c):
    try:
        os.makedirs(CONFIG_DIR, exist_ok=True)
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(c, f)
    except Exception:
        pass


# ---------------- Mikrofon listesi ----------------
def list_sources():
    """[(name, açıklama)] — monitor (hoparlör çıkışı) kaynakları hariç."""
    if not shutil.which("pactl"):
        return []
    try:
        out = subprocess.run(["pactl", "-f", "json", "list", "sources"],
                             capture_output=True, text=True, timeout=3).stdout
        res = []
        for s in json.loads(out):
            name = s.get("name", "")
            if name.endswith(".monitor"):
                continue
            res.append((name, s.get("description") or name))
        return res
    except Exception:
        pass
    try:
        out = subprocess.run(["pactl", "list", "short", "sources"],
                             capture_output=True, text=True, timeout=3).stdout
        res = []
        for line in out.splitlines():
            parts = line.split("\t")
            if len(parts) > 1 and not parts[1].endswith(".monitor"):
                res.append((parts[1], parts[1]))
        return res
    except Exception:
        return []


# ---------------- Sistem mikrofonunu sessize alma ----------------
def _src(source):
    return source or "@DEFAULT_SOURCE@"


def get_mute(source):
    """True/False, okunamazsa None."""
    try:
        if shutil.which("pactl"):
            out = subprocess.run(["pactl", "get-source-mute", _src(source)],
                                 capture_output=True, text=True, timeout=2).stdout.lower()
            if "yes" in out or "evet" in out:
                return True
            if "no" in out or "hayır" in out:
                return False
        if shutil.which("wpctl") and not source:
            out = subprocess.run(["wpctl", "get-volume", "@DEFAULT_AUDIO_SOURCE@"],
                                 capture_output=True, text=True, timeout=2).stdout
            return "[MUTED]" in out
    except Exception:
        pass
    return None


def set_mute(source, mute):
    val = "1" if mute else "0"
    try:
        if shutil.which("pactl"):
            return subprocess.run(["pactl", "set-source-mute", _src(source), val],
                                  timeout=2).returncode == 0
        if shutil.which("wpctl") and not source:
            return subprocess.run(["wpctl", "set-mute", "@DEFAULT_AUDIO_SOURCE@", val],
                                  timeout=2).returncode == 0
    except Exception:
        pass
    return False


# ---------------- Ses yakalama ----------------
class Capture:
    def __init__(self):
        self.proc = None
        self.watch = None
        self.buf = b""
        self.first = True
        self.peak_rms = -1.0   # son tick'ten beri en yüksek RMS (-1 = veri yok)

    @property
    def alive(self):
        return self.proc is not None

    def start(self, source=None):
        self.stop()
        env = dict(os.environ)
        cmd = None
        if shutil.which("parec"):
            cmd = ["parec", "--rate=%d" % RATE, "--channels=1", "--format=s16le",
                   "--latency-msec=40", "--raw"]
            if source:
                cmd.append("--device=" + source)
        elif shutil.which("pw-record"):
            cmd = ["pw-record", "--rate", str(RATE), "--channels", "1", "--format", "s16"]
            if source:
                cmd += ["--target", source]
            cmd.append("-")
        elif shutil.which("arecord"):
            cmd = ["arecord", "-q", "-f", "S16_LE", "-r", str(RATE), "-c", "1", "-t", "raw"]
            if source:
                env["PULSE_SOURCE"] = source
                cmd += ["-D", "pulse"]
        if not cmd:
            return False
        try:
            self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE,
                                         stderr=subprocess.DEVNULL, env=env)
        except OSError:
            self.proc = None
            return False
        fd = self.proc.stdout.fileno()
        os.set_blocking(fd, False)
        self.buf = b""
        self.first = True
        self.watch = GLib.io_add_watch(fd, GLib.PRIORITY_DEFAULT,
                                       GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR, self._on_data)
        return True

    def _on_data(self, fd, cond):
        if not (cond & GLib.IO_IN):
            self._dead()
            return False
        try:
            data = os.read(fd, 65536)
        except BlockingIOError:
            return True
        except OSError:
            self._dead()
            return False
        if not data:
            self._dead()
            return False

        if self.first:
            self.first = False
            if data[:4] == b"RIFF":           # pw-record WAV başlığı yazarsa atla
                i = data.find(b"data")
                data = data[i + 8:] if i >= 0 else b""

        self.buf += data
        n = len(self.buf) // 2 * 2
        if n == 0:
            return True
        samples = array.array("h")
        samples.frombytes(self.buf[:n])
        self.buf = self.buf[n:]
        if sys.byteorder == "big":
            samples.byteswap()

        best = 0.0
        for i in range(0, len(samples), BLOCK):
            seg = samples[i:i + BLOCK]
            if not seg:
                continue
            rms = math.sqrt(sum(s * s for s in seg) / len(seg)) / 32768.0
            if rms > best:
                best = rms
        if best > self.peak_rms:
            self.peak_rms = best
        return True

    def take(self):
        v = self.peak_rms
        self.peak_rms = -1.0
        return v

    def _dead(self):
        self.watch = None
        if self.proc:
            try:
                self.proc.kill()
                self.proc.wait(timeout=1)
            except Exception:
                pass
        self.proc = None

    def stop(self):
        if self.watch:
            GLib.source_remove(self.watch)
            self.watch = None
        if self.proc:
            try:
                self.proc.terminate()
                self.proc.wait(timeout=1)
            except Exception:
                try:
                    self.proc.kill()
                except Exception:
                    pass
        self.proc = None


# ---------------- Renk yardımcıları ----------------
def clamp01(v):
    return 0.0 if v < 0 else (1.0 if v > 1 else v)


def mix(a, b, t):
    t = clamp01(t)
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(3))


def rgb(r, g, b):
    return (r / 255.0, g / 255.0, b / 255.0)


BG = rgb(16, 16, 20)
ACCENT = rgb(60, 235, 140)
GREEN, YELLOW, RED = rgb(40, 230, 120), rgb(255, 200, 40), rgb(255, 70, 60)


def seg_color(pos):
    return GREEN if pos < 0.65 else (YELLOW if pos < 0.85 else RED)


def round_rect(cr, x, y, w, h, r):
    cr.new_sub_path()
    cr.arc(x + w - r, y + r, r, -math.pi / 2, 0)
    cr.arc(x + w - r, y + h - r, r, 0, math.pi / 2)
    cr.arc(x + r, y + h - r, r, math.pi / 2, math.pi)
    cr.arc(x + r, y + r, r, math.pi, 3 * math.pi / 2)
    cr.close_path()


# ---------------- Pencere ----------------
class Meter(Gtk.Window):
    def __init__(self):
        super().__init__(type=Gtk.WindowType.TOPLEVEL)
        self.cfg = load_cfg()
        self.cap = Capture()
        self.level = 0.0
        self.peak = 0.0
        self.peak_hold = 0
        self.db = -90.0
        self.no_data = 0
        self.user_moved = False
        self.dragging = False
        self.drag_off = (0, 0)
        self.muted = False
        self.num = 0
        self.num_peak = 0.0
        self.num_ticks = 0
        self.press_icon = False
        self.mute_ticks = 0
        self.hand = None

        self.set_title("MicMeter")
        self.set_decorated(False)
        self.set_resizable(False)
        self.set_skip_taskbar_hint(True)
        self.set_skip_pager_hint(True)
        self.set_keep_above(True)
        self.set_accept_focus(False)
        self.set_focus_on_map(False)
        self.set_type_hint(Gdk.WindowTypeHint.UTILITY)
        self.stick()  # tüm çalışma alanlarında görünsün

        screen = self.get_screen()
        visual = screen.get_rgba_visual()
        self.rgba = bool(visual and screen.is_composited())
        if self.rgba:
            self.set_visual(visual)
        self.set_app_paintable(True)

        self.add_events(Gdk.EventMask.BUTTON_PRESS_MASK | Gdk.EventMask.BUTTON_RELEASE_MASK
                        | Gdk.EventMask.POINTER_MOTION_MASK)
        self.connect("draw", self.on_draw)
        self.connect("button-press-event", self.on_press)
        self.connect("button-release-event", self.on_release)
        self.connect("motion-notify-event", self.on_motion)
        self.connect("destroy", self.on_quit)

        self.apply_size()
        self.show_all()
        self.place()

        m = get_mute(self.cfg["source"])
        self.muted = bool(m)
        self.cap.start(self.cfg["source"])
        GLib.timeout_add(25, self.tick)
        GLib.timeout_add(1500, self.reassert_top)

    # ----- boyut / konum -----
    @property
    def mode(self):
        return self.cfg["size"]

    def apply_size(self):
        _, w, h = SIZES[self.mode]
        self.set_size_request(w, h)
        self.resize(w, h)
        if not self.user_moved:
            GLib.idle_add(self.place)
        self.queue_draw()

    def place(self):
        home = self.cfg.get("home")
        if home:
            self.move(int(home[0]), int(home[1]))
            return False
        return self.place_bottom_right()

    def place_bottom_right(self):
        display = Gdk.Display.get_default()
        mon = display.get_primary_monitor() or display.get_monitor(0)
        wa = mon.get_workarea()
        _, w, h = SIZES[self.mode]
        self.move(wa.x + wa.width - w - 12, wa.y + wa.height - h - 12)
        return False

    def reset_position(self):
        self.user_moved = False
        self.place()

    def save_home(self):
        x, y = self.get_position()
        self.cfg["home"] = [x, y]
        save_cfg(self.cfg)
        self.user_moved = False

    def reassert_top(self):
        self.set_keep_above(True)
        return True

    # ----- mikrofon simgesi -----
    def icon_geom(self):
        m = self.mode
        return (8, 12, 22)[m], self.get_allocated_height() / 2.0, (3.5, 6.5, 13)[m]

    def icon_hit(self, x, y):
        cx, cy, r = self.icon_geom()
        hit = r + (6 if self.mode == 0 else 4)
        return (x - cx) ** 2 + (y - cy) ** 2 <= hit * hit

    def toggle_mute(self):
        target = not self.muted
        if set_mute(self.cfg["source"], target):
            self.muted = target
        else:
            m = get_mute(self.cfg["source"])
            if m is not None:
                self.muted = m
        self.queue_draw()

    # ----- fare -----
    def on_press(self, w, e):
        if e.button == 1:
            on_icon = self.icon_hit(e.x, e.y)
            if e.type == Gdk.EventType._2BUTTON_PRESS:
                self.dragging = False
                if not on_icon:
                    self.reset_position()
            elif e.type == Gdk.EventType.BUTTON_PRESS:
                if on_icon:
                    self.press_icon = True
                else:
                    self.dragging = True
                    self.drag_off = (e.x, e.y)
        elif e.button == 3 and e.type == Gdk.EventType.BUTTON_PRESS:
            self.build_menu().popup_at_pointer(e)
        return True

    def on_release(self, w, e):
        if self.press_icon and e.button == 1 and self.icon_hit(e.x, e.y):
            self.toggle_mute()
        self.press_icon = False
        self.dragging = False
        return True

    def on_motion(self, w, e):
        if self.dragging:
            self.move(int(e.x_root - self.drag_off[0]), int(e.y_root - self.drag_off[1]))
            self.user_moved = True
        else:
            gw = self.get_window()
            if gw:
                if self.hand is None:
                    self.hand = Gdk.Cursor.new_from_name(gw.get_display(), "pointer")
                gw.set_cursor(self.hand if self.icon_hit(e.x, e.y) else None)
        return True

    # ----- menü -----
    def build_menu(self):
        menu = Gtk.Menu()

        mute = Gtk.MenuItem(label="Mikrofonu aç" if self.muted else "Mikrofonu kapat")
        mute.connect("activate", lambda *_: self.toggle_mute())
        menu.append(mute)
        menu.append(Gtk.SeparatorMenuItem())

        size_item = Gtk.MenuItem(label="Boyut")
        sub = Gtk.Menu()
        group = None
        for i, (name, _, _) in enumerate(SIZES):
            it = Gtk.RadioMenuItem.new_with_label_from_widget(group, name)
            group = it
            it.set_active(i == self.mode)
            it.connect("activate", self.on_size, i)
            sub.append(it)
        size_item.set_submenu(sub)
        menu.append(size_item)

        mic_item = Gtk.MenuItem(label="Mikrofon seç")
        sub = Gtk.Menu()
        group = Gtk.RadioMenuItem.new_with_label_from_widget(None, "Sistem varsayılanı")
        group.set_active(self.cfg["source"] is None)
        group.connect("activate", self.on_source, None)
        sub.append(group)
        for name, desc in list_sources():
            it = Gtk.RadioMenuItem.new_with_label_from_widget(group, desc)
            it.set_active(self.cfg["source"] == name)
            it.connect("activate", self.on_source, name)
            sub.append(it)
        mic_item.set_submenu(sub)
        menu.append(mic_item)

        home = Gtk.MenuItem(label="Bu konumu kaydet (çift tık buraya döner)")
        home.connect("activate", lambda *_: self.save_home())
        menu.append(home)

        reset = Gtk.MenuItem(label="Sağ alta taşı")
        reset.connect("activate", lambda *_: (setattr(self, "user_moved", True), self.place_bottom_right()))
        menu.append(reset)

        auto = Gtk.CheckMenuItem(label="Oturum açılınca başlat")
        auto.set_active(os.path.exists(AUTOSTART_FILE))
        auto.connect("toggled", self.on_autostart)
        menu.append(auto)

        menu.append(Gtk.SeparatorMenuItem())
        q = Gtk.MenuItem(label="Çıkış")
        q.connect("activate", lambda *_: self.destroy())
        menu.append(q)

        menu.show_all()
        return menu

    def on_size(self, item, i):
        if item.get_active() and self.cfg["size"] != i:
            self.cfg["size"] = i
            save_cfg(self.cfg)
            self.apply_size()

    def on_source(self, item, name):
        if item.get_active() and self.cfg["source"] != name:
            self.cfg["source"] = name
            save_cfg(self.cfg)
            self.level = self.peak = 0.0
            self.db = -90.0
            self.cap.start(name)
            m = get_mute(name)
            self.muted = bool(m)

    def on_autostart(self, item):
        if item.get_active():
            os.makedirs(os.path.dirname(AUTOSTART_FILE), exist_ok=True)
            exe = os.path.abspath(sys.argv[0])
            with open(AUTOSTART_FILE, "w", encoding="utf-8") as f:
                f.write("[Desktop Entry]\nType=Application\nName=MicMeter\n"
                        "Exec=%s\nX-GNOME-Autostart-enabled=true\nNoDisplay=true\n" % exe)
        else:
            try:
                os.remove(AUTOSTART_FILE)
            except FileNotFoundError:
                pass

    # ----- ölçüm -----
    def tick(self):
        rms = self.cap.take()
        if rms >= 0:
            self.no_data = 0
            db = 20 * math.log10(rms) if rms > 0 else -90.0
            db = max(db, -90.0)
            target = clamp01((db - MIN_DB) / (0 - MIN_DB))
            self.level += (target - self.level) * (0.65 if target > self.level else 0.12)
            self.db += (db - self.db) * 0.25
            self.num_peak = max(self.num_peak, rms)
        else:
            self.no_data += 1
            if self.no_data > 20:
                self.level *= 0.85
                self.db = -90.0
            if self.no_data % 120 == 0 and not self.cap.alive:   # ~3 sn'de bir yeniden dene
                self.cap.start(self.cfg["source"])

        self.num_ticks += 1
        if self.num_ticks >= 4:        # sayı 10 kez/sn güncellenir (okunabilsin diye)
            self.num_ticks = 0
            self.num = 0 if self.muted else min(1000, int(round(self.num_peak * NUM_GAIN)))
            self.num_peak = 0.0

        self.mute_ticks += 1
        if self.mute_ticks >= 20:      # ~0.5 sn: dışarıdan (klavye, ayarlar) değişirse yakala
            self.mute_ticks = 0
            m = get_mute(self.cfg["source"])
            if m is not None:
                self.muted = m

        if self.level >= self.peak:
            self.peak, self.peak_hold = self.level, 35
        elif self.peak_hold > 0:
            self.peak_hold -= 1
        else:
            self.peak = max(self.level, self.peak - 0.012)

        self.queue_draw()
        return True

    # ----- çizim -----
    def on_draw(self, w, cr):
        m = self.mode
        W, H = self.get_allocated_width(), self.get_allocated_height()
        lv = self.level
        no_mic = not self.cap.alive
        rad = (7, 9, 12)[m]

        cr.set_operator(0)  # CLEAR
        cr.paint()
        cr.set_operator(2)  # OVER

        # Arka plan (ses arttıkça hafif aydınlanır)
        round_rect(cr, 0, 0, W, H, rad if self.rgba else 0)
        cr.set_source_rgba(*mix(BG, rgb(22, 60, 40), lv), 0.96)
        cr.fill()

        # Kenar: ses arttıkça parlar
        round_rect(cr, 0.75, 0.75, W - 1.5, H - 1.5, rad)
        if self.muted:
            cr.set_source_rgba(230 / 255.0, 60 / 255.0, 60 / 255.0, 0.8)
        else:
            cr.set_source_rgba(*ACCENT, (50 + 205 * lv) / 255.0)
        cr.set_line_width(1.0 if m == 0 else 1.5)
        cr.stroke()

        # Parlayan nokta
        cx, cy, r = self.icon_geom()
        glow = r * (1 + 0.45 * lv) + r * 0.3
        grad = cairo.RadialGradient(cx, cy, 0, cx, cy, glow)
        grad.add_color_stop_rgba(0, ACCENT[0], ACCENT[1], ACCENT[2], 0.67 * lv)
        grad.add_color_stop_rgba(1, ACCENT[0], ACCENT[1], ACCENT[2], 0)
        cr.set_source(grad)
        cr.arc(cx, cy, glow, 0, 2 * math.pi)
        cr.fill()

        if self.muted:
            dot = rgb(215, 50, 50)
        elif no_mic:
            dot = rgb(90, 40, 40)
        else:
            dot = mix(rgb(35, 55, 45), ACCENT, 0.15 + 0.85 * lv)
        cr.set_source_rgb(*dot)
        cr.arc(cx, cy, r, 0, 2 * math.pi)
        cr.fill()
        if m >= 1:
            col = (1, 1, 1) if self.muted else (rgb(10, 30, 18) if lv > 0.5 else rgb(220, 240, 230))
            self.draw_mic(cr, cx, cy, r, col)
            if self.muted:           # üstü çizili mikrofon
                k = r * 0.62
                cr.set_source_rgb(1, 1, 1)
                cr.set_line_width(max(1.5, r * 0.16))
                cr.move_to(cx - k, cy - k)
                cr.line_to(cx + k, cy + k)
                cr.stroke()

        # Segmentli çubuk
        x0 = cx + r + (9 if m == 2 else 4)
        x1 = W - (32, 40, 74)[m]
        bar_h = (6, 9, 14)[m]
        y0 = cy - bar_h / 2.0
        gap = 2.0 if m == 2 else 1.0
        seg_w = ((x1 - x0) - gap * (SEGS - 1)) / SEGS
        bright = 0.45 + 0.55 * lv
        for i in range(SEGS):
            pos = (i + 1) / SEGS
            lit = clamp01((lv - i / SEGS) * SEGS)
            on = mix(BG, seg_color(pos), bright)
            off = mix(BG, seg_color(pos), 0.13)
            cr.set_source_rgb(*mix(off, on, lit))
            cr.rectangle(x0 + i * (seg_w + gap), y0, seg_w, bar_h)
            cr.fill()

        # Tepe çizgisi
        if self.peak > 0.02:
            px = x0 + (x1 - x0) * self.peak
            ext = 1 if m == 0 else 3
            cr.set_source_rgba(1, 1, 1, 0.9)
            cr.set_line_width(1.0 if m == 0 else 2.0)
            cr.move_to(px, y0 - ext)
            cr.line_to(px, y0 + bar_h + ext)
            cr.stroke()

        # Sondaki 1-1000 sayacı (+ Normal'de dB / durum)
        if self.muted:
            num_txt, status, sc = "0", "Kapalı", rgb(255, 90, 80)
        elif no_mic:
            num_txt, status, sc = "--", "Mik. yok", rgb(255, 110, 100)
        else:
            num_txt = str(self.num)
            if self.db < -50:
                status, sc = "Sessiz", rgb(130, 130, 140)
            elif self.db < -35:
                status, sc = "Düşük", YELLOW
            elif self.db < -10:
                status, sc = "İyi", ACCENT
            else:
                status, sc = "Yüksek", rgb(255, 80, 70)

        tx0, tw = x1 + 2, W - x1 - 4
        tc = sc if (no_mic or self.muted) else mix(rgb(150, 150, 160), (1, 1, 1), lv)
        if m == 2:
            db_txt = "-∞ dB" if self.db <= -89 else "%d dB" % round(self.db)
            self.text(cr, num_txt, tx0, tw, H * 0.33, 13, True, tc)
            self.text(cr, "%s · %s" % (db_txt, status) if not (no_mic or self.muted) else status,
                      tx0, tw, H * 0.73, 8, False, sc)
        else:
            self.text(cr, num_txt, tx0, tw, cy, (8, 10)[m], True, tc)
        return False

    @staticmethod
    def text(cr, s, x, w, cy, size, bold, color):
        cr.select_font_face("Sans", 0, 1 if bold else 0)
        cr.set_font_size(size)
        ext = cr.text_extents(s)
        cr.set_source_rgb(*color)
        cr.move_to(x + (w - ext.width) / 2 - ext.x_bearing, cy - ext.height / 2 - ext.y_bearing)
        cr.show_text(s)

    @staticmethod
    def draw_mic(cr, cx, cy, r, color):
        w, h = r * 0.55, r * 0.95
        cr.set_source_rgb(*color)
        round_rect(cr, cx - w / 2, cy - h * 0.75, w, h, w / 2)
        cr.fill()
        cr.set_line_width(max(1.0, r * 0.13))
        aw = r * 0.95
        cr.save()
        cr.translate(cx, cy - h * 0.55 + h * 0.475)
        cr.scale(aw / 2, h * 0.475)
        cr.arc(0, 0, 1, 0, math.pi)
        cr.restore()
        cr.stroke()
        cr.move_to(cx, cy + h * 0.4)
        cr.line_to(cx, cy + h * 0.62)
        cr.move_to(cx - w * 0.6, cy + h * 0.62)
        cr.line_to(cx + w * 0.6, cy + h * 0.62)
        cr.stroke()

    def on_quit(self, *_):
        self.cap.stop()
        Gtk.main_quit()


def main():
    win = Meter()
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGINT, lambda: win.destroy() or False)
    GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, signal.SIGTERM, lambda: win.destroy() or False)
    Gtk.main()


if __name__ == "__main__":
    main()
