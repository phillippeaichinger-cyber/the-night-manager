# The Night Manager: NINA plugin

Watch your N.I.N.A. observatory from your phone (iPhone and Android): every sub as it lands with a preview and a grade,
the guiding graph, NINA's status and the sequence's progress, alerts only when something needs you, and a summary of
the night in the morning.

This repository is the **open-source NINA side** of [The Night Manager](https://www.ninanightagent.com). The plugin
talks only to The Night Manager's service, and only after you pair it with the app. The apps and the service are
separate and closed source; the plugin is the go-between, and everything it sends is in this code.

## Requirements

- N.I.N.A. **3.2** (.NET 8) or **3.3** nightly (.NET 10), on Windows. There's a build for each.
- The Night Manager app for iPhone or Android, to pair and to watch. The app is in public beta:
  [ninanightagent.com](https://www.ninanightagent.com/#beta).

## Install

Download the newest build from [Releases](https://github.com/phillippeaichinger-cyber/the-night-manager/releases/latest):

| Your NINA | Download |
|---|---|
| NINA 3.2 (the current release, most people) | [NightManager-NINA3.2.zip](https://github.com/phillippeaichinger-cyber/the-night-manager/releases/latest/download/NightManager-NINA3.2.zip) |
| NINA 3.3 nightly | [NightManager-NINA3.3.zip](https://github.com/phillippeaichinger-cyber/the-night-manager/releases/latest/download/NightManager-NINA3.3.zip) |

1. Close NINA (all instances).
2. Paste `%LOCALAPPDATA%\NINA\Plugins\3.0.0` into File Explorer's address bar.
3. Unzip the download there, so you get a folder called `The Night Manager`. If Windows blocked the zip, right-click
   it → Properties → Unblock first.
4. Start NINA, open *Options → Plugins → The Night Manager* and press **Pair**. Enter the code in the app, or on the
   pairing page in any browser.

Full guide: [ninanightagent.com/install](https://www.ninanightagent.com/install).

## What it does

| | |
|---|---|
| Live state | Camera, mount, guider, focuser, filter wheel, rotator, weather, safety monitor, dome and flat panel, as they change. Live updates every second only while someone is watching in the app. |
| Timeline | Sequence start and end, targets, filters, meridian flips, autofocus runs, dithers, guiding, parks, the dome, devices connecting and dropping out, download and save failures. |
| Sub grading | Each light frame against your own recent good frames of the same target, filter and exposure (HFR, stars, guiding, background): accept, marginal or reject, with the reason. |
| Night Watch | Clouds, focus drift, dew risk, guiding trouble, cooling lost and stalled sequences: one alert per problem, and one when it's resolved. |
| Readiness | Before dark: drive space, the sequence (no flip, no autofocus trigger, no dithering, no safe end?), and Target Scheduler. |
| Cull list | At the end of the night, a CSV of grades and a list of rejects next to your images. Nothing is ever deleted or moved. |
| Sequence instructions | *Notify my phone*, *Ask my phone*, *Wait for go*, *Wake me up*, *Send latest image*, *Note to timeline*, *Night ending*, *Send night report*, and others (category "The Night Manager"). |
| Remote actions | Pause, abort, park, close the dome, warm the camera, safe shutdown. Off until you allow them in the plugin's options; each must be confirmed on the phone, runs once, expires after 5 minutes, and is confirmed back. |
| Home Wi-Fi link | With the phone on the same home network, live data and full-size previews come straight from the PC (HTTPS, pinned certificate, signed requests, never over a VPN). Can be switched off. |
| Startup helper | Optional, off until you turn it on: reopens NINA after a power cut or restart with the plan chosen in the app (`Launcher/NightManager.Launcher.exe`, registered at Windows logon; turning it off removes that). |
| Offline | Everything is queued on disk and sent when the internet returns. |

Works with the Advanced and classic sequencers, Target Scheduler, PHD2 and Hocus Focus.

## Free and Pro

The plugin is free. The app is free for one rig: live view, every alert (safety ones included), the remote actions
and a few AI night reports a month. An optional Pro subscription adds several rigs and sites in one app, sharing,
coaching and nightly reports, and pays for running the service for everyone. NINA's own functions and other free
plugins' data (Target Scheduler, for example) are never behind the paywall.

## Privacy

- **Sent:** equipment state, events, frame numbers, small stretched JPEG previews of light frames (optional), file
  names (optional).
- **Never sent:** FITS or XISF files, or full paths.
- **Beta research data:** during the beta, anonymous focus runs, star measurements, temperatures and equipment
  specifications help build the focus and optics features (testers agree to this when they sign up). Never images or
  personal details. Switch it off in *Options → The Night Manager → Data and privacy → optics and focus data*.
- The pairing credential is stored encrypted with Windows DPAPI for your Windows user.
- Data folder: `%LOCALAPPDATA%\NINA\NightManager` (settings, the outbox queue, session state).
- Privacy policy: [ninanightagent.com/privacy](https://www.ninanightagent.com/privacy).

## Building

```powershell
# NinaTarget=3.2 (default, NINA 3.2) or 3.3 (NINA 3.3 nightly)
dotnet build -p:NinaTarget=3.2
dotnet test                                   # Core tests run on any OS; plugin tests need Windows
Copy-Item local.props.example local.props     # set NinaExecutable
dotnet build ./src/NightManager/NightManager.csproj -t:DeployPlugin
```

- `src/NightManager.Core`: everything that doesn't need NINA (contracts, outbox, sync, grading, Night Watch,
  sessions, commands, pairing, the home link). `net8.0`, so it loads in both NINA versions.
- `src/NightManager`: the NINA side (MEF exports, device consumers, events, previews, options page, sequence
  instructions).
- `src/NightManager.Launcher`: the optional Startup helper.

## Support

Questions and bug reports: the [Night Manager Discord](https://discord.gg/yz2RNBPYYk) (please include NINA's log from
`%LOCALAPPDATA%\NINA\Logs`). This plugin is not supported by the N.I.N.A. team.

## AI-assisted development

Substantial parts of this plugin were written with AI assistance (Anthropic's Claude), directed by the maintainer, Phil
Aichinger, who is accountable for the code and maintains it.

## Licence

[Mozilla Public License 2.0](LICENSE.txt). Third-party components and their licences:
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
