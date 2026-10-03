# OSC network controls

Enable **Settings > Replay Buffer > Enable OSC** to receive Open Sound Control (OSC) messages over UDP. OSC starts disabled; the default port is **9001**. The listener binds all IPv4 and IPv6 interfaces, with IPv4 fallback when IPv6 is unavailable. Listener status shows the bound port or the reason listening failed. A port conflict requires choosing another port or closing the other listener; ClypDat does not substitute a port.

The [website setup guide](https://www.clypdat.xyz/docs/osc) also generates Command Prompt examples for your target PC and port.

**Stream Deck:** install the [OSC Remote](https://marketplace.elgato.com/product/osc-remote-9ccf7913-4532-4d68-8b1d-6c512923cda6) plugin from the Elgato Marketplace, put one of its actions on a key, and point it at the recording PC's IP and this port with the `/clypdat/clip` address (or `/clypdat/replay-duration` and a value such as `60`).

**Anyone who can reach the enabled OSC port can save clips and change replay length.** There is no sender authentication or IP filtering. ClypDat does not create or change firewall rules. Enable OSC only on a network where you intend to allow this access. Turning OSC off, changing its port, or quitting closes the listener and discards pending commands.

## Commands

Addresses are fixed and case-sensitive. Use [OSC binary encoding](https://opensoundcontrol.stanford.edu/spec-1_0.html), including the type-tag string. Plain text, JSON and HTTP requests do not work.

| Address | Arguments | Result |
|---|---|---|
| `/clypdat/clip` | None, OSC boolean `true` (`T`), or one positive int32 (`i`) or float32 (`f`) | Save a manual clip ending at packet reception time. |
| `/clypdat/clip` | OSC boolean `false` (`F`), zero, or a negative int32/float32 | Ignore the button release. |
| `/clypdat/replay-duration` | One int32 or integral float32 equal to `30`, `60`, `120`, `180`, `240` or `300` | Change the saved replay length in seconds. |

Numeric clip arguments are button presses, not clip lengths. Duration commands change the global setting and the active game's existing effective Replay override. Other games stay unchanged. They do not create profiles or enable overrides; Desktop Capture changes only the global length. Repeating an identical setting does not save or restart capture.

A changed effective length uses the existing capture restart: replay history clears and starts accumulating again. An active Full Session continues in another file. Wait for capture to become ready and collect enough history before requesting a clip. A duration change immediately followed by clipping, including within a bundle, rejects that clip while capture restarts.

Clips use the manual-save pipeline, including metadata, notifications and library handling. They are rejected while another operation is busy, capture is reconfiguring, arming or suspended, or no capture is available. Saves do not queue. Commands never open dialogs. A delayed clip is rejected if its capture identity changes before submission.

Messages and immediate bundles (timetag `1`) are supported; scheduled bundles are rejected. Bundle commands run in packet order. Packets are limited to 4 KiB, eight nested bundle levels, 32 messages and 32 pending packets. A malformed packet or an unsupported type tag rejects the entire packet before any command is applied. Unknown addresses are ignored. ClypDat sends no OSC replies.

## Send from Command Prompt

Windows includes PowerShell; no additional software is needed. Run these commands in **Command Prompt** to download the helper, select 30 seconds, wait for the restarted replay buffer, then clip:

```bat
powershell -NoProfile -Command "Invoke-WebRequest -UseBasicParsing 'https://www.clypdat.xyz/tools/Send-Osc.ps1' -OutFile '%TEMP%\Send-Osc.ps1'"
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\Send-Osc.ps1" -Action Duration -Seconds 30
timeout /t 35 /nobreak
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\Send-Osc.ps1" -Action Clip
```

Capture must already be running. If it takes longer to restart, wait until ClypDat shows recording ready, then allow 30 seconds of history. If fewer than 30 seconds are available, the clip contains only the available history.

From a repository checkout, run the helper directly:

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Send-Osc.ps1" -Action Duration -Seconds 120
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Send-Osc.ps1" -Action Clip
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Send-Osc.ps1" -Action Clip -TargetHost 192.168.1.20 -Port 9001
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\Send-Osc.ps1" -Action Clip -TargetHost ::1
```

The duration and clip examples above are separate commands; allow the restarted buffer to accumulate history between them. Replace the remote IP and port with the recording PC's address and configured OSC port. The helper's `Sent` message confirms only UDP transmission, not reception or a successful save.

## Verify results and troubleshoot

Open **Settings > About > Log folder**. The main `clypdat-yyyy-MM-dd.log` contains `[OSC] Replay duration set to 30s.`, `[OSC] Clip requested: receivedUtc=...`, and `[OSC] Clip saved.` or a refusal/failure reason. `Replay clip saved: ...` gives the saved media path. Open that media in the library and verify its duration; UDP send success alone does not confirm clipping succeeded. Rejected or malformed packet diagnostics are in the debug log.

If localhost works but another device does not, check the recording PC's IP, OSC port and firewall profile. You can add an inbound UDP rule for that port in Windows Defender Firewall and restrict its remote addresses to the controllers you trust. That firewall restriction is external to ClypDat. Check other firewall or router rules too. If localhost also fails, first check listener status, then confirm replay capture is ready and the library folder is available.
