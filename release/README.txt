TimeTrace 1.0.0
Understand where your time goes.
==========================================================================

TimeTrace is a local-first Windows application that measures how long each
application is in the foreground and shows it as interactive daily and weekly
timelines, dashboards and reports.

REQUIREMENTS
  Windows 10 or Windows 11, 64-bit. No .NET runtime needs to be installed:
  both TimeTrace.exe and the installer are self-contained.

INSTALLATION
  Option A - installer (recommended)
    1. Run TimeTrace_Setup.exe.
    2. Choose the install folder (default: your user profile, no administrator
       rights needed; an administrator can pick "all users" -> Program Files).
    3. Optionally create a desktop shortcut and launch TimeTrace.
    The installer does not start tracking and does not add TimeTrace to
    Windows startup. Upgrades install over the old version and keep your data.

  Option B - portable executable
    Copy TimeTrace.exe anywhere and run it. Nothing is written next to the
    executable. Start with:   TimeTrace.exe --data-dir D:\MyData\TimeTrace
    to keep all data in a folder of your choice (e.g. on a USB stick).

FIRST RUN
  TimeTrace explains what it records and asks for your consent before it
  tracks anything. You can also choose there whether it starts with Windows
  (off by default).

USING TIMETRACE
  Dashboard     Today / yesterday / 7 / 30 days / custom range: totals, top
                applications, charts, heatmap, category split, recent activity.
  Timeline      24-hour (daily) or 7-day (weekly) timeline. Hover blocks for
                details, click to open a session, Ctrl+mouse wheel to zoom,
                filter by application or category, delete single sessions.
  Applications  All detected applications with usage, sessions, last used and
                category. Double-click for the detail page. Exclude apps.
  Reports       Daily, weekly, monthly, custom, application and category
                reports as CSV, JSON or HTML into a folder you choose.
  Categories    Create, rename, recolour and delete categories; classification
                rules by executable name or path.
  Settings      General, tracking (idle threshold, exclusions), display,
                notifications, database (backup/restore/export), privacy.
  Tray icon     Open, pause/resume, today's statistics, timeline, exit.

DATA LOCATION
  Database : %LOCALAPPDATA%\TimeTrace\timetrace.db   (SQLite, UTC timestamps)
  Logs     : %LOCALAPPDATA%\TimeTrace\logs
  Icons    : %LOCALAPPDATA%\TimeTrace\icons
  Backups  : wherever you save them (Settings -> Database)
  The location can be changed in Settings -> Database.

PRIVACY
  Everything stays on your computer. TimeTrace has no account, no cloud
  synchronisation, no telemetry and never uploads anything.
  It records: the foreground application (name, executable, path), its window
  title (optional) and idle periods.
  It never records: keystrokes, mouse movement, screenshots, clipboard,
  microphone or webcam.
  You can pause, stop or completely disable tracking, exclude applications /
  paths / window titles, delete selected sessions or all history, and export
  everything. Categories are your own labels; TimeTrace does not claim to know
  whether your time was "productive".

KNOWN LIMITATIONS
  * PDF export is not included; open the HTML report and use "Print to PDF".
  * English is the only interface language.
  * Elevated (administrator) applications are tracked by process name, but
    their file path and icon may be unavailable. Windows may also block
    reading some window titles of protected processes.
  * Time while the PC is locked (default), asleep, or while TimeTrace is not
    running is "not observed". Reports show it separately and never invent it.
  * The activity of the last few seconds before an unexpected power loss may
    be lost (progress is saved every 30 seconds).
  * The installer and executable are not code-signed, so Windows SmartScreen
    may show a warning on first run.

LICENSE
  MIT. See LICENSE.txt.
