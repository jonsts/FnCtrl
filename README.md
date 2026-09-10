# FnCtrl

Use the **Fn** key on an Apple Magic Keyboard as **Ctrl** on Windows — and the **Eject** key as **Print Screen**. No kernel driver, no Secure Boot changes, no licence fee.

Roughly 300 lines of C#, compiled by the C# compiler that already ships inside Windows. Nothing to install first.

## Why the usual tools can't do this

The Fn key never reaches Windows as a keyboard scancode, so keyboard hooks — PowerToys Keyboard Manager, AutoHotkey, SharpKeys — genuinely cannot see it. That part of the common advice is correct.

What that advice usually misses is that the key is not silent. It arrives as a **HID report** on a different collection of the same keyboard:

| Connection | Where Fn shows up |
|---|---|
| Bluetooth | consumer-control collection, usage page `0x000C` usage `0x01` |
| USB | Apple vendor "top case" page, usage page `0xFF01` usage `0x03` |

On a Bluetooth Magic Keyboard (VID `05AC`, PID `0256`) the report is 2 bytes — report id `0x11`, then a bitfield:

```
11 10   Fn      pressed        11 00   released
11 08   Eject   pressed        11 00   released
```

The Windows **Raw Input** API hands those reports to any ordinary user-mode program. FnCtrl watches for the bit and synthesises the replacement key with `SendInput`. Because it is plain user-mode code, there is no driver to sign and Secure Boot is not involved at all.

## Install

```powershell
git clone https://github.com/jonsts/FnCtrl.git
cd FnCtrl
.\install.ps1
```

That builds the exe, copies it to `C:\Program Files\FnCtrl`, and registers a logon task that runs it elevated so the remapped keys also work in administrator windows (Task Manager, elevated terminals). It asks for one UAC approval.

**Without admin rights**, use a Startup-folder shortcut instead:

```powershell
.\install.ps1 -Mode Startup
```

Everything works the same, except the remapped keys won't reach elevated windows — Windows blocks lower-privileged processes from sending input to higher-privileged ones.

Either way you get a tray icon; its menu shows the active mappings and offers **Exit**.

To remove: `.\uninstall.ps1`

## If your keyboard reports something different

Apple has shipped many keyboards. If Fn does nothing after installing, find your model's report:

```powershell
.\build\FnCtrl.exe --probe
```

Press Fn a few times and watch for a line that appears on press and clears on release:

```
VID_05AC PID_0256 UsagePage 0x000C Usage 0x01  |  report: 11 10
VID_05AC PID_0256 UsagePage 0x000C Usage 0x01  |  report: 11 00
```

Read off the **report id** (first byte), the **byte index** that changed (here byte 1), and the **bit** that set (here `0x10`). Then install with that mapping:

```powershell
.\install.ps1 -ExtraArgs '--map','Fn:11:1:10:A2:1D'
```

The `--map` format is `NAME:REPORTID:BYTE:MASK:VK[:SCAN]`, all values hex. Repeat `--map` for more keys; supplying any `--map` replaces the built-in defaults, so list every key you want.

Useful virtual-key codes: `A2` Left Ctrl · `A0` Left Shift · `A4` Left Alt · `5B` Left Win · `2C` Print Screen · `1B` Esc · `2E` Delete · `91` Scroll Lock · `13` Pause.

`--probe --log probe.txt` writes to a file instead of a console — handy for attaching to an issue.

## Notes and limitations

- **This adds a key, it doesn't steal one.** Your real Ctrl still works; you end up with two.
- **Elevated windows need the elevated install.** That's a Windows security boundary (UIPI), not something the program can work around.
- **A stuck modifier** is theoretically possible if the keyboard disconnects mid-press. The program releases on disconnect and on exit; if it ever happens anyway, tap the real Ctrl key to clear it.
- **Fn combinations become Ctrl combinations.** Fn+F1 becomes Ctrl+F1, and so on.
- **Not tested beyond** a Bluetooth Magic Keyboard (A1644, VID `05AC` PID `0256`) on Windows 11. Probe output from other models is welcome.

## Build only

```powershell
.\build.ps1        # produces build\FnCtrl.exe
```

Requires nothing but Windows: it uses `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, present on every Windows 10/11 install. Since you compile it yourself, there is no unsigned download for SmartScreen to complain about.

## Licence

MIT
