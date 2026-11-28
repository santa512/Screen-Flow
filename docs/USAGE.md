# ScreenFlow

Version **0.1** · Windows x64 · [GitHub repository](https://github.com/santa512/Screen-Flow)

A native Windows desktop application for building repeatable mouse-and-keyboard workflows. Built with C# / WPF, Win32 `SendInput`, and OpenCV through OpenCvSharp. No browser DOM inspection, browser extensions, or browser debugging connections.

## Run

[Download ScreenFlow 0.1 for Windows x64](https://github.com/santa512/Screen-Flow/releases/download/v0.1/ScreenFlow-v0.1-win-x64.zip), extract the archive, and open `ScreenFlow\ScreenFlow.exe`. Keep the entire extracted folder together. The portable build includes .NET; ordinary applications do not require an installer or administrator rights.

To build from source, run `./build.ps1 -Test -Publish`, then open **`dist\ScreenFlow-Production\ScreenFlow.exe`**.

## Windows appearance

ScreenFlow uses Microsoft's built-in WPF Fluent controls, Windows accent colors, Segoe UI Variable typography (with Segoe UI as a fallback), and Segoe Fluent Icons (with Segoe MDL2 Assets as a fallback). Buttons retain native hover, pressed, disabled, and keyboard-focus states.

Choose **System**, **Light**, or **Dark** from the appearance selector at the top right. System follows Windows' app theme, including changes while ScreenFlow is open. The selection applies immediately to the editor and practice window and is remembered in `%LocalAppData%\ScreenFlow\settings.json`. The built-in Fluent theme also honors Windows contrast themes. Your workflow files do not store appearance preferences.

The interface uses native Windows styling; ScreenFlow remains an independent application.

The application icon uses the supplied mouse-circle SVG in `ScreenFlow/Assets/ScreenFlow.svg`. Its Windows `.ico` contains nine resolutions from 16 to 256 pixels. The header and window icons reuse the vector artwork with a theme-aware accent tint. `tools/IconAssets` generates these assets from the SVG.

## Build a workflow

1. Add actions from the library on the left.
2. Select a step and edit its settings on the right. Use **Pick point on screen** for fixed coordinates or **Capture visual target** to drag around a button or icon. Press Escape to cancel capture.
3. Reorder with the arrow buttons, duplicate steps, or uncheck steps to disable them. Changes apply when you choose **Apply changes**, switch steps, save, or run.
4. Set repeat cycles and the start delay. Choose **Run workflow**, then focus the destination application during the countdown. **F8 stops execution globally**, including during image searches and waits. The Stop button also cancels a run.
5. Save to a `.screenflow.json` file. Captured image targets are embedded, so there are no separate image files to manage. Workflows are editable JSON, not executable scripts.

The **practice window** provides a local text field, a sample address bar, and a button. Use it to try workflows without interacting with a browser. Browser actions use native window focus and keyboard input; no browser connection is established.

## Actions

| Action | Behavior |
| --- | --- |
| Focus browser | Find the active / last-used supported browser and bring its window to the foreground |
| New browser tab | Find and focus the browser, verify foreground focus, then send `Ctrl+T` |
| Click / Double click / Right click | Click a fixed desktop pixel coordinate |
| Move mouse | Position the pointer over a target, for example before scrolling |
| Scroll | Send wheel notches; negative values scroll down |
| Type text | Send Unicode keyboard input to the focused field |
| Hotkey | Send shortcuts such as `Ctrl+Tab`, `Ctrl+Shift+Tab`, `Ctrl+A`, `Alt+Tab`, or `Enter` |
| Delay | Wait for a specified duration |
| Find image & click | Search the visible desktop and click the center of the best qualifying match |
| Wait for image | Wait until a visible target reaches the configured confidence |
| Copy page URL | Send `Ctrl+L`, `Ctrl+C`, read the changed clipboard, and save the URL to a variable |
| Read clipboard | Store current clipboard text in a variable |
| Set variable | Set reusable text, optionally referencing earlier variables |
| If / Else / End if | Run a block only when a condition is true, with an optional Else block |
| Loop / End loop | Repeat a block a fixed number of times; the optional index variable counts 1, 2, 3… |
| Loop while | Repeat a block while a condition stays true |
| Exit loop | Leave the innermost loop and continue after its End loop |
| Label / Go to label | Jump to a named spot in the workflow |
| Stop workflow | End the run successfully, skipping remaining actions and cycles |
| Middle click | Middle-click a fixed desktop coordinate |
| Drag and drop | Press the left button at a start point, move to an end point, release |
| Launch application | Start a program, document or shortcut with optional arguments; optionally wait for it to exit |
| Open file or URL | Open a web address, file or folder with its default app |
| Focus window | Wait for a window whose title matches, restore it, and bring it to the front |
| Wait for window | Wait until a matching window opens, or closes |
| Close window | Ask the first matching window to close (like clicking X) |
| Run command | Run a hidden Command Prompt or PowerShell command; store its output and `{{exit_code}}` |
| Take screenshot | Save the whole desktop as a PNG |
| Show message | Show a message box and pause until OK |
| Ask for input | Ask for a value during the run; Cancel stops the workflow |
| Set clipboard | Put text on the clipboard |
| Increase variable | Add (or subtract) a number; missing variables start at 0 |
| Transform text | Trim, upper/lowercase, replace, regex extract, character count, or substring into a variable |
| Get date and time | Store the current date/time using a .NET format such as `yyyy-MM-dd HH:mm:ss` |
| Random number | Store a random whole number between a minimum and maximum |
| Read text file / Write text file | Read a file into a variable; write or append UTF-8 text, creating folders as needed |

Every non-delay action can pause afterward (flow-control markers run instantly). A failed action stops the workflow and identifies the failing step in the activity log. Input is sent to the currently focused app; there is no background interaction with hidden windows.

## Example: focus the browser and open a new tab

Choose **New browser tab** from the Browser action library, or **Load new browser tab example** from Run Settings. Run the workflow. It finds an existing browser window, restores it if minimized, verifies that it owns foreground focus, and sends `Ctrl+T` through real keyboard input. Add later actions to type a URL or perform the next step.

Selection prefers a supported browser already in the foreground, followed by the last browser used while ScreenFlow was open, followed by the frontmost available non-minimized browser. If only minimized browsers are available, one is restored. Supported browsers include Octo profile windows (Octium), Chrome, Edge, Firefox, Brave, Vivaldi, Opera, and Chromium. Closed browser windows and stale remembered window handles are discarded.

For Octo, open the desired profile first, bring its browser window to the front, then start the workflow. With multiple profiles open, the foreground profile takes priority; otherwise the most recently used profile is preferred. The Octo profile manager is not selected as a browser window. Ctrl+T, URL copying, typing, mouse actions, and visual image matching operate on the visible browser and its real websites using the same native input pipeline. No profile API or DOM connection is required.

Octo support is enabled in the production application. Development tests use synthetic Octium window records and ScreenFlow's own practice fixture; they never control Octo or visit real websites.

If no browser is open or Windows denies foreground activation, the action stops with an error before sending the shortcut. It also rechecks focus immediately before `Ctrl+T`. Existing saved workflows retain their behavior; add **Focus browser** first if you want to bring the browser forward before your own hotkey actions.

## Example: URL handoff between browser tabs

Use **Load URL handoff example**, then customize it:

1. **Focus browser** → find and activate the browser window.
2. **Copy page URL** → variable `url`.
3. **Hotkey** → `Ctrl+Tab`.
4. **Delay** → let the destination tab load.
5. **Click** → pick the destination field. This placeholder is disabled until you configure and enable it. A visual image click can replace it.
6. **Type text** → `{{url}}`. Enable this step after configuring the target field.
7. Add an image click on your destination button if needed.

The sample does not submit anything automatically. Configure and enable your own actions before running. Copy URL changes the clipboard just as pressing Ctrl+C would.

Variable names contain letters, digits, and underscores and start with a letter or underscore. Insert values with `{{name}}`. Values last for a run, including repeated cycles; they reset on the next run. Missing variables produce an error. Newlines in typed text press Enter, and tabs press Tab.

## Visual matching and desktop behavior

- Image matching uses OpenCV normalized correlation. Capture a distinctive icon, text, or button edge; plain flat targets are rejected. Default confidence is 0.92.
- Keep browser zoom, theme, and display scaling consistent. Optional scale search checks 80%, 90%, 100%, 110%, 125%, and 150% of the captured size. It takes longer and is not a general solution to redesigned controls.
- Targets must be visible on an unlocked desktop. If several identical targets are visible, the highest scoring target wins. Capture more surrounding context to distinguish them.
- Coordinates use physical pixels across the virtual desktop, including negative coordinates on secondary displays. Capture overlays map their selection back to physical pixels. Mixed-DPI layouts are supported by this mapping but have not been tested on physical multi-monitor hardware here.
- Windows permits injected input only into applications at an equal or lower privilege level. Run ScreenFlow and the target at the same privilege level. Secure desktops and lock screens are unsupported.
- F8 is reserved for stopping. It is registered only during a run, with an additional key-state polling fallback.

## Flow control

Adding **If**, **Loop**, or **Loop while** also adds the matching **End if** / **End loop**. New actions go directly below the selected step, so select the If (or a step inside it) before adding the actions that belong in the block. Blocks can nest; the step list indents their contents.

Conditions compare two values that can contain `{{variables}}`. When both sides are numbers they compare numerically (`10` is greater than `9`); otherwise text comparisons ignore case. Other conditions check whether a captured image is visible right now, whether a window with a matching title exists, or whether a file or folder exists.

Unchecking an If or Loop switches off the whole block. Else / End markers always keep the structure intact. Structure mistakes (a missing End if, a Go to with no matching label, Exit loop outside a loop) are reported before the run starts. F8 stops endless loops and jumps.

Window title matching ignores case and matches any title containing the text. Use `*` as a wildcard to match the whole title, for example `Report*Excel`.

Run command runs hidden without a console window. Command Prompt is the default; tick PowerShell to use it instead. A non-zero exit code does not stop the run, so branch on `{{exit_code}}` with If.

This version does not include passive action recording, OCR, or a scheduler.

## Develop and verify

Requires Windows x64 and a .NET SDK supporting .NET 10. Package restore requires internet access.

```powershell
./build.ps1 -Test -Publish
```

The build uses workspace-local NuGet packages and build settings. Build artifacts and downloaded packages are ignored by Git.

The 22 checks in `--self-test` cover native input structure layout, keyboard parsing, variable expansion, OpenCV exact/scaled/absent matches, invalid settings, embedded-image save/load, repeated runs, disabled steps, browser selection (including multiple synthetic Octium profiles), focus-before-input ordering, focus failure, lost focus, cancellation, new browser action save/load, If / Else / loop / Go to ordering, block-structure errors, and text, date, file and command actions. Browser tests use fake window candidates and send no real browser input.

An interactive integration test exercises native minimize/restore and foreground activation, real `Ctrl+T` input, desktop capture, OpenCV matching, URL copying, Unicode typing, keyboard shortcuts, and mouse clicks **only in ScreenFlow's own practice window**. It supplies that fixture as the sole browser candidate, so browser discovery is never used during the test:

```powershell
Start-Process ./dist/ScreenFlow-Production/ScreenFlow.exe -ArgumentList '--ui-self-test' -Wait
Get-Content ./ui-test-results.txt
```

Run it on an unlocked interactive desktop, click **Start local check** in the practice window, and leave it unobstructed until it finishes. This click grants normal foreground-input rights, matching the user pressing Run. It does not open or activate a browser. The test temporarily uses the clipboard and restores prior text when available.

For only the native restore / browser-focus / Ctrl+T check, use `--browser-self-test` and read `browser-test-results.txt`. This also uses only the practice fixture, never real browser windows.

Implementation references: [Microsoft SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [Microsoft RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey), [Microsoft SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow), [Microsoft EnumWindows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows), [OpenCV template matching](https://docs.opencv.org/4.13.0/df/dfb/group__imgproc__object.html), and [OpenCvSharp Windows package](https://www.nuget.org/packages/OpenCvSharp4.Windows/4.13.0.20261107).
