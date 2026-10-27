using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;

const int MaxLines = 5000;
// 74880 = ESP8266 boot ROM, 250000 = Marlin / 3D printers
int[] bauds = { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 74880, 115200,
                230400, 250000, 460800, 500000, 921600, 1000000, 2000000 };
// Rates offered by the "wrong baud?" banner, most common first
int[] suggestions = { 115200, 9600, 74880, 57600, 38400, 19200, 230400 };
string[] endings = { "No line ending", "Newline (\\n)", "Carriage return (\\r)", "Both (\\r\\n)" };
string[] endingValues = { "", "\n", "\r", "\r\n" };

var defaultText = new Vector4(0.9f, 0.9f, 0.9f, 1f);

var log = new List<LogLine>();
var incoming = new ConcurrentQueue<LogLine>();

string[] ports = SerialPort.GetPortNames();
int portIdx = 0, endingIdx = 1;
int baudSel = Array.IndexOf(bauds, 115200);   // index into bauds; bauds.Length means "Custom"
int customBaud = 115200;
string status = "Disconnected";
string input = "", filter = "";
bool autoScroll = true, showTime = true, paused = false, showSent = true;
bool warnBaud = true, baudDismissed = false;
var health = new BaudHealth();

// Frame pacing
int refreshHz = 60;
bool fpsCapActive = false;
double nextRateCheck = 0;

// Settings: ~/.config/.fluxwave/settings.json (hot-reloaded, autosaved)
string appDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".fluxwave");
Directory.CreateDirectory(appDir);
string settingsPath = Path.Combine(appDir, "settings.json");
string logDir = Path.Combine(appDir, "logs");
string[] endingKeys = { "none", "lf", "cr", "crlf" };   // same order as `endings`
var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};
var settings = new Settings();
var lastSaved = settings;
bool settingsBroken = false;          // invalid JSON on disk: don't overwrite the user's file
DateTime settingsStamp = DateTime.MinValue;
double nextSettingsCheck = 0;

Hex.TryParse(Settings.DefaultAccent, out Vector4 accent);

// Toast notifications
var toasts = new List<Toast>();
int toastId = 0;

SerialPort? port = null;
CancellationTokenSource? cts = null;

int CurrentBaud() => baudSel < bauds.Length ? bauds[baudSel] : customBaud;

bool Visible(LogLine l) =>
    (showSent || !l.Sent) &&
    (filter.Length == 0 || l.Plain.Contains(filter, StringComparison.OrdinalIgnoreCase));

void DrawLine(LogLine l)
{
    if (showTime) { ImGui.TextDisabled(l.Time); ImGui.SameLine(); }

    if (l.Segs.Count == 0) { ImGui.TextUnformatted(" "); return; }

    for (int i = 0; i < l.Segs.Count; i++)
    {
        if (i > 0) ImGui.SameLine(0, 0);
        var seg = l.Segs[i];
        // Color.W == 0 means "no ANSI color set" -> use the default text color
        ImGui.PushStyleColor(ImGuiCol.Text, l.Sent ? accent : seg.Color.W > 0 ? seg.Color : defaultText);
        ImGui.TextUnformatted(seg.Text);
        ImGui.PopStyleColor();
    }
}

void Connect()
{
    try
    {
        port = new SerialPort(ports[portIdx], CurrentBaud())
        {
            // Stops many ESP32/Arduino boards from auto-resetting on open
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 200
        };
        port.Open();
        status = $"Connected: {port.PortName} @ {port.BaudRate}";
        health.Reset();
        baudDismissed = false;

        settings = settings with { LastPort = port.PortName };
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var p = port;

        new Thread(() =>
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var parser = new AnsiParser();
            var buf = new byte[4096];
            var chars = new char[4096];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    int n = p.BytesToRead;
                    if (n == 0) { Thread.Sleep(5); continue; }

                    int r = p.Read(buf, 0, Math.Min(n, buf.Length));
                    int c = decoder.GetChars(buf, 0, r, chars, 0);
                    health.Feed(chars, c);   // garbage detector (wrong baud rate)

                    for (int i = 0; i < c; i++)
                    {
                        var line = parser.Feed(chars[i]);
                        if (line != null) incoming.Enqueue(line);
                    }

                    // Unfinished line (e.g. a "esp32> " prompt) so the UI can show it live
                    Shared.Partial = parser.Snapshot();
                }
                catch (TimeoutException) { }
                catch { break; } // port unplugged etc.
            }
            Shared.Partial = null;
        })
        { IsBackground = true }.Start();
    }
    catch (Exception e) { status = "Error: " + e.Message; port = null; }
}

void Disconnect()
{
    cts?.Cancel();
    try { port?.Close(); } catch { }
    port = null;
    Shared.Partial = null;
    health.Reset();
    status = "Disconnected";
}

void SetBaud(int b)
{
    int i = Array.IndexOf(bauds, b);
    if (i >= 0) baudSel = i;
    else { baudSel = bauds.Length; customBaud = b; }
    OnBaudChanged();
}

// Called whenever the user picks or types a new baud rate. Applies it live if connected.
void OnBaudChanged()
{
    customBaud = Math.Clamp(customBaud, 50, 10_000_000);
    if (port is not { IsOpen: true }) return;

    try
    {
        port.BaudRate = CurrentBaud();
        status = $"Connected: {port.PortName} @ {port.BaudRate}";
    }
    catch (Exception e) { status = "Baud error: " + e.Message; }

    health.Reset();
    baudDismissed = false;
}

void Send()
{
    if (port is not { IsOpen: true } || input.Length == 0) return;
    try
    {
        port.Write(input + endingValues[endingIdx]);
        string text = "> " + input;
        log.Add(new LogLine(DateTime.Now.ToString("HH:mm:ss.fff"),
                            new List<Seg> { new(text, default) }, text, true));
        input = "";
    }
    catch (Exception e) { status = "Send error: " + e.Message; }
}

// ============================================================================
// Toasts
// ============================================================================

double Now() => Environment.TickCount64 / 1000.0;

void Notify(string title, string body, NoteKind kind = NoteKind.Info, string? copy = null, double seconds = 6)
{
    toasts.Add(new Toast { Id = ++toastId, Title = title, Body = body, Kind = kind, CopyText = copy, Expires = Now() + seconds });
    if (toasts.Count > 5) toasts.RemoveAt(0);
}

void DrawToasts()
{
    double now = Now();
    toasts.RemoveAll(x => x.Expires <= now);

    float sw = Raylib.GetScreenWidth(), sh = Raylib.GetScreenHeight();
    float w = Math.Min(480f, sw - 24f);
    const float padX = 12f;
    float y = sh - 12f;

    for (int i = toasts.Count - 1; i >= 0; i--)   // newest at the bottom
    {
        var t = toasts[i];
        var tint = t.Kind switch
        {
            NoteKind.Error => new Vector4(1f, 0.4f, 0.4f, 1f),
            NoteKind.Warn  => new Vector4(1f, 0.75f, 0.25f, 1f),
            _              => accent
        };
        float alpha = (float)Math.Clamp((t.Expires - now) / 0.4, 0.0, 1.0);   // fade out

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padX, 8f));
        ImGui.PushStyleColor(ImGuiCol.Border, tint);

        ImGui.SetNextWindowPos(new Vector2(sw - 12f, y), ImGuiCond.Always, new Vector2(1f, 1f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(w, 0f), new Vector2(w, float.MaxValue));
        ImGui.Begin($"##toast{t.Id}", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                                      ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                      ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove);

        ImGui.PushStyleColor(ImGuiCol.Text, tint);
        ImGui.TextUnformatted(t.Title);
        ImGui.PopStyleColor();

        ImGui.PushTextWrapPos(w - padX);
        ImGui.TextUnformatted(t.Body);
        ImGui.PopTextWrapPos();

        if (t.CopyText != null)
            ImGui.TextDisabled(t.Copied ? "Copied to clipboard" : "Click to copy path");

        if (ImGui.IsWindowHovered())
        {
            t.Expires = Math.Max(t.Expires, now + 1.5);   // don't vanish while the mouse is on it
            if (t.CopyText != null && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                ImGui.SetClipboardText(t.CopyText);
                t.Copied = true;
            }
        }

        t.Height = ImGui.GetWindowHeight();
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        y -= t.Height + 8f;
    }
}

// ============================================================================
// Accent color + settings.json
// ============================================================================

void ApplyAccent(Vector4 col)
{
    accent = col;
    var c = ImGui.GetStyle().Colors;
    Vector4 A(float alpha) => new(col.X, col.Y, col.Z, alpha);

    c[(int)ImGuiCol.Button]              = A(0.40f);
    c[(int)ImGuiCol.ButtonHovered]       = A(0.75f);
    c[(int)ImGuiCol.ButtonActive]        = A(1.00f);
    c[(int)ImGuiCol.FrameBgHovered]      = A(0.40f);
    c[(int)ImGuiCol.FrameBgActive]       = A(0.60f);
    c[(int)ImGuiCol.CheckMark]           = A(1.00f);
    c[(int)ImGuiCol.SliderGrab]          = A(0.80f);
    c[(int)ImGuiCol.SliderGrabActive]    = A(1.00f);
    c[(int)ImGuiCol.Header]              = A(0.31f);
    c[(int)ImGuiCol.HeaderHovered]       = A(0.80f);
    c[(int)ImGuiCol.HeaderActive]        = A(1.00f);
    c[(int)ImGuiCol.ScrollbarGrabActive] = A(1.00f);
    c[(int)ImGuiCol.TextSelectedBg]      = A(0.35f);
    c[(int)ImGuiCol.SeparatorHovered]    = A(0.78f);
    c[(int)ImGuiCol.SeparatorActive]     = A(1.00f);
    c[(int)ImGuiCol.ResizeGripHovered]   = A(0.67f);
    c[(int)ImGuiCol.ResizeGripActive]    = A(0.95f);
}

// Current UI state -> Settings (accent / logPrefix / lastPort come from `settings`)
Settings Snapshot() => settings with
{
    AutoScroll = autoScroll,
    Timestamps = showTime,
    ShowSent = showSent,
    BaudCheck = warnBaud,
    Baud = CurrentBaud(),
    LineEnding = endingKeys[endingIdx],
};

// Settings -> UI state
void ApplySettings(Settings s)
{
    settings = s;
    autoScroll = s.AutoScroll;
    showTime = s.Timestamps;
    showSent = s.ShowSent;
    warnBaud = s.BaudCheck;

    int e = Array.IndexOf(endingKeys, s.LineEnding?.ToLowerInvariant());
    if (e >= 0) endingIdx = e;

    int pi = Array.IndexOf(ports, s.LastPort);
    if (pi >= 0 && port is not { IsOpen: true }) portIdx = pi;

    if (s.Baud >= 50 && s.Baud != CurrentBaud()) SetBaud(s.Baud);   // applied live if connected

    if (Hex.TryParse(s.Accent, out var col)) ApplyAccent(col);
    else
    {
        Hex.TryParse(Settings.DefaultAccent, out var def);
        ApplyAccent(def);
        Notify("Invalid accent color",
               $"\"{s.Accent}\" is not a valid hex code (use #RGB or #RRGGBB). Using the default blue.",
               NoteKind.Warn);
    }
}

void SaveSettings(Settings s)
{
    lastSaved = s;   // set even on failure, so a read-only folder doesn't spam errors every frame
    try
    {
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(s, jsonOpts));
        settingsStamp = File.GetLastWriteTimeUtc(settingsPath);
    }
    catch (Exception e) { Notify("Could not save settings", e.Message, NoteKind.Error); }
}

void LoadSettings(bool startup)
{
    if (!File.Exists(settingsPath))
    {
        if (startup) ApplySettings(settings);   // defaults (this also applies the accent)
        SaveSettings(Snapshot());
        if (startup) Notify("Created settings.json", settingsPath, NoteKind.Info, copy: settingsPath);
        return;
    }

    settingsStamp = File.GetLastWriteTimeUtc(settingsPath);
    try
    {
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath), jsonOpts) ?? new Settings();
        settingsBroken = false;
        ApplySettings(s);
        lastSaved = s;
        if (!startup) Notify("settings.json reloaded", "Changes applied.", NoteKind.Info);
    }
    catch (Exception e)
    {
        settingsBroken = true;
        if (startup) ApplySettings(settings);
        Notify("settings.json is invalid",
               e.Message + "\nFix it and it reloads automatically. Changes are not saved until then.",
               NoteKind.Error, seconds: 10);
    }
}

// ============================================================================
// Saving the log
// ============================================================================

string SafePrefix(string? prefix)
{
    var bad = Path.GetInvalidFileNameChars();
    string p = new string((prefix ?? "").Trim().Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
    return p.Length == 0 ? "serial" : p;
}

void SaveLog()
{
    var lines = log.Where(Visible).Select(x => $"[{x.Time}] {x.Plain}").ToList();
    if (lines.Count == 0)
    {
        Notify("Nothing to save", "The log is empty (or everything is filtered out).", NoteKind.Warn);
        return;
    }

    try
    {
        Directory.CreateDirectory(logDir);
        string path = Path.GetFullPath(Path.Combine(logDir,
            $"{SafePrefix(settings.LogPrefix)}_{DateTime.Now:yyyyMMdd_HHmmss}.log"));
        File.WriteAllLines(path, lines);
        Notify($"Saved {lines.Count} lines", path, NoteKind.Info, copy: path);
    }
    catch (Exception e) { Notify("Could not save log", e.Message, NoteKind.Error); }
}

// V-Sync locks the frame rate to the monitor's refresh rate (60 / 75 / 144 / 165 Hz ...)
Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
Raylib.InitWindow(1000, 650, "FluxWave");
int firstHz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
refreshHz = firstHz > 0 ? firstHz : 60;
rlImGui.Setup(true);
LoadSettings(startup: true);

while (!Raylib.WindowShouldClose())
{
    // Re-check the monitor's refresh rate every 2s (follows the window across monitors)
    if (Raylib.GetTime() >= nextRateCheck)
    {
        nextRateCheck = Raylib.GetTime() + 2.0;
        int hz = Raylib.GetMonitorRefreshRate(Raylib.GetCurrentMonitor());
        if (hz > 0) refreshHz = hz;

        // V-Sync is only a hint. If we are clearly running faster than the monitor,
        // the driver ignored it, so fall back to a manual cap at the refresh rate.
        if (!fpsCapActive && Raylib.GetFPS() > refreshHz * 1.3f) fpsCapActive = true;
        if (fpsCapActive) Raylib.SetTargetFPS(refreshHz);
    }

    // settings.json: hot-reload when edited on disk, autosave when changed in the UI
    if (Now() >= nextSettingsCheck)
    {
        nextSettingsCheck = Now() + 0.5;
        if (!File.Exists(settingsPath) || File.GetLastWriteTimeUtc(settingsPath) != settingsStamp)
            LoadSettings(startup: false);
    }
    if (!settingsBroken)
    {
        var snap = Snapshot();
        if (snap != lastSaved) SaveSettings(snap);
    }

    if (!paused)
    {
        while (incoming.TryDequeue(out var l)) log.Add(l);
        if (log.Count > MaxLines) log.RemoveRange(0, log.Count - MaxLines);
    }

    Raylib.BeginDrawing();
    Raylib.ClearBackground(Color.Black);
    rlImGui.Begin();

    ImGui.SetNextWindowPos(Vector2.Zero);
    ImGui.SetNextWindowSize(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()));
    ImGui.Begin("##main", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                          ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                          ImGuiWindowFlags.NoBringToFrontOnFocus);

    bool connected = port is { IsOpen: true };

    // --- Header ---
    ImGui.SetWindowFontScale(1f);
    ImGui.TextUnformatted("FluxWave");
    ImGui.SetWindowFontScale(1f);
    ImGui.SameLine();
    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));
    ImGui.TextUnformatted("by Pradosh (v1.0.0)");
    ImGui.PopStyleColor();
    ImGui.Separator();

    // --- Top bar ---
    ImGui.BeginDisabled(connected);
    ImGui.SetNextItemWidth(160);
    if (ports.Length > 0) ImGui.Combo("##port", ref portIdx, ports, ports.Length);
    else ImGui.TextDisabled("No ports");
    ImGui.SameLine();
    if (ImGui.Button("Refresh")) { ports = SerialPort.GetPortNames(); portIdx = 0; }
    ImGui.EndDisabled();   // the port can't change while connected...

    // ...but the baud rate can: changes are applied to the open port live
    ImGui.SameLine();
    ImGui.SetNextItemWidth(110);
    string baudLabel = baudSel < bauds.Length ? bauds[baudSel].ToString() : "Custom";
    if (ImGui.BeginCombo("##baud", baudLabel))
    {
        for (int i = 0; i < bauds.Length; i++)
            if (ImGui.Selectable(bauds[i].ToString(), i == baudSel)) { baudSel = i; OnBaudChanged(); }
        if (ImGui.Selectable("Custom...", baudSel == bauds.Length)) { baudSel = bauds.Length; OnBaudChanged(); }
        ImGui.EndCombo();
    }
    if (baudSel == bauds.Length)
    {
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("##custombaud", ref customBaud, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue))
            OnBaudChanged();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Type any baud rate and press Enter");
    }

    ImGui.SameLine();
    if (!connected) { if (ImGui.Button("Connect") && ports.Length > 0) Connect(); }
    else if (ImGui.Button("Disconnect")) Disconnect();

    ImGui.SameLine();
    ImGui.TextDisabled($"{status}   |   {Raylib.GetFPS()} FPS ({refreshHz} Hz{(fpsCapActive ? ", capped" : "")})");

    // --- Options row ---
    ImGui.Checkbox("Autoscroll", ref autoScroll);   ImGui.SameLine();
    ImGui.Checkbox("Timestamps", ref showTime);     ImGui.SameLine();
    ImGui.Checkbox("Show sent (>)", ref showSent);  ImGui.SameLine();
    ImGui.Checkbox("Pause", ref paused);            ImGui.SameLine();
    ImGui.Checkbox("Baud check", ref warnBaud);            ImGui.SameLine();
    if (ImGui.Button("Clear")) log.Clear();         ImGui.SameLine();
    if (ImGui.Button("Copy"))
        ImGui.SetClipboardText(string.Join("\n", log.Where(Visible).Select(x => x.Plain)));
    ImGui.SameLine();
    if (ImGui.Button("Save")) SaveLog();
    ImGui.SameLine();
    ImGui.SetNextItemWidth(200);
    ImGui.InputTextWithHint("##filter", "Filter...", ref filter, 128);

    // --- Wrong-baud banner ---
    if (connected && warnBaud && health.Suspect && !baudDismissed)
    {
        var st = ImGui.GetStyle();
        float h = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() + st.WindowPadding.Y * 2;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.45f, 0.30f, 0.05f, 1f));
        ImGui.BeginChild("##baudwarn", new Vector2(0, h), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar);

        ImGui.TextUnformatted($"(!) {health.BadRatio * 100:F0}% of recent characters look garbled. " +
                              $"The baud rate ({CurrentBaud()}) may be wrong.");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Try:");
        int shown = 0;
        foreach (int b in suggestions)
        {
            if (b == CurrentBaud() || shown >= 4) continue;
            ImGui.SameLine();
            if (ImGui.Button(b.ToString())) SetBaud(b);
            if (b == 74880 && ImGui.IsItemHovered()) ImGui.SetTooltip("ESP8266 boot ROM messages use 74880");
            shown++;
        }
        ImGui.SameLine();
        if (ImGui.Button("Dismiss")) baudDismissed = true;

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }
    if (!health.Suspect) baudDismissed = false;   // re-arm once the data looks clean again

    // --- Log area ---
    float footer = ImGui.GetFrameHeightWithSpacing() + 4;
    ImGui.BeginChild("##log", new Vector2(0, -footer),
                     ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);

    foreach (var line in log)
        if (Visible(line)) DrawLine(line);

    var partial = Shared.Partial;
    if (partial != null && filter.Length == 0) DrawLine(partial);

    if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1f)
        ImGui.SetScrollHereY(1f);
    ImGui.EndChild();

    // --- Send bar ---
    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 260);
    bool enter = ImGui.InputText("##send", ref input, 512, ImGuiInputTextFlags.EnterReturnsTrue);
    ImGui.SameLine();
    ImGui.SetNextItemWidth(160);
    ImGui.Combo("##ending", ref endingIdx, endings, endings.Length);
    ImGui.SameLine();
    if (ImGui.Button("Send") || enter)
    {
        Send();
        ImGui.SetKeyboardFocusHere(-1);
    }

    ImGui.End();

    DrawToasts();

    rlImGui.End();
    Raylib.EndDrawing();
}

if (!settingsBroken)
{
    var fin = Snapshot();
    if (fin != lastSaved) SaveSettings(fin);
}

Disconnect();
rlImGui.Shutdown();
Raylib.CloseWindow();

// ============================================================================
// Types (must come after top-level statements)
// ============================================================================

readonly record struct Seg(string Text, Vector4 Color);

sealed record LogLine(string Time, List<Seg> Segs, string Plain, bool Sent);

static class Shared
{
    public static volatile LogLine? Partial;
}

/// <summary>
/// Mini terminal emulator for one line at a time. Handles:
///   SGR colors (16 / 256 / truecolor foreground), bold
///   ESC[K (erase in line), ESC[C/D/G (cursor), ESC[P (delete chars)
///   \r (carriage return = overwrite), \b, \t
/// Unknown sequences (including OSC and ?-private modes) are swallowed silently.
/// Color state persists across lines, like a real terminal.
/// </summary>
sealed class AnsiParser
{
    enum State { Text, Esc, Csi, Osc }
    struct Cell { public char Ch; public Vector4 Color; }

    State state = State.Text;
    readonly StringBuilder param = new();
    readonly List<Cell> cells = new();
    int cursor;

    // SGR state
    bool bold;
    int fgIdx = -1;      // -1 = default, 0..255 = palette index
    Vector4? fgRgb;      // truecolor override

    // ---- Public API ---------------------------------------------------------

    /// <summary>Feed one char. Returns a finished line on '\n', otherwise null.</summary>
    public LogLine? Feed(char c)
    {
        switch (state)
        {
            case State.Text:
                switch (c)
                {
                    case '\x1B': state = State.Esc; break;
                    case '\n': return Commit();
                    case '\r': cursor = 0; break;
                    case '\b': cursor = Math.Max(0, cursor - 1); break;
                    case '\t': do Put(' '); while (cursor % 8 != 0); break;
                    default: if (c >= ' ' && c != '\x7F') Put(c); break;
                }
                break;

            case State.Esc:
                if (c == '[') { param.Clear(); state = State.Csi; }
                else if (c == ']') state = State.Osc;
                else state = State.Text; // ESC c, ESC 7, ESC \ ... just drop
                break;

            case State.Csi:
                if (c >= '0' && c <= '?') param.Append(c);          // digits ; : < = > ?
                else if (c >= ' ' && c <= '/') { }                  // intermediates
                else if (c >= '@' && c <= '~') { ExecCsi(c); state = State.Text; }
                else state = State.Text;                            // malformed -> bail out
                break;

            case State.Osc:
                if (c == '\a') state = State.Text;
                else if (c == '\x1B') state = State.Esc;
                break;
        }
        return null;
    }

    /// <summary>Current unfinished line (or null if empty).</summary>
    public LogLine? Snapshot() => cells.Count == 0 ? null : Build();

    // ---- Internals ----------------------------------------------------------

    LogLine Commit()
    {
        var line = Build();
        cells.Clear();
        cursor = 0;
        return line;
    }

    LogLine Build()
    {
        var segs = new List<Seg>();
        var seg = new StringBuilder();
        var plain = new StringBuilder();

        int end = cells.Count;
        while (end > 0 && cells[end - 1].Ch == ' ') end--; // trim trailing spaces

        Vector4 cur = default;
        for (int i = 0; i < end; i++)
        {
            var cell = cells[i];
            if (seg.Length > 0 && cell.Color != cur)
            {
                segs.Add(new Seg(seg.ToString(), cur));
                seg.Clear();
            }
            cur = cell.Color;
            seg.Append(cell.Ch);
            plain.Append(cell.Ch);
        }
        if (seg.Length > 0) segs.Add(new Seg(seg.ToString(), cur));

        return new LogLine(DateTime.Now.ToString("HH:mm:ss.fff"), segs, plain.ToString(), false);
    }

    void Put(char ch)
    {
        while (cells.Count < cursor) cells.Add(new Cell { Ch = ' ' });
        var cell = new Cell { Ch = ch, Color = CurrentColor() };
        if (cursor < cells.Count) cells[cursor] = cell; else cells.Add(cell);
        cursor++;
    }

    void ExecCsi(char fin)
    {
        string ps = param.ToString();
        if (ps.Length > 0 && ps[0] == '?') return; // private modes (cursor show/hide, etc.)

        int[] a = ps.Length == 0
            ? Array.Empty<int>()
            : ps.Split(';').Select(s => int.TryParse(s, out var v) ? v : 0).ToArray();

        int Arg(int idx, int def) => idx < a.Length && a[idx] > 0 ? a[idx] : def;

        switch (fin)
        {
            case 'm':
                Sgr(a);
                break;

            case 'K':
            {
                int mode = a.Length > 0 ? a[0] : 0;
                if (mode == 0)
                {
                    if (cursor < cells.Count) cells.RemoveRange(cursor, cells.Count - cursor);
                }
                else if (mode == 1)
                {
                    int upto = Math.Min(cursor + 1, cells.Count);
                    for (int i = 0; i < upto; i++) cells[i] = new Cell { Ch = ' ' };
                }
                else cells.Clear();
                break;
            }

            case 'C': cursor += Arg(0, 1); break;
            case 'D': cursor = Math.Max(0, cursor - Arg(0, 1)); break;
            case 'G': cursor = Math.Max(0, Arg(0, 1) - 1); break;

            case 'P':
                if (cursor < cells.Count)
                    cells.RemoveRange(cursor, Math.Min(Arg(0, 1), cells.Count - cursor));
                break;

            case 'J':
                if (a.Length > 0 && a[0] >= 2) { cells.Clear(); cursor = 0; }
                break;
        }
    }

    void Sgr(int[] a)
    {
        if (a.Length == 0) a = new[] { 0 };

        for (int i = 0; i < a.Length; i++)
        {
            int p = a[i];

            if (p == 0) { bold = false; fgIdx = -1; fgRgb = null; }
            else if (p == 1) bold = true;
            else if (p == 22) bold = false;
            else if (p >= 30 && p <= 37) { fgIdx = p - 30; fgRgb = null; }
            else if (p == 39) { fgIdx = -1; fgRgb = null; }
            else if (p >= 90 && p <= 97) { fgIdx = p - 90 + 8; fgRgb = null; }
            else if (p == 38 && i + 1 < a.Length)
            {
                if (a[i + 1] == 5 && i + 2 < a.Length)
                {
                    fgIdx = Math.Clamp(a[i + 2], 0, 255); fgRgb = null; i += 2;
                }
                else if (a[i + 1] == 2 && i + 4 < a.Length)
                {
                    fgRgb = new Vector4(a[i + 2] / 255f, a[i + 3] / 255f, a[i + 4] / 255f, 1f);
                    i += 4;
                }
            }
            else if (p == 48 && i + 1 < a.Length)
            {
                // Background colors are ignored, but skip their sub-parameters
                // so "48;5;n" isn't misread as a foreground code.
                i += a[i + 1] == 5 ? 2 : a[i + 1] == 2 ? 4 : 0;
            }
        }
    }

    Vector4 CurrentColor()
    {
        if (fgRgb is Vector4 rgb) return rgb;
        if (fgIdx < 0) return default; // W == 0 -> "use default text color"
        int i = bold && fgIdx < 8 ? fgIdx + 8 : fgIdx;
        return Palette(i);
    }

    static readonly Vector4[] Base =
    {
        new(0.40f, 0.40f, 0.40f, 1), // black (lightened so it's visible on a dark bg)
        new(0.80f, 0.20f, 0.20f, 1), // red
        new(0.20f, 0.80f, 0.30f, 1), // green
        new(0.85f, 0.75f, 0.20f, 1), // yellow
        new(0.35f, 0.50f, 0.95f, 1), // blue
        new(0.80f, 0.30f, 0.80f, 1), // magenta
        new(0.20f, 0.75f, 0.80f, 1), // cyan
        new(0.80f, 0.80f, 0.80f, 1), // white
        new(0.55f, 0.55f, 0.55f, 1), // bright black
        new(1.00f, 0.40f, 0.40f, 1), // bright red
        new(0.40f, 1.00f, 0.50f, 1), // bright green
        new(1.00f, 0.95f, 0.40f, 1), // bright yellow
        new(0.50f, 0.65f, 1.00f, 1), // bright blue
        new(1.00f, 0.50f, 1.00f, 1), // bright magenta
        new(0.40f, 1.00f, 1.00f, 1), // bright cyan
        new(1.00f, 1.00f, 1.00f, 1), // bright white
    };

    static Vector4 Palette(int i)
    {
        if (i < 16) return Base[i];
        if (i < 232)
        {
            i -= 16;
            static float L(int v) => v == 0 ? 0f : (55 + v * 40) / 255f;
            return new Vector4(L(i / 36), L(i / 6 % 6), L(i % 6), 1f);
        }
        float g = (8 + (i - 232) * 10) / 255f;
        return new Vector4(g, g, g, 1f);
    }
}

/// <summary>
/// Watches decoded characters and flags a probably-wrong baud rate.
/// A wrong baud rate produces framing noise: NULs, control chars, 0xFF bytes
/// (decoded as U+FFFD) and C1 controls. Real text almost never contains those.
/// Evaluated in windows of 128 chars; two bad windows in a row raise the flag,
/// one clean window clears it. Thread-safe: Feed() runs on the reader thread,
/// Reset() and the flags are used from the UI thread.
/// </summary>
sealed class BaudHealth
{
    const int Window = 128;            // chars per evaluation window
    const float BadThreshold = 0.25f;  // >= 25% garbage = bad window
    const float GoodThreshold = 0.05f; // <= 5% garbage = clean window
    const int StrikesNeeded = 2;       // consecutive bad windows before warning

    readonly object gate = new();
    int total, bad, strikes;

    public volatile bool Suspect;
    public volatile float BadRatio;

    public void Feed(char[] chars, int count)
    {
        lock (gate)
        {
            for (int i = 0; i < count; i++)
            {
                total++;
                if (IsGarbage(chars[i])) bad++;
                if (total < Window) continue;

                float ratio = (float)bad / total;
                BadRatio = ratio;

                if (ratio >= BadThreshold)
                {
                    if (++strikes >= StrikesNeeded) Suspect = true;
                }
                else
                {
                    strikes = 0;
                    if (ratio <= GoodThreshold) Suspect = false;
                }
                total = bad = 0;
            }
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            total = bad = strikes = 0;
            Suspect = false;
            BadRatio = 0;
        }
    }

    static bool IsGarbage(char c) =>
        c == '\uFFFD' || c == '\0' || c == '\x7F' ||
        (c < ' ' && c != '\n' && c != '\r' && c != '\t' && c != '\b' && c != '\x1B') ||
        (c >= '\x80' && c <= '\x9F');
}

// ============================================================================
// Settings / toasts / hex colors
// ============================================================================

/// <summary>
/// Everything in settings.json. Missing keys fall back to the defaults below.
/// Edit the file by hand while the app runs: it hot-reloads.
/// </summary>
sealed record Settings
{
    public const string DefaultAccent = "#4DA6FF";

    public string Accent { get; init; } = DefaultAccent;   // "#RGB" or "#RRGGBB" (alpha is ignored)
    public string LogPrefix { get; init; } = "serial";     // log files: <logPrefix>_yyyyMMdd_HHmmss.log

    public bool AutoScroll { get; init; } = true;
    public bool Timestamps { get; init; } = true;
    public bool ShowSent { get; init; } = true;
    public bool BaudCheck { get; init; } = true;
    public int Baud { get; init; } = 115200;
    public string LineEnding { get; init; } = "lf";        // none | lf | cr | crlf
    public string LastPort { get; init; } = "";
}

enum NoteKind { Info, Warn, Error }

sealed class Toast
{
    public int Id;
    public string Title = "", Body = "";
    public NoteKind Kind;
    public string? CopyText;
    public double Expires;
    public float Height = 70;
    public bool Copied;
}

static class Hex
{
    /// <summary>Accepts "#RGB", "#RRGGBB" or "#RRGGBBAA", with or without the '#'. Alpha is dropped.</summary>
    public static bool TryParse(string? text, out Vector4 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string h = text.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
        if (h.Length is not (6 or 8)) return false;
        if (!uint.TryParse(h, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;

        if (h.Length == 8) v >>= 8;
        color = new Vector4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
        return true;
    }
}